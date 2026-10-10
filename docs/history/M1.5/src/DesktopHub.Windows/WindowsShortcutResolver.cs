using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using DesktopHub.Core;

namespace DesktopHub.Windows;

/// <summary>Reads user-selected shortcut metadata without Resolve, target searching, or ShellExecute.</summary>
public sealed class WindowsShortcutResolver : IShortcutResolver
{
    public ResolvedShortcut Resolve(string shortcutPath)
    {
        if (!File.Exists(shortcutPath)) throw new TargetValidationException("target_missing", "快捷方式已不存在，请重新选择。");
        object? instance = null;
        try
        {
            instance = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046"), true)!)!;
            ((IPersistFile)instance).Load(shortcutPath, 0);
            var link = (IShellLinkW)instance;
            ((IShellLinkDataList)instance).GetFlags(out var flags);
            if ((flags & 0x1000) != 0)
                throw new TargetValidationException("unsupported_shortcut", "Windows Installer 特殊快捷方式待后续兼容，请选择普通 EXE 或其快捷方式。");
            if ((flags & 0x2000) != 0)
                throw new TargetValidationException("elevation_required", "此快捷方式要求以管理员身份运行，当前启动台不支持此启动方式。");
            var target = new StringBuilder(32768);
            link.GetPath(target, target.Capacity, nint.Zero, 4); // SLGP_RAWPATH. Do not resolve or update the original LNK.
            if (target.Length == 0)
                throw new TargetValidationException("unsupported_shortcut", "快捷方式没有普通 EXE 目标。Store/MSIX、网页及特殊 Shell 入口待后续兼容。");
            var arguments = new StringBuilder(32768); link.GetArguments(arguments, arguments.Capacity);
            var workingDirectory = new StringBuilder(32768); link.GetWorkingDirectory(workingDirectory, workingDirectory.Capacity);
            var icon = new StringBuilder(32768); link.GetIconLocation(icon, icon.Capacity, out var index);
            return new ResolvedShortcut(Environment.ExpandEnvironmentVariables(target.ToString()), arguments.ToString(),
                Environment.ExpandEnvironmentVariables(workingDirectory.ToString()),
                icon.Length == 0 ? null : Environment.ExpandEnvironmentVariables(icon.ToString()), index);
        }
        catch (COMException ex)
        {
            throw new TargetValidationException("invalid_shortcut", $"无法读取 Windows 快捷方式（0x{ex.HResult:X8}）：{ex.Message}");
        }
        finally { if (instance is not null && Marshal.IsComObject(instance)) Marshal.FinalReleaseComObject(instance); }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, nint data, uint flags);
        void GetIDList(out nint pidl);
        void SetIDList(nint pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder text, int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string text);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, Guid("45E2B4AE-B1C3-11D0-B92F-00A0C90312E1"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkDataList
    {
        void AddDataBlock(nint data);
        void CopyDataBlock(uint signature, out nint data);
        void RemoveDataBlock(uint signature);
        void GetFlags(out uint flags);
        void SetFlags(uint flags);
    }
}
