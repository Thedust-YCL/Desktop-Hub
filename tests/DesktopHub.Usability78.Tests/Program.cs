using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopHub.Core;
using DesktopHub.Windows;
using Microsoft.Web.WebView2.Core;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly List<object> checks = [];
    private static readonly List<Process> fixtures = [];
    private static string root = "", output = "";
    [STAThread] private static int Main(string[] args)
    {
        if (args.Length != 1 || !Guid.TryParseExact(args[0], "D", out var id) || id == Guid.Empty) return 2;
        root = Path.Combine(AppContext.BaseDirectory, "task78-owned-" + id); output = Path.Combine(root, "evidence"); Directory.CreateDirectory(output);
        using var instance = new SingleInstanceService("Task78.Native." + id, allowForegroundGrant: false);
        if (!instance.IsPrimaryInstance) return 3;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown }; HubApplication? host = null;
        app.Startup += async (_, _) => {
            try {
                var data = Path.Combine(root, "data");
                new SettingsStore(data).Save(new AppSettings { Theme = "dark", HoverEnabled = false, ReducedMotion = true, OnboardingCompleted = true });
                host = new HubApplication(data, smoke: true); await host.StartAsync(); await host.OpenWorkspaceAsync("library");
                var window = host.Workspace!; await Wait(window, "Boolean(window.DesktopHubDesktop?.getState().hostState)");
                var dpi = VisualTreeHelper.GetDpi(window);
                File.WriteAllText(Path.Combine(output, "environment.json"), JsonSerializer.Serialize(new { dpiX = dpi.DpiScaleX, dpiY = dpi.DpiScaleY, runtime = Environment.Version.ToString(), os = Environment.OSVersion.ToString(),
                    files = EvidenceFiles().Select(path => new { path, relativePath = Path.GetRelativePath(AppContext.BaseDirectory, path), sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))) }) }, Json));
                await Exercise(host, window);
                File.WriteAllText(Path.Combine(output, "summary.txt"), $"{checks.Count} Task7-8 integrated native checks passed.\n");
                CloseFixtures(); host.Exit();
            } catch (Exception ex) { File.WriteAllText(Path.Combine(output, "error.txt"), ex.ToString()); CloseFixtures(); app.Shutdown(1); }
        };
        app.DispatcherUnhandledException += (_, e) => { File.WriteAllText(Path.Combine(output, "dispatcher-error.txt"), e.Exception.ToString()); e.Handled = true; app.Shutdown(1); };
        app.Exit += (_, _) => { CloseFixtures(); host?.Dispose(); };
        return app.Run();
    }
    private static async Task Exercise(HubApplication host, HubWindow window)
    {
        var fileRoot = Path.Combine(root, "files"); Directory.CreateDirectory(fileRoot);
        var pixels = new byte[96 * 64 * 4]; for (var i = 0; i < pixels.Length; i += 4) { pixels[i] = 180; pixels[i+1] = 90; pixels[i+2] = 25; pixels[i+3] = 255; }
        var bitmap = BitmapSource.Create(96, 64, 96, 96, PixelFormats.Bgra32, null, pixels, 96 * 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(Path.Combine(fileRoot, "owned.png"))) encoder.Save(stream);
        File.WriteAllText(Path.Combine(fileRoot, "owned.txt"), "Owned unsupported preview fixture");
        WritePdf(Path.Combine(fileRoot, "owned.pdf"));
        var scan = await host.Resources.PreviewAsync(fileRoot, "custom", false, Id()); await host.Resources.ConfirmAsync(scan.Token);
        var image = host.Resources.Store.Snapshot().Resources.Single(r => r.Extension == ".png");
        var before = host.Activity.Snapshot().Count;
        var content = await RequestOk(window, "resources.previewContent", new { resourceId = image.Id, thumbnail = false, requestId = Id() });
        Check("Authorized image renders through real bridge without adding recent activity", content.GetProperty("width").GetInt32() == 96 && content.GetProperty("height").GetInt32() == 64 && host.Activity.Snapshot().Count == before);
        Check("Preview rejects frontend path fields", !(await Request(window, "resources.previewContent", new { resourceId = image.Id, thumbnail = false, requestId = Id(), path = image.CanonicalPath })).GetProperty("ok").GetBoolean());
        Check("Preview rejects unknown resource identity", !(await Request(window, "resources.previewContent", new { resourceId = Id(), thumbnail = true, requestId = Id() })).GetProperty("ok").GetBoolean());
        var pdf = host.Resources.Store.Snapshot().Resources.Single(r => r.Extension == ".pdf");
        var pdfContent = await RequestOk(window, "resources.previewContent", new { resourceId = pdf.Id, thumbnail = false, requestId = Id() });
        Check("Real WPF bridge renders the first of two PDF pages within the pixel ceiling", pdfContent.GetProperty("kind").GetString() == "pdf" && pdfContent.GetProperty("width").GetInt32() <= 1200 && pdfContent.GetProperty("height").GetInt32() <= 1200 && pdfContent.GetProperty("detail").GetString()!.Contains("2"));
        File.WriteAllBytes(Path.Combine(output,"pdf-first-page.png"),Convert.FromBase64String(pdfContent.GetProperty("png").GetString()!));
        await RequestOk(window, "resources.previewCancel", new { requestId = Id() });
        await Js(window, "Hub.navigate('library');DesktopHubResources.reload()");
        await Wait(window, $"DesktopHubResources.getState().result?.items.some(x=>x.resource.id==={J(image.Id)})");
        await Js(window, $"(()=>{{const row=document.querySelector('.nr-resource[data-resource-id=\"{image.Id}\"]');row.click();row.focus();row.dispatchEvent(new KeyboardEvent('keydown',{{key:' ',code:'Space',bubbles:true}}))}})()");
        await Wait(window, "Boolean(document.querySelector('.rp-image'))");
        await Matrix(window, "preview");
        Check("Native Escape closes preview before hiding Workspace", (await Js(window, "DesktopHubDesktop.handleEscape()")).GetBoolean() && window.IsVisible);
        await RequestOk(window, "settings.save", new { settings = new { performanceMode = "lightweight" } });
        Check("Real performance preference persists", host.Store.Current.PerformanceMode == "lightweight");

        var fixtureSource = Environment.GetEnvironmentVariable("DESKTOP_HUB_TEST_EXE") ?? throw new InvalidOperationException("Missing owned fixture");
        if (Path.GetFileName(fixtureSource) != "DesktopHub.LaunchFixture.exe") throw new InvalidOperationException("Unexpected fixture executable");
        var fixtureDir = Path.Combine(root, "app"); Directory.CreateDirectory(fixtureDir);
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(fixtureSource)!, "DesktopHub.LaunchFixture.*")) File.Copy(file, Path.Combine(fixtureDir, Path.GetFileName(file)));
        var fixturePath = Path.Combine(fixtureDir, "DesktopHub.LaunchFixture.exe");
        var registered = host.Launcher.RegisterApp(fixturePath); host.AppLibrary.Add(registered);
        var scene = new Scene { Id = Id(), Name = "隔离布局验收", Actions = [new SceneAction { Id = Id(), Type = "application", AppEntryId = registered.Id, Name = "自有窗口" }] };
        host.SceneLibrary.Save(scene); host.SettingsChanged();
        var process = StartFixture(fixturePath); await WaitProcess(process);
        var capture = await RequestOk(window, "scenes.layoutCapture", new { sceneId = scene.Id });
        var candidate = capture.GetProperty("candidates").EnumerateArray().First(c => c.GetProperty("appIds").EnumerateArray().Any(a => a.GetString() == registered.Id) && c.GetProperty("skipReason").GetString() == "");
        Check("Capture returns opaque tokens and no persisted handles", !candidate.TryGetProperty("handle", out _) && !candidate.TryGetProperty("processId", out _));
        await Js(window, $"DesktopHubWindowLayout.capture({J(scene.Id)})");
        await Wait(window, "Boolean(document.querySelector('.layout-candidate'))");
        await Matrix(window, "layout-capture");
        await Js(window, "Hub.closeModal()");
        await RequestOk(window, "scenes.layoutSave", new { sceneId = scene.Id, captureToken = capture.GetProperty("captureToken").GetString(), selections = new[] { new { candidateToken = candidate.GetProperty("candidateToken").GetString(), appEntryId = registered.Id, titleContains = "" } }, restoreAfterRun = false, waitTimeoutMs = 1000 });
        var restored = await RequestOk(window, "scenes.layoutRestore", new { sceneId = scene.Id });
        Check("Unique controlled native window restores", restored.GetProperty("entries")[0].GetProperty("status").GetString() == "restored");
        var second = StartFixture(fixturePath); await WaitProcess(second);
        var ambiguous = await RequestOk(window, "scenes.layoutRestore", new { sceneId = scene.Id });
        var entry = ambiguous.GetProperty("entries")[0];
        Check("Two actual matching windows require explicit choice", entry.GetProperty("status").GetString() == "needsChoice" && entry.GetProperty("candidates").GetArrayLength() == 2);
        await Js(window, $"DesktopHubWindowLayout.openResult({JsonSerializer.Serialize(ambiguous,Json)})");
        await Matrix(window, "layout-choice");
        await Js(window, "Hub.closeModal()");
        await RequestOk(window, "scenes.layoutChoose", new { restoreToken = ambiguous.GetProperty("restoreToken").GetString(), choices = new[] { new { entryToken = entry.GetProperty("entryToken").GetString(), candidateToken = entry.GetProperty("candidates")[0].GetProperty("candidateToken").GetString() } } });
        var automatic = host.Store.Current.Scenes.Single(s => s.Id == scene.Id);
        automatic.WindowLayout!.RestoreAfterRun = true; host.SceneLibrary.Save(automatic);
        var execution = host.Scenes.Start(scene.Id);
        var edited = host.Store.Current.Scenes.Single(s => s.Id == scene.Id);
        edited.WindowLayout = null; host.SceneLibrary.Save(edited);
        await host.Scenes.WaitForCompletionAsync(execution.Id).WaitAsync(TimeSpan.FromSeconds(10));
        foreach(var action in host.Scenes.Snapshot().Single(r => r.Id == execution.Id).Actions) if(action.ProcessId is { } pid) fixtures.Add(Process.GetProcessById(pid));
        await Wait(window, $"DesktopHubDesktop.getState().hostState.layoutResults?.some(x=>x.executionId==={J(execution.Id)})");
        var autoState = await RequestOk(window, "app.getState", new { });
        var autoResult = autoState.GetProperty("layoutResults").EnumerateArray().Single(x => x.GetProperty("executionId").GetString() == execution.Id).GetProperty("result");
        Check("Automatic restore uses frozen layout after live scene is edited", autoResult.GetProperty("entries").GetArrayLength() == 1 && autoResult.GetProperty("entries")[0].GetProperty("status").GetString() == "needsChoice");
        var autoEntry = autoResult.GetProperty("entries")[0];
        await RequestOk(window, "scenes.layoutChoose", new { restoreToken = autoResult.GetProperty("restoreToken").GetString(), choices = new[] { new { entryToken = autoEntry.GetProperty("entryToken").GetString(), candidateToken = autoEntry.GetProperty("candidates")[0].GetProperty("candidateToken").GetString() } } });
        var resolvedState = await RequestOk(window, "app.getState", new { });
        Check("Explicit automatic-layout choice updates the host result retained for rebuilding", resolvedState.GetProperty("layoutResults").EnumerateArray().Single(x => x.GetProperty("executionId").GetString() == execution.Id).GetProperty("result").GetProperty("entries")[0].GetProperty("status").GetString() == "restored");
        CloseFixtures();

        await host.OpenDockAsync();
        var originalDock = host.Dock!;
        await Wait(originalDock, "Boolean(DesktopHubDesktop.getState().hostState)");
        await CheckDockAddAlignment(originalDock, "Empty Dock add links use aligned equal-size plus icons");
        host.CloseDock();
        await host.OpenWorkspaceAsync("library");
        await Js(window, "Hub.navigate('library');DesktopHubResources.reload()");
        await Wait(window, "!DesktopHubResources.getState().loading");
        await Js(window, "Hub.showModal('待处理草稿','<label>名称<input id=\"owned-draft\" value=\"保留此输入\"></label>','')");
        host.CloseWorkspace(); await Task.Delay(61000);
        Check("A real hidden minute retains an unresolved modal and its draft", ReferenceEquals(host.Workspace, window) && !window.IsVisible);
        await host.OpenWorkspaceAsync();
        Check("Retained draft survives reopening", (await Js(window, "document.querySelector('#owned-draft')?.value==='保留此输入'")).GetBoolean());
        await Js(window, "Hub.closeModal();Hub.navigate('library');DesktopHubResources.reload()");
        await Wait(window, "!DesktopHubResources.getState().loading");
        var resourceService = host.Resources; var sceneService = host.Scenes; var executionCount = host.Scenes.Snapshot().Count;
        var historyBefore = (await RequestOk(window, "app.getState", new { })).GetProperty("layoutResults").EnumerateArray().Where(item => item.GetProperty("kind").GetString() == "manual").Select(item => item.GetProperty("resultId").GetString()).ToArray();
        host.CloseWorkspace(); await Task.Delay(62000);
        Check("Lightweight releases the actual hidden window after sixty seconds", host.Workspace is null && window.IsDisposed);
        Check("Release retains background services and Mini", ReferenceEquals(resourceService, host.Resources) && ReferenceEquals(sceneService, host.Scenes) && !host.Mini.IsDisposed);
        Check("Lightweight also releases the actual hidden Dock", host.Dock is null && originalDock.IsDisposed);
        await host.OpenDockAsync();
        var rebuiltDock = host.Dock!;
        await Wait(rebuiltDock, "Boolean(DesktopHubDesktop.getState().hostState)");
        await Task.Delay(250);
        Check("Rebuilt Dock stays visible without opening Workspace or replaying actions", !ReferenceEquals(rebuiltDock, originalDock) && rebuiltDock.IsVisible && host.Workspace is null && host.Scenes.Snapshot().Count == executionCount && host.Activity.Snapshot().Count == before);
        await CheckDockAddAlignment(rebuiltDock, "Rebuilt Dock keeps add-link icon and label alignment");
        using (var dockImage = File.Create(Path.Combine(output, "rebuilt-dock.png"))) await rebuiltDock.Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, dockImage);
        host.CloseDock();
        await host.OpenWorkspaceAsync(); var rebuilt = host.Workspace!;
        await Wait(rebuilt, "Hub.ui.page==='library' && Boolean(DesktopHubDesktop.getState().hostState)");
        Check("Rebuild restores navigation without replaying launches or writes", !ReferenceEquals(rebuilt, window) && host.Scenes.Snapshot().Count == executionCount && host.Activity.Snapshot().Count == before);
        var historyAfter = (await RequestOk(rebuilt, "app.getState", new { })).GetProperty("layoutResults").EnumerateArray().Where(item => item.GetProperty("kind").GetString() == "manual").Select(item => item.GetProperty("resultId").GetString()).ToArray();
        Check("Manual layout results keep stable identity across actual release and rebuilding", historyBefore.Length > 0 && historyBefore.SequenceEqual(historyAfter));
        Check("Host and assembly versions agree", (await RequestOk(rebuilt, "app.getState", new { })).GetProperty("version").GetString() == typeof(HubApplication).Assembly.GetName().Version?.ToString(3));
        await Matrix(rebuilt, "rebuilt-library");
    }
    private static IEnumerable<string> EvidenceFiles()
    {
        var rootFiles = Directory.GetFiles(AppContext.BaseDirectory).Where(path => Path.GetExtension(path) is ".dll" or ".exe" or ".json");
        return rootFiles.Concat(new[] { "UI", "ThirdParty", "licenses", "runtimes" }
            .Select(name => Path.Combine(AppContext.BaseDirectory, name)).Where(Directory.Exists)
            .SelectMany(path => Directory.GetFiles(path, "*", SearchOption.AllDirectories))).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
    }
    private static Process StartFixture(string path) { var p = Process.Start(new ProcessStartInfo(path) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(path)! })!; fixtures.Add(p); return p; }
    private static void WritePdf(string path)
    {
        const string red="1 0 0 rg 0 0 400 200 re f\n",blue="0 0 1 rg 0 0 400 200 re f\n";
        string[] objects=["<< /Type /Catalog /Pages 2 0 R >>","<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>","<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 200] /Resources << >> /Contents 4 0 R >>",$"<< /Length {red.Length} >>\nstream\n{red}endstream","<< /Type /Page /Parent 2 0 R /MediaBox [0 0 400 200] /Resources << >> /Contents 6 0 R >>",$"<< /Length {blue.Length} >>\nstream\n{blue}endstream"];
        using var bytes=new MemoryStream();void Text(string text)=>bytes.Write(System.Text.Encoding.ASCII.GetBytes(text));
        Text("%PDF-1.4\n");var offsets=new List<long>{0};for(var i=0;i<objects.Length;i++){offsets.Add(bytes.Position);Text($"{i+1} 0 obj\n{objects[i]}\nendobj\n");}
        var xref=bytes.Position;Text($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");foreach(var offset in offsets.Skip(1))Text($"{offset:D10} 00000 n \n");Text($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");File.WriteAllBytes(path,bytes.ToArray());
    }
    private static async Task WaitProcess(Process process) { for(var i=0;i<100;i++) { process.Refresh(); if(process.MainWindowHandle!=0)return; await Task.Delay(50); } throw new TimeoutException("Owned fixture window"); }
    private static async Task CheckDockAddAlignment(HubWindow window, string name)
    {
        Check(name, (await Js(window, "(()=>{const buttons=['.dock-add-app','.dock-add-scene'].map(s=>document.querySelector(s));if(buttons.some(b=>!b?.querySelector('svg')))return false;const geometry=buttons.map(b=>{const r=b.getBoundingClientRect(),i=b.querySelector('svg').getBoundingClientRect(),s=getComputedStyle(b);return {left:r.left,iconLeft:i.left,width:i.width,height:i.height,centerOffset:i.top+i.height/2-r.top-r.height/2,font:s.fontSize}});const[a,b]=geometry;return Math.abs(a.left-b.left)<.5&&Math.abs(a.iconLeft-b.iconLeft)<.5&&a.width===b.width&&a.height===b.height&&a.font===b.font&&Math.abs(a.centerOffset)<.5&&Math.abs(b.centerOffset)<.5})()")).GetBoolean());
    }
    private static void CloseFixtures() { foreach(var p in fixtures) { try { if(!p.HasExited && p.MainModule?.FileName is { } path && Path.GetFullPath(path).StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { p.CloseMainWindow(); if(!p.WaitForExit(1500))p.Kill(); } } catch (InvalidOperationException) {} finally {p.Dispose();} } fixtures.Clear(); }
    private static async Task Matrix(HubWindow w, string name) { foreach(var theme in new[]{"dark","light"}) { await RequestOk(w,"settings.save",new{settings=new{theme}}); foreach(var size in new[]{(640d,480d),(960d,640d)}) { w.Width=size.Item1;w.Height=size.Item2; await Task.Delay(180); Check($"{name} fits {theme} {size}",(await Js(w,"document.documentElement.scrollWidth<=innerWidth+1 && (()=>{const m=document.querySelector('.modal,.rp-panel');if(!m)return true;const r=m.getBoundingClientRect(),f=m.querySelector('.modal-footer,footer')?.getBoundingClientRect();return r.left>=0 && r.right<=innerWidth+1 && r.top>=0 && r.bottom<=innerHeight+1 && (!f||f.bottom<=innerHeight+1)})()")).GetBoolean()); using var file=File.Create(Path.Combine(output,$"{name}-{theme}-{size.Item1}x{size.Item2}.png")); await w.Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png,file); } } }
    private static async Task<JsonElement> Js(HubWindow w,string text) { using var doc=JsonDocument.Parse(await w.Browser.ExecuteScriptAsync(text));return doc.RootElement.Clone(); }
    private static async Task Wait(HubWindow w,string text) { for(var i=0;i<240;i++){if((await Js(w,text)).ValueKind==JsonValueKind.True)return;await Task.Delay(50);}throw new TimeoutException(text); }
    private static async Task<JsonElement> Request(HubWindow w,string type,object payload) { await Js(w,$"window.__reply78=null;DesktopHubNative.bridge.request({J(type)},{JsonSerializer.Serialize(payload,Json)},{{timeoutMs:30000}}).then(result=>window.__reply78={{ok:true,result}}).catch(error=>window.__reply78={{ok:false,error:{{code:error.code,message:error.message}}}})");await Wait(w,"Boolean(window.__reply78)");return await Js(w,"window.__reply78"); }
    private static async Task<JsonElement> RequestOk(HubWindow w,string type,object payload) { var reply=await Request(w,type,payload);if(!reply.GetProperty("ok").GetBoolean())throw new InvalidOperationException(type+": "+reply);return reply.GetProperty("result"); }
    private static void Check(string name,bool ok) {checks.Add(new{name,passed=ok});File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(checks,Json));if(!ok)throw new InvalidOperationException(name);}
    private static string Id()=>Guid.NewGuid().ToString("D"); private static string J(string text)=>JsonSerializer.Serialize(text);
}
