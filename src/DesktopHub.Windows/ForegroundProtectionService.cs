using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DesktopHub.Windows;

public sealed record ForegroundProtectionResult(bool IsProtected, string? Reason, IntPtr WindowHandle, string? ProcessName);

/// <summary>Read-only foreground process and monitor geometry guard; no hooks or process manipulation.</summary>
public sealed class ForegroundProtectionService
{
    public ForegroundProtectionResult Evaluate(IEnumerable<string> blocklist)
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero) return new(false, null, window, null);
        NativeMethods.GetWindowThreadProcessId(window, out var pid);
        if (pid == Environment.ProcessId) return new(false, null, window, "DesktopHub");
        string? processName = null;
        try { using var process = Process.GetProcessById((int)pid); processName = process.ProcessName; }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
        if (processName is not null && blocklist.Any(entry => ProcessMatches(entry, processName)))
            return new(true, "foreground-blocklist", window, processName);

        // The shell desktop itself fills its monitor but is not a full-screen game.
        if (IsShellDesktop(window)) return new(false, null, window, processName);
        var info = new NativeMethods.MonitorInfo { Size = Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        var monitor = NativeMethods.MonitorFromWindow(window, 2);
        if (NativeMethods.GetMonitorInfo(monitor, ref info) && NativeMethods.GetWindowRect(window, out var rect) &&
            CoversMonitor(rect.Left, rect.Top, rect.Right, rect.Bottom, info.Monitor.Left, info.Monitor.Top, info.Monitor.Right, info.Monitor.Bottom))
            return new(true, "foreground-fullscreen", window, processName);
        return new(false, null, window, processName);
    }

    public static bool ProcessMatches(string? blockedName, string processName)
    {
        if (string.IsNullOrWhiteSpace(blockedName)) return false;
        var normalized = blockedName.Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) normalized = normalized[..^4];
        return normalized.Equals(processName, StringComparison.OrdinalIgnoreCase);
    }
    public static bool CoversMonitor(int left, int top, int right, int bottom, int monitorLeft, int monitorTop, int monitorRight, int monitorBottom) =>
        monitorRight > monitorLeft && monitorBottom > monitorTop && left <= monitorLeft && top <= monitorTop && right >= monitorRight && bottom >= monitorBottom;

    private static bool IsShellDesktop(IntPtr hwnd)
    {
        var className = new System.Text.StringBuilder(128);
        GetClassName(hwnd, className, className.Capacity);
        return className.ToString() is "Progman" or "WorkerW";
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder className, int maxCount);
}
