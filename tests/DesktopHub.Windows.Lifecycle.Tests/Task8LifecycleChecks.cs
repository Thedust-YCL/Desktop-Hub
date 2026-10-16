using DesktopHub.Windows;
internal static class Task8LifecycleChecks
{
 public static void Run(Action<string,Action> run,Action<bool,string> check)
 {
  run("Task8 early timer wake waits the remaining millisecond and reaches release without another event",()=>{
   var state=new HiddenSurfaceLifecycle();state.SetMode(true);var generation=state.Hide(0);
   check(BrowserViewSnapshot.TryParse("{\"version\":1,\"page\":\"home\"}",out var snapshot),"safe snapshot");
   state.Acknowledge(generation,snapshot,false,0);long now=0;var waits=0;
   var reached=state.WaitForDeadlineAsync(generation,()=>now,(remaining,cancellation)=>{
    check(remaining.TotalMilliseconds==(waits==0?60000:1),"waits only remaining monotonic time");
    now=++waits==1?59999:60000;
    check(!state.TryRelease(generation,59999,0),"never releases before sixty seconds");
    return Task.CompletedTask;
   },default).GetAwaiter().GetResult();
   check(reached&&state.TryRelease(generation,now,0),"deadline reaches release after early wake");
   check(waits==2,"one early wake needs exactly one remaining wait");
  });
  run("Task8 deadline wait cannot release a shown disposed or mode-changed owner",()=>{
   foreach(var transition in new Action<HiddenSurfaceLifecycle>[] {s=>s.Show(),s=>s.Dispose(),s=>s.SetMode(false)}) {
    var state=new HiddenSurfaceLifecycle();state.SetMode(true);var generation=state.Hide(0);var waits=0;
    var reached=state.WaitForDeadlineAsync(generation,()=>59999,(remaining,cancellation)=>{
     check(++waits==1,"stale owner does not schedule another wait");transition(state);return Task.CompletedTask;
    },default).GetAwaiter().GetResult();
    check(!reached,"stale owner cannot reach release");
   }
  });
  run("Task8 cancellation at a timer wake prevents deadline release",()=>{
   var state=new HiddenSurfaceLifecycle();state.SetMode(true);var generation=state.Hide(0);using var cancellation=new CancellationTokenSource();long now=0;
   var pending=state.WaitForDeadlineAsync(generation,()=>now,(remaining,token)=>{now=60000;cancellation.Cancel();return Task.CompletedTask;},cancellation.Token);
   try {pending.GetAwaiter().GetResult();throw new Exception("Expected cancellation");}catch(OperationCanceledException){}
   check(state.State==HiddenSurfaceState.PreparingHide,"cancellation did not authorize release");
  });
  BrowserViewSnapshot Safe() {check(BrowserViewSnapshot.TryParse("{\"version\":1,\"page\":\"library\",\"resources\":{\"query\":\"notes\",\"sort\":\"name\"},\"scroll\":{\"top\":99,\"left\":0}}",out var value),"safe DTO");return value!;}
  run("Task8 sixty second release requires latest safe snapshot and preserves quick mode",()=>{
   var state=new HiddenSurfaceLifecycle();state.SetMode(true);var generation=state.Hide(100);
   check(!state.TryRelease(generation,60100,0),"no trusted ack retains");check(state.Acknowledge(generation,Safe(),false,0),"quiescence");
   check(!state.TryRelease(generation,60099,0),"59.999 seconds retains");check(state.TryRelease(generation,60100,0),"60 seconds releases");
   state=new HiddenSurfaceLifecycle();generation=state.Hide(0);state.Acknowledge(generation,Safe(),false,0);check(!state.TryRelease(generation,1000000,0),"quick always retains");
  });
  run("Task8 busy modal draft import chooser native write and unknown snapshot retain past deadline",()=>{
   foreach(var busy in new[]{"modal","scene-draft","project-draft","app-draft","import","chooser","write","folder"}) {
    var state=new HiddenSurfaceLifecycle();state.SetMode(true);var generation=state.Hide(0);
    check(!state.Acknowledge(generation,Safe(),true,0)&&!state.TryRelease(generation,90000,0),busy);
    check(state.Acknowledge(generation,Safe(),false,0)&&state.TryRelease(generation,90000,0),"clear after deadline releases");
   }
   var unknown=new HiddenSurfaceLifecycle();unknown.SetMode(true);var g=unknown.Hide(0);unknown.Acknowledge(g,null,false,0);check(!unknown.TryRelease(g,90000,0),"timeout retains");
   unknown.Acknowledge(g,Safe(),false,1);check(!unknown.TryRelease(g,90000,0),"active native retains without fresh ack");unknown.Acknowledge(g,Safe(),false,0);check(!unknown.TryRelease(g,90000,1),"new request prevents release");
  });
  run("Task8 show hide and mode changes reject every stale async completion",()=>{
   var state=new HiddenSurfaceLifecycle();state.SetMode(true);var first=state.Hide(0);state.Show();var second=state.Hide(1000);
   check(!state.Acknowledge(first,Safe(),false,0),"stale hide ack rejected");state.Acknowledge(second,Safe(),false,0);check(!state.TryRelease(second,60000,0),"new hide starts full deadline");
   state.SetMode(false);check(!state.TryRelease(second,100000,0),"mode switch invalidates");state.SetMode(true);check(!state.TryRelease(state.Generation,100000,0),"new mode needs new ack");
   state.Dispose();state.Dispose();check(!state.Acknowledge(state.Generation,Safe(),false,0)&&!state.TryRelease(state.Generation,100000,0),"disposed owner cannot complete");
  });
  run("Task8 snapshot rejects unknown authority fields malformed bounds duplicates and oversized data",()=>{
   foreach(var json in new[]{"{\"version\":2,\"page\":\"home\"}","{\"version\":1,\"page\":\"home\",\"launch\":{}}","{\"version\":1,\"page\":\"folder\",\"resources\":{\"folder\":{\"path\":\"C:/data\",\"token\":\"old\"}}}","{\"version\":1,\"page\":\"home\",\"scroll\":{\"top\":-1}}","{\"version\":1,\"version\":1,\"page\":\"home\"}","{\"version\":\"1\",\"page\":\"home\"}","{\"version\":1,\"page\":\"home\",\"history\":{\"entries\":[],\"index\":0}}"})check(!BrowserViewSnapshot.TryParse(json,out _),json);
   check(!BrowserViewSnapshot.TryParse(new string('x',65537),out _),"byte bound");check(Safe().Value.GetProperty("resources").GetProperty("query").GetString()=="notes","real DTO survives document disposal");
  });
 }
}
