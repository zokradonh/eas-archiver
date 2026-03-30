using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using EasArchiver.Gui.Services;
using Serilog;

namespace EasArchiver.Gui.ViewModels;

public partial class MainViewModel : ObservableObject
{
    // ── Config fields ───────────────────────────────────────────────────────
    [ObservableProperty] private string serverUrl = "";
    [ObservableProperty] private string domain = "";
    [ObservableProperty] private string username = "";
    [ObservableProperty] private string archiveDirectory = "mail_archive";
    [ObservableProperty] private int windowSize = 50;
    [ObservableProperty] private bool fixHeaders = true;

    // ── Password persistence (Windows DPAPI) ────────────────────────────
    [ObservableProperty] private bool savePassword;

    // ── Verbosity (0=V1, 1=V2, 2=V3) ────────────────────────────────────
    [ObservableProperty] public partial int VerbosityIndex { get; set; }
    public bool CanSavePassword => CredentialService.IsSupported;

    // ── Folder list ─────────────────────────────────────────────────────────
    public ObservableCollection<FolderItemViewModel> Folders { get; } = [];
    [ObservableProperty] private bool hasFolders;

    // ── Sync state ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool isSyncing;
    [ObservableProperty] private bool isListingFolders;
    [ObservableProperty] private string statusText = "Ready";
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private string requestCountText = "0 requests";
    [ObservableProperty] private bool showLog;
    private int _totalRequestCount;
    private int _lastReportedCount;

    public ObservableCollection<string> LogLines { get; } = [];

    /// <summary>
    /// Interaction callback for requesting a password from the user.
    /// Set by the View — the ViewModel only knows the signature, not the implementation.
    /// </summary>
    public Func<Task<string?>>? RequestPassword { get; set; }

    private CancellationTokenSource? _cts;
    private string? _cachedPassword;

    public MainViewModel()
    {
        LoadConfig();
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    [RelayCommand]
    private void SaveConfig()
    {
        var cfg = BuildConfig();
        ConfigService.SaveConfig(cfg);
        // Persist or delete DPAPI-encrypted password
        if (SavePassword && _cachedPassword is not null)
            CredentialService.Save(_cachedPassword);
        else
            CredentialService.Delete();
        StatusText = "Configuration saved";
    }

    [RelayCommand]
    private void LoadConfig()
    {
        var cfg = ConfigService.LoadConfig();
        ServerUrl = cfg.ServerUrl;
        Domain = cfg.Domain;
        Username = cfg.Username;
        ArchiveDirectory = cfg.ArchiveDirectory;
        WindowSize = cfg.WindowSize;
        FixHeaders = cfg.FixHeaders;
        // Restore folder selection from saved Include list
        LoadFolderSelection(cfg.Include);
        // Try loading DPAPI-encrypted password
        var storedPwd = CredentialService.Load();
        if (storedPwd is not null)
        {
            _cachedPassword = storedPwd;
            SavePassword = true;
        }
        StatusText = "Configuration loaded";
    }

    [RelayCommand(CanExecute = nameof(CanListFolders))]
    private async Task ListFolders()
    {
        var cfg = BuildConfig();

        if (string.IsNullOrWhiteSpace(cfg.ServerUrl) ||
            string.IsNullOrWhiteSpace(cfg.Username))
        {
            StatusText = "Server URL and username are required";
            return;
        }

        var pwd = await PromptPassword();
        if (pwd is null) return;
        cfg.Password = pwd;

        IsListingFolders = true;
        StatusText = "Fetching folders…";
        LogLines.Clear();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(new DelegateSink(AppendLog))
            .CreateLogger();

        var progress = new Progress<SyncProgress>(p =>
        {
            _totalRequestCount += p.RequestCount - _lastReportedCount;
            _lastReportedCount = p.RequestCount;
            RequestCountText = $"{_totalRequestCount} requests";
        });
        _lastReportedCount = 0;

        try
        {
            var state = ConfigService.LoadState();
            var archiver = new global::EasArchiver.EasArchiver(cfg);
            var paths = await Task.Run(async () => await archiver.ListFoldersAsync(state, progress));
            ConfigService.SaveState(state);

            // Remember which folders were previously selected
            var previouslySelected = Folders
                .Where(f => f.IsSelected)
                .Select(f => f.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Folders.Clear();
            foreach (var path in paths)
            {
                // Pre-select if previously selected, or all if first load
                bool selected = previouslySelected.Count == 0 || previouslySelected.Contains(path);
                var item = new FolderItemViewModel(path) { IsSelected = selected };
                item.PropertyChanged += (_, _) => NotifyFolderSelectionChanged();
                Folders.Add(item);
            }
            HasFolders = Folders.Count > 0;
            NotifyFolderSelectionChanged();

            if (SavePassword && _cachedPassword is not null)
                CredentialService.Save(_cachedPassword);

            StatusText = $"{Folders.Count} folders found";
        }
        catch (EasAuthException)
        {
            StatusText = "Authentication failed — check username/password";
            _cachedPassword = null;
        }
        catch (EasQuarantineException ex)
        {
            StatusText = $"Device not approved (quarantine) — ID: {ex.DeviceId}";
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            await Log.CloseAndFlushAsync();
            IsListingFolders = false;
        }
    }

    private bool CanListFolders() => !IsSyncing && !IsListingFolders;

    [RelayCommand(CanExecute = nameof(CanStartSync))]
    private async Task StartSync()
    {
        var cfg = BuildConfig();

        if (string.IsNullOrWhiteSpace(cfg.ServerUrl) ||
            string.IsNullOrWhiteSpace(cfg.Username))
        {
            StatusText = "Server URL and username are required";
            return;
        }

        var pwd = await PromptPassword();
        if (pwd is null) return;
        cfg.Password = pwd;

        IsSyncing = true;
        StatusText = "Syncing…";
        ProgressText = "";
        LogLines.Clear();
        _cts = new CancellationTokenSource();

        // Set up Serilog to write into our log panel
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(new DelegateSink(AppendLog))
            .CreateLogger();

        var progress = new Progress<SyncProgress>(p =>
        {
            if (p.Phase is not "Request")
            {
                ProgressText = p.Phase switch
                {
                    "DeviceInfo" => "Registering device…",
                    "FolderSync" => "Fetching folders…",
                    "Sync" => $"Syncing emails… ({p.FoldersTotal} folders)",
                    "Done" => $"Done — {p.EmailsNew} new email(s)",
                    _ => p.Phase
                };
            }
            _totalRequestCount += p.RequestCount - _lastReportedCount;
            _lastReportedCount = p.RequestCount;
            RequestCountText = $"{_totalRequestCount} requests";
        });
        _lastReportedCount = 0;

        var state = ConfigService.LoadState();

        try
        {
            var archiver = new global::EasArchiver.EasArchiver(cfg);
            await Task.Run(async () => await archiver.RunAsync(state, progress, _cts.Token));
            ConfigService.SaveState(state);

            if (SavePassword && _cachedPassword is not null)
                CredentialService.Save(_cachedPassword);

            var archivePath = Path.IsPathRooted(cfg.ArchiveDirectory)
                ? cfg.ArchiveDirectory
                : Path.GetFullPath(cfg.ArchiveDirectory);
            StatusText = $"Done — archive: {archivePath}";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Sync cancelled";
        }
        catch (EasQuarantineException ex)
        {
            StatusText = $"Device not approved (quarantine) — ID: {ex.DeviceId}";
        }
        catch (EasAuthException)
        {
            StatusText = "Authentication failed — check username/password";
            _cachedPassword = null;
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
            AppendLog($"ERROR: {ex}");
        }
        finally
        {
            await Log.CloseAndFlushAsync();
            IsSyncing = false;
            _cts = null;
        }
    }

    private bool CanStartSync() => !IsSyncing && Folders.Any(f => f.IsSelected);

    [RelayCommand(CanExecute = nameof(CanStopSync))]
    private void StopSync()
    {
        _cts?.Cancel();
        StatusText = "Cancelling…";
    }

    private bool CanStopSync() => IsSyncing;

    [RelayCommand]
    private void ResetSyncState()
    {
        ConfigService.SaveState(new SyncState());
        StatusText = "Sync state reset — next sync will be a full sync";
    }

    [RelayCommand]
    private void SelectAllFolders()
    {
        foreach (var f in Folders) f.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNoneFolders()
    {
        foreach (var f in Folders) f.IsSelected = false;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    partial void OnIsSyncingChanged(bool value)
    {
        StartSyncCommand.NotifyCanExecuteChanged();
        StopSyncCommand.NotifyCanExecuteChanged();
        ListFoldersCommand.NotifyCanExecuteChanged();
    }

    partial void OnIsListingFoldersChanged(bool value)
    {
        ListFoldersCommand.NotifyCanExecuteChanged();
    }

    private void NotifyFolderSelectionChanged()
    {
        StartSyncCommand.NotifyCanExecuteChanged();
    }

    private async Task<string?> PromptPassword()
    {
        if (_cachedPassword is not null) return _cachedPassword;
        var pwd = RequestPassword is not null ? await RequestPassword() : null;
        if (string.IsNullOrEmpty(pwd)) return null;
        _cachedPassword = pwd;
        return pwd;
    }

    private void LoadFolderSelection(List<string> include)
    {
        Folders.Clear();
        foreach (var path in include.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var item = new FolderItemViewModel(path);
            item.PropertyChanged += (_, _) => NotifyFolderSelectionChanged();
            Folders.Add(item);
        }
        HasFolders = Folders.Count > 0;
    }

    private EasConfig BuildConfig()
    {
        // Selected folders → Include list (empty = sync all, handled by core)
        var selected = Folders.Where(f => f.IsSelected).Select(f => f.Path).ToList();
        var include = selected.Count == Folders.Count && Folders.Count > 0 ? [] : selected;

        return new EasConfig
        {
            ServerUrl = ServerUrl.Trim(),
            Domain = Domain.Trim(),
            Username = Username.Trim(),
            Password = "",
            ArchiveDirectory = ArchiveDirectory.Trim(),
            WindowSize = WindowSize,
            FixHeaders = FixHeaders,
            Verbosity = VerbosityIndex + 1,
            Include = include,
        };
    }

    private void AppendLog(string message)
    {
        Dispatcher.UIThread.Post(() => LogLines.Add(message));
    }
}
