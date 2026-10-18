namespace DesktopHub.Windows;

internal sealed record UpdateState(string CurrentVersion, string Stage, string? AvailableVersion = null,
    int Progress = 0, string? Error = null);

internal interface IAppUpdateBackend
{
    bool IsInstalled { get; }
    string CurrentVersion { get; }
    string? PendingVersion { get; }
    Task<string?> CheckAsync();
    Task DownloadAsync(Action<int> progress, CancellationToken token);
    void PrepareInstall();
}

internal sealed class UnavailableUpdateBackend(string version) : IAppUpdateBackend
{
    public bool IsInstalled => false;
    public string CurrentVersion => version;
    public string? PendingVersion => null;
    public Task<string?> CheckAsync() => Task.FromResult<string?>(null);
    public Task DownloadAsync(Action<int> progress, CancellationToken token) => throw new InvalidOperationException("Updates unavailable.");
    public void PrepareInstall() => throw new InvalidOperationException("Updates unavailable.");
}
