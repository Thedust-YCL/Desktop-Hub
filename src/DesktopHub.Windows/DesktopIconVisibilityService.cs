using System.Runtime.InteropServices;
using DesktopHub.Core;

namespace DesktopHub.Windows;

public sealed class DesktopIconVisibilityService : DesktopIconVisibilityController
{
    public DesktopIconVisibilityService(SettingsStore store, IDesktopIconBackend? backend = null)
        : base(store, backend ?? new ShellDesktopIconBackend()) { }
}

/// <summary>Public Shell interfaces only. Reacquire each time so Explorer restart invalidates no cached RCW.</summary>
public sealed class ShellDesktopIconBackend : IDesktopIconBackend
{
    private const uint NoIcons = 0x1000; // FWF_NOICONS; only this mask may be changed.
    public DesktopIconBackendResult Query() => WithView(view =>
    {
        Marshal.ThrowExceptionForHR(view.GetCurrentFolderFlags(out var flags));
        return new(true, (flags & NoIcons) == 0);
    });
    public DesktopIconBackendResult SetVisible(bool visible) => WithView(view =>
    {
        Marshal.ThrowExceptionForHR(view.GetCurrentFolderFlags(out var before));
        if (((before & NoIcons) == 0) != visible)
            Marshal.ThrowExceptionForHR(view.SetCurrentFolderFlags(NoIcons, visible ? 0 : NoIcons));
        Marshal.ThrowExceptionForHR(view.GetCurrentFolderFlags(out var after));
        return ((after & NoIcons) == 0) == visible
            ? new(true, visible)
            : new(false, (after & NoIcons) == 0, "verification_failed", "Windows Shell 未应用图标可见性目标。");
    });

    private static DesktopIconBackendResult WithView(Func<IFolderView2, DesktopIconBackendResult> operation)
    {
        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            return new(false, null, "shell_apartment_unavailable", "桌面图标 Shell 接口需要 STA 线程；当前状态未知。");
        object? shellWindows = null, desktop = null, browserObject = null, viewObject = null;
        try
        {
            // Microsoft desktop view discovery: IShellWindows -> IServiceProvider -> IShellBrowser -> IFolderView2.
            // https://devblogs.microsoft.com/oldnewthing/20130318-00/?p=4933
            var type = Type.GetTypeFromCLSID(new Guid("9BA05972-F6A8-11CF-A442-00A0C90A8F39"), throwOnError: true)!;
            shellWindows = Activator.CreateInstance(type)!;
            object location = 0; // CSIDL_DESKTOP
            object? root = null; // VT_EMPTY (the documented reserved argument)
            var windows = (IShellWindows)shellWindows;
            var hr = windows.FindWindowSW(ref location, ref root, 8 /* SWC_DESKTOP */, out _, 1 /* SWFO_NEEDDISPATCH */, out desktop);
            if (hr != 0 || desktop is null) return new(false, null, "shell_unavailable", "Windows 桌面视图尚未就绪或不可用。");
            var service = new Guid("4C96BE40-915C-11CF-99D3-00AA004AE837"); // SID_STopLevelBrowser
            var browserId = typeof(IShellBrowser).GUID;
            Marshal.ThrowExceptionForHR(((IShellServiceProvider)desktop).QueryService(ref service, ref browserId, out browserObject));
            Marshal.ThrowExceptionForHR(((IShellBrowser)browserObject).QueryActiveShellView(out viewObject));
            return operation((IFolderView2)viewObject);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or NotSupportedException)
        { return new(false, null, "shell_unavailable", $"当前桌面图标状态未知/接口不受支持（0x{ex.HResult:X8}）：{ex.Message}"); }
        finally
        {
            // Each method owns these references; no global Shell RCW survives Explorer restart.
            foreach (var instance in new[] { viewObject, browserObject, desktop, shellWindows })
                if (instance is not null && Marshal.IsComObject(instance)) Marshal.ReleaseComObject(instance);
        }
    }

    // Declarations preserve the published Windows SDK vtable order; unused slots are never invoked.
    [ComImport, Guid("85CB6900-4D95-11CF-960C-0080C7F4EE85"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IShellWindows
    {
        [PreserveSig] int get_Count(out int count);
        [PreserveSig] int Item([MarshalAs(UnmanagedType.Struct)] object index, [MarshalAs(UnmanagedType.IDispatch)] out object item);
        [PreserveSig] int NewEnum([MarshalAs(UnmanagedType.IUnknown)] out object enumerator);
        [PreserveSig] int Register([MarshalAs(UnmanagedType.IDispatch)] object pid, int hwnd, int shellClass, out int cookie);
        [PreserveSig] int RegisterPending(int threadId, [MarshalAs(UnmanagedType.Struct)] ref object location, [MarshalAs(UnmanagedType.Struct)] ref object root, int shellClass, out int cookie);
        [PreserveSig] int Revoke(int cookie);
        [PreserveSig] int OnNavigate(int cookie, [MarshalAs(UnmanagedType.Struct)] ref object location);
        [PreserveSig] int OnActivated(int cookie, [MarshalAs(UnmanagedType.VariantBool)] bool active);
        [PreserveSig] int FindWindowSW([MarshalAs(UnmanagedType.Struct)] ref object location,
            [MarshalAs(UnmanagedType.Struct)] ref object? root, int shellClass, out int hwnd, int options,
            [MarshalAs(UnmanagedType.IDispatch)] out object? desktop);
    }
    [ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellServiceProvider
    {
        [PreserveSig] int QueryService(ref Guid service, ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }
    [ComImport, Guid("000214E2-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellBrowser
    {
        [PreserveSig] int GetWindow(out nint window);
        [PreserveSig] int ContextSensitiveHelp([MarshalAs(UnmanagedType.Bool)] bool enter);
        [PreserveSig] int InsertMenusSB(nint menu, nint widths);
        [PreserveSig] int SetMenuSB(nint menu, nint oleMenu, nint activeWindow);
        [PreserveSig] int RemoveMenusSB(nint menu);
        [PreserveSig] int SetStatusTextSB([MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int EnableModelessSB([MarshalAs(UnmanagedType.Bool)] bool enabled);
        [PreserveSig] int TranslateAcceleratorSB(nint message, ushort id);
        [PreserveSig] int BrowseObject(nint pidl, uint flags);
        [PreserveSig] int GetViewStateStream(uint mode, out nint stream);
        [PreserveSig] int GetControlWindow(uint id, out nint window);
        [PreserveSig] int SendControlMsg(uint id, uint message, nuint wParam, nint lParam, out nint result);
        [PreserveSig] int QueryActiveShellView([MarshalAs(UnmanagedType.IUnknown)] out object view);
    }
    [ComImport, Guid("1AF3A467-214F-4298-908E-06B03E0B39F9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFolderView2
    {
        [PreserveSig] int GetCurrentViewMode(out uint mode);
        [PreserveSig] int SetCurrentViewMode(uint mode);
        [PreserveSig] int GetFolder(ref Guid iid, out nint folder);
        [PreserveSig] int Item(int index, out nint pidl);
        [PreserveSig] int ItemCount(uint flags, out int count);
        [PreserveSig] int Items(uint flags, ref Guid iid, out nint items);
        [PreserveSig] int GetSelectionMarkedItem(out int item);
        [PreserveSig] int GetFocusedItem(out int item);
        [PreserveSig] int GetItemPosition(nint pidl, nint point);
        [PreserveSig] int GetSpacing(nint point);
        [PreserveSig] int GetDefaultSpacing(nint point);
        [PreserveSig] int GetAutoArrange();
        [PreserveSig] int SelectItem(int item, uint flags);
        [PreserveSig] int SelectAndPositionItems(uint count, nint pidls, nint points, uint flags);
        [PreserveSig] int SetGroupBy(nint propertyKey, [MarshalAs(UnmanagedType.Bool)] bool ascending);
        [PreserveSig] int GetGroupBy(nint propertyKey, nint ascending);
        [PreserveSig] int SetViewProperty(nint pidl, nint propertyKey, nint value);
        [PreserveSig] int GetViewProperty(nint pidl, nint propertyKey, nint value);
        [PreserveSig] int SetTileViewProperties(nint pidl, [MarshalAs(UnmanagedType.LPWStr)] string properties);
        [PreserveSig] int SetExtendedTileViewProperties(nint pidl, [MarshalAs(UnmanagedType.LPWStr)] string properties);
        [PreserveSig] int SetText(int type, [MarshalAs(UnmanagedType.LPWStr)] string text);
        [PreserveSig] int SetCurrentFolderFlags(uint mask, uint flags);
        [PreserveSig] int GetCurrentFolderFlags(out uint flags);
    }
}
