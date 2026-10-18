namespace DesktopHub.Windows;

// Network failures are state, not startup failures. The backend owns verified package assets.
internal sealed class AppUpdateService : IDisposable
{
    private readonly IAppUpdateBackend backend;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly TimeSpan checkTimeout;
    private readonly Func<DateTimeOffset> utcNow;
    private Task<string?>? pendingCheck;
    private UpdateState state;
    private DateTimeOffset lastCheck = DateTimeOffset.MinValue;
    private bool disposed;
    public event Action<UpdateState>? Changed;

    public AppUpdateService(IAppUpdateBackend backend, TimeSpan? checkTimeout = null, Func<DateTimeOffset>? utcNow = null)
    {
        this.backend = backend;
        this.checkTimeout = checkTimeout ?? TimeSpan.FromSeconds(45);
        this.utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        state = new(backend.CurrentVersion, !backend.IsInstalled ? "unavailable" : backend.PendingVersion != null ? "ready" : "idle",
            backend.PendingVersion, backend.PendingVersion != null ? 100 : 0);
    }

    public UpdateState Snapshot() { lock (gate) return state; }
    private void Publish() { Changed?.Invoke(Snapshot()); }
    private bool Active => state.Stage is "checking" or "downloading" or "installing";

    public async Task CheckAsync()
    {
        lock (gate)
        {
            if (disposed || !backend.IsInstalled || Active || pendingCheck is { IsCompleted: false } || state.Stage == "ready" || utcNow() - lastCheck < TimeSpan.FromMinutes(1)) return;
            lastCheck = utcNow();
            state = new(state.CurrentVersion, "checking");
        }
        Publish();
        try
        {
            var check = backend.CheckAsync();
            lock (gate) pendingCheck = check;
            var version = await check.WaitAsync(checkTimeout, lifetime.Token);
            lock (gate) { if (disposed) return; state = new(state.CurrentVersion, version == null ? "current" : "available", version); }
        }
        catch (Exception) { lock (gate) { if (disposed) return; state = state with { Stage = "error", Error = "无法检查更新，请检查网络后稍后重试。" }; } }
        Publish();
    }

    public async Task DownloadAsync()
    {
        lock (gate)
        {
            if (disposed || Active || state.Stage == "ready" || state.AvailableVersion == null) return;
            state = state with { Stage = "downloading", Progress = 0, Error = null };
        }
        Publish();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            await backend.DownloadAsync(value => {
                lock (gate) { if (disposed || state.Stage != "downloading") return; state = state with { Progress = Math.Clamp(value, 0, 100) }; }
                Publish();
            }, timeout.Token);
            lock (gate)
            {
                if (disposed) return;
                if (backend.PendingVersion != state.AvailableVersion) throw new InvalidOperationException("Package verification did not complete.");
                state = state with { Stage = "ready", Progress = 100 };
            }
        }
        catch (Exception) { lock (gate) { if (disposed) return; state = state with { Stage = "error", Error = "更新下载未完成，请检查网络后重试下载。" }; } }
        Publish();
    }

    public bool TryPrepareInstall(bool busy)
    {
        lock (gate)
        {
            if (disposed || busy || state.Stage != "ready" || backend.PendingVersion != state.AvailableVersion) return false;
            try { backend.PrepareInstall(); state = state with { Stage = "installing", Error = null }; }
            catch (Exception) { state = state with { Error = "无法启动更新程序，请重试或重新下载安装包。" }; return false; }
        }
        Publish(); return true;
    }

    public void Dispose() { lock (gate) { if (disposed) return; disposed = true; lifetime.Cancel(); } }
}
