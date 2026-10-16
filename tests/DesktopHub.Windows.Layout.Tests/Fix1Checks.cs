using DesktopHub.Windows;
using DesktopHub.Core;
using System.Windows.Threading;
internal static class Fix1Checks
{
 public static async Task Run(Func<string,Func<Task>,Task> check)
 {
  await check("manual history survives snapshots and never overwrites frozen automatic result",()=>{
   var history=new SceneWindowLayoutHistory();var automatic=Sample("auto-token","automatic frozen");var manual=Sample("manual-token","manual latest");
   var first=history.RecordAutomatic("real-execution",automatic);var second=history.RecordManual(manual);var snapshot=history.Snapshot();
   Require(snapshot.Count==2,"separate manual and actual automatic records");Require(snapshot.Single(x=>x.Kind=="automatic").ExecutionId=="real-execution","only actual automatic execution ID");Require(snapshot.Single(x=>x.Kind=="manual").ExecutionId is null,"manual must not fabricate execution ID");Require(first.ResultId!=second.ResultId&&second.Order>first.Order,"distinct stable identity and monotonic order");
   var chosen=history.RecordChoice("manual-token",Sample(null,"chosen manual"));snapshot=history.Snapshot();Require(chosen.ResultId==second.ResultId,"choice keeps manual result identity");Require(snapshot.Single(x=>x.Kind=="automatic").Result.Entries[0].Message=="automatic frozen","manual choice cannot replace automatic frozen outcome");Require(snapshot.Single(x=>x.Kind=="manual").Result.Entries[0].Message=="chosen manual","choice updates exact manual result");return Task.CompletedTask;
  });
  await check("manual history bound retains eight newest separately from automatic results",()=>{
   var history=new SceneWindowLayoutHistory();history.RecordAutomatic("real",Sample("auto","frozen"));for(var i=0;i<10;i++)history.RecordManual(Sample("manual-"+i,i.ToString()));var snapshot=history.Snapshot();Require(snapshot.Count(x=>x.Kind=="manual")==8,"manual history is bounded");Require(snapshot.Count(x=>x.Kind=="automatic")==1,"manual retention never evicts automatic slot");Require(snapshot.Where(x=>x.Kind=="manual").First().Result.Entries[0].Message=="2","oldest manual is evicted");return Task.CompletedTask;
  });
  await check("ready restore keeps caller queue responsive during delayed refused placement",()=>Responsive(false));
  await check("choice keeps caller queue and owner cancellation responsive during delayed refused placement",()=>Responsive(true));
 }
 static async Task Responsive(bool choose)
 {
  using var f=new Fixture();using var backend=new DelayedBackend(f.Backend);var service=new SceneWindowLayoutService(f.Store,a=>a.Target,backend);
  LayoutRestoreResult? preview=null;if(choose){f.Backend.Windows.Add(f.Window(2));preview=await service.RestoreAsync("ui",f.Scene,default);}
  using var ui=new UiQueue();var progress=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);var invoked=new TaskCompletionSource<Task<LayoutRestoreResult>>(TaskCreationOptions.RunContinuationsAsynchronously);
  ui.Post(()=>{try{var operation=choose?service.ChooseAsync("ui",new(preview!.RestoreToken!,[new(preview.Entries[0].EntryToken,preview.Entries[0].Candidates[0].CandidateToken)]),default):service.RestoreAsync("ui",f.Scene,default);invoked.SetResult(operation);}catch(Exception ex){invoked.SetException(ex);}});
  await backend.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));ui.Post(()=>{service.CancelOwner("ui");progress.TrySetResult(true);});var winner=await Task.WhenAny(progress.Task,Task.Delay(300));backend.Release.Set();var result=await (await invoked.Task);
  Require(ReferenceEquals(winner,progress.Task),"UI queue/cancellation must run before delayed native verification finishes");Require(backend.ApplyThread!=ui.ThreadId,"native placement must execute off caller dispatcher");Require(result.Entries.Any(e=>e.Status is "failed" or "cancelled"),"refused native result must be reported truthfully");
 }
 static LayoutRestoreResult Sample(string? token,string message)=>new("scene",token,[new("entry","app","needsChoice","multipleWindows",message,[])]);
 static void Require(bool value,string text){if(!value)throw new Exception(text);}
 sealed class DelayedBackend(Backend inner):IWindowLayoutBackend,IDisposable
 {
  public TaskCompletionSource<bool> Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);public ManualResetEventSlim Release=new();public int ApplyThread;
  public IReadOnlyList<LayoutWindow> Enumerate()=>inner.Enumerate();public IReadOnlyList<LayoutMonitor> Monitors()=>inner.Monitors();public string FallbackMonitorId()=>inner.FallbackMonitorId();
  public bool Apply(LayoutWindow window,LayoutRect rect,string state){ApplyThread=Environment.CurrentManagedThreadId;Entered.TrySetResult(true);Release.Wait(TimeSpan.FromSeconds(3));return false;}
  public void Dispose(){Release.Set();Release.Dispose();}
 }
 sealed class UiQueue:IDisposable
 {
  readonly Thread thread;readonly Dispatcher dispatcher;public int ThreadId=>thread.ManagedThreadId;
  public UiQueue(){var ready=new TaskCompletionSource<Dispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);thread=new Thread(()=>{ready.SetResult(Dispatcher.CurrentDispatcher);Dispatcher.Run();});thread.IsBackground=true;thread.SetApartmentState(ApartmentState.STA);thread.Start();dispatcher=ready.Task.GetAwaiter().GetResult();}
  public void Post(Action action)=>dispatcher.BeginInvoke(action,DispatcherPriority.Normal);
  public void Dispose(){dispatcher.BeginInvokeShutdown(DispatcherPriority.Normal);thread.Join(5000);}
 }
}
