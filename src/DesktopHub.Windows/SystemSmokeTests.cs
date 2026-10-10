using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using DesktopHub.Core;

namespace DesktopHub.Windows;

// Explicit, isolated native acceptance. No desktop-icon or startup-registry writes.
internal static class SystemSmokeTests
{
    private static readonly List<object> checks = [];
    private static readonly List<object> samples = [];
    private static string output = "";
    private static readonly JsonSerializerOptions Json = new(HubApplication.Json) { WriteIndented = true };
    public static void Prepare(string directory)
    {
        var expected = Path.Combine(AppContext.BaseDirectory, "m23-smoke-data");
        if (!Path.GetFullPath(directory).Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Isolated profile required.");
        Directory.CreateDirectory(directory);
        new SettingsStore(directory).Save(new AppSettings { HoverEnabled = false, DockHotkey = "Ctrl+Alt+Shift+F8", WorkspaceHotkey = "Ctrl+Alt+Shift+F9", Theme = "dark", ReducedMotion = true });
    }
    public static async Task RunAsync(HubApplication host, bool restart)
    {
        output = Path.Combine(AppContext.BaseDirectory, "m23-smoke-evidence"); Directory.CreateDirectory(output);
        checks.Clear();
        try
        {
            if (restart)
            {
                using var previous = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "phase1-results.json")));
                checks.AddRange(previous.RootElement.EnumerateArray().Select(x => (object)x.Clone()));
                using var expected = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "expected-settings.json")));
                var actual = JsonSerializer.SerializeToElement(host.Store.Current, HubApplication.Json);
                foreach (var key in new[] { "dockHotkey", "workspaceHotkey", "foregroundBlocklist", "miniEnabled", "miniOpacity", "hoverEnabled", "theme", "reducedMotion" })
                    Check("Actual process restart preserves " + key, actual.GetProperty(key).GetRawText() == expected.RootElement.GetProperty(key).GetRawText());
                Check("Restart registers saved independent hotkeys", host.Hotkeys?.DockHotkey == host.Store.Current.DockHotkey && host.Hotkeys.WorkspaceHotkey == host.Store.Current.WorkspaceHotkey);
                await host.OpenWorkspaceAsync("settings");
                await WaitJs(host.Workspace!, "Boolean(window.DesktopHubSystem)");
                await Capture(host.Workspace!, "settings-restart.png");
                Complete(host, "summary.txt"); return;
            }
            await WaitJs(host.Mini, "Boolean(window.DesktopHubDesktop?.getState().hostState)");
            Check("Quiet startup shows no Workspace or Dock", host.Workspace is null && host.Dock is null);
            Check("Mini uses non-topmost independent HWND", NativeMethods.IsWindow(host.Mini.Handle) && !host.Mini.Topmost);
            Check("Independent system hotkeys register", host.Hotkeys?.DockHotkey == "Ctrl+Alt+Shift+F8" && host.Hotkeys.WorkspaceHotkey == "Ctrl+Alt+Shift+F9");
            var tray = host.Tray ?? throw new Exception("Tray unavailable");
            var rebuilds = tray.RecoveryCount;
            SendMessage(tray.DiagnosticWindowHandle, tray.DiagnosticTaskbarCreatedMessage, 0, 0);
            Check("Tray rebuilds on a targeted TaskbarCreated test message", tray.RecoveryCount == rebuilds + 1);
            var systemBefore = host.Store.Current;
            File.WriteAllText(Path.Combine(output, "readonly-system-state.json"), JsonSerializer.Serialize(new { icons = host.DesktopIcons.Query(), startup = host.Startup.Query() }, Json));
            Check("Read-only queries do not enable startup or authorize hiding", !host.Store.Current.StartWithWindows && host.Store.Current.DesktopIcons.ExitPolicy is null);
            await Capture(host.Mini, "mini.png");
            await Measure(host, "mini-visible");
            host.ToggleMini();
            Check("Mini can hide while host stays resident", !host.Mini.IsVisible);
            await Measure(host, "quiet-resident-all-hidden");
            host.ToggleMini();
            await host.OpenDockAsync();
            await WaitJs(host.Dock!, "Boolean(window.DesktopHubDesktop?.getState().hostState)");
            Check("Dock hides Mini and preserves applications/scenes sections", host.Dock!.IsVisible && !host.Mini.IsVisible && (await Js(host.Dock, "Boolean(document.querySelector('.dock-app-grid') && document.querySelector('.dock-scenes'))")).GetBoolean());
            await Capture(host.Dock, "dock.png");
            await Measure(host, "dock-visible");
            await host.OpenWorkspaceAsync("settings");
            var window = host.Workspace!;
            await WaitJs(window, "Boolean(window.DesktopHubSystem)");
            Check("Workspace has priority over Dock/Mini", window.IsVisible && !host.Dock.IsVisible && !host.Mini.IsVisible);
            Check("Own Workspace is excluded from fullscreen guard", !new ForegroundProtectionService().Evaluate(["DesktopHub.Windows.exe"]).IsProtected);
            await Capture(window, "settings-dark.png");
            var iconBefore = host.DesktopIcons.Query();
            await Js(window, "Hub.actions['settings-section']({dataset:{section:'system'}});Hub.actions['sys-icons']({dataset:{visible:'false'}})");
            Check("First hide dialog explains files/recovery and has no selected policy", (await Js(window, "document.querySelector('.modal-body').textContent.includes('不会删除') && document.querySelector('.modal-body').textContent.includes('显示桌面图标') && !document.querySelector('.modal input[type=radio]:checked') && document.querySelector('[data-action=sys-confirm-hide]').disabled")).GetBoolean());
            await Capture(window, "first-hide-consent.png");
            await Js(window, "Hub.actions['sys-cancel-policy']()");
            Check("Cancelling first hide leaves real system and consent unchanged", !host.Store.Current.DesktopIcons.FirstHideAuthorized && host.DesktopIcons.Query().Visible == iconBefore.Visible);
            await CaptureSystemPanel(window, "system-dark");
            await Measure(host, "workspace-visible");
            var wrongSurface = false;
            try { new SystemBridge(host, host.Mini).Dispatch("system.setStartup", JsonSerializer.SerializeToElement(new { enabled = true })); }
            catch (BridgeException ex) { wrongSurface = ex.Code == "WRONG_SURFACE"; }
            Check("Mini cannot perform startup writes", wrongSurface);
            var denied = await Request(window, "system.setDesktopIcons", new { visible = false, firstHidePolicy = "restore", command = "forbidden" });
            Check("Extra command payload cannot reach desktop controller", !denied.GetProperty("ok").GetBoolean() && denied.GetProperty("error").GetProperty("code").GetString() == "INVALID_PARAMS");
            denied = await Request(window, "settings.save", new { settings = new { startWithWindows = true } });
            Check("Generic settings cannot bypass system authorization", !denied.GetProperty("ok").GetBoolean());
            var oldDock = host.Store.Current.DockHotkey; var oldWorkspace = host.Store.Current.WorkspaceHotkey;
            using (var conflict = new GlobalHotkeyService(_ => { }))
            {
                var occupied = conflict.Apply("Ctrl+Alt+Shift+F10", "Ctrl+Alt+Shift+F11");
                Check("Controlled second registrar owns conflict keys", occupied.Success);
                var result = await Request(window, "system.setHotkeys", new { dock = "Ctrl+Alt+Shift+F10", workspace = "Ctrl+Alt+Shift+F7" });
                Check("Real RegisterHotKey conflict preserves both saved bindings", result.GetProperty("ok").GetBoolean() && !result.GetProperty("result").GetProperty("operation").GetProperty("success").GetBoolean() && host.Store.Current.DockHotkey == oldDock && host.Store.Current.WorkspaceHotkey == oldWorkspace);
            }
            var saved = await Request(window, "system.setHotkeys", new { dock = "Ctrl+Alt+Shift+F6", workspace = "Ctrl+Alt+Shift+F7" });
            Check("New independent hotkeys really register and persist", saved.GetProperty("ok").GetBoolean() && host.Hotkeys?.DockHotkey == "Ctrl+Alt+Shift+F6" && host.Store.Current.WorkspaceHotkey == "Ctrl+Alt+Shift+F7");
            await Request(window, "settings.save", new { settings = new { foregroundBlocklist = new[] { "DesktopHub.LaunchFixture.exe" }, miniOpacity = .8, reducedMotion = true, theme = "light" } });
            Check("System settings survive the atomic store", host.Store.Current.ForegroundBlocklist.SequenceEqual(["DesktopHub.LaunchFixture.exe"], StringComparer.OrdinalIgnoreCase) && host.Store.Current.MiniOpacity == .8);
            await WaitJs(window, "document.documentElement.dataset.theme==='light'");
            await Capture(window, "settings-light.png");
            await CaptureSystemPanel(window, "system-light");
            SendHotkey(0x76); // The real registered Ctrl+Alt+Shift+F7.
            await Wait(() => !window.IsVisible);
            Check("Real keyboard input reaches registered Workspace shortcut", !window.IsVisible && host.Mini.IsVisible);
            SendHotkey(0x76);
            await Wait(() => window.IsVisible);
            Check("Registered Workspace shortcut opens the same resident window", window.IsVisible && !host.Mini.IsVisible);
            var width = window.Width; var height = window.Height;
            window.Width = 7680; window.Height = 4320; window.ClampToWorkArea(-32768, 32768);
            Check("Oversized/offscreen window is clamped to work area", window.Left >= SystemParameters.WorkArea.Left && window.Top >= SystemParameters.WorkArea.Top && window.Left + window.Width <= SystemParameters.WorkArea.Right + 1 && window.Top + window.Height <= SystemParameters.WorkArea.Bottom + 1);
            window.Width = width; window.Height = height; window.ClampToWorkArea(32, 24);
            host.CloseWorkspace();
            Check("Closing Workspace preserves resident host and returns Mini", !window.IsVisible && host.Mini.IsVisible && host.Hotkeys is not null);
            await VerifyRealForegroundProtectionAsync(host, window);
            using (var second = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--system-smoke" }, UseShellExecute = false }))
            {
                if (second is null) throw new Exception("Second process unavailable");
                await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                await Wait(() => window.IsVisible);
                Check("Second real process forwards activation and exits", second.ExitCode == 0 && window.IsVisible);
            }
            // A no-op resource event in an explicitly owned directory observes the existing M2.2 watcher.
            var owned = Path.Combine(AppContext.BaseDirectory, "m23-owned-resource-observation-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(owned);
            File.WriteAllText(Path.Combine(owned, "before.txt"), "Owned fixture");
            var preview = await host.Resources.PreviewAsync(owned, "custom", false, Guid.NewGuid().ToString("N"));
            await host.Resources.ConfirmAsync(preview.Token);
            File.WriteAllText(Path.Combine(owned, "after.txt"), "Owned fixture");
            await Measure(host, "authorized-file-change");
            Check("M2.2 authorized watcher remains active after lifecycle switches", host.Resources.Store.Snapshot().Resources.Any(x => x.DisplayName == "after.txt"));
            host.CloseWorkspace();
            Check("System tests never wrote startup or authorized icon hide", !host.Store.Current.StartWithWindows && host.Store.Current.DesktopIcons.ExitPolicy == systemBefore.DesktopIcons.ExitPolicy);
            File.WriteAllText(Path.Combine(output, "expected-settings.json"), JsonSerializer.Serialize(host.Store.Current, HubApplication.Json));
            File.WriteAllText(Path.Combine(output, "phase1-results.json"), JsonSerializer.Serialize(checks, Json));
            Complete(host, "phase1-summary.txt");
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString());
            Application.Current.Shutdown(1);
        }
    }
    private static async Task VerifyRealForegroundProtectionAsync(HubApplication host, HubWindow workspace)
    {
        var source = Environment.GetEnvironmentVariable("DESKTOP_HUB_TEST_EXE");
        if (source is null || Path.GetFileName(source) != "DesktopHub.LaunchFixture.exe" || !File.Exists(source))
            throw new InvalidOperationException("Set DESKTOP_HUB_TEST_EXE to the built repository LaunchFixture.");
        var fixtureDirectory = Path.Combine(host.DataDirectory, "foreground-fixture-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureDirectory);
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(source)!, "DesktopHub.LaunchFixture.*"))
            File.Copy(file, Path.Combine(fixtureDirectory, Path.GetFileName(file)));
        var fixturePath = Path.Combine(fixtureDirectory, "DesktopHub.LaunchFixture.exe");
        var originalBlocklist = host.Store.Current.ForegroundBlocklist;
        var observations = new List<object>();
        using var fixture = Process.Start(new ProcessStartInfo(fixturePath)
            { UseShellExecute = false, WorkingDirectory = fixtureDirectory, ArgumentList = { "--m23-owned-foreground" } })
            ?? throw new InvalidOperationException("Controlled foreground fixture did not start.");
        var started = fixture.StartTime.ToUniversalTime().Ticks;
        try
        {
            await Wait(() => { fixture.Refresh(); return fixture.MainWindowHandle != 0 && NativeMethods.IsWindow(fixture.MainWindowHandle); });
            var fixtureWindow = fixture.MainWindowHandle;
            NativeMethods.ShowWindow(fixtureWindow, 9);
            NativeMethods.SetForegroundWindow(fixtureWindow);
            await Wait(() => NativeMethods.GetForegroundWindow() == fixtureWindow);
            host.Store.Update(settings => settings with { ForegroundBlocklist = ["DesktopHub.LaunchFixture.exe"] });
            host.SettingsChanged();
            var guarded = new ForegroundProtectionService().Evaluate(host.Store.Current.ForegroundBlocklist);
            Check("Real foreground executable is blocked by the configured name", guarded.IsProtected && guarded.Reason == "foreground-blocklist" && guarded.WindowHandle == fixtureWindow);
            Check("Blocked real foreground suppresses Mini without stealing focus", !host.Mini.IsVisible && NativeMethods.GetForegroundWindow() == fixtureWindow);
            SendHotkey(0x76); // Registered Workspace F7.
            await Task.Delay(500);
            Check("Real Workspace hotkey cannot bypass foreground blocklist", !workspace.IsVisible && host.Dock?.IsVisible != true && NativeMethods.GetForegroundWindow() == fixtureWindow);
            SendHotkey(0x75); // Registered Dock F6.
            await Task.Delay(500);
            Check("Real Dock hotkey cannot bypass foreground blocklist", host.Dock?.IsVisible != true && !workspace.IsVisible && NativeMethods.GetForegroundWindow() == fixtureWindow);
            await host.OpenFromSystemAsync("activation");
            Check("System activation cannot bypass foreground blocklist or steal focus", !workspace.IsVisible && NativeMethods.GetForegroundWindow() == fixtureWindow);
            observations.Add(new { phase = "blocked", fixturePid = fixture.Id, fixtureWindow = fixtureWindow.ToInt64(), guardReason = guarded.Reason, foregroundWindow = NativeMethods.GetForegroundWindow().ToInt64() });

            host.Store.Update(settings => settings with { ForegroundBlocklist = [] });
            host.SettingsChanged();
            Check("Clearing foreground blocklist restores enabled Mini without taking focus", host.Mini.IsVisible && NativeMethods.GetForegroundWindow() == fixtureWindow);
            SendHotkey(0x76);
            await Wait(() => workspace.IsVisible && NativeMethods.GetForegroundWindow() == workspace.Handle);
            Check("Ordinary real foreground allows registered Workspace hotkey", workspace.IsVisible && !host.Mini.IsVisible);
            SendEscape();
            await Wait(() => !workspace.IsVisible);
            // Windows can legally deny foreground restoration. Record the actual result, never claim restoration in that case.
            Check("Real Esc closes Workspace and reports a valid live-window focus result", host.FocusRestoreResult is "restored" or "windows-denied-activation");
            var restored = NativeMethods.GetForegroundWindow() == fixtureWindow;
            Check("Successful focus-restore status agrees with actual foreground HWND", host.FocusRestoreResult != "restored" || restored);
            observations.Add(new { phase = "esc-live-target", fixturePid = fixture.Id, fixtureWindow = fixtureWindow.ToInt64(), focusRestore = host.FocusRestoreResult, actuallyRestored = restored, foregroundWindow = NativeMethods.GetForegroundWindow().ToInt64() });

            // Ensure the next Workspace opening captures this controlled target, then retire it while Workspace is open.
            NativeMethods.SetForegroundWindow(fixtureWindow);
            await Wait(() => NativeMethods.GetForegroundWindow() == fixtureWindow);
            SendHotkey(0x76);
            await Wait(() => workspace.IsVisible && NativeMethods.GetForegroundWindow() == workspace.Handle);
            if (!fixture.CloseMainWindow()) throw new InvalidOperationException("Controlled fixture could not be closed.");
            await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check("Only the test-created foreground fixture was closed", fixture.HasExited && !NativeMethods.IsWindow(fixtureWindow));
            SendEscape();
            await Wait(() => !workspace.IsVisible);
            Check("Real Esc safely handles a previously captured window that has exited", host.FocusRestoreResult == "previous-window-unavailable" && !workspace.IsVisible);
            observations.Add(new { phase = "esc-exited-target", fixturePid = fixture.Id, fixtureWindow = fixtureWindow.ToInt64(), focusRestore = host.FocusRestoreResult, foregroundWindow = NativeMethods.GetForegroundWindow().ToInt64() });
        }
        finally
        {
            host.Store.Update(settings => settings with { ForegroundBlocklist = originalBlocklist });
            host.SettingsChanged();
            File.WriteAllText(Path.Combine(output, "foreground-observations.json"), JsonSerializer.Serialize(observations, Json));
            // This handle belongs to Process.Start above; exact path and start time guard even the fallback cleanup.
            if (!fixture.HasExited && fixture.StartTime.ToUniversalTime().Ticks == started &&
                string.Equals(fixture.MainModule?.FileName, fixturePath, StringComparison.OrdinalIgnoreCase))
            {
                fixture.CloseMainWindow();
                try { await fixture.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { fixture.Kill(); await fixture.WaitForExitAsync(); }
            }
        }
    }
    private static async Task Measure(HubApplication host, string scenario)
    {
        await Task.Delay(2000);
        var points = new List<Dictionary<int, (long Started, long Private, double Cpu)>>();
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < 6; i++)
        {
            var group = new Dictionary<int, (long, long, double)>();
            foreach (var id in host.Mini.Browser.CoreWebView2.Environment.GetProcessInfos().Select(p => (int)p.ProcessId).Append(Environment.ProcessId).Distinct())
            {
                try { using var process = Process.GetProcessById(id); group[id] = (process.StartTime.ToUniversalTime().Ticks, process.PrivateMemorySize64, process.TotalProcessorTime.TotalSeconds); }
                catch (ArgumentException) { }
            }
            points.Add(group);
            if (i < 5) await Task.Delay(1000);
        }
        var stable = points.All(p => p.Count == points[0].Count && p.All(pair => points[0].TryGetValue(pair.Key, out var first) && first.Started == pair.Value.Started));
        samples.Add(new { scenario, seconds = clock.Elapsed.TotalSeconds, sampleCount = points.Count,
            meanPrivateMiB = points.Average(p => p.Values.Sum(v => v.Private)) / 1048576.0,
            cpuMachinePercent = stable ? 100 * (points[^1].Values.Sum(v => v.Cpu) - points[0].Values.Sum(v => v.Cpu)) / clock.Elapsed.TotalSeconds / Environment.ProcessorCount : (double?)null,
            processGroupStable = stable, windows = host.Windows.Select(w => new { w.Surface, w.IsVisible, w.Topmost, suspended = w.Browser.CoreWebView2.IsSuspended }).ToArray() });
        File.WriteAllText(Path.Combine(output, "resource-observations.json"), JsonSerializer.Serialize(new { environment = RuntimeInformation.OSDescription, framework = RuntimeInformation.FrameworkDescription, processors = Environment.ProcessorCount,
            method = "2 s settle + 5 s sample; host plus GetProcessInfos WebView processes; private committed memory, machine-normalized CPU; excludes crashpad/GPU-memory; short observation only", samples }, Json));
    }
    private static void Complete(HubApplication host, string marker) { File.WriteAllText(Path.Combine(output, marker), $"{checks.Count} M2.3 native checks passed; no icon or startup writes.\n"); host.Exit(); }
    private static void Check(string name, bool passed) { checks.Add(new { name, passed }); File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(checks, Json)); if (!passed) throw new Exception(name); }
    private static async Task<JsonElement> Js(HubWindow window, string script) { using var doc = JsonDocument.Parse(await window.Browser.ExecuteScriptAsync(script)); return doc.RootElement.Clone(); }
    private static async Task WaitJs(HubWindow window, string script) { for (var i = 0; i < 200; i++) { if ((await Js(window, script)).ValueKind == JsonValueKind.True) return; await Task.Delay(50); } throw new TimeoutException(script); }
    private static async Task Wait(Func<bool> condition) { for (var i = 0; i < 100; i++) { if (condition()) return; await Task.Delay(50); } throw new TimeoutException("Native state"); }
    private static async Task<JsonElement> Request(HubWindow window, string type, object payload)
    {
        await Js(window, $"window.__systemReply=null;DesktopHubNative.bridge.request({JsonSerializer.Serialize(type)},{JsonSerializer.Serialize(payload,HubApplication.Json)}).then(result=>window.__systemReply={{ok:true,result}}).catch(error=>window.__systemReply={{ok:false,error:{{code:error.code,message:error.message}}}})");
        await WaitJs(window, "Boolean(window.__systemReply)"); return await Js(window, "window.__systemReply");
    }
    private static async Task Capture(HubWindow window, string name) { await Task.Delay(200); using var file = File.Create(Path.Combine(output, name)); await window.Browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, file); }
    private static async Task CaptureSystemPanel(HubWindow window, string prefix)
    {
        await Js(window, "Hub.actions['settings-section']({dataset:{section:'system'}});document.querySelector('.workspace-content').scrollTop=0");
        await Capture(window, prefix + "-top.png");
        // .settings-content flows inside the known .workspace-content scroll container in the accepted layout.
        await Js(window, "document.querySelector('.workspace-content').scrollTop=document.querySelector('.workspace-content').scrollHeight");
        await Capture(window, prefix + "-bottom.png");
        await Js(window, "document.querySelector('.workspace-content').scrollTop=0");
    }
    private static void SendHotkey(byte key)
    {
        foreach (var code in new byte[] { 0x11, 0x12, 0x10, key }) KeyEvent(code, 0, 0, 0);
        foreach (var code in new byte[] { key, 0x10, 0x12, 0x11 }) KeyEvent(code, 0, 2, 0);
    }
    private static void SendEscape() { KeyEvent(0x1b, 0, 0, 0); KeyEvent(0x1b, 0, 2, 0); }
    [DllImport("user32.dll", EntryPoint = "keybd_event")] private static extern void KeyEvent(byte key, byte scan, uint flags, nuint extra);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
