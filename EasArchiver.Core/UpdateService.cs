using Velopack;
using Velopack.Sources;

namespace EasArchiver;

public class UpdateService
{
    private const string GitHubRepoUrl = "https://github.com/zokradonh/eas-archiver";
    private UpdateInfo? _pendingUpdate;

    public bool UpdateAvailable => _pendingUpdate is not null;
    public string? PendingVersion => _pendingUpdate?.TargetFullRelease.Version.ToString();

    public async Task<bool> CheckForUpdatesAsync()
    {
        var mgr = new UpdateManager(new GithubSource(GitHubRepoUrl, null, false));
        _pendingUpdate = await mgr.CheckForUpdatesAsync();
        return _pendingUpdate is not null;
    }

    public async Task DownloadAndApplyAsync(Action<int>? onProgress = null)
    {
        if (_pendingUpdate is null) throw new InvalidOperationException("No pending update");
        var mgr = new UpdateManager(new GithubSource(GitHubRepoUrl, null, false));
        await mgr.DownloadUpdatesAsync(_pendingUpdate, onProgress);
        mgr.ApplyUpdatesAndRestart(_pendingUpdate);
    }
}
