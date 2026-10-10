using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopHub.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace DesktopHub.Windows;

internal sealed class BridgeException(string code, string message) : Exception(message) { public string Code { get; } = code; }

internal sealed class NativeBridge(HubApplication host, HubWindow window)
{
    private readonly HashSet<string> pending = new();
    private static readonly HashSet<string> Editable = new(StringComparer.Ordinal)
    { "theme", "reducedMotion", "transparency", "miniPosition", "miniSize", "miniMode", "dockPosition", "dockSize", "dockIconSize", "hoverEnabled", "hoverDelay", "allowForeground" };
    private static readonly HashSet<string> Pages = new(StringComparer.Ordinal)
    { "home", "scenes", "library", "tags", "favorites", "search", "settings" };
    private bool pickerOpen, launching;
    private readonly ResourceBridge resources = new(host, window);

    public async Task ReceiveAsync(CoreWebView2WebMessageReceivedEventArgs message)
    {
        string? id = null;
        try
        {
            if (!HubWindow.IsAllowedPage(message.Source)) return;
            var json = message.WebMessageAsJson;
            if (json.Length > 1024 * 1024) throw new BridgeException("MESSAGE_TOO_LARGE", "请求过大。");
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 16 });
            var root = document.RootElement;
            RequireKeys(root, "version", "id", "type", "payload");
            if (!root.TryGetProperty("id", out var idValue) || idValue.ValueKind != JsonValueKind.String) throw new BridgeException("INVALID_REQUEST", "请求缺少编号。");
            id = idValue.GetString();
            if (id is null || id.Length is < 1 or > 96) throw new BridgeException("INVALID_REQUEST", "请求编号无效。");
            if (!root.TryGetProperty("version", out var version) || !version.TryGetInt32(out var protocol) || protocol != 1)
                throw new BridgeException("UNSUPPORTED_VERSION", "通信版本不受支持。");
            if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
                throw new BridgeException("INVALID_REQUEST", "请求格式无效。");
            if (json.Length > 16384 && type.GetString() is not ("apps.update" or "apps.reorder" or "launcher.setPins" or "scenes.save" or "scenes.reorder"))
                throw new BridgeException("MESSAGE_TOO_LARGE", "请求过大。");
            if (pending.Count >= 16 || !pending.Add(id)) throw new BridgeException("BUSY", "请等待当前操作完成。");
            try
            {
                host.Log("BridgeRequest", type.GetString()!);
                var result = await DispatchAsync(type.GetString()!, payload);
                Reply(new { version = 1, id, ok = true, result });
            }
            finally { pending.Remove(id); }
        }
        catch (Exception e)
        {
            var code = e switch { BridgeException b => b.Code, TargetValidationException t => t.Code,
                JsonException => "INVALID_PARAMS", SettingsValidationException => "INVALID_PARAMS", ArgumentException => "INVALID_PARAMS", _ => "NATIVE_ERROR" };
            host.Log("BridgeError", code + ": " + e.Message);
            Reply(new { version = 1, id, ok = false, error = new { code, message = e.Message } });
        }
    }

    private async Task<object> DispatchAsync(string type, JsonElement payload)
    {
        if (type.StartsWith("resources.", StringComparison.Ordinal)) return await resources.DispatchAsync(type, payload);
        switch (type)
        {
            case "app.getState": Empty(payload); return host.State(window);
            case "settings.get": Empty(payload); return new { settings = host.Store.Current, effectiveTheme = host.EffectiveTheme };
            case "settings.save":
                RequireKeys(payload, "settings");
                var patch = payload.GetProperty("settings");
                if (patch.ValueKind != JsonValueKind.Object) throw new BridgeException("INVALID_PARAMS", "设置必须是对象。");
                if (window.Surface != "workspace" && (window.Surface != "dock" ||
                    !patch.EnumerateObject().Any() || patch.EnumerateObject().Any(field => field.Name != "dockIconSize")))
                    throw new BridgeException("WRONG_SURFACE", "请在工作空间修改此设置。");
                var patchKeys = new HashSet<string>(StringComparer.Ordinal);
                var merged = JsonSerializer.SerializeToNode(host.Store.Current, HubApplication.Json)!.AsObject();
                foreach (var field in patch.EnumerateObject())
                {
                    if (!Editable.Contains(field.Name)) throw new BridgeException("FORBIDDEN_SETTING", "此设置不能由界面修改：" + field.Name);
                    if (!patchKeys.Add(field.Name)) throw new BridgeException("INVALID_PARAMS", "重复设置：" + field.Name);
                    merged[field.Name] = JsonNode.Parse(field.Value.GetRawText());
                }
                var settings = merged.Deserialize<AppSettings>(HubApplication.Json)!;
                host.Store.Save(settings); host.SettingsChanged();
                return new { settings = host.Store.Current, effectiveTheme = host.EffectiveTheme };
            case "window.openWorkspace":
                RequireKeys(payload, "page");
                string? page = payload.TryGetProperty("page", out _) ? String(payload, "page", 24) : null;
                if (page is not null && !Pages.Contains(page)) throw new BridgeException("INVALID_PARAMS", "页面不存在。");
                await host.OpenWorkspaceAsync(page); return new { opened = true };
            case "window.closeWorkspace": RequireWorkspace(); Empty(payload); host.CloseWorkspace(); return new { closed = true, focusRestore = host.FocusRestoreResult };
            case "window.openDock": Empty(payload); await host.OpenDockAsync(); return new { opened = true };
            case "window.closeDock": Empty(payload); host.CloseDock(); return new { closed = true };
            case "window.resizeSurface": RequireKeys(payload, "width", "height"); window.ResizeSurface(payload.GetProperty("width").GetDouble(), payload.GetProperty("height").GetDouble()); return new { resized = true };
            case "window.drag":
                Empty(payload);
                _ = window.Dispatcher.BeginInvoke(window.BeginNativeDrag);
                return new { dragging = true };
            case "apps.add":
                RequireWorkspace(); Empty(payload); return await ChooseAppAsync();
            case "apps.replaceTarget":
                RequireWorkspace(); RequireKeys(payload, "appId");
                return await ChooseAppAsync(String(payload, "appId", 96));
            case "apps.update":
                RequireWorkspace(); RequireKeys(payload, "appId", "name", "arguments", "workingDirectory", "pinnedToMini", "pinnedToDock");
                var updated = host.AppLibrary.Update(String(payload, "appId", 96), String(payload, "name", 200),
                    OptionalString(payload, "arguments", 32767), OptionalString(payload, "workingDirectory", 32767),
                    Boolean(payload, "pinnedToMini"), Boolean(payload, "pinnedToDock"));
                host.SettingsChanged(); return new { settings = updated };
            case "apps.remove":
                RequireWorkspace(); RequireKeys(payload, "appId", "confirmReferences");
                var removeId = String(payload, "appId", 96);
                if (host.Store.Current.Scenes.Any(scene => scene.Actions.Any(action => action.AppEntryId == removeId)) &&
                    (!payload.TryGetProperty("confirmReferences", out var confirmed) || confirmed.ValueKind != JsonValueKind.True))
                    throw new BridgeException("REFERENCES_REQUIRE_CONFIRMATION", "此应用被场景引用，请查看受影响场景后确认移除。");
                var removed = host.AppLibrary.Remove(removeId);
                host.SettingsChanged(); return new { settings = removed };
            case "scenes.save":
                RequireWorkspace(); RequireKeys(payload, "scene");
                var scene = payload.GetProperty("scene").Deserialize<Scene>(SettingsStore.JsonOptions)
                    ?? throw new BridgeException("INVALID_PARAMS", "场景内容为空。");
                var sceneSaved = host.SceneLibrary.Save(scene);
                host.SettingsChanged(); return new { settings = sceneSaved };
            case "scenes.remove":
                RequireWorkspace(); RequireKeys(payload, "sceneId");
                var sceneRemoved = host.SceneLibrary.Remove(String(payload, "sceneId", 96));
                host.SettingsChanged(); return new { settings = sceneRemoved };
            case "scenes.reorder":
                RequireWorkspace(); RequireKeys(payload, "sceneIds");
                var sceneOrdered = host.SceneLibrary.Reorder(StringArray(payload, "sceneIds"));
                host.SettingsChanged(); return new { settings = sceneOrdered };
            case "scenes.run":
                RequireKeys(payload, "sceneId");
                return new { execution = host.Scenes.Start(String(payload, "sceneId", 96)), executions = host.Scenes.Snapshot() };
            case "scenes.pickTarget":
                RequireWorkspace(); RequireKeys(payload, "kind");
                return await ChooseSceneTargetAsync(String(payload, "kind", 20));
            case "apps.reorder":
                RequireWorkspace(); RequireKeys(payload, "appIds");
                var reordered = host.AppLibrary.Reorder(StringArray(payload, "appIds"));
                host.SettingsChanged(); return new { settings = reordered };
            case "launcher.setPins":
                RequireWorkspace(); RequireKeys(payload, "surface", "pins");
                var pinList = payload.GetProperty("pins");
                if (pinList.ValueKind != JsonValueKind.Array) throw new BridgeException("INVALID_PARAMS", "固定入口必须是列表。");
                var pins = new List<LauncherPin>();
                foreach (var pin in pinList.EnumerateArray())
                {
                    RequireKeys(pin, "itemType", "itemId", "enabled");
                    pins.Add(new LauncherPin { ItemType = String(pin, "itemType", 12), ItemId = String(pin, "itemId", 96), Enabled = Boolean(pin, "enabled") });
                }
                var pinned = host.AppLibrary.SetPins(String(payload, "surface", 12), pins);
                host.SettingsChanged(); return new { settings = pinned };
            case "apps.launch":
                RequireKeys(payload, "appId");
                if (launching) throw new BridgeException("BUSY", "程序正在启动。");
                var appId = String(payload, "appId", 96);
                var snapshot = host.Store.Current;
                launching = true;
                try { return await Task.Run(() => host.Launcher.LaunchApp(snapshot, appId)); }
                finally { launching = false; }
            case "app.exit": Empty(payload); _ = window.Dispatcher.BeginInvoke(host.Exit); return new { exiting = true };
            default: throw new BridgeException("UNKNOWN_MESSAGE", "不支持此操作。");
        }
    }

    private async Task<object> ChooseAppAsync(string? replaceId = null)
    {
        if (pickerOpen) throw new BridgeException("BUSY", "文件选择窗口已打开。");
        if (replaceId is not null && !host.Store.Current.AppEntries.Any(app => app.Id == replaceId))
            throw new BridgeException("app_not_found", "此应用已从应用库移除，请刷新后重试。");
        pickerOpen = true;
        try
        {
            var dialog = new OpenFileDialog { Title = "选择本地程序", Filter = "程序与快捷方式 (*.exe;*.lnk)|*.exe;*.lnk|Windows 程序 (*.exe)|*.exe|Windows 快捷方式 (*.lnk)|*.lnk",
                CheckFileExists = true, Multiselect = false, DereferenceLinks = false };
            host.Log("Picker", "Opening");
            // Leave the WebView message callback before opening the modal native picker.
            if (await window.Dispatcher.InvokeAsync(() => dialog.ShowDialog(window)) != true)
                return new { cancelled = true, app = (AppEntry?)null, settings = host.Store.Current };
            var app = host.Launcher.RegisterApp(dialog.FileName);
            app.Icon = host.Icons.Extract(app);
            var settings = replaceId is null ? host.AppLibrary.Add(app) : host.AppLibrary.ReplaceTarget(replaceId, app);
            host.SettingsChanged();
            var saved = settings.AppEntries.Single(item => item.Id == (replaceId ?? app.Id));
            return new { cancelled = false, app = saved, settings, appIcons = host.Icons.GetIcons(settings) };
        }
        finally { pickerOpen = false; }
    }

    private async Task<object> ChooseSceneTargetAsync(string kind)
    {
        if (pickerOpen) throw new BridgeException("BUSY", "文件选择窗口已打开。");
        pickerOpen = true;
        try
        {
            if (kind is "folder" or "workingDirectory")
            {
                var folder = new OpenFolderDialog { Title = "选择本地文件夹", Multiselect = false };
                if (await window.Dispatcher.InvokeAsync(() => folder.ShowDialog(window)) != true) return new { cancelled = true };
                return new { cancelled = false, target = NativeLaunchService.ValidateLocalPath(folder.FolderName) };
            }
            if (kind is not ("application" or "file" or "script" or "waitTarget")) throw new BridgeException("INVALID_PARAMS", "目标类型无效。");
            var dialog = new OpenFileDialog { Title = "选择场景动作目标", CheckFileExists = true, Multiselect = false, DereferenceLinks = false,
                Filter = kind == "script" ? "本地脚本 (*.bat;*.cmd;*.ps1)|*.bat;*.cmd;*.ps1" : kind is "application" or "waitTarget" ? "应用 (*.exe;*.lnk)|*.exe;*.lnk" : "所有文件 (*.*)|*.*" };
            if (await window.Dispatcher.InvokeAsync(() => dialog.ShowDialog(window)) != true) return new { cancelled = true };
            var target = NativeLaunchService.ValidateLocalPath(dialog.FileName);
            if (kind == "application") host.Launcher.RegisterApp(target);
            return new { cancelled = false, target };
        }
        finally { pickerOpen = false; }
    }

    private void RequireWorkspace() { if (window.Surface != "workspace") throw new BridgeException("WRONG_SURFACE", "请在工作空间完成此操作。"); }
    private static string String(JsonElement obj, string name, int max)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length is < 1 || text.Length > max)
            throw new BridgeException("INVALID_PARAMS", "参数无效：" + name);
        return text;
    }
    private static string OptionalString(JsonElement obj, string name, int max)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String || value.GetString() is not { } text || text.Length > max)
            throw new BridgeException("INVALID_PARAMS", "参数无效：" + name);
        return text;
    }
    private static bool Boolean(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new BridgeException("INVALID_PARAMS", "参数无效：" + name);
        return value.GetBoolean();
    }
    private static List<string> StringArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array)
            throw new BridgeException("INVALID_PARAMS", "参数必须是列表：" + name);
        return array.EnumerateArray().Select(value => value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 and <= 96 } text
            ? text : throw new BridgeException("INVALID_PARAMS", "列表编号无效。")).ToList();
    }
    private static void Empty(JsonElement element) => RequireKeys(element);
    private static void RequireKeys(JsonElement element, params string[] allowed)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new BridgeException("INVALID_PARAMS", "参数必须是对象。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in element.EnumerateObject())
            if (!allowed.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name)) throw new BridgeException("INVALID_PARAMS", "未知或重复参数：" + field.Name);
    }
    private void Reply(object response)
    {
        try { window.Browser.CoreWebView2?.PostWebMessageAsJson(JsonSerializer.Serialize(response, HubApplication.Json)); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }
}
