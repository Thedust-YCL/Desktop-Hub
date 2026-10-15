using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using DesktopHub.Core;
using DesktopHub.Windows;
using Microsoft.Web.WebView2.Core;

// Opt-in scoped native acceptance, with a unique data/profile/mutex and only owned inert programs.
internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly List<object> checks = [];
    private static readonly Dictionary<int, long> owned = [];
    private static string output = "";
    private static string root = "";
    private static string fixtureSource = "";

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length is < 1 or > 2 || !Guid.TryParseExact(args[0], "D", out var runId) || runId == Guid.Empty || args.Length == 2 && args[1] != "--restart") return 2;
        root = Path.Combine(AppContext.BaseDirectory, "task46-owned-" + runId.ToString("D"));
        output = Path.Combine(root, "evidence"); Directory.CreateDirectory(output);
        using var instance = new SingleInstanceService("Task46.Native." + runId, allowForegroundGrant: false);
        if (!instance.IsPrimaryInstance) return 3;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        HubApplication? host = null;
        app.Startup += async (_, _) =>
        {
            try
            {
                var data = Path.Combine(root, "data");
                var restart = args.Length == 2;
                if (!restart)
                {
                    fixtureSource = Environment.GetEnvironmentVariable("DESKTOP_HUB_TEST_EXE") ?? throw new InvalidOperationException("Controlled fixture path is required.");
                    if (Path.GetFileName(fixtureSource) != "DesktopHub.LaunchFixture.exe" || !File.Exists(fixtureSource)) throw new InvalidOperationException("Controlled fixture is unavailable.");
                    new SettingsStore(data).Save(new AppSettings { Theme = "dark", HoverEnabled = false, ReducedMotion = true, OnboardingCompleted = true });
                }
                host = new HubApplication(data, smoke: true);
                await host.StartAsync();
                await host.OpenWorkspaceAsync("home");
                var window = host.Workspace!;
                await WaitJs(window, "Boolean(window.DesktopHubDesktop?.getState().hostState)");
                var dpi = VisualTreeHelper.GetDpi(window);
                var identityFiles = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "UI", "desktop"))
                    .Append(typeof(HubApplication).Assembly.Location).Append(typeof(Scene).Assembly.Location);
                File.WriteAllText(Path.Combine(output, restart ? "restart-environment.json" : "environment.json"), JsonSerializer.Serialize(new
                {
                    os = Environment.OSVersion.ToString(), runtime = Environment.Version.ToString(), dpiX = dpi.DpiScaleX, dpiY = dpi.DpiScaleY,
                    hostVersion = typeof(HubApplication).Assembly.GetName().Version?.ToString(),
                    files = identityFiles.Select(path => new { path, sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }).ToArray()
                }, Json));
                if (restart) await Restart(host, window);
                else await FirstRun(host, window);
                File.WriteAllText(Path.Combine(output, restart ? "restart-summary.txt" : "phase1-summary.txt"), $"{checks.Count} scoped native checks passed.\n");
                CloseOwned();
                host.Exit();
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString());
                if (host is not null) foreach (var run in host.Scenes.Snapshot()) Own(run);
                CloseOwned(); app.Shutdown(1);
            }
        };
        app.DispatcherUnhandledException += (_, e) =>
        {
            File.WriteAllText(Path.Combine(output, "dispatcher-error.txt"), e.Exception.ToString());
            e.Handled = true; app.Shutdown(1);
        };
        app.Exit += (_, _) => { if (host is not null) foreach (var run in host.Scenes.Snapshot()) Own(run); CloseOwned(); host?.Dispose(); };
        return app.Run();
    }

    private static async Task FirstRun(HubApplication host, HubWindow window)
    {
        Check("Recent route is connected through the real WebView2 bridge", (await Request(window, "activity.query", new { })).GetProperty("ok").GetBoolean());
        var firstPath = CopyFixture("first"); var repairedPath = CopyFixture("repair");
        var thirdPath = CopyFixture("third");
        var app = host.Launcher.RegisterApp(firstPath); host.AppLibrary.Add(app);
        var repairApp = host.Launcher.RegisterApp(repairedPath); host.AppLibrary.Add(repairApp); host.SettingsChanged();
        await WaitJs(window, $"DesktopHubDesktop.getState().hostState.settings.appEntries.some(a=>a.id==={J(app.Id)})");
        await RequestOk(window, "apps.favorite", new { appId = app.Id, favorite = true });
        Check("Favorite is independent from Mini and Dock pins", host.Store.Current.AppEntries.Single(a => a.Id == app.Id).Favorite && host.Store.Current.LauncherPins.Count == 0);

        var first = new SceneAction { Id = Id(), Name = "已完成应用", Type = "application", AppEntryId = app.Id, DisplayOrder = 0 };
        var failed = new SceneAction { Id = Id(), Name = "修复后重试", Type = "application", AppEntryId = repairApp.Id, DisplayOrder = 1 };
        var blocked = new SceneAction { Id = Id(), Name = "受阻应用", Type = "application", Target = thirdPath, DisplayOrder = 2 };
        var scene = new Scene { Id = Id(), Name = "原生顺序与重试", EditorMode = "simple", ExecutionMode = "sequential", Actions = [first, failed, blocked] };
        await RequestOk(window, "scenes.save", new { scene });
        File.Delete(repairedPath);
        var established = await RequestOk(window, "scenes.run", new { sceneId = scene.Id });
        var runId = established.GetProperty("execution").GetProperty("id").GetString()!;
        await host.Scenes.WaitForCompletionAsync(runId).WaitAsync(TimeSpan.FromSeconds(25));
        var run = host.Scenes.Snapshot().Single(r => r.Id == runId); Own(run);
        Check("Native simple sequence completes first and blocks work after a missing target", run.Actions[0].SatisfiesDependencies && run.Actions[1].Status == "Failed" && run.Actions[2].Code == "dependency_failed");
        await WaitJs(window, $"DesktopHubScenes.getExecutions().some(r=>r.id==={J(runId)} && r.finishedAt)");
        await Js(window, $"Hub.actions['edit-scene']({{dataset:{{id:{J(scene.Id)}}}}});Hub.actions['native-scene-field']({{dataset:{{field:'name'}},value:'明确放弃的原生草稿'}});Hub.actions['scene-repair-app']({{dataset:{{id:{J(failed.Id)}}}}})");
        await WaitJs(window, "Boolean(document.querySelector('[data-action=\"scene-repair-discard\"]'))");
        await Js(window, "Hub.actions['scene-repair-discard']()");
        await WaitJs(window, "Boolean(document.querySelector('#app-name'))");
        Check("Explicit discard removes changed scene data before registered-app repair", (await Js(window, "!Hub.ui.sceneDraftDirty && (!Hub.ui.sceneDraft || Hub.ui.sceneDraft.name!=='明确放弃的原生草稿')")).GetBoolean());
        await Js(window, "Hub.closeModal();Hub.navigate('home')");
        await Js(window, "if(!DesktopHubScenes.getState().resultOpen) Hub.actions['scene-results']()");
        await WaitJs(window, "Boolean(document.querySelector('#scene-results-panel'))");
        Check("Results offer recovery for the actual failed prerequisite", (await Js(window, $"Boolean(document.querySelector('[data-action=\"scene-inspect-prerequisite\"][data-id=\"{blocked.Id}\"]'))")).GetBoolean());
        await Js(window, $"Hub.actions['scene-inspect-prerequisite']({{dataset:{{id:{J(blocked.Id)}}}}})");
        Check("Prerequisite recovery expands its failed result", (await Js(window, $"Boolean(document.querySelector('[data-scene-result-detail=\"{failed.Id}\"]').open)")).GetBoolean());
        await CaptureMatrix(window, "scene-results", true);
        var invalidRetry = await Request(window, "scenes.retry", new { executionId = runId, target = firstPath });
        Check("Real retry route rejects extra fields", !invalidRetry.GetProperty("ok").GetBoolean());
        var invalidScripts = await Request(window, "scenes.retryScripts", new { executionId = runId, actionIds = new[] { first.Id } });
        Check("Real script-repeat route rejects an application ID", !invalidScripts.GetProperty("ok").GetBoolean());
        await Js(window, "Hub.showModal('隔离草稿', '<label>草稿<input id=\"native-owned-draft\" value=\"等待结果期间保留\"></label>', '');document.querySelector('.workspace-content').scrollTop=35;window.__task46Scroll=document.querySelector('.workspace-content').scrollTop");
        CopyFixture("repair");
        await RequestOk(window, "scenes.retry", new { executionId = runId });
        var retry = host.Scenes.Snapshot().Last(r => r.Id != runId);
        await host.Scenes.WaitForCompletionAsync(retry.Id).WaitAsync(TimeSpan.FromSeconds(25));
        retry = host.Scenes.Snapshot().Single(r => r.Id == retry.Id); Own(retry);
        Check("Native retry reuses the successful process and launches failed/blocked targets", retry.Actions.All(a => a.SatisfiesDependencies) && retry.Actions[0].ProcessId == run.Actions[0].ProcessId);
        await WaitJs(window, $"DesktopHubScenes.getExecutions().some(r=>r.id==={J(retry.Id)} && r.finishedAt)");
        Check("Real execution updates retain the newer modal, draft and page scroll", (await Js(window, "Hub.ui.page==='home' && document.querySelector('#native-owned-draft')?.value==='等待结果期间保留' && document.querySelector('.workspace-content').scrollTop===window.__task46Scroll")).GetBoolean());
        await Js(window, "Hub.closeModal();Hub.actions['scene-results-close']()");
        await Js(window, $"Hub.actions['edit-scene']({{dataset:{{id:{J(scene.Id)}}}}})");
        await WaitJs(window, "Hub.ui.page==='editor' && Boolean(document.querySelector('[data-change=\"native-scene-mode\"]'))");
        Check("Native simple editor retains saved mode and real sequence", (await Js(window, "document.querySelector('[data-change=\"native-scene-mode\"]').value==='simple' && document.querySelector('[data-change=\"native-execution-mode\"]').value==='sequential'")).GetBoolean());
        await CaptureMatrix(window, "simple-editor");
        await Js(window, "Hub.actions['native-add-apps']()");
        await WaitJs(window, "Boolean(document.querySelector('.scene-app-picker'))");
        await CaptureMatrix(window, "scene-app-picker", modal: true);
        await Js(window, "Hub.closeModal();Hub.navigate('home')");

        var activity = await RequestOk(window, "activity.query", new { });
        Check("Only user-level scene execution is recorded, not internal launches", activity.GetProperty("items").GetArrayLength() == 1 && activity.GetProperty("items")[0].GetProperty("record").GetProperty("type").GetString() == "scene");
        var launched = await RequestOk(window, "apps.launch", new { appId = app.Id });
        if (launched.TryGetProperty("processId", out var launchedPid) && launchedPid.ValueKind == JsonValueKind.Number) Own(launchedPid.GetInt32());
        activity = await RequestOk(window, "activity.query", new { });
        Check("Independent successful app launch records the registered app ID", activity.GetProperty("items")[0].GetProperty("record").GetProperty("id").GetString() == app.Id);
        await RequestOk(window, "settings.save", new { settings = new { autoCloseDock = true } });
        await host.OpenDockAsync();
        await WaitJs(host.Dock!, "Boolean(window.DesktopHubDesktop?.getState().hostState)");
        var dockRetry = await Request(host.Dock!, "scenes.retryScripts", new { executionId = runId, actionIds = new[] { first.Id } });
        Check("Real script-repeat route rejects the Dock surface", !dockRetry.GetProperty("ok").GetBoolean() && dockRetry.GetProperty("error").GetProperty("code").GetString() == "WRONG_SURFACE");
        var dockLaunch = await RequestOk(host.Dock!, "apps.launch", new { appId = app.Id });
        if (dockLaunch.TryGetProperty("processId", out var dockPid) && dockPid.ValueKind == JsonValueKind.Number) Own(dockPid.GetInt32());
        Check("Real successful independent launch closes Dock when enabled", dockLaunch.GetProperty("success").GetBoolean() && !host.Dock!.IsVisible);
        var missingPath = CopyFixture("missing"); var missingApp = host.Launcher.RegisterApp(missingPath); host.AppLibrary.Add(missingApp); File.Delete(missingPath); host.SettingsChanged();
        await host.OpenDockAsync();
        var dockFailure = await RequestOk(host.Dock!, "apps.launch", new { appId = missingApp.Id });
        Check("A real failed launch retains Dock", !dockFailure.GetProperty("success").GetBoolean() && host.Dock!.IsVisible);
        await host.OpenWorkspaceAsync("favorites");
        await Js(window, $"Hub.actions['app-launch']({{dataset:{{id:{J(missingApp.Id)}}}}})");
        await WaitJs(window, "Boolean(document.querySelector('#unified-launch-failure'))");
        Check("Off-home launch failure is visible outside the content scroller", (await Js(window, "Hub.ui.page==='favorites' && document.querySelector('#unified-launch-failure').parentElement===document.querySelector('.workspace-content').parentElement")).GetBoolean());
        await Js(window, "Hub.actions['unified-launch-dismiss']?.();Hub.navigate('home')");

        var textDirectory = Path.Combine(root, "files"); Directory.CreateDirectory(textDirectory);
        File.WriteAllText(Path.Combine(textDirectory, "owned.txt"), "Owned native acceptance document.");
        var scan = await host.Resources.PreviewAsync(textDirectory, "custom", false, Id()); await host.Resources.ConfirmAsync(scan.Token);
        var resource = host.Resources.Store.Snapshot().Resources.Single(r => r.ResourceType == "file");
        var projectId = Id(); var webItemId = Id();
        var project = new { id = projectId, name = "原生验收项目", description = "应用、场景与文件的关联\n保留原始文件。", displayOrder = 0, pinnedToHome = true,
            items = new object[] { new { id = Id(), type = "app", entityId = app.Id, url = (string?)null, name = (string?)null },
                new { id = Id(), type = "scene", entityId = scene.Id, url = (string?)null, name = (string?)null },
                new { id = Id(), type = "file", entityId = resource.Id, url = (string?)null, name = (string?)null },
                new { id = webItemId, type = "web", entityId = (string?)null, url = "https://example.com/", name = "网页关联" } } };
        await RequestOk(window, "projects.save", new { project });
        var beforeRuns = host.Scenes.Snapshot().Count;
        var beforeActivity = (await RequestOk(window, "activity.query", new { })).GetProperty("items").GetRawText();
        await Js(window, $"DesktopHubProjects.open({J(projectId)})");
        await WaitJs(window, "Hub.ui.page === 'project'");
        Check("Opening a project navigates without running contents or recording reads", host.Scenes.Snapshot().Count == beforeRuns && (await RequestOk(window, "activity.query", new { })).GetProperty("items").GetRawText() == beforeActivity);
        var found = await RequestOk(window, "search.query", new { query = "原生验收项目", type = "project" });
        Check("Unified search returns the saved project's stable ID", found.EnumerateArray().Any(r => r.GetProperty("id").GetString() == projectId));
        await CaptureMatrix(window, "project");
        await Js(window, $"Hub.actions['ap-edit']({{dataset:{{id:{J(projectId)}}}}})");
        await WaitJs(window, "Boolean(document.querySelector('#ap-project-name'))");
        Check("Native project editor reads current saved metadata", (await Js(window, "document.querySelector('#ap-project-name').value==='原生验收项目' && document.querySelector('#ap-project-pin').checked")).GetBoolean());
        await CaptureMatrix(window, "project-editor", modal: true);
        await Js(window, "Hub.actions['ap-add-type']({dataset:{type:'scene'}});Hub.actions['ap-import-existing']()");
        await WaitJs(window, "Hub.ui.page==='editor' && Boolean(document.querySelector('[data-change=\"native-scene-mode\"]'))");
        Check("Project scene creation uses the native simple draft contract", (await Js(window, "(()=>{const d=Hub.ui.sceneDraft;return /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(d.id) && d.editorMode==='simple' && d.executionMode==='simultaneous' && Array.isArray(d.actions) && Array.isArray(d.dependencies)})()")).GetBoolean());
        var createdSceneId = (await Js(window, "Hub.ui.sceneDraft.id")).GetString()!;
        await Js(window, "Hub.actions['native-add-type']({dataset:{type:'application'}});Hub.actions['native-scene-save']()");
        await WaitJs(window, "Hub.ui.page==='scenes' && !Hub.ui.sceneDraftDirty");
        Check("Project-created scene saves through the real native scene route", host.Store.Current.Scenes.Any(item => item.Id == createdSceneId && item.EditorMode == "simple" && item.Actions.Count == 1));
        await Js(window, "Hub.actions['ap-resume-draft']();Hub.actions['ap-editor-back']()");
        await WaitJs(window, "Boolean(document.querySelector('#ap-project-name'))");
        Check("Returning from new scene creation preserves the project draft", (await Js(window, "document.querySelector('#ap-project-name').value==='原生验收项目' && document.querySelector('#ap-project-pin').checked && document.querySelector('.ap-editor').textContent.includes('4 / 100')")).GetBoolean());
        await Js(window, "Hub.closeModal();Hub.navigate('home')");
        await WaitJs(window, "Boolean(document.querySelector('.activity-home') && document.querySelector('.projects-home'))");
        Check("Home contains one recent section and one project section", (await Js(window, "document.querySelectorAll('.activity-home').length===1 && document.querySelectorAll('.projects-home').length===1")).GetBoolean());
        File.WriteAllText(Path.Combine(output, "restart-expected.json"), JsonSerializer.Serialize(new { appId = app.Id, projectId, sceneId = scene.Id, resourceId = resource.Id, filePath = resource.CanonicalPath }, Json));
        File.WriteAllText(Path.Combine(output, "phase1-results.json"), JsonSerializer.Serialize(checks, Json));
    }

    private static async Task Restart(HubApplication host, HubWindow window)
    {
        using var expectedFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "restart-expected.json"))); var expected = expectedFile.RootElement;
        Check("Restart retains independent favorite and original app ID", host.Store.Current.AppEntries.Single(a => a.Id == expected.GetProperty("appId").GetString()).Favorite);
        var projects = await RequestOk(window, "projects.query", new { projectId = expected.GetProperty("projectId").GetString() });
        Check("Restart retains ordered project associations", projects.GetProperty("project").GetProperty("items").GetArrayLength() == 4);
        var activity = await RequestOk(window, "activity.query", new { });
        Check("Restart retains successful recent entries", activity.GetProperty("items").GetArrayLength() == 2);
        Check("Restart retains resource ID and original file bytes", host.Resources.Store.Snapshot().Resources.Any(r => r.Id == expected.GetProperty("resourceId").GetString()) && File.ReadAllText(expected.GetProperty("filePath").GetString()!) == "Owned native acceptance document.");
        var noSnapshot = await Request(window, "scenes.retry", new { executionId = Id() });
        Check("Restart does not fabricate a retry snapshot", !noSnapshot.GetProperty("ok").GetBoolean());
        await Capture(window, "restart-home.png");
    }

    private static async Task CaptureMatrix(HubWindow window, string prefix, bool results = false, bool modal = false)
    {
        foreach (var theme in new[] { "dark", "light" })
        {
            await RequestOk(window, "settings.save", new { settings = new { theme } });
            foreach (var size in new[] { (960, 640), (640, 480) })
            {
                window.Width = size.Item1; window.Height = size.Item2;
                await Task.Delay(200);
                Check($"{prefix} content has no horizontal overflow: {theme} {size}", (await Js(window, "document.documentElement.scrollWidth<=innerWidth+1 && Array.from(document.querySelectorAll('.workspace-content')).every(n=>n.scrollWidth<=n.clientWidth+1)")).GetBoolean());
                if (results) Check($"Results remain reachable in the viewport: {theme} {size}", (await Js(window, "(()=>{const p=document.querySelector('#scene-results-panel'),r=p?.getBoundingClientRect();return r && r.top>=0 && r.left>=0 && r.right<=innerWidth+1 && r.bottom<=innerHeight+1 && p.querySelector('.scene-results').clientHeight>0})()")).GetBoolean());
                if (modal) Check($"Modal content and actions fit the viewport: {prefix} {theme} {size}", (await Js(window, "(()=>{const p=document.querySelector('.modal'),r=p?.getBoundingClientRect(),f=p?.querySelector('.modal-footer')?.getBoundingClientRect();return r && r.top>=0 && r.left>=0 && r.right<=innerWidth+1 && r.bottom<=innerHeight+1 && p.scrollWidth<=p.clientWidth+1 && (!f || f.bottom<=innerHeight+1)})()")).GetBoolean());
                await Capture(window, $"{prefix}-{theme}-{size.Item1}x{size.Item2}.png");
            }
        }
    }
    private static string CopyFixture(string name)
    {
        var destination = Path.Combine(root, name); Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(fixtureSource)!, "DesktopHub.LaunchFixture.*")) File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), true);
        return Path.Combine(destination, "DesktopHub.LaunchFixture.exe");
    }
    private static void Own(SceneExecution run)
    {
        foreach (var result in run.Actions.Where(a => a.ProcessId is not null))
        {
            Own(result.ProcessId!.Value);
        }
    }
    private static void Own(int pid)
    {
        try { using var process = Process.GetProcessById(pid); if (process.MainModule?.FileName is { } path && path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(path) == "DesktopHub.LaunchFixture.exe") owned[process.Id] = process.StartTime.ToUniversalTime().Ticks; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
    }
    private static void CloseOwned()
    {
        foreach (var (pid, started) in owned.ToArray())
        {
            try { using var process = Process.GetProcessById(pid); if (process.StartTime.ToUniversalTime().Ticks == started && process.MainModule?.FileName is { } path && path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { process.CloseMainWindow(); if (!process.WaitForExit(1500)) process.Kill(); } }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }
        owned.Clear();
    }
    private static async Task<JsonElement> Js(HubWindow window, string expression) { using var doc = JsonDocument.Parse(await window.Browser.ExecuteScriptAsync(expression)); return doc.RootElement.Clone(); }
    private static async Task WaitJs(HubWindow window, string expression) { for (var i = 0; i < 160; i++) { if ((await Js(window, expression)).ValueKind == JsonValueKind.True) return; await Task.Delay(50); } throw new TimeoutException(expression); }
    private static async Task<JsonElement> Request(HubWindow window, string type, object payload)
    {
        await Js(window, $"window.__task46Reply=null;DesktopHubNative.bridge.request({J(type)},{JsonSerializer.Serialize(payload, Json)}).then(result=>window.__task46Reply={{ok:true,result}}).catch(error=>window.__task46Reply={{ok:false,error:{{code:error.code,message:error.message}}}})");
        await WaitJs(window, "Boolean(window.__task46Reply)"); return await Js(window, "window.__task46Reply");
    }
    private static async Task<JsonElement> RequestOk(HubWindow window, string type, object payload) { var reply = await Request(window, type, payload); if (!reply.GetProperty("ok").GetBoolean()) throw new InvalidOperationException(type + ": " + reply); return reply.GetProperty("result"); }
    private static async Task Capture(HubWindow window, string name) { using var file = File.Create(Path.Combine(output, name)); await window.Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file); }
    private static void Check(string name, bool passed) { checks.Add(new { name, passed }); File.WriteAllText(Path.Combine(output, "results.json"), JsonSerializer.Serialize(checks, Json)); if (!passed) throw new InvalidOperationException(name); }
    private static string Id() => Guid.NewGuid().ToString("D");
    private static string J(string value) => JsonSerializer.Serialize(value);
}
