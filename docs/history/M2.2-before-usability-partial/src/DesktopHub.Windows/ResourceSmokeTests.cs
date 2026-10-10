using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using DesktopHub.Core;

namespace DesktopHub.Windows;

internal static class ResourceSmokeTests
{
    private static readonly List<object> checks = [];
    private static string output = "";
    public static void Prepare(string directory)
    {
        if (!Path.GetFullPath(directory).Equals(Path.Combine(AppContext.BaseDirectory,"m22-smoke-data"),StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Unsafe test directory");
        new SettingsStore(directory).Save(new AppSettings { Theme="dark", ReducedMotion=true, HoverEnabled=false });
        new ResourceStore(directory).Update(data => { data.Sources.Clear(); data.Resources.Clear(); data.ResourceSources.Clear(); data.ResourceTags.Clear(); data.Tags.Clear(); data.Suggestions.Clear(); });
    }
    public static async Task RunAsync(HubApplication host, bool restart)
    {
        output=Path.Combine(AppContext.BaseDirectory,"m22-smoke-evidence");Directory.CreateDirectory(output);checks.Clear();
        try
        {
            await host.OpenWorkspaceAsync("library");var window=host.Workspace!;
            await WaitJs(window,"Boolean(window.DesktopHubResources && window.DesktopHubDesktop.getState().hostState)");
            await Js(window,"Hub.navigate('library')");await WaitJs(window,"!DesktopHubResources.getState().loading");
            if(restart)
            {
                using var phase=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"phase1-results.json")));checks.AddRange(phase.RootElement.EnumerateArray().Select(x=>(object)x.Clone()));
                using var expected=JsonDocument.Parse(File.ReadAllText(Path.Combine(output,"expected-resources.json")));var actual=host.Resources.Store.Snapshot();
                var expectedIds=expected.RootElement.GetProperty("resources").EnumerateArray().Select(x=>x.GetProperty("id").GetString()).Order().ToArray();
                Check("Independent second Windows host reloads stable IDs",expectedIds.SequenceEqual(actual.Resources.Select(x=>x.Id).Order()));
                Check("Second host retains tags, favorites, sources and relationships",actual.Tags.Count>0&&actual.Resources.Any(r=>r.Favorite)&&actual.ResourceTags.Count>0&&actual.Sources.Count>0&&actual.ResourceSources.Count>0);
                await WaitJs(window,"DesktopHubResources.getState().result.total>0 && document.querySelectorAll('[data-resource-id]').length>0");
                Check("Restarted real Workspace renders indexed resources",(await Js(window,"document.querySelectorAll('[data-resource-id]').length")).GetInt32()>0);
                await Capture(window,"restart-library.png");Complete(host,"summary.txt");return;
            }
            var discovery=await Request(window,"resources.discover",new{});
            Check("Native bridge discovers actual desktop without importing or monitoring",discovery.GetProperty("ok").GetBoolean()&&Same(discovery.GetProperty("result").GetProperty("desktop").GetString()!,ResourceScanner.DesktopPath())&&host.Resources.WatcherCount==0&&host.Resources.Query(new()).Total==0);
            Check("Empty native library does not show prototype paths",!(await Js(window,"document.querySelector('#page-content').textContent.includes('Demo')")).GetBoolean());
            var deniedPublic=await Request(window,"resources.preview",new{kind="publicDesktop",recursive=false,operationId=Guid.NewGuid().ToString("N")});
            Check("Native boundary refuses public desktop scanning without explicit consent",!deniedPublic.GetProperty("ok").GetBoolean()&&host.Resources.WatcherCount==0);
            var folder=Path.Combine(AppContext.BaseDirectory,"m22-owned-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var sub=Path.Combine(folder,"设计资料");Directory.CreateDirectory(sub);
            var original=Path.Combine(folder,"课程笔记.txt");File.WriteAllText(original,"Desktop Hub harmless owned note.");File.WriteAllText(Path.Combine(sub,"内部.txt"),"Owned temporary browse content.");File.WriteAllText(Path.Combine(folder,"image.png"),"Not an image; contents must never be decoded.");
            var hash=Hash(original);
            var picker=Task.Run(()=>SelectFolder(folder));
            await Js(window,"document.querySelector('[data-action=nr-source-add][data-kind=custom]').click()");
            await picker.WaitAsync(TimeSpan.FromSeconds(45));
            await WaitJs(window,"Boolean(DesktopHubResources.getState().preview)");
            Check("Actual Windows folder picker yields read-only preview",Same((await Js(window,"DesktopHubResources.getState().preview.path")).GetString()!,folder)&&host.Resources.WatcherCount==0&&host.Resources.Query(new()).Total==0);
            Check("Preview UI reports actual direct child counts and confirmation",(await Js(window,"DesktopHubResources.getState().preview.files===2 && DesktopHubResources.getState().preview.folders===1 && !!document.querySelector('[data-action=nr-confirm]')")).GetBoolean());
            await Capture(window,"source-preview.png");
            await Js(window,"document.querySelector('[data-action=nr-confirm]').click()");
            await WaitJs(window,"!Hub.ui.modal && DesktopHubResources.getState().result.total===3");
            Check("Confirmed import starts watcher and only indexes direct children",host.Resources.WatcherCount==1&&host.Resources.Query(new()).Total==3&&!host.Resources.Store.Snapshot().Resources.Any(r=>r.DisplayName=="内部.txt"));
            Check("Original note bytes unchanged by indexing",hash==Hash(original));
            var note=host.Resources.Store.Snapshot().Resources.Single(r=>r.CanonicalPath==original);
            await Js(window,$"document.querySelector('[data-action=nr-favorite][data-id=\"{note.Id}\"]').click()");await Wait(()=>host.Resources.Store.Snapshot().Resources.Single(r=>r.Id==note.Id).Favorite);
            await WaitJs(window,"!DesktopHubResources.getState().busy");
            await Js(window,"Hub.actions['nr-new-tag']();document.querySelector('#nr-tag-name').value='UI 验收';document.querySelector('#nr-tag-kind').value='theme';document.querySelector('[data-action=nr-save-tag]').click()");await Wait(()=>host.Resources.Store.Snapshot().Tags.Any(t=>t.Name=="UI 验收"));
            var tag=host.Resources.Store.Snapshot().Tags.Single(t=>t.Name=="UI 验收");await WaitJs(window,"!Hub.ui.modal && !DesktopHubResources.getState().loading");
            await Js(window,$"Hub.actions['nr-detail']({{dataset:{{id:{J(note.Id)}}}}})");
            await Js(window,$"var checkbox=document.querySelector('[data-change=nr-bind][data-id=\"{tag.Id}\"]');checkbox.checked=true;checkbox.dispatchEvent(new Event('change',{{bubbles:true}}))");await Wait(()=>host.Resources.Store.Snapshot().ResourceTags.Contains(new(note.Id,tag.Id)));
            await Js(window,"Hub.closeModal()");
            await Js(window,"Hub.navigate('favorites')");await WaitJs(window,"!DesktopHubResources.getState().loading && DesktopHubResources.getState().result.total===1");
            Check("Native favorites page filters the actual indexed favorite",(await Js(window,$"DesktopHubResources.getState().result.items[0].resource.id==={J(note.Id)}")).GetBoolean());
            await Js(window,"Hub.navigate('search')");await WaitJs(window,"!DesktopHubResources.getState().loading");
            await Js(window,"var search=document.querySelector('[data-change=nr-search]');search.value='课程笔记';search.dispatchEvent(new Event('input',{bubbles:true}))");await WaitJs(window,"DesktopHubResources.getState().query==='课程笔记' && !DesktopHubResources.getState().loading && DesktopHubResources.getState().result.total===1");
            Check("Actual Workspace filename search selects the real resource",(await Js(window,$"DesktopHubResources.getState().result.items[0].resource.id==={J(note.Id)}")).GetBoolean());
            await Js(window,"var search=document.querySelector('[data-change=nr-search]');search.value='UI 验收';search.dispatchEvent(new Event('input',{bubbles:true}))");await WaitJs(window,"DesktopHubResources.getState().query==='UI 验收' && !DesktopHubResources.getState().loading && DesktopHubResources.getState().result.total===1");
            await Task.Delay(250);await WaitJs(window,"!DesktopHubResources.getState().loading && DesktopHubResources.getState().result.total===1");
            Check("Actual Workspace formal tag search filters native relationships",(await Js(window,$"DesktopHubResources.getState().result.items[0].resource.id==={J(note.Id)}")).GetBoolean());
            await Js(window,"Hub.navigate('library')");await WaitJs(window,"!DesktopHubResources.getState().loading && DesktopHubResources.getState().result.total===3");
            Check("Actual Workspace favorite and multi-tag UI persist native metadata",host.Resources.Store.Snapshot().Resources.Single(r=>r.Id==note.Id).Favorite&&host.Resources.Store.Snapshot().ResourceTags.Contains(new(note.Id,tag.Id)));
            await Js(window,"Hub.actions['nr-view']({dataset:{view:'grid'}})");await Capture(window,"library-grid-dark.png");
            Check("Grid uses real records and type icons, never content thumbnails",(await Js(window,"document.querySelectorAll('.nr-card').length===3 && document.querySelectorAll('.nr-card img').length===0")).GetBoolean());
            await Js(window,"Hub.actions['nr-view']({dataset:{view:'tags'}})");await Capture(window,"library-tags-dark.png");Check("Tag view renders actual formal tags",(await Js(window,"document.querySelector('.nr-tag-navigation').textContent.includes('UI 验收')")).GetBoolean());
            await Js(window,"Hub.actions['nr-view']({dataset:{view:'list'}})");await Capture(window,"library-list-dark.png");
            var suggestion=host.Resources.Store.Snapshot().Suggestions.First(x=>x.ResourceId==note.Id&&x.Status=="Pending");await Js(window,$"Hub.actions['nr-accept']({{dataset:{{id:{J(suggestion.Id)}}}}})");await Wait(()=>host.Resources.Store.Snapshot().Suggestions.Single(x=>x.Id==suggestion.Id).Status=="Accepted");
            Check("Suggestion becomes formal relation only after real UI acceptance",host.Resources.Store.Snapshot().ResourceTags.Count>=2);
            await WaitJs(window,"!DesktopHubResources.getState().busy");
            var period=host.Resources.Store.Snapshot().Tags.Single(t=>t.TagType=="period");
            await Js(window,$"Hub.actions['nr-filter-tag']({{dataset:{{id:{J(tag.Id)}}}}});Hub.actions['nr-filter-tag']({{dataset:{{id:{J(period.Id)}}}}})");await WaitJs(window,"DesktopHubResources.getState().tagIds.length===2 && !DesktopHubResources.getState().loading && DesktopHubResources.getState().result.total===1");
            Check("Actual multi-tag filter requires both formal tags",(await Js(window,$"DesktopHubResources.getState().result.items[0].resource.id==={J(note.Id)}")).GetBoolean());await Js(window,"Hub.actions['nr-clear']()");await WaitJs(window,"!DesktopHubResources.getState().loading && DesktopHubResources.getState().result.total===3");
            var directory=host.Resources.Store.Snapshot().Resources.Single(r=>r.CanonicalPath==sub);
            await Js(window,$"Hub.actions['nr-browse']({{dataset:{{id:{J(directory.Id)}}}}})");await WaitJs(window,"Hub.ui.page==='folder'");Check("Temporary native folder view reads child without indexing",(await Js(window,"DesktopHubResources.getState().folder.items.length")).GetInt32()==1&&host.Resources.Query(new()).Total==3);await Capture(window,"temporary-folder.png");
            await Js(window,"document.querySelector('[data-action=nr-folder-add]').click()");await Wait(()=>host.Resources.Query(new()).Total==4);Check("Explicit add from folder browser indexes one selected item",host.Resources.Store.Snapshot().ResourceSources.Any(x=>x.Explicit));
            await Js(window,"Hub.navigate('library')");await WaitJs(window,"!DesktopHubResources.getState().loading");
            var invalid=await Request(window,"resources.open",new{resourceId=note.Id,mode="open",path=@"C:\Windows\System32\cmd.exe"});Check("Native boundary rejects arbitrary extra path",!invalid.GetProperty("ok").GetBoolean());
            var traversal=await Request(window,"resources.browse",new{token=(await Js(window,"DesktopHubResources.getState().folder.token")).GetString(),name="..\\.."});Check("Native boundary rejects folder path traversal",!traversal.GetProperty("ok").GetBoolean());
            var shellNote=host.Resources.Store.Snapshot().Resources.Single(r=>r.DisplayName=="内部.txt");
            var shell=await Request(window,"resources.open",new{resourceId=shellNote.Id,mode="open"});Check("File open returns actual Windows Shell submission",shell.GetProperty("ok").GetBoolean()&&shell.GetProperty("result").GetProperty("submitted").GetBoolean());
            var reveal=await Request(window,"resources.open",new{resourceId=shellNote.Id,mode="reveal"});Check("Explorer reveal submits a verified indexed path",reveal.GetProperty("ok").GetBoolean());
            var renamed=Path.Combine(folder,"课程笔记-更名.txt");File.Move(original,renamed);await Wait(()=>host.Resources.Store.Snapshot().Resources.Any(r=>r.Id==note.Id&&r.CanonicalPath==renamed));
            await WaitJs(window,"document.querySelector('#page-content').textContent.includes('课程笔记-更名.txt')");Check("Real file watcher updates visible Workspace and preserves tags/favorite",host.Resources.Store.Snapshot().Resources.Single(r=>r.Id==note.Id).Favorite&&host.Resources.Store.Snapshot().ResourceTags.Contains(new(note.Id,tag.Id))&&hash==Hash(renamed));
            File.Delete(renamed);await Wait(()=>host.Resources.Store.Snapshot().Resources.Single(r=>r.Id==note.Id).Status=="Missing");await Js(window,"Hub.navigate('missing')");await WaitJs(window,"document.querySelector('#page-content').textContent.includes('缺失')");await Capture(window,"missing-library.png");
            var missingOpen=await Request(window,"resources.open",new{resourceId=note.Id,mode="open"});Check("Missing original retains index and reports actual opening error",!missingOpen.GetProperty("ok").GetBoolean()&&host.Resources.Store.Snapshot().ResourceTags.Contains(new(note.Id,tag.Id)));
            var replacement=Path.Combine(folder,"修复目标.txt");File.WriteAllText(replacement,"Owned replacement note.");await host.Resources.RefreshAsync();await Request(window,"resources.beginRepair",new{resourceId=note.Id});
            var repairPicker=Task.Run(()=>SelectFolder(replacement,"重新关联原资源（不移动文件）"));var repairRequest=Request(window,"resources.repair",new{resourceId=note.Id});await repairPicker.WaitAsync(TimeSpan.FromSeconds(45));var repaired=await repairRequest;
            Check("Actual Windows repair picker accepts an existing target",repaired.GetProperty("ok").GetBoolean());await WaitJs(window,"!DesktopHubResources.getState().loading");
            Check("Native manual repair keeps original identity, tags and favorites",host.Resources.Store.Snapshot().Resources.Single(r=>r.Id==note.Id).Status=="Active"&&host.Resources.Store.Snapshot().Resources.Single(r=>r.Id==note.Id).Favorite&&host.Resources.Store.Snapshot().ResourceTags.Contains(new(note.Id,tag.Id)));
            // Clean a separate missing test index through the real two-confirmation flow.
            var clean=host.Resources.Store.Snapshot().Resources.Single(r=>r.DisplayName=="image.png");File.Delete(clean.CanonicalPath);await host.Resources.RefreshAsync();await Js(window,"Hub.navigate('missing')");await WaitJs(window,"!DesktopHubResources.getState().loading");
            await Js(window,$"Hub.actions['nr-select']({{dataset:{{id:{J(clean.Id)}}},checked:true}});Hub.actions['nr-clean']()");await Capture(window,"cleanup-confirmation.png");
            Check("Cleanup UI explicitly distinguishes index cleanup from physical delete",(await Js(window,"document.querySelector('.modal-body').textContent.includes('不会删除实际文件')")).GetBoolean());
            await Js(window,"document.querySelector('[data-action=nr-clean-confirm]').click()");await Wait(()=>!host.Resources.Store.Snapshot().Resources.Any(r=>r.Id==clean.Id));Check("Confirmed cleanup leaves remaining files untouched",File.Exists(replacement)&&File.Exists(Path.Combine(sub,"内部.txt")));
            await Js(window,"Hub.navigate('library')");await WaitJs(window,"!DesktopHubResources.getState().loading");await Request(window,"settings.save",new{settings=new{theme="light"}});await WaitJs(window,"document.documentElement.dataset.theme==='light'");await Capture(window,"library-list-light.png");
            Check("Light and dark native Workspace keep readable library controls",(await Js(window,"document.querySelectorAll('[data-resource-id]').length>0 && document.querySelector('.nr-search input').getAttribute('aria-label').length>0")).GetBoolean());
            await Js(window,"Hub.navigate('home')");Check("Original application launcher and scene shortcuts coexist after resource operations",(await Js(window,"Boolean(document.querySelector('.application-launcher') && document.querySelector('.home-scenes'))")).GetBoolean());
            var large=Path.Combine(AppContext.BaseDirectory,"m22-owned-large-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(large);
            await Task.Run(()=>{for(var i=0;i<1800;i++)File.WriteAllText(Path.Combine(large,i+".txt"),"Owned metadata performance fixture.");});
            await Js(window,"window.__resourceTicks=0;window.__resourceInterval=setInterval(()=>window.__resourceTicks++,5)");
            var timing=Stopwatch.StartNew();var largePreview=await host.Resources.PreviewAsync(large,"custom",false,Guid.NewGuid().ToString("N"));var ticks=(await Js(window,"window.__resourceTicks")).GetInt32();await Js(window,"clearInterval(window.__resourceInterval)");host.Resources.Cancel(largePreview.Token);
            Check("Actual WebView2 UI continues ticking during 1800-file asynchronous preview",largePreview.Files==1800&&ticks>0);
            File.WriteAllText(Path.Combine(output,"large-preview-performance.json"),JsonSerializer.Serialize(new{files=largePreview.Files,elapsedMs=timing.ElapsedMilliseconds,webViewTicks=ticks,workingSetBytes=Process.GetCurrentProcess().WorkingSet64},HubApplication.Json));
            File.WriteAllText(Path.Combine(output,"expected-resources.json"),JsonSerializer.Serialize(host.Resources.Store.Snapshot(),ResourceStore.Json));
            File.WriteAllText(Path.Combine(output,"phase1-results.json"),JsonSerializer.Serialize(checks,HubApplication.Json));Complete(host,"phase1-summary.txt");
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(output,"error.txt"),ex.ToString());if(host.Workspace is not null)try{await Capture(host.Workspace,"failure.png");}catch{}Application.Current.Shutdown(1);}
    }
    private static void SelectFolder(string folder, string title = "选择要建立虚拟索引的目录")
    {
        AutomationElement? dialog=null;var wait=Stopwatch.StartNew();
        var condition=new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty,Environment.ProcessId),new PropertyCondition(AutomationElement.NameProperty,title));
        while(wait.ElapsedMilliseconds<30000)
        {
            var foreground=NativeMethods.GetForegroundWindow();
            if(foreground!=0&&NativeMethods.IsOurWindow(foreground)){var candidate=AutomationElement.FromHandle(foreground);if(candidate.Current.ClassName=="#32770")dialog=candidate;}
            dialog ??= AutomationElement.RootElement.FindFirst(TreeScope.Descendants,condition);if(dialog is not null)break;Thread.Sleep(100);
        }
        if(dialog is null)
        {
            File.WriteAllText(Path.Combine(output,"folder-picker-windows.txt"),string.Join("\n",AutomationElement.RootElement.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ProcessIdProperty,Environment.ProcessId)).Cast<AutomationElement>().Select(x=>$"{x.Current.Name} {x.Current.ClassName}")));
            throw new TimeoutException("Native folder picker did not appear");
        }
        var handle=(nint)dialog.Current.NativeWindowHandle;
        try
        {
            var edits=dialog.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Edit)).Cast<AutomationElement>().ToArray();
            var input=edits.FirstOrDefault(x=>x.Current.AutomationId=="1148"||x.Current.Name.Contains("文件夹:")||x.Current.Name.Contains("Folder:"))??throw new Exception("Folder name edit not found");
            ((ValuePattern)input.GetCurrentPattern(ValuePattern.Pattern)).SetValue(folder);
            for(var attempt=0;attempt<4;attempt++)
            {
                var button=dialog.FindAll(TreeScope.Descendants,new PropertyCondition(AutomationElement.ControlTypeProperty,ControlType.Button)).Cast<AutomationElement>().First(x=>x.Current.AutomationId=="1");
                ((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();Thread.Sleep(350);if(!NativeMethods.IsWindow(handle))return;
            }
            throw new TimeoutException("Folder picker did not close after selecting owned directory");
        }
        catch
        {
            File.WriteAllText(Path.Combine(output,"folder-picker-controls.txt"),string.Join("\n",dialog.FindAll(TreeScope.Descendants,System.Windows.Automation.Condition.TrueCondition).Cast<AutomationElement>().Select(x=>$"{x.Current.ControlType.ProgrammaticName} {x.Current.AutomationId} {x.Current.Name}")));
            PostMessage(handle,0x0010,0,0);throw;
        }
    }
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool PostMessage(nint window,uint message,nint wParam,nint lParam);
    private static bool Same(string a,string b)=>Path.GetFullPath(a).Equals(Path.GetFullPath(b),StringComparison.OrdinalIgnoreCase);
    private static string J(string text)=>JsonSerializer.Serialize(text);
    private static async Task<JsonElement> Js(HubWindow window,string expression){using var json=JsonDocument.Parse(await window.Browser.ExecuteScriptAsync(expression));return json.RootElement.Clone();}
    private static async Task WaitJs(HubWindow window,string expression){for(var i=0;i<300;i++){if((await Js(window,expression)).ValueKind==JsonValueKind.True)return;await Task.Delay(50);}throw new TimeoutException(expression);}
    private static async Task Wait(Func<bool> predicate){for(var i=0;i<200;i++){if(predicate())return;await Task.Delay(80);}throw new TimeoutException("Native resource state");}
    private static async Task<JsonElement> Request(HubWindow window,string type,object payload)
    {await Js(window,$"window.__resourceReply=null;DesktopHubNative.bridge.request({J(type)},{JsonSerializer.Serialize(payload,HubApplication.Json)}).then(result=>window.__resourceReply={{ok:true,result}}).catch(error=>window.__resourceReply={{ok:false,error:{{code:error.code,message:error.message}}}})");await WaitJs(window,"Boolean(window.__resourceReply)");return await Js(window,"window.__resourceReply");}
    private static async Task Capture(HubWindow window,string file){await Task.Delay(200);await Js(window,"document.querySelector('#toast-root')?.replaceChildren()");using var stream=File.Create(Path.Combine(output,file));await window.Browser.CoreWebView2.CapturePreviewAsync(Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,stream);}
    private static void Check(string name,bool passed){checks.Add(new{name,passed});File.WriteAllText(Path.Combine(output,"results.json"),JsonSerializer.Serialize(checks,new JsonSerializerOptions(HubApplication.Json){WriteIndented=true}));if(!passed)throw new Exception(name);}
    private static void Complete(HubApplication host,string name){File.WriteAllText(Path.Combine(output,name),$"{checks.Count} real Windows resource checks passed.\n");host.Exit();}
}
