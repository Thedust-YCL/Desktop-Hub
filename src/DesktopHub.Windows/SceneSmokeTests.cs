using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopHub.Core;

namespace DesktopHub.Windows;

// Explicit, isolated acceptance; never loads or edits the user's actual settings.
internal static class SceneSmokeTests
{
    private static readonly List<object> checks = [];
    private static readonly Dictionary<int, long> owned = [];
    private static string output = "";
    public static void Prepare(string directory)
    {
        var expected = Path.Combine(AppContext.BaseDirectory, "m21-smoke-data");
        if (!Path.GetFullPath(directory).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid test data directory");
        new SettingsStore(directory).Save(new AppSettings { Theme = "dark", ReducedMotion = true, HoverEnabled = false });
    }
    public static async Task RunAsync(HubApplication host, bool restart)
    {
        output = Path.Combine(AppContext.BaseDirectory, "m21-smoke-evidence"); Directory.CreateDirectory(output);
        checks.Clear();
        try
        {
            await WaitJs(host.Mini, "Boolean(window.DesktopHubDesktop?.getState().hostState && window.DesktopHubScenes)");
            await host.OpenWorkspaceAsync("home"); var workspace = host.Workspace!;
            await WaitJs(workspace, "Boolean(window.DesktopHubDesktop?.getState().hostState && window.DesktopHubScenes)");
            if (restart)
            {
                using var phase = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "phase1-results.json")));
                checks.AddRange(phase.RootElement.EnumerateArray().Select(item => (object)item.Clone()));
                var expected = File.ReadAllText(Path.Combine(output, "expected-settings.json"));
                var actual = JsonSerializer.Serialize(host.Store.Current, SettingsStore.JsonOptions);
                // Window geometry can be saved during normal exit; compare only business records.
                using var previous = JsonDocument.Parse(expected); using var current = JsonDocument.Parse(actual);
                foreach (var field in new[] { "appEntries", "scenes", "launcherPins" }) Check("Second host reloads " + field,
                    previous.RootElement.GetProperty(field).GetRawText() == current.RootElement.GetProperty(field).GetRawText());
                Check("Scene references and missing references survive actual host restart", host.Store.Current.Scenes.Any(scene => scene.Actions.Any(action => action.ReferenceInvalid)));
                await Js(workspace, "Hub.navigate('scenes')");
                Check("Restarted Workspace renders native scene library", (await Js(workspace, "document.querySelectorAll('.scene-card').length")).GetInt32() == host.Store.Current.Scenes.Count);
                await Capture(workspace, "restart-scenes.png");
                Complete(host, "summary.txt"); return;
            }
            var before = host.Store.Current;
            Check("Independent app launcher and native scenes coexist on home", (await Js(workspace, "Boolean(document.querySelector('.application-launcher') && document.querySelector('.home-scenes'))")).GetBoolean());
            var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
            var targets = new[] { "winver.exe", "charmap.exe", "mstsc.exe", "msinfo32.exe", "colorcpl.exe" }.Select(name => Path.Combine(system, name)).ToArray();
            Check("Five different harmless Windows applications available", targets.All(File.Exists), targets);
            var apps = targets.Select(host.Launcher.RegisterApp).ToArray();
            foreach (var app in apps) { app.Icon = host.Icons.Extract(app); host.AppLibrary.Add(app); } host.SettingsChanged();
            await WaitJs(workspace, "document.querySelectorAll('.launcher-app').length === 5");

            // Use the same user-facing editor actions and fields; select existing IDs without retyping paths.
            await Js(workspace, "Hub.actions['new-scene']();");
            await Js(workspace, "document.querySelector('[data-change=native-scene-field][data-field=name]').value='五应用场景';document.querySelector('[data-change=native-scene-field][data-field=name]').dispatchEvent(new Event('input',{bubbles:true}));");
            foreach (var app in apps)
            {
                await Js(workspace, "Hub.actions['native-add-type']({dataset:{type:'application'}})");
                await Js(workspace, $"Hub.actions['native-action-app']({{value:{J(app.Id)}}});Hub.actions['native-action-field']({{dataset:{{field:'completionCondition'}},type:'select-one',value:'window'}});");
            }
            Check("Editor selects five AppEntry IDs without copying target paths", (await Js(workspace, "Hub.ui.sceneDraft.actions.length === 5 && Hub.ui.sceneDraft.actions.every(action=>action.appEntryId && !action.target)")).GetBoolean());
            var sceneId = (await Js(workspace, "Hub.ui.sceneDraft.id")).GetString()!;
            await Capture(workspace, "scene-editor-dark.png");
            await Js(workspace, "Hub.actions['native-scene-save']()");
            await WaitJs(workspace, "Hub.ui.page === 'scenes' && !Hub.ui.sceneDraftDirty");
            Check("Scene editor saves to shared native store", host.Store.Current.Scenes.Single(scene => scene.Id == sceneId).Actions.Count == 5);
            host.AppLibrary.SetPins("mini", [new() { ItemType = "app", ItemId = apps[0].Id }, new() { ItemType = "scene", ItemId = sceneId }]);
            host.AppLibrary.SetPin("dock", "scene", sceneId, true); host.SettingsChanged();
            host.CloseWorkspace();
            await WaitJs(host.Mini, "document.querySelectorAll('.launcher-pin').length === 2");
            await host.OpenWorkspaceAsync("scenes");
            var first = await ClickScene(host, workspace, sceneId);
            Check("Workspace really starts five application requests and observes their windows", first.Actions.All(action => action.Status is "Success" or "Skipped" && action.SatisfiesDependencies), first);
            foreach (var action in first.Actions.Where(action => action.Code == "window_appeared" && action.ProcessId is not null))
            {
                using var process = Process.GetProcessById(action.ProcessId!.Value);
                var expectedPath = targets[Array.FindIndex(apps, app => app.Id == host.Store.Current.Scenes.Single(scene => scene.Id == sceneId).Actions.Single(item => item.Id == action.ActionId).AppEntryId)];
                Check("Actual PID path matches scene application " + Path.GetFileName(expectedPath), Same(process.MainModule!.FileName, expectedPath), new { process.Id, target = process.MainModule.FileName });
                // Only own a process created by this test, never an already-running user's program.
                if (action.Status == "Success") owned[process.Id] = process.StartTime.ToUniversalTime().Ticks;
            }
            await Capture(workspace, "workspace-results.png");
            await host.OpenDockAsync(); await WaitJs(host.Dock!, "Boolean(window.DesktopHubScenes)");
            var second = await ClickScene(host, host.Dock!, sceneId);
            Check("Dock repeat skips all five running apps by target path", second.Actions.All(action => action.Status == "Skipped" && action.Code == "window_appeared"), second);
            Check("Execution completion keeps Dock visible with Workspace safely hidden", host.Dock!.IsVisible && !workspace.IsVisible);
            await Capture(host.Dock!, "dock-results.png");
            host.CloseDock();
            var third = await ClickScene(host, host.Mini, sceneId);
            Check("Mini mixed entry triggers its own new execution", third.Id != second.Id && third.Actions.All(action => action.Status == "Skipped"), third);
            await Capture(host.Mini, "mini-mixed-results.png");
            var miniStatus = (await Js(host.Mini, $"window.DesktopHubScenes.getExecutions().find(run=>run.id==={J(third.Id)}).status")).GetString();
            await host.OpenDockAsync();
            await WaitJs(host.Dock!, $"window.DesktopHubScenes.getExecutions().some(run=>run.id==={J(third.Id)} && run.finishedAt)");
            var dockStatus = (await Js(host.Dock!, $"window.DesktopHubScenes.getExecutions().find(run=>run.id==={J(third.Id)}).status")).GetString();
            await host.OpenWorkspaceAsync("scenes");
            await WaitJs(workspace, $"window.DesktopHubScenes.getExecutions().some(run=>run.id==={J(third.Id)} && run.finishedAt)");
            Check("All three surfaces share the same final execution state when resumed", miniStatus == third.Status && dockStatus == third.Status
                && (await Js(workspace, $"window.DesktopHubScenes.getExecutions().find(run=>run.id==={J(third.Id)}).status")).GetString() == third.Status);
            foreach (var pid in owned.Keys.ToArray()) CloseOwned(pid);
            host.CloseDock();

            // Controlled fixture: arguments, updated references, path identity, delays, and bounded windows.
            var source = Environment.GetEnvironmentVariable("DESKTOP_HUB_TEST_EXE") ?? throw new InvalidOperationException("Set fixture path");
            var fixtureDirectory = Path.Combine(host.DataDirectory, "fixture-" + Id()); Directory.CreateDirectory(fixtureDirectory);
            foreach (var fixtureFile in Directory.GetFiles(Path.GetDirectoryName(source)!, "DesktopHub.LaunchFixture.*")) File.Copy(fixtureFile, Path.Combine(fixtureDirectory, Path.GetFileName(fixtureFile)), true);
            var fixture = Path.Combine(fixtureDirectory, "DesktopHub.LaunchFixture.exe");
            var fixtureApp = host.Launcher.RegisterApp(fixture); fixtureApp.Icon = host.Icons.Extract(fixtureApp); host.AppLibrary.Add(fixtureApp);
            var fixtureAction = Action("最新应用配置", "application", "") with { AppEntryId = fixtureApp.Id, CompletionCondition = "window" };
            var processNext = Action("等待进程后延迟", "application", "") with { AppEntryId = fixtureApp.Id, DelayMs = 250 };
            var ordered = Scene("顺序与更新", fixtureAction, processNext); ordered.Dependencies = [new() { ActionId = processNext.Id, DependsOnActionId = fixtureAction.Id, Condition = "process" }];
            host.SceneLibrary.Save(ordered); host.AppLibrary.Update(fixtureApp.Id, "已更新应用", "--updated \"two words\"", fixtureDirectory, false, true); host.SettingsChanged();
            var orderedResult = await ClickScene(host, workspace, ordered.Id, false);
            var fixturePid = orderedResult.Actions[0].ProcessId!.Value; Own(fixturePid);
            var markerPath = Path.Combine(fixtureDirectory, $"fixture-started-{fixturePid}.json");
            for (var wait = 0; wait < 100 && !File.Exists(markerPath); wait++) await Task.Delay(50);
            using (var marker = JsonDocument.Parse(File.ReadAllText(markerPath))) Check("Referenced scene reads edited AppEntry arguments and work directory", marker.RootElement.GetProperty("arguments")[0].GetString() == "--updated" && marker.RootElement.GetProperty("arguments")[1].GetString() == "two words" && Same(marker.RootElement.GetProperty("workingDirectory").GetString()!, fixtureDirectory));
            Check("Sequential process dependency accepts already-running app and honors delay", orderedResult.Actions[1].Status == "Skipped" && orderedResult.Actions[1].FinishedAt - orderedResult.Actions[0].FinishedAt >= TimeSpan.FromMilliseconds(230), orderedResult);

            var twinDirectory = Path.Combine(host.DataDirectory, "same-name-fixture-" + Id()); Directory.CreateDirectory(twinDirectory);
            foreach (var fixtureFile in Directory.GetFiles(Path.GetDirectoryName(source)!, "DesktopHub.LaunchFixture.*")) File.Copy(fixtureFile, Path.Combine(twinDirectory, Path.GetFileName(fixtureFile)), true);
            var twinPath = Path.Combine(twinDirectory, "DesktopHub.LaunchFixture.exe");
            var identityRunner = new WindowsSceneActionRunner(host.Launcher);
            var twin = await identityRunner.LaunchAsync(Action("同名不同路径", "application", twinPath), host.Store.Current, default, _ => { });
            Check("Same process name at another path is not mistaken for running target", twin.Success && !twin.Skipped && twin.ProcessId != fixturePid, twin); Own(twin.ProcessId!.Value);
            var extra = await identityRunner.LaunchAsync(fixtureAction with { RepeatPolicy = "always" }, host.Store.Current, default, _ => { });
            Check("Explicit repeat policy can create another instance", extra.Success && !extra.Skipped && extra.ProcessId != fixturePid, extra); Own(extra.ProcessId!.Value);
            for (var wait = 0; wait < 100 && WindowsSceneActionRunner.FindProcesses(fixture).Ids.Count < 2; wait++) await Task.Delay(50);
            var multiple = await identityRunner.LaunchAsync(fixtureAction, host.Store.Current, default, _ => { });
            Check("Default app rule skips reliably matched multiple instances", multiple.Success && multiple.Skipped && multiple.Message.Contains("2 个实例"), multiple);

            var timeoutAction = fixtureAction with { Id = Id(), Name = "等待不存在的窗口", WindowTitle = "DesktopHub window never exists " + Id() };
            var blocked = processNext with { Id = Id(), Name = "依赖失败后跳过" };
            var independent = fixtureAction with { Id = Id(), Name = "独立动作继续", CompletionCondition = "process" };
            var timeoutScene = Scene("窗口超时与依赖", timeoutAction, blocked, independent); timeoutScene.Dependencies = [new() { ActionId = blocked.Id, DependsOnActionId = timeoutAction.Id }]; host.SceneLibrary.Save(timeoutScene); host.SettingsChanged();
            var watch = Stopwatch.StartNew(); var pending = host.Scenes.Start(timeoutScene.Id);
            await Js(workspace, "window.__m21Ticks=0;window.__m21Interval=setInterval(()=>window.__m21Ticks++,25)");
            await host.Scenes.WaitForCompletionAsync(pending.Id); var timeout = host.Scenes.Snapshot().Single(run => run.Id == pending.Id);
            await Js(workspace, "clearInterval(window.__m21Interval)");
            Check("Real window wait fails after the default ten seconds", watch.ElapsedMilliseconds >= 9900 && watch.ElapsedMilliseconds < 14000 && timeout.Actions[0].Status == "Failed" && timeout.Actions[0].Code == "wait_window_timeout", timeout);
            Check("Failed prerequisite skips dependent and independent action continues", timeout.Actions[1].Status == "Skipped" && timeout.Actions[2].SatisfiesDependencies && timeout.Status == "PartialSuccess");
            Check("UI responds during asynchronous scene wait", (await Js(workspace, "window.__m21Ticks")).GetInt32() > 150);
            await Capture(workspace, "window-timeout.png");

            var script = Path.Combine(host.DataDirectory, "controlled.cmd"); var scriptMarker = Path.Combine(host.DataDirectory, "script-marker.txt");
            File.WriteAllText(scriptMarker, "");
            // ASCII fixture uses its selected working directory; cmd's OEM script encoding stays native.
            File.WriteAllText(script, "@echo off\r\necho marker>>script-marker.txt\r\nexit /b 0\r\n");
            var scriptAction = Action("受控脚本", "script", script);
            var scriptScene = Scene("脚本重复执行", scriptAction); host.SceneLibrary.Save(scriptScene); host.SettingsChanged();
            var runA = host.Scenes.Start(scriptScene.Id); var runB = host.Scenes.Start(scriptScene.Id);
            await Task.WhenAll(host.Scenes.WaitForCompletionAsync(runA.Id), host.Scenes.WaitForCompletionAsync(runB.Id));
            Check("Concurrent clicks create separate runs; each script runs exactly once", File.ReadAllLines(scriptMarker).Length == 2 && host.Scenes.Snapshot().Where(run => run.Id == runA.Id || run.Id == runB.Id).All(run => run.Actions[0].Code == "script_completed"));
            await ClickScene(host, workspace, scriptScene.Id, false); Check("New scene execution reruns the script", File.ReadAllLines(scriptMarker).Length == 3);
            var injected = scriptAction with { ArgumentList = ["& echo injected"] };
            var rejected = await new WindowsSceneActionRunner(host.Launcher).LaunchAsync(injected, host.Store.Current, default, _ => { });
            Check("Batch command control syntax is rejected before execution", !rejected.Success && rejected.Code == "unsafe_batch_arguments" && File.ReadAllLines(scriptMarker).Length == 3);
            var badScript = Path.Combine(host.DataDirectory, "exit-error.cmd"); File.WriteAllText(badScript, "@echo off\r\nexit /b 7\r\n");
            var errorOutcome = await new WindowsSceneActionRunner(host.Launcher).LaunchAsync(scriptAction with { Target = badScript }, host.Store.Current, default, _ => { });
            Check("Script exit errors report actual Windows process result", !errorOutcome.Success && errorOutcome.Code == "script_exit_error" && errorOutcome.Message.Contains('7'));
            var slowScript = Path.Combine(host.DataDirectory, "slow.cmd"); File.WriteAllText(slowScript, "@echo off\r\nping -n 3 127.0.0.1 >nul\r\nexit /b 0\r\n");
            var slowRunner = new WindowsSceneActionRunner(host.Launcher);
            var slow = await slowRunner.LaunchAsync(scriptAction with { Target = slowScript, TimeoutMs = 300 }, host.Store.Current, default, _ => { });
            Check("Interactive or slow script produces a finite timeout", !slow.Success && slow.Code == "script_timeout");
            var stillRunning = await slowRunner.LaunchAsync(scriptAction with { Target = slowScript, TimeoutMs = 300 }, host.Store.Current, default, _ => { });
            Check("Timed-out script is not duplicated while its previous process is still running", !stillRunning.Success && stillRunning.Code == "previous_script_running", stillRunning);

            var web = Action("网页", "web", "http://127.0.0.1:9/desktop-hub-scene-test");
            var file = Path.Combine(host.DataDirectory, "association-test.txt"); File.WriteAllText(file, "Desktop Hub harmless association test.");
            var shellScene = Scene("系统关联打开", web, Action("文件", "file", file), Action("文件夹", "folder", host.DataDirectory)); host.SceneLibrary.Save(shellScene); host.SettingsChanged();
            var opened = await ClickScene(host, workspace, shellScene.Id, false);
            Check("Real Windows shell receives web/file/folder open requests", opened.Actions.All(action => action.Status == "Success" && action.Code == "submitted"), opened);
            var missing = await new WindowsSceneActionRunner(host.Launcher).LaunchAsync(Action("不存在的文件", "file", Path.Combine(host.DataDirectory, "absent.txt")), host.Store.Current, default, _ => { });
            Check("Missing file reports a real error", !missing.Success && missing.Code == "target_missing");

            await Js(workspace, $"Hub.actions['app-remove']({{dataset:{{id:{J(fixtureApp.Id)}}}}})");
            Check("Removing referenced app lists affected scenes", (await Js(workspace, "document.querySelector('.modal-body').textContent.includes('顺序与更新') && document.querySelector('.modal-body').textContent.includes('窗口超时与依赖')")).GetBoolean());
            await Capture(workspace, "referenced-app-removal.png");
            var unconfirmed = await Request(workspace, "apps.remove", new { appId = fixtureApp.Id });
            Check("Native bridge requires explicit confirmation of affected scenes", !unconfirmed.GetProperty("ok").GetBoolean() && unconfirmed.GetProperty("error").GetProperty("code").GetString() == "REFERENCES_REQUIRE_CONFIRMATION");
            var removed = await Request(workspace, "apps.remove", new { appId = fixtureApp.Id, confirmReferences = true }); Check("Confirmed removal succeeds", removed.GetProperty("ok").GetBoolean());
            Check("Referenced scene survives removal with repairable invalid action", host.Store.Current.Scenes.Single(scene => scene.Id == ordered.Id).Actions.All(action => action.ReferenceInvalid) && File.Exists(fixture));
            await Js(workspace, "Hub.closeModal();Hub.navigate('scenes')"); await Capture(workspace, "scene-center-dark.png");
            await Request(workspace, "settings.save", new { settings = new { theme = "light" } }); await WaitJs(workspace, "document.documentElement.dataset.theme === 'light'");
            await Js(workspace, $"Hub.actions['edit-scene']({{dataset:{{id:{J(ordered.Id)}}}}})"); await Capture(workspace, "scene-editor-light.png");
            Check("Local scene diagnostics exist", File.Exists(Path.Combine(host.DataDirectory, "scene-executions.jsonl")));
            File.WriteAllText(Path.Combine(output, "expected-settings.json"), JsonSerializer.Serialize(host.Store.Current, SettingsStore.JsonOptions));
            File.WriteAllText(Path.Combine(output, "phase1-results.json"), JsonSerializer.Serialize(checks, HubApplication.Json));
            Complete(host, "phase1-summary.txt");
        }
        catch (Exception ex) { File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); Application.Current.Shutdown(1); }
        finally { foreach (var pid in owned.Keys.ToArray()) CloseOwned(pid); }
    }
    private static void Complete(HubApplication host, string file) { File.WriteAllText(Path.Combine(output, file), $"{checks.Count} real Windows scene checks passed.\n"); host.Exit(); }
    private static void Check(string name, bool passed, object? detail = null)
    {
        checks.Add(new { name, passed, detail }); File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions(HubApplication.Json) { WriteIndented = true }));
        if (!passed) throw new InvalidOperationException(name);
    }
    private static string Id() => Guid.NewGuid().ToString("D");
    private static string J(string text) => JsonSerializer.Serialize(text);
    private static bool Same(string a, string b) => Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    private static SceneAction Action(string name, string type, string target) => new() { Id = Id(), Name = name, Type = type, Target = target };
    private static Scene Scene(string name, params SceneAction[] actions) => new() { Id = Id(), Name = name, Actions = actions.Select((action, order) => action with { DisplayOrder = order }).ToList() };
    private static void Own(int pid) { using var process = Process.GetProcessById(pid); owned[pid] = process.StartTime.ToUniversalTime().Ticks; }
    private static void CloseOwned(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (owned.TryGetValue(pid, out var time) && process.StartTime.ToUniversalTime().Ticks == time)
            {
                try { process.WaitForInputIdle(3000); } catch (InvalidOperationException) { }
                process.CloseMainWindow();
                if (!process.WaitForExit(1000)) { SmokeTests.TryCloseOwnedWindow(pid); process.WaitForExit(3000); }
                if (!process.HasExited && process.MainModule?.FileName is { } path &&
                    path.StartsWith(Path.Combine(AppContext.BaseDirectory, "m21-smoke-data"), StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(path) == "DesktopHub.LaunchFixture.exe") { process.Kill(); process.WaitForExit(3000); }
            }
        }
        catch (ArgumentException) { } catch (InvalidOperationException) { } catch (System.ComponentModel.Win32Exception) { } finally { owned.Remove(pid); }
    }
    private static async Task<SceneExecution> ClickScene(HubApplication host, HubWindow window, string id, bool click = true)
    {
        host.SettingsChanged(); var previous = host.Scenes.Snapshot().Select(run => run.Id).ToHashSet();
        if (click) await Js(window, $"document.querySelector('[data-action=run-scene][data-id=\"{id}\"]').click()");
        else await Js(window, $"Hub.actions['run-scene']({{dataset:{{id:{J(id)}}}}})");
        SceneExecution? run = null;
        for (var i = 0; i < 100; i++) { run = host.Scenes.Snapshot().FirstOrDefault(item => !previous.Contains(item.Id)); if (run is not null) break; await Task.Delay(50); }
        if (run is null) throw new Exception("No scene execution created");
        await host.Scenes.WaitForCompletionAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(40));
        return host.Scenes.Snapshot().Single(item => item.Id == run.Id);
    }
    private static async Task<JsonElement> Js(HubWindow window, string expression) { using var document = JsonDocument.Parse(await window.Browser.ExecuteScriptAsync(expression)); return document.RootElement.Clone(); }
    private static async Task WaitJs(HubWindow window, string expression) { for (var i = 0; i < 200; i++) { if ((await Js(window, expression)).ValueKind == JsonValueKind.True) return; await Task.Delay(50); } throw new TimeoutException(expression); }
    private static async Task<JsonElement> Request(HubWindow window, string type, object payload)
    {
        await Js(window, $"window.__sceneReply=null;DesktopHubNative.bridge.request({J(type)},{JsonSerializer.Serialize(payload, HubApplication.Json)}).then(result=>window.__sceneReply={{ok:true,result}}).catch(error=>window.__sceneReply={{ok:false,error:{{code:error.code,message:error.message}}}})");
        await WaitJs(window, "Boolean(window.__sceneReply)"); return await Js(window, "window.__sceneReply");
    }
    private static async Task Capture(HubWindow window, string filename)
    {
        await Task.Delay(200);
        if (window.Surface == "workspace") { using var stream = File.Create(Path.Combine(output, filename)); await window.Browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png, stream); return; }
        var dpi = VisualTreeHelper.GetDpi(window);
        var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * dpi.DpiScaleX), (int)(window.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bitmap.Render(window); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(Path.Combine(output, filename)); encoder.Save(file);
    }
}
