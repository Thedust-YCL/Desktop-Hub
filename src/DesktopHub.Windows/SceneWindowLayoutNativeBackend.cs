using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
namespace DesktopHub.Windows;

/// <summary>Win32 edge. All geometry is physical pixels under PMv2; only self-owned helper windows probe DPI.</summary>
public sealed class Win32WindowLayoutBackend:IWindowLayoutBackend
{
    private sealed record NativeMonitor(nint Handle,LayoutMonitor Monitor);
    public IReadOnlyList<LayoutMonitor> Monitors(){using var dpi=new DpiScope();return ReadMonitors().Select(x=>x.Monitor).ToArray();}
    public string FallbackMonitorId()
    {
        using var dpi=new DpiScope();var monitors=ReadMonitors();var foreground=GetForegroundWindow();var monitor=foreground!=0?MonitorFromWindow(foreground,2):0;
        if(monitor==0&&GetCursorPos(out var point))monitor=MonitorFromPoint(point,2);
        return monitors.FirstOrDefault(m=>m.Handle==monitor)?.Monitor.Id??monitors.FirstOrDefault()?.Monitor.Id??"";
    }
    public IReadOnlyList<LayoutWindow> Enumerate()
    {
        using var dpi=new DpiScope();var monitors=ReadMonitors();var result=new List<LayoutWindow>();var integrity=Integrity(GetCurrentProcess());
        EnumWindows((window,_)=>{if(result.Count>=4096)return false;var observed=ReadWindow(window,monitors,integrity);if(observed is not null)result.Add(observed);return true;},0);
        return result;
    }
    public bool Apply(LayoutWindow selected,LayoutRect bounds,string state)
    {
        using var dpi=new DpiScope();var monitors=ReadMonitors();var integrity=Integrity(GetCurrentProcess());
        var current=ReadWindow(new nint(selected.Handle),monitors,integrity);
        if(current is null||current.SkipReason.Length>0||!SceneWindowLayoutService.SameIdentity(selected,current)||state is not("normal" or "maximized"))return false;
        var target=monitors.FirstOrDefault(m=>Contains(m.Monitor.Work,bounds))?.Monitor;
        if(target is null)return false;
        var workspace=SceneWindowLayoutService.ScreenToWorkspace(bounds,target);
        var placement=new Placement{Length=44};if(!GetWindowPlacement(new nint(selected.Handle),ref placement))return false;
        placement.Flags=4; // WPF_ASYNCWINDOWPLACEMENT keeps an unresponsive target from blocking the host.
        placement.ShowCmd=state=="maximized"?3u:4u; // SW_SHOWNOACTIVATE for normal windows.
        placement.Normal=Rect.From(workspace);
        // The last identity read is intentionally adjacent to the mutating call.
        var final=ReadWindow(new nint(selected.Handle),monitors,integrity);
        if(final is null||final.SkipReason.Length>0||!SceneWindowLayoutService.SameIdentity(selected,final)||!SetWindowPlacement(new nint(selected.Handle),ref placement))return false;
        for(var attempt=0;attempt<16;attempt++)
        {
            var actual=ReadWindow(new nint(selected.Handle),ReadMonitors(),integrity);
            if(actual is null||!SceneWindowLayoutService.SameIdentity(selected,actual))return false;
            if(actual.State==state&&Near(actual.NormalBounds,bounds))return true;
            Thread.Sleep(25);
        }
        return false;
    }
    public static bool IsFullscreen(LayoutRect rect,LayoutRect monitor,bool maximized,long style)=>!maximized&&(style&0x00C00000)==0&&rect.Left<=monitor.Left&&rect.Top<=monitor.Top&&(long)rect.Left+rect.Width>=monitor.Left+monitor.Width&&(long)rect.Top+rect.Height>=monitor.Top+monitor.Height;
    private static bool Contains(LayoutRect work,LayoutRect rect)=>rect.Left>=work.Left&&rect.Top>=work.Top&&(long)rect.Left+rect.Width<=work.Left+work.Width&&(long)rect.Top+rect.Height<=work.Top+work.Height;
    private static bool Near(LayoutRect a,LayoutRect b)=>Math.Abs((long)a.Left-b.Left)<=3&&Math.Abs((long)a.Top-b.Top)<=3&&Math.Abs((long)a.Width-b.Width)<=3&&Math.Abs((long)a.Height-b.Height)<=3;
    private static LayoutWindow? ReadWindow(nint window,IReadOnlyList<NativeMonitor> monitors,int? hostIntegrity)
    {
        if(!IsWindowVisible(window)||GetWindow(window,4)!=0)return null;
        GetWindowThreadProcessId(window,out var pid);if(pid==0||pid==Environment.ProcessId)return null;
        var cls=new StringBuilder(256);if(GetClassName(window,cls,cls.Capacity)==0)return null;var className=cls.ToString();
        if(className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "DV2ControlHost" or "Windows.UI.Core.CoreWindow")return null;
        var exStyle=GetWindowLongPtr(window,-20).ToInt64();if((exStyle&0x80)!=0)return null;
        if(DwmGetWindowAttribute(window,14,out int cloaked,4)!=0||cloaked!=0)return null;
        using var process=new ProcessHandle(OpenProcess(0x1000,false,pid));if(process.IsInvalid)return null;
        var path=new StringBuilder(32768);uint pathLength=(uint)path.Capacity;
        if(!QueryFullProcessImageName(process.Handle,0,path,ref pathLength)||!GetProcessTimes(process.Handle,out var started,out _,out _,out _))return null;
        var title=new StringBuilder(1024);GetWindowText(window,title,title.Capacity);
        var monitor=monitors.FirstOrDefault(m=>m.Handle==MonitorFromWindow(window,2))?.Monitor;if(monitor is null)return null;
        var placement=new Placement{Length=44};if(!GetWindowPlacement(window,ref placement))return null;
        var normal=SceneWindowLayoutService.WorkspaceToScreen(placement.Normal.ToLayout(),monitor);
        if(normal.Width<=0||normal.Height<=0)return null;
        var maximized=IsZoomed(window);var style=GetWindowLongPtr(window,-16).ToInt64();var skip="";
        var targetIntegrity=Integrity(process.Handle);
        if(hostIntegrity is null||targetIntegrity is null)skip="accessDenied";
        else if(targetIntegrity>hostIntegrity)skip="elevated";
        else if(IsIconic(window))skip="minimized";
        else if(GetWindowRect(window,out var rect)&&IsFullscreen(rect.ToLayout(),monitor.Screen,maximized,style))skip="fullscreen";
        return new(window.ToInt64(),(int)pid,unchecked((long)started),path.ToString(),className,title.ToString(),monitor.Id,normal,maximized?"maximized":"normal",skip);
    }
    private static IReadOnlyList<NativeMonitor> ReadMonitors()
    {
        var monitors=new List<NativeMonitor>();
        EnumDisplayMonitors(0,0,(nint handle,nint dc,ref Rect rect,nint data)=>
        {
            var info=new MonitorInfo{Size=Marshal.SizeOf<MonitorInfo>()};if(!GetMonitorInfo(handle,ref info))return true;
            var identities=new List<string>();
            for(uint index=0;index<32;index++)
            {
                var device=new DisplayDevice{Size=Marshal.SizeOf<DisplayDevice>()};if(!EnumDisplayDevices(info.Device,index,ref device,1))break;
                if((device.Flags&1)!=0&&!string.IsNullOrWhiteSpace(device.DeviceId))identities.Add(device.DeviceId.ToUpperInvariant());
            }
            // A transient DISPLAYn is never persisted as though it were stable. Unknown identities are not offered.
            if(identities.Count==0)return true;
            identities.Sort(StringComparer.Ordinal);var identity="monitor:"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("|",identities))));
            var work=info.Work.ToLayout();var helper=CreateWindowEx(0x80,"STATIC","Desktop Hub DPI probe",0x80000000,work.Left+Math.Min(32,work.Width/2),work.Top+Math.Min(32,work.Height/2),1,1,0,0,0,0);
            if(helper==0)return true;uint value;try{value=GetDpiForWindow(helper);}finally{DestroyWindow(helper);}
            if(value is <48 or >768)return true;
            monitors.Add(new(handle,new(identity,info.Monitor.ToLayout(),work,value)));return true;
        },0);
        return monitors;
    }
    private static int? Integrity(nint process)
    {
        if(!OpenProcessToken(process,8,out var token))return null;
        try
        {
            GetTokenInformation(token,25,0,0,out var size);if(size<=0||size>4096)return null;
            var buffer=Marshal.AllocHGlobal(size);try
            {
                if(!GetTokenInformation(token,25,buffer,size,out _))return null;
                var sid=Marshal.ReadIntPtr(buffer);var countPointer=GetSidSubAuthorityCount(sid);if(countPointer==0)return null;
                var count=Marshal.ReadByte(countPointer);if(count==0)return null;
                var level=GetSidSubAuthority(sid,(uint)count-1);return level==0?null:Marshal.ReadInt32(level);
            }finally{Marshal.FreeHGlobal(buffer);}
        }finally{CloseHandle(token);}
    }
    private sealed class DpiScope:IDisposable
    {
        private readonly nint previous=SetThreadDpiAwarenessContext(new nint(-4));
        public DpiScope(){if(previous==0)throw new Win32Exception(Marshal.GetLastWin32Error(),"Cannot establish PMv2 context.");}
        public void Dispose(){SetThreadDpiAwarenessContext(previous);}
    }
    private sealed class ProcessHandle(nint handle):IDisposable{public nint Handle=>handle;public bool IsInvalid=>handle==0;public void Dispose(){if(handle!=0)CloseHandle(handle);}}
    [StructLayout(LayoutKind.Sequential)]private struct Point{public int X,Y;}
    [StructLayout(LayoutKind.Sequential)]private struct Rect{public int Left,Top,Right,Bottom;public readonly LayoutRect ToLayout()=>new(Left,Top,Right-Left,Bottom-Top);public static Rect From(LayoutRect r)=>new(){Left=r.Left,Top=r.Top,Right=r.Left+r.Width,Bottom=r.Top+r.Height};}
    // SDK 10.0.26100 WinUser.h guards rcDevice with _MAC; Windows ABI is 44 bytes, including x64.
    [StructLayout(LayoutKind.Sequential)]private struct Placement{public uint Length,Flags,ShowCmd;public Point Min,Max;public Rect Normal;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct MonitorInfo{public int Size;public Rect Monitor,Work;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Device;}
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]private struct DisplayDevice{public int Size;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)]public string Name;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string Description;public uint Flags;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string DeviceId;[MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)]public string DeviceKey;}
    private delegate bool EnumWindow(nint window,nint data);
    private delegate bool EnumMonitor(nint monitor,nint dc,ref Rect rect,nint data);
    [DllImport("user32.dll",SetLastError=true)]private static extern nint SetThreadDpiAwarenessContext(nint context);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumWindow callback,nint data);
    [DllImport("user32.dll")]private static extern bool EnumDisplayMonitors(nint dc,nint clip,EnumMonitor callback,nint data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern bool GetMonitorInfo(nint monitor,ref MonitorInfo info);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern bool EnumDisplayDevices(string device,uint index,ref DisplayDevice result,uint flags);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern nint CreateWindowEx(uint exStyle,string cls,string title,uint style,int x,int y,int width,int height,nint parent,nint menu,nint instance,nint param);
    [DllImport("user32.dll")]private static extern bool DestroyWindow(nint window);
    [DllImport("user32.dll")]private static extern uint GetDpiForWindow(nint window);
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll")]private static extern bool IsZoomed(nint window);
    [DllImport("user32.dll")]private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")]private static extern nint GetWindow(nint window,uint command);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(nint window,out uint pid);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(nint window,StringBuilder buffer,int length);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(nint window,StringBuilder buffer,int length);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern nint GetWindowLongPtr(nint window,int index);
    [DllImport("user32.dll")]private static extern bool GetWindowRect(nint window,out Rect rect);
    [DllImport("user32.dll")]private static extern bool GetWindowPlacement(nint window,ref Placement placement);
    [DllImport("user32.dll")]private static extern bool SetWindowPlacement(nint window,ref Placement placement);
    [DllImport("user32.dll")]private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")]private static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")]private static extern nint MonitorFromPoint(Point point,uint flags);
    [DllImport("user32.dll")]private static extern nint MonitorFromWindow(nint window,uint flags);
    [DllImport("dwmapi.dll")]private static extern int DwmGetWindowAttribute(nint window,uint attribute,out int value,int size);
    [DllImport("kernel32.dll")]private static extern nint GetCurrentProcess();
    [DllImport("kernel32.dll")]private static extern nint OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll")]private static extern bool CloseHandle(nint handle);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]private static extern bool QueryFullProcessImageName(nint process,uint flags,StringBuilder path,ref uint size);
    [DllImport("kernel32.dll")]private static extern bool GetProcessTimes(nint process,out ulong created,out ulong exited,out ulong kernel,out ulong user);
    [DllImport("advapi32.dll")]private static extern bool OpenProcessToken(nint process,uint access,out nint token);
    [DllImport("advapi32.dll")]private static extern bool GetTokenInformation(nint token,int type,nint buffer,int length,out int returned);
    [DllImport("advapi32.dll")]private static extern nint GetSidSubAuthorityCount(nint sid);
    [DllImport("advapi32.dll")]private static extern nint GetSidSubAuthority(nint sid,uint index);
}
