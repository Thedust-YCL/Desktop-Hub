using DesktopHub.Core;
using DesktopHub.Windows;
using System.Text.Json;

if(args.Contains("--fixture"))return NativeChecks.Fixture();
if(args.Contains("--native"))return NativeChecks.Run();
var failed = 0;
await Check("ambiguous candidates are not moved", async () => {
 using var f = new Fixture(); f.Backend.Windows.Add(f.Window(2));
 var r = await f.Service.RestoreAsync("owner", f.Scene, default);
 Equal("needsChoice", r.Entries.Single().Status); Equal(0, f.Backend.Moves.Count);
});
await Check("unique candidate is moved using target monitor DPI and negative origin", async () => {
 using var f = new Fixture(); var r = await f.Service.RestoreAsync("owner", f.Scene, default);
 Equal("restored", r.Entries.Single().Status); Equal(new LayoutRect(-1700, 120, 800, 600), f.Backend.Moves.Single().Rect);
});
await Check("changed registered executable never moves previous executable windows", async () => {
 using var f = new Fixture(); f.Store.Update(s => { s.AppEntries[0].Target = @"C:\new.exe"; return s; });
 var r = await f.Service.RestoreAsync("owner", f.Scene, default); Equal("executableChanged", r.Entries.Single().Code); Equal(0, f.Backend.Moves.Count);
});
await Check("two placements sharing a unique window are both unresolved", async () => {
 using var f = new Fixture(); f.Scene.WindowLayout!.Windows.Add(f.Scene.WindowLayout.Windows[0] with { });
 var r = await f.Service.RestoreAsync("owner", f.Scene, default); Equal(2, r.Entries.Count(e => e.Status == "needsChoice")); Equal(0, f.Backend.Moves.Count);
});
await Check("capture tokens bind owner and save preview geometry", () => {
 using var f = new Fixture(); var c = f.Service.Capture("owner", f.Scene.Id); var candidate = c.Candidates.Single();
 Throws(() => f.Service.Save("other", new(f.Scene.Id, c.CaptureToken, [new(candidate.CandidateToken, f.AppId, "")], true, 15000)));
 f.Backend.Windows[0] = f.Window(1) with { NormalBounds = new(500, 500, 100, 100) };
 var saved = f.Service.Save("owner", new(f.Scene.Id,c.CaptureToken,[new(candidate.CandidateToken, f.AppId, "")],true,15000));
 Equal(100d,saved.Scenes[0].WindowLayout!.Windows[0].Bounds.Left); Equal(true,saved.Scenes[0].WindowLayout!.RestoreAfterRun);
 Throws(() => f.Service.Save("owner",new(f.Scene.Id,c.CaptureToken,[],false,15000)));return Task.CompletedTask;
});
await Check("capture rejects edited scene and recycled HWND identity", () => {
 using var f = new Fixture(); var c=f.Service.Capture("owner",f.Scene.Id); var q=new LayoutSaveRequest(f.Scene.Id,c.CaptureToken,[new(c.Candidates[0].CandidateToken,f.AppId,"")],false,15000);
 f.Backend.Windows[0]=f.Window(1) with { Started=999 }; Throws(()=>f.Service.Save("owner",q));
 c=f.Service.Capture("owner",f.Scene.Id); q=q with { CaptureToken=c.CaptureToken,Selections=[new(c.Candidates[0].CandidateToken,f.AppId,"")] };
 f.Store.Update(s=>{s.Scenes[0].Name="edited";return s;}); Throws(()=>f.Service.Save("owner",q));return Task.CompletedTask;
});
await Check("choice is exact owner scene identity and rejects duplicate HWND", async () => {
 using var f = new Fixture(); f.Backend.Windows.Add(f.Window(2)); var r=await f.Service.RestoreAsync("owner",f.Scene,default); var e=r.Entries.Single();
 Throws(()=>f.Service.Choose("other",new(r.RestoreToken!,[new(e.EntryToken,e.Candidates[0].CandidateToken)])));
 var chosen=f.Service.Choose("owner",new(r.RestoreToken!,[new(e.EntryToken,e.Candidates[1].CandidateToken)])); Equal("restored",chosen.Entries.Single().Status); Equal(2L,f.Backend.Moves.Single().Window.Handle);
 Throws(()=>f.Service.Choose("owner",new(r.RestoreToken!,[])));
});
await Check("unsafe windows are skipped and refused geometry is reported", async () => {
 using var f = new Fixture(); f.Backend.Windows[0]=f.Window(1) with { SkipReason="fullscreen" }; var r=await f.Service.RestoreAsync("owner",f.Scene,default); Equal("skipped",r.Entries[0].Status);Equal(0,f.Backend.Moves.Count);
 f.Backend.Windows[0]=f.Window(1); f.Backend.Accept=false;r=await f.Service.RestoreAsync("owner",f.Scene,default);Equal("failed",r.Entries[0].Status);
});
await Check("cancelled restore never moves", async () => {
 using var f = new Fixture(); using var cts=new CancellationTokenSource();cts.Cancel();var r=await f.Service.RestoreAsync("owner",f.Scene,cts.Token);Equal("cancelled",r.Entries[0].Status);Equal(0,f.Backend.Moves.Count);
});
await Check("missing monitor falls back and clamps oversized negative placement", async () => {
 using var f=new Fixture(); var p=f.Scene.WindowLayout!.Windows[0];p.MonitorId="missing";p.Bounds=new(){Left=-100,Top=-200,Width=4000,Height=3000};var r=await f.Service.RestoreAsync("owner",f.Scene,default);Equal(new LayoutRect(-1900,20,1900,1000),f.Backend.Moves.Single().Rect);Equal("restored",r.Entries[0].Status);
});
await Check("strict bridge parser rejects unknown duplicate handles and nonboolean", () => {
 Throws(()=>LayoutRequestParser.ParseSave(Json("{\"sceneId\":\""+Guid.NewGuid()+"\",\"sceneId\":\""+Guid.NewGuid()+"\"}")));
 Throws(()=>LayoutRequestParser.ParseSceneId(Json("{\"sceneId\":\""+Guid.NewGuid()+"\",\"hwnd\":1}")));
 Throws(()=>LayoutRequestParser.ParseSave(Json("{\"sceneId\":\""+Guid.NewGuid()+"\",\"captureToken\":\""+Guid.NewGuid()+"\",\"selections\":[],\"restoreAfterRun\":1,\"waitTimeoutMs\":15000}")));
 Equal("11111111-1111-1111-1111-111111111111",LayoutRequestParser.ParseSceneId(Json("{\"sceneId\":\"11111111-1111-1111-1111-111111111111\"}")));return Task.CompletedTask;
});
await BoundaryChecks.Run(Check);
await Fix1Checks.Run(Check);
Console.WriteLine($"Layout checks: {failed} failure(s)");return failed;
async Task Check(string name,Func<Task> test){try{await test();Console.WriteLine("PASS "+name);}catch(Exception ex){failed++;Console.WriteLine("FAIL "+name+": "+ex.ToString());}}
void Equal<T>(T expected,T actual){if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new Exception($"expected {expected}, got {actual}");}
void Throws(Action a){try{a();}catch(InvalidOperationException){return;}catch(SettingsValidationException){return;}catch(JsonException){return;}throw new Exception("expected authorization/validation rejection");}
JsonElement Json(string text)=>JsonDocument.Parse(text).RootElement.Clone();
sealed class Fixture:IDisposable{
 public string AppId=Guid.NewGuid().ToString("D");public Scene Scene;public SettingsStore Store;public Backend Backend=new();public SceneWindowLayoutService Service;readonly string dir=Path.Combine(Environment.CurrentDirectory,"artifacts","usability-development","layout-test-runtime",Guid.NewGuid().ToString("N"));
 public Fixture(){Scene=new(){Id=Guid.NewGuid().ToString("D"),Name="test",Actions=[new(){Id=Guid.NewGuid().ToString("D"),Name="app",AppEntryId=AppId}],WindowLayout=new(){WaitTimeoutMs=1,Windows=[new(){AppEntryId=AppId,ExecutablePath=@"C:\app.exe",MonitorId="stable",Bounds=new(){Left=100,Top=50,Width=400,Height=300},Dpi=96}]}};Store=new(dir);Store.Load();Store.Update(s=>{s.AppEntries=[new(){Id=AppId,Name="app",Target=@"C:\app.exe"}];s.Scenes=[SceneValidator.Clone(Scene)];return s;});Backend.Windows.Add(Window(1));Service=new(Store,app=>app.Target,Backend);}
 public LayoutWindow Window(long h)=>new(h,5,100,@"C:\app.exe","Class","Document","stable",new(-1700,120,800,600),"normal","");
 public void Dispose(){Directory.Delete(dir,true);}
}
sealed class Backend:IWindowLayoutBackend{
 public List<LayoutWindow> Windows=[];public List<(LayoutWindow Window,LayoutRect Rect,string State)> Moves=[];public bool Accept=true;
 public IReadOnlyList<LayoutWindow> Enumerate()=>Windows.ToArray();public IReadOnlyList<LayoutMonitor> Monitors()=>[new("stable",new(-1920,0,1920,1080),new(-1900,20,1900,1000),192)];public string FallbackMonitorId()=>"stable";
 public bool Apply(LayoutWindow window,LayoutRect bounds,string state){Moves.Add((window,bounds,state));return Accept;}
}





