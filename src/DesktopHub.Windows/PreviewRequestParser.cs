using System.Text.Json;

namespace DesktopHub.Windows;

internal sealed record PreviewRequest(string ResourceId, bool Thumbnail, string RequestId);
internal static class PreviewRequestParser
{
    internal static PreviewRequest Content(JsonElement value)
    {
        Keys(value, "resourceId", "thumbnail", "requestId");
        if (!value.TryGetProperty("thumbnail", out var flag) || flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new ArgumentException("缩略参数无效。");
        return new(Id(value, "resourceId"), flag.GetBoolean(), Id(value, "requestId"));
    }
    internal static string Cancel(JsonElement value) { Keys(value, "requestId"); return Id(value, "requestId"); }
    internal static void ValidateId(string value) { if (string.IsNullOrWhiteSpace(value) || value.Length > 96 || value.Any(char.IsControl)) throw new ArgumentException("资源或请求编号无效。"); }
    private static string Id(JsonElement value, string key)
    { if (!value.TryGetProperty(key, out var field) || field.ValueKind != JsonValueKind.String) throw new ArgumentException("缺少参数：" + key); var id = field.GetString()!; ValidateId(id); return id; }
    private static void Keys(JsonElement value, params string[] allowed)
    { if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("预览参数必须为对象。"); var seen = new HashSet<string>(); foreach (var field in value.EnumerateObject()) if (!allowed.Contains(field.Name) || !seen.Add(field.Name)) throw new ArgumentException("未知或重复参数：" + field.Name); }
}
