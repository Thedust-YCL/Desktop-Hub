using System.ComponentModel;
using System.Diagnostics;

namespace DesktopHub.Core;

/// <summary>Launches registered local EXE/LNK entries without shell execution, elevation, or command hosts.</summary>
public sealed class NativeLaunchService(IShortcutResolver? shortcutResolver = null)
{
    private static readonly HashSet<string> DeniedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "powershell", "powershell_ise", "pwsh", "cmd", "wscript", "cscript", "mshta", "rundll32", "regsvr32",
        "dotnet", "python", "pythonw", "python3", "node", "nodejs", "bash", "sh", "zsh", "wsl", "git-bash",
        "csi", "fsi", "perl", "ruby"
    };

    public AppEntry RegisterApp(string fullPath)
    {
        var selected = ValidateAppPath(fullPath);
        var kind = Path.GetExtension(selected).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? "lnk" : "exe";
        var resolved = ResolveExecutable(selected);
        ValidateArguments(resolved.Arguments);
        var workingDirectory = ValidateWorkingDirectory(resolved.WorkingDirectory, requireExists: true);
        return new AppEntry
        {
            Id = Guid.NewGuid().ToString("D"), Target = selected, EntryKind = kind,
            Name = Path.GetFileNameWithoutExtension(selected), Arguments = resolved.Arguments,
            WorkingDirectory = string.IsNullOrEmpty(workingDirectory) ? Path.GetDirectoryName(resolved.Target)! : workingDirectory,
            Icon = "default", PinnedToDock = true
        };
    }

    public LaunchResult LaunchApp(AppSettings settings, string appId)
    {
        if (!Guid.TryParseExact(appId, "D", out var requestedId) || requestedId == Guid.Empty)
            return Failed("app_not_registered", "请求的应用没有有效注册标识。");
        var app = settings.AppEntries?.FirstOrDefault(entry => entry is not null &&
            Guid.TryParseExact(entry.Id, "D", out var savedId) && savedId == requestedId);
        if (app is null) return Failed("app_not_registered", "应用记录不存在，可能已从应用库移除。");
        return LaunchEntry(app);
    }

    // Kept solely for M1 test/protocol compatibility. New application launches always use AppEntry IDs.
    public LaunchTarget RegisterTarget(string fullPath)
    {
        var canonical = ValidatePath(fullPath);
        ValidatePortableExecutable(canonical);
        return new LaunchTarget { Id = Guid.NewGuid().ToString("D"), Path = canonical,
            DisplayName = Path.GetFileNameWithoutExtension(canonical) };
    }

    public LaunchResult Launch(AppSettings settings, string targetId)
    {
        if (settings.LaunchTarget is not { } target)
            return Failed("target_not_selected", "请先通过文件选择器选择本地程序。");
        if (!Guid.TryParseExact(targetId, "D", out var requestedId) || requestedId == Guid.Empty ||
            !Guid.TryParseExact(target.Id, "D", out var savedId) || savedId != requestedId)
            return Failed("target_not_registered", "请求的程序未经过本机选择注册，无法启动。");
        return LaunchEntry(new AppEntry { Id = target.Id, Target = target.Path });
    }

    public LaunchResult LaunchEntry(AppEntry app)
    {
        try
        {
            // Resolve the selected LNK on every launch, and validate the resulting executable again.
            // Saved arguments/work directory are explicit overrides, including deliberately empty arguments.
            var resolved = ResolveExecutable(ValidateAppPath(app.Target));
            ValidateArguments(app.Arguments);
            var workingDirectory = ValidateWorkingDirectory(app.WorkingDirectory, requireExists: true);
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = resolved.Target,
                Arguments = app.Arguments,
                WorkingDirectory = string.IsNullOrEmpty(workingDirectory) ? Path.GetDirectoryName(resolved.Target)! : workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = false
            });
            if (process is null) return Failed("launch_failed", "Windows 未返回启动进程。");
            return new LaunchResult(true, process.Id, "started", $"Windows 已创建进程（PID {process.Id}）。程序是否完成初始化需由后续阶段检测。");
        }
        catch (TargetValidationException ex) { return Failed(ex.Code, ex.Message); }
        catch (Win32Exception ex)
        {
            var code = ex.NativeErrorCode switch
            {
                2 or 3 => "target_missing", 5 => "access_denied", 193 or 216 => "invalid_executable",
                740 => "elevation_required", _ => "launch_failed"
            };
            return Failed(code, $"Windows 启动失败（错误 {ex.NativeErrorCode}）：{ex.Message}");
        }
        catch (UnauthorizedAccessException ex) { return Failed("access_denied", $"无权读取或启动程序：{ex.Message}"); }
        catch (IOException ex) { return Failed("io_error", $"读取程序失败：{ex.Message}"); }
        catch (InvalidOperationException ex) { return Failed("launch_failed", $"无法获取启动结果：{ex.Message}"); }
        catch (ArgumentException ex) { return Failed("invalid_target", $"启动配置无效：{ex.Message}"); }
    }

    private ResolvedShortcut ResolveExecutable(string selected)
    {
        if (Path.GetExtension(selected).Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            var exe = ValidatePath(selected);
            ValidatePortableExecutable(exe);
            return new ResolvedShortcut(exe, "", Path.GetDirectoryName(exe)!);
        }
        if (!File.Exists(selected)) throw new TargetValidationException("target_missing", "快捷方式已经不存在，请编辑应用重新选择。");
        if (shortcutResolver is null)
            throw new TargetValidationException("shortcut_unsupported", "当前宿主未提供 Windows 快捷方式解析，暂不支持此 LNK。");
        var resolved = shortcutResolver.Resolve(selected);
        var target = ValidatePath(resolved.Target);
        ValidatePortableExecutable(target);
        ValidateArguments(resolved.Arguments);
        ValidateWorkingDirectory(resolved.WorkingDirectory, requireExists: false);
        return resolved with { Target = target };
    }

    public ResolvedShortcut ResolveEntry(AppEntry app) => ResolveExecutable(ValidateAppPath(app.Target));

    internal static string ValidatePath(string? fullPath, bool checkDrive = true)
    {
        var canonical = ValidateLocalPath(fullPath, "invalid_target", checkDrive);
        if (!Path.GetExtension(canonical).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new TargetValidationException("unsupported_target", "仅支持普通 .exe 程序，不支持脚本、协议或 Store/MSIX 激活目标。");
        var executableName = Path.GetFileNameWithoutExtension(canonical);
        if (DeniedHosts.Contains(executableName) || IsVersionedPythonHost(executableName))
            throw new TargetValidationException("blocked_host", "禁止选择命令、脚本或系统执行宿主。");
        return canonical;
    }

    internal static string ValidateAppPath(string? fullPath, bool checkDrive = true)
    {
        var canonical = ValidateLocalPath(fullPath, "invalid_target", checkDrive);
        var extension = Path.GetExtension(canonical);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) return ValidatePath(canonical, checkDrive);
        if (extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) return canonical;
        throw new TargetValidationException("unsupported_target", "仅支持本地 EXE 和指向普通 EXE 的 Windows LNK 快捷方式。");
    }

    public static void ValidateArguments(string? arguments)
    {
        if (arguments is null || arguments.Length > 16384 || arguments.Any(char.IsControl))
            throw new TargetValidationException("invalid_arguments", "启动参数不能包含控制字符，且不能超过 16384 个字符。");
    }

    public static string ValidateWorkingDirectory(string? directory, bool requireExists = false, bool checkDrive = true)
    {
        if (string.IsNullOrEmpty(directory)) return "";
        var canonical = ValidateLocalPath(directory, "invalid_working_directory", checkDrive);
        if (requireExists && !Directory.Exists(canonical))
            throw new TargetValidationException("working_directory_missing", "工作目录不存在或不可访问，请编辑应用修复。");
        return canonical;
    }

    public static string ValidateLocalPath(string? fullPath, string code = "invalid_target", bool checkDrive = true)
    {
        if (string.IsNullOrWhiteSpace(fullPath) || fullPath.Length > 32767 || fullPath.Any(char.IsControl) ||
            fullPath.StartsWith('\\') || fullPath.StartsWith('/') || fullPath.Length < 3 ||
            !char.IsAsciiLetter(fullPath[0]) || fullPath[1] != ':' || (fullPath[2] != '\\' && fullPath[2] != '/') ||
            fullPath[2..].Contains(':') || fullPath.IndexOfAny(['"', '<', '>', '|', '?', '*']) >= 0)
            throw new TargetValidationException(code, "仅允许本地磁盘上的绝对路径，不支持 UNC、设备、协议或命令。");
        string canonical;
        try { canonical = Path.GetFullPath(fullPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { throw new TargetValidationException(code, "本地路径无效。"); }
        if (canonical[3..].Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Any(segment =>
            IsDosDevice(segment.Split('.')[0]) || segment.EndsWith(' ') || segment.EndsWith('.')))
            throw new TargetValidationException(code, "不能选择 Windows 设备路径或含歧义的目录名称。");
        // Persistence checks only syntax: a disconnected/remapped drive must not discard library records.
        if (checkDrive && new DriveInfo(Path.GetPathRoot(canonical)!).DriveType == DriveType.Network)
            throw new TargetValidationException(code, "不支持网络映射磁盘上的程序或工作目录。");
        return canonical;
    }

    private static bool IsDosDevice(string name)
    {
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase) || name.Equals("NUL", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("PRN", StringComparison.OrdinalIgnoreCase) || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)) return true;
        return name.Length == 4 && name[3] is >= '1' and <= '9' &&
            (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsVersionedPythonHost(string name)
    {
        if (!name.StartsWith("python", StringComparison.OrdinalIgnoreCase)) return false;
        var version = name[6..];
        if (version.EndsWith('w')) version = version[..^1];
        return version.Length > 0 && version.All(character => char.IsAsciiDigit(character) || character == '.');
    }

    private static void ValidatePortableExecutable(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 64 || reader.ReadUInt16() != 0x5A4D) InvalidPe();
            stream.Position = 0x3C;
            var offset = reader.ReadInt32();
            if (offset < 64 || offset > stream.Length - 26) InvalidPe();
            stream.Position = offset;
            if (reader.ReadUInt32() != 0x00004550) InvalidPe();
            var machine = reader.ReadUInt16();
            var sections = reader.ReadUInt16();
            stream.Position = offset + 20;
            var optionalSize = reader.ReadUInt16();
            var characteristics = reader.ReadUInt16();
            var magic = reader.ReadUInt16();
            if (machine is not (0x014c or 0x8664 or 0xaa64) || sections == 0 || optionalSize < 96 ||
                offset + 24L + optionalSize > stream.Length || magic is not (0x010b or 0x020b) ||
                (characteristics & 0x0002) == 0 || (characteristics & 0x2000) != 0) InvalidPe();
        }
        catch (FileNotFoundException) { throw new TargetValidationException("target_missing", "所选程序已经不存在，请编辑应用重新选择。"); }
        catch (DirectoryNotFoundException) { throw new TargetValidationException("target_missing", "所选程序目录已经不存在，请编辑应用重新选择。"); }
        catch (EndOfStreamException) { InvalidPe(); }
    }

    private static void InvalidPe() => throw new TargetValidationException("invalid_executable", "所选文件不是有效的 Windows 可执行程序（PE）。");
    private static LaunchResult Failed(string code, string message) => new(false, null, code, message);
}
