using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopHub.Core;

public sealed record SourceRoot
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Path { get; set; } = "";
    public string SourceType { get; set; } = "custom";
    public bool RecursiveEnabled { get; set; }
    public bool WatchEnabled { get; set; } = true;
    public DateTimeOffset AuthorizedAt { get; set; } = DateTimeOffset.UtcNow;
    public string AvailabilityStatus { get; set; } = "Active";
    public string MonitoringStatus { get; set; } = "Starting";
    public string? Error { get; set; }
    public DateTimeOffset? LastReconciledAt { get; set; }
}
public sealed record Resource
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string CanonicalPath { get; set; } = "";
    public string LastKnownPath { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string ResourceType { get; set; } = "file";
    public string Extension { get; set; } = "";
    public string? FilesystemIdentity { get; set; }
    public DateTimeOffset CreationTime { get; set; }
    public DateTimeOffset ModificationTime { get; set; }
    public string Status { get; set; } = "Active";
    public bool Favorite { get; set; }
    public bool CloudPlaceholder { get; set; }
    public bool ReparsePoint { get; set; }
    public bool ManualTarget { get; set; }
    public DateTimeOffset FirstIndexedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed record ResourceSource(string ResourceId, string SourceRootId, bool Explicit = false);
public sealed record Tag
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string TagType { get; set; } = "custom";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed record ResourceTag(string ResourceId, string TagId);
public sealed record TagSuggestion
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ResourceId { get; set; } = "";
    public string SuggestedName { get; set; } = "";
    public string TagType { get; set; } = "theme";
    public string SuggestionBasis { get; set; } = "";
    public string Status { get; set; } = "Pending";
    public DateTimeOffset GeneratedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed record ResourceLibrary
{
    public int SchemaVersion { get; set; } = 1;
    public bool? AutoTagByModificationTime { get; set; }
    public List<SourceRoot> Sources { get; set; } = [];
    public List<Resource> Resources { get; set; } = [];
    public List<ResourceSource> ResourceSources { get; set; } = [];
    public List<Tag> Tags { get; set; } = [];
    public List<ResourceTag> ResourceTags { get; set; } = [];
    public List<TagSuggestion> Suggestions { get; set; } = [];
}

// A separate atomic metadata file keeps resource recovery independent from apps/scenes.
public sealed class ResourceStore
{
    public static JsonSerializerOptions Json { get; } = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    public string FilePath { get; }
    public string? RecoveryWarning { get; private set; }
    private ResourceLibrary current = new();
    private readonly object gate = new();
    public ResourceStore(string directory) { FilePath = Path.Combine(Path.GetFullPath(directory), "resources.json"); Load(); }
    public ResourceLibrary Snapshot() { lock (gate) return Clone(current); }
    internal T Read<T>(Func<ResourceLibrary, T> reader) { lock (gate) return reader(current); }
    private static ResourceLibrary Clone(ResourceLibrary value) => value with
    {
        Sources = value.Sources.Select(x => x with { }).ToList(), Resources = value.Resources.Select(x => x with { }).ToList(),
        Tags = value.Tags.Select(x => x with { }).ToList(), Suggestions = value.Suggestions.Select(x => x with { }).ToList(),
        ResourceSources = [.. value.ResourceSources], ResourceTags = [.. value.ResourceTags]
    };
    private void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            using var input = File.OpenRead(FilePath);
            if (input.Length > 128 * 1024 * 1024) throw new InvalidDataException("资源元数据超过 128 MiB。");
            var value = JsonSerializer.Deserialize<ResourceLibrary>(input, Json) ?? throw new InvalidDataException("空资源配置。");
            Validate(value); current = value;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException or TargetValidationException)
        { RecoveryWarning = "资源配置未载入，原文件保留且停止写入。请检查 resources.json：" + ex.Message; }
    }
    public void Update(Action<ResourceLibrary> mutation)
    {
        lock (gate)
        {
            if (RecoveryWarning is not null) throw new InvalidOperationException(RecoveryWarning);
            var next = Clone(current); mutation(next); Validate(next);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
            if (bytes.Length > 128 * 1024 * 1024) throw new InvalidDataException("资源元数据超过 128 MiB，未保存。");
            var directory = Path.GetDirectoryName(FilePath)!; Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, "resources." + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                { stream.Write(bytes); stream.Flush(true); }
                if (File.Exists(FilePath)) File.Replace(temporary, FilePath, null); else File.Move(temporary, FilePath);
                current = next;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    private static void Validate(ResourceLibrary value)
    {
        if (value.SchemaVersion != 1 || value.Sources is null || value.Resources is null || value.Tags is null ||
            value.ResourceSources is null || value.ResourceTags is null || value.Suggestions is null) throw new InvalidDataException("资源配置版本或列表无效。");
        if (value.Sources.Any(x => x is null) || value.Resources.Any(x => x is null) || value.Tags.Any(x => x is null) || value.Suggestions.Any(x => x is null) || value.ResourceSources.Any(x => x is null) || value.ResourceTags.Any(x => x is null)) throw new InvalidDataException("资源配置包含空条目。");
        static HashSet<string> Ids(IEnumerable<string> ids) { var result = new HashSet<string>(); foreach (var id in ids) if (!Guid.TryParse(id, out _) || !result.Add(id)) throw new InvalidDataException("资源编号无效或重复。"); return result; }
        var sources = Ids(value.Sources.Select(x => x.Id)); var resources = Ids(value.Resources.Select(x => x.Id)); var tags = Ids(value.Tags.Select(x => x.Id)); Ids(value.Suggestions.Select(x => x.Id));
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in value.Sources)
        {
            source.Path = ResourceScanner.Canonical(source.Path);
            if (!paths.Add(source.Path) || source.SourceType is not ("desktop" or "publicDesktop" or "custom")) throw new InvalidDataException("来源重复或类型无效。");
        }
        paths.Clear();
        foreach (var resource in value.Resources)
        {
            resource.CanonicalPath = ResourceScanner.Canonical(resource.CanonicalPath);
            if (!paths.Add(resource.CanonicalPath) || resource.Status is not ("Active" or "Missing" or "PendingRepair" or "SourceUnavailable") || resource.ResourceType is not ("file" or "folder")) throw new InvalidDataException("资源路径重复或状态无效。");
        }
        if (value.ResourceSources.DistinctBy(x => (x.ResourceId, x.SourceRootId)).Count() != value.ResourceSources.Count || value.ResourceTags.Distinct().Count() != value.ResourceTags.Count) throw new InvalidDataException("重复关系。");
        foreach (var relation in value.ResourceSources) if (!resources.Contains(relation.ResourceId) || !sources.Contains(relation.SourceRootId)) throw new InvalidDataException("失效来源关系。");
        foreach (var relation in value.ResourceTags) if (!resources.Contains(relation.ResourceId) || !tags.Contains(relation.TagId)) throw new InvalidDataException("失效标签关系。");
        foreach (var tag in value.Tags) ValidateTag(tag.Name, tag.TagType);
        if (value.Tags.DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Count() != value.Tags.Count) throw new InvalidDataException("标签名称重复。");
        foreach (var suggestion in value.Suggestions) if (!resources.Contains(suggestion.ResourceId) || suggestion.Status is not ("Pending" or "Accepted" or "Rejected")) throw new InvalidDataException("建议关系无效。");
    }
    public static void ValidateTag(string name, string kind)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl) || kind is not ("theme" or "period" or "custom")) throw new ArgumentException("标签名称最多 80 字，类型为主题、时期或自定义。");
    }
}
