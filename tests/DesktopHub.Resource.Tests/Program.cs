using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using DesktopHub.Core;

var root = Path.Combine(AppContext.BaseDirectory, "m22-owned-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var results = new List<object>(); var passed = 0; var failed = 0;
await Test("Native resource envelope budget accommodates 500 real GUID IDs per bounded list without expanding unrelated routes", async () =>
{
    bool Allowed(string type,int length)=>DesktopHub.Windows.ResourceRequestParser.EnvelopeAllowed(type,length);
    var ids=Enumerable.Range(0,500).Select(_=>Guid.NewGuid().ToString("D")).ToArray();
    var query=JsonSerializer.Serialize(new {version=1,id="ui-123",type="resources.query",payload=new{query=new string('课',200),extension=new string('课',260),tagIds=ids,selectedIds=ids,cleanupIds=ids,sort="modifiedDesc",page=100000,invalidOnly=true,favoritesOnly=true,status="SourceUnavailable"}});
    True(query.Length>16384);True(query.Length<=65536);True(Allowed("resources.query",query.Length));
    var batch=JsonSerializer.Serialize(new{version=1,id="ui-124",type="resources.batch",payload=new{resourceIds=ids,operation="addTags",values=ids}});True(Allowed("resources.batch",batch.Length));
    var legacyIds=Enumerable.Range(0,500).Select(_=>"              "+Guid.NewGuid().ToString("X")+"              ").ToArray();Equal(96,legacyIds[0].Length);
    var maximal=JsonSerializer.Serialize(new{version=1,id=new string('i',96),type="resources.query",payload=new{query=new string('课',200),extension=new string('课',260),tagIds=legacyIds,selectedIds=legacyIds,cleanupIds=legacyIds,page=100000,sort="modifiedDesc",favoritesOnly=true,invalidOnly=true,status="SourceUnavailable"}});
    True(maximal.Length>65536);True(Allowed("resources.query",maximal.Length));using(var document=JsonDocument.Parse(maximal)){var parsed=DesktopHub.Windows.ResourceRequestParser.Query(document.RootElement.GetProperty("payload"));Equal(legacyIds[0],parsed.SelectedIds[0]);}
    foreach(var type in new[]{"resources.query","resources.batch","resources.batchSuggestions","resources.filterSave"}){True(Allowed(type,256*1024));True(!Allowed(type,256*1024+1));}
    True(!Allowed("resources.open",16385));True(Allowed("apps.update",1024*1024));True(!Allowed("apps.update",1024*1024+1));
    await Task.CompletedTask;
});
await Test("A blocked legacy migration retains usable metadata and does not claim the resources were unloaded", async () =>
{
    var directory = NewDirectory("locked-migration"); var path = Path.Combine(directory,"resources.json");var folder=Fixture(0);
    File.WriteAllText(path,JsonSerializer.Serialize(new ResourceLibrary { SchemaVersion=1, Resources=[new(){Id=Id(),CanonicalPath=Path.Combine(folder,"owned.txt"),ManualTarget=true}] },ResourceStore.Json));
    var original=File.ReadAllText(path);
    using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)) {
        var store=new ResourceStore(directory);Equal(1,store.Snapshot().Resources.Count);Equal(2,store.Snapshot().SchemaVersion);True(store.RecoveryWarning is not null);True(!store.RecoveryWarning!.Contains("未载入"));
        await ThrowsAsync(()=>Task.Run(()=>store.Update(_=>{})));Equal(original,File.ReadAllText(path));
    }
});
await Test("Native resource request parsing strictly rejects malformed nested fields and operation values before mutation", async () =>
{
    object Parse(string method, string json) { using var doc = JsonDocument.Parse(json); return method switch {
        "Batch" => DesktopHub.Windows.ResourceRequestParser.Batch(doc.RootElement), "Query" => DesktopHub.Windows.ResourceRequestParser.Query(doc.RootElement),
        "FilterSave" => DesktopHub.Windows.ResourceRequestParser.FilterSave(doc.RootElement), "Suggestions" => DesktopHub.Windows.ResourceRequestParser.Suggestions(doc.RootElement),
        "FilterDelete" => DesktopHub.Windows.ResourceRequestParser.FilterDelete(doc.RootElement), _ => throw new ArgumentException("Unknown diagnostic parser operation") }; }
    var id = Id(); var tag = Id();
    var parsed = JsonSerializer.SerializeToElement(Parse("Batch", $$$"""{"resourceIds":["{{{id}}}"],"operation":"favorite","values":[]}"""), ResourceStore.Json);
    Equal("favorite", parsed.GetProperty("operation").GetString()); Equal(id, parsed.GetProperty("resourceIds")[0].GetString());
    foreach (var json in new[] {
        $$$"""{"resourceIds":["{{{id}}}"],"operation":"favorite","values":["{{{tag}}}"]}""",
        $$$"""{"resourceIds":["{{{id}}}","{{{id}}}"],"operation":"favorite","values":[]}""",
        $$$"""{"resourceIds":["{{{id}}}"],"operation":"delete","values":[]}""",
        $$$"""{"resourceIds":["{{{id}}}"],"operation":"addTags","values":[]}""",
        $$$"""{"resourceIds":["{{{id}}}"],"operation":"acceptSuggestions","values":[]}""",
        $$$"""{"resourceIds":["{{{id}}}"],"operation":"favorite","operation":"unfavorite","values":[]}""",
        "{\"resourceIds\":[\"not-guid\"],\"operation\":\"favorite\",\"values\":[]}",
        $$$"""{"resourceIds":["{{{id}}}"],"operation":"favorite","values":[],"path":"C:/outside"}""",
        $$$"""{"resourceIds":["\n{{{id}}}"],"operation":"favorite","values":[]}""",
        JsonSerializer.Serialize(new { resourceIds = Enumerable.Range(0,501).Select(_ => Id()), operation = "favorite", values = Array.Empty<string>() }), "[]" })
        await ThrowsAsync(() => Task.Run(() => Parse("Batch", json)));
    foreach (var json in new[] { "{\"sort\":\"random\"}", "{\"page\":\"1\"}", "{\"favoritesOnly\":1}", "{\"query\":\"a\\u0000b\"}", $$$"""{"filterId":"{{{id}}}","query":""}""", $$$"""{"selectedIds":["{{{id}}}","{{{id}}}"]}""", "{\"unknown\":true}" })
        await ThrowsAsync(() => Task.Run(() => Parse("Query", json)));
    foreach (var json in new[] { $$$"""{"filter":{"id":"{{{id}}}","name":"Valid","name":"Other","tagIds":[]}}""", $$$"""{"filter":{"id":"{{{id}}}","name":"Valid","tagIds":[],"path":"C:/outside"}}""", $$$"""{"filter":{"id":"{{{id}}}","name":"Valid","tagIds":[],"favoritesOnly":null}}""" })
        await ThrowsAsync(() => Task.Run(() => Parse("FilterSave", json)));
    var filter = JsonSerializer.SerializeToElement(Parse("FilterSave", $$$"""{"filter":{"id":"{{{id}}}","name":"  Text only  ","query":"a","tagIds":[],"extension":null,"favoritesOnly":false,"sort":"name"}}"""), ResourceStore.Json);
    Equal("Text only", filter.GetProperty("name").GetString()); Equal(0, filter.GetProperty("tagIds").GetArrayLength());
    await ThrowsAsync(() => Task.Run(() => Parse("Suggestions", $$$"""{"resourceIds":["{{{id}}}"],"suggestionName":"Valid","suggestionType":"arbitrary"}""")));
    await ThrowsAsync(() => Task.Run(() => Parse("FilterDelete", "{\"id\":\"00000000-0000-0000-0000-000000000000\"}")));
});
await Test("Selection metadata resolves off-page IDs, cleanup eligibility and bounded comparable pending suggestions", async () =>
{
    using var service = Service(); var folder = Fixture(0); var a = Id(); var b = Id(); var missing = Id(); var offline = Id();
    service.Store.Update(data =>
    {
        data.Resources.AddRange([new() { Id = a, CanonicalPath = Path.Combine(folder, "a.txt"), ManualTarget = true }, new() { Id = b, CanonicalPath = Path.Combine(folder, "b.txt"), ManualTarget = true }, new() { Id = missing, CanonicalPath = Path.Combine(folder, "m.txt"), ManualTarget = true, Status = "Missing" }, new() { Id = offline, CanonicalPath = Path.Combine(folder, "o.txt"), ManualTarget = true, Status = "SourceUnavailable" }]);
        data.Suggestions.AddRange([new() { ResourceId = a, SuggestedName = " Shared ", TagType = "theme" }, new() { ResourceId = b, SuggestedName = "shared", TagType = "theme" }, new() { ResourceId = a, SuggestedName = "Wrong type", TagType = "theme" }, new() { ResourceId = b, SuggestedName = "Wrong type", TagType = "period" }, new() { ResourceId = a, SuggestedName = "Handled", Status = "Rejected" }, new() { ResourceId = b, SuggestedName = "Handled", Status = "Accepted" }]);
    });
    ResourceSelectionMetadata Read(string[] selected, string[] cleanup) => service.SelectionMetadata(selected, cleanup);
    var before = File.ReadAllText(service.Store.FilePath);
    var json = JsonSerializer.SerializeToElement(Read([a, b, Id()], [missing, offline]), ResourceStore.Json);
    Equal(2, json.GetProperty("selectedResources").GetArrayLength()); Equal(2, json.GetProperty("cleanupResources").GetArrayLength());
    var groups = json.GetProperty("commonSuggestions"); Equal(1, groups.GetArrayLength()); Equal("theme", groups[0].GetProperty("type").GetString()); True(groups[0].GetProperty("name").GetString()!.Equals("shared", StringComparison.OrdinalIgnoreCase));
    await ThrowsAsync(() => Task.Run(() => Read([a, a], []))); await ThrowsAsync(() => Task.Run(() => Read(Enumerable.Range(0, 501).Select(_ => Id()).ToArray(), [])));
    Equal(before, File.ReadAllText(service.Store.FilePath));
    service.Store.Update(data => { data.Resources.RemoveAll(r => r.Id == a); data.Suggestions.RemoveAll(s => s.ResourceId == a); data.Resources.Single(r => r.Id == b).Status = "Missing"; });
    json = JsonSerializer.SerializeToElement(Read([a, b], []), ResourceStore.Json); Equal(1, json.GetProperty("selectedResources").GetArrayLength()); Equal("Missing", json.GetProperty("selectedResources")[0].GetProperty("status").GetString()); Equal(0, json.GetProperty("commonSuggestions").GetArrayLength());
});
await Test("Batch changes at most 500 active resources and rejects the whole invalid request without partial writes", async () =>
{
    using var service = Service(); var folder = Fixture(0);
    service.Store.Update(data => data.Resources = Enumerable.Range(0, 501).Select(i => new Resource { Id = Id(), CanonicalPath = Path.Combine(folder, i + ".txt"), DisplayName = i + ".txt", ManualTarget = true }).ToList());
    var ids = service.Store.Snapshot().Resources.Select(r => r.Id).ToArray();
    await service.BatchAsync(ids.Take(500).ToArray(), "favorite", Array.Empty<string>());
    Equal(500, service.Store.Snapshot().Resources.Count(r => r.Favorite)); var before = File.ReadAllText(service.Store.FilePath);
    await ThrowsAsync(() => service.BatchAsync(ids, "unfavorite", Array.Empty<string>()));
    await ThrowsAsync(() => service.BatchAsync(new[] { ids[0], Id() }, "unfavorite", Array.Empty<string>()));
    await ThrowsAsync(() => service.BatchAsync(new[] { ids[0], ids[0] }, "unfavorite", Array.Empty<string>()));
    await ThrowsAsync(() => service.BatchAsync(Array.Empty<string>(), "favorite", Array.Empty<string>()));
    await ThrowsAsync(() => service.BatchAsync(new[] { ids[0] }, "delete", Array.Empty<string>()));
    await ThrowsAsync(() => service.BatchAsync(new[] { ids[0] }, "favorite", new[] { Id() }));
    Equal(before, File.ReadAllText(service.Store.FilePath)); Equal(500, service.Store.Snapshot().Resources.Count(r => r.Favorite));
    service.Store.Update(data => data.Resources[1].Status = "Missing"); before = File.ReadAllText(service.Store.FilePath);
    await ThrowsAsync(() => service.BatchAsync(ids.Take(2).ToArray(), "unfavorite", Array.Empty<string>())); Equal(before, File.ReadAllText(service.Store.FilePath));
});
await Test("Batch tags are incremental and a failed disk replacement leaves all relationships unchanged", async () =>
{
    using var service = Service(); var folder = Fixture(0); var id = Id(); var first = Id(); var second = Id();
    service.Store.Update(data => { data.Resources.Add(new Resource { Id = id, CanonicalPath = Path.Combine(folder, "item.txt"), ManualTarget = true }); data.Tags.AddRange([new() { Id = first, Name = "Existing", CategoryId = "custom" }, new() { Id = second, Name = "Added", CategoryId = "custom" }]); data.ResourceTags.Add(new(id, first)); });
    await service.BatchAsync(new[] { id }, "addTags", new[] { second });
    Equal(2, service.Store.Snapshot().ResourceTags.Count); await service.BatchAsync(new[] { id }, "removeTags", new[] { second }); Equal(new List<ResourceTag> { new(id, first) }, service.Store.Snapshot().ResourceTags);
    var before = File.ReadAllText(service.Store.FilePath);
    await ThrowsAsync(() => service.BatchAsync(new[] { id }, "addTags", new[] { second, Id() })); Equal(before, File.ReadAllText(service.Store.FilePath));
    using (var locked = new FileStream(service.Store.FilePath, FileMode.Open, FileAccess.Read, FileShare.Read)) await ThrowsAsync(() => service.BatchAsync(new[] { id }, "addTags", new[] { second }));
    Equal(before, File.ReadAllText(service.Store.FilePath)); Equal(1, service.Store.Snapshot().ResourceTags.Count);
});
await Test("Batch suggestion acceptance requires same name and type for every selected resource", async () =>
{
    using var service = Service(); var folder = Fixture(0); var first = Id(); var second = Id(); var s1 = Id(); var s2 = Id(); var wrong = Id();
    service.Store.Update(data => { data.Resources.AddRange([new() { Id = first, CanonicalPath = Path.Combine(folder, "a.txt"), ManualTarget = true }, new() { Id = second, CanonicalPath = Path.Combine(folder, "b.txt"), ManualTarget = true }]);
        data.Suggestions.AddRange([new() { Id = s1, ResourceId = first, SuggestedName = "Shared", TagType = "theme" }, new() { Id = s2, ResourceId = second, SuggestedName = "Shared", TagType = "theme" }, new() { Id = wrong, ResourceId = second, SuggestedName = "Shared", TagType = "period" }]); });
    var before = File.ReadAllText(service.Store.FilePath);
    await ThrowsAsync(() => service.BatchAsync(new[] { first, second }, "acceptSuggestions", new[] { s1, wrong })); Equal(before, File.ReadAllText(service.Store.FilePath));
    await service.BatchAsync(new[] { first, second }, "acceptSuggestions", new[] { s1, s2 });
    var after = service.Store.Snapshot(); Equal(1, after.Tags.Count); Equal(2, after.ResourceTags.Count); Equal(2, after.Suggestions.Count(s => s.Status == "Accepted")); Equal("Pending", after.Suggestions.Single(s => s.Id == wrong).Status);
});
await Test("Resource schema one migrates without losing relationships and saved filters survive restart with explicit missing-tag status", async () =>
{
    var directory = NewDirectory("filter-migration"); var folder = Fixture(0); var tagId = Id(); var resourceId = Id();
    var legacy = new ResourceLibrary { SchemaVersion = 1, AutoTagByModificationTime = false, Resources = [new() { Id = resourceId, CanonicalPath = Path.Combine(folder, "a.txt"), DisplayName = "a.txt", Extension = ".txt", Favorite = true, ManualTarget = true }], Tags = [new() { Id = tagId, Name = "Old", CategoryId = "custom" }], ResourceTags = [new(resourceId, tagId)] };
    File.WriteAllText(Path.Combine(directory, "resources.json"), JsonSerializer.Serialize(legacy, ResourceStore.Json)); var filterId = Id();
    using (var service = new ResourceLibraryService(directory))
    {
        Equal(2, service.Store.Snapshot().SchemaVersion); Equal(legacy.ResourceTags, service.Store.Snapshot().ResourceTags); Equal(false, service.Store.Snapshot().AutoTagByModificationTime);
        var filter = JsonSerializer.Deserialize<SavedResourceFilter>(JsonSerializer.Serialize(new { id = filterId, name = "My filter", query = "a", tagIds = new[] { tagId }, extension = ".txt", favoritesOnly = true, sort = "name" }), ResourceStore.Json)!;
        await service.SaveFilterAsync(filter);
        Equal(1, ((ResourcePage)service.QueryFilter(filterId, 0)!).Total);
        var isolated = service.Store.Snapshot(); isolated.SavedFilters[0].TagIds.Clear();
        Equal(1, ((ResourcePage)service.QueryFilter(filterId, 0)!).Total);
        var emptyFilter = JsonSerializer.Deserialize<SavedResourceFilter>(JsonSerializer.Serialize(new { id = Id(), name = "Text only", query = "a", tagIds = Array.Empty<string>(), sort = "name" }), ResourceStore.Json)!;
        await service.SaveFilterAsync(emptyFilter);
        var emptyId = emptyFilter.Id;
        True(JsonSerializer.SerializeToElement(service.ResolveFilter(emptyId), ResourceStore.Json).GetProperty("valid").GetBoolean());
        var original = File.ReadAllText(service.Store.FilePath);
        var badFilter = JsonSerializer.Deserialize<SavedResourceFilter>(JsonSerializer.Serialize(new { id = Id(), name = "Invalid", tagIds = new[] { Id() }, sort = "name" }), ResourceStore.Json)!;
        await ThrowsAsync(() => service.SaveFilterAsync(badFilter)); Equal(original, File.ReadAllText(service.Store.FilePath));
        await service.DeleteFilterAsync(emptyId); await service.DeleteTagAsync(tagId, true);
    }
    using var restart = new ResourceLibraryService(directory); var data = restart.Store.Snapshot();
    using var json = JsonDocument.Parse(JsonSerializer.Serialize(data, ResourceStore.Json)); var saved = json.RootElement.GetProperty("savedFilters")[0]; Equal(tagId, saved.GetProperty("tagIds")[0].GetString());
    var status = restart.ResolveFilter(filterId)!; var statusJson = JsonSerializer.SerializeToElement(status, ResourceStore.Json); True(!statusJson.GetProperty("valid").GetBoolean()); Equal(tagId, statusJson.GetProperty("missingTagIds")[0].GetString());
    await ThrowsAsync(() => Task.Run(() => restart.QueryFilter(filterId, 0)));
    await restart.DeleteFilterAsync(filterId); using var deleted = JsonDocument.Parse(JsonSerializer.Serialize(new ResourceStore(directory).Snapshot(), ResourceStore.Json)); Equal(0, deleted.RootElement.GetProperty("savedFilters").GetArrayLength());
});
await Test("Known-folder desktop discovery creates no index or watcher", async () =>
{
    using var service = Service(); await service.StartAsync();
    var desktop = ResourceScanner.DesktopPath(); True(Path.IsPathFullyQualified(desktop)); Equal(ResourceScanner.Canonical(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)), desktop);
    Equal(0, service.WatcherCount); Equal(0, service.Query(new()).Total); True(!File.Exists(service.Store.FilePath));
});
await Test("Preview is read-only, cancellation does not authorize, unconfirmed custom/public sources remain unwatched", async () =>
{
    using var service = Service(); var folder = Fixture(3);
    var preview = await service.PreviewAsync(folder, "custom", false, Id()); Equal(3, preview.Files); Equal(0, service.WatcherCount); True(!File.Exists(service.Store.FilePath));
    service.Cancel(preview.Token); await ThrowsAsync(() => service.ConfirmAsync(preview.Token)); Equal(0, service.Query(new()).Total);
    var publicFolder = Fixture(1); await service.PreviewAsync(publicFolder, "publicDesktop", false, Id()); Equal(0, service.WatcherCount);
});
await Test("Confirmation indexes only direct children, subfolders are one resource and original bytes stay identical", async () =>
{
    using var service = Service(); var folder = Fixture(2); var sub = Path.Combine(folder, "sub"); Directory.CreateDirectory(sub); File.WriteAllText(Path.Combine(sub,"deep.txt"),"safe");
    var hash = Hash(Path.Combine(folder,"0.txt")); await Import(service,folder);
    Equal(3,service.Query(new()).Total); True(!service.Store.Snapshot().Resources.Any(r=>r.DisplayName=="deep.txt")); Equal(hash,Hash(Path.Combine(folder,"0.txt"))); Equal(1,service.WatcherCount);
});
await Test("Independent recursive authorization, nested and duplicate sources deduplicate by path", async () =>
{
    using var service = Service(); var folder=Fixture(1);var sub=Path.Combine(folder,"sub");Directory.CreateDirectory(sub);File.WriteAllText(Path.Combine(sub,"inner.txt"),"x");
    await Import(service,folder,true);await Import(service,sub);await Import(service,folder,true);
    var data=service.Store.Snapshot();Equal(2,data.Sources.Count);Equal(3,data.Resources.Count);var inner=data.Resources.Single(r=>r.DisplayName=="inner.txt");Equal(2,data.ResourceSources.Count(x=>x.ResourceId==inner.Id));
    await service.RemoveSourceAsync(data.Sources.Single(x=>x.Path==sub).Id,true);Equal(inner.Id,service.Store.Snapshot().Resources.Single(r=>r.DisplayName=="inner.txt").Id);
});
await Test("First-import consent tags every page by modification month, reuses tags and survives restart", async () =>
{
    var directory = NewDirectory("time-tags"); var folder = Fixture(67);
    for (var i = 0; i < 67; i++) File.SetLastWriteTimeUtc(Path.Combine(folder, i + ".txt"), new DateTime(2022, i % 2 + 1, 15, 12, 0, 0, DateTimeKind.Utc));
    using (var service = new ResourceLibraryService(directory))
    {
        await service.SaveTagAsync(null, "2022年1月", "period");
        var preview = await service.PreviewAsync(folder, "custom", false, Id());
        Equal(null, service.Query(new()).AutoTagByModificationTime); Equal(0, service.Store.Snapshot().ResourceTags.Count);
        await service.ConfirmAsync(preview.Token, true);
        var data = service.Store.Snapshot(); Equal(true, data.AutoTagByModificationTime); Equal(67, data.ResourceTags.Count); Equal(2, data.Tags.Count);
        foreach (var item in service.Query(new(Page: 1)).Items) Equal(item.Resource.ModificationTime.ToLocalTime().ToString("yyyy年M月"), item.Tags.Single().Name);
        True(data.Suggestions.Where(s => s.TagType == "period").All(s => s.Status == "Accepted"));
        var again = await service.PreviewAsync(folder, "custom", false, Id()); await service.ConfirmAsync(again.Token);
        Equal(67, service.Store.Snapshot().ResourceTags.Count);
    }
    using var restart = new ResourceLibraryService(directory); await restart.StartAsync(); Equal(true, restart.Query(new()).AutoTagByModificationTime);
    var added = Path.Combine(folder, "later.txt"); File.WriteAllText(added, "safe"); File.SetLastWriteTimeUtc(added, new DateTime(2021, 6, 15, 12, 0, 0, DateTimeKind.Utc));
    await restart.RefreshAsync();
    var later = restart.Query(new("later.txt")).Items.Single(); Equal("2021年6月", later.Tags.Single().Name);
});
await Test("Declining time tags persists without binding tags; cancelled previews never record consent", async () =>
{
    var directory = NewDirectory("decline-tags"); var folder = Fixture(2);
    using (var service = new ResourceLibraryService(directory))
    {
        var cancelled = await service.PreviewAsync(folder, "custom", false, Id()); service.Cancel(cancelled.Token);
        await ThrowsAsync(() => service.ConfirmAsync(cancelled.Token, true)); Equal(null, service.Store.Snapshot().AutoTagByModificationTime);
        var preview = await service.PreviewAsync(folder, "custom", false, Id()); await service.ConfirmAsync(preview.Token, false);
        Equal(false, service.Query(new()).AutoTagByModificationTime); Equal(0, service.Store.Snapshot().ResourceTags.Count);
    }
    using var restart = new ResourceLibraryService(directory); await restart.StartAsync(); await Import(restart, Fixture(1));
    Equal(false, restart.Query(new()).AutoTagByModificationTime); Equal(0, restart.Store.Snapshot().ResourceTags.Count);
});
await Test("Enabling time tags for an existing library preserves manual tags and favorites", async () =>
{
    using var service = Service(); var folder = Fixture(1); await Import(service, folder);
    var item = service.Store.Snapshot().Resources.Single(); await service.FavoriteAsync(item.Id, true);
    await service.SaveTagAsync(null, "手动标签", "custom", "custom"); var tag = service.Store.Snapshot().Tags.Single(); await service.BindTagAsync(item.Id, tag.Id, true);
    var preview = await service.PreviewAsync(Fixture(1), "custom", false, Id()); await service.ConfirmAsync(preview.Token, true);
    var data = service.Store.Snapshot(); Equal(3, data.ResourceTags.Count); True(data.Resources.Single(r => r.Id == item.Id).Favorite); True(data.ResourceTags.Contains(new(item.Id, tag.Id)));
});
await Test("Temporary folder browse has pagination and does not persist; explicit single item is indexed and watched", async () =>
{
    using var service=Service();var folder=Fixture(1);var sub=Path.Combine(folder,"sub");Directory.CreateDirectory(sub);File.WriteAllText(Path.Combine(sub,"inside.txt"),"x");await Import(service,folder);
    var id=service.Store.Snapshot().Resources.Single(r=>r.ResourceType=="folder").Id;var before=File.ReadAllText(service.Store.FilePath);
    var browsing=await service.BrowseAsync(id,null,null,0);Equal(1,browsing.Total);Equal(before,File.ReadAllText(service.Store.FilePath));
    await ThrowsAsync(()=>service.AddBrowsedAsync(browsing.Token,"..\\elsewhere"));await service.AddBrowsedAsync(browsing.Token,"inside.txt");Equal(3,service.Query(new()).Total);
    File.Delete(Path.Combine(sub,"inside.txt"));await Until(()=>service.Store.Snapshot().Resources.Single(r=>r.DisplayName=="inside.txt").Status=="Missing");
});
await Test("Folder-specific recursive preview can use native browse scope and still requires confirmation", async () =>
{
    using var service=Service();var folder=Fixture(1);var sub=Path.Combine(folder,"sub");Directory.CreateDirectory(sub);File.WriteAllText(Path.Combine(sub,"inside.txt"),"x");await Import(service,folder);
    var item=service.Store.Snapshot().Resources.Single(r=>r.ResourceType=="folder");var browse=await service.BrowseAsync(item.Id,null,null,0);
    var preview=await service.PreviewBrowseAsync(browse.Token,Id());Equal(2,service.Query(new()).Total);True(preview.Recursive);await service.ConfirmAsync(preview.Token);Equal(3,service.Query(new()).Total);
});
await Test("Actual FileSystemWatcher create and rename preserve stable id, favorite and tags", async () =>
{
    using var service=Service();var folder=Fixture(1);await Import(service,folder);var path=Path.Combine(folder,"watch.txt");File.WriteAllText(path,"watch");await Until(()=>service.Store.Snapshot().Resources.Any(r=>r.CanonicalPath==path));
    var item=service.Store.Snapshot().Resources.Single(r=>r.CanonicalPath==path);await service.FavoriteAsync(item.Id,true);await service.SaveTagAsync(null,"主题 A","theme");var tag=service.Store.Snapshot().Tags.Single();await service.BindTagAsync(item.Id,tag.Id,true);
    var renamed=Path.Combine(folder,"renamed.txt");File.Move(path,renamed);await Until(()=>service.Store.Snapshot().Resources.Any(r=>r.Id==item.Id&&r.CanonicalPath==renamed));var data=service.Store.Snapshot();True(data.Resources.Single(r=>r.Id==item.Id).Favorite);True(data.ResourceTags.Contains(new(item.Id,tag.Id)));
});
await Test("Offline same-directory rename and renamed folder descendants survive startup reconciliation", async () =>
{
    var directory=NewDirectory("data");var folder=Fixture(1);var sub=Path.Combine(folder,"sub");Directory.CreateDirectory(sub);File.WriteAllText(Path.Combine(sub,"inner.txt"),"x");
    string fileId,childId; using(var first=new ResourceLibraryService(directory)){await Import(first,folder,true);fileId=first.Store.Snapshot().Resources.Single(x=>x.DisplayName=="0.txt").Id;childId=first.Store.Snapshot().Resources.Single(x=>x.DisplayName=="inner.txt").Id;await first.FavoriteAsync(childId,true);}
    File.Move(Path.Combine(folder,"0.txt"),Path.Combine(folder,"offline.txt"));Directory.Move(sub,Path.Combine(folder,"renamed-sub"));
    using var second=new ResourceLibraryService(directory);await second.StartAsync();var data=second.Store.Snapshot();Equal(fileId,data.Resources.Single(x=>x.DisplayName=="offline.txt").Id);var child=data.Resources.Single(x=>x.DisplayName=="inner.txt");Equal(childId,child.Id);True(child.Favorite);True(child.CanonicalPath.Contains("renamed-sub"));
});
await Test("Deleted original leaves Missing metadata and confirmed cleanup changes no remaining file", async () =>
{
    using var service=Service();var folder=Fixture(2);await Import(service,folder);var item=service.Store.Snapshot().Resources.Single(r=>r.DisplayName=="0.txt");await service.FavoriteAsync(item.Id,true);File.Delete(item.CanonicalPath);await Until(()=>service.Store.Snapshot().Resources.Single(r=>r.Id==item.Id).Status=="Missing");
    var other=Path.Combine(folder,"1.txt");var hash=Hash(other);await ThrowsAsync(()=>service.CleanAsync([item.Id],false));await service.CleanAsync([item.Id],true);True(!service.Store.Snapshot().Resources.Any(x=>x.Id==item.Id));Equal(hash,Hash(other));
});
await Test("Unavailable source is distinct from Missing and restores on refresh without losing favorites", async () =>
{
    using var service=Service();var folder=Fixture(1);await Import(service,folder);var item=service.Store.Snapshot().Resources.Single();await service.FavoriteAsync(item.Id,true);var away=folder+"-offline";Directory.Move(folder,away);await service.RefreshAsync();
    Equal("SourceUnavailable",service.Store.Snapshot().Resources.Single().Status);await ThrowsAsync(()=>service.CleanAsync([item.Id],true));Directory.Move(away,folder);await service.RefreshAsync();Equal("Active",service.Store.Snapshot().Resources.Single().Status);True(service.Store.Snapshot().Resources.Single().Favorite);
});
await Test("Healthy overlapping source wins when another source becomes unavailable", async () =>
{
    using var service=Service();var folder=Fixture(0);var sub=Path.Combine(folder,"sub");Directory.CreateDirectory(sub);File.WriteAllText(Path.Combine(sub,"x.txt"),"x");await Import(service,folder,true);await Import(service,sub);
    // An individually disabled/unavailable source must not erase shared valid metadata.
    service.Store.Update(data=>data.Sources.Single(x=>x.Path==sub).AvailabilityStatus="SourceUnavailable");await service.RefreshAsync();Equal("Active",service.Store.Snapshot().Resources.Single(x=>x.DisplayName=="x.txt").Status);
});
await Test("Manual repair merges existing duplicate into original id and retains both tag/favorite relations", async () =>
{
    using var service=Service();var folder=Fixture(2);await Import(service,folder);var data=service.Store.Snapshot();var a=data.Resources.Single(x=>x.DisplayName=="0.txt");var b=data.Resources.Single(x=>x.DisplayName=="1.txt");await service.FavoriteAsync(a.Id,true);await service.SaveTagAsync(null,"修复标签", "custom", "custom");var tag=service.Store.Snapshot().Tags.Single();await service.BindTagAsync(a.Id,tag.Id,true);
    File.Delete(a.CanonicalPath);await service.RefreshAsync();await service.BeginRepairAsync(a.Id);Equal("PendingRepair",service.Store.Snapshot().Resources.Single(x=>x.Id==a.Id).Status);await service.RepairAsync(a.Id,b.CanonicalPath);data=service.Store.Snapshot();Equal(1,data.Resources.Count);Equal(a.Id,data.Resources.Single().Id);True(data.Resources.Single().Favorite);True(data.ResourceTags.Contains(new(a.Id,tag.Id)));True(File.Exists(b.CanonicalPath));
});
await Test("Repair outside source authorizes only a single target, survives refresh, never scans its siblings", async () =>
{
    using var service=Service();var folder=Fixture(1);var outside=Fixture(2);await Import(service,folder);var item=service.Store.Snapshot().Resources.Single();File.Delete(item.CanonicalPath);await service.RefreshAsync();await service.RepairAsync(item.Id,Path.Combine(outside,"0.txt"));await service.RefreshAsync();Equal(1,service.Query(new()).Total);Equal("Active",service.Store.Snapshot().Resources.Single().Status);True(service.Store.Snapshot().Resources.Single().ManualTarget);
});
await Test("Theme/period/custom tags bind multiple per resource; combined filename/tag/favorite filters work", async () =>
{
    using var service=Service();await Import(service,Fixture(2));var a=service.Store.Snapshot().Resources[0];foreach(var kind in new[]{"theme","period","custom"})await service.SaveTagAsync(null,kind,kind,kind);var tags=service.Store.Snapshot().Tags;foreach(var tag in tags)await service.BindTagAsync(a.Id,tag.Id,true);await service.FavoriteAsync(a.Id,true);
    Equal(1,service.Query(new("theme",tags.Select(x=>x.Id).ToList(),true)).Total);await service.SaveTagAsync(tags[0].Id,"更名","theme");Equal(1,service.Query(new("更名")).Total);await service.DeleteTagAsync(tags[0].Id,true);Equal(2,service.Store.Snapshot().ResourceTags.Count);Equal(2,service.Query(new()).Total);
});
await Test("Suggestions have metadata explanations; accepted/edited only become tags, rejections persist", async () =>
{
    var directory=NewDirectory("data");using var service=new ResourceLibraryService(directory);var folder=Fixture(0);File.WriteAllText(Path.Combine(folder,"课程设计.txt"),"x");await Import(service,folder);var data=service.Store.Snapshot();Equal(0,data.ResourceTags.Count);True(data.Suggestions.Any(s=>s.TagType=="period"&&s.SuggestionBasis.Contains("修改时间")));
    await service.SuggestionAsync(data.Suggestions[0].Id,false);await service.SuggestionAsync(data.Suggestions[1].Id,true,"用户编辑");await service.RefreshAsync();data=service.Store.Snapshot();Equal("Rejected",data.Suggestions[0].Status);Equal(1,data.ResourceTags.Count);True(data.Tags.Any(t=>t.Name=="用户编辑"));var restart=new ResourceStore(directory);Equal("Rejected",restart.Snapshot().Suggestions[0].Status);
});
await Test("Restart restores authorized sources, stable resources, favorites, tags and source relationships", async () =>
{
    var directory=NewDirectory("data");var folder=Fixture(2);using(var first=new ResourceLibraryService(directory)){await Import(first,folder);var item=first.Store.Snapshot().Resources[0];await first.FavoriteAsync(item.Id,true);await first.SaveTagAsync(null,"保存", "custom", "custom");await first.BindTagAsync(item.Id,first.Store.Snapshot().Tags[0].Id,true);}
    var before=new ResourceStore(directory).Snapshot();using var restart=new ResourceLibraryService(directory);await restart.StartAsync();var after=restart.Store.Snapshot();Equal(before.Resources.Select(x=>x.Id).Order().ToArray(),after.Resources.Select(x=>x.Id).Order().ToArray());Equal(before.ResourceTags,after.ResourceTags);True(after.Resources.Any(r=>r.Favorite));Equal(1,restart.WatcherCount);
});
await Test("Atomic replacement failure leaves previous metadata and apps/scenes configuration bytes intact", async () =>
{
    var directory=NewDirectory("data");new SettingsStore(directory).Save(new AppSettings());using var service=new ResourceLibraryService(directory);await Import(service,Fixture(1));var settings=File.ReadAllText(Path.Combine(directory,"settings.json"));var original=File.ReadAllText(service.Store.FilePath);var item=service.Store.Snapshot().Resources[0];
    using(var locked=new FileStream(service.Store.FilePath,FileMode.Open,FileAccess.Read,FileShare.Read)){await ThrowsAsync(()=>service.FavoriteAsync(item.Id,true));}Equal(original,File.ReadAllText(service.Store.FilePath));True(!service.Store.Snapshot().Resources[0].Favorite);Equal(settings,File.ReadAllText(Path.Combine(directory,"settings.json")));Equal(0,Directory.GetFiles(directory,"resources.*.tmp").Length);
});
await Test("Corrupted/unknown-version resource metadata is preserved and cannot overwrite app/scene data", async () =>
{
    foreach(var damaged in new[]{"{\"schemaVersion\":999}","{\"resources\":[null]}","{\"sources\":[{\"path\":\"bad relative path\"}]}"})
    { var directory=NewDirectory("data");var file=Path.Combine(directory,"resources.json");File.WriteAllText(file,damaged);var store=new ResourceStore(directory);True(store.RecoveryWarning is not null);await ThrowsAsync(()=>Task.Run(()=>store.Update(_=>{})));Equal(damaged,File.ReadAllText(file)); }
});
await Test("Large scan reports progress, paginates 60 items, can cancel and stays asynchronous", async () =>
{
    using var service=Service();var folder=Fixture(1800);var id=Id();var progress=0;var responsiveness=0;
    service.Progress+=p=>{if(p.OperationId==id)progress++;};var watch=Stopwatch.StartNew();var task=service.PreviewAsync(folder,"custom",false,id);while(!task.IsCompleted){responsiveness++;await Task.Delay(5);}var preview=await task;True(progress>0);True(responsiveness>0);await service.ConfirmAsync(preview.Token);Equal(1800,service.Query(new()).Total);Equal(60,service.Query(new()).Items.Count);Equal(60,service.Query(new(Page:1)).Items.Count);Console.WriteLine($"PERF 1800 items: preview/confirm {watch.ElapsedMilliseconds}ms, UI-like ticks={responsiveness}");
    var cancelId=Id();service.Progress+=p=>{if(p.OperationId==cancelId)service.Cancel(cancelId);};await ThrowsAsync(()=>service.PreviewAsync(folder,"custom",true,cancelId));Equal(1800,service.Query(new()).Total);
});
await Test("Short burst of real events and explicit recovery converge without duplicate resources", async () =>
{
    using var service=Service();var folder=Fixture(0);await Import(service,folder);for(var i=0;i<350;i++)File.WriteAllText(Path.Combine(folder,i+".txt"),"x");await Until(()=>service.Query(new()).Total==350,20000);
    for(var i=0;i<120;i++)File.Delete(Path.Combine(folder,i+".txt"));await service.RefreshAsync();var data=service.Store.Snapshot();Equal(350,data.Resources.Count);Equal(120,data.Resources.Count(x=>x.Status=="Missing"));Equal(350,data.Resources.Select(x=>x.CanonicalPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
});
await Test("Recursive scanning rejects junction escape/cycles and never indexes external contents", async () =>
{
    using var service=Service();var folder=Fixture(0);var external=Fixture(1);var link=Path.Combine(folder,"junction");
    var start=new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true};start.ArgumentList.Add("/c");start.ArgumentList.Add("mklink");start.ArgumentList.Add("/J");start.ArgumentList.Add(link);start.ArgumentList.Add(external);using var process=Process.Start(start)!;await process.WaitForExitAsync();if(process.ExitCode!=0)throw new Exception("Could not create owned junction fixture");
    var preview=await service.PreviewAsync(folder,"custom",true,Id());True(preview.Warnings.Count>0);await service.ConfirmAsync(preview.Token);Equal(1,service.Query(new()).Total);True(service.Store.Snapshot().Resources.Single().ReparsePoint);await ThrowsAsync(()=>service.BrowseAsync(service.Store.Snapshot().Resources.Single().Id,null,null,0));
});
await Test("Path traversal, arbitrary browse tokens, relative/UNC/device/ADS paths are rejected", async () =>
{
    using var service=Service();await ThrowsAsync(()=>service.BrowseAsync(null,"untrusted","..",0));foreach(var path in new[]{"../tmp",@"\\server\share",@"C:\x.txt:stream",@"\\?\C:\x"})await ThrowsAsync(()=>service.PreviewAsync(path,"custom",true,Id()));
});
await Test("Scanning locked files reads metadata only, and cloud-placeholder targets never open", async () =>
{
    using var service=Service();var folder=Fixture(1);var path=Path.Combine(folder,"0.txt");var hash=Hash(path);using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.None)){await Import(service,folder);Equal(1,service.Query(new()).Total);}Equal(hash,Hash(path));
    File.SetAttributes(path,File.GetAttributes(path)|FileAttributes.Offline);await service.RefreshAsync();var item=service.Store.Snapshot().Resources.Single();True(item.CloudPlaceholder);await ThrowsAsync(()=>Task.Run(()=>service.OpenTarget(item.Id)));File.SetAttributes(path,FileAttributes.Normal);
});
await Test("Resource store snapshots are isolated and metadata-only operations need no network", async () =>
{
    using var service=Service();await Import(service,Fixture(1));var copy=service.Store.Snapshot();copy.Resources[0].Favorite=true;copy.Resources.Clear();Equal(1,service.Query(new()).Total);True(!service.Store.Snapshot().Resources[0].Favorite);await service.SaveTagAsync(null,"离线", "custom", "custom");Equal(1,service.Store.Snapshot().Tags.Count);
});
await Test("Extension grouping/filtering applies before pagination and custom categories persist", async () =>
{
    var directory = NewDirectory("categories"); var folder = Fixture(63);
    File.WriteAllText(Path.Combine(folder, "a.PDF"), "owned"); File.WriteAllText(Path.Combine(folder, "z.pdf"), "owned");
    File.WriteAllText(Path.Combine(folder, "no-extension"), "owned"); Directory.CreateDirectory(Path.Combine(folder, "sub"));
    string categoryId, tagId;
    using (var service = new ResourceLibraryService(directory))
    {
        await Import(service, folder);
        var page = service.Query(new()); Equal("folder", page.Items[0].Resource.ResourceType);
        var pdfs = service.Query(new(Extension: ".PDF")); Equal(2, pdfs.Total); True(pdfs.Items.All(x => x.Resource.Extension.Equals(".pdf", StringComparison.OrdinalIgnoreCase)));
        Equal(63, service.Query(new(Extension: ".txt")).Total); Equal(3, service.Query(new(Extension: ".txt", Page: 1)).Items.Count);
        Equal(1, service.Query(new(Extension: "__none")).Total); Equal(1, service.Query(new(Extension: "__folders")).Total);
        True(pdfs.Extensions!.Contains(".txt")); True(page.Items.Select(x => x.Resource.Extension.ToLowerInvariant()).Skip(1).SequenceEqual(page.Items.Skip(1).Select(x => x.Resource.Extension.ToLowerInvariant()).Order(StringComparer.OrdinalIgnoreCase)));
        await ThrowsAsync(() => service.SaveTagAsync(null, "missing category", "custom"));
        await service.SaveCategoryAsync("项目"); categoryId = service.Store.Snapshot().TagCategories.Single(c => c.Name == "项目").Id;
        await ThrowsAsync(() => service.SaveCategoryAsync("项目")); await ThrowsAsync(() => service.SaveCategoryAsync(" "));
        await service.SaveTagAsync(null, "项目 A", "custom", categoryId); tagId = service.Store.Snapshot().Tags.Single().Id;
        await service.BindTagAsync(pdfs.Items[0].Resource.Id, tagId, true); Equal(1, service.Query(new(TagIds: [tagId], Extension: ".pdf")).Total);
        var copy = service.Store.Snapshot(); copy.TagCategories.Clear(); Equal(4, service.Store.Snapshot().TagCategories.Count);
    }
    using var restarted = new ResourceLibraryService(directory);
    Equal(categoryId, restarted.Store.Snapshot().Tags.Single(t => t.Id == tagId).CategoryId);
    True(restarted.Query(new()).TagCategories!.Any(c => c.Id == categoryId));
});
Console.WriteLine($"RESOURCE RESULT {passed} passed; {failed} failed. Fixtures retained at {root}");
File.WriteAllText(Path.Combine(root,"results.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
Environment.ExitCode=failed==0?0:1;

string Id()=>Guid.NewGuid().ToString("N");
string NewDirectory(string name){var path=Path.Combine(root,name+"-"+Id());Directory.CreateDirectory(path);return path;}
ResourceLibraryService Service()=>new(NewDirectory("data"));
string Fixture(int count){var path=NewDirectory("source");for(var i=0;i<count;i++)File.WriteAllText(Path.Combine(path,i+".txt"),"Desktop Hub owned harmless file "+i);return path;}
async Task Import(ResourceLibraryService service,string path,bool recursive=false){var preview=await service.PreviewAsync(path,"custom",recursive,Id());await service.ConfirmAsync(preview.Token);}
string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
void True(bool value){if(!value)throw new Exception("Assertion failed");}
void Equal<T>(T expected,T actual){if(JsonSerializer.Serialize(expected)!=JsonSerializer.Serialize(actual))throw new Exception($"Expected {JsonSerializer.Serialize(expected)}, got {JsonSerializer.Serialize(actual)}");}
async Task ThrowsAsync(Func<Task> action){try{await action();}catch{return;}throw new Exception("Expected failure");}
async Task Until(Func<bool> predicate,int timeout=10000){var watch=Stopwatch.StartNew();while(watch.ElapsedMilliseconds<timeout){if(predicate())return;await Task.Delay(80);}throw new TimeoutException("Watcher convergence");}
async Task Test(string name,Func<Task> test){try{await test();passed++;results.Add(new{name,passed=true});Console.WriteLine("PASS "+name);}catch(Exception ex){failed++;results.Add(new{name,passed=false,error=ex.ToString()});Console.WriteLine("FAIL "+name+"\n"+ex);}}
