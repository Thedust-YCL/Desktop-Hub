namespace DesktopHub.Windows;

public enum HiddenSurfaceState { Visible, PreparingHide, HiddenRetained, HiddenSuspended, Releasing, Released, Rebuilding, Disposed }
/// <summary>Pure monotonic-clock policy. Window adapters serialize all calls on their dispatcher.</summary>
public sealed class HiddenSurfaceLifecycle
{
    public const long ReleaseDelayMilliseconds = 60000;
    public HiddenSurfaceState State { get; private set; } = HiddenSurfaceState.Visible;
    public long Generation { get; private set; }
    public long HiddenSince { get; private set; }
    public BrowserViewSnapshot? Snapshot { get; private set; }
    public bool Lightweight { get; private set; }
    private bool safe;
    public long Hide(long now) { if(State==HiddenSurfaceState.Disposed)return Generation; Generation++;HiddenSince=now;safe=false;Snapshot=null;State=HiddenSurfaceState.PreparingHide;return Generation; }
    public void Show() { if(State is HiddenSurfaceState.Disposed or HiddenSurfaceState.Released)return;Generation++;safe=false;State=HiddenSurfaceState.Visible; }
    public void SetMode(bool lightweight) { if(Lightweight==lightweight)return;Lightweight=lightweight;Generation++;safe=false;Snapshot=null;if(State is not HiddenSurfaceState.Visible and not HiddenSurfaceState.Disposed and not HiddenSurfaceState.Released)State=HiddenSurfaceState.HiddenRetained; }
    public bool Acknowledge(long generation,BrowserViewSnapshot? snapshot,bool busy,int nativeRequests)
    {
        if(!Current(generation))return false;
        Snapshot=snapshot;safe=snapshot is not null&&!busy&&nativeRequests==0;
        State=safe?HiddenSurfaceState.HiddenSuspended:HiddenSurfaceState.HiddenRetained;return safe;
    }
    public bool Current(long generation) => Generation==generation && State is HiddenSurfaceState.PreparingHide or HiddenSurfaceState.HiddenRetained or HiddenSurfaceState.HiddenSuspended;
    public void Invalidate() {safe=false;if(State==HiddenSurfaceState.HiddenSuspended)State=HiddenSurfaceState.HiddenRetained;}
    public bool TryRelease(long generation,long now,int nativeRequests)
    {
        if(!Current(generation)||!Lightweight||!safe||Snapshot is null||nativeRequests!=0||now-HiddenSince<ReleaseDelayMilliseconds)return false;
        State=HiddenSurfaceState.Releasing;return true;
    }
    internal async Task<bool> WaitForDeadlineAsync(long generation, Func<long> now,
        Func<TimeSpan, CancellationToken, Task> delay, CancellationToken cancellation)
    {
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Current(generation) || !Lightweight) return false;
            var remaining = ReleaseDelayMilliseconds - (now() - HiddenSince);
            if (remaining <= 0) return true;
            // Task.Delay's clock can wake before the monotonic release threshold.
            await delay(TimeSpan.FromMilliseconds(remaining), cancellation);
        }
    }
    public void Released() {if(State==HiddenSurfaceState.Releasing)State=HiddenSurfaceState.Released;}
    public void Dispose() {Generation++;safe=false;State=HiddenSurfaceState.Disposed;}
}
