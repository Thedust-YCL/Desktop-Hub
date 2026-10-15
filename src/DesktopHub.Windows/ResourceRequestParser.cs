using System.Text.Json;
using DesktopHub.Core;

namespace DesktopHub.Windows;

internal sealed record ResourceBatchRequest(List<string> ResourceIds, string Operation, List<string> Values);
internal sealed record ResourceSuggestionRequest(List<string> ResourceIds, string SuggestionName, string SuggestionType);
internal sealed record ResourceQueryRequest(ResourceQuery Query, string? FilterId, List<string> SelectedIds, List<string> CleanupIds);

// No WPF or host dependency; linked-source tests exercise the adapter used by ResourceBridge.
internal static class ResourceRequestParser
{
    internal static bool EnvelopeAllowed(string type, int length)
    {
        var limit = type switch
        {
            "projects.save" => 768 * 1024,
            "resources.query" or "resources.batch" or "resources.batchSuggestions" or "resources.filterSave" => 256 * 1024,
            "apps.update" or "apps.reorder" or "launcher.setPins" or "scenes.save" or "scenes.reorder" => 1024 * 1024,
            _ => 16384
        };
        return length >= 0 && length <= limit;
    }
    internal static (string Type, string Id)? ActivityReference(ResourceLibrary library, string? requestedId, string mode)
    {
        if (requestedId is null || mode == "reveal") return null;
        var indexed = library.Resources.FirstOrDefault(item => item.Id == requestedId);
        if (indexed is null || indexed.ResourceType is not ("file" or "folder") || indexed.Status != "Active" || mode != "open" && !(mode == "folder" && indexed.ResourceType == "folder")) return null;
        return (indexed.ResourceType, requestedId);
    }
    internal static ResourceBatchRequest Batch(JsonElement payload)
    {
        Keys(payload, "resourceIds", "operation", "values");
        var ids = Ids(payload, "resourceIds", true); var operation = Text(payload, "operation", 30, required: true)!; var values = Ids(payload, "values");
        if (operation is not ("addTags" or "removeTags" or "favorite" or "unfavorite" or "acceptSuggestions") ||
            (operation is "addTags" or "removeTags" && values.Count == 0) ||
            (operation is "favorite" or "unfavorite" && values.Count != 0) ||
            (operation == "acceptSuggestions" && values.Count != ids.Count)) throw new ArgumentException("批量操作或附加条件无效。");
        return new(ids, operation, values);
    }
    internal static ResourceSuggestionRequest Suggestions(JsonElement payload)
    {
        Keys(payload, "resourceIds", "suggestionName", "suggestionType");
        var name = Text(payload, "suggestionName", 80, required: true)!.Trim(); var type = Text(payload, "suggestionType", 30, required: true)!;
        ResourceStore.ValidateTag(name, type); return new(Ids(payload, "resourceIds", true), name, type);
    }
    internal static ResourceQueryRequest Query(JsonElement payload)
    {
        Keys(payload, "query", "tagIds", "favoritesOnly", "invalidOnly", "status", "page", "extension", "sort", "filterId", "selectedIds", "cleanupIds");
        var filterId = payload.TryGetProperty("filterId", out _) ? Id(payload, "filterId") : null;
        if (filterId is not null && new[] { "query", "tagIds", "favoritesOnly", "invalidOnly", "status", "extension", "sort" }.Any(key => payload.TryGetProperty(key, out _))) throw new ArgumentException("保存筛选不能混用其他条件。");
        var status = Text(payload, "status", 30);
        if (status is not null && status is not ("Active" or "Missing" or "PendingRepair" or "SourceUnavailable")) throw new ArgumentException("资源状态无效。");
        var sort = Text(payload, "sort", 30, "type")!; Sort(sort);
        return new(new(Text(payload, "query", 200, "")!, Ids(payload, "tagIds"), Bool(payload, "favoritesOnly"), Bool(payload, "invalidOnly"), status, Page(payload), Text(payload, "extension", 260), sort), filterId, Ids(payload, "selectedIds"), Ids(payload, "cleanupIds"));
    }
    internal static SavedResourceFilter FilterSave(JsonElement payload)
    {
        Keys(payload, "filter"); if (!payload.TryGetProperty("filter", out var filter)) throw new ArgumentException("缺少筛选。");
        Keys(filter, "id", "name", "query", "tagIds", "extension", "favoritesOnly", "sort");
        var sort = Text(filter, "sort", 30, "type")!; Sort(sort);
        return new() { Id = Id(filter, "id"), Name = Text(filter, "name", 80, required: true)!.Trim(), Query = Text(filter, "query", 200, "")!, TagIds = Ids(filter, "tagIds"), Extension = Text(filter, "extension", 260, nullable: true), FavoritesOnly = Bool(filter, "favoritesOnly"), Sort = sort };
    }
    internal static string FilterDelete(JsonElement payload) { Keys(payload, "id"); return Id(payload, "id"); }
    private static void Sort(string sort) { if (sort is not ("type" or "name" or "modifiedAsc" or "modifiedDesc")) throw new ArgumentException("资源排序无效。"); }
    private static void Keys(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("参数应为对象。");
        var seen = new HashSet<string>(StringComparer.Ordinal); foreach (var field in value.EnumerateObject()) if (!allowed.Contains(field.Name) || !seen.Add(field.Name)) throw new ArgumentException("未知或重复参数：" + field.Name);
    }
    private static string? Text(JsonElement value, string key, int max, string? fallback = null, bool required = false, bool nullable = false)
    {
        if (!value.TryGetProperty(key, out var item)) { if (required) throw new ArgumentException("缺少参数：" + key); return fallback; }
        if (nullable && item.ValueKind == JsonValueKind.Null) return null;
        if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text || text.Length > max || text.Any(char.IsControl) || (required && string.IsNullOrWhiteSpace(text))) throw new ArgumentException("参数无效：" + key); return text;
    }
    private static string Id(JsonElement value, string key)
    { var text = Text(value, key, 96, required: true)!; if (!Guid.TryParse(text, out var id) || id == Guid.Empty) throw new ArgumentException("编号无效：" + key); return text; }
    private static List<string> Ids(JsonElement value, string key, bool required = false)
    {
        if (!value.TryGetProperty(key, out var array)) { if (required) throw new ArgumentException("缺少参数：" + key); return []; }
        if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 500 || (required && array.GetArrayLength() == 0)) throw new ArgumentException("列表最多 500 项：" + key);
        var ids = new List<string>(); var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { Length: > 0 and <= 96 } text || text.Any(char.IsControl) || !Guid.TryParse(text, out var id) || id == Guid.Empty || !seen.Add(text)) throw new ArgumentException("编号重复或无效：" + key);
            ids.Add(text);
        }
        return ids;
    }
    private static bool Bool(JsonElement value, string key)
    { if (!value.TryGetProperty(key, out var item)) return false; if (item.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("参数无效：" + key); return item.GetBoolean(); }
    private static int Page(JsonElement value)
    { if (!value.TryGetProperty("page", out var item)) return 0; if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out var page) || page is < 0 or > 100000) throw new ArgumentException("页码无效。"); return page; }
}
