using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DesktopHub.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace DesktopHub.Windows;

public sealed class HubApplication : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public SettingsStore Store { get; }
    public NativeLaunchService Launcher { get; } = new(new WindowsShortcutResolver());
    public AppLibraryService AppLibrary { get; }
    public SceneLibraryService SceneLibrary { get; }
    public SceneExecutionService Scenes { get; }
    internal ApplicationIconCache Icons { get; }
    public string DataDirectory { get; }
    public bool Smoke { get; }
    internal bool AllowSmokeHover { get; set; }
    public HubWindow Mini { get; private set; } = null!;
    public HubWindow? Dock { get; private set; }
    public HubWindow? Workspace { get; private set; }
    private CoreWebView2Environment? environment;
    private readonly DispatcherTimer timer;
    private nint lastExternalForeground, previousForeground;
    private uint previousForegroundProcess;
    private DateTime? edgeEntered;
    private bool openingDock, exiting;
    private string lastEffectiveTheme = "";
    public string FocusRestoreResult { get; private set; } = "not-attempted";
    public List<string> BlockedRequests { get; } = new();
    public IEnumerable<HubWindow> Windows => new[] { Mini, Dock, Workspace }.OfType<HubWindow>();

    public HubApplication(string dataDirectory, bool smoke)
    {
        DataDirectory = dataDirectory; Smoke = smoke;
        Directory.CreateDirectory(dataDirectory);
        Store = new SettingsStore(dataDirectory);
        AppLibrary = new AppLibraryService(Store);
        SceneLibrary = new SceneLibraryService(Store);
        Scenes = new SceneExecutionService(Store, new WindowsSceneActionRunner(Launcher));
        Scenes.Changed += () => _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            foreach (var surface in Windows) surface.SendEvent("scene.executions", new { executions = Scenes.Snapshot() });
        });
        Icons = new ApplicationIconCache(dataDirectory, new WindowsShortcutResolver());
        var firstRun = !File.Exists(Store.SettingsPath);
        Store.Load();
        if (Store.RecoveryWarning is { } warning) Log("SettingsRecovery", warning);
        var loaded = Store.Current;
        var iconsChanged = false;
        foreach (var app in loaded.AppEntries.Where(app => app.Icon == "default"))
        {
            var icon = Icons.Extract(app);
            if (icon != "default") { app.Icon = icon; iconsChanged = true; }
        }
        if (iconsChanged)
        {
            try { Store.Save(loaded); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SettingsValidationException) { Log("IconCache", e.Message); }
        }
        if (firstRun)
        {
            var settings = Store.Current;
            var area = SystemParameters.WorkArea;
            settings.WorkspaceBounds = new WindowBounds { Left = area.Left + 32, Top = area.Top + 24, Width = area.Width - 64, Height = area.Height - 48 };
            Store.Save(settings);
        }
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += Tick;
    }

    public async Task StartAsync()
    {
        var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
        Log("Runtime", version);
        environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDirectory, "WebView2"),
            new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-component-update --disable-features=msEdgeSidebarV2"));
        Mini = new HubWindow(this, "mini");
        Mini.Show();
        await Mini.InitializeAsync(environment);
        timer.Start();
    }

    public string EffectiveTheme
    {
        get
        {
            if (Store.Current.Theme != "system") return Store.Current.Theme;
            try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
                return Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 1)) == 0 ? "dark" : "light"; }
            catch { return "light"; }
        }
    }

    public object State(HubWindow window)
    {
        var settings = Store.Current;
        return new { version = "0.2.1", surface = window.Surface, settings, executions = Scenes.Snapshot(), appIcons = Icons.GetIcons(settings),
            effectiveTheme = EffectiveTheme, settingsWarning = Store.RecoveryWarning,
            runtimeVersion = environment?.BrowserVersionString };
    }

    public async Task OpenDockAsync()
    {
        if (openingDock) { if (Dock is not null) await Dock.Ready.Task; return; }
        openingDock = true;
        try
        {
            if (Dock is null)
            {
                Dock = new HubWindow(this, "dock"); Dock.Show();
                await Dock.InitializeAsync(environment!);
            }
            else Dock.Show();
            Dock.Topmost = true; // Only expanded Dock overlays ordinary foreground windows.
            Dock.Activate();
        }
        catch { Dock?.Dispose(); Dock = null; throw; }
        finally { openingDock = false; }
    }

    public void CloseDock()
    {
        if (Dock is null) return;
        SaveWindow(Dock); Dock.Topmost = false; Dock.Hide();
    }

    public async Task OpenWorkspaceAsync(string? page = null)
    {
        if (Workspace?.IsVisible != true)
        {
            var foreground = NativeMethods.GetForegroundWindow();
            previousForeground = foreground != 0 && !NativeMethods.IsOurWindow(foreground) ? foreground : lastExternalForeground;
            NativeMethods.GetWindowThreadProcessId(previousForeground, out previousForegroundProcess);
        }
        CloseDock();
        try
        {
            if (Workspace is null)
            {
                Workspace = new HubWindow(this, "workspace"); Workspace.Show();
                await Workspace.InitializeAsync(environment!);
            }
            else { Workspace.Show(); await Workspace.Ready.Task; }
        }
        catch { Workspace?.Dispose(); Workspace = null; throw; }
        // No permanent topmost Workspace. Foreground activation is a user-requested action.
        Workspace.Activate();
        NativeMethods.SetForegroundWindow(new WindowInteropHelper(Workspace).Handle);
        if (page is not null) Workspace.SendEvent("window.navigate", new { page });
    }

    public void CloseWorkspace()
    {
        if (Workspace is null) return;
        var foreground = NativeMethods.GetForegroundWindow();
        var mayRestore = NativeMethods.IsOurWindow(foreground);
        SaveWindow(Workspace); Workspace.Hide();
        if (!mayRestore) { FocusRestoreResult = "external-user-focus-preserved"; return; }
        NativeMethods.GetWindowThreadProcessId(previousForeground, out var previousProcessNow);
        if (previousForeground == 0 || !NativeMethods.IsWindow(previousForeground) || previousProcessNow != previousForegroundProcess) { FocusRestoreResult = "previous-window-unavailable"; return; }
        if (NativeMethods.IsIconic(previousForeground)) NativeMethods.ShowWindow(previousForeground, 9);
        var requested = NativeMethods.SetForegroundWindow(previousForeground);
        FocusRestoreResult = requested && NativeMethods.GetForegroundWindow() == previousForeground ? "restored" : "windows-denied-activation";
        Log("FocusRestore", FocusRestoreResult);
    }

    public void SettingsChanged()
    {
        foreach (var window in Windows)
        {
            window.ApplyNativeTheme();
            window.ApplyPositionIfChanged();
            window.SendEvent("state.changed", State(window));
        }
    }

    public void SaveWindow(HubWindow window)
    {
        if (!window.IsLoaded || window.WindowState != WindowState.Normal) return;
        var bounds = new WindowBounds { Left = window.Left, Top = window.Top, Width = window.Width, Height = window.Height };
        var settings = Store.Current;
        switch (window.Surface)
        {
            case "workspace": settings.WorkspaceBounds = bounds; break;
            case "mini": settings.MiniBounds = bounds; break;
            case "dock": settings.DockBounds = bounds; break;
        }
        try { Store.Save(settings); } catch (Exception e) { Log("SettingsWrite", e.Message); }
    }

    private void Tick(object? sender, EventArgs e)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (foreground != 0 && !NativeMethods.IsOurWindow(foreground)) lastExternalForeground = foreground;
        var theme = EffectiveTheme;
        if (theme != lastEffectiveTheme) { lastEffectiveTheme = theme; SettingsChanged(); }
        if ((Smoke && !AllowSmokeHover) || !Store.Current.HoverEnabled || Workspace?.IsVisible == true || Dock?.IsVisible == true || !Store.Current.AllowForeground)
        { edgeEntered = null; return; }
        if (!NativeMethods.GetCursorPos(out var cursor)) return;
        var info = new NativeMethods.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        var monitor = NativeMethods.MonitorFromWindow(new WindowInteropHelper(Mini).Handle, 2);
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        // Conservative geometry guard; no game injection, hooks, or undocumented desktop parenting.
        if (foreground != 0 && !NativeMethods.IsOurWindow(foreground) && NativeMethods.GetWindowRect(foreground, out var rect) &&
            rect.Left <= info.Monitor.Left && rect.Top <= info.Monitor.Top && rect.Right >= info.Monitor.Right && rect.Bottom >= info.Monitor.Bottom)
        { edgeEntered = null; return; }
        var near = Store.Current.DockPosition switch
        {
            "right" => cursor.X >= info.Monitor.Right - 2 && cursor.X < info.Monitor.Right,
            "top" => cursor.Y >= info.Monitor.Top && cursor.Y <= info.Monitor.Top + 2,
            "bottom" => cursor.Y >= info.Monitor.Bottom - 2 && cursor.Y < info.Monitor.Bottom,
            _ => cursor.X >= info.Monitor.Left && cursor.X <= info.Monitor.Left + 2
        };
        if (!near) { edgeEntered = null; return; }
        edgeEntered ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - edgeEntered.Value).TotalMilliseconds < Store.Current.HoverDelay) return;
        edgeEntered = null;
        _ = OpenDockFromHoverAsync();
    }

    private async Task OpenDockFromHoverAsync()
    {
        try { await OpenDockAsync(); } catch (Exception e) { Log("DockHover", e.ToString()); }
    }

    public void Exit()
    {
        if (exiting) return;
        exiting = true; timer.Stop();
        foreach (var window in Windows) SaveWindow(window);
        Application.Current.Shutdown();
    }

    public void Log(string type, string message)
    {
        try { File.AppendAllText(Path.Combine(DataDirectory, "host.log"), $"{DateTime.UtcNow:O} [{type}] {message}\n"); } catch { }
    }

    public void Dispose()
    {
        Scenes.Dispose();
        timer.Stop();
        foreach (var window in Windows) window.Dispose();
    }
}
