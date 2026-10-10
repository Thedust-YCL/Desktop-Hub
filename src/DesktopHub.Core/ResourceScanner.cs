using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DesktopHub.Core;

public sealed record ScanResult(List<Resource> Items, List<string> Warnings, List<string> InaccessiblePaths, bool Available, string? Error);
public sealed record ScanProgress(string OperationId, int Count, string Path, string Phase);
public sealed record ScanPreview(string Token, string Path, string SourceType, bool Recursive, int Files, int Folders, bool CloudPath, List<string> Warnings, DateTimeOffset ExpiresAt);
public sealed record FolderPage(string Token, string Path, List<Resource> Items, int Total, int Page, List<string> Warnings);

public static class ResourceScanner
{
    public const int MaximumItems = 100000;
    public static string Canonical(string path) => Path.TrimEndingDirectorySeparator(NativeLaunchService.ValidateLocalPath(path, "invalid_resource_path", false));
    public static bool Within(string path, string root) => path.Equals(root, StringComparison.OrdinalIgnoreCase) || path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    public static bool SameParent(string a, string b) => string.Equals(Path.GetDirectoryName(a), Path.GetDirectoryName(b), StringComparison.OrdinalIgnoreCase);
    public static string DesktopPath(bool shared = false) => Canonical(Environment.GetFolderPath(shared ? Environment.SpecialFolder.CommonDesktopDirectory : Environment.SpecialFolder.DesktopDirectory, Environment.SpecialFolderOption.DoNotVerify));
    public static bool CloudPath(string path) => path.Split(Path.DirectorySeparatorChar).Any(part => part.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase));
    public static void EnsureSafe(string path, bool allowLeafLink = false)
    {
        path = Canonical(path); var current = new DirectoryInfo(Path.GetDirectoryName(path)!);
        while (current is not null)
        {
            if ((current.Attributes & FileAttributes.ReparsePoint) != 0 && !IsCloudReparse(current.FullName)) throw new IOException("路径经过目录联接或符号链接，请选择其真实目录。");
            if (((uint)current.Attributes & (0x1000u | 0x40000u | 0x400000u)) != 0) throw new IOException("路径经过云占位目录，请先在 Windows 中使目录可用。");
            current = current.Parent;
        }
        var leaf = File.GetAttributes(path);
        if (!allowLeafLink && (leaf & FileAttributes.ReparsePoint) != 0 && !IsCloudReparse(path)) throw new IOException("不浏览或打开重解析点，请选择真实路径。");
        if (leaf.HasFlag(FileAttributes.Directory) && ((uint)leaf & (0x1000u | 0x40000u | 0x400000u)) != 0) throw new IOException("云占位目录未在本地可用，请先在 Windows 中处理。");
    }
    public static Resource Read(string path)
    {
        path = Canonical(path);
        var attributes = File.GetAttributes(path);
        var folder = attributes.HasFlag(FileAttributes.Directory);
        FileSystemInfo info = folder ? new DirectoryInfo(path) : new FileInfo(path);
        // Metadata only: never read document/image bytes or hydrate a cloud placeholder.
        var cloud = ((uint)attributes & (0x1000u | 0x40000u | 0x400000u)) != 0;
        var link = attributes.HasFlag(FileAttributes.ReparsePoint) && !IsCloudReparse(path);
        return new Resource { CanonicalPath = path, LastKnownPath = path, DisplayName = info.Name, ResourceType = folder ? "folder" : "file",
            Extension = folder ? "" : info.Extension.ToLowerInvariant(), CreationTime = info.CreationTimeUtc, ModificationTime = info.LastWriteTimeUtc,
            CloudPlaceholder = cloud, ReparsePoint = link, FilesystemIdentity = cloud || link ? null : Identity(path) };
    }
    private static Resource ReadWithRetry(string path, CancellationToken token)
    {
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested();
            try { return Read(path); }
            catch (Exception ex) when (attempt < 2 && ex is IOException or UnauthorizedAccessException)
            { if (token.WaitHandle.WaitOne(60 * (attempt + 1))) token.ThrowIfCancellationRequested(); }
        }
    }
    public static ScanResult Scan(string root, bool recursive, string excluded, CancellationToken token, Action<int, string>? progress = null)
    {
        root = Canonical(root); var items = new List<Resource>(); var warnings = new List<string>(); var inaccessible = new List<string>();
        try { EnsureSafe(root); if (!Directory.Exists(root)) throw new DirectoryNotFoundException("来源目录不可用。"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(items, warnings, [root], false, ex.Message); }
        var stack = new Stack<(string Path, int Depth)>(); stack.Push((root, 0));
        while (stack.TryPop(out var entry))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                EnsureSafe(entry.Path);
                foreach (var path in Directory.EnumerateFileSystemEntries(entry.Path))
                {
                    token.ThrowIfCancellationRequested();
                    if (Within(path, excluded)) continue;
                    if (items.Count >= MaximumItems) throw new InvalidOperationException("单次扫描超过 100000 项，请拆分来源。");
                    try
                    {
                        var item = ReadWithRetry(path, token); items.Add(item);
                        if (recursive && item.ResourceType == "folder")
                        {
                            if (item.ReparsePoint || item.CloudPlaceholder) warnings.Add("未递归链接/云占位目录：" + path);
                            else if (entry.Depth >= 63) { inaccessible.Add(path); warnings.Add("超过 64 层，未继续扫描：" + path); }
                            else stack.Push((path, entry.Depth + 1));
                        }
                        if (items.Count % 100 == 0) progress?.Invoke(items.Count, path);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { inaccessible.Add(path); if (warnings.Count < 100) warnings.Add("暂无法读取：" + path + " · " + ex.Message); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { inaccessible.Add(entry.Path); if (warnings.Count < 100) warnings.Add("暂无法浏览：" + entry.Path + " · " + ex.Message); }
        }
        progress?.Invoke(items.Count, root);
        return new(items, warnings.Take(100).ToList(), inaccessible, !inaccessible.Contains(root, StringComparer.OrdinalIgnoreCase), inaccessible.Contains(root, StringComparer.OrdinalIgnoreCase) ? "来源无法完整读取，请检查权限后刷新。" : null);
    }
    private static string? Identity(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        using var handle = CreateFile(path, 0x80, 7, 0, 3, 0x02300000, 0); // attributes, shared R/W/delete, backup + open reparse point + no recall
        if (handle.IsInvalid || !GetFileInformationByHandle(handle, out var info)) return null;
        return $"{info.Volume:X8}:{info.IndexHigh:X8}{info.IndexLow:X8}";
    }
    private static bool IsCloudReparse(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        using var handle = CreateFile(path, 0x80, 7, 0, 3, 0x02300000, 0);
        return !handle.IsInvalid && GetFileInformationByHandleEx(handle, 9, out var info, 8) && (info.Tag & 0xFFFF0FFFu) == 0x9000001Au;
    }
    [StructLayout(LayoutKind.Sequential)] private struct AttributeTag { public uint Attributes, Tag; }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int kind, out AttributeTag info, uint size);
    [StructLayout(LayoutKind.Sequential)] private struct FileInfoNative
    {
        public uint Attributes; public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafeFileHandle CreateFile(string path, uint access, uint share, nint security, uint disposition, uint flags, nint template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInfoNative info);
}
