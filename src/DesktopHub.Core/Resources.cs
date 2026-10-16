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
    public string CategoryId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed record TagCategory(string Id, string Name);
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
    public int SchemaVersion { get; set; } = 2;
    public bool? AutoTagByModificationTime { get; set; }
    public List<SourceRoot> Sources { get; set; } = [];
    public List<Resource> Resources { get; set; } = [];
    public List<ResourceSource> ResourceSources { get; set; } = [];
    public List<Tag> Tags { get; set; } = [];
    public List<TagCategory> TagCategories { get; set; } = [new("theme", "主题"), new("period", "时期"), new("custom", "自定义")];
    public List<ResourceTag> ResourceTags { get; set; } = [];
    public List<TagSuggestion> Suggestions { get; set; } = [];
    public List<SavedResourceFilter> SavedFilters { get; set; } = [];
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
        Tags = value.Tags.Select(x => x with { }).ToList(), TagCategories = [.. value.TagCategories], Suggestions = value.Suggestions.Select(x => x with { }).ToList(),
        ResourceSources = [.. value.ResourceSources], ResourceTags = [.. value.ResourceTags],
        SavedFilters = value.SavedFilters.Select(CloneFilter).ToList()
    };
    internal static SavedResourceFilter CloneFilter(SavedResourceFilter filter) => filter with { TagIds = [.. filter.TagIds] };
    private void Load()
    {
        if (!File.Exists(FilePath)) return;
        try
        {
            ResourceLibrary value;
            using (var input = File.OpenRead(FilePath))
            {
                if (input.Length > 128 * 1024 * 1024) throw new InvalidDataException("资源元数据超过 128 MiB。");
                value = JsonSerializer.Deserialize<ResourceLibrary>(input, Json) ?? throw new InvalidDataException("空资源配置。");
            }
            var migrating = value.SchemaVersion == 1;
            Validate(value); current = value;
            if (migrating) Update(_ => { });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException or TargetValidationException)
        { RecoveryWarning = "资源配置需要检查，原文件保留且停止写入。请检查 resources.json：" + ex.Message; }
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
                if (File.Exists(FilePath)) AtomicFileReplacement.Replace(temporary, FilePath); else File.Move(temporary, FilePath);
                current = next;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }
    private static void Validate(ResourceLibrary value)
    {
        if (value.SchemaVersion is not (1 or 2) || value.Sources is null || value.Resources is null || value.Tags is null ||
            value.ResourceSources is null || value.ResourceTags is null || value.Suggestions is null || value.SavedFilters is null) throw new InvalidDataException("资源配置版本或列表无效。");
        value.SchemaVersion = 2;
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
        if (value.TagCategories is null || value.TagCategories.Any(c => c is null ||
            (c.Id is not ("theme" or "period" or "custom") && !Guid.TryParse(c.Id, out _)))) throw new InvalidDataException("标签分类编号无效。");
        foreach (var category in value.TagCategories) ValidateCategoryName(category.Name);
        if (value.TagCategories.Select(c => c.Id).Distinct().Count() != value.TagCategories.Count ||
            value.TagCategories.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.TagCategories.Count ||
            new[] { "theme", "period", "custom" }.Any(id => !value.TagCategories.Any(c => c.Id == id))) throw new InvalidDataException("标签分类缺失或重复。");
        foreach (var tag in value.Tags)
        {
            ValidateTag(tag.Name, tag.TagType);
            if (string.IsNullOrEmpty(tag.CategoryId)) tag.CategoryId = tag.TagType; // Existing libraries keep all tag relationships.
            if (!value.TagCategories.Any(c => c.Id == tag.CategoryId)) throw new InvalidDataException("标签分类不存在。");
        }
        if (value.Tags.DistinctBy(x => x.Name, StringComparer.OrdinalIgnoreCase).Count() != value.Tags.Count) throw new InvalidDataException("标签名称重复。");
        foreach (var suggestion in value.Suggestions) if (!resources.Contains(suggestion.ResourceId) || suggestion.Status is not ("Pending" or "Accepted" or "Rejected")) throw new InvalidDataException("建议关系无效。");
        if (value.SavedFilters.Any(filter => filter is null)) throw new InvalidDataException("保存筛选包含空条目。");
        Ids(value.SavedFilters.Select(filter => filter.Id));
        foreach (var filter in value.SavedFilters) ValidateFilter(filter);
        if (value.SavedFilters.Select(filter => filter.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.SavedFilters.Count) throw new InvalidDataException("保存筛选名称重复。");
    }
    internal static void ValidateFilter(SavedResourceFilter filter)
    {
        if (!Guid.TryParse(filter.Id, out var id) || id == Guid.Empty || string.IsNullOrWhiteSpace(filter.Name) || filter.Name.Length > 80 || filter.Name.Any(char.IsControl) ||
            filter.Query is null || filter.Query.Length > 200 || filter.Query.Any(char.IsControl) || filter.TagIds is null || filter.TagIds.Count > 500 ||
            filter.TagIds.Distinct(StringComparer.Ordinal).Count() != filter.TagIds.Count || filter.TagIds.Any(tagId => !Guid.TryParse(tagId, out var tagGuid) || tagGuid == Guid.Empty) ||
            filter.Extension is { Length: > 260 } || filter.Extension?.Any(char.IsControl) == true || filter.Sort is not ("type" or "name" or "modifiedAsc" or "modifiedDesc"))
            throw new ArgumentException("保存筛选名称、条件或排序无效。");
        filter.Name = filter.Name.Trim();
        // Missing label IDs remain persisted. Resolution blocks execution until the user edits the filter.
    }
    public static void ValidateTag(string name, string kind)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 80 || name.Any(char.IsControl) || kind is not ("theme" or "period" or "custom")) throw new ArgumentException("标签名称最多 80 字，类型为主题、时期或自定义。");
    }
    public static void ValidateCategoryName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 40 || name.Any(char.IsControl)) throw new ArgumentException("分类名称必填，最多 40 字。");
    }
}
