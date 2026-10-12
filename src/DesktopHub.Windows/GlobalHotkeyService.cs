using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace DesktopHub.Windows;

public sealed record HotkeyApplyResult(bool Success, string? Error, string DockHotkey, string WorkspaceHotkey);
public sealed record HotkeyBinding(string DisplayName, uint Modifiers, uint VirtualKey);

public interface IHotkeyRegistrationBackend
{
    bool Register(IntPtr hwnd, int id, uint modifiers, uint virtualKey, out int error);
    bool Unregister(IntPtr hwnd, int id);
}

/// <summary>Two independent RegisterHotKey bindings. New bindings are staged before old ones are released.</summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const uint NoRepeat = 0x4000;
    private readonly HwndSource source;
    private readonly Action<string> onRequested;
    private readonly IHotkeyRegistrationBackend backend;
    private readonly Dictionary<int, Registration> registrations = new();
    private int nextId = 0x3300;
    private bool disposed;
    public string DockHotkey { get; private set; } = "";
    public string WorkspaceHotkey { get; private set; } = "";

    public GlobalHotkeyService(Action<string> onRequested, IHotkeyRegistrationBackend? backend = null)
    {
        this.onRequested = onRequested;
        this.backend = backend ?? new WindowsHotkeyBackend();
        source = new HwndSource(new HwndSourceParameters("DesktopHub.Hotkeys")
        { ParentWindow = new IntPtr(-3), Width = 0, Height = 0, WindowStyle = 0 });
        source.AddHook(WindowProc);
    }

    public HotkeyApplyResult Apply(string dock, string workspace)
    {
        if (disposed) return Failure("快捷键服务已停止。");
        if (!TryParse(dock, out var dockBinding, out var error)) return Failure(error!);
        if (!TryParse(workspace, out var workspaceBinding, out error)) return Failure(error!);
        if (Same(dockBinding!, workspaceBinding!)) return Failure("Dock 与 Workspace 必须使用不同的快捷键。");

        var desired = new[] { (Surface: "dock", Binding: dockBinding!), (Surface: "workspace", Binding: workspaceBinding!) };
        var selected = new Dictionary<int, Registration>();
        var staged = new List<int>();
        foreach (var item in desired)
        {
            var existing = registrations.FirstOrDefault(pair => Same(pair.Value.Binding, item.Binding));
            if (existing.Value is not null)
            {
                selected.Add(existing.Key, new Registration(item.Surface, item.Binding));
                continue;
            }
            var id = NextId();
            while (registrations.ContainsKey(id) || selected.ContainsKey(id)) id = NextId();
            if (!backend.Register(source.Handle, id, item.Binding.Modifiers | NoRepeat, item.Binding.VirtualKey, out var code))
            {
                foreach (var registered in staged) backend.Unregister(source.Handle, registered);
                return Failure($"无法注册 {item.Binding.DisplayName}（Windows 错误 {code}，可能与其他程序冲突）。原有效快捷键已保留。");
            }
            staged.Add(id);
            selected.Add(id, new Registration(item.Surface, item.Binding));
        }
        foreach (var id in registrations.Keys.Where(id => !selected.ContainsKey(id))) backend.Unregister(source.Handle, id);
        registrations.Clear();
        foreach (var pair in selected) registrations.Add(pair.Key, pair.Value);
        DockHotkey = dockBinding!.DisplayName;
        WorkspaceHotkey = workspaceBinding!.DisplayName;
        return new(true, null, DockHotkey, WorkspaceHotkey);
    }

    public static bool TryParse(string? text, out HotkeyBinding? binding, out string? error)
    {
        binding = null;
        error = "快捷键须包含至少两个 Ctrl / Alt / Shift 修饰键，以及字母、数字或 F1–F24（F12 除外）。Windows 系统组合键不可用。";
        if (string.IsNullOrWhiteSpace(text) || text.Length > 64) return false;
        var parts = text.Split('+', StringSplitOptions.TrimEntries);
        if (parts.Length is < 3 or > 4 || parts.Any(string.IsNullOrEmpty)) return false;
        uint modifiers = 0;
        foreach (var part in parts[..^1])
        {
            var flag = part.ToUpperInvariant() switch { "CTRL" or "CONTROL" => 2u, "ALT" => 1u, "SHIFT" => 4u, _ => 0u };
            if (flag == 0 || (modifiers & flag) != 0) return false;
            modifiers |= flag;
        }
        var key = parts[^1].ToUpperInvariant();
        uint virtualKey;
        if (key.Length == 1 && key[0] is >= 'A' and <= 'Z' or >= '0' and <= '9') virtualKey = key[0];
        else if (key.StartsWith('F') && int.TryParse(key[1..], out var function) && function is >= 1 and <= 24 && function != 12)
        { virtualKey = (uint)(0x70 + function - 1); key = "F" + function; }
        else return false;
        var normalized = ((modifiers & 2) != 0 ? "Ctrl+" : "") + ((modifiers & 1) != 0 ? "Alt+" : "") + ((modifiers & 4) != 0 ? "Shift+" : "") + key;
        binding = new(normalized, modifiers, virtualKey);
        error = null;
        return true;
    }

    private static bool Same(HotkeyBinding left, HotkeyBinding right) => left.Modifiers == right.Modifiers && left.VirtualKey == right.VirtualKey;
    private int NextId()
    {
        var id = nextId++;
        if (nextId > 0xbfff) nextId = 0x3300;
        return id;
    }
    private HotkeyApplyResult Failure(string error) => new(false, error, DockHotkey, WorkspaceHotkey);
    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!disposed && message == 0x0312 && registrations.TryGetValue(wParam.ToInt32(), out var registration))
        {
            handled = true;
            onRequested(registration.Surface);
        }
        return IntPtr.Zero;
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        foreach (var id in registrations.Keys) backend.Unregister(source.Handle, id);
        registrations.Clear();
        source.RemoveHook(WindowProc);
        source.Dispose();
    }
    private sealed record Registration(string Surface, HotkeyBinding Binding);
    private sealed class WindowsHotkeyBackend : IHotkeyRegistrationBackend
    {
        public bool Register(IntPtr hwnd, int id, uint modifiers, uint virtualKey, out int error)
        {
            var result = RegisterHotKey(hwnd, id, modifiers, virtualKey);
            error = result ? 0 : Marshal.GetLastWin32Error();
            return result;
        }
        public bool Unregister(IntPtr hwnd, int id) => UnregisterHotKey(hwnd, id);
    }
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
