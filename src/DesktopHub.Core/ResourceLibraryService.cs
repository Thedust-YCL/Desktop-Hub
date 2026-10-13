using System.Collections.Concurrent;

namespace DesktopHub.Core;

public sealed record ResourceQuery(string Query = "", List<string>? TagIds = null, bool FavoritesOnly = false, bool InvalidOnly = false, string? Status = null, int Page = 0, string? Extension = null, string Sort = "type");
public sealed record ResourceItem(Resource Resource, List<Tag> Tags, List<TagSuggestion> Suggestions);
public sealed record ResourcePage(List<ResourceItem> Items, int Total, int Page, int PageSize, List<SourceRoot> Sources, List<Tag> Tags, string? Warning, bool? AutoTagByModificationTime = null, List<TagCategory>? TagCategories = null, List<string>? Extensions = null);

public sealed class ResourceLibraryService : IDisposable
{
    public ResourceStore Store { get; }
    public event Action? Changed;
    public event Action<ScanProgress>? Progress;
    private readonly string excluded;
    private readonly SemaphoreSlim serial = new(1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> operations = new();
    private readonly Dictionary<string, (ScanPreview Preview, ScanResult Scan, string? SourceId)> previews = [];
    private readonly Dictionary<string, (string Path, string SourceId, DateTimeOffset Expires)> browses = [];
    private readonly List<FileSystemWatcher> watchers = [];
    private readonly object watchGate = new();
    private readonly List<(string Old, string New)> renames = [];
    private readonly Timer debounce, recovery;
    private bool disposed;
    private DateTimeOffset? eventBatchStarted;
    private int backgroundQueued;
    private bool refreshRequested;
    public int WatcherCount { get { lock (watchGate) return watchers.Count; } }
    public ResourceLibraryService(string directory)
    {
        excluded = Path.GetFullPath(directory); Store = new(directory);
        debounce = new Timer(_ => QueueRefresh(), null, Timeout.Infinite, Timeout.Infinite);
        recovery = new Timer(_ =>
        {
            if (Store.Read(data => data.Sources.Any(s => s.AvailabilityStatus != "Active" || (s.WatchEnabled && s.MonitoringStatus != "Watching") || !Directory.Exists(s.Path)) || data.Resources.Any(r => r.ManualTarget && r.Status == "SourceUnavailable"))) QueueRefresh();
        }, null, 60000, 60000);
    }
    public Task StartAsync() => RefreshAsync();
    public void Cancel(string operationId) { if (operations.TryGetValue(operationId, out var token)) token.Cancel(); lock (previews) previews.Remove(operationId); }
    private async Task<T> Operation<T>(string id, Func<CancellationToken, T> action)
    {
        if (id.Length is < 1 or > 96 || id.Any(char.IsControl)) throw new ArgumentException("操作编号无效。");
        using var token = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        if (!operations.TryAdd(id, token)) throw new InvalidOperationException("操作正在进行。");
        try { return await Task.Run(() => action(token.Token), token.Token); }
        finally { operations.TryRemove(id, out _); }
    }
    public Task<ScanPreview> PreviewAsync(string path, string kind, bool recursive, string operationId, string? sourceId = null)
    {
        return Operation(operationId, token =>
        {
            if (kind is not ("desktop" or "publicDesktop" or "custom")) throw new ArgumentException("来源类型无效。");
            path = ResourceScanner.Canonical(path);
            if (ResourceScanner.Within(path, excluded)) throw new ArgumentException("应用自身的数据目录不能成为索引来源。");
            var result = ResourceScanner.Scan(path, recursive, excluded, token, (count, current) => Progress?.Invoke(new(operationId, count, current, "Scanning")));
            if (!result.Available) throw new IOException(result.Error);
            token.ThrowIfCancellationRequested();
            var preview = new ScanPreview(operationId, path, kind, recursive, result.Items.Count(x => x.ResourceType == "file"), result.Items.Count(x => x.ResourceType == "folder"), ResourceScanner.CloudPath(path), result.Warnings, DateTimeOffset.UtcNow.AddMinutes(15));
            lock (previews)
            {
                foreach (var key in previews.Where(x => x.Value.Preview.ExpiresAt < DateTimeOffset.UtcNow).Select(x => x.Key).ToArray()) previews.Remove(key);
                if (previews.Count >= 8) throw new InvalidOperationException("请关闭已有扫描预览后重试。");
                previews[operationId] = (preview, result, sourceId);
            }
            return preview;
        });
    }
    public Task<ScanPreview> PreviewSourceAsync(string sourceId, bool recursive, string operationId)
    {
        var source = Store.Snapshot().Sources.SingleOrDefault(x => x.Id == sourceId) ?? throw new ArgumentException("来源不存在。");
        return PreviewAsync(source.Path, source.SourceType, recursive, operationId, sourceId);
    }
    public Task<ScanPreview> PreviewBrowseAsync(string token, string operationId)
    { var scope = BrowseScope(token); return PreviewAsync(scope.Path, "custom", true, operationId); }
    public async Task ConfirmAsync(string token, bool? autoTagByModificationTime = null)
    {
        (ScanPreview Preview, ScanResult Scan, string? SourceId) draft;
        lock (previews) { if (!previews.TryGetValue(token, out draft) || draft.Preview.ExpiresAt < DateTimeOffset.UtcNow) throw new ArgumentException("预览已失效，请重新扫描。"); }
        await serial.WaitAsync(lifetime.Token);
        try
        {
            await Task.Run(() => Store.Update(data =>
            {
                if (autoTagByModificationTime.HasValue) data.AutoTagByModificationTime = autoTagByModificationTime;
                var source = data.Sources.SingleOrDefault(x => x.Path.Equals(draft.Preview.Path, StringComparison.OrdinalIgnoreCase));
                if (source is null) { source = new() { Path = draft.Preview.Path, SourceType = draft.Preview.SourceType }; data.Sources.Add(source); }
                source.RecursiveEnabled = draft.Preview.Recursive; source.WatchEnabled = true;
                if (!source.RecursiveEnabled)
                {
                    var byId = data.Resources.ToDictionary(r => r.Id);
                    data.ResourceSources.RemoveAll(link => link.SourceRootId == source.Id && !link.Explicit && !ResourceScanner.SameParent(byId[link.ResourceId].CanonicalPath, Path.Combine(source.Path, "_")));
                    RemoveOrphans(data);
                }
                Merge(data, source, draft.Scan, []);
                if (autoTagByModificationTime == true) AddModificationTimeTags(data, data.Resources);
            }), lifetime.Token);
            lock (previews) previews.Remove(token);
            Changed?.Invoke();
        }
        finally { serial.Release(); }
        await RefreshAsync(); // close the preview/confirmation event gap before watching
    }
    public async Task RefreshAsync(string? operationId = null)
    {
        if (disposed || Store.RecoveryWarning is not null) return;
        var id = operationId ?? Guid.NewGuid().ToString("N");
        await serial.WaitAsync(lifetime.Token);
        try
        {
            await Operation(id, token =>
            {
                var snapshot = Store.Snapshot(); if (snapshot.Sources.Count == 0 && !snapshot.Resources.Any(r => r.ManualTarget)) return true;
                List<(string Old, string New)> hints; lock (watchGate) { hints = [.. renames]; renames.Clear(); }
                var scans = new Dictionary<string, ScanResult>();
                foreach (var source in snapshot.Sources)
                {
                    ScanResult result = new([], [], [], false, "来源不可用。");
                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        result = ResourceScanner.Scan(source.Path, source.RecursiveEnabled, excluded, token, (count, path) => Progress?.Invoke(new(id, count, path, "Reconciling")));
                        if (result.Available) break;
                        if (token.WaitHandle.WaitOne(150 * (attempt + 1))) token.ThrowIfCancellationRequested();
                    }
                    // Explicitly added single items receive only metadata reads, not deep scans.
                    foreach (var link in snapshot.ResourceSources.Where(x => x.SourceRootId == source.Id && x.Explicit))
                    {
                        var resource = snapshot.Resources.Single(x => x.Id == link.ResourceId);
                        var path = resource.CanonicalPath;
                        foreach (var hint in hints) if (path.Equals(hint.Old, StringComparison.OrdinalIgnoreCase) && ResourceScanner.SameParent(hint.Old, hint.New)) path = hint.New;
                        try { ResourceScanner.EnsureSafe(path, true); var item = ResourceScanner.Read(path); if (!result.Items.Any(x => x.CanonicalPath.Equals(path, StringComparison.OrdinalIgnoreCase))) result.Items.Add(item); }
                        catch (FileNotFoundException) { }
                        catch (DirectoryNotFoundException) { }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { result.InaccessiblePaths.Add(path); }
                    }
                    scans[source.Id] = result;
                }
                token.ThrowIfCancellationRequested();
                Store.Update(data =>
                {
                    foreach (var source in data.Sources) Merge(data, source, scans[source.Id], hints);
                    var relations = data.ResourceSources.ToLookup(x => x.ResourceId);
                    var observed = scans.ToDictionary(x => x.Key, x => x.Value.Items.Select(r => r.CanonicalPath).ToHashSet(StringComparer.OrdinalIgnoreCase));
                    // A healthy overlapping source takes precedence over an unavailable source.
                    foreach (var resource in data.Resources)
                    {
                        if (resource.ManualTarget)
                        {
                            try { ResourceScanner.EnsureSafe(resource.CanonicalPath); var actual = ResourceScanner.Read(resource.CanonicalPath); resource.LastSeenAt = DateTimeOffset.UtcNow; resource.ModificationTime = actual.ModificationTime; if (resource.Status != "PendingRepair") resource.Status = "Active"; }
                            catch (FileNotFoundException) { if (resource.Status != "PendingRepair") resource.Status = "Missing"; }
                            catch (DirectoryNotFoundException) { if (resource.Status != "PendingRepair") resource.Status = Directory.Exists(Path.GetDirectoryName(resource.CanonicalPath)) ? "Missing" : "SourceUnavailable"; }
                            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { if (resource.Status != "PendingRepair") resource.Status = "SourceUnavailable"; }
                            continue;
                        }
                        var links = relations[resource.Id].ToArray();
                        var seen = links.Any(link => observed[link.SourceRootId].Contains(resource.CanonicalPath));
                        var uncertain = links.Any(link => !scans[link.SourceRootId].Available || scans[link.SourceRootId].InaccessiblePaths.Any(path => ResourceScanner.Within(resource.CanonicalPath, path)));
                        if (seen) { if (resource.Status != "PendingRepair") resource.Status = "Active"; }
                        else if (resource.Status != "PendingRepair") resource.Status = uncertain ? "SourceUnavailable" : "Missing";
                    }
                });
                SetupWatchers(); return true;
            });
            Changed?.Invoke();
        }
        finally { serial.Release(); }
    }
    private static void Merge(ResourceLibrary data, SourceRoot source, ScanResult scan, List<(string Old, string New)> hints)
    {
        source.AvailabilityStatus = scan.Available ? "Active" : "SourceUnavailable";
        source.Error = scan.Error ?? (scan.Warnings.Count > 0 ? string.Join("\n", scan.Warnings.Take(5)) : null);
        source.LastReconciledAt = DateTimeOffset.UtcNow;
        var paths = data.Resources.ToDictionary(x => x.CanonicalPath, StringComparer.OrdinalIgnoreCase);
        var observed = scan.Items.Select(x => x.CanonicalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var identities = data.Resources.Where(x => x.FilesystemIdentity is not null).ToLookup(x => x.FilesystemIdentity!);
        var linked = data.ResourceSources.Where(x => x.SourceRootId == source.Id).Select(x => x.ResourceId).ToHashSet();
        var suggestionKeys = data.Suggestions.Select(x => (x.ResourceId, x.SuggestedName)).ToHashSet();
        var directoryRenames = new List<(string Old, string New)>();
        var newResources = new List<Resource>();
        foreach (var item in scan.Items)
        {
            paths.TryGetValue(item.CanonicalPath, out var existing);
            if (existing is null && item.FilesystemIdentity is not null)
            {
                var candidates = identities[item.FilesystemIdentity].Where(x => !observed.Contains(x.CanonicalPath) && (ResourceScanner.SameParent(x.CanonicalPath, item.CanonicalPath) || directoryRenames.Any(rename => ResourceScanner.Within(x.CanonicalPath, rename.Old) && item.CanonicalPath.Equals(rename.New + x.CanonicalPath[rename.Old.Length..], StringComparison.OrdinalIgnoreCase)))).ToArray();
                if (candidates.Length == 1) existing = candidates[0];
            }
            if (existing is null)
            {
                var hint = hints.LastOrDefault(x => x.New.Equals(item.CanonicalPath, StringComparison.OrdinalIgnoreCase) && ResourceScanner.SameParent(x.Old, x.New));
                if (hint.Old is not null) existing = data.Resources.SingleOrDefault(x => x.CanonicalPath.Equals(hint.Old, StringComparison.OrdinalIgnoreCase) &&
                    (x.FilesystemIdentity is null || item.FilesystemIdentity is null || x.FilesystemIdentity == item.FilesystemIdentity));
            }
            if (existing is null)
            {
                existing = item; data.Resources.Add(existing); AddSuggestions(data, existing, suggestionKeys);
                newResources.Add(existing);
            }
            else
            {
                if (existing.ResourceType == "folder" && existing.FilesystemIdentity is not null && existing.FilesystemIdentity == item.FilesystemIdentity && !existing.CanonicalPath.Equals(item.CanonicalPath, StringComparison.OrdinalIgnoreCase)) directoryRenames.Add((existing.CanonicalPath, item.CanonicalPath));
                existing.LastKnownPath = existing.CanonicalPath;
                existing.CanonicalPath = item.CanonicalPath; existing.DisplayName = item.DisplayName; existing.Extension = item.Extension;
                existing.ResourceType = item.ResourceType; existing.CreationTime = item.CreationTime; existing.ModificationTime = item.ModificationTime;
                existing.FilesystemIdentity = item.FilesystemIdentity; existing.CloudPlaceholder = item.CloudPlaceholder; existing.ReparsePoint = item.ReparsePoint;
                existing.LastSeenAt = DateTimeOffset.UtcNow; if (existing.Status != "PendingRepair") existing.Status = "Active";
            }
            paths[item.CanonicalPath] = existing;
            if (linked.Add(existing.Id)) data.ResourceSources.Add(new(existing.Id, source.Id));
        }
        if (data.AutoTagByModificationTime == true) AddModificationTimeTags(data, newResources);
    }
    private static void AddSuggestions(ResourceLibrary data, Resource resource, HashSet<(string, string)> suggestionKeys)
    {
        var candidates = new List<(string Name, string Kind, string Basis)> { (resource.ModificationTime.ToLocalTime().ToString("yyyy年M月"), "period", "根据文件修改时间") };
        var text = resource.DisplayName + " " + Path.GetFileName(Path.GetDirectoryName(resource.CanonicalPath));
        foreach (var word in new[] { "课程", "作业", "设计", "工作", "照片" }) if (text.Contains(word, StringComparison.OrdinalIgnoreCase)) candidates.Add((word, "theme", "名称或所在目录包含“" + word + "”"));
        foreach (var candidate in candidates) if (suggestionKeys.Add((resource.Id, candidate.Name)))
            data.Suggestions.Add(new() { ResourceId = resource.Id, SuggestedName = candidate.Name, TagType = candidate.Kind, SuggestionBasis = candidate.Basis });
    }
    private static void AddModificationTimeTags(ResourceLibrary data, IEnumerable<Resource> resources)
    {
        var tags = data.Tags.ToDictionary(t => t.Name, StringComparer.OrdinalIgnoreCase);
        var links = data.ResourceTags.ToHashSet();
        var suggestions = data.Suggestions.ToLookup(s => s.ResourceId);
        foreach (var resource in resources)
        {
            var name = resource.ModificationTime.ToLocalTime().ToString("yyyy年M月");
            if (!tags.TryGetValue(name, out var tag))
            {
                tag = new() { Name = name, TagType = "period" }; data.Tags.Add(tag); tags.Add(name, tag);
            }
            var link = new ResourceTag(resource.Id, tag.Id);
            if (links.Add(link)) data.ResourceTags.Add(link);
            foreach (var suggestion in suggestions[resource.Id].Where(s => s.TagType == "period" && s.SuggestedName == name && s.Status == "Pending")) suggestion.Status = "Accepted";
        }
    }
    public ResourcePage Query(ResourceQuery query)
    {
        return Store.Read(data =>
        {
        var tags = query.TagIds ?? [];
        if (query.Page < 0 || query.Page > 100000 || query.Query.Length > 200 || tags.Any(id => !data.Tags.Any(t => t.Id == id))) throw new ArgumentException("搜索条件无效。");
        var byResource = data.ResourceTags.ToLookup(x => x.ResourceId, x => x.TagId);
        var matchingTags = data.Tags.Where(t => t.Name.Contains(query.Query, StringComparison.OrdinalIgnoreCase)).Select(t => t.Id).ToHashSet();
        var candidates = data.Resources.Where(r => (!query.FavoritesOnly || r.Favorite) && (!query.InvalidOnly || r.Status != "Active") && (query.Status is null || r.Status == query.Status) &&
            (string.IsNullOrEmpty(query.Query) || r.DisplayName.Contains(query.Query, StringComparison.OrdinalIgnoreCase) || byResource[r.Id].Any(matchingTags.Contains)) && tags.All(id => byResource[r.Id].Contains(id)))
            .ToArray();
        static string ExtensionKey(Resource resource) => resource.ResourceType == "folder" ? "__folders" : string.IsNullOrEmpty(resource.Extension) ? "__none" : resource.Extension.ToLowerInvariant();
        if (query.Extension is { Length: > 260 } || query.Extension?.Any(char.IsControl) == true) throw new ArgumentException("扩展名筛选无效。");
        var filtered = candidates.Where(r => string.IsNullOrEmpty(query.Extension) || ExtensionKey(r).Equals(query.Extension, StringComparison.OrdinalIgnoreCase));
        IOrderedEnumerable<Resource> ordered = query.Sort switch
        {
            "type" => filtered.OrderBy(r => r.ResourceType == "folder" ? 0 : 1).ThenBy(r => r.Extension, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase),
            "name" => filtered.OrderBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase),
            "modifiedAsc" => filtered.OrderBy(r => r.ModificationTime),
            "modifiedDesc" => filtered.OrderByDescending(r => r.ModificationTime),
            _ => throw new ArgumentException("资源排序无效。")
        };
        var all = ordered.ThenBy(r => r.Id).ToArray();
        var page = Math.Min(query.Page, Math.Max(0, (all.Length - 1) / 60));
        var suggestions = data.Suggestions.Where(x => x.Status == "Pending").ToLookup(x => x.ResourceId);
        var byTag = data.Tags.ToDictionary(t => t.Id);
        return new ResourcePage(all.Skip(page * 60).Take(60).Select(r => new ResourceItem(r with { }, byResource[r.Id].Select(id => byTag[id] with { }).ToList(), suggestions[r.Id].Select(s => s with { }).ToList())).ToList(), all.Length, page, 60, data.Sources.Select(s => s with { }).ToList(), data.Tags.Select(t => t with { }).ToList(), Store.RecoveryWarning, data.AutoTagByModificationTime, [.. data.TagCategories], candidates.Select(ExtensionKey).Distinct().Order(StringComparer.OrdinalIgnoreCase).ToList());
        });
    }
    public async Task MutateAsync(Action<ResourceLibrary> mutation)
    {
        await serial.WaitAsync(lifetime.Token);
        try { await Task.Run(() => Store.Update(mutation), lifetime.Token); Changed?.Invoke(); }
        finally { serial.Release(); }
    }
    public Task FavoriteAsync(string id, bool favorite) => MutateAsync(data => Find(data, id).Favorite = favorite);

    /// <summary>Operations: addTags/removeTags use label IDs; favorite/unfavorite use no values;
    /// acceptSuggestions uses one pending suggestion ID per resource, all with identical name and type.</summary>
    public Task BatchAsync(IReadOnlyList<string> resourceIds, string operation, IReadOnlyList<string> values)
    {
        ArgumentNullException.ThrowIfNull(resourceIds); ArgumentNullException.ThrowIfNull(values);
        var ids = resourceIds.ToArray(); var requested = values.ToArray();
        return MutateAsync(data => ApplyBatch(data, ids, operation, requested));
    }

    public Task BatchAcceptSuggestionsAsync(IReadOnlyList<string> resourceIds, string suggestionName, string suggestionType)
    {
        ArgumentNullException.ThrowIfNull(resourceIds);
        var ids = resourceIds.ToArray();
        return MutateAsync(data =>
        {
            ResourceStore.ValidateTag(suggestionName, suggestionType);
            var wanted = ids.ToHashSet(StringComparer.Ordinal);
            var suggestions = data.Suggestions.Where(s => wanted.Contains(s.ResourceId) && s.Status == "Pending" && s.TagType == suggestionType &&
                s.SuggestedName.Trim().Equals(suggestionName.Trim(), StringComparison.OrdinalIgnoreCase)).Select(s => s.Id).ToArray();
            ApplyBatch(data, ids, "acceptSuggestions", suggestions);
        });
    }

    private static void ApplyBatch(ResourceLibrary data, IReadOnlyList<string> ids, string operation, IReadOnlyList<string> values)
    {
        if (ids.Count is < 1 or > 500 || ids.Distinct(StringComparer.Ordinal).Count() != ids.Count || values.Count > 500 || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new ArgumentException("批量操作需要 1–500 个不同资源，条件不能重复或超过 500 项。");
        var resources = ids.Select(id => Find(data, id)).ToArray();
        if (resources.Any(resource => resource.Status != "Active")) throw new ArgumentException("批量整理仅支持正常资源，请将失效索引清理与整理分开。");
        var wanted = ids.ToHashSet(StringComparer.Ordinal);
        switch (operation)
        {
            case "favorite":
            case "unfavorite":
                if (values.Count != 0) throw new ArgumentException("收藏操作不接受附加条件。");
                foreach (var resource in resources) resource.Favorite = operation == "favorite";
                break;
            case "addTags":
            case "removeTags":
                if (values.Count == 0 || values.Any(id => !data.Tags.Any(tag => tag.Id == id))) throw new ArgumentException("请选择有效标签。");
                var tags = values.ToHashSet(StringComparer.Ordinal);
                if (operation == "removeTags") data.ResourceTags.RemoveAll(link => wanted.Contains(link.ResourceId) && tags.Contains(link.TagId));
                else
                {
                    var links = data.ResourceTags.ToHashSet();
                    foreach (var resource in resources) foreach (var tagId in values)
                    { var link = new ResourceTag(resource.Id, tagId); if (links.Add(link)) data.ResourceTags.Add(link); }
                }
                break;
            case "acceptSuggestions":
                if (values.Count != ids.Count) throw new ArgumentException("每个资源必须选择一条同名同类型建议。");
                var selected = values.Select(id => data.Suggestions.SingleOrDefault(s => s.Id == id && s.Status == "Pending") ?? throw new ArgumentException("建议不存在或已处理。")).ToArray();
                var name = selected[0].SuggestedName.Trim(); var kind = selected[0].TagType;
                ResourceStore.ValidateTag(name, kind);
                if (selected.Select(s => s.ResourceId).Distinct().Count() != ids.Count || selected.Any(s => !wanted.Contains(s.ResourceId) || s.TagType != kind || !s.SuggestedName.Trim().Equals(name, StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException("所选建议必须覆盖全部资源且同名同类型。");
                var tag = data.Tags.SingleOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (tag is not null && tag.TagType != kind) throw new ArgumentException("已有同名标签类型不同，请编辑建议。");
                if (tag is null) { tag = new() { Name = name, TagType = kind, CategoryId = kind }; data.Tags.Add(tag); }
                foreach (var suggestion in selected)
                {
                    var link = new ResourceTag(suggestion.ResourceId, tag.Id); if (!data.ResourceTags.Contains(link)) data.ResourceTags.Add(link);
                    suggestion.Status = "Accepted";
                }
                break;
            default: throw new ArgumentException("批量操作类型无效。");
        }
    }

    public Task SaveFilterAsync(SavedResourceFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var requested = filter with { TagIds = filter.TagIds is null ? null! : [.. filter.TagIds] };
        return MutateAsync(data =>
        {
            ResourceStore.ValidateFilter(requested);
            if (requested.TagIds.Any(id => !data.Tags.Any(tag => tag.Id == id))) throw new ArgumentException("保存筛选引用的标签不存在。");
            var index = data.SavedFilters.FindIndex(existing => existing.Id == requested.Id);
            if (index < 0) data.SavedFilters.Add(ResourceStore.CloneFilter(requested));
            else data.SavedFilters[index] = ResourceStore.CloneFilter(requested);
        });
    }

    public Task DeleteFilterAsync(string filterId) => MutateAsync(data =>
    {
        if (data.SavedFilters.RemoveAll(filter => filter.Id == filterId) == 0) throw new ArgumentException("保存筛选不存在。");
    });

    public SavedResourceFilterStatus ResolveFilter(string filterId) => Store.Read(data =>
    {
        var filter = data.SavedFilters.SingleOrDefault(filter => filter.Id == filterId) ?? throw new ArgumentException("保存筛选不存在。");
        var tags = data.Tags.Select(tag => tag.Id).ToHashSet(StringComparer.Ordinal);
        return new SavedResourceFilterStatus(ResourceStore.CloneFilter(filter), filter.TagIds.Where(id => !tags.Contains(id)).ToList());
    });

    public ResourcePage QueryFilter(string filterId, int page = 0)
    {
        var resolved = ResolveFilter(filterId);
        if (!resolved.Valid) throw new ArgumentException("保存筛选的标签条件已失效，请编辑后使用。");
        var filter = resolved.Filter;
        return Query(new(filter.Query, filter.TagIds, filter.FavoritesOnly, Page: page, Extension: filter.Extension, Sort: filter.Sort));
    }
    public Task BeginRepairAsync(string id) => MutateAsync(data => { var item = Find(data, id); if (item.Status == "Active") throw new ArgumentException("正常资源无需修复。"); item.Status = "PendingRepair"; });
    public async Task RepairAsync(string id, string path)
    {
        await MutateAsync(data =>
        {
        ResourceScanner.EnsureSafe(path); var replacement = ResourceScanner.Read(path); var resource = Find(data, id);
        if (resource.Status == "Active") throw new ArgumentException("正常资源无需修复。");
        if (replacement.CloudPlaceholder) throw new ArgumentException("请先在 Windows 中使目标可用。");
        if (replacement.ResourceType != resource.ResourceType) throw new ArgumentException("修复目标的文件/文件夹类型须与原资源一致。");
        var duplicate = data.Resources.SingleOrDefault(x => x.Id != id && x.CanonicalPath.Equals(replacement.CanonicalPath, StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
        {
            resource.Favorite |= duplicate.Favorite;
            foreach (var link in data.ResourceTags.Where(x => x.ResourceId == duplicate.Id).ToArray()) if (!data.ResourceTags.Contains(new(id, link.TagId))) data.ResourceTags.Add(new(id, link.TagId));
            foreach (var link in data.ResourceSources.Where(x => x.ResourceId == duplicate.Id).ToArray()) if (!data.ResourceSources.Any(x => x.ResourceId == id && x.SourceRootId == link.SourceRootId)) data.ResourceSources.Add(new(id, link.SourceRootId, link.Explicit));
            RemoveResources(data, [duplicate.Id]);
        }
        resource.LastKnownPath = resource.CanonicalPath; resource.CanonicalPath = replacement.CanonicalPath; resource.DisplayName = replacement.DisplayName;
        resource.ResourceType = replacement.ResourceType; resource.Extension = replacement.Extension; resource.FilesystemIdentity = replacement.FilesystemIdentity;
        resource.CreationTime = replacement.CreationTime; resource.ModificationTime = replacement.ModificationTime; resource.CloudPlaceholder = false; resource.ReparsePoint = false; resource.Status = "Active";
        // A repair outside existing scopes is an explicitly authorized single item; no external directory scan.
        data.ResourceSources.RemoveAll(x => x.ResourceId == id && !data.Sources.Any(s => s.Id == x.SourceRootId && ResourceScanner.Within(resource.CanonicalPath, s.Path)));
        foreach (var source in data.Sources.Where(s => ResourceScanner.Within(resource.CanonicalPath, s.Path))) if (!data.ResourceSources.Any(x => x.ResourceId == id && x.SourceRootId == source.Id)) data.ResourceSources.Add(new(id, source.Id, true));
        resource.ManualTarget = !data.ResourceSources.Any(x => x.ResourceId == id);
        });
        SetupWatchers();
    }
    public Task CleanAsync(List<string> ids, bool confirmed) => MutateAsync(data =>
    {
        if (!confirmed || ids.Count == 0 || ids.Any(id => Find(data, id).Status is "Active" or "SourceUnavailable")) throw new ArgumentException("仅可确认清理缺失或待修复索引。");
        RemoveResources(data, ids);
    });
    public async Task RemoveSourceAsync(string id, bool confirmed)
    {
        if (!confirmed) throw new ArgumentException("请确认移除来源。");
        await MutateAsync(data => { if (data.Sources.RemoveAll(x => x.Id == id) == 0) throw new ArgumentException("来源不存在。"); data.ResourceSources.RemoveAll(x => x.SourceRootId == id); RemoveOrphans(data); });
        await RefreshAsync(); SetupWatchers();
    }
    public Task SaveCategoryAsync(string name) => MutateAsync(data =>
    {
        name = name.Trim(); ResourceStore.ValidateCategoryName(name);
        if (data.TagCategories.Any(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("已有同名分类。");
        data.TagCategories.Add(new(Guid.NewGuid().ToString("N"), name));
    });
    public Task SaveTagAsync(string? id, string name, string kind, string? categoryId = null) => MutateAsync(data =>
    {
        name = name.Trim(); ResourceStore.ValidateTag(name, kind);
        categoryId ??= kind is "theme" or "period" ? kind : null;
        if (string.IsNullOrWhiteSpace(categoryId) || !data.TagCategories.Any(c => c.Id == categoryId)) throw new ArgumentException("请为标签选择分类，或先新建分类。");
        if (data.Tags.Any(x => x.Id != id && x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new ArgumentException("已有同名标签。");
        var tag = id is null ? new Tag() : data.Tags.SingleOrDefault(x => x.Id == id) ?? throw new ArgumentException("标签不存在。");
        tag.Name = name; tag.TagType = kind; tag.CategoryId = categoryId; if (id is null) data.Tags.Add(tag);
    });
    public Task DeleteTagAsync(string id, bool confirmed) => MutateAsync(data => { if (!confirmed) throw new ArgumentException("请确认删除标签。"); data.Tags.RemoveAll(x => x.Id == id); data.ResourceTags.RemoveAll(x => x.TagId == id); });
    public Task BindTagAsync(string id, string tagId, bool bind) => MutateAsync(data =>
    {
        Find(data, id); if (!data.Tags.Any(x => x.Id == tagId)) throw new ArgumentException("标签不存在。");
        var link = new ResourceTag(id, tagId); if (!bind) data.ResourceTags.Remove(link); else if (!data.ResourceTags.Contains(link)) data.ResourceTags.Add(link);
    });
    public Task SuggestionAsync(string id, bool accept, string? editedName = null) => MutateAsync(data =>
    {
        var suggestion = data.Suggestions.SingleOrDefault(x => x.Id == id && x.Status == "Pending") ?? throw new ArgumentException("建议已处理。");
        if (accept)
        {
            var name = (editedName ?? suggestion.SuggestedName).Trim(); ResourceStore.ValidateTag(name, suggestion.TagType);
            var tag = data.Tags.SingleOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (tag is null) { tag = new() { Name = name, TagType = suggestion.TagType }; data.Tags.Add(tag); }
            var link = new ResourceTag(suggestion.ResourceId, tag.Id); if (!data.ResourceTags.Contains(link)) data.ResourceTags.Add(link);
        }
        suggestion.Status = accept ? "Accepted" : "Rejected";
    });
    public Task<FolderPage> BrowseAsync(string? resourceId, string? browseToken, string? childName, int page)
    {
        return Operation(Guid.NewGuid().ToString("N"), token =>
        {
            string path, sourceId;
            var data = Store.Snapshot();
            if (resourceId is not null)
            {
                var item = Find(data, resourceId); if (item.ResourceType != "folder" || item.Status != "Active") throw new ArgumentException("文件夹暂不可用。");
                path = item.CanonicalPath; sourceId = data.ResourceSources.FirstOrDefault(x => x.ResourceId == resourceId)?.SourceRootId ?? throw new ArgumentException("文件夹未在授权来源中。");
            }
            else
            {
                var scope = BrowseScope(browseToken!); path = scope.Path; sourceId = scope.SourceId;
                if (childName is not null) path = Child(path, childName);
            }
            var source = data.Sources.SingleOrDefault(x => x.Id == sourceId) ?? throw new ArgumentException("来源已移除。");
            if (!ResourceScanner.Within(path, source.Path)) throw new ArgumentException("超出授权来源。");
            var scan = ResourceScanner.Scan(path, false, excluded, token); if (!scan.Available) throw new IOException(scan.Error);
            var key = Guid.NewGuid().ToString("N");
            lock (browses) { foreach (var old in browses.Where(x => x.Value.Expires < DateTimeOffset.UtcNow).Select(x => x.Key).ToArray()) browses.Remove(old); if (browses.Count >= 64) browses.Clear(); browses[key] = (path, sourceId, DateTimeOffset.UtcNow.AddMinutes(15)); }
            var sorted = scan.Items.OrderBy(x => x.ResourceType == "folder" ? 0 : 1).ThenBy(x => x.Extension, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray(); page = Math.Clamp(page, 0, Math.Max(0, (sorted.Length - 1) / 60));
            return new FolderPage(key, path, sorted.Skip(page * 60).Take(60).ToList(), sorted.Length, page, scan.Warnings);
        });
    }
    public async Task AddBrowsedAsync(string token, string name)
    {
        var scope = BrowseScope(token); var path = Child(scope.Path, name);
        await MutateAsync(data =>
        {
            var source = data.Sources.SingleOrDefault(x => x.Id == scope.SourceId) ?? throw new ArgumentException("来源已移除。");
            if (!ResourceScanner.Within(path, source.Path)) throw new ArgumentException("超出授权来源。");
            ResourceScanner.EnsureSafe(path); var item = ResourceScanner.Read(path);
            Merge(data, source, new([item], [], [], true, null), []);
            var resource = data.Resources.Single(x => x.CanonicalPath.Equals(path, StringComparison.OrdinalIgnoreCase));
            data.ResourceSources.RemoveAll(x => x.ResourceId == resource.Id && x.SourceRootId == source.Id); data.ResourceSources.Add(new(resource.Id, source.Id, true));
        });
        SetupWatchers();
    }
    public Resource OpenTarget(string? id, string? token = null, string? name = null)
    {
        var data = Store.Snapshot();
        var resource = id is not null ? Find(data, id) : ResourceScanner.Read(Child(BrowseScope(token!).Path, name!));
        if (resource.Status != "Active") throw new IOException("引用不可用，请刷新或重新关联。");
        ResourceScanner.EnsureSafe(resource.CanonicalPath);
        var actual = ResourceScanner.Read(resource.CanonicalPath);
        if (actual.CloudPlaceholder || actual.ReparsePoint || (resource.FilesystemIdentity is not null && actual.FilesystemIdentity != resource.FilesystemIdentity)) throw new IOException("文件是云占位、链接或身份已变化，请在 Windows 中检查后刷新。");
        return actual;
    }
    private (string Path, string SourceId, DateTimeOffset Expires) BrowseScope(string token)
    {
        lock (browses) { if (!browses.TryGetValue(token, out var scope) || scope.Expires < DateTimeOffset.UtcNow || !Store.Snapshot().Sources.Any(x => x.Id == scope.SourceId)) throw new ArgumentException("浏览会话已失效，请重新打开文件夹。"); return scope; }
    }
    private static string Child(string parent, string name)
    {
        if (string.IsNullOrEmpty(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0 || name.Any(char.IsControl)) throw new ArgumentException("子项名称无效。");
        return ResourceScanner.Canonical(Path.Combine(parent, name));
    }
    private static Resource Find(ResourceLibrary data, string id) => data.Resources.SingleOrDefault(x => x.Id == id) ?? throw new ArgumentException("资源不存在，请刷新。");
    private static void RemoveResources(ResourceLibrary data, IEnumerable<string> ids)
    {
        var set = ids.ToHashSet(); data.Resources.RemoveAll(x => set.Contains(x.Id)); data.ResourceSources.RemoveAll(x => set.Contains(x.ResourceId));
        data.ResourceTags.RemoveAll(x => set.Contains(x.ResourceId)); data.Suggestions.RemoveAll(x => set.Contains(x.ResourceId));
    }
    private static void RemoveOrphans(ResourceLibrary data)
    { var linked = data.ResourceSources.Select(x => x.ResourceId).ToHashSet(); RemoveResources(data, data.Resources.Where(r => !r.ManualTarget && !linked.Contains(r.Id)).Select(r => r.Id).ToArray()); }
    private void Schedule(object? sender, FileSystemEventArgs args)
    {
        if (ResourceScanner.Within(args.FullPath, excluded)) return;
        lock (watchGate)
        {
            if (disposed) return;
            if (args is RenamedEventArgs renamed && renames.Count < 2048) renames.Add((renamed.OldFullPath, renamed.FullPath));
            eventBatchStarted ??= DateTimeOffset.UtcNow;
            debounce.Change((DateTimeOffset.UtcNow - eventBatchStarted.Value).TotalSeconds >= 2 ? 1 : 400, Timeout.Infinite);
        }
    }
    private void QueueRefresh()
    {
        lock (watchGate)
        {
            eventBatchStarted = null;
            if (disposed) return;
            if (backgroundQueued != 0) { refreshRequested = true; return; }
            backgroundQueued = 1;
        }
        _ = BackgroundRefresh();
    }
    private async Task BackgroundRefresh()
    {
        try
        {
            while (!disposed)
            {
                await RefreshAsync();
                lock (watchGate) { if (!refreshRequested) break; refreshRequested = false; }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Progress?.Invoke(new("background", 0, ex.Message, "Failed")); }
        finally
        {
            lock (watchGate) { backgroundQueued = 0; if (!disposed && refreshRequested) { refreshRequested = false; debounce.Change(150, Timeout.Infinite); } }
        }
    }
    private void SetupWatchers()
    {
        lock (watchGate)
        {
            if (disposed) return;
            foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear();
            var data = Store.Snapshot(); var statuses = new Dictionary<string, (string Status, string? Error)>();
            foreach (var source in data.Sources)
            {
                try
                {
                    if (!source.WatchEnabled) { statuses[source.Id] = ("Disabled", null); continue; }
                    ResourceScanner.EnsureSafe(source.Path);
                    Watch(source.Path, "*", source.RecursiveEnabled);
                    foreach (var link in data.ResourceSources.Where(x => x.SourceRootId == source.Id && x.Explicit))
                    {
                        var item = data.Resources.Single(x => x.Id == link.ResourceId);
                        if (!ResourceScanner.SameParent(item.CanonicalPath, Path.Combine(source.Path, "_")) && Directory.Exists(Path.GetDirectoryName(item.CanonicalPath))) Watch(Path.GetDirectoryName(item.CanonicalPath)!, Path.GetFileName(item.CanonicalPath), false);
                    }
                    statuses[source.Id] = ("Watching", null);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { statuses[source.Id] = ("Error", ex.Message); }
            }
            foreach (var item in data.Resources.Where(r => r.ManualTarget))
            {
                try { var parent = Path.GetDirectoryName(item.CanonicalPath)!; ResourceScanner.EnsureSafe(parent); Watch(parent, Path.GetFileName(item.CanonicalPath), false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
            }
            if (data.Sources.Count > 0) Store.Update(next => { foreach (var source in next.Sources) { source.MonitoringStatus = statuses[source.Id].Status; if (statuses[source.Id].Error is { } error) source.Error = error; } });
        }
    }
    private void Watch(string path, string filter, bool recursive)
    {
        var watcher = new FileSystemWatcher(path, filter) { IncludeSubdirectories = recursive, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Attributes, InternalBufferSize = 32768 };
        watcher.Created += Schedule; watcher.Deleted += Schedule; watcher.Changed += Schedule; watcher.Renamed += Schedule;
        watcher.Error += (_, _) => { lock (watchGate) if (!disposed) debounce.Change(200, Timeout.Infinite); };
        watcher.EnableRaisingEvents = true; watchers.Add(watcher);
    }
    public void Dispose()
    {
        lock (watchGate) { disposed = true; lifetime.Cancel(); debounce.Dispose(); recovery.Dispose(); foreach (var watcher in watchers) watcher.Dispose(); watchers.Clear(); }
    }
}
