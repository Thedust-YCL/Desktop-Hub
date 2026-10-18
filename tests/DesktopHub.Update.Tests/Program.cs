using DesktopHub.Windows;

var passed = 0;
async Task Test(string name, Func<Task> test) { await test(); Console.WriteLine("PASS " + name); passed++; }
void Check(bool value, string message) { if (!value) throw new Exception(message); }
await Test("not installed never calls network", async () => {
    var backend = new FakeBackend { IsInstalled = false };
    using var service = new AppUpdateService(backend);
    await service.CheckAsync();
    Check(service.Snapshot().Stage == "unavailable" && backend.Checks == 0, "source run");
});
await Test("no update and repeated checks are throttled", async () => {
    var backend = new FakeBackend(); using var service = new AppUpdateService(backend);
    await service.CheckAsync(); await service.CheckAsync();
    Check(service.Snapshot().Stage == "current" && backend.Checks == 1, "throttle");
});
await Test("offline fails visibly and cannot install", async () => {
    var backend = new FakeBackend { CheckError = true }; using var service = new AppUpdateService(backend);
    await service.CheckAsync();
    Check(service.Snapshot().Stage == "error", "network error");
    Check(!service.TryPrepareInstall(false) && backend.Installs == 0, "not downloaded");
});
await Test("download failure can retry and never installs partial data", async () => {
    var backend = new FakeBackend { Available = "0.3.2", DownloadError = true }; using var service = new AppUpdateService(backend);
    await service.CheckAsync(); await service.DownloadAsync();
    Check(service.Snapshot().Stage == "error" && !service.TryPrepareInstall(false), "partial forbidden");
    backend.DownloadError = false; await service.DownloadAsync();
    Check(service.Snapshot().Stage == "ready" && service.Snapshot().Progress == 100, "retry");
    Check(!service.TryPrepareInstall(true) && backend.Installs == 0, "busy forbidden");
    Check(service.TryPrepareInstall(false) && backend.Installs == 1, "ready install");
    Check(!service.TryPrepareInstall(false) && backend.Installs == 1, "duplicate forbidden");
});
await Test("concurrent clicks share one network operation", async () => {
    var backend = new FakeBackend { Available = "0.3.2", Block = new(TaskCreationOptions.RunContinuationsAsynchronously) };
    using var service = new AppUpdateService(backend);
    var first = service.CheckAsync(); await service.CheckAsync();
    Check(backend.Checks == 1 && service.Snapshot().Stage == "checking", "single flight");
    backend.Block.SetResult(); await first;
    Check(service.Snapshot().Stage == "available", "new version");
});
await Test("pending verified package is available after app restart", () => {
    var backend = new FakeBackend { PendingVersion = "0.3.2" }; using var service = new AppUpdateService(backend);
    Check(service.Snapshot().Stage == "ready" && service.TryPrepareInstall(false), "pending package");
    return Task.CompletedTask;
});
await Test("timed out check remains single flight until underlying request completes", async () => {
    var now = DateTimeOffset.UtcNow;
    var backend = new FakeBackend { Block = new(TaskCreationOptions.RunContinuationsAsynchronously) };
    using var service = new AppUpdateService(backend, TimeSpan.FromMilliseconds(10), () => now);
    await service.CheckAsync();
    Check(service.Snapshot().Stage == "error", "visible timeout");
    now += TimeSpan.FromMinutes(2);
    await service.CheckAsync();
    Check(backend.Checks == 1, "late request must not race a second request");
    backend.Block.SetResult(); await backend.LastCheck!;
    await service.CheckAsync();
    Check(backend.Checks == 2 && service.Snapshot().Stage == "current", "retry after actual completion");
});
Console.WriteLine($"Updates: {passed} passed.");

sealed class FakeBackend : IAppUpdateBackend
{
    public bool IsInstalled { get; set; } = true;
    public string CurrentVersion => "0.3.1";
    public string? PendingVersion { get; set; }
    public string? Available;
    public bool CheckError, DownloadError;
    public int Checks, Installs;
    public TaskCompletionSource? Block;
    public Task<string?>? LastCheck;
    public Task<string?> CheckAsync() => LastCheck = RunCheckAsync();
    private async Task<string?> RunCheckAsync() { Checks++; if (Block != null) await Block.Task; if (CheckError) throw new IOException(); return Available; }
    public Task DownloadAsync(Action<int> progress, CancellationToken token) { if (DownloadError) throw new IOException(); progress(100); PendingVersion = Available; return Task.CompletedTask; }
    public void PrepareInstall() { Installs++; }
}
