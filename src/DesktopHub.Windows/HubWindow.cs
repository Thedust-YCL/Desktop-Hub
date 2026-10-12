using System.IO;
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
    public IWebView2 Browser { get; }
    public TaskCompletionSource Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public nint Handle => new WindowInteropHelper(this).Handle;
    private readonly HubApplication host;
    private readonly NativeBridge bridge;
    private string? position;
    private bool disposing;
    private int activeNativeRequests;

    public HubWindow(HubApplication application, string surface)
    {
        host = application; Surface = surface;
        Title = "Desktop Hub · " + surface;
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
        IsVisibleChanged += async (_, _) =>
        {
            if (Browser.CoreWebView2 is null || disposing) return;
            try
            {
                if (IsVisible)
                {
                    ((FrameworkElement)Browser).Visibility = Visibility.Visible;
                    Browser.CoreWebView2.Resume();
                    SendEvent("state.changed", host.State(this));
                }
                else
                {
                    ((FrameworkElement)Browser).Visibility = Visibility.Hidden;
                    await Task.Delay(150);
                    if (!IsVisible && !disposing && activeNativeRequests == 0) host.Log("Suspend", Surface + ":" + await Browser.CoreWebView2.TrySuspendAsync());
                }
            }
            catch (Exception e) { host.Log("Suspend", Surface + ":" + e.Message); }
        };
        bridge = new NativeBridge(host, this);
        ApplyBounds();
        SourceInitialized += (_, _) =>
        {
            ApplyNativeTheme();
            HwndSource.FromHwnd(Handle)?.AddHook((nint hwnd, int message, nint wParam, nint lParam, ref bool handled) =>
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
            });
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

    public async Task InitializeAsync(CoreWebView2Environment environment)
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
        core.Navigate($"{Origin}/desktop/index.html?surface={Surface}");
        await Ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        }
        catch (Exception e) { Ready.TrySetException(e); throw; }
    }

    internal static bool IsAllowedPage(string value) => Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
        uri.Scheme == "https" && uri.Host == "desktop-hub.local" && uri.AbsolutePath == "/desktop/index.html";

    public void SendEvent(string type, object payload)
    {
        if (Browser.CoreWebView2 is null || disposing || !IsVisible) return;
        Browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(new { version = 1, type, payload }, HubApplication.Json));
    }

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

    internal void BeginNativeRequest() => activeNativeRequests++;
    internal async void EndNativeRequest()
    {
        activeNativeRequests--;
        if (activeNativeRequests != 0 || IsVisible || disposing) return;
        try
        {
            // Allow an opening request's reply to reach its original surface before suspension.
            await Task.Delay(150);
            if (activeNativeRequests == 0 && !IsVisible && !disposing && Browser.CoreWebView2 is { } core)
                await core.TrySuspendAsync();
        }
        catch (Exception ex) { host.Log("Suspend", Surface + ":" + ex.Message); }
    }

    public void Dispose() { disposing = true; Browser.Dispose(); Close(); }

    private static class NativeDrag
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct Point { public int X; public int Y; }
        [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool GetCursorPos(out Point point);
        [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern bool ReleaseCapture();
        [System.Runtime.InteropServices.DllImport("user32.dll")] internal static extern nint SendMessage(nint hwnd, uint msg, nint wParam, nint lParam);
    }
}
