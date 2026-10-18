using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using DesktopHub.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DesktopHub.Windows;

public sealed class HubWindow : Window, IDisposable
{
    public const string Origin = "https://desktop-hub.local";
    public string Surface { get; }
    public string LayoutOwner { get; } = Guid.NewGuid().ToString("D");
    public IWebView2 Browser { get; }
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource BrowserReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool IsDisposed => disposing;
    public HiddenSurfaceState LifecycleState => lifecycle.State;
    public nint Handle => new WindowInteropHelper(this).Handle;
    private readonly HubApplication host;
    private readonly NativeBridge bridge;
    private string? position;
    private bool disposing;
    private int activeNativeRequests;
    internal int ActiveNativeRequests => activeNativeRequests;
    private readonly HiddenSurfaceLifecycle lifecycle = new();
    private CancellationTokenSource? visibilityWork;
    private long handshakeRevision;
    private HwndSource? hwndSource;
    private HwndSourceHook? hwndHook;
    private static long Now => (long)Stopwatch.GetElapsedTime(0).TotalMilliseconds;

    public HubWindow(HubApplication application, string surface)
    {
        host = application; Surface = surface;
        ApplyLanguage();
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Assets/desktop-hub.ico"));
        WindowStyle = WindowStyle.None; ResizeMode = surface == "workspace" ? ResizeMode.CanResizeWithGrip : ResizeMode.NoResize;
        if (surface == "workspace")
        {
            MinWidth = 640; MinHeight = 480;
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0, GlassFrameThickness = new Thickness(0), CornerRadius = new CornerRadius(18),
                ResizeBorderThickness = new Thickness(6), NonClientFrameEdges = NonClientFrameEdges.None,
                UseAeroCaptionButtons = false
            });
        }
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        if (surface == "workspace") Opacity = host.Store.Current.WorkspaceOpacity;
        ShowInTaskbar = surface == "workspace"; ShowActivated = surface != "mini";
        Topmost = surface == "dock";
        // Composition makes the entire surface participate in WPF alpha blending,
        // including its WebView content, rather than leaving an opaque child HWND.
        Browser = new WebView2CompositionControl { DefaultBackgroundColor = System.Drawing.Color.Transparent, UseLayoutRounding = false };
        Content = (FrameworkElement)Browser;
        if (surface == "workspace")
        {
            var content = (FrameworkElement)Browser;
            content.SizeChanged += (_, _) => content.Clip = new RectangleGeometry(new Rect(0, 0, content.ActualWidth, content.ActualHeight), 18, 18);
        }
        lifecycle.SetMode(host.Store.Current.PerformanceMode == "lightweight");
        IsVisibleChanged += (_, _) => VisibilityChanged();
        bridge = new NativeBridge(host, this);
        if (surface == "workspace")
        {
            AllowDrop = true;
            ((FrameworkElement)Browser).AllowDrop = true;
            PreviewDragOver += (_, e) =>
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                e.Effects = bridge.CanStageDrop ? DragDropEffects.Copy : DragDropEffects.None;
                e.Handled = true;
            };
            PreviewDrop += (_, e) =>
            {
                if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
                e.Handled = true;
                if (e.Data.GetData(DataFormats.FileDrop) is string[] files) bridge.StageApplicationDrop(files);
            };
        }
        ApplyBounds();
        SourceInitialized += (_, _) =>
        {
            ApplyNativeTheme();
            hwndSource = HwndSource.FromHwnd(Handle);
            hwndHook = (nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
            {
                if (surface == "mini" && message == 0x21) { handled = true; return 3; } // WM_MOUSEACTIVATE / MA_NOACTIVATE
                if (surface == "workspace" && message == 0x0319) // WM_APPCOMMAND / browser back & forward
                {
                    var command = ((long)lParam >> 16) & 0x7ff;
                    if (command is 1 or 2)
                    {
                        handled = true;
                        SendEvent("window.historyNavigate", new { direction = command == 1 ? -1 : 1 });
                        return 1;
                    }
                }
                if (message is 0x02E0 or 0x007E) // WM_DPICHANGED / WM_DISPLAYCHANGE, after WPF processes DPI.
                    _ = Dispatcher.BeginInvoke(() => { if (!disposing) ClampToWorkArea(Left, Top); });
                return 0;
            };
            hwndSource?.AddHook(hwndHook);
        };
        Closing += (_, e) =>
        {
            if (disposing || host.IsExiting) return;
            e.Cancel = true;
            if (surface == "workspace") host.CloseWorkspace();
            else if (surface == "dock") host.CloseDock();
            else Hide(); // Closing a window never toggles persisted preferences during shutdown.
        };
        LocationChanged += (_, _) => { if (IsLoaded) position = PositionSetting; };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            Dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    if (Browser.CoreWebView2 is null) return;
                    var dismissed = await Browser.ExecuteScriptAsync("Boolean(window.DesktopHubDesktop?.handleEscape?.())");
                    if (dismissed == "true") return;
                    if (surface == "workspace") host.CloseWorkspace(); else if (surface == "dock") host.CloseDock();
                }
                catch (Exception ex) { host.Log("Escape", ex.Message); }
            });
        };
    }

    public async Task InitializeAsync(CoreWebView2Environment environment, BrowserViewSnapshot? snapshot = null, string? pageOverride = null)
    {
        try
        {
        await Browser.EnsureCoreWebView2Async(environment);
        var core = Browser.CoreWebView2;
        core.SetVirtualHostNameToFolderMapping("desktop-hub.local", Path.Combine(AppContext.BaseDirectory, "UI"), CoreWebView2HostResourceAccessKind.DenyCors);
        core.Settings.AreDevToolsEnabled = host.Smoke;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        core.Settings.AreHostObjectsAllowed = false;
        core.Settings.IsPasswordAutosaveEnabled = false;
        core.Settings.IsGeneralAutofillEnabled = false;
        core.Settings.IsStatusBarEnabled = false;
        core.NavigationStarting += (_, e) => { if (!IsAllowedPage(e.Uri)) { e.Cancel = true; host.BlockedRequests.Add(e.Uri); } };
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; };
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, e) =>
        {
            if (Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.Host == "desktop-hub.local") return;
            host.BlockedRequests.Add(e.Request.Uri);
            e.Response = environment.CreateWebResourceResponse(Stream.Null, 403, "Offline only", "Content-Type: text/plain");
        };
        core.WebMessageReceived += async (_, e) => await bridge.ReceiveAsync(e);
        core.ProcessFailed += (_, e) => host.Log("WebViewProcessFailed", e.ProcessFailedKind.ToString());
        core.NavigationCompleted += (_, e) =>
        {
            if (!e.IsSuccess) Ready.TrySetException(new InvalidOperationException("本地 UI 加载失败：" + e.WebErrorStatus));
        };
        core.DOMContentLoaded += (_, _) => Ready.TrySetResult();
        core.Navigate($"{Origin}/desktop/index.html?surface={Surface}&restoring=1");
        await Ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        // ExecuteScriptAsync does not await a Promise. Poll only this initial, bounded ready handshake.
        // A fixed script owns the promise and exposes a small acknowledgement.
        await WaitForBrowserReadyAsync(snapshot, pageOverride);
        BrowserReady.TrySetResult();
        if (!IsVisible && !disposing) VisibilityChanged();
        }
        catch (Exception e) { Ready.TrySetException(e); BrowserReady.TrySetException(e); throw; }
    }

    private async Task WaitForBrowserReadyAsync(BrowserViewSnapshot? snapshot, string? pageOverride)
    {
        await Browser.ExecuteScriptAsync("window.__hubLifecycleReady=null; Promise.resolve(window.DesktopHubLifecycle.restore(" + (snapshot?.Value.GetRawText() ?? "null") + "," + JsonSerializer.Serialize(pageOverride) + ")).then(v=>window.__hubLifecycleReady=v,()=>window.__hubLifecycleReady={ready:false})");
        var deadline = Now + 15000;
        while (!disposing && Now < deadline)
        {
            var reply = await Browser.ExecuteScriptAsync("window.__hubLifecycleReady");
            if (reply != "null")
            {
                using var result = JsonDocument.Parse(reply);
                if (result.RootElement.TryGetProperty("ready", out var ready) && ready.ValueKind == JsonValueKind.True) return;
                throw new InvalidOperationException("界面恢复未完成，请重新打开。");
            }
            await Task.Delay(25);
        }
        throw new TimeoutException("界面恢复超时。");
    }

    private void CancelVisibilityWork()
    {
        ++handshakeRevision;
        visibilityWork?.Cancel(); visibilityWork?.Dispose(); visibilityWork = null;
    }

    private void VisibilityChanged()
    {
        if (disposing) return;
        CancelVisibilityWork();
        if (IsVisible)
        {
            lifecycle.Show();
            ((FrameworkElement)Browser).Visibility = Visibility.Visible;
            if (Browser.CoreWebView2 is { } core)
            {
                core.Resume();
                if (BrowserReady.Task.IsCompletedSuccessfully)
                {
                    _ = Browser.ExecuteScriptAsync("window.DesktopHubLifecycle?.resume(" + lifecycle.Generation + ")");
                    SendEvent("state.changed", host.State(this));
                }
            }
            return;
        }
        // Native cancellation precedes every renderer handshake, including Mini visibility transitions.
        host.CancelSurfacePreviews(this);
        var generation = lifecycle.Hide(Now);
        ((FrameworkElement)Browser).Visibility = Visibility.Hidden;
        visibilityWork = new CancellationTokenSource();
        if (Browser.CoreWebView2 is null || !BrowserReady.Task.IsCompletedSuccessfully) return;
        _ = PrepareHiddenAsync(generation, visibilityWork.Token);
        if (Surface != "mini" && lifecycle.Lightweight) _ = ReleaseAtDeadlineAsync(generation, visibilityWork.Token);
    }

    internal void PerformanceModeChanged()
    {
        var lightweight = host.Store.Current.PerformanceMode == "lightweight";
        if (lifecycle.Lightweight == lightweight) return;
        lifecycle.SetMode(lightweight);
        if (!IsVisible) VisibilityChanged();
    }

    private async Task PrepareHiddenAsync(long generation, CancellationToken cancellation)
    {
        var revision = ++handshakeRevision;
        try
        {
            // Let a native response and its JS finally block settle before asking about pending work.
            await Task.Delay(150, cancellation);
            if (!lifecycle.Current(generation) || disposing || IsVisible || revision != handshakeRevision || Browser.CoreWebView2 is not { } core) return;
            core.Resume();
            var json = await Browser.ExecuteScriptAsync("window.DesktopHubLifecycle?.prepareHide(" + generation + ")").WaitAsync(TimeSpan.FromSeconds(2), cancellation);
            if (!lifecycle.Current(generation) || disposing || IsVisible || revision != handshakeRevision) return;
            using var document = JsonDocument.Parse(json);
            var ack = document.RootElement;
            BrowserViewSnapshot? snapshot = null;
            var trusted = ack.ValueKind == JsonValueKind.Object && ack.TryGetProperty("version", out var v) && v.TryGetInt32(out var version) && version == 1 && ack.TryGetProperty("generation", out var g) && g.TryGetInt64(out var returned) && returned == generation && ack.TryGetProperty("quiesced", out var q) && q.ValueKind == JsonValueKind.True && ack.TryGetProperty("busyReasons", out var reasons) && reasons.ValueKind == JsonValueKind.Array && reasons.GetArrayLength() <= 32 && reasons.EnumerateArray().All(reason => reason.ValueKind == JsonValueKind.String) && ack.TryGetProperty("snapshot", out var saved) && BrowserViewSnapshot.TryParse(saved.GetRawText(), out snapshot);
            var busy = !trusted || ack.GetProperty("busyReasons").GetArrayLength() != 0;
            if (!lifecycle.Acknowledge(generation, snapshot, busy, activeNativeRequests)) return;
            if (TryRelease(generation)) return;
            var suspended = await core.TrySuspendAsync();
            if (!disposing && (IsVisible || activeNativeRequests != 0 || !lifecycle.Current(generation) || revision != handshakeRevision)) core.Resume();
            host.Log("Suspend", Surface + ":" + suspended);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (lifecycle.Current(generation) && revision == handshakeRevision) lifecycle.Acknowledge(generation, null, true, activeNativeRequests);
            if (!disposing) host.Log("LifecycleRetained", Surface + ":" + ex.Message);
        }
    }

    private async Task ReleaseAtDeadlineAsync(long generation, CancellationToken cancellation)
    {
        try
        {
            if (await lifecycle.WaitForDeadlineAsync(generation, () => Now, Task.Delay, cancellation)) TryRelease(generation);
        }
        catch (OperationCanceledException) { }
    }

    private bool TryRelease(long generation)
    {
        if (Surface == "mini" || disposing || IsVisible || host.IsExiting || !lifecycle.TryRelease(generation, Now, activeNativeRequests)) return false;
        return host.ReleaseHiddenSurface(this, lifecycle.Snapshot!);
    }

    internal static bool IsAllowedPage(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == "desktop-hub.local" && uri.AbsolutePath == "/desktop/index.html";

    public void SendEvent(string type, object payload)
    {
        if (Browser.CoreWebView2 is null || disposing || !IsVisible) return;
        Browser.CoreWebView2.PostWebMessageAsJson(UiLocalization.Serialize(new { version = 1, type, payload }, host.Store.Current.Language));
    }

    internal void ApplyLanguage() => Title = UiText.Translate(host.Store.Current.Language, "Desktop Hub") + " · " + Surface;

    internal void ApplyNativeTheme()
    {
        if (Surface != "workspace") return;
        var dark = host.EffectiveTheme == "dark";
        if (new WindowInteropHelper(this).Handle == 0) return;
        var immersive = dark ? 1 : 0;
        NativeMethods.DwmSetWindowAttribute(Handle, 20, ref immersive, sizeof(int));
        // Windows 11's outer thin frame is also suppressed; unsupported attributes on Windows 10 are harmless.
        var border = unchecked((int)0xFFFFFFFE); // DWMWA_COLOR_NONE
        NativeMethods.DwmSetWindowAttribute(Handle, 34, ref border, sizeof(int));
    }

    private string PositionSetting => Surface == "mini" ? host.Store.Current.MiniPosition : host.Store.Current.DockPosition;

    private void ApplyBounds()
    {
        var settings = host.Store.Current;
        var bounds = Surface switch { "workspace" => settings.WorkspaceBounds, "dock" => settings.DockBounds, _ => settings.MiniBounds };
        Width = Surface == "workspace" ? Math.Max(640, bounds.Width) : bounds.Width;
        Height = Surface == "workspace" ? Math.Max(480, bounds.Height) : bounds.Height;
        ClampToWorkArea(bounds.Left, bounds.Top);
        position = PositionSetting;
    }

    public void ClampToWorkArea(double left, double top)
    {
        var area = SystemParameters.WorkArea;
        Width = Math.Min(Width, area.Width); Height = Math.Min(Height, area.Height);
        Left = Math.Clamp(left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }

    public void ApplyPositionIfChanged()
    {
        if (Surface == "workspace" || position == PositionSetting) return;
        position = PositionSetting;
        var area = SystemParameters.WorkArea;
        var (left, top) = position switch
        {
            "right" => (area.Right - Width - 18, area.Top + (area.Height - Height) / 2),
            "top" => (area.Left + (area.Width - Width) / 2, area.Top + 18),
            "bottom" => (area.Left + (area.Width - Width) / 2, area.Bottom - Height - 18),
            _ => (area.Left + 18, area.Top + (area.Height - Height) / 2)
        };
        ClampToWorkArea(left, top); host.SaveWindow(this);
    }

    public void ResizeSurface(double width, double height)
    {
        if (Surface == "workspace") throw new BridgeException("WRONG_SURFACE", "工作空间不能由网页调整大小。");
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 40 || width > 600 || height < 40 || height > 1200)
            throw new BridgeException("INVALID_PARAMS", "窗口尺寸无效。");
        Width = width; Height = height; ClampToWorkArea(Left, Top);
    }

    public void BeginNativeDrag()
    {
        // Start the standard Windows move loop after JS has posted its mouse event.
        NativeDrag.ReleaseCapture(); NativeDrag.SendMessage(Handle, 0xA1, 2, 0);
        host.SaveWindow(this);
    }

    public void BeginNativeResize(string edge)
    {
        if (Surface != "workspace") throw new BridgeException("WRONG_SURFACE", "请在工作空间调整窗口大小。");
        var hit = edge switch { "left" => 10, "right" => 11, "top" => 12, "top-left" => 13,
            "top-right" => 14, "bottom" => 15, "bottom-left" => 16, "bottom-right" => 17,
            _ => throw new BridgeException("INVALID_PARAMS", "缩放边缘无效。") };
        NativeDrag.GetCursorPos(out var cursor);
        var coordinates = (nint)((cursor.Y << 16) | (cursor.X & 0xffff));
        NativeDrag.ReleaseCapture(); NativeDrag.SendMessage(Handle, 0xA1, hit, coordinates);
        host.SaveWindow(this);
    }

    internal void BeginNativeRequest() { if (disposing) return; activeNativeRequests++; lifecycle.Invalidate(); }
    internal void EndNativeRequest()
    {
        activeNativeRequests = Math.Max(0, activeNativeRequests - 1);
        if (activeNativeRequests == 0 && !IsVisible && !disposing && visibilityWork is { } work && BrowserReady.Task.IsCompletedSuccessfully)
            _ = PrepareHiddenAsync(lifecycle.Generation, work.Token);
    }

    public void Dispose()
    {
        if (disposing) return;
        disposing = true; CancelVisibilityWork(); lifecycle.Dispose();
        host.CancelSurfacePreviews(this);
        BrowserReady.TrySetCanceled(); Ready.TrySetCanceled();
        if (hwndSource is not null && hwndHook is not null) hwndSource.RemoveHook(hwndHook);
        hwndSource = null; hwndHook = null;
        Browser.Dispose(); Content = null; Close();
    }

    private static class NativeDrag
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct Point { public int X; public int Y; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
        [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);
    }
}
