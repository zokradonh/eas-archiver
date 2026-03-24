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
    [ObservableProperty] private string password = "";
    [ObservableProperty] private string archiveDirectory = "mail_archive";
    [ObservableProperty] private int windowSize = 50;
    [ObservableProperty] private bool fixHeaders = true;
    [ObservableProperty] private string includeFolders = "";
    [ObservableProperty] private string excludeFolders = "";

    // ── Sync state ──────────────────────────────────────────────────────────
    [ObservableProperty] private bool isSyncing;
    [ObservableProperty] private string statusText = "Ready";
    [ObservableProperty] private string progressText = "";

    public ObservableCollection<string> LogLines { get; } = [];

    /// <summary>
    /// Interaction callback for requesting a password from the user.
    /// Set by the View — the ViewModel only knows the signature, not the implementation.
    /// </summary>
    public Func<Task<string?>>? RequestPassword { get; set; }

    private CancellationTokenSource? _cts;

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
        StatusText = "Configuration saved";
    }

    [RelayCommand]
    private void LoadConfig()
    {
        var cfg = ConfigService.LoadConfig();
        ServerUrl = cfg.ServerUrl;
        Domain = cfg.Domain;
        Username = cfg.Username;
        Password = cfg.Password;
        ArchiveDirectory = cfg.ArchiveDirectory;
        WindowSize = cfg.WindowSize;
        FixHeaders = cfg.FixHeaders;
        IncludeFolders = string.Join(", ", cfg.Include);
        ExcludeFolders = string.Join(", ", cfg.Exclude);
        StatusText = "Configuration loaded";
    }

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

        // Prompt for password if not configured
        if (string.IsNullOrWhiteSpace(cfg.Password))
        {
            var pwd = RequestPassword is not null ? await RequestPassword() : null;
            if (string.IsNullOrEmpty(pwd)) return;
            Password = pwd;
            cfg.Password = pwd;
        }

        IsSyncing = true;
        StatusText = "Syncing…";
        ProgressText = "";
        LogLines.Clear();
        _cts = new CancellationTokenSource();

        // Set up Serilog to write into our log panel
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Information()
            .WriteTo.Sink(new DelegateSink(AppendLog))
            .CreateLogger();

        var progress = new Progress<SyncProgress>(p =>
        {
            ProgressText = p.Phase switch
            {
                "DeviceInfo" => "Registering device…",
                "FolderSync" => "Fetching folders…",
                "Sync" => $"Syncing emails… ({p.FoldersTotal} folders)",
                "Done" => $"Done — {p.EmailsNew} new email(s)",
                _ => p.Phase
            };
        });

        var state = ConfigService.LoadState();

        try
        {
            var archiver = new global::EasArchiver.EasArchiver(cfg);
            await Task.Run(async () => await archiver.RunAsync(state, progress, _cts.Token));
            ConfigService.SaveState(state);

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

    private bool CanStartSync() => !IsSyncing;

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

    // ── Helpers ──────────────────────────────────────────────────────────────

    partial void OnIsSyncingChanged(bool value)
    {
        StartSyncCommand.NotifyCanExecuteChanged();
        StopSyncCommand.NotifyCanExecuteChanged();
    }

    private EasConfig BuildConfig()
    {
        return new EasConfig
        {
            ServerUrl = ServerUrl.Trim(),
            Domain = Domain.Trim(),
            Username = Username.Trim(),
            Password = Password,
            ArchiveDirectory = ArchiveDirectory.Trim(),
            WindowSize = WindowSize,
            FixHeaders = FixHeaders,
            Include = ParseList(IncludeFolders),
            Exclude = ParseList(ExcludeFolders),
        };
    }

    private static List<string> ParseList(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => s.Length > 0).ToList();
    }

    private void AppendLog(string message)
    {
        Dispatcher.UIThread.Post(() => LogLines.Add(message));
    }
}
