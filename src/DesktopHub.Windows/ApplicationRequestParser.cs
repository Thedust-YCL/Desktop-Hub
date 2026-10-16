using System.Text.Json;
using DesktopHub.Core;

namespace DesktopHub.Windows;

internal sealed record ApplicationScanRequest(string Source, bool Recursive, string ScanId);
internal sealed record ApplicationUpdateRequest(string AppId, string Name, string Arguments, string WorkingDirectory, bool Mini, bool Dock, bool? Favorite);
internal sealed record ApplicationSearchRequest(string Query, string Type, bool FavoritesOnly)
{
    public IReadOnlyList<SearchEntity> Execute(IEnumerable<SearchEntity> entities) => UnifiedSearchService.Search(
        FavoritesOnly ? entities.Where(entity => entity.Favorite && entity.Type is "app" or "file" or "folder") : entities, Query, Type);
}

/// <summary>Pure boundary shared with the executable harness; no frontend paths are admitted.</summary>
internal static class ApplicationRequestParser
{
    public static ApplicationScanRequest Scan(JsonElement value)
    {
        Keys(value, "source", "recursive", "scanId");
        var source = Text(value, "source", 12); var recursive = Bool(value, "recursive"); var id = Text(value, "scanId", 96, true);
        if (source is not ("desktop" or "folder" or "current" or "startMenu" or "files" or "drop") ||
            (source is "current" or "drop" ? !IsId(id) : id.Length != 0) ||
            (source is "files" or "drop" && recursive) || (source == "startMenu" && !recursive)) Invalid();
        return new(source, recursive, id);
    }
    public static ApplicationSearchRequest Search(JsonElement value)
    {
        Keys(value, "query", "type", "favoritesOnly");
        var query = Text(value, "query", 200, true); var type = Text(value, "type", 12);
        if (type is not ("all" or "app" or "scene" or "file" or "folder" or "tag" or "project")) Invalid();
        return new(query, type, value.TryGetProperty("favoritesOnly", out _) && Bool(value, "favoritesOnly"));
    }
    public static ApplicationUpdateRequest Update(JsonElement value)
    {
        Keys(value, "appId", "name", "arguments", "workingDirectory", "pinnedToMini", "pinnedToDock", "favorite");
        return new(Id(value, "appId"), Text(value, "name", 200), Text(value, "arguments", 32767, true), Text(value, "workingDirectory", 32767, true),
            Bool(value, "pinnedToMini"), Bool(value, "pinnedToDock"), value.TryGetProperty("favorite", out _) ? Bool(value, "favorite") : null);
    }
    public static (string AppId, bool Favorite) Favorite(JsonElement value)
    { Keys(value, "appId", "favorite"); return (Id(value, "appId"), Bool(value, "favorite")); }
    public static (string? GroupId, string Name) GroupSave(JsonElement value)
    { Keys(value, "groupId", "name"); return (NullableId(value, "groupId"), Text(value, "name", 80)); }
    public static string GroupRemove(JsonElement value)
    { Keys(value, "groupId"); return Id(value, "groupId"); }
    public static (string AppId, string? GroupId) MoveGroup(JsonElement value)
    { Keys(value, "appId", "groupId"); return (Id(value, "appId"), NullableId(value, "groupId")); }
    public static IReadOnlyList<string> GroupReorder(JsonElement value)
    {
        Keys(value, "groupIds");
        if (!value.TryGetProperty("groupIds", out var ids) || ids.ValueKind != JsonValueKind.Array || ids.GetArrayLength() > 5000) Invalid();
        var result = ids.EnumerateArray().Select(item => item.ValueKind == JsonValueKind.String && IsId(item.GetString()!) ? item.GetString()! : throw new ArgumentException("分组编号无效。")).ToArray();
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Length) Invalid();
        return result;
    }
    public static bool ShouldCloseDock(string surface, bool enabled, bool success) => surface == "dock" && enabled && success;
    private static bool IsId(string id) => id.Length is > 0 and <= 96 && !id.Any(char.IsControl) && Guid.TryParse(id, out var guid) && guid != Guid.Empty;
    private static string Id(JsonElement value, string name) { var id = Text(value, name, 96); if (!IsId(id)) Invalid(); return id; }
    private static string? NullableId(JsonElement value, string name)
    { if (!value.TryGetProperty(name, out var field)) Invalid(); return field.ValueKind == JsonValueKind.Null ? null : Id(value, name); }
    private static string Text(JsonElement value, string name, int max, bool empty = false)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind != JsonValueKind.String || field.GetString() is not { } text ||
            text.Length > max || (!empty && text.Length == 0) || text.Any(char.IsControl)) throw new ArgumentException("参数无效：" + name);
        return text;
    }
    private static bool Bool(JsonElement value, string name)
    { if (!value.TryGetProperty(name, out var field) || field.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("参数无效：" + name); return field.GetBoolean(); }
    private static void Keys(JsonElement value, params string[] allowed)
    {
        if (value.ValueKind != JsonValueKind.Object) Invalid();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject()) if (!allowed.Contains(field.Name, StringComparer.Ordinal) || !seen.Add(field.Name)) Invalid();
    }
    private static void Invalid() => throw new ArgumentException("应用请求参数无效。");
}
