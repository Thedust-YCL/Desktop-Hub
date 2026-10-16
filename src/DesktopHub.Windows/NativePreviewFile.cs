using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using DesktopHub.Core;

namespace DesktopHub.Windows;

// Pins the ancestor chain against rename/delete, then validates the actual leaf handle.
// The decoder only receives this stream; it never reopens the authorized path.
internal sealed class NativePreviewFile : IDisposable
{
    private const uint Unavailable = 0x1000u | 0x40000u | 0x400000u;
    private readonly List<SafeFileHandle> ancestors = [];
    internal FileStream Stream { get; private set; } = null!;
    internal string Stamp { get; private set; } = "";
    internal string Identity { get; private set; } = "";
    private string path = "";
    internal static NativePreviewFile Open(Resource resource)
    {
        var result = new NativePreviewFile();
        try
        {
            result.path = ResourceScanner.Canonical(resource.CanonicalPath);
            var parents = new Stack<string>();
            for (var directory = Path.GetDirectoryName(result.path); directory is not null; directory = Path.GetDirectoryName(directory)) parents.Push(directory);
            while (parents.TryPop(out var directory))
            {
                Precheck(directory);
                var handle = CreateFile(directory, 0x80, 3, 0, 3, 0x02300000, 0);
                if (handle.IsInvalid) { handle.Dispose(); throw new IOException("目录暂不可读取。", new Win32Exception(Marshal.GetLastWin32Error())); }
                result.ancestors.Add(handle); Validate(handle, directory, true);
            }
            Precheck(result.path);
            var leaf = CreateFile(result.path, 0x80000000, 1, 0, 3, 0x00300000, 0);
            if (leaf.IsInvalid) { leaf.Dispose(); throw new IOException("文件暂不可读取，可能已移动或正在写入。", new Win32Exception(Marshal.GetLastWin32Error())); }
            try
            {
                var info = Validate(leaf, result.path, false);
                result.Identity = $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}";
                if (resource.FilesystemIdentity is not null && resource.FilesystemIdentity != result.Identity) throw new IOException("文件身份已变化，请刷新或重新关联。");
                result.Stamp = StampOf(info);
                result.Stream = new FileStream(leaf, FileAccess.Read, 65536, false);
            }
            catch { leaf.Dispose(); throw; }
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    internal void Verify()
    { if (StampOf(Validate(Stream.SafeFileHandle, path, false)) != Stamp) throw new IOException("预览期间文件已变化，请重试。"); }
    private static void Precheck(string path)
    { if (((uint)File.GetAttributes(path) & Unavailable) != 0) throw new IOException("文件是云占位，请先在 Windows 中使其在本地可用。"); }
    private static Info Validate(SafeFileHandle handle, string path, bool directory)
    {
        if (!GetFileInformationByHandle(handle, out var info) || !GetFileInformationByHandleEx(handle, 9, out var tag, 8)) throw new IOException("无法检查文件身份。");
        if ((info.Attributes & Unavailable) != 0) throw new IOException("云占位内容不会主动下载，请先在 Windows 中使其可用。");
        if ((tag.Attributes & 0x400) != 0 && (tag.Tag & 0xFFFF0FFFu) != 0x9000001Au) throw new IOException("路径经过链接，请选择其真实目录。");
        if (((info.Attributes & 0x10) != 0) != directory) throw new IOException("资源类型已变化，请刷新。");
        var final = new StringBuilder(32768); var length = GetFinalPathNameByHandle(handle, final, (uint)final.Capacity, 0);
        if (length == 0 || length >= final.Capacity) throw new IOException("无法验证文件最终路径。");
        var actual = final.ToString(); if (actual.StartsWith(@"\\?\", StringComparison.Ordinal)) actual = actual[4..];
        if (!Path.TrimEndingDirectorySeparator(actual).Equals(Path.TrimEndingDirectorySeparator(path), StringComparison.OrdinalIgnoreCase)) throw new IOException("文件路径在授权后已变化，请重试。");
        return info;
    }
    private static string StampOf(Info info) => $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}:{info.Write.High:X8}{info.Write.Low:X8}:{info.SizeHigh:X8}{info.SizeLow:X8}:{info.Attributes:X8}";
    public void Dispose() { Stream?.Dispose(); foreach (var handle in ancestors) handle.Dispose(); ancestors.Clear(); }
    [StructLayout(LayoutKind.Sequential)] private struct Time { public uint Low, High; }
    [StructLayout(LayoutKind.Sequential)] private struct Info { public uint Attributes; public Time Creation, Access, Write; public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow; }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes, Tag; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out Info info);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out AttributeTag info, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")] private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint size, uint flags);
}
