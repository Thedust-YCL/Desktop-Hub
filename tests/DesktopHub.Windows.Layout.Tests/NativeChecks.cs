using System.Diagnostics;
using System.Runtime.InteropServices;
using DesktopHub.Windows;
internal static class NativeChecks
{
 public static int Fixture()
 {
  var context=SetThreadDpiAwarenessContext(new nint(-4));
  CreateWindowEx(0,"STATIC","Desktop Hub private layout fixture A",0x10CF0000,100,100,500,300,0,0,0,0);
  CreateWindowEx(0,"STATIC","Desktop Hub private layout fixture B",0x10CF0000,650,100,500,300,0,0,0,0);
  while(GetMessage(out var message,0,0,0)>0){TranslateMessage(ref message);DispatchMessage(ref message);}SetThreadDpiAwarenessContext(context);return 0;
 }
 public static int Run()
 {
  using var child=Process.Start(new ProcessStartInfo(Environment.ProcessPath!,"--fixture"){UseShellExecute=false})!;
  try{
   var backend=new Win32WindowLayoutBackend();List<LayoutWindow> windows=[];
   for(var attempt=0;attempt<40;attempt++){windows=backend.Enumerate().Where(w=>w.ProcessId==child.Id).ToList();if(windows.Count==2)break;Thread.Sleep(100);}
   Require(windows.Count==2,"two controlled fixture windows must be visible");
   Require(windows.All(w=>w.SkipReason==""),"controlled windows must be controllable");
   var monitors=backend.Monitors();Require(monitors.Count>0&&monitors.All(m=>m.Dpi>=48),"PMv2 helper must report current monitor DPI");
   Require(monitors.All(m=>m.Id.StartsWith("monitor:",StringComparison.Ordinal)),"persisted identity must use monitor device interface");
   var first=windows[0];var monitor=monitors.Single(m=>m.Id==first.MonitorId);var target=new LayoutRect(monitor.Work.Left+80,monitor.Work.Top+80,560,380);
   Require(backend.Apply(first,target,"normal"),"normal placement and post-move verification");
   Require(backend.Apply(first,target,"maximized"),"maximized placement and normal bounds verification");
   var after=backend.Enumerate().Single(w=>w.Handle==first.Handle);Require(after.State=="maximized","maximized state must be observed");
   var bounds=after.NormalBounds;Require(Math.Abs(bounds.Left-target.Left)<=2&&Math.Abs(bounds.Top-target.Top)<=2,"normal restoration bounds remain screen-relative");
   Require(backend.Enumerate().All(w=>w.ProcessId!=Environment.ProcessId),"host windows excluded");
   var stale=first with{Started=first.Started+1};Require(!backend.Apply(stale,target,"normal"),"reused identity rejected without movement");
   Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new{native="PASS",fixtureProcessId=child.Id,windows=windows.Count,monitors,windowPlacementBytes=44}));return 0;
  }catch(Exception ex){Console.WriteLine("FAIL native fixture: "+ex.Message);return 1;}
  finally{if(!child.HasExited)child.Kill();child.WaitForExit(5000);}
 }
 static void Require(bool value,string text){if(!value)throw new Exception(text);}
 [StructLayout(LayoutKind.Sequential)]struct Point{public int X,Y;}
 [StructLayout(LayoutKind.Sequential)]struct Message{public nint Hwnd;public uint Id;public nuint WParam;public nint LParam;public uint Time;public Point Point;public uint Private;}
 [DllImport("user32.dll")]static extern nint SetThreadDpiAwarenessContext(nint context);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)]static extern nint CreateWindowEx(uint ex,string cls,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint param);
 [DllImport("user32.dll")]static extern int GetMessage(out Message message,nint window,uint min,uint max);
 [DllImport("user32.dll")]static extern bool TranslateMessage(ref Message message);
 [DllImport("user32.dll")]static extern nint DispatchMessage(ref Message message);
}
