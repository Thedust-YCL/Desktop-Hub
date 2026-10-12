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
        new ResourceStore(directory).Update(data => { data.AutoTagByModificationTime=null; data.Sources.Clear(); data.Resources.Clear(); data.ResourceSources.Clear(); data.ResourceTags.Clear(); data.Tags.Clear(); data.TagCategories = new ResourceLibrary().TagCategories; data.Suggestions.Clear(); });
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
                Check("Independent restart retains onboarding completion", host.Store.Current.OnboardingCompleted && !(await Js(window, "DesktopHubOnboarding.getState().active")).GetBoolean());
                await WaitJs(window,"DesktopHubResources.getState().result.total>0 && document.querySelectorAll('[data-resource-id]').length>0");
                Check("Restarted real Workspace renders indexed resources",(await Js(window,"document.querySelectorAll('[data-resource-id]').length")).GetInt32()>0);
                Check("Restarted Workspace restores its independent opacity", host.Store.Current.WorkspaceOpacity == .65 && window.Opacity == .65);
                await Capture(window,"restart-library.png");Complete(host,"summary.txt");return;
            }
            await VerifyWorkspaceHeaderAsync(host, window);
            await VerifyOnboardingAsync(host, window);
            var discovery=await Request(window,"resources.discover",new{});
            Check("Native bridge discovers actual desktop without importing or monitoring",discovery.GetProperty("ok").GetBoolean()&&Same(discovery.GetProperty("result").GetProperty("desktop").GetString()!,ResourceScanner.DesktopPath())&&host.Resources.WatcherCount==0&&host.Resources.Query(new()).Total==0);
            Check("Empty native library does not show prototype paths",!(await Js(window,"document.querySelector('#page-content').textContent.includes('Demo')")).GetBoolean());
            var deniedPublic=await Request(window,"resources.preview",new{kind="publicDesktop",recursive=false,operationId=Guid.NewGuid().ToString("N")});
            Check("Native boundary refuses public desktop scanning without explicit consent",!deniedPublic.GetProperty("ok").GetBoolean()&&host.Resources.WatcherCount==0);
            var folder=Path.Combine(AppContext.BaseDirectory,"m22-owned-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);var sub=Path.Combine(folder,"设计资料");Directory.CreateDirectory(sub);
            var original=Path.Combine(folder,"课程笔记.txt");File.WriteAllText(original,"Desktop Hub harmless owned note.");File.WriteAllText(Path.Combine(sub,"内部.txt"),"Owned temporary browse content.");File.WriteAllText(Path.Combine(folder,"image.png"),"Not an image; contents must never be decoded.");
            var hash=Hash(original);
            await Js(window,"Hub.openOnboarding();Hub.actions['guide-next']()");
            var picker=Task.Run(()=>SelectFolder(folder));
            await Js(window,"document.querySelector('[data-action=guide-import][data-kind=custom]').click()");
            await picker.WaitAsync(TimeSpan.FromSeconds(45));
            await WaitJs(window,"Boolean(DesktopHubResources.getState().preview)");
            Check("Actual Windows folder picker yields read-only preview",Same((await Js(window,"DesktopHubResources.getState().preview.path")).GetString()!,folder)&&host.Resources.WatcherCount==0&&host.Resources.Query(new()).Total==0);
            Check("Preview UI reports actual direct child counts and confirmation",(await Js(window,"DesktopHubResources.getState().preview.files===2 && DesktopHubResources.getState().preview.folders===1 && !!document.querySelector('[data-action=nr-confirm]')")).GetBoolean());
            Check("First import offers an explicit modification-time tag choice",(await Js(window,"document.querySelector('.nr-time-tag-choice').textContent.includes('是否按修改时间自动添加所有标签') && document.querySelector('input[name=nr-time-tags]:checked').value==='no'")).GetBoolean());
            await Js(window,"document.querySelector('input[name=nr-time-tags][value=yes]').click()");
            await Capture(window,"source-preview.png");
            await Js(window,"document.querySelector('[data-action=nr-confirm]').click()");
            await WaitJs(window,"!DesktopHubOnboarding.getState().suspended && DesktopHubOnboarding.getState().step===1 && DesktopHubResources.getState().result.total===3");
            Check("Guided native import returns to the same tutorial after confirmation", (await Js(window, "!!document.querySelector('[data-guide-step=\"1\"]')")).GetBoolean());
            await Js(window, "document.querySelector('[data-action=guide-skip]').click()");
            await WaitJs(window, "!DesktopHubOnboarding.getState().active");
            await Js(window, "Hub.navigate('library')");
            await WaitJs(window, "!DesktopHubResources.getState().loading && DesktopHubResources.getState().result.total===3");
            Check("Confirmed import starts watcher and only indexes direct children",host.Resources.WatcherCount==1&&host.Resources.Query(new()).Total==3&&!host.Resources.Store.Snapshot().Resources.Any(r=>r.DisplayName=="内部.txt"));
            Check("Original note bytes unchanged by indexing",hash==Hash(original));
            Check("Opt-in adds a formal modification-month tag to every imported item",host.Resources.Store.Snapshot().AutoTagByModificationTime==true&&host.Resources.Store.Snapshot().ResourceTags.Count==3);
            Check("Resource rows share aligned tag/status columns and three ordered accessible actions",(await Js(window,"(() => {const rows=[...document.querySelectorAll('.nr-row')];return ['.nr-tag-cell','.nr-status','.nr-row-actions'].every(selector=>rows.every(row=>Math.abs(row.querySelector(selector).getBoundingClientRect().left-rows[0].querySelector(selector).getBoundingClientRect().left)<1)) && rows.every(row=>[...row.querySelector('.nr-row-actions').children].map(b=>b.dataset.action).join(',')==='nr-favorite,nr-detail,nr-open' && [...row.querySelector('.nr-row-actions').children].every(b=>b.getBoundingClientRect().height>=40))})()")).GetBoolean());
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
            await Js(window,"Hub.actions['nr-view']({dataset:{view:'tags'}});Hub.actions['nr-category']({dataset:{category:'theme'}})");await Capture(window,"library-tags-dark.png");Check("Tag view expands formal tags within their category",(await Js(window,"document.querySelector('.nr-filter-tags').textContent.includes('UI 验收')")).GetBoolean());
            await Js(window,"Hub.actions['nr-view']({dataset:{view:'list'}})");await Capture(window,"library-list-dark.png");
            var suggestion=host.Resources.Store.Snapshot().Suggestions.First(x=>x.ResourceId==note.Id&&x.Status=="Pending");await Js(window,$"Hub.actions['nr-accept']({{dataset:{{id:{J(suggestion.Id)}}}}})");await Wait(()=>host.Resources.Store.Snapshot().Suggestions.Single(x=>x.Id==suggestion.Id).Status=="Accepted");
            Check("Suggestion becomes formal relation only after real UI acceptance",host.Resources.Store.Snapshot().ResourceTags.Count>=2);
            await WaitJs(window,"!DesktopHubResources.getState().busy");
            var period=host.Resources.Store.Snapshot().Tags.Single(t=>t.TagType=="period");
            await Js(window,$"Hub.actions['nr-filter-tag']({{dataset:{{id:{J(tag.Id)}}}}});Hub.actions['nr-filter-tag']({{dataset:{{id:{J(period.Id)}}}}})");await WaitJs(window,"DesktopHubResources.getState().tagIds.length===2 && !DesktopHubResources.getState().loading && DesktopHubResources.getState().result.total===1");
            Check("Actual multi-tag filter requires both formal tags",(await Js(window,$"DesktopHubResources.getState().result.items[0].resource.id==={J(note.Id)}")).GetBoolean());await Js(window,"Hub.actions['nr-clear']()");await WaitJs(window,"!DesktopHubResources.getState().loading && DesktopHubResources.getState().result.total===3");
            var directory=host.Resources.Store.Snapshot().Resources.Single(r=>r.CanonicalPath==sub);
            await Js(window,$"document.querySelector('.nr-row-hit[data-id=\"{directory.Id}\"]').click()");await WaitJs(window,"Hub.ui.page==='folder'");Check("Whole-row folder browse reads child without indexing",(await Js(window,"DesktopHubResources.getState().folder.items.length")).GetInt32()==1&&host.Resources.Query(new()).Total==3);await Capture(window,"temporary-folder.png");
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
            // Enough owned rows to expose scroll regressions in the real WebView2 viewport.
            await Task.Run(()=>{for(var i=0;i<84;i++)File.WriteAllText(Path.Combine(folder,$"滚动验收-{i:D2}.txt"),"Owned scroll fixture.");});
            await host.Resources.RefreshAsync();await WaitJs(window,"!DesktopHubResources.getState().loading && document.querySelectorAll('.nr-row').length===60");
            await Request(window,"settings.save",new{settings=new{reducedMotion=false}});await WaitJs(window,"document.documentElement.dataset.motion==='normal'");
            await Js(window,"document.querySelector('#nr-sources').open=true;Hub.render();document.querySelector('.workspace-content').scrollTop=720;window.__scrollBefore=document.querySelector('.workspace-content').scrollTop;window.__scrollResource=document.querySelectorAll('.nr-row')[12].dataset.resourceId;var favorite=document.querySelectorAll('.nr-row')[12].querySelector('[data-action=nr-favorite]');favorite.focus({preventScroll:true});favorite.click()");
            await WaitJs(window,"!DesktopHubResources.getState().busy && !DesktopHubResources.getState().loading");await Task.Delay(250);
            File.WriteAllText(Path.Combine(output,"scroll-metrics.json"),(await Js(window,"({before:window.__scrollBefore,after:document.querySelector('.workspace-content').scrollTop,sourceOpen:document.querySelector('#nr-sources').open,expectedId:window.__scrollResource,focusedId:document.activeElement.dataset.id,focusedAction:document.activeElement.dataset.action})")).GetRawText());
            Check("Favorite mutation and watcher refresh preserve scroll, expanded source and focus",(await Js(window,"window.__scrollBefore>0 && Math.abs(document.querySelector('.workspace-content').scrollTop-window.__scrollBefore)<1 && document.querySelector('#nr-sources').open && document.activeElement.dataset.id===window.__scrollResource && document.activeElement.dataset.action==='nr-favorite'")).GetBoolean());
            await Capture(window,"library-scroll-preserved.png");
            await Js(window,"Hub.actions['nr-detail']({dataset:{id:window.__scrollResource}});window.__opaqueSamples=[];window.__opaqueTimer=setInterval(()=>{window.__opaqueSamples.push(getComputedStyle(document.querySelector('.workspace')).opacity,getComputedStyle(document.querySelector('.modal')).opacity)},10)");
            await Task.Delay(220);await Js(window,"clearInterval(window.__opaqueTimer)");
            Check("Normal-motion dialogs and workbench stay opaque throughout their entrance",(await Js(window,"window.__opaqueSamples.length>10 && window.__opaqueSamples.every(value=>value==='1') && getComputedStyle(document.querySelector('.workspace')).animationName==='none'")).GetBoolean());
            await Js(window,"Hub.actions['nr-detail']({dataset:{id:window.__scrollResource}})");
            Check("Replacing an open dialog does not restart its dimming or entrance animation",(await Js(window,"getComputedStyle(document.querySelector('.modal-backdrop')).animationName==='none' && getComputedStyle(document.querySelector('.modal')).animationName==='none'")).GetBoolean());
            await Js(window,"Hub.closeModal();Hub.render()");await Task.Delay(150);
            Check("Closing details and rerendering do not move the workbench to the top",(await Js(window,"Math.abs(document.querySelector('.workspace-content').scrollTop-window.__scrollBefore)<1")).GetBoolean());
            var previousWidth=window.Width;var previousHeight=window.Height;
            window.Width=1000;window.Height=720;await Task.Delay(250);
            Check("Medium-width resource rows fit the viewport",(await Js(window,"document.querySelector('.workspace-content').scrollWidth<=document.querySelector('.workspace-content').clientWidth && [...document.querySelectorAll('.nr-row-actions')].every(actions=>actions.getBoundingClientRect().right<=document.querySelector('.nr-list').getBoundingClientRect().right)")).GetBoolean());
            await Capture(window,"library-medium-light.png");
            window.Width=640;await Task.Delay(250);await Js(window,"document.querySelector('#nr-sources').open=false;document.querySelector('.nr-list').scrollIntoView({block:'start'})");
            Check("Minimum-width resource rows keep all three actions visible without horizontal scrolling",(await Js(window,"document.querySelector('.workspace-content').scrollWidth<=document.querySelector('.workspace-content').clientWidth && [...document.querySelectorAll('.nr-row-actions')].every(actions=>actions.getBoundingClientRect().right<=document.querySelector('.nr-list').getBoundingClientRect().right)")).GetBoolean());
            await Capture(window,"library-compact-light.png");window.Width=previousWidth;window.Height=previousHeight;await Task.Delay(200);
            await VerifyUsabilityAsync(host, window);
            await Js(window,"Hub.navigate('home')");Check("Original application launcher and scene shortcuts coexist after resource operations",(await Js(window,"Boolean(document.querySelector('.application-launcher') && document.querySelector('.home-scenes'))")).GetBoolean());
            var large=Path.Combine(AppContext.BaseDirectory,"m22-owned-large-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(large);
            await Task.Run(()=>{for(var i=0;i<1800;i++)File.WriteAllText(Path.Combine(large,i+".txt"),"Owned metadata performance fixture.");});
            await Js(window,"window.__resourceTicks=0;window.__resourceInterval=setInterval(()=>window.__resourceTicks++,5)");
            var timing=Stopwatch.StartNew();var largePreview=await host.Resources.PreviewAsync(large,"custom",false,Guid.NewGuid().ToString("N"));var ticks=(await Js(window,"window.__resourceTicks")).GetInt32();await Js(window,"clearInterval(window.__resourceInterval)");host.Resources.Cancel(largePreview.Token);
            Check("Actual WebView2 UI continues ticking during 1800-file asynchronous preview",largePreview.Files==1800&&ticks>0);
            File.WriteAllText(Path.Combine(output,"large-preview-performance.json"),JsonSerializer.Serialize(new{files=largePreview.Files,elapsedMs=timing.ElapsedMilliseconds,webViewTicks=ticks,workingSetBytes=Process.GetCurrentProcess().WorkingSet64},HubApplication.Json));
            File.WriteAllText(Path.Combine(output,"expected-resources.json"),JsonSerializer.Serialize(host.Resources.Store.Snapshot(),ResourceStore.Json));
            await Request(window, "settings.save", new { settings = new { workspaceOpacity = .65 } });
            File.WriteAllText(Path.Combine(output,"phase1-results.json"),JsonSerializer.Serialize(checks,HubApplication.Json));Complete(host,"phase1-summary.txt");
        }
        catch(Exception ex){File.WriteAllText(Path.Combine(output,"error.txt"),ex.ToString());if(host.Workspace is not null)try{await Capture(host.Workspace,"failure.png");}catch{}Application.Current.Shutdown(1);}
    }
    private static async Task VerifyOnboardingAsync(HubApplication host, HubWindow window)
    {
        await WaitJs(window, "Boolean(window.DesktopHubOnboarding)");
        var before = host.Store.Current;
        var startup = host.Startup.Query();
        var icons = host.DesktopIcons.Query();
        await Js(window, "Hub.navigate('settings');Hub.actions['settings-section']({dataset:{section:'system'}})");
        Check("Native settings exposes an enabled replay guide", (await Js(window, "!!document.querySelector('[data-action=settings-replay-onboarding]:not(:disabled)')")).GetBoolean());
        await Js(window, "document.querySelector('[data-action=settings-replay-onboarding]').click()");
        Check("Native replay opens a locked six-step guide without marking it complete", (await Js(window, "DesktopHubOnboarding.getState().active && Hub.ui.modal.locked && document.querySelectorAll('.guide-progress li').length===6")).GetBoolean() && host.Store.Current.OnboardingCompleted == before.OnboardingCompleted);
        for (var step = 0; step < 6; step++)
        {
            await WaitJs(window, $"DesktopHubOnboarding.getState().step==={step}");
            Check($"Guide step {step + 1} fits its modal with reachable footer", (await Js(window, "(() => {const modal=document.querySelector('.modal'),body=document.querySelector('.modal-body'),footer=document.querySelector('.modal-footer');return body.scrollWidth<=body.clientWidth+1 && modal.getBoundingClientRect().left>=0 && footer.getBoundingClientRect().bottom<=innerHeight})()")).GetBoolean());
            await Capture(window, $"guide-step-{step + 1}-dark.png");
            if (step == 3)
                Check("Guide offers manual Windows recovery and requires first-hide policy", (await Js(window, "document.querySelector('.native-guide').textContent.includes('桌面空白处右键') && !document.querySelector('input[name=guide-policy]:checked') && document.querySelector('[data-action=guide-icons][data-visible=false]').disabled")).GetBoolean());
            if (step < 5) await Js(window, "document.querySelector('[data-action=guide-next]').click()");
        }
        await Js(window, "document.querySelector('[data-action=guide-finish]').click()");
        await WaitJs(window, "!DesktopHubOnboarding.getState().active && !Hub.ui.modal");
        Check("Guide completion is persisted by the native settings bridge", host.Store.Current.OnboardingCompleted);
        Check("Reading guide steps keeps system integration and existing content unchanged", host.Startup.Query().Enabled == startup.Enabled && host.DesktopIcons.Query().Visible == icons.Visible && host.Store.Current.AppEntries.Count == before.AppEntries.Count && host.Resources.Query(new()).Total == 0);
        await Request(window, "settings.save", new { settings = new { theme = "light" } });
        window.Width = 480; window.Height = 440; await Task.Delay(250);
        await Js(window, "Hub.openOnboarding();Hub.actions['guide-next']();Hub.actions['guide-next']();Hub.actions['guide-next']()");
        Check("Replayed guide at narrow size keeps progress, scroll and footer usable", (await Js(window, "document.querySelector('.modal-body').scrollWidth<=document.querySelector('.modal-body').clientWidth+1 && document.querySelector('.modal-footer').getBoundingClientRect().bottom<=innerHeight && DesktopHubOnboarding.getState().step===3")).GetBoolean());
        await Capture(window, "guide-compact-light.png");
        await Js(window, "document.querySelector('[data-action=guide-skip]').click()");
        await WaitJs(window, "!DesktopHubOnboarding.getState().active");
        Check("Skipping replay retains completion and existing contents", host.Store.Current.OnboardingCompleted && host.Store.Current.AppEntries.Count == before.AppEntries.Count);
        window.Width = 960; window.Height = 640;
        await Request(window, "settings.save", new { settings = new { theme = "dark" } });
        await Js(window, "Hub.navigate('library')");
        await WaitJs(window, "!DesktopHubResources.getState().loading");
    }
    private static async Task VerifyWorkspaceHeaderAsync(HubApplication host, HubWindow window)
    {
        Check("Workspace header keeps one Close button and removes full exit", (await Js(window, "document.querySelectorAll('.header-actions [data-action=close-surface]').length===1 && !document.querySelector('.header-actions [data-action=native-exit]')")).GetBoolean());
        await Js(window, "Hub.navigate('settings');Hub.actions['settings-section']({dataset:{section:'appearance'}})");
        Check("Appearance settings retain all three theme choices", (await Js(window, "document.querySelectorAll('button[data-choice-key=theme]').length===3 && !!document.querySelector('[data-choice-key=theme][data-choice-value=system]')")).GetBoolean());
        await Js(window, "document.querySelector('[data-choice-key=theme][data-choice-value=system]').click()");
        await WaitJs(window, "DesktopHubDesktop.getState().hostState.settings.theme==='system'");
        Check("Settings can still save Follow System", host.Store.Current.Theme == "system");
        await Capture(window, "header-settings-system.png");
        var expected = host.EffectiveTheme == "dark" ? "light" : "dark";
        await Js(window, "document.querySelector('.header-actions [data-action=cycle-theme]').click()");
        await Wait(() => host.Store.Current.Theme == expected);
        Check("Quick theme switches from the current system color to its opposite", (await Js(window, $"document.documentElement.dataset.theme==={J(expected)} && !document.querySelector('.header-actions [data-action=cycle-theme]').title.includes('跟随系统')")).GetBoolean());
        for (var i = 0; i < 3; i++)
        {
            expected = expected == "dark" ? "light" : "dark";
            await Js(window, "document.querySelector('.header-actions [data-action=cycle-theme]').click()");
            await Wait(() => host.Store.Current.Theme == expected);
        }
        Check("Repeated quick theme clicks alternate only light and dark", (await Js(window, $"Hub.state.settings.theme==={J(expected)} && document.documentElement.dataset.theme==={J(expected)}")).GetBoolean());
        await Js(window, "document.querySelector('.header-actions [data-action=close-surface]').click()");
        await Wait(() => !window.IsVisible);
        Check("Header Close hides Workspace and keeps the application running", !host.IsExiting && host.Mini.IsVisible);
        await host.OpenWorkspaceAsync("library");
        await Request(window, "settings.save", new { settings = new { theme = "dark" } });
        await WaitJs(window, "Hub.ui.page==='library' && !DesktopHubResources.getState().loading");
    }
    private static async Task VerifyUsabilityAsync(HubApplication host, HubWindow window)
    {
        await Js(window, "Hub.navigate('library');Hub.actions['nr-clear']()"); await WaitJs(window, "!DesktopHubResources.getState().loading");
        Check("Tags start with categories rather than a flat wall of time labels", (await Js(window, "document.querySelectorAll('.nr-category-choices [data-category]').length===3 && document.querySelectorAll('.nr-filter-tags [data-action=nr-filter-tag]').length===0")).GetBoolean());
        await Js(window, "Hub.actions['nr-new-category']();document.querySelector('#nr-category-name').value='验收项目';Hub.actions['nr-save-category']()");
        await WaitJs(window, "!Hub.ui.modal && !DesktopHubResources.getState().busy");
        Check("Custom tag category persists through the native bridge", host.Resources.Store.Snapshot().TagCategories.Any(c => c.Name == "验收项目"));
        await Js(window, "Hub.actions['nr-new-tag']();document.querySelector('#nr-tag-name').value='验收项目 A';document.querySelector('#nr-tag-kind').value='';Hub.actions['nr-save-tag']({dataset:{}})");
        Check("Creating a tag requires a classification", (await Js(window, "document.querySelector('#nr-tag-error').textContent.includes('分类') && !!Hub.ui.modal")).GetBoolean());
        await Js(window, "document.querySelector('#nr-tag-kind').value=DesktopHubResources.getState().category;Hub.actions['nr-save-tag']({dataset:{}})"); await WaitJs(window, "!Hub.ui.modal && !DesktopHubResources.getState().busy");
        Check("Custom classified tag retains its category", host.Resources.Store.Snapshot().Tags.Single(t => t.Name == "验收项目 A").CategoryId == host.Resources.Store.Snapshot().TagCategories.Single(c => c.Name == "验收项目").Id);
        await Js(window, "Hub.actions['nr-extension']({value:'.txt'})"); await WaitJs(window, "!DesktopHubResources.getState().loading");
        Check("Extension filtering operates on every native result page", (await Js(window, "DesktopHubResources.getState().result.items.every(x=>x.resource.extension==='.txt') && document.querySelector('[data-change=nr-extension]').value==='.txt'")).GetBoolean());
        await Js(window, "Hub.navigate('missing')"); await WaitJs(window, "!DesktopHubResources.getState().loading");
        Check("Invalid-resource breadcrumb retains its library parent and return action", (await Js(window, "document.querySelector('.workspace-subnav').textContent.includes('文件资源库/失效资源') && !!document.querySelector('.page-heading [data-action=nr-library]')")).GetBoolean());
        await Js(window, "document.dispatchEvent(new PointerEvent('pointerup',{button:3,bubbles:true}))"); await WaitJs(window, "Hub.ui.page==='library' && !DesktopHubResources.getState().loading");
        Check("Back mouse side key restores the library extension filter", (await Js(window, "DesktopHubResources.getState().extension==='.txt'")).GetBoolean());
        await Task.Delay(180); await Js(window, "document.dispatchEvent(new PointerEvent('pointerup',{button:4,bubbles:true}))");
        Check("Forward mouse side key restores the next page", (await Js(window, "Hub.ui.page==='missing'")).GetBoolean());
        await Js(window, "Hub.navigate('settings');Hub.actions['settings-section']({dataset:{section:'appearance'}})");
        Check("Settings dropdown is replaced by keyboard accessible button choices", (await Js(window, "document.querySelectorAll('.settings-content select:not([hidden])').length===0 && document.querySelector('.setting-segments[role=radiogroup] button[role=radio]')!==null && parseFloat(getComputedStyle(document.querySelector('.setting-row strong')).fontSize)>=16")).GetBoolean());
        await Js(window, "document.querySelector('[data-choice-key=theme][data-choice-value=dark]').click();window.__slideVisible=document.querySelector('.setting-slider').getAnimations().length>0");
        Check("Theme selection starts a sliding indicator animation", (await Js(window, "window.__slideVisible")).GetBoolean());
        await WaitJs(window, "DesktopHubDesktop.getState().hostState.settings.theme==='dark'"); await Task.Delay(260); await Capture(window, "settings-buttons-dark.png");
        await VerifyWorkspaceFrameAsync(host, window);
        window.Width = 640; window.Height = 560; await Task.Delay(150);
        Check("Native rounded clipping follows workspace resizing", ((FrameworkElement)window.Browser).Clip is System.Windows.Media.RectangleGeometry clip && clip.Rect.Width == ((FrameworkElement)window.Browser).ActualWidth && clip.Rect.Height == ((FrameworkElement)window.Browser).ActualHeight);
        Check("Settings remain readable without horizontal overflow at minimum width", (await Js(window, "document.querySelector('.workspace-content').scrollWidth<=document.querySelector('.workspace-content').clientWidth && [...document.querySelectorAll('.setting-segments')].every(x=>x.getBoundingClientRect().right<=document.querySelector('.workspace-content').getBoundingClientRect().right)")).GetBoolean());
        await Capture(window, "settings-compact-dark.png"); window.Width = 960; window.Height = 640;
        await Js(window, "Hub.actions['settings-section']({dataset:{section:'surfaces'}})");
        Check("Every surface-setting dropdown becomes a segmented control", (await Js(window, "document.querySelectorAll('.settings-content select:not([hidden])').length===0 && document.querySelectorAll('.setting-segments').length===7")).GetBoolean());
        await Js(window, "var opacity=document.querySelector('input[type=range]');opacity.value=0;opacity.dispatchEvent(new Event('input',{bubbles:true}));window.__opacityPreview=document.querySelector('.opacity-control output').textContent;opacity.dispatchEvent(new Event('change',{bubbles:true}))");
        await WaitJs(window, "DesktopHubDesktop.getState().hostState.settings.miniOpacity===0");
        Check("Opacity slider previews zero and saves the exact native value", (await Js(window, "window.__opacityPreview==='0%' && document.querySelector('input[type=range]').value==='0'")).GetBoolean());
        await Js(window, "var opacity=document.querySelector('input[type=range]');opacity.value=100;opacity.dispatchEvent(new Event('change',{bubbles:true}))"); await WaitJs(window, "DesktopHubDesktop.getState().hostState.settings.miniOpacity===1");
        await Capture(window, "settings-surfaces-dark.png");
        Check("Workspace exposes eight native resize handles and a visible corner", (await Js(window, "document.querySelectorAll('[data-resize-edge]').length===8 && getComputedStyle(document.querySelector('.resize-bottom-right')).cursor==='nwse-resize'")).GetBoolean());
        var invalidResize = await Request(window, "window.beginResize", new { edge = "invalid" }); Check("Native resize boundary rejects unknown edges", !invalidResize.GetProperty("ok").GetBoolean());
        var registered = host.Launcher.RegisterApp(Path.Combine(AppContext.BaseDirectory, "DesktopHub.Windows.exe")); host.AppLibrary.Add(registered); host.SettingsChanged();
        await Js(window, "Hub.navigate('home')"); await Task.Delay(200);
        Check("New applications are not automatically collected by Dock", !host.Store.Current.AppEntries.Single(a => a.Id == registered.Id).PinnedToDock);
        await Js(window, $"document.querySelector('[data-action=app-favorite][data-id={J(registered.Id)}]').click()"); await Wait(() => host.Store.Current.AppEntries.Single(a => a.Id == registered.Id).PinnedToDock);
        Check("Application star saves a Dock favorite", host.Store.Current.LauncherPins.Any(p => p.ItemId == registered.Id && p.Surface == "dock"));
        await Task.Delay(180); await Capture(window, "home-compact-dark.png");
        await Js(window, $"document.querySelector('[data-action=app-favorite][data-id={J(registered.Id)}]').click()"); await Wait(() => !host.Store.Current.AppEntries.Single(a => a.Id == registered.Id).PinnedToDock);
        Check("Removing a favorite keeps its application entry", host.Store.Current.AppEntries.Any(a => a.Id == registered.Id));
        await host.OpenDockAsync(); await WaitJs(host.Dock!, "Boolean(DesktopHubDesktop.getState().hostState)");
        Check("Dock displays only favorited applications", (await Js(host.Dock!, "document.querySelectorAll('.dock-app').length===0")).GetBoolean()); host.CloseDock();
        await host.OpenWorkspaceAsync("home"); await Task.Delay(150);
        host.SaveWindow(window); Check("User-resized workspace bounds persist", host.Store.Current.WorkspaceBounds.Width == 960 && host.Store.Current.WorkspaceBounds.Height == 640);
    }
    private static async Task VerifyWorkspaceFrameAsync(HubApplication host, HubWindow window)
    {
        Check("Workspace uses composition and a transparent native frame", window.AllowsTransparency && window.Browser is Microsoft.Web.WebView2.Wpf.WebView2CompositionControl && window.Background == System.Windows.Media.Brushes.Transparent);
        Check("Workspace page rounds its border and leaves corners transparent", (await Js(window, "getComputedStyle(document.querySelector('.workspace')).borderTopLeftRadius==='18px' && getComputedStyle(document.body).backgroundColor==='rgba(0, 0, 0, 0)'")).GetBoolean());
        Check("Workspace exposes its own complete opacity range", (await Js(window, "(() => {const slider=document.querySelector('[data-setting=workspaceOpacity]');return slider?.type==='range' && slider.min==='0' && slider.max==='100' && slider.value==='100'})()")).GetBoolean());
        var underlayColor = System.Drawing.Color.FromArgb(45, 110, 150);
        var underlay = new Window { WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false,
            Left = window.Left - 8, Top = window.Top - 8, Width = window.Width + 16, Height = window.Height + 16,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(underlayColor.R, underlayColor.G, underlayColor.B)), Topmost = true };
        var wasTopmost = window.Topmost;
        var miniOpacity = host.Store.Current.MiniOpacity;
        try
        {
            underlay.Show(); window.Topmost = true; window.Activate(); NativeMethods.SetForegroundWindow(window.Handle); await Task.Delay(300);
            using var opaque = CaptureNativeWorkspace(window, "workspace-native-rounded.png");
            Check("All four native window corners show the owned backdrop", new[] { opaque.GetPixel(1, 1), opaque.GetPixel(opaque.Width-2, 1), opaque.GetPixel(1, opaque.Height-2), opaque.GetPixel(opaque.Width-2, opaque.Height-2) }.All(color => Near(color, underlayColor)));
            var sampleX = opaque.Width - 100; var sampleY = opaque.Height - 30;
            var opaquePixel = opaque.GetPixel(sampleX, sampleY);
            await Js(window, "var slider=document.querySelector('[data-setting=workspaceOpacity]');slider.value=65;slider.dispatchEvent(new Event('change',{bubbles:true}))");
            await Wait(() => host.Store.Current.WorkspaceOpacity == .65 && window.Opacity == .65); await Task.Delay(300);
            using var translucent = CaptureNativeWorkspace(window, "workspace-native-opacity-65.png");
            var blended = System.Drawing.Color.FromArgb((int)Math.Round(opaquePixel.R*.65+underlayColor.R*.35), (int)Math.Round(opaquePixel.G*.65+underlayColor.G*.35), (int)Math.Round(opaquePixel.B*.65+underlayColor.B*.35));
            Check("Actual Workspace pixels blend at the selected opacity", !Near(opaquePixel, underlayColor) && Near(translucent.GetPixel(sampleX, sampleY), blended));
            Check("Workspace opacity does not change Mini's preference", host.Store.Current.MiniOpacity == miniOpacity);
            await Js(window, "var slider=document.querySelector('[data-setting=workspaceOpacity]');slider.value=0;slider.dispatchEvent(new Event('change',{bubbles:true}))");
            await Wait(() => host.Store.Current.WorkspaceOpacity == 0 && window.Opacity == 0);
            await host.OpenWorkspaceAsync("settings"); await Task.Delay(180);
            Check("Tray Settings can reveal a zero-opacity Workspace without overwriting it", window.Opacity == 1 && host.Store.Current.WorkspaceOpacity == 0);
            await Request(window, "settings.save", new { settings = new { workspaceOpacity = 1, theme = "light" } }); await Task.Delay(300);
            using var light = CaptureNativeWorkspace(window, "workspace-native-rounded-light.png");
            Check("Theme changes keep the native corners transparent", Near(light.GetPixel(1, 1), underlayColor));
        }
        finally
        {
            await Request(window, "settings.save", new { settings = new { workspaceOpacity = 1, theme = "dark" } });
            window.Topmost = wasTopmost; underlay.Close();
        }
    }
    private static bool Near(System.Drawing.Color a, System.Drawing.Color b) => Math.Abs(a.R-b.R)<=4 && Math.Abs(a.G-b.G)<=4 && Math.Abs(a.B-b.B)<=4;
    private static System.Drawing.Bitmap CaptureNativeWorkspace(HubWindow window, string file)
    {
        if (NativeMethods.GetForegroundWindow() != window.Handle || !NativeMethods.GetWindowRect(window.Handle, out var bounds)) throw new InvalidOperationException("Owned Workspace must be foreground before capturing its frame.");
        var bitmap = new System.Drawing.Bitmap(bounds.Right-bounds.Left, bounds.Bottom-bounds.Top);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bitmap.Size);
        bitmap.Save(Path.Combine(output, file), System.Drawing.Imaging.ImageFormat.Png);
        return bitmap;
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
