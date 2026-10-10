using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DesktopHub.Core;

/// <summary>Windows operations are asynchronous, bounded, and separate from the scheduler.</summary>
public sealed class WindowsSceneActionRunner(NativeLaunchService launcher) : ISceneActionRunner
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> launchGates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (int Pid, long StartTicks)> submittedProcesses = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (int Pid, long StartTicks)> runningScripts = new(StringComparer.Ordinal);

    public async Task<ActionOutcome> LaunchAsync(SceneAction action, AppSettings settings, CancellationToken cancellation,
        Action<ActionOutcome> progress)
    {
        try
        {
            if (action.Type == "application")
            {
                var app = action.AppEntryId is { } id ? settings.AppEntries.FirstOrDefault(app => app.Id == id)
                    : new AppEntry { Target = action.Target, Arguments = action.Arguments, WorkingDirectory = action.WorkingDirectory };
                if (app is null) return Failed("reference_invalid", "引用的应用已移除，请编辑动作重新关联。");
                var resolved = launcher.ResolveEntry(app);
                var serial = launchGates.GetOrAdd(resolved.Target, _ => new SemaphoreSlim(1));
                await serial.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    var (ids, uncertain) = FindProcesses(resolved.Target);
                    // A newly created app can briefly lack readable MainModule metadata. Our own
                    // validated launch receipt plus PID/start time closes that duplicate-start race.
                    if (submittedProcesses.TryGetValue(resolved.Target, out var receipt))
                    {
                        try
                        {
                            using var submitted = Process.GetProcessById(receipt.Pid);
                            if (!submitted.HasExited && submitted.StartTime.ToUniversalTime().Ticks == receipt.StartTicks && !ids.Contains(receipt.Pid)) ids.Add(receipt.Pid);
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
                    }
                    if (action.RepeatPolicy != "always" && ids.Count > 0)
                        return new(true, true, "already_running", $"已运行，跳过启动（匹配目标路径，{ids.Count} 个实例）", ids[0], resolved.Target);
                    var result = await Task.Run(() => launcher.LaunchEntry(app), cancellation).WaitAsync(TimeSpan.FromMilliseconds(action.TimeoutMs), cancellation).ConfigureAwait(false);
                    if (result.Success && result.ProcessId is { } startedPid)
                    {
                        try { using var started = Process.GetProcessById(startedPid); submittedProcesses[resolved.Target] = (startedPid, started.StartTime.ToUniversalTime().Ticks); }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
                    }
                    var outcome = new ActionOutcome(result.Success, false, result.Success ? "submitted" : result.Code,
                        result.Success ? "启动请求已提交" + (uncertain ? "；部分同名进程路径不可读取，未据名称判定已运行" : "") : result.Message,
                        result.ProcessId, resolved.Target, uncertain ? "同名进程存在不可读路径；没有按进程名跳过，无法完全保证不重复启动。" : "");
                    progress(outcome);
                    return outcome;
                }
                finally { serial.Release(); }
            }
            if (action.Type == "script")
            {
                var key = "script/" + action.Id + "/" + action.Target;
                var serial = launchGates.GetOrAdd(key, _ => new SemaphoreSlim(1));
                await serial.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    if (runningScripts.TryGetValue(key, out var receipt))
                    {
                        try
                        {
                            using var previous = Process.GetProcessById(receipt.Pid);
                            if (!previous.HasExited && previous.StartTime.ToUniversalTime().Ticks == receipt.StartTicks)
                                return Failed("previous_script_running", "此动作的上一次脚本仍在运行，请检查可见终端；结束后可再次执行。");
                        }
                        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
                    }
                    return await ScriptAsync(action, cancellation, update =>
                    {
                        if (update.ProcessId is { } pid)
                        {
                            try { using var process = Process.GetProcessById(pid); runningScripts[key] = (pid, process.StartTime.ToUniversalTime().Ticks); }
                            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { }
                        }
                        progress(update);
                    }).ConfigureAwait(false);
                }
                finally { serial.Release(); }
            }
            string target;
            if (action.Type == "web")
            {
                if (!Uri.TryCreate(action.Target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
                    return Failed("invalid_url", "请输入有效 HTTP / HTTPS 地址。");
                target = uri.AbsoluteUri;
            }
            else
            {
                target = NativeLaunchService.ValidateLocalPath(action.Target);
                if (action.Type == "folder" ? !Directory.Exists(target) : !File.Exists(target))
                    return Failed("target_missing", "目标" + (action.Type == "folder" ? "文件夹" : "文件") + "不存在或不可访问。");
            }
            var start = action.Type == "folder"
                ? new ProcessStartInfo { FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), UseShellExecute = true }
                : new ProcessStartInfo { FileName = target, UseShellExecute = true };
            if (action.Type == "folder") start.ArgumentList.Add(target);
            using var process = await Task.Run(() => Process.Start(start), cancellation).WaitAsync(TimeSpan.FromMilliseconds(action.TimeoutMs), cancellation).ConfigureAwait(false);
            var opened = new ActionOutcome(true, false, "submitted", "打开请求已提交给 Windows" , process?.Id,
                action.Type == "folder" ? start.FileName : "");
            progress(opened); return opened;
        }
        catch (TargetValidationException ex) { return Failed(ex.Code, ex.Message); }
        catch (TimeoutException) { return Failed("launch_timeout", "Windows 启动请求超时；请检查可见的系统对话框。未自动终止目标进程。"); }
        catch (Win32Exception ex) { return Failed("windows_error_" + ex.NativeErrorCode, $"Windows 拒绝执行（{ex.NativeErrorCode}）：{ex.Message}"); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        { return Failed("launch_error", ex.Message); }
    }

    private static async Task<ActionOutcome> ScriptAsync(SceneAction action, CancellationToken cancellation, Action<ActionOutcome> progress)
    {
        var path = NativeLaunchService.ValidateLocalPath(action.Target);
        if (!File.Exists(path)) return Failed("target_missing", "脚本文件不存在，请重新选择。");
        var directory = NativeLaunchService.ValidateWorkingDirectory(action.WorkingDirectory, true);
        var extension = Path.GetExtension(path).ToLowerInvariant();
        var start = new ProcessStartInfo { UseShellExecute = true, CreateNoWindow = false,
            WorkingDirectory = directory.Length > 0 ? directory : Path.GetDirectoryName(path)! };
        if (action.RunAsAdministrator) start.Verb = "runas";
        if (extension == ".ps1")
        {
            start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-File"); start.ArgumentList.Add(path);
            foreach (var argument in action.ArgumentList) { NativeLaunchService.ValidateArguments(argument); start.ArgumentList.Add(argument); }
        }
        else if (extension is ".bat" or ".cmd")
        {
            // cmd.exe has a separate parser. Reject expansion/control syntax in both path and tokens.
            static bool Unsafe(string value) => value.Any(character => char.IsControl(character) || "\"%!?^&|<>()".Contains(character));
            if (Unsafe(path) || action.ArgumentList.Any(Unsafe)) return Failed("unsafe_batch_arguments", "BAT / CMD 路径与参数不能含命令控制或变量展开字符；可改用 PowerShell 脚本。");
            start.FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            start.Arguments = "/d /s /c \"\"" + path + "\"" + string.Concat(action.ArgumentList.Select(argument => " \"" + argument + "\"")) + "\"";
        }
        else return Failed("unsupported_script", "仅支持 BAT、CMD、PowerShell 脚本。");
        using var process = await Task.Run(() => Process.Start(start), cancellation).WaitAsync(TimeSpan.FromMilliseconds(action.TimeoutMs), cancellation).ConfigureAwait(false);
        if (process is null) return Failed("script_unobservable", "Windows 未返回脚本进程，无法确认结果；请检查系统提示。");
        var submitted = new ActionOutcome(true, false, "submitted", "脚本已启动，等待退出结果（交互终端可见）", process.Id, start.FileName);
        progress(submitted);
        try { await process.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromMilliseconds(action.TimeoutMs), cancellation).ConfigureAwait(false); }
        catch (TimeoutException) { return submitted with { Success = false, Code = "script_timeout", Message = "脚本等待退出超时，请查看终端。未自动终止脚本或绕过系统权限。" }; }
        return submitted with { Success = process.ExitCode == 0, Code = process.ExitCode == 0 ? "script_completed" : "script_exit_error",
            Message = process.ExitCode == 0 ? "脚本已完成（退出码 0）" : $"脚本执行失败（退出码 {process.ExitCode}），请检查终端。" };
    }

    public async Task<ActionOutcome> ObserveAsync(SceneAction action, ActionOutcome launched, string condition,
        int timeoutMs, CancellationToken cancellation)
    {
        var target = action.WaitTargetPath.Length > 0 ? action.WaitTargetPath : launched.TargetPath;
        if (target.Length == 0) return launched with { Success = false, Skipped = false, Code = "wait_target_required", Message = "无法识别目标进程；请在高级设置指定要等待的 EXE 路径。" };
        var watch = Stopwatch.StartNew();
        var uncertain = false;
        do
        {
            cancellation.ThrowIfCancellationRequested();
            var (ids, inaccessible) = FindProcesses(target); uncertain |= inaccessible;
            var matched = condition == "process" ? ids.FirstOrDefault() : ids.FirstOrDefault(pid => HasWindow(pid, action.WindowTitle));
            if (matched > 0) return launched with { Success = true, Code = condition + "_appeared", ProcessId = matched, TargetPath = target,
                Message = (launched.Skipped ? "已运行，跳过启动；" : "") + (condition == "process" ? "目标进程已出现；尚未确认窗口就绪" : "目标窗口已出现") };
            await Task.Delay(Math.Min(100, Math.Max(1, timeoutMs - (int)watch.ElapsedMilliseconds)), cancellation).ConfigureAwait(false);
        } while (watch.ElapsedMilliseconds < timeoutMs);
        return launched with { Success = false, Skipped = false, Code = "wait_" + condition + "_timeout",
            Message = $"等待目标{(condition == "process" ? "进程" : "窗口")}超时（{timeoutMs / 1000.0:g} 秒）" + (uncertain ? "；部分进程路径无权读取" : "") };
    }

    public static (List<int> Ids, bool Uncertain) FindProcesses(string target)
    {
        var ids = new List<int>(); var uncertain = false;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(target)))
        {
            using (process)
            {
                try
                {
                    if (!process.HasExited && process.MainModule?.FileName is { } path &&
                        Path.GetFullPath(path).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) ids.Add(process.Id);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException) { uncertain = true; }
            }
        }
        return (ids, uncertain);
    }
    private static bool HasWindow(int pid, string title)
    {
        var found = false;
        EnumWindows((handle, _) =>
        {
            GetWindowThreadProcessId(handle, out var owner);
            if (owner == pid && IsWindowVisible(handle))
            {
                var text = new StringBuilder(1024); GetWindowText(handle, text, text.Capacity);
                if (title.Length == 0 || text.ToString().Contains(title, StringComparison.OrdinalIgnoreCase)) { found = true; return false; }
            }
            return true;
        }, 0);
        return found;
    }
    private static ActionOutcome Failed(string code, string message) => new(false, false, code, message);
    private delegate bool EnumWindowCallback(nint handle, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowCallback callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint handle);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint handle, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint handle, StringBuilder text, int length);
}
