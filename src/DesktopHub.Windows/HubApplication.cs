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
    public DesktopIconVisibilityService DesktopIcons { get; }
    public StartupRegistrationService Startup { get; }
    public GlobalHotkeyService? Hotkeys { get; private set; }
    public object HotkeyStatus { get; private set; } = new { success = false, error = "快捷键尚未注册。" };
    private TrayLifecycleService? tray;
    internal TrayLifecycleService? Tray => tray;
    private readonly ForegroundProtectionService protection = new();
    private readonly bool systemSmoke;
    private string systemTheme = "light";
    public NativeLaunchService Launcher { get; } = new(new WindowsShortcutResolver());
    public AppLibraryService AppLibrary { get; }
    public SceneLibraryService SceneLibrary { get; }
    public SceneExecutionService Scenes { get; }
    public ResourceLibraryService Resources { get; private set; } = null!;
    private readonly Task<ResourceLibraryService> resourceInitialization;
    internal Task<ResourceLibraryService> GetResourcesAsync() => resourceInitialization;
    internal ApplicationIconCache Icons { get; }
    public string DataDirectory { get; }
    public bool Smoke { get; }
    public bool IsExiting => exiting;
    internal bool AllowSmokeHover { get; set; }
    public HubWindow Mini { get; private set; } = null!;
    public HubWindow? Dock { get; private set; }
    public HubWindow? Workspace { get; private set; }
    private CoreWebView2Environment? environment;
    private readonly DispatcherTimer timer;
    private nint lastExternalForeground, previousForeground;
    private nint observedForeground;
    private uint previousForegroundProcess;
    private DateTime? edgeEntered;
    private bool openingDock, exiting;
    private int activationGeneration;
    private string lastEffectiveTheme = "";
    public string FocusRestoreResult { get; private set; } = "not-attempted";
    public List<string> BlockedRequests { get; } = new();
    public IEnumerable<HubWindow> Windows => new[] { Mini, Dock, Workspace }.OfType<HubWindow>();

    public HubApplication(string dataDirectory, bool smoke, bool systemSmoke = false)
    {
        DataDirectory = dataDirectory; Smoke = smoke; this.systemSmoke = systemSmoke;
        Directory.CreateDirectory(dataDirectory);
        Store = new SettingsStore(dataDirectory);
        AppLibrary = new AppLibraryService(Store);
        SceneLibrary = new SceneLibraryService(Store);
        Scenes = new SceneExecutionService(Store, new WindowsSceneActionRunner(Launcher));
        resourceInitialization = Task.Run(() =>
        {
            var service = new ResourceLibraryService(dataDirectory);
            service.Changed += () => _ = Application.Current.Dispatcher.BeginInvoke(() => Workspace?.SendEvent("resources.changed", new { changed = true }));
            service.Progress += progress => _ = Application.Current.Dispatcher.BeginInvoke(() => Workspace?.SendEvent("resources.progress", progress));
            return service;
        });
        Scenes.Changed += () => _ = Application.Current.Dispatcher.BeginInvoke(() =>
        {
            foreach (var surface in Windows) surface.SendEvent("scene.executions", new { executions = Scenes.Snapshot() });
        });
        Icons = new ApplicationIconCache(dataDirectory, new WindowsShortcutResolver());
        var firstRun = !File.Exists(Store.SettingsPath);
        Store.Load();
        DesktopIcons = new DesktopIconVisibilityService(Store);
        Startup = new StartupRegistrationService(Store);
        systemTheme = ReadSystemTheme();
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
        if (firstRun || Store.Current.WorkspaceLayoutVersion < 1)
        {
            var settings = Store.Current;
            var area = SystemParameters.WorkArea;
            if (firstRun || (settings.WorkspaceBounds.Width >= area.Width * .88 && settings.WorkspaceBounds.Height >= area.Height * .88))
            {
                var width = Math.Min(960, area.Width * .8);
                var height = Math.Min(640, area.Height * .8);
                settings.WorkspaceBounds = new WindowBounds { Left = area.Left + (area.Width - width) / 2, Top = area.Top + (area.Height - height) / 2, Width = width, Height = height };
            }
            settings.WorkspaceLayoutVersion = 1;
            Store.Save(settings);
        }
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += Tick;
    }

    public async Task StartAsync(bool loginStartup = false)
    {
        var version = CoreWebView2Environment.GetAvailableBrowserVersionString();
        Log("Runtime", version);
        environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDirectory, "WebView2"),
            new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-component-update --disable-features=msEdgeSidebarV2"));
        Mini = new HubWindow(this, "mini");
        Mini.Show();
        await Mini.InitializeAsync(environment);
        Mini.Opacity = Store.Current.Transparency ? Store.Current.MiniOpacity : 1;
        SyncMiniVisibility();
        if (!Smoke || systemSmoke)
        {
            Hotkeys = new GlobalHotkeyService(surface => _ = OpenFromSystemAsync(surface));
            var settings = Store.Current;
            HotkeyStatus = Hotkeys.Apply(settings.DockHotkey, settings.WorkspaceHotkey);
            tray = new TrayLifecycleService(() => _ = OpenFromSystemAsync("activation"),
                () => Store.Current.MiniEnabled, ToggleMini,
                () => _ = OpenFromSystemAsync("settings"), ShowDesktopIcons, Exit);
        }
        var recovery = DesktopIcons.ReconcileOnStartup();
        Log("DesktopIconReconcile", JsonSerializer.Serialize(recovery, Json));
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        Application.Current.SessionEnding += OnSessionEnding;
        timer.Interval = TimeSpan.FromMilliseconds(Store.Current.HoverEnabled ? 100 : 1000);
        timer.Start();
        Resources = await resourceInitialization;
        _ = ResumeResourcesAsync();
        if (!Smoke && !loginStartup && !Store.Current.OnboardingCompleted)
            await OpenWorkspaceAsync("home");
    }

    private async Task ResumeResourcesAsync()
    {
        try { await Resources.StartAsync(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log("ResourceStartup", ex.ToString()); }
    }

    public string EffectiveTheme
    {
        get
        {
            if (Store.Current.Theme != "system") return Store.Current.Theme;
            return systemTheme;
        }
    }

    public object State(HubWindow window)
    {
        var settings = Store.Current;
        return new { version = "0.2.3", surface = window.Surface, settings, executions = Scenes.Snapshot(), appIcons = Icons.GetIcons(settings),
            effectiveTheme = EffectiveTheme, settingsWarning = Store.RecoveryWarning,
            onboardingRequired = !Smoke && !settings.OnboardingCompleted,
            systemIntegration = new { desktopIcons = DesktopIcons.Query(), startup = Startup.Query(), hotkeys = HotkeyStatus },
            runtimeVersion = environment?.BrowserVersionString };
    }

    public async Task OpenDockAsync()
    {
        if (exiting) return;
        if (openingDock)
        {
            var pending = Dock;
            var requested = ++activationGeneration;
            if (pending is null) return;
            await pending.Ready.Task;
            if (exiting || requested != activationGeneration || Dock != pending) return;
            if (Workspace?.IsVisible == true) CloseWorkspace(restoreFocus: false);
            Mini.Hide(); pending.Show(); pending.Topmost = true; pending.Activate();
            return;
        }
        openingDock = true;
        try
        {
            if (Workspace?.IsVisible == true) CloseWorkspace(restoreFocus: false);
            Mini.Hide();
            var generation = ++activationGeneration;
            if (Dock is null)
            {
                Dock = new HubWindow(this, "dock"); Dock.Show();
                await Dock.InitializeAsync(environment!);
            }
            else Dock.Show();
            if (exiting || generation != activationGeneration || !Dock.IsVisible) return;
            Dock.Topmost = true; // Only expanded Dock overlays ordinary foreground windows.
            Dock.Activate();
        }
        catch { Dock?.Dispose(); Dock = null; SyncMiniVisibility(); throw; }
        finally { openingDock = false; }
    }

    public void CloseDock()
    {
        activationGeneration++;
        if (Dock is null) return;
        SaveWindow(Dock); Dock.Topmost = false; Dock.Hide(); SyncMiniVisibility();
    }

    public async Task OpenWorkspaceAsync(string? page = null)
    {
        if (exiting) return;
        if (Workspace?.IsVisible != true)
        {
            var foreground = NativeMethods.GetForegroundWindow();
            previousForeground = foreground != 0 && !NativeMethods.IsOurWindow(foreground) ? foreground : lastExternalForeground;
            NativeMethods.GetWindowThreadProcessId(previousForeground, out previousForegroundProcess);
        }
        CloseDock();
        Mini.Hide();
        var generation = ++activationGeneration;
        try
        {
            if (Workspace is null)
            {
                Workspace = new HubWindow(this, "workspace"); Workspace.Show();
                await Workspace.InitializeAsync(environment!);
            }
            else { Workspace.Show(); await Workspace.Ready.Task; }
        }
        catch { Workspace?.Dispose(); Workspace = null; SyncMiniVisibility(); throw; }
        if (exiting || generation != activationGeneration || !Workspace.IsVisible) return;
        // The tray's Settings entry is a recovery path for a fully transparent
        // workspace. Temporarily reveal it without overwriting the saved value.
        Workspace.Opacity = (page == "settings" || !Store.Current.OnboardingCompleted) && Store.Current.WorkspaceOpacity < .1 ? 1 : Store.Current.WorkspaceOpacity;
        // No permanent topmost Workspace. Foreground activation is a user-requested action.
        Workspace.Activate();
        NativeMethods.SetForegroundWindow(new WindowInteropHelper(Workspace).Handle);
        if (page is not null) Workspace.SendEvent("window.navigate", new { page });
    }

    public void CloseWorkspace(bool restoreFocus = true)
    {
        activationGeneration++;
        if (Workspace is null) return;
        var foreground = NativeMethods.GetForegroundWindow();
        var mayRestore = NativeMethods.IsOurWindow(foreground);
        SaveWindow(Workspace); Workspace.Hide(); SyncMiniVisibility();
        if (!restoreFocus) return;
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
        if (exiting) return;
        timer.Interval = TimeSpan.FromMilliseconds(Store.Current.HoverEnabled ? 100 : 1000);
        Mini.Opacity = Store.Current.Transparency ? Store.Current.MiniOpacity : 1;
        if (Workspace is not null) Workspace.Opacity = Store.Current.WorkspaceOpacity;
        SyncMiniVisibility();
        tray?.RefreshMiniLabel();
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
        if (foreground != observedForeground) { observedForeground = foreground; SyncMiniVisibility(); }
        var theme = EffectiveTheme;
        if (theme != lastEffectiveTheme) { lastEffectiveTheme = theme; SettingsChanged(); }
        var settings = Store.Current;
        if ((Smoke && !AllowSmokeHover) || !settings.HoverEnabled || Workspace?.IsVisible == true || Dock?.IsVisible == true || !settings.AllowForeground)
        { edgeEntered = null; return; }
        if (!NativeMethods.GetCursorPos(out var cursor)) return;
        var info = new NativeMethods.MonitorInfo { Size = System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        var monitor = NativeMethods.MonitorFromWindow(new WindowInteropHelper(Mini).Handle, 2);
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return;
        var near = settings.DockPosition switch
        {
            "right" => cursor.X >= info.Monitor.Right - 2 && cursor.X < info.Monitor.Right,
            "top" => cursor.Y >= info.Monitor.Top && cursor.Y <= info.Monitor.Top + 2,
            "bottom" => cursor.Y >= info.Monitor.Bottom - 2 && cursor.Y < info.Monitor.Bottom,
            _ => cursor.X >= info.Monitor.Left && cursor.X <= info.Monitor.Left + 2
        };
        if (!near) { edgeEntered = null; return; }
        if (protection.Evaluate(settings.ForegroundBlocklist).IsProtected) { edgeEntered = null; return; }
        edgeEntered ??= DateTime.UtcNow;
        if ((DateTime.UtcNow - edgeEntered.Value).TotalMilliseconds < settings.HoverDelay) return;
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
        Scenes.Dispose(); // Cancel pending scheduling; never terminate launched user applications.
        foreach (var window in Windows) SaveWindow(window);
        try { Log("DesktopIconExit", JsonSerializer.Serialize(DesktopIcons.RestoreForExit(), Json)); }
        catch (Exception ex) { Log("DesktopIconExit", ex.ToString()); }
        Hotkeys?.Dispose(); Hotkeys = null;
        tray?.Dispose(); tray = null;
        Application.Current.Shutdown();
    }

    public void Log(string type, string message)
    {
        try { File.AppendAllText(Path.Combine(DataDirectory, "host.log"), $"{DateTime.UtcNow:O} [{type}] {message}\n"); } catch { }
    }

    public void Dispose()
    {
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        Application.Current.SessionEnding -= OnSessionEnding;
        Hotkeys?.Dispose(); tray?.Dispose();
        if (Resources is not null) Resources.Dispose();
        else _ = resourceInitialization.ContinueWith(task => { if (task.IsCompletedSuccessfully) task.Result.Dispose(); });
        Scenes.Dispose();
        timer.Stop();
        foreach (var window in Windows) window.Dispose();
    }

    public object SetHotkeys(string dock, string workspace)
    {
        if (exiting || Hotkeys is null) throw new BridgeException("HOTKEY_UNAVAILABLE", "当前会话无法更改全局快捷键。");
        var previous = Store.Current;
        var result = Hotkeys.Apply(dock, workspace);
        if (!result.Success) return result;
        try { Store.Update(settings => settings with { DockHotkey = result.DockHotkey, WorkspaceHotkey = result.WorkspaceHotkey }); }
        catch { HotkeyStatus = Hotkeys.Apply(previous.DockHotkey, previous.WorkspaceHotkey); throw; }
        HotkeyStatus = result;
        SettingsChanged();
        return result;
    }

    public void ToggleMini()
    {
        if (exiting) return;
        Store.Update(settings => settings with { MiniEnabled = !settings.MiniEnabled });
        SettingsChanged();
    }

    private void SyncMiniVisibility()
    {
        if (Mini is null || exiting) return;
        var settings = Store.Current;
        var visible = settings.MiniEnabled && Workspace?.IsVisible != true && Dock?.IsVisible != true && !protection.Evaluate(settings.ForegroundBlocklist).IsProtected;
        if (visible && !Mini.IsVisible) Mini.Show();
        else if (!visible && Mini.IsVisible) Mini.Hide();
    }

    internal async Task OpenFromSystemAsync(string surface)
    {
        if (exiting) return;
        var guard = protection.Evaluate(Store.Current.ForegroundBlocklist);
        if (guard.IsProtected) { edgeEntered = null; Log("ForegroundProtection", guard.Reason ?? "protected"); return; }
        try
        {
            if (surface == "dock")
            { if (Dock?.IsVisible == true) CloseDock(); else await OpenDockAsync(); }
            else if (surface == "workspace" && Workspace?.IsVisible == true) CloseWorkspace();
            else await OpenWorkspaceAsync(surface == "settings" ? "settings" : null);
        }
        catch (Exception ex) { Log("SystemActivation", ex.ToString()); }
    }

    private void ShowDesktopIcons()
    {
        try { var result = DesktopIcons.SetVisible(true); Log("DesktopIconTray", JsonSerializer.Serialize(result, Json)); SettingsChanged();
            if (!result.Success) MessageBox.Show(result.Message, "Desktop Hub · 桌面图标", MessageBoxButton.OK, MessageBoxImage.Information); }
        catch (Exception ex) { Log("DesktopIconTray", ex.ToString()); }
    }

    private static string ReadSystemTheme()
    {
        try { using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return Convert.ToInt32(key?.GetValue("AppsUseLightTheme", 1)) == 0 ? "dark" : "light"; }
        catch { return "light"; }
    }

    private void OnSystemPreferenceChanged(object sender, UserPreferenceChangedEventArgs e) =>
        _ = Application.Current.Dispatcher.BeginInvoke(() => { if (exiting) return; systemTheme = ReadSystemTheme(); SettingsChanged(); });

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _ = Application.Current.Dispatcher.BeginInvoke(() => { if (exiting) return; foreach (var window in Windows) window.ClampToWorkArea(window.Left, window.Top); });

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        if (exiting) return;
        exiting = true;
        timer.Stop();
        Scenes.Dispose();
        foreach (var window in Windows) SaveWindow(window);
        Log("SessionEnding", e.ReasonSessionEnding.ToString());
        // Session ending is not an explicit full exit. Keep recovery metadata for next login.
        DesktopIcons.RestoreForExit(sessionEnding: true);
    }
}
