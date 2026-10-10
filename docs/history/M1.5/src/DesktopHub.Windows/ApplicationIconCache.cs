using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DesktopHub.Core;

namespace DesktopHub.Windows;

/// <summary>Native local icon extraction, content-addressed cache, shared default on extraction failure.</summary>
internal sealed class ApplicationIconCache(string dataDirectory, IShortcutResolver shortcuts)
{
    private readonly string directory = Path.Combine(dataDirectory, "icons");
    private readonly Dictionary<string, string> loaded = new(StringComparer.Ordinal);

    public string Extract(AppEntry app)
    {
        nint large = 0, small = 0;
        try
        {
            var target = app.Target;
            if (Path.GetExtension(target).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                var shortcut = shortcuts.Resolve(target);
                target = NativeLaunchService.ValidateWorkingDirectory(shortcut.Target);
                if (TryLocalIconPath(shortcut.IconPath, out var iconPath))
                    ExtractIconEx(iconPath, shortcut.IconIndex, out large, out small, 1);
            }
            target = NativeLaunchService.ValidateWorkingDirectory(target);
            if (!Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase)) return "default";
            if (new DriveInfo(Path.GetPathRoot(target)!).DriveType == DriveType.Network) return "default";
            if (!File.Exists(target)) return "default";
            if (large == 0 && small == 0)
            {
                SHGetFileInfo(target, 0, out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100);
                large = info.Icon;
            }
            var handle = large != 0 ? large : small;
            if (handle == 0) return "default";
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = new MemoryStream(); encoder.Save(stream);
            var bytes = stream.ToArray();
            if (bytes.Length > 256 * 1024) return "default";
            var name = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant() + ".png";
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, name);
            if (!File.Exists(path)) File.WriteAllBytes(path, bytes);
            loaded[name] = "data:image/png;base64," + Convert.ToBase64String(bytes);
            return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ExternalException or ArgumentException or TargetValidationException or NotSupportedException or InvalidOperationException)
        { return "default"; }
        finally
        {
            if (large != 0) DestroyIcon(large);
            if (small != 0 && small != large) DestroyIcon(small);
        }
    }

    public Dictionary<string, string> GetIcons(AppSettings settings)
    {
        var result = new Dictionary<string, string>();
        foreach (var app in settings.AppEntries)
        {
            var name = app.Icon;
            if (name.Length != 68 || !name.EndsWith(".png", StringComparison.Ordinal) || !name[..64].All(Uri.IsHexDigit)) continue;
            try
            {
                if (!loaded.TryGetValue(name, out var data))
                {
                    var path = Path.Combine(directory, name);
                    if (!File.Exists(path) || new FileInfo(path).Length > 256 * 1024) continue;
                    data = "data:image/png;base64," + Convert.ToBase64String(File.ReadAllBytes(path));
                    loaded[name] = data;
                }
                result[app.Id] = data;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
        return result;
    }

    private static bool TryLocalIconPath(string? path, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrEmpty(path)) return false;
        // Use the shared syntactic local-path validator before touching any resource file.
        // No directory existence check here: icon sources may be DLL, EXE, or ICO files.
        try
        {
            canonical = NativeLaunchService.ValidateWorkingDirectory(path);
            if (new DriveInfo(Path.GetPathRoot(canonical)!).DriveType == DriveType.Network) return false;
            return File.Exists(canonical);
        }
        catch (TargetValidationException) { return false; }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("shell32.dll", EntryPoint = "ExtractIconExW", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string path, int index, out nint large, out nint small, uint count);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
}
