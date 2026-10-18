using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopHub.Core;

namespace DesktopHub.Windows;

// Pure strict adapters linked into the independent Core harness; no WPF or process dependency.
internal static class ActivityProjectRequestParser
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    internal static void Empty(JsonElement payload) => Keys(payload);
    internal static string? Query(JsonElement payload)
    {
        Keys(payload, "projectId");
        return payload.TryGetProperty("projectId", out _) ? Stable(payload, "projectId") : null;
    }
    internal static string Remove(JsonElement payload) { Keys(payload, "projectId"); return Stable(payload, "projectId"); }
    internal static (string ProjectId, string ItemId) OpenWeb(JsonElement payload)
    { Keys(payload, "projectId", "itemId"); return (Stable(payload, "projectId"), Stable(payload, "itemId")); }
    internal static Project Save(JsonElement payload)
    {
        Keys(payload, "project");
        if (!payload.TryGetProperty("project", out var p)) throw new ArgumentException("缺少项目。");
        Keys(p, "id", "name", "description", "displayOrder", "pinnedToHome", "items");
        _ = Stable(p, "id"); _ = Text(p, "name", 80); _ = Text(p, "description", 500, description: true, empty: true);
        if (!p.TryGetProperty("displayOrder", out var order) || order.ValueKind != JsonValueKind.Number || !order.TryGetInt32(out var position) || position is < 0 or > 199) throw new ArgumentException("项目顺序无效。");
        if (!p.TryGetProperty("pinnedToHome", out var pin) || pin.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("项目固定选项无效。");
        if (!p.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 100) throw new ArgumentException("项目关联最多 100 项。");
        foreach (var item in items.EnumerateArray())
        {
            Keys(item, "id", "type", "entityId", "url", "name");
            _ = Stable(item, "id"); var type = Text(item, "type", 10);
            if (type is not ("app" or "scene" or "file" or "folder" or "web")) throw new ArgumentException("项目关联类型无效。");
            foreach (var field in new[] { "entityId", "url", "name" }) if (!item.TryGetProperty(field, out _)) throw new ArgumentException("缺少关联字段：" + field);
            if (type == "web")
            {
                if (item.GetProperty("entityId").ValueKind != JsonValueKind.Null) throw new ArgumentException("网页不能包含实体编号。");
                _ = ProjectUrlValidator.Normalize(Text(item, "url", 1024));
                if (item.GetProperty("name").ValueKind != JsonValueKind.Null) _ = Text(item, "name", 80);
            }
            else
            {
                _ = Entity(item, "entityId");
                if (item.GetProperty("url").ValueKind != JsonValueKind.Null || item.GetProperty("name").ValueKind != JsonValueKind.Null) throw new ArgumentException("实体关联只能保存编号。");
            }
        }
        return p.Deserialize<Project>(Json) ?? throw new ArgumentException("项目不能为空。");
    }
    private static void Keys(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("参数必须是对象。");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject()) if (!allowed.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name)) throw new ArgumentException("未知或重复字段：" + field.Name);
    }
    private static string Text(JsonElement value, string key, int maximum, bool description = false, bool empty = false)
    {
        if (!value.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.String || field.GetString() is not { } text ||
            (description ? text : text.Trim()).Length > maximum || (!empty && string.IsNullOrWhiteSpace(text)) || text.Any(c => char.IsControl(c) && !(description && c is '\r' or '\n' or '\t')))
            throw new ArgumentException("参数无效：" + key);
        return text;
    }
    private static string Entity(JsonElement value, string key)
    {
        var text = Text(value, key, 96);
        if (text.Length > 96 || !Guid.TryParse(text, out var guid) || guid == Guid.Empty) throw new ArgumentException("实体编号无效。");
        return text;
    }
    private static string Stable(JsonElement value, string key)
    {
        var text = Text(value, key, 36);
        if (text.Length != 36 || !Guid.TryParseExact(text, "D", out var guid) || guid == Guid.Empty) throw new ArgumentException("项目或关联编号无效。");
        return text;
    }
}

internal sealed record ActivityResolvedItem(ActivityRecord Record, string Name, string Detail, string Status, bool CanExecute);
internal sealed record ProjectResolvedItem(ProjectItem Item, string Name, string Detail, string Status, bool CanExecute);
internal sealed record ProjectSummary(string Id, string Name, string Description, int DisplayOrder, bool PinnedToHome, int ItemCount);
internal sealed record CurrentReference(string Name, string Detail, string Status, bool CanExecute);

internal static class ActivityProjectMetadata
{
    internal static CurrentReference Resolve(string type, string id, AppSettings settings, ResourceLibrary resources)
    {
        if (type == "app" && settings.AppEntries.FirstOrDefault(app => app.Id == id) is { } app) return new(app.Name, app.Target, "Active", true);
        if (type == "scene" && settings.Scenes.FirstOrDefault(scene => scene.Id == id) is { } scene)
        {
            var invalid = scene.Actions.Any(action => action.Enabled && (action.ReferenceInvalid || action.AppEntryId is { Length: > 0 } appId && !settings.AppEntries.Any(app => app.Id == appId)));
            var status = !scene.Enabled ? "Disabled" : invalid ? "Invalid" : "Active";
            return new(scene.Name, settings.Language == "en-US" ? $"{scene.Actions.Count} actions" : $"{scene.Actions.Count} 个动作", status, status == "Active");
        }
        if (type is "file" or "folder" && resources.Resources.FirstOrDefault(resource => resource.Id == id && resource.ResourceType == type) is { } file)
            return new(file.DisplayName, file.CanonicalPath, file.Status, file.Status == "Active");
        return new(UiText.Translate(settings.Language, "已移除"), UiText.Translate(settings.Language, "关联失效，请移除或重新选择。"), "Removed", false);
    }
    internal static ActivityResolvedItem Activity(ActivityRecord record, AppSettings settings, ResourceLibrary resources)
    {
        var current = Resolve(record.Type, record.Id, settings, resources);
        return new(record with { }, current.Name, current.Detail, current.Status, current.CanExecute);
    }
    internal static ProjectResolvedItem Item(ProjectItem item, AppSettings settings, ResourceLibrary resources)
    {
        if (item.Type == "web")
        {
            var url = ProjectUrlValidator.Normalize(item.Url);
            var uri = new Uri(url);
            return new(item with { Url = url }, item.Name ?? uri.Host, url, "Active", true);
        }
        var current = Resolve(item.Type, item.EntityId!, settings, resources);
        return new(item with { }, current.Name, current.Detail, current.Status, current.CanExecute);
    }
    internal static ProjectSummary Summary(Project project) => new(project.Id, project.Name, project.Description, project.DisplayOrder, project.PinnedToHome, project.Items.Count);
}
