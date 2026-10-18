using Velopack;
using Velopack.Sources;

namespace DesktopHub.Windows;

internal sealed class VelopackUpdateBackend : IAppUpdateBackend
{
    private readonly UpdateManager manager = new(new GithubSource("https://github.com/Thedust-YCL/Desktop-Hub", null, false));
    private UpdateInfo? available;
    public bool IsInstalled => manager.IsInstalled;
    public string CurrentVersion => manager.CurrentVersion?.ToString() ??
        typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.3.1";
    public string? PendingVersion => manager.UpdatePendingRestart?.Version.ToString();
    public async Task<string?> CheckAsync()
    {
        available = await manager.CheckForUpdatesAsync();
        return available?.TargetFullRelease.Version.ToString();
    }
    public Task DownloadAsync(Action<int> progress, CancellationToken token) =>
        manager.DownloadUpdatesAsync(available ?? throw new InvalidOperationException("Check for updates first."), progress, token);
    public void PrepareInstall() => manager.WaitExitThenApplyUpdates(
        manager.UpdatePendingRestart ?? throw new InvalidOperationException("No verified update."), silent: false, restart: true);
}
