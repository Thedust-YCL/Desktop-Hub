using System.Text.Json;

namespace DesktopHub.Windows;

// This boundary admits target-state system operations only. There is no command,
// executable path, registry path or arbitrary shell payload coming from the WebView.
internal sealed class SystemBridge(HubApplication host, HubWindow window)
{
    public object Dispatch(string type, JsonElement payload)
    {
        if (window.Surface != "workspace") throw new BridgeException("WRONG_SURFACE", "请在工作空间修改系统设置。");
        object operation;
        switch (type)
        {
            case "system.getState":
                Keys(payload); return host.State(window);
            case "system.setDesktopIcons":
                Keys(payload, "visible", "firstHidePolicy");
                var visible = Boolean(payload, "visible");
                var policy = payload.TryGetProperty("firstHidePolicy", out _) ? Policy(payload, "firstHidePolicy") : null;
                if (visible && policy is not null) throw new BridgeException("INVALID_PARAMS", "显示桌面图标无需首次隐藏策略。");
                operation = host.DesktopIcons.SetVisible(visible, policy); break;
            case "system.setIconExitPolicy":
                Keys(payload, "policy"); operation = host.DesktopIcons.SetExitPolicy(Policy(payload, "policy")); break;
            case "system.setStartup":
                Keys(payload, "enabled"); operation = host.Startup.SetEnabled(Boolean(payload, "enabled")); break;
            case "system.setHotkeys":
                Keys(payload, "dock", "workspace"); operation = host.SetHotkeys(Text(payload, "dock", 64), Text(payload, "workspace", 64)); break;
            default: throw new BridgeException("UNKNOWN_MESSAGE", "不支持此系统操作。");
        }
        host.SettingsChanged();
        return new { state = host.State(window), operation };
    }

    internal static void ValidateBlocklist(JsonElement list)
    {
        if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 100)
            throw new BridgeException("INVALID_PARAMS", "屏蔽名单必须是最多 100 个程序名称的列表。");
        foreach (var value in list.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 4 and <= 120 } name ||
                !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || name.Any(char.IsControl) ||
                name.IndexOfAny(['\\', '/', ':', '*', '?', '"', '<', '>', '|']) >= 0 || name != name.Trim())
                throw new BridgeException("INVALID_PARAMS", "请输入程序文件名，例如 game.exe，不要填写路径或命令。");
        }
    }

    private static string Policy(JsonElement payload, string key)
    {
        var value = Text(payload, key, 16);
        return value is "restore" or "keepHidden" ? value : throw new BridgeException("INVALID_PARAMS", "请选择退出后恢复或保持隐藏。");
    }
    private static bool Boolean(JsonElement payload, string key)
    {
        if (!payload.TryGetProperty(key, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new BridgeException("INVALID_PARAMS", "参数必须是布尔值：" + key);
        return value.GetBoolean();
    }
    private static string Text(JsonElement payload, string key, int max)
    {
        if (!payload.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 } text || text.Length > max || text.Any(char.IsControl))
            throw new BridgeException("INVALID_PARAMS", "参数无效：" + key);
        return text;
    }
    private static void Keys(JsonElement payload, params string[] allowed)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new BridgeException("INVALID_PARAMS", "参数必须是对象。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in payload.EnumerateObject())
            if (!allowed.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name))
                throw new BridgeException("INVALID_PARAMS", "未知或重复参数：" + field.Name);
    }
}
