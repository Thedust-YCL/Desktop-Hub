using System.Diagnostics;
using System.Text.Json;
using DesktopHub.Core;
using Microsoft.Win32;

namespace DesktopHub.Windows;

// Only native pickers/known folders produce source paths. UI sends IDs and preview tokens.
internal sealed class ResourceBridge(HubApplication host, HubWindow window)
{
    private bool pickerOpen;
    public async Task<object> DispatchAsync(string type, JsonElement payload)
    {
        if (window.Surface != "workspace") throw new BridgeException("WRONG_SURFACE", "请在工作空间管理资源。");
        var service = await host.GetResourcesAsync();
        switch (type)
        {
            case "resources.query":
                Keys(payload, "query", "tagIds", "favoritesOnly", "invalidOnly", "status", "page", "extension");
                return await Task.Run(() => service.Query(new(Text(payload, "query", 200, "")!, Strings(payload, "tagIds"), Bool(payload, "favoritesOnly"), Bool(payload, "invalidOnly"), Text(payload, "status", 30, null), Number(payload, "page"), Text(payload, "extension", 260, null))));
            case "resources.discover":
                Keys(payload); return await Task.Run(() => new { desktop = ResourceScanner.DesktopPath(), cloudPath = ResourceScanner.CloudPath(ResourceScanner.DesktopPath()), publicDesktopAvailable = true });
            case "resources.preview":
                Keys(payload, "kind", "recursive", "operationId", "sourceId", "browseToken", "authorizePublic");
                var operation = Required(payload, "operationId"); var recursive = Bool(payload, "recursive"); var source = Text(payload, "sourceId", 96, null);
                if (Text(payload, "browseToken", 96, null) is { } browseToken) return await service.PreviewBrowseAsync(browseToken, operation);
                if (source is not null) return await service.PreviewSourceAsync(source, recursive, operation);
                var kind = Required(payload, "kind"); string path;
                if (kind == "publicDesktop" && !Bool(payload, "authorizePublic")) throw new ArgumentException("请先勾选允许扫描公共桌面。");
                if (kind is "desktop" or "publicDesktop") path = ResourceScanner.DesktopPath(kind == "publicDesktop");
                else if (kind == "custom")
                {
                    if (pickerOpen) throw new BridgeException("BUSY", "目录选择窗口已打开。");
                    pickerOpen = true;
                    try
                    {
                        var dialog = new OpenFolderDialog { Title = "选择要建立虚拟索引的目录", Multiselect = false };
                        host.Log("ResourcePicker", "Opening");
                        if (await window.Dispatcher.InvokeAsync(() => dialog.ShowDialog(window)) != true) return new { cancelled = true };
                        path = dialog.FolderName;
                    }
                    finally { pickerOpen = false; }
                }
                else throw new ArgumentException("来源类型无效。");
                return await service.PreviewAsync(path, kind, recursive, operation);
            case "resources.cancel": Keys(payload, "operationId"); service.Cancel(Required(payload, "operationId")); return new { cancelled = true };
            case "resources.confirm": Keys(payload, "token", "autoTagByModificationTime"); await service.ConfirmAsync(Required(payload, "token"), payload.TryGetProperty("autoTagByModificationTime", out _) ? Bool(payload, "autoTagByModificationTime") : null); break;
            case "resources.refresh": Keys(payload, "operationId"); await service.RefreshAsync(Required(payload, "operationId")); break;
            case "resources.removeSource": Keys(payload, "sourceId", "confirmed"); await service.RemoveSourceAsync(Required(payload, "sourceId"), Bool(payload, "confirmed")); break;
            case "resources.favorite": Keys(payload, "resourceId", "favorite"); await service.FavoriteAsync(Required(payload, "resourceId"), Bool(payload, "favorite")); break;
            case "resources.beginRepair": Keys(payload, "resourceId"); await service.BeginRepairAsync(Required(payload, "resourceId")); break;
            case "resources.repair":
                Keys(payload, "resourceId"); var resourceId = Required(payload, "resourceId");
                var resource = service.Store.Snapshot().Resources.SingleOrDefault(x => x.Id == resourceId) ?? throw new ArgumentException("资源不存在。");
                if (pickerOpen) throw new BridgeException("BUSY", "文件选择窗口已打开。");
                pickerOpen = true;
                try
                {
                    string chosen;
                    if (resource.ResourceType == "folder")
                    {
                        var folder = new OpenFolderDialog { Title = "重新关联原资源（不移动文件）", Multiselect = false };
                        if (await window.Dispatcher.InvokeAsync(() => folder.ShowDialog(window)) != true) return new { cancelled = true };
                        chosen = folder.FolderName;
                    }
                    else
                    {
                        var file = new OpenFileDialog { Title = "重新关联原资源（不移动文件）", CheckFileExists = true, DereferenceLinks = false, Multiselect = false, Filter = "所有文件 (*.*)|*.*" };
                        if (await window.Dispatcher.InvokeAsync(() => file.ShowDialog(window)) != true) return new { cancelled = true };
                        chosen = file.FileName;
                    }
                    await service.RepairAsync(resourceId, chosen);
                }
                finally { pickerOpen = false; }
                break;
            case "resources.clean": Keys(payload, "resourceIds", "confirmed"); await service.CleanAsync(Strings(payload, "resourceIds"), Bool(payload, "confirmed")); break;
            case "resources.saveTag": Keys(payload, "tagId", "name", "kind", "categoryId"); await service.SaveTagAsync(Text(payload, "tagId", 96, null), Required(payload, "name", 80), Required(payload, "kind"), Required(payload, "categoryId")); break;
            case "resources.saveCategory":
                Keys(payload, "name"); var categoryName = Required(payload, "name", 40).Trim();
                await service.SaveCategoryAsync(categoryName);
                return new { saved = true, category = service.Store.Snapshot().TagCategories.Single(c => c.Name == categoryName) };
            case "resources.deleteTag": Keys(payload, "tagId", "confirmed"); await service.DeleteTagAsync(Required(payload, "tagId"), Bool(payload, "confirmed")); break;
            case "resources.bindTag": Keys(payload, "resourceId", "tagId", "bind"); await service.BindTagAsync(Required(payload, "resourceId"), Required(payload, "tagId"), Bool(payload, "bind")); break;
            case "resources.suggestion": Keys(payload, "suggestionId", "accept", "editedName"); await service.SuggestionAsync(Required(payload, "suggestionId"), Bool(payload, "accept"), Text(payload, "editedName", 80, null)); break;
            case "resources.browse": Keys(payload, "resourceId", "token", "name", "page"); return await service.BrowseAsync(Text(payload, "resourceId", 96, null), Text(payload, "token", 96, null), Text(payload, "name", 255, null), Number(payload, "page"));
            case "resources.addBrowsed": Keys(payload, "token", "name"); await service.AddBrowsedAsync(Required(payload, "token"), Required(payload, "name", 255)); break;
            case "resources.open":
                Keys(payload, "resourceId", "token", "name", "mode");
                var mode = Required(payload, "mode"); if (mode is not ("open" or "reveal" or "folder")) throw new ArgumentException("打开方式无效。");
                var target = await Task.Run(() => service.OpenTarget(Text(payload, "resourceId", 96, null), Text(payload, "token", 96, null), Text(payload, "name", 255, null)));
                await Task.Run(() =>
                {
                    if (mode == "reveal")
                    {
                        var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { UseShellExecute = false };
                        start.ArgumentList.Add("/select,"); start.ArgumentList.Add(target.CanonicalPath); Process.Start(start);
                    }
                    else
                    {
                        // Shell receives one verified registered path, never a command assembled from UI text.
                        var pathToOpen = mode == "folder" && target.ResourceType != "folder" ? System.IO.Path.GetDirectoryName(target.CanonicalPath)! : target.CanonicalPath;
                        if (mode == "open" && target.ResourceType == "file" && new[] { ".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".msi", ".lnk", ".url", ".scr", ".hta", ".reg", ".wsf" }.Contains(target.Extension))
                            throw new ArgumentException("此资源是程序、脚本或快捷方式。请在应用库/场景中明确配置，或在资源管理器中打开。");
                        Process.Start(new ProcessStartInfo(pathToOpen) { UseShellExecute = true });
                    }
                });
                return new { submitted = true, message = "已向 Windows 提交打开请求。" };
            default: throw new BridgeException("UNKNOWN_MESSAGE", "不支持此资源操作。");
        }
        return new { saved = true };
    }
    private static void Keys(JsonElement value, params string[] allowed)
    { var seen = new HashSet<string>(); foreach (var field in value.EnumerateObject()) if (!allowed.Contains(field.Name) || !seen.Add(field.Name)) throw new ArgumentException("未知或重复参数：" + field.Name); }
    private static string Required(JsonElement value, string key, int max = 96) => Text(value, key, max, null) is { Length: > 0 } text ? text : throw new ArgumentException("缺少参数：" + key);
    private static string? Text(JsonElement value, string key, int max, string? fallback)
    { if (!value.TryGetProperty(key, out var item)) return fallback; if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text || text.Length > max || text.Any(char.IsControl)) throw new ArgumentException("参数无效：" + key); return text; }
    private static bool Bool(JsonElement value, string key)
    { if (!value.TryGetProperty(key, out var item)) return false; if (item.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("参数无效：" + key); return item.GetBoolean(); }
    private static int Number(JsonElement value, string key)
    { if (!value.TryGetProperty(key, out var item)) return 0; if (!item.TryGetInt32(out var number) || number < 0 || number > 100000) throw new ArgumentException("页码无效。"); return number; }
    private static List<string> Strings(JsonElement value, string key)
    { if (!value.TryGetProperty(key, out var item)) return []; if (item.ValueKind != JsonValueKind.Array || item.GetArrayLength() > 1000) throw new ArgumentException("列表无效。"); return item.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String && x.GetString() is { Length: > 0 and <= 96 } text ? text : throw new ArgumentException("列表编号无效。")).ToList(); }
}
