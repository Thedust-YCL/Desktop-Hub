using System.Text;
using System.Text.Json;
namespace DesktopHub.Windows;

/// <summary>Bounded, non-authorizing browser state. Kept only in the host's memory.</summary>
public sealed class BrowserViewSnapshot
{
    public const int MaximumBytes = 64 * 1024;
    public JsonElement Value { get; }
    private BrowserViewSnapshot(JsonElement value) => Value = value.Clone();
    private static readonly HashSet<string> Pages = new("home scenes editor library tags favorites search settings activity projects project folder missing sources suggestions cleanup".Split(' '), StringComparer.Ordinal);
    public static bool TryParse(string? json, out BrowserViewSnapshot? snapshot)
    {
        snapshot = null;
        if (json is null || Encoding.UTF8.GetByteCount(json) > MaximumBytes) return false;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            var value = document.RootElement;
            if (!Object(value, "version page resources history scenes projects scroll") || !value.TryGetProperty("version", out var version) || !version.TryGetInt32(out var v) || v != 1 || !value.TryGetProperty("page", out var page) || page.ValueKind != JsonValueKind.String || !Pages.Contains(page.GetString()!)) return false;
            if (!Optional(value,"resources",Resources) || !Optional(value,"projects",Projects) || !Optional(value,"scenes",Scenes) || !Optional(value,"scroll",Scroll) || !Optional(value,"history",History)) return false;
            snapshot = new BrowserViewSnapshot(value); return true;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { return false; }
    }
    private static bool Object(JsonElement value,string keys)
    {
        if(value.ValueKind != JsonValueKind.Object) return false;
        var allowed=keys.Split(' ').ToHashSet(StringComparer.Ordinal);var seen=new HashSet<string>(StringComparer.Ordinal);
        return value.EnumerateObject().All(p=>allowed.Contains(p.Name)&&seen.Add(p.Name));
    }
    private static bool Optional(JsonElement value,string name,Func<JsonElement,bool> validate) => !value.TryGetProperty(name,out var p)||validate(p);
    private static bool Text(JsonElement value,int max=96) => value.ValueKind==JsonValueKind.String && value.GetString()!.Length<=max && !value.GetString()!.Any(char.IsControl);
    private static bool Number(JsonElement value,double max) => value.ValueKind==JsonValueKind.Number && value.TryGetDouble(out var n)&&double.IsFinite(n)&&n>=0&&n<=max;
    private static bool Integer(JsonElement value,int max) => value.TryGetInt32(out var n)&&n>=0&&n<=max;
    private static bool Bool(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    private static bool Strings(JsonElement value,int count) => value.ValueKind==JsonValueKind.Array&&value.GetArrayLength()<=count&&value.EnumerateArray().All(v=>Text(v));
    private static bool Scroll(JsonElement value) => Object(value,"top left")&&Optional(value,"top",v=>Number(v,10000000))&&Optional(value,"left",v=>Number(v,10000000));
    private static bool Projects(JsonElement value) => Object(value,"projectId")&&Optional(value,"projectId",v=>v.ValueKind==JsonValueKind.Null||Text(v));
    private static bool Scenes(JsonElement value) => Object(value,"sceneId actionId selectedExecutionId expandedResultIds resultOpen")&&Optional(value,"sceneId",v=>Text(v))&&Optional(value,"actionId",v=>Text(v))&&Optional(value,"selectedExecutionId",v=>Text(v))&&Optional(value,"expandedResultIds",v=>Strings(v,32))&&Optional(value,"resultOpen",Bool);
    private static bool Resources(JsonElement value)
    {
        if(!Object(value,"query tagIds view page status category extension sort filterId favoritesOnly folderResourceId"))return false;
        return value.EnumerateObject().All(p=>p.Name switch {
          "query"=>Text(p.Value,512), "tagIds"=>Strings(p.Value,100), "page"=>p.Value.ValueKind==JsonValueKind.Number&&Integer(p.Value,1000000),"favoritesOnly"=>Bool(p.Value),
          "view"=>Text(p.Value,32)&&new[]{"list","grid","tags"}.Contains(p.Value.GetString()),
          "sort"=>Text(p.Value,32)&&new[]{"name","type","modifiedAsc","modifiedDesc"}.Contains(p.Value.GetString()),
          _=>Text(p.Value,96)});
    }
    private static bool History(JsonElement value)
    {
        if(!Object(value,"entries index")||!value.TryGetProperty("entries",out var entries)||entries.ValueKind!=JsonValueKind.Array||entries.GetArrayLength()>32||!value.TryGetProperty("index",out var index)||!index.TryGetInt32(out var i)||i< -1||i>=entries.GetArrayLength())return false;
        return entries.EnumerateArray().All(e=>Object(e,"page resources projects scroll")&&e.TryGetProperty("page",out var p)&&p.ValueKind==JsonValueKind.String&&Pages.Contains(p.GetString()!)&&Optional(e,"resources",Resources)&&Optional(e,"projects",Projects)&&Optional(e,"scroll",v=>Number(v,10000000)));
    }
}


