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
    [ObservableProperty] public partial bool DebugBlobs { get; set; }

    // ── Password persistence (Windows DPAPI) ────────────────────────────
    [ObservableProperty] private bool savePassword;

    // ── Verbosity (0=V1, 1=V2, 2=V3) ────────────────────────────────────
    [ObservableProperty] public partial int VerbosityIndex { get; set; }
    public bool CanSavePassword => CredentialService.IsSupported;

    // ── Folder list ─────────────────────────────────────────────────────────
    public ObservableCollection<FolderItemViewModel> Folders { get; } = [];
    [ObservableProperty] private bool hasFolders;
    [ObservableProperty] private bool syncAll = true;

    // ── Sync state ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool isSyncing;
    [ObservableProperty] private bool isListingFolders;
    [ObservableProperty] private string statusText = "Ready";
    [ObservableProperty] private string progressText = "";
    [ObservableProperty] private string requestCountText = "0 requests";
    [ObservableProperty] private bool showLog;
    private int _totalRequestCount;
    private int _lastReportedCount;

    // ── App version ─────────────────────────────────────────────────────────
    public static string AppVersion => EasArchiver.AppVersion;

    // ── Update state ─────────────────────────────────────────────────────────
    [ObservableProperty] private bool isCheckingForUpdate;
    [ObservableProperty] private bool updateAvailable;
    [ObservableProperty] private string updateVersion = "";
    private readonly UpdateService _updateService = new();

    public ObservableCollection<string> LogLines { get; } = [];

    /// <summary>
    /// Interaction callback for requesting a password from the user.
    /// Set by the View — the ViewModel only knows the signature, not the implementation.
    /// </summary>
    public Func<Task<string?>>? RequestPassword { get; set; }

    /// <summary>
    /// Interaction callback for browsing for a folder.
    /// Set by the View.
    /// </summary>
    public Func<string?, Task<string?>>? BrowseFolder { get; set; }

    [RelayCommand]
    private async Task BrowseArchiveDir()
    {
        if (BrowseFolder is null) return;
        var result = await BrowseFolder(ArchiveDirectory.Trim());
        if (result is not null)
            ArchiveDirectory = result;
    }

    private static readonly string LogFile = Path.Combine(
        EasArchiver.AppDataDir, "eas-archiver.log");

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
        DebugBlobs = cfg.DebugBlobs;
        VerbosityIndex = cfg.Verbosity > 0 ? cfg.Verbosity - 1 : 0;
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
        IsListingFolders = true;
        await RunEasOperationAsync("Fetching folders…", null, async (archiver, state, progress, ct) =>
        {
            var paths = await Task.Run(async () => await archiver.ListFoldersAsync(state, progress, ct), ct);

            var previouslySelected = Folders
                .Where(f => f.IsSelected)
                .Select(f => f.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            Folders.Clear();
            foreach (var path in paths)
            {
                bool selected = SyncAll || previouslySelected.Count == 0 || previouslySelected.Contains(path);
                var item = new FolderItemViewModel(path) { IsSelected = selected };
                item.PropertyChanged += (_, _) => NotifyFolderSelectionChanged();
                Folders.Add(item);
            }
            HasFolders = Folders.Count > 0;
            NotifyFolderSelectionChanged();

            return $"{Folders.Count} folders found";
        });
        IsListingFolders = false;
    }

    private bool CanListFolders() => !IsSyncing && !IsListingFolders;

    [RelayCommand(CanExecute = nameof(CanStartSync))]
    private async Task StartSync()
    {
        IsSyncing = true;
        _cts = new CancellationTokenSource();
        ProgressText = "";
        await RunEasOperationAsync("Syncing…", p =>
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
        }, async (archiver, state, progress, ct) =>
        {
            await Task.Run(async () => await archiver.ArchiveAsync(state, progress, ct), ct);

            var dir = ArchiveDirectory.Trim();
            var archivePath = Path.IsPathRooted(dir) ? dir : Path.GetFullPath(dir);
            return $"Done — archive: {archivePath}";
        });
        IsSyncing = false;
        _cts = null;
    }

    private bool CanStartSync() => !IsSyncing && (SyncAll || Folders.Any(f => f.IsSelected));

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

    [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
    private async Task CheckForUpdates()
    {
        IsCheckingForUpdate = true;
        UpdateAvailable = false;
        StatusText = "Checking for updates…";
        try
        {
            var hasUpdate = await _updateService.CheckForUpdatesAsync();
            if (!hasUpdate)
            {
                StatusText = "You are running the latest version";
            }
            else
            {
                UpdateVersion = _updateService.PendingVersion ?? "";
                UpdateAvailable = true;
                StatusText = $"Update available: v{UpdateVersion}";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"Update check failed: {ex.Message}";
        }
        finally
        {
            IsCheckingForUpdate = false;
        }
    }

    private bool CanCheckForUpdates() => !IsCheckingForUpdate;

    [RelayCommand(CanExecute = nameof(CanApplyUpdate))]
    private async Task ApplyUpdate()
    {
        StatusText = "Downloading update…";
        try
        {
            await _updateService.DownloadAndApplyAsync(p =>
                StatusText = $"Downloading update… {p}%");
        }
        catch (Exception ex)
        {
            StatusText = $"Update failed: {ex.Message}";
        }
    }

    private bool CanApplyUpdate() => UpdateAvailable && !IsCheckingForUpdate;

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

    // ── Shared EAS operation runner ────────────────────────────────────────

    private async Task RunEasOperationAsync(string statusLabel,
        Action<SyncProgress>? onProgress,
        Func<EasArchiver, SyncState, IProgress<SyncProgress>, CancellationToken, Task<string>> operation)
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

        StatusText = statusLabel;
        LogLines.Clear();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Sink(new DelegateSink(AppendLog))
            .WriteTo.File(LogFile,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}",
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14)
            .CreateLogger();

        var progress = new Progress<SyncProgress>(p =>
        {
            onProgress?.Invoke(p);
            _totalRequestCount += p.RequestCount - _lastReportedCount;
            _lastReportedCount = p.RequestCount;
            RequestCountText = $"{_totalRequestCount} requests";
        });
        _lastReportedCount = 0;

        try
        {
            var state = ConfigService.LoadState();
            using var archiver = new EasArchiver(cfg);
            StatusText = await operation(archiver, state, progress, _cts?.Token ?? CancellationToken.None);
            ConfigService.SaveState(state);

            if (SavePassword && _cachedPassword is not null)
                CredentialService.Save(_cachedPassword);
        }
        catch (OperationCanceledException)
        {
            StatusText = "Sync cancelled";
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
            AppendLog($"ERROR: {ex}");
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
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

    partial void OnIsCheckingForUpdateChanged(bool value)
    {
        CheckForUpdatesCommand.NotifyCanExecuteChanged();
        ApplyUpdateCommand.NotifyCanExecuteChanged();
    }

    partial void OnUpdateAvailableChanged(bool value)
    {
        ApplyUpdateCommand.NotifyCanExecuteChanged();
    }

    partial void OnSyncAllChanged(bool value)
    {
        StartSyncCommand.NotifyCanExecuteChanged();
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
        SyncAll = include.Count == 0;
        Folders.Clear();
        foreach (var path in include.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var item = new FolderItemViewModel(path) { IsSelected = true };
            item.PropertyChanged += (_, _) => NotifyFolderSelectionChanged();
            Folders.Add(item);
        }
        HasFolders = Folders.Count > 0;
    }

    private EasConfig BuildConfig()
    {
        return new EasConfig
        {
            ServerUrl = ServerUrl.Trim(),
            Domain = Domain.Trim(),
            Username = Username.Trim(),
            Password = "",
            ArchiveDirectory = ArchiveDirectory.Trim(),
            WindowSize = WindowSize,
            FixHeaders = FixHeaders,
            DebugBlobs = DebugBlobs,
            Verbosity = VerbosityIndex + 1,
            Include = SyncAll ? [] : Folders.Where(f => f.IsSelected).Select(f => f.Path).ToList(),
        };
    }

    private void AppendLog(string message)
    {
        Dispatcher.UIThread.Post(() => LogLines.Add(message));
    }
}
