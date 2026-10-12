using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopHub.Core;

namespace DesktopHub.Windows;

// Opt-in M1.5 native acceptance. Isolated settings; system GUI apps and a repository-owned fixture.
internal static class SmokeTests
{
    private static readonly List<object> checks = new();
    private static string output = "";
    private static int fixturePid;
    private static readonly Dictionary<int, long> spawnedPids = new();

    public static void Prepare(string directory)
    {
        var expected = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "m15-smoke-data"));
        if (!Path.GetFullPath(directory).Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Smoke preparation is restricted to the isolated M1.5 directory.");
        Directory.CreateDirectory(expected);
        var settings = Path.Combine(expected, "settings.json");
        if (File.Exists(settings)) File.Delete(settings);
    }
    private static void Check(string name, bool passed, object? detail = null)
    {
        checks.Add(new { name, passed, detail });
        File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions(HubApplication.Json) { WriteIndented = true }));
        if (!passed) throw new InvalidOperationException("Smoke assertion failed: " + name);
    }

    public static async Task RunAsync(HubApplication host, bool restart = false)
    {
        output = Path.Combine(AppContext.BaseDirectory, "m15-smoke-evidence"); Directory.CreateDirectory(output);
        checks.Clear();
        if (restart && File.Exists(Path.Combine(output, "phase1-results.json")))
        {
            using var previous = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "phase1-results.json")));
            checks.AddRange(previous.RootElement.EnumerateArray().Select(item => (object)item.Clone()));
        }
        foreach (var stale in new[] { "error.txt", "picker-diagnostic.txt", "summary.txt" })
            if (File.Exists(Path.Combine(output, stale))) File.Delete(Path.Combine(output, stale));
        try
        {
            await WaitJs(host.Mini, "Boolean(window.DesktopHubDesktop?.getState().hostState)");
            if (restart)
            {
                await VerifyRestartAsync(host);
                File.WriteAllText(Path.Combine(output, "summary.txt"), $"{checks.Count} M1.5 native integration checks passed, including an actual host restart.\n");
                host.Exit(); return;
            }
            await Task.Delay(400);
            Measure(host, "mini-idle");
            Check("Mini is an independent HWND", NativeMethods.IsWindow(host.Mini.Handle), new { hwnd = host.Mini.Handle.ToInt64(), host.Mini.Width, host.Mini.Height, host.Mini.Topmost, host.Mini.AllowsTransparency });
            await Capture(host.Mini, "mini.png");
            var initial = await Request(host.Mini, "app.getState", new { });
            Check("WebView native handshake", initial.GetProperty("ok").GetBoolean(), initial);
            await Request(host.Mini, "window.openDock", new { });
            await WaitJs(host.Dock!, "Boolean(window.DesktopHubDesktop?.getState().hostState)");
            await Task.Delay(500);
            Check("Dock uses a separate visible HWND", host.Dock!.IsVisible && host.Dock.Handle != host.Mini.Handle && host.Dock.Topmost, new { hwnd = host.Dock.Handle.ToInt64(), host.Dock.Width, host.Dock.Height });
            await Capture(host.Dock, "dock.png");
            await Request(host.Dock, "window.closeDock", new { });
            Check("Dock really hides without exiting", !host.Dock.IsVisible && host.Mini.IsVisible);
            await Request(host.Mini, "window.openWorkspace", new { page = "home" });
            var workspace = host.Workspace!;
            await WaitJs(workspace, "Boolean(window.DesktopHubDesktop?.getState().hostState) && Hub.ui.page === 'home'");
            Check("Workspace uses a third near-fullscreen HWND", workspace.Handle != host.Mini.Handle && workspace.Handle != host.Dock.Handle && workspace.IsVisible, new { workspace.Width, workspace.Height, workspace.Left, workspace.Top, workArea = SystemParameters.WorkArea.ToString() });
            await Request(workspace, "settings.save", new { settings = new { theme = "dark", reducedMotion = true } });
            await WaitJs(workspace, "document.documentElement.dataset.theme === 'dark'");
            await Capture(workspace, "workspace-dark.png");
            await CaptureNativeFrame(workspace, "dark");
            CheckNativeResizeHitTests(workspace);
            var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var systemTargets = new[] { "winver.exe", "charmap.exe", "mstsc.exe", "msinfo32.exe", "colorcpl.exe" }
                .Select(name => Path.Combine(systemDirectory, name)).ToArray();
            Check("Five distinct ordinary Windows GUI programs are available", systemTargets.All(File.Exists), systemTargets);
            var registered = new List<string>();
            foreach (var target in systemTargets)
            {
                var appId = await AddViaPicker(workspace, host, target);
                registered.Add(appId);
                var apps = AppEntries(host);
                Check($"Native picker appends {Path.GetFileName(target)} without overwriting previous records",
                    apps.Length == registered.Count && registered.All(id => apps.Any(app => Id(app) == id))
                    && apps.Select(Id).Distinct().Count() == registered.Count && apps.Any(app => Id(app) == appId && SamePath(Text(app, "target"), target)),
                    new { appId, target, total = apps.Length });
            }
            await WaitJs(workspace, "document.querySelectorAll('.launcher-grid [data-action=app-launch]').length === 5");
            Check("Workspace shows five independent application launch buttons", (await Js(workspace, "document.querySelectorAll('.launcher-grid [data-action=app-launch]').length")).GetInt32() == 5);
            await WaitJs(workspace, "Array.from(document.querySelectorAll('.launcher-grid img.launcher-real-icon')).every(image=>image.complete && image.naturalWidth>0)");
            Check("Local application icons are extracted and rendered", (await Js(workspace, "document.querySelectorAll('.launcher-grid img.launcher-real-icon').length")).GetInt32() == 5
                && AppEntries(host).All(app => !string.IsNullOrWhiteSpace(Text(app, "icon"))), AppEntries(host).Select(app => new { id = Id(app), icon = Text(app, "icon") }));
            await Capture(workspace, "workspace-five-apps.png");
            for (var i = 0; i < registered.Count; i++)
            {
                var launchResult = await ClickLaunch(workspace, registered[i]);
                Check($"UI independently launches {Path.GetFileName(systemTargets[i])}", launchResult.GetProperty("success").GetBoolean(), launchResult);
                var pid = launchResult.GetProperty("processId").GetInt32();
                TrackOwnedProcess(pid);
                using var launched = Process.GetProcessById(pid);
                for (var wait = 0; wait < 200; wait++)
                {
                    launched.Refresh();
                    if (launched.HasExited || launched.MainWindowHandle != 0) break;
                    await Task.Delay(100);
                }
                launched.Refresh();
                Check($"Actual GUI process exists for {Path.GetFileName(systemTargets[i])}", !launched.HasExited && launched.MainWindowHandle != 0
                    && Path.GetFullPath(launched.MainModule!.FileName).Equals(systemTargets[i], StringComparison.OrdinalIgnoreCase),
                    new { processId = pid, hwnd = launched.MainWindowHandle.ToInt64(), executable = launched.HasExited ? null : launched.MainModule?.FileName });
                CloseOwnedProcess(pid);
                Check($"Test GUI {Path.GetFileName(systemTargets[i])} exited without interaction", launched.HasExited);
            }
            var fixtureSource = Environment.GetEnvironmentVariable("DESKTOP_HUB_TEST_EXE");
            if (fixtureSource is null || Path.GetFileName(fixtureSource) != "DesktopHub.LaunchFixture.exe" || !File.Exists(fixtureSource)) throw new InvalidOperationException("Set DESKTOP_HUB_TEST_EXE to the built repository fixture.");
            var fixtureDirectory = Path.Combine(host.DataDirectory, "fixture"); Directory.CreateDirectory(fixtureDirectory);
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(fixtureSource)!, "DesktopHub.LaunchFixture.*")) File.Copy(file, Path.Combine(fixtureDirectory, Path.GetFileName(file)), true);
            var fixture = Path.Combine(fixtureDirectory, "DesktopHub.LaunchFixture.exe");
            var fixtureId = await AddViaPicker(workspace, host, fixture);
            var othersBeforeEdit = AppEntries(host).Where(app => Id(app) != fixtureId).Select(app => app.GetRawText()).ToArray();
            await EnsureManage(workspace, fixtureId);
            await Click(workspace, $"[data-action=app-edit][data-id='{fixtureId}']");
            await WaitJs(workspace, "Boolean(document.querySelector('#app-name'))");
            var editedArguments = "--label \"M1.5 参数保留\"";
            await workspace.Browser.ExecuteScriptAsync($"document.querySelector('#app-name').value='M1.5 独立测试程序';document.querySelector('#app-arguments').value={JsonSerializer.Serialize(editedArguments)};document.querySelector('#app-working-directory').value={JsonSerializer.Serialize(fixtureDirectory)};");
            await Capture(workspace, "application-editor.png");
            await Click(workspace, $"[data-action=app-save][data-id='{fixtureId}']");
            await WaitJs(workspace, $"window.DesktopHubDesktop.getState().hostState.settings.appEntries.some(app=>app.id==={JsonSerializer.Serialize(fixtureId)} && app.name==='M1.5 独立测试程序')");
            var edited = AppEntries(host).Single(app => Id(app) == fixtureId);
            Check("Actual application editor changes only selected record", Text(edited, "name") == "M1.5 独立测试程序" && Text(edited, "arguments") == editedArguments
                && SamePath(Text(edited, "workingDirectory"), fixtureDirectory) && othersBeforeEdit.SequenceEqual(AppEntries(host).Where(app => Id(app) != fixtureId).Select(app => app.GetRawText())), edited);
            var fixtureResult = await ClickLaunch(workspace, fixtureId);
            fixturePid = fixtureResult.GetProperty("processId").GetInt32(); TrackOwnedProcess(fixturePid);
            var fixtureMarker = await WaitFixtureMarker(fixtureDirectory, fixturePid);
            Check("Edited arguments and working directory reach actual process", fixtureMarker.GetProperty("arguments").EnumerateArray().Select(arg => arg.GetString()).SequenceEqual(new[] { "--label", "M1.5 参数保留" })
                && SamePath(Text(fixtureMarker, "workingDirectory"), fixtureDirectory), fixtureMarker);
            await Capture(workspace, "launch-success.png");
            CloseOwnedProcess(fixturePid); fixturePid = 0;
            var shortcut = Path.Combine(fixtureDirectory, "M15 Fixture Shortcut.lnk");
            CreateShortcut(shortcut, fixture, "--shortcut \"LNK 参数\"", fixtureDirectory);
            var shortcutHash = Hash(shortcut); var fixtureHash = Hash(fixture);
            var shortcutId = await AddViaPicker(workspace, host, shortcut);
            var shortcutResult = await ClickLaunch(workspace, shortcutId);
            var shortcutPid = shortcutResult.GetProperty("processId").GetInt32(); TrackOwnedProcess(shortcutPid);
            var shortcutMarker = await WaitFixtureMarker(fixtureDirectory, shortcutPid);
            Check("Actual LNK picker and launch preserve shortcut target, arguments and working directory", SamePath(Text(AppEntries(host).Single(app => Id(app) == shortcutId), "target"), shortcut)
                && shortcutMarker.GetProperty("arguments").EnumerateArray().Select(arg => arg.GetString()).SequenceEqual(new[] { "--shortcut", "LNK 参数" })
                && SamePath(Text(shortcutMarker, "workingDirectory"), fixtureDirectory), shortcutMarker);
            CloseOwnedProcess(shortcutPid);
            var absent = fixture + ".temporarily-absent";
            File.Move(fixture, absent, true); // Only the harness-owned copy, never a user program.
            try
            {
                var failed = await ClickLaunch(workspace, fixtureId);
                var error = await Js(workspace, "window.DesktopHubDesktop.getState().launch");
                Check("Actual missing EXE failure appears in UI", !failed.GetProperty("success").GetBoolean() && Text(error, "code") == "target_missing", error);
                var failedShortcut = await ClickLaunch(workspace, shortcutId);
                var shortcutError = await Js(workspace, "window.DesktopHubDesktop.getState().launch");
                Check("Actual missing shortcut target failure appears in UI", !failedShortcut.GetProperty("success").GetBoolean() && Text(shortcutError, "code") == "target_missing", shortcutError);
                await Capture(workspace, "launch-failure.png");
            }
            finally { File.Move(absent, fixture, true); }
            var beforeRemove = AppEntries(host).Where(app => Id(app) != fixtureId).Select(StableConfiguration).ToArray();
            await EnsureManage(workspace, fixtureId);
            await Click(workspace, $"[data-action=app-edit][data-id='{fixtureId}']");
            await Click(workspace, $"[data-action=app-remove][data-id='{fixtureId}']");
            await WaitJs(workspace, $"Boolean(document.querySelector('[data-action=app-remove-confirm][data-id=\"{fixtureId}\"]'))");
            await Capture(workspace, "removal-confirmation.png");
            Check("Removal dialog clearly states original software stays installed", (await Js(workspace, "document.body.textContent.includes('卸载') && document.body.textContent.includes('移除')")).GetBoolean());
            await Click(workspace, $"[data-action=app-remove-confirm][data-id='{fixtureId}']");
            await WaitJs(workspace, $"!window.DesktopHubDesktop.getState().hostState.settings.appEntries.some(app=>app.id==={JsonSerializer.Serialize(fixtureId)})");
            Check("Actual removal preserves other application configurations and relative order", beforeRemove.SequenceEqual(AppEntries(host).Select(StableConfiguration)));
            Check("Removing an entry neither deletes nor modifies original software/shortcut", File.Exists(fixture) && Hash(fixture) == fixtureHash && File.Exists(shortcut) && Hash(shortcut) == shortcutHash);
            var reverse = AppEntries(host).Select(Id).Reverse().ToArray();
            await RequireRequest(workspace, "apps.reorder", new { appIds = reverse });
            Check("Application ordering is independently saved", AppEntries(host).OrderBy(app => app.GetProperty("displayOrder").GetInt32()).Select(Id).SequenceEqual(reverse));
            await RequireRequest(workspace, "launcher.setPins", new { surface = "dock", pins = registered.Select(id => new { itemType = "app", itemId = id, enabled = true }).Reverse().ToArray() });
            var pinScene = new Scene { Id = Guid.NewGuid().ToString("D"), Name = "回归测试场景" };
            host.SceneLibrary.Save(pinScene); host.SettingsChanged();
            var sceneId = pinScene.Id;
            await RequireRequest(workspace, "launcher.setPins", new { surface = "mini", pins = new[] { new { itemType = "app", itemId = registered[0], enabled = true }, new { itemType = "scene", itemId = sceneId, enabled = true } } });
            host.CloseWorkspace();
            await WaitJs(host.Mini, "document.querySelectorAll('.launcher-pin').length === 2");
            Check("Mini mixes two application/scene pins without duplicating AppEntry", (await Js(host.Mini, "document.querySelectorAll('.launcher-pin[data-action=app-launch]').length === 1")).GetBoolean() && AppEntries(host).Length == 6);
            await Capture(host.Mini, "mini-mixed-pins.png");
            await Request(host.Mini, "window.openDock", new { });
            await WaitJs(host.Dock, "document.querySelectorAll('.dock-app-grid [data-action=app-launch]').length === 5");
            var dockIds = (await Js(host.Dock, "Array.from(document.querySelectorAll('.dock-app-grid [data-action=app-launch]'),button=>button.dataset.id)"))
                .EnumerateArray().Select(app => app.GetString()).ToArray();
            Check("Dock displays independent application pin order", dockIds.SequenceEqual(registered.AsEnumerable().Reverse()), dockIds);
            var dockLaunch = await ClickLaunch(host.Dock, registered[4]);
            var dockPid = dockLaunch.GetProperty("processId").GetInt32(); TrackOwnedProcess(dockPid);
            Check("Dock application icon directly launches its own program", dockLaunch.GetProperty("success").GetBoolean(), dockLaunch);
            CloseOwnedProcess(dockPid);
            await host.Dock.Browser.ExecuteScriptAsync("document.querySelector('#dock-icon-size').value='compact';document.querySelector('#dock-icon-size').dispatchEvent(new Event('change',{bubbles:true}));");
            await WaitJs(host.Dock, "window.DesktopHubDesktop.getState().hostState.settings.dockIconSize === 'compact'");
            Check("Dock icon size preference is saved", Text(Settings(host), "dockIconSize") == "compact");
            await Capture(host.Dock, "dock-five-apps.png");
            host.CloseDock();
            await host.OpenWorkspaceAsync("home");
            var forbidden = await Request(workspace, "settings.save", new { settings = new { appEntries = new[] { new { target = @"C:\Windows\System32\cmd.exe" } } } });
            Check("Frontend path injection rejected", !forbidden.GetProperty("ok").GetBoolean() && forbidden.GetProperty("error").GetProperty("code").GetString() == "FORBIDDEN_SETTING", forbidden);
            var invalidLaunch = await Request(workspace, "apps.launch", new { appId = "" });
            Check("Invalid launch request is rejected before reserving the launcher", !invalidLaunch.GetProperty("ok").GetBoolean() && Text(invalidLaunch.GetProperty("error"), "code") == "INVALID_PARAMS", invalidLaunch);
            var wrongId = await Request(workspace, "apps.launch", new { appId = Guid.NewGuid().ToString("D") });
            Check("Launcher recovers after rejected request and refuses unregistered target", wrongId.GetProperty("ok").GetBoolean() && !wrongId.GetProperty("result").GetProperty("success").GetBoolean(), wrongId);
            var unknown = await RequestRaw(workspace, "system.exec", new { command = "anything" });
            Check("Native allowlist rejects arbitrary command", !unknown.GetProperty("ok").GetBoolean() && unknown.GetProperty("error").GetProperty("code").GetString() == "UNKNOWN_MESSAGE", unknown);
            await Request(workspace, "settings.save", new { settings = new { theme = "light" } });
            await WaitJs(workspace, "document.documentElement.dataset.theme === 'light'");
            await Capture(workspace, "workspace-light.png");
            await CaptureNativeFrame(workspace, "light");
            var reopened = new SettingsStore(host.DataDirectory); reopened.Load();
            Check("New store reads all saved application records and pins", reopened.Current.Theme == "light" && JsonSerializer.SerializeToElement(reopened.Current, HubApplication.Json).GetProperty("appEntries").GetRawText() == Settings(host).GetProperty("appEntries").GetRawText()
                && JsonSerializer.SerializeToElement(reopened.Current, HubApplication.Json).GetProperty("launcherPins").GetRawText() == Settings(host).GetProperty("launcherPins").GetRawText());
            workspace.Left = 45; workspace.Top = 40; host.SaveWindow(workspace); reopened.Load();
            Check("Window coordinates persist", reopened.Current.WorkspaceBounds.Left == workspace.Left && reopened.Current.WorkspaceBounds.Top == workspace.Top);
            var beforeUri = workspace.Browser.Source!.ToString();
            await workspace.Browser.ExecuteScriptAsync("location.href='https://example.com/'");
            await Task.Delay(300);
            Check("Remote navigation blocked", workspace.Browser.Source!.ToString() == beforeUri && host.BlockedRequests.Contains("https://example.com/"));
            host.CloseDock(); host.CloseWorkspace();
            var focusReply = await Request(host.Mini, "apps.launch", new { appId = shortcutId });
            Check("Controlled focus fixture launches from its saved application ID", focusReply.GetProperty("ok").GetBoolean()
                && focusReply.GetProperty("result").GetProperty("success").GetBoolean(), focusReply);
            var focusFixture = focusReply.GetProperty("result");
            var focusPid = focusFixture.GetProperty("processId").GetInt32(); TrackOwnedProcess(focusPid);
            await WaitFixtureMarker(fixtureDirectory, focusPid);
            using var process = Process.GetProcessById(focusPid);
            for (var wait = 0; wait < 100 && process.MainWindowHandle == 0; wait++) { process.Refresh(); await Task.Delay(100); }
            process.Refresh(); var foregroundHandle = process.MainWindowHandle;
            NativeMethods.GetWindowThreadProcessId(foregroundHandle, out var focusOwner);
            Check("Controlled foreground HWND belongs to this test process", focusOwner == focusPid
                && process.StartTime.ToUniversalTime().Ticks == spawnedPids[focusPid]);
            var focusMonitor = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
            if (!NativeMethods.GetMonitorInfo(NativeMethods.MonitorFromWindow(host.Mini.Handle, 2), ref focusMonitor)
                || !NativeMethods.GetWindowRect(foregroundHandle, out var focusBounds)) throw new InvalidOperationException("Test foreground bounds unavailable.");
            if (!SetWindowPos(foregroundHandle, 0, focusMonitor.Work.Right - (focusBounds.Right - focusBounds.Left) - 40,
                focusMonitor.Work.Top + 40, 0, 0, 0x15)) throw new InvalidOperationException("Controlled test window could not be positioned.");
            NativeMethods.SetForegroundWindow(foregroundHandle); await Task.Delay(250);
            Check("Controlled external foreground ready", NativeMethods.GetForegroundWindow() == foregroundHandle, new { hwnd = foregroundHandle.ToInt64() });
            // Prepare an unobscured Mini within the ordinary window stack; NOACTIVATE preserves the external foreground.
            if (!SetWindowPos(host.Mini.Handle, 0, 0, 0, 0, 0, 0x13)) throw new InvalidOperationException("Test Mini could not be exposed.");
            await Task.Delay(200);
            Check("Exposing Mini for a physical click preserves external foreground", NativeMethods.GetForegroundWindow() == foregroundHandle);
            var brand = await Js(host.Mini, "(()=>{const r=document.querySelector('.mini-brand').getBoundingClientRect();return {x:r.x+r.width/2,y:r.y+r.height/2};})()");
            var clickPoint = host.Mini.PointToScreen(new Point(brand.GetProperty("x").GetDouble(), brand.GetProperty("y").GetDouble()));
            var hit = WindowFromPoint(new NativeMethods.Point { X = (int)clickPoint.X, Y = (int)clickPoint.Y });
            var hitRoot = GetAncestor(hit, 2);
            NativeMethods.GetWindowThreadProcessId(hitRoot, out var hitOwner);
            Check("Physical Mini click point belongs to its actual native window", hitRoot == host.Mini.Handle,
                new { x = clickPoint.X, y = clickPoint.Y, hit = hit.ToInt64(), root = hitRoot.ToInt64(), hitOwner, mini = host.Mini.Handle.ToInt64() });
            SendClick((int)clickPoint.X, (int)clickPoint.Y);
            for (var i = 0; i < 50 && !workspace.IsVisible; i++) await Task.Delay(100);
            await Task.Delay(350);
            Check("Actual Mini click makes Workspace foreground", NativeMethods.GetForegroundWindow() == workspace.Handle,
                new { foreground = NativeMethods.GetForegroundWindow().ToInt64(), workspace = workspace.Handle.ToInt64(), workspace.IsActive });
            SendEscape();
            for (var i = 0; i < 50 && workspace.IsVisible; i++) await Task.Delay(100);
            Check("Real Esc hides Workspace and restores prior foreground", !workspace.IsVisible && NativeMethods.GetForegroundWindow() == foregroundHandle, new { host.FocusRestoreResult, foreground = NativeMethods.GetForegroundWindow().ToInt64() });
            host.AllowSmokeHover = true;
            var hoverWatch = Stopwatch.StartNew();
            SetCursorPos(1, 700);
            await Task.Delay(300);
            Check("Native edge hover respects delay", !host.Dock.IsVisible);
            for (var i = 0; i < 20 && !host.Dock.IsVisible; i++) await Task.Delay(100);
            Check("Native edge hover opens actual Dock", host.Dock.IsVisible, new { elapsedMilliseconds = hoverWatch.ElapsedMilliseconds });
            host.AllowSmokeHover = false; host.CloseDock();
            // Measure after image buffers have been collected; this is still a short-run sample.
            await Task.Delay(1000);
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            Measure(host, "three-surfaces-loaded-hidden");
            Check("Local UI stays offline", host.Windows.All(w => w.Browser.Source!.Host == "desktop-hub.local"), host.BlockedRequests);
            var checkpoint = new { hostProcessId = Environment.ProcessId, settings = Settings(host), originalApplicationIds = registered };
            File.WriteAllText(Path.Combine(output, "restart-checkpoint.json"), JsonSerializer.Serialize(checkpoint, new JsonSerializerOptions(HubApplication.Json) { WriteIndented = true }));
            File.Copy(Path.Combine(output, "results.json"), Path.Combine(output, "phase1-results.json"), true);
            File.WriteAllText(Path.Combine(output, "phase1-summary.txt"), $"{checks.Count} M1.5 native integration checks passed before restart.\nHost working set {Process.GetCurrentProcess().WorkingSet64}\n");
        }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(output, "error.txt"), e.ToString()); host.Log("Smoke", e.ToString()); Application.Current.Shutdown(1); return;
        }
        finally { foreach (var pid in spawnedPids.Keys.ToArray()) CloseOwnedProcess(pid); }
        host.Exit();
    }

    private static JsonElement Settings(HubApplication host) => JsonSerializer.SerializeToElement(host.Store.Current, HubApplication.Json);
    private static JsonElement[] AppEntries(HubApplication host) => Settings(host).GetProperty("appEntries").EnumerateArray().Select(app => app.Clone()).ToArray();
    private static string Text(JsonElement value, string property) => value.TryGetProperty(property, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
    private static string Id(JsonElement app) => Text(app, "id");
    private static bool SamePath(string actual, string expected) => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    private static string CanonicalJson(JsonElement value) => JsonSerializer.Serialize(value, HubApplication.Json);
    private static string StableConfiguration(JsonElement app) => string.Join("\n", app.EnumerateObject().Where(property => property.Name != "displayOrder").Select(property => property.Name + ":" + property.Value.GetRawText()));
    private static string Hash(string filename) { using var stream = File.OpenRead(filename); return Convert.ToHexString(SHA256.HashData(stream)); }

    private static async Task Click(HubWindow window, string selector)
    {
        await WaitJs(window, $"Boolean(document.querySelector({JsonSerializer.Serialize(selector)})) && !document.querySelector({JsonSerializer.Serialize(selector)}).disabled");
        await window.Browser.ExecuteScriptAsync($"document.querySelector({JsonSerializer.Serialize(selector)}).click()");
    }

    private static async Task EnsureManage(HubWindow window, string appId)
    {
        var selector = $"[data-action=app-edit][data-id='{appId}']";
        if ((await Js(window, $"Boolean(document.querySelector({JsonSerializer.Serialize(selector)}))")).GetBoolean()) return;
        await Click(window, "[data-action=apps-manage]");
        await WaitJs(window, $"Boolean(document.querySelector({JsonSerializer.Serialize(selector)}))");
    }

    private static async Task<JsonElement> RequireRequest(HubWindow window, string type, object payload)
    {
        var result = await Request(window, type, payload);
        if (!result.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(type + ": " + result.GetRawText());
        return result.GetProperty("result").Clone();
    }

    private static async Task<string> AddViaPicker(HubWindow window, HubApplication host, string filename)
    {
        var before = AppEntries(host).Select(Id).ToHashSet();
        try
        {
            var automation = Task.Run(() => SelectNativePicker(filename));
            await Click(window, "[data-action=app-add]");
            await automation.WaitAsync(TimeSpan.FromSeconds(30));
            for (var wait = 0; wait < 100 && AppEntries(host).Length <= before.Count; wait++) await Task.Delay(100);
            var added = AppEntries(host).Where(app => !before.Contains(Id(app))).ToArray();
            if (added.Length != 1) throw new InvalidOperationException("Picker did not append exactly one entry: " + filename);
            var id = Id(added[0]);
            await WaitJs(window, $"window.DesktopHubDesktop.getState().hostState.settings.appEntries.some(app=>app.id==={JsonSerializer.Serialize(id)})");
            await WaitJs(window, "Boolean(document.querySelector('[data-action=app-add]')) && !document.querySelector('[data-action=app-add]').disabled");
            return id;
        }
        catch { CancelNativePicker(0); throw; }
    }

    private static async Task<JsonElement> ClickLaunch(HubWindow window, string appId)
    {
        // Passive observation preserves the production bridge and actual DOM click path.
        await window.Browser.ExecuteScriptAsync("window.__smokeLaunchResult=null;chrome.webview.addEventListener('message',function observeLaunch(e){let message=e.data;if(typeof message==='string'){try{message=JSON.parse(message);}catch{return;}}if(message?.result && typeof message.result.success==='boolean'){window.__smokeLaunchResult=message.result;chrome.webview.removeEventListener('message',observeLaunch);}});");
        await Click(window, $"[data-action=app-launch][data-id='{appId}']");
        await WaitJs(window, "Boolean(window.__smokeLaunchResult)");
        await WaitJs(window, $"window.DesktopHubDesktop.getState().launch.appId==={JsonSerializer.Serialize(appId)} && ['success','error'].includes(window.DesktopHubDesktop.getState().launch.status)");
        return await Js(window, "window.__smokeLaunchResult");
    }

    private static async Task<JsonElement> WaitFixtureMarker(string directory, int pid)
    {
        var marker = Path.Combine(directory, $"fixture-started-{pid}.json");
        for (var wait = 0; wait < 100 && !File.Exists(marker); wait++) await Task.Delay(100);
        using var document = JsonDocument.Parse(File.ReadAllText(marker));
        return document.RootElement.Clone();
    }

    private static void TrackOwnedProcess(int pid)
    {
        using var process = Process.GetProcessById(pid);
        spawnedPids[pid] = process.StartTime.ToUniversalTime().Ticks;
        File.WriteAllText(Path.Combine(output, "owned-launch-processes.json"), JsonSerializer.Serialize(spawnedPids.Select(pair => new { processId = pair.Key,
            startTimeUtcTicks = pair.Value }), new JsonSerializerOptions(HubApplication.Json) { WriteIndented = true }));
    }

    private static void CloseOwnedProcess(int pid)
    {
        // The PID came from the native launch performed by this opt-in test; no process-name cleanup.
        try
        {
            using var process = Process.GetProcessById(pid);
            if (spawnedPids.TryGetValue(pid, out var started) && !process.HasExited && process.StartTime.ToUniversalTime().Ticks == started)
            {
                var deadline = Stopwatch.StartNew(); var closePosted = false;
                process.Refresh();
                if (process.MainWindowHandle != 0) process.CloseMainWindow();
                while (!process.HasExited && deadline.ElapsedMilliseconds < 10000)
                {
                    process.Refresh();
                    if (!closePosted && deadline.ElapsedMilliseconds >= 2000) closePosted = TryCloseOwnedWindow(pid);
                    if (process.WaitForExit(100)) break;
                }
            }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }

    internal static bool TryCloseOwnedWindow(int pid)
    {
        try
        {
            var windows = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, pid));
            foreach (AutomationElement window in windows)
            {
                var handle = (nint)window.Current.NativeWindowHandle;
                if (handle == 0 || !NativeMethods.IsWindow(handle)) continue;
                NativeMethods.GetWindowThreadProcessId(handle, out var owner);
                if (owner != pid) continue;
                // Property sheets such as Color Management require their real Cancel/Close command.
                var cancel = GetDlgItem(handle, 2);
                if (cancel != 0 && IsWindowEnabled(cancel))
                {
                    NativeMethods.GetWindowThreadProcessId(cancel, out var buttonOwner);
                    var caption = new System.Text.StringBuilder(256); GetWindowText(cancel, caption, caption.Capacity);
                    if (buttonOwner == pid && IsCloseCaption(caption.ToString())) return PostMessage(cancel, 0xF5, 0, 0);
                }
                var buttons = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                foreach (AutomationElement button in buttons)
                {
                    if (button.Current.ProcessId != pid || !button.Current.IsEnabled || button.Current.IsOffscreen || !IsCloseCaption(button.Current.Name)) continue;
                    if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)) { ((InvokePattern)pattern).Invoke(); return true; }
                }
            }
        }
        catch (ElementNotAvailableException) { }
        catch (InvalidOperationException) { }
        return false;
    }

    private static bool IsCloseCaption(string caption)
    {
        var normalized = System.Text.RegularExpressions.Regex.Replace(caption, @"\s*\(&?[^)]*\)\s*", "").Replace("&", "").Trim();
        return normalized is "关闭" or "取消" || normalized.Equals("Close", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals("Cancel", StringComparison.OrdinalIgnoreCase);
    }

    private static void CreateShortcut(string shortcut, string target, string arguments, string directory)
    {
        object? shell = null, link = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("Windows shortcut COM service is unavailable."));
            dynamic dynamicShell = shell!;
            link = dynamicShell.CreateShortcut(shortcut);
            dynamic dynamicLink = link;
            dynamicLink.TargetPath = target; dynamicLink.Arguments = arguments; dynamicLink.WorkingDirectory = directory;
            dynamicLink.IconLocation = target + ",0"; dynamicLink.Save();
        }
        finally
        {
            if (link is not null && Marshal.IsComObject(link)) Marshal.FinalReleaseComObject(link);
            if (shell is not null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }

    private static async Task VerifyRestartAsync(HubApplication host)
    {
        using var checkpointDocument = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "restart-checkpoint.json")));
        var checkpoint = checkpointDocument.RootElement;
        var saved = checkpoint.GetProperty("settings"); var current = Settings(host);
        Check("Persistence is verified in a second actual Windows host process", checkpoint.GetProperty("hostProcessId").GetInt32() != Environment.ProcessId,
            new { previousProcessId = checkpoint.GetProperty("hostProcessId").GetInt32(), currentProcessId = Environment.ProcessId });
        Check("Actual restart retains all application names, paths, parameters, work directories, icons, order and pin flags", CanonicalJson(current.GetProperty("appEntries")) == CanonicalJson(saved.GetProperty("appEntries")), current.GetProperty("appEntries"));
        Check("Actual restart retains surface pin positions and Dock icon size", CanonicalJson(current.GetProperty("launcherPins")) == CanonicalJson(saved.GetProperty("launcherPins"))
            && Text(current, "dockIconSize") == Text(saved, "dockIconSize"), current.GetProperty("launcherPins"));
        var originalIds = checkpoint.GetProperty("originalApplicationIds").EnumerateArray().Select(item => item.GetString()).ToArray();
        Check("All original five different software entries survive host restart", originalIds.All(id => AppEntries(host).Any(app => Id(app) == id)) && AppEntries(host).Length == 6);
        await WaitJs(host.Mini, "document.querySelectorAll('.launcher-pin').length === 2");
        await Request(host.Mini, "window.openDock", new { });
        await WaitJs(host.Dock!, "Boolean(window.DesktopHubDesktop?.getState().hostState) && document.querySelectorAll('.dock-app-grid [data-action=app-launch]').length === 5");
        Check("Restarted Dock renders five saved application launch entries", host.Dock!.IsVisible);
        await Capture(host.Dock, "restart-dock.png");
        host.CloseDock();
        await Request(host.Dock!, "window.openWorkspace", new { page = "home" });
        await WaitJs(host.Workspace!, "Boolean(window.DesktopHubDesktop?.getState().hostState) && document.querySelectorAll('.launcher-grid [data-action=app-launch]').length === 6");
        Check("Restarted Workspace renders the complete saved application library", host.Workspace!.IsVisible);
        await Capture(host.Workspace, "restart-workspace.png");
        File.WriteAllText(Path.Combine(output, "restart-completed.json"), JsonSerializer.Serialize(new { completedUtc = DateTime.UtcNow, processId = Environment.ProcessId, applications = AppEntries(host).Length }));
    }

    private static async Task<JsonElement> Js(HubWindow window, string expression)
    {
        using var doc = JsonDocument.Parse(await window.Browser.ExecuteScriptAsync(expression)); return doc.RootElement.Clone();
    }
    private static async Task WaitJs(HubWindow window, string expression)
    {
        for (var i = 0; i < 200; i++) { if ((await Js(window, expression)).ValueKind == JsonValueKind.True) return; await Task.Delay(100); }
        throw new TimeoutException(expression);
    }
    private static async Task<JsonElement> Request(HubWindow window, string type, object payload)
    {
        await window.Browser.ExecuteScriptAsync($"window.__smokeReply=null; DesktopHubNative.bridge.request({JsonSerializer.Serialize(type)},{JsonSerializer.Serialize(payload, HubApplication.Json)}).then(result=>window.__smokeReply={{ok:true,result}}).catch(error=>window.__smokeReply={{ok:false,error:{{code:error.code,message:error.message}}}})");
        await WaitJs(window, "Boolean(window.__smokeReply)"); return await Js(window, "window.__smokeReply");
    }
    private static async Task<JsonElement> RequestRaw(HubWindow window, string type, object payload)
    {
        await window.Browser.ExecuteScriptAsync($"window.__smokeReply=null; chrome.webview.addEventListener('message',function receive(e){{if(e.data.id==='smoke-raw'){{window.__smokeReply=e.data;chrome.webview.removeEventListener('message',receive);}}}});chrome.webview.postMessage({{version:1,id:'smoke-raw',type:{JsonSerializer.Serialize(type)},payload:{JsonSerializer.Serialize(payload)}}})");
        await WaitJs(window, "Boolean(window.__smokeReply)"); return await Js(window, "window.__smokeReply");
    }
    private static async Task Capture(HubWindow window, string filename)
    {
        await Task.Delay(200);
        if (window.Surface == "workspace")
        {
            using var preview = File.Create(Path.Combine(output, filename));
            await window.Browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, preview);
            return;
        }
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * dpi.DpiScaleX), (int)(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window);
        if (window.Surface is "mini" or "dock")
        {
            var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4];
            bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
            var transparent = 0; var translucent = 0;
            for (var i = 3; i < pixels.Length; i += 4) { if (pixels[i] == 0) transparent++; else if (pixels[i] < 255) translucent++; }
            File.WriteAllText(Path.Combine(output, window.Surface + "-alpha.json"), JsonSerializer.Serialize(new { transparentPixels = transparent, translucentPixels = translucent, totalPixels = pixels.Length / 4 }));
        }
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, filename)); encoder.Save(stream);
    }

    private static async Task CaptureNativeFrame(HubWindow window, string theme)
    {
        window.Activate(); NativeMethods.SetForegroundWindow(window.Handle);
        await Task.Delay(250);
        if (NativeMethods.GetForegroundWindow() != window.Handle)
        {
            // A real click on our own resize edge activates the window without moving the pointer during the click.
            var point = window.PointToScreen(new Point(2, window.ActualHeight / 2));
            SendClick((int)point.X, (int)point.Y);
            await Task.Delay(250);
        }
        Check($"Native {theme} frame capture uses the actual foreground Workspace", NativeMethods.GetForegroundWindow() == window.Handle);
        if (!NativeMethods.GetWindowRect(window.Handle, out var rectangle)) throw new InvalidOperationException("Cannot read the Workspace frame rectangle.");
        var width = rectangle.Right - rectangle.Left; var height = rectangle.Bottom - rectangle.Top;
        var visible = rectangle;
        if (DwmGetWindowAttribute(window.Handle, 9, out NativeMethods.Rect extended, 16) == 0) visible = extended;
        nint screen = 0, memory = 0, bitmap = 0, previous = 0;
        try
        {
            screen = GetDC(0); memory = CreateCompatibleDC(screen); bitmap = CreateCompatibleBitmap(screen, width, height);
            if (screen == 0 || memory == 0 || bitmap == 0) throw new InvalidOperationException("Cannot create the native frame capture surface.");
            previous = SelectObject(memory, bitmap);
            if (!BitBlt(memory, 0, 0, width, height, screen, rectangle.Left, rectangle.Top, 0x40CC0020))
                throw new InvalidOperationException("Windows rejected native frame capture.");
            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var image = new FormatConvertedBitmap(source, PixelFormats.Bgr32, null, 0); image.Freeze();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(Path.Combine(output, $"workspace-{theme}-native-frame.png"))) encoder.Save(file);
            var pixels = new byte[width * height * 4]; image.CopyPixels(pixels, width * 4, 0);
            var top = Math.Clamp(visible.Top - rectangle.Top, 0, height - 6);
            var left = Math.Clamp(visible.Left - rectangle.Left + (visible.Right - visible.Left) * 2 / 5, 0, width - 1);
            var right = Math.Clamp(visible.Left - rectangle.Left + (visible.Right - visible.Left) * 3 / 5, left + 1, width);
            var rows = new List<object>(); var allRowsMatch = true;
            for (var offset = 0; offset < 6; offset++)
            {
                var matching = 0; long red = 0, green = 0, blue = 0;
                for (var x = left; x < right; x++)
                {
                    var index = ((top + offset) * width + x) * 4;
                    var b = pixels[index]; var g = pixels[index + 1]; var r = pixels[index + 2];
                    red += r; green += g; blue += b;
                    if (theme == "dark" ? r < 140 && g < 140 && b < 140 : r > 155 && g > 155 && b > 155) matching++;
                }
                var count = right - left; var ratio = (double)matching / count;
                rows.Add(new { offset, matchingRatio = ratio, averageRed = (double)red / count, averageGreen = (double)green / count, averageBlue = (double)blue / count });
                allRowsMatch &= ratio >= 0.98;
            }
            var cropped = new CroppedBitmap(image, new Int32Rect(0, top, width, Math.Min(24, height - top)));
            var cropEncoder = new PngBitmapEncoder(); cropEncoder.Frames.Add(BitmapFrame.Create(cropped));
            using (var cropFile = File.Create(Path.Combine(output, $"workspace-{theme}-native-top.png"))) cropEncoder.Save(cropFile);
            Check(theme == "dark" ? "Actual dark Workspace top frame has no narrow light strip" : "Actual light Workspace top frame follows light theme", allRowsMatch,
                new { windowLeft = rectangle.Left, windowTop = rectangle.Top, width, height, visibleTopOffset = top, sampleLeft = left, sampleRight = right, rows });
        }
        finally
        {
            if (previous != 0 && memory != 0) SelectObject(memory, previous);
            if (bitmap != 0) DeleteObject(bitmap);
            if (memory != 0) DeleteDC(memory);
            if (screen != 0) ReleaseDC(0, screen);
        }
    }

    private static void CheckNativeResizeHitTests(HubWindow window)
    {
        if (!NativeMethods.GetWindowRect(window.Handle, out var rectangle)) throw new InvalidOperationException("Cannot read resize bounds.");
        var middleX = (rectangle.Left + rectangle.Right) / 2; var middleY = (rectangle.Top + rectangle.Bottom) / 2;
        var points = new[] { new { edge = "top", x = middleX, y = rectangle.Top + 1, expected = 12 },
            new { edge = "left", x = rectangle.Left + 1, y = middleY, expected = 10 },
            new { edge = "right", x = rectangle.Right - 2, y = middleY, expected = 11 },
            new { edge = "bottom", x = middleX, y = rectangle.Bottom - 2, expected = 15 },
            new { edge = "center", x = middleX, y = middleY, expected = 1 } };
        foreach (var point in points)
        {
            var coordinates = unchecked((int)((uint)(ushort)point.x | ((uint)(ushort)point.y << 16)));
            var actual = SendMessage(window.Handle, 0x84, 0, coordinates).ToInt32(); // WM_NCHITTEST
            Check($"Actual Workspace {point.edge} hit test preserves {(point.edge == "center" ? "client interaction" : "window resizing")}", actual == point.expected,
                new { point.x, point.y, expected = point.expected, actual });
        }
    }

    [DllImport("user32.dll")] private static extern nint GetDC(nint window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint window, nint dc);
    [DllImport("user32.dll")] private static extern nint SendMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern bool PostMessage(nint window, uint message, nint wParam, nint lParam);
    [DllImport("user32.dll")] private static extern nint GetDlgItem(nint dialog, int id);
    [DllImport("user32.dll")] private static extern bool IsWindowEnabled(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")] private static extern nint GetLastActivePopup(nint window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint window, System.Text.StringBuilder text, int maximum);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out NativeMethods.Rect value, int size);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint value);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint target, int x, int y, int width, int height, nint source, int sourceX, int sourceY, uint operation);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint value);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    private static void SelectNativePicker(string filename)
    {
        nint dialogHandle = 0;
        try { SelectNativePickerCore(filename, ref dialogHandle); }
        catch { CancelNativePicker(dialogHandle); throw; }
    }

    private static void SelectNativePickerCore(string filename, ref nint dialogHandle)
    {
        var deadline = Stopwatch.StartNew();
        AutomationElement? dialog = null;
        var condition = new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty, Environment.ProcessId), new PropertyCondition(AutomationElement.NameProperty, "选择本地程序"));
        while (deadline.ElapsedMilliseconds < 20000 && dialog is null)
        {
            var hwnd = FindWindow(null, "选择本地程序");
            if (hwnd != 0 && NativeMethods.IsOurWindow(hwnd)) dialog = AutomationElement.FromHandle(hwnd);
            dialog ??= AutomationElement.RootElement.FindFirst(TreeScope.Descendants, condition);
            Thread.Sleep(100);
        }
        if (dialog is null)
        {
            var own = AutomationElement.RootElement.FindAll(TreeScope.Children, new PropertyCondition(AutomationElement.ProcessIdProperty, Environment.ProcessId));
            File.WriteAllText(Path.Combine(output, "picker-diagnostic.txt"), string.Join("\n", own.Cast<AutomationElement>().Select(e => e.Current.Name + " " + e.Current.ClassName)));
            throw new TimeoutException("Native picker did not open.");
        }
        dialogHandle = (nint)dialog.Current.NativeWindowHandle;
        if (dialogHandle == 0 || !NativeMethods.IsWindow(dialogHandle) || !NativeMethods.IsOurWindow(dialogHandle))
            throw new InvalidOperationException("Native picker ownership could not be confirmed.");
        AutomationElement? filenameEdit = null;
        var valueSet = false;
        while (!valueSet && deadline.ElapsedMilliseconds < 20000)
        {
            try
            {
                var edits = dialog.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                var edit = edits.Cast<AutomationElement>().FirstOrDefault(e => e.Current.Name.Contains("文件名") || e.Current.Name.Contains("File name", StringComparison.OrdinalIgnoreCase)
                    || e.Current.AutomationId == "1148");
                if (edit is not null && edit.Current.IsEnabled && !edit.Current.IsOffscreen && edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern))
                {
                    ((ValuePattern)pattern).SetValue(filename); filenameEdit = edit; valueSet = true;
                }
            }
            catch (ElementNotAvailableException) { }
            if (!valueSet) Thread.Sleep(100);
        }
        if (!valueSet) { PickerDiagnostic(dialog); throw new TimeoutException("File name field unavailable after bounded native UI retries."); }
        Thread.Sleep(200); // Setting the path can rebuild the shell's button control tree.
        var buttonDeadline = Math.Min(deadline.ElapsedMilliseconds + 1000, 20000);
        while (deadline.ElapsedMilliseconds < buttonDeadline)
        {
            try
            {
                var buttonCondition = new OrCondition(new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.SplitButton));
                var buttons = dialog.FindAll(TreeScope.Descendants, buttonCondition).Cast<AutomationElement>()
                    .OrderByDescending(e => e.Current.AutomationId == "1");
                foreach (var button in buttons)
                {
                    var name = button.Current.Name;
                    if (!(button.Current.AutomationId == "1" || name.Contains("打开") || name.Contains("Open", StringComparison.OrdinalIgnoreCase)
                        || name.Contains("确定") || name.Equals("OK", StringComparison.OrdinalIgnoreCase))) continue;
                    if (!button.Current.IsEnabled || button.Current.IsOffscreen || !button.TryGetCurrentPattern(InvokePattern.Pattern, out var pattern)) continue;
                    ((InvokePattern)pattern).Invoke();
                    WaitPickerClosed(dialogHandle); return;
                }
            }
            catch (ElementNotAvailableException) { }
            Thread.Sleep(100);
        }
        // Some Windows 11 shell builds omit the Open control from UIA after the first dialog.
        // IDC1/Enter still traverse the actual dialog's selection and validation behavior.
        PickerDiagnostic(dialog);
        if (filenameEdit is null || !filenameEdit.TryGetCurrentPattern(ValuePattern.Pattern, out var valuePattern)
            || !SamePath(((ValuePattern)valuePattern).Current.Value, filename))
            throw new InvalidOperationException("Picker filename could not be confirmed before native input fallback.");
        if (!NativeMethods.IsWindow(dialogHandle) || !NativeMethods.IsOurWindow(dialogHandle))
            throw new InvalidOperationException("Picker ownership changed before native input fallback.");
        var nativeOpen = GetDlgItem(dialogHandle, 1);
        if (nativeOpen != 0 && NativeMethods.IsOurWindow(nativeOpen) && IsWindowEnabled(nativeOpen) && IsWindowVisible(nativeOpen))
        {
            File.AppendAllText(Path.Combine(output, "picker-interactions.txt"), $"{filename}\tIDC1 BM_CLICK\thwnd={dialogHandle}\tpid={Environment.ProcessId}\n");
            SendMessage(nativeOpen, 0xF5, 0, 0); // BM_CLICK, actual Open button.
            if (PickerClosedSoon(dialogHandle, 500)) return;
        }
        NativeMethods.SetForegroundWindow(dialogHandle); filenameEdit.SetFocus(); Thread.Sleep(100);
        if (NativeMethods.GetForegroundWindow() != dialogHandle)
        {
            var bounds = filenameEdit.Current.BoundingRectangle;
            if (bounds.IsEmpty) throw new InvalidOperationException("Picker filename field has no clickable native bounds.");
            SendClick((int)(bounds.Left + bounds.Width / 2), (int)(bounds.Top + bounds.Height / 2));
            Thread.Sleep(100); filenameEdit.SetFocus();
        }
        if (NativeMethods.GetForegroundWindow() != dialogHandle || !NativeMethods.IsOurWindow(dialogHandle)
            || !filenameEdit.TryGetCurrentPattern(ValuePattern.Pattern, out valuePattern)
            || !SamePath(((ValuePattern)valuePattern).Current.Value, filename))
            throw new InvalidOperationException("Enter fallback refused: current foreground picker or filename is unconfirmed.");
        File.AppendAllText(Path.Combine(output, "picker-interactions.txt"), $"{filename}\tFocused filename Enter\thwnd={dialogHandle}\tpid={Environment.ProcessId}\n");
        SendEnter(); WaitPickerClosed(dialogHandle);
    }

    private static bool PickerClosedSoon(nint dialog, int timeout)
    {
        var deadline = Stopwatch.StartNew();
        while (NativeMethods.IsWindow(dialog) && IsWindowVisible(dialog) && deadline.ElapsedMilliseconds < timeout) Thread.Sleep(100);
        return !NativeMethods.IsWindow(dialog) || !IsWindowVisible(dialog);
    }

    private static void WaitPickerClosed(nint dialog)
    {
        if (!PickerClosedSoon(dialog, 5000)) throw new TimeoutException("Actual native file picker did not close after Open/Enter.");
    }

    private static void CancelNativePicker(nint dialog)
    {
        if (dialog == 0) dialog = FindWindow(null, "选择本地程序");
        if (dialog == 0 || !NativeMethods.IsWindow(dialog) || !NativeMethods.IsOurWindow(dialog)) return;
        var popup = GetLastActivePopup(dialog);
        if (popup != 0 && popup != dialog && NativeMethods.IsOurWindow(popup)) PostMessage(popup, 0x10, 0, 0);
        var cancel = GetDlgItem(dialog, 2);
        if (cancel != 0 && NativeMethods.IsOurWindow(cancel)) PostMessage(cancel, 0xF5, 0, 0);
        else PostMessage(dialog, 0x10, 0, 0); // WM_CLOSE cancels only our known modal picker.
    }

    private static void PickerDiagnostic(AutomationElement dialog)
    {
        try
        {
            var controls = dialog.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>()
                .Where(element => element.Current.ControlType == ControlType.Edit || element.Current.ControlType == ControlType.Button || element.Current.ControlType == ControlType.SplitButton)
                .Select(element => $"{element.Current.ControlType.ProgrammaticName} id={element.Current.AutomationId} name={element.Current.Name} enabled={element.Current.IsEnabled} offscreen={element.Current.IsOffscreen}");
            File.WriteAllText(Path.Combine(output, "picker-diagnostic.txt"), string.Join("\n", controls));
        }
        catch (ElementNotAvailableException) { File.WriteAllText(Path.Combine(output, "picker-diagnostic.txt"), "Picker control tree disappeared while collecting diagnostics."); }
    }
    private static void Measure(HubApplication host, string name)
    {
        var processes = new List<object>(); long workingSet = 0; long privateBytes = 0;
        foreach (var info in host.Mini.Browser.CoreWebView2.Environment.GetProcessInfos())
        {
            try
            {
                using var process = Process.GetProcessById(info.ProcessId);
                workingSet += process.WorkingSet64; privateBytes += process.PrivateMemorySize64;
                processes.Add(new { info.ProcessId, kind = info.Kind.ToString(), workingSet = process.WorkingSet64, privateBytes = process.PrivateMemorySize64 });
            }
            catch (ArgumentException) { }
        }
        using var current = Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(output, name + "-memory.json"), JsonSerializer.Serialize(new { hostWorkingSet = current.WorkingSet64,
            hostPrivateBytes = current.PrivateMemorySize64, webViewWorkingSet = workingSet, webViewPrivateBytes = privateBytes, processes }, new JsonSerializerOptions(HubApplication.Json) { WriteIndented = true }));
    }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Key; [FieldOffset(0)] public MouseInput Mouse; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput { public ushort Vk, Scan; public uint Flags, Time; public nuint Extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseInput { public int X, Y; public uint MouseData, Flags, Time; public nuint Extra; }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, Input[] inputs, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindow(string? className, string? windowName);
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativeMethods.Point point);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint window, uint flags);
    private static void SendClick(int x, int y)
    {
        SetCursorPos(x, y);
        var inputs = new[] { new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 2 } } },
            new Input { Type = 0, Data = new InputUnion { Mouse = new MouseInput { Flags = 4 } } } };
        if (SendInput(2, inputs, Marshal.SizeOf<Input>()) != 2) throw new InvalidOperationException("Windows rejected test click.");
    }
    private static void SendEscape()
    {
        var inputs = new[] { new Input { Type = 1, Data = new InputUnion { Key = new KeyboardInput { Vk = 0x1B } } }, new Input { Type = 1, Data = new InputUnion { Key = new KeyboardInput { Vk = 0x1B, Flags = 2 } } } };
        if (SendInput(2, inputs, Marshal.SizeOf<Input>()) != 2) throw new InvalidOperationException("Windows rejected Esc input.");
    }
    private static void SendEnter()
    {
        var inputs = new[] { new Input { Type = 1, Data = new InputUnion { Key = new KeyboardInput { Vk = 0x0D } } }, new Input { Type = 1, Data = new InputUnion { Key = new KeyboardInput { Vk = 0x0D, Flags = 2 } } } };
        if (SendInput(2, inputs, Marshal.SizeOf<Input>()) != 2) throw new InvalidOperationException("Windows rejected picker Enter input.");
    }
}
