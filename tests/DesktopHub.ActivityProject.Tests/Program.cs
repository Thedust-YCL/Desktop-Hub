using System.Text;
using System.Text.Json;
using DesktopHub.Core;

var root=Path.Combine(AppContext.BaseDirectory,"DesktopHub-activity-project-tests-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed=0;var failed=0;
try {
Test("Activity records only truthful type-bound success, persists no copied entity metadata",()=>{
    var d=Dir();var clock=new FixedClock();var s=new ActivityService(d,clock);var changes=0;s.Changed+=()=>changes++;
    foreach(var pair in new[]{("app","started"),("scene","execution_established"),("file","submitted"),("folder","submitted")}) True(s.Record(pair.Item1,Id(),pair.Item2));
    foreach(var result in new[]{"failed","busy","submitted","execution_established","","success"}) True(!s.Record("app",Id(),result));
    Equal(4,s.Snapshot().Count);Equal(4,changes);Equal(clock.GetUtcNow(),s.Snapshot()[0].At);
    var json=File.ReadAllText(Path.Combine(d,"activity.json"));True(!json.Contains("name")&&!json.Contains("path")&&!json.Contains("target"));
    Equal(4,new ActivityService(d).Snapshot().Count);
    Throws(()=>s.Record("web",Id(),"started"));Throws(()=>s.Record("app","bad","started"));Throws(()=>s.Record("app",Guid.Empty.ToString(),"started"));Throws(()=>s.Record("app",Id()+"\n","started"));
});
Test("Activity exact type-ID dedup, newest order, equal time determinism, cap50, snapshot isolation and clear",()=>{
    var d=Dir();var s=new ActivityService(d,new FixedClock());var first=Id();s.Record("app",first,"started");s.Record("scene",first,"execution_established");s.Record("app",first,"started");
    Equal("app",s.Snapshot()[0].Type);Equal("scene",s.Snapshot()[1].Type);Equal(2,s.Snapshot().Count);
    for(var i=0;i<51;i++)s.Record("file",GuidFrom(i+1),"submitted");Equal(50,s.Snapshot().Count);Equal(GuidFrom(51),s.Snapshot()[0].Id);Equal(GuidFrom(2),s.Snapshot()[49].Id);
    if(s.Snapshot() is IList<ActivityRecord> list)list.Clear();Equal(50,s.Snapshot().Count);
    var legacy="  "+Guid.NewGuid().ToString("B")+"  ";s.Record("file",legacy,"submitted");Equal(legacy,s.Snapshot()[0].Id);
    var changes=0;s.Changed+=()=>changes++;s.Clear();Equal(1,changes);Equal(0,s.Snapshot().Count);Equal(0,new ActivityService(d).Snapshot().Count);
});
Test("Project save isolates caller/return/snapshot, normalizes order, persists pins and shared references",()=>{
    var d=Dir();var entity=Id();var s=new ProjectService(d,(t,id)=>t=="app"&&id==entity);var count=0;s.Changed+=()=>count++;
    var a=Project(" A ");a.Description="line1\r\nline2\t";a.Items=[new(){Type="app",EntityId=entity}];var saved=s.Save(a);
    Equal("A",saved.Name);a.Items[0].EntityId=Id();a.Name="mutated";saved.Items.Clear();Equal(entity,s.Snapshot()[0].Items[0].EntityId);
    var b=s.Save(Project("B") with{Items=[new(){Type="app",EntityId=entity}],DisplayOrder=0});Equal(b.Id,s.Snapshot()[0].Id);Equal(0,s.Snapshot()[0].DisplayOrder);Equal(1,s.Snapshot()[1].DisplayOrder);
    var snapshot=s.Snapshot();snapshot[0].Items.Clear();snapshot[0].Name="mutated";Equal(1,s.Snapshot()[0].Items.Count);
    var move=s.Snapshot().Single(p=>p.Id==a.Id);move.DisplayOrder=1;move.PinnedToHome=true;s.Save(move);Equal(b.Id,s.Snapshot()[0].Id);True(s.Snapshot()[1].PinnedToHome);
    var c=s.Save(Project("C") with{DisplayOrder=199});Equal(c.Id,s.Snapshot()[2].Id);c.DisplayOrder=0;s.Save(c);Equal(c.Id,s.Snapshot()[0].Id);
    var restart=new ProjectService(d,(_,_)=>throw new Exception("load must not resolve entities"));Equal(3,restart.Snapshot().Count);Equal(1,restart.Snapshot()[2].Items.Count);
    var sentinel=Path.Combine(d,"entity.txt");File.WriteAllText(sentinel,"untouched");s.Remove(a.Id);Equal(2,s.Snapshot().Count);Equal("untouched",File.ReadAllText(sentinel));Equal(entity,s.Snapshot().Single(p=>p.Id==b.Id).Items[0].EntityId);Throws(()=>s.Remove(a.Id));Equal(6,count);
});
Test("Missing references round-trip only when the persisted item identity and target stay unchanged",()=>{
    var d=Dir();var existing=Id();var available=true;var s=new ProjectService(d,(t,id)=>available&&t=="file"&&id==existing);
    var a=s.Save(Project("A")with{Items=[new(){Type="file",EntityId=existing}]});available=false;a.Name="Renamed";a.PinnedToHome=true;s.Save(a);
    var restart=new ProjectService(d,(_,_)=>false);Equal(existing,restart.Snapshot()[0].Items[0].EntityId);
    var forged=Project("Forged")with{Items=[a.Items[0] with{}]};Throws(()=>s.Save(forged));
    var changed=s.Snapshot()[0];changed.Items[0].EntityId=Id();Throws(()=>s.Save(changed));
    changed=s.Snapshot()[0];changed.Items[0].Id=Id();Throws(()=>s.Save(changed));
    changed=s.Snapshot()[0];changed.Items[0].Type="folder";Throws(()=>s.Save(changed));
    changed=s.Snapshot()[0];changed.Items=[];s.Save(changed);Equal(0,s.Snapshot()[0].Items.Count);
});
Test("Project item order is canonical and a missing unchanged reference may be reordered",()=>{
    var d=Dir();var available=true;var s=new ProjectService(d,(_,_)=>available);var refs=Enumerable.Range(0,4).Select(i=>new ProjectItem{Type=i%2==0?"app":"scene",EntityId=Id()}).ToList();
    var a=s.Save(Project("A")with{Items=refs});available=false;a.Items=[a.Items[3],a.Items[0],a.Items[1],a.Items[2]];s.Save(a);Equal(refs[3].Id,s.Snapshot()[0].Items[0].Id);Equal(refs[2].Id,s.Snapshot()[0].Items[3].Id);
});
Test("Projects enforce record/item bounds without altering the previous successful transaction",()=>{
    var d=Dir();var s=new ProjectService(d,(_,_)=>true);for(var i=0;i<200;i++)s.Save(Project("P"+i)with{DisplayOrder=i});Equal(200,s.Snapshot().Count);var before=File.ReadAllBytes(Path.Combine(d,"projects.json"));Throws(()=>s.Save(Project("overflow")));Bytes(before,File.ReadAllBytes(Path.Combine(d,"projects.json")));
    var a=s.Snapshot()[0];a.Items=Enumerable.Range(0,100).Select(_=>new ProjectItem{Type="file",EntityId=Id()}).ToList();s.Save(a);Equal(100,s.Snapshot().Single(p=>p.Id==a.Id).Items.Count);a.Items.Add(new(){Type="file",EntityId=Id()});Throws(()=>s.Save(a));Equal(100,s.Snapshot().Single(p=>p.Id==a.Id).Items.Count);
});
Test("Project validation rejects malformed IDs/text/type/ref combinations and duplicates",()=>{
    var d=Dir();var s=new ProjectService(d,(_,_)=>true);var baseline=s.Save(Project("Allowed")with{Name=new string('名',80),Description=new string('描',500)});
    foreach(var project in new[]{Project(""),Project(new string('a',81)),Project("a\nb"),Project("ok")with{Id=Guid.Empty.ToString()},Project("ok")with{Id=Guid.NewGuid().ToString("N")},Project("ok")with{DisplayOrder=-1},Project("ok")with{DisplayOrder=200},Project("ok")with{Description=new string('a',501)},Project("ok")with{Description="a\u0000b"},Project("ok")with{Items=null!},Project("ok")with{Name=null!},Project("ok")with{Description=null!}})Throws(()=>s.Save(project));
    var entity=Id();foreach(var item in new ProjectItem[]{new(){Type="web",EntityId=entity,Url="https://example.com"},new(){Type="app",EntityId=entity,Url="https://example.com"},new(){Type="file",EntityId=entity,Name="old filename"},new(){Type="app",EntityId=null},new(){Type="shell",EntityId=entity},new(){Type="app",EntityId=entity,Id=Guid.Empty.ToString()},new(){Type="app",EntityId=entity+"\n"},null!})Throws(()=>s.Save(Project("bad")with{Items=[item]}));
    var itemId=Id();Throws(()=>s.Save(Project("dup")with{Items=[new(){Id=itemId,EntityId=entity},new(){Id=itemId,EntityId=Id()}]}));Throws(()=>s.Save(Project("dup")with{Items=[new(){EntityId=entity},new(){EntityId=entity}]}));
    Equal(1,s.Snapshot().Count);Equal(baseline.Id,s.Snapshot()[0].Id);
});
Test("Shared URL validation canonicalizes HTTP(S), supports IDN/IP and rejects ambiguous unsafe syntax",()=>{
    Equal("https://example.com/",ProjectUrlValidator.Normalize("  HTTPS://EXAMPLE.COM:443  "));Equal("http://127.0.0.1:8080/a",ProjectUrlValidator.Normalize("http://127.0.0.1:8080/a"));True(ProjectUrlValidator.Normalize("https://[::1]/").StartsWith("https://[::1]"));True(ProjectUrlValidator.Normalize("https://例子.测试/路径").StartsWith("https://"));
    foreach(var url in new[]{"file:///C:/x","javascript:alert(1)","data:text/html,x","shell:AppsFolder","//example.com","https://","https:///x","https://bad host/","https://example.com/hi there","https://user:pass@example.com/","https://@example.com/","https://example.com\\evil","https://example.com:99999/","https://example.com:abc/","https://example..com/","https://exa_mple.com/","https://example.com/\n","https://example.com/\u0000"})Throws(()=>ProjectUrlValidator.Normalize(url));
    var prefix="https://example.com/";Equal(1024,ProjectUrlValidator.Normalize(prefix+new string('a',1024-prefix.Length)).Length);Throws(()=>ProjectUrlValidator.Normalize(prefix+new string('a',1025-prefix.Length)));Throws(()=>ProjectUrlValidator.Normalize(prefix+new string('名',400)));
    var s=new ProjectService(Dir(),(_,_)=>false);var a=s.Save(Project("web")with{Items=[new(){Type="web",Url=" HTTPS://EXAMPLE.COM:443 ",Name=" Label "}]});Equal("https://example.com/",a.Items[0].Url);Equal("Label",a.Items[0].Name);
    Throws(()=>s.Save(Project("dup")with{Items=[new(){Type="web",Url="https://example.com"},new(){Type="web",Url="HTTPS://EXAMPLE.COM:443/"}]}));Throws(()=>s.Save(Project("bad label")with{Items=[new(){Type="web",Url="https://example.com",Name=""}]}));
});
foreach(var filename in new[]{"activity.json","projects.json"}) {
    foreach(var bad in new[]{"{\"schemaVersion\":2}","{\"unknown\":"+new string('[',20)+"0"+new string(']',20)+",\"schemaVersion\":2}","{\"schemaVersion\":99999999999999999999999999999}","{\"schemaVersion\":1.0}","{\"schemaVersion\":1,\"schemaVersion\":2}","{\"schemaVersion\":2,\"schemaVersion\":1}","{\"schemaVersion\":1,\"schemaVersion\":1}","{\"schemaVersion\":\"1\"}","{}","[]","{broken","{\"schemaVersion\":1}","{\"schemaVersion\":1,\"items\":[],\"projects\":[],\"unknown\":true}"}) {
        Test(filename+" preserves and blocks malformed/unsupported schema "+bad[..Math.Min(55,bad.Length)],()=>{
            var d=Dir();var path=Path.Combine(d,filename);var bytes=Encoding.UTF8.GetBytes(bad);File.WriteAllBytes(path,bytes);Blocked(d,filename);Bytes(bytes,File.ReadAllBytes(path));Equal(1,Directory.GetFiles(d).Length);
        });
    }
    Test(filename+" preserves BOM future schema after deeply nested unknown field",()=>{var d=Dir();var path=Path.Combine(d,filename);var bytes=Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("{\"unknown\":"+new string('[',20)+"0"+new string(']',20)+",\"schemaVersion\":2}")).ToArray();File.WriteAllBytes(path,bytes);Blocked(d,filename);Bytes(bytes,File.ReadAllBytes(path));});
    Test(filename+" accepts BOM schema1 and rejects actual oversized/unreadable files",()=>{
        var d=Dir();var path=Path.Combine(d,filename);var text=filename=="activity.json"?"{\"schemaVersion\":1,\"items\":[]}":"{\"schemaVersion\":1,\"projects\":[]}";File.WriteAllBytes(path,Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(text)).ToArray());if(filename=="activity.json")True(new ActivityService(d).RecoveryWarning is null);else True(new ProjectService(d,(_,_)=>true).RecoveryWarning is null);
        File.WriteAllBytes(path,new byte[16*1024*1024+1]);Blocked(d,filename);Equal(16*1024*1024+1,new FileInfo(path).Length);
        File.WriteAllText(path,text);using(var locked=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){Blocked(d,filename);}Equal(text,File.ReadAllText(path));
    });
    Test(filename+" guards every mutation when invalid bytes are swapped after initialization",()=>{
        foreach(var invalid in new[]{"{\"schemaVersion\":2}","{broken","{}"}) {
            var d=Dir();var path=Path.Combine(d,filename);if(filename=="activity.json"){var s=new ActivityService(d);s.Record("app",Id(),"started");var before=s.Snapshot();var count=0;s.Changed+=()=>count++;File.WriteAllText(path,invalid);Throws(()=>s.Record("app",Id(),"started"));Throws(s.Clear);Equal(before[0],s.Snapshot()[0]);Equal(0,count);True(s.RecoveryWarning is not null);}else{var s=new ProjectService(d,(_,_)=>true);var a=s.Save(Project("A"));var count=0;s.Changed+=()=>count++;File.WriteAllText(path,invalid);Throws(()=>s.Save(Project("B")));Throws(()=>s.Remove(a.Id));Equal(a.Id,s.Snapshot()[0].Id);Equal(0,count);True(s.RecoveryWarning is not null);}Equal(invalid,File.ReadAllText(path));Equal(1,Directory.GetFiles(d).Length);
        }
    });
    Test(filename+" replacement lock preserves disk/state, emits no event and removes own temp",()=>{
        var d=Dir();var path=Path.Combine(d,filename);if(filename=="activity.json"){var s=new ActivityService(d);s.Record("app",Id(),"started");var before=File.ReadAllBytes(path);var old=s.Snapshot()[0];var events=0;s.Changed+=()=>events++;using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){Throws(()=>s.Record("file",Id(),"submitted"));Throws(s.Clear);}Equal(old,s.Snapshot()[0]);Equal(0,events);Bytes(before,File.ReadAllBytes(path));True(s.RecoveryWarning is not null);s.Clear();Equal(0,s.Snapshot().Count);}else{var s=new ProjectService(d,(_,_)=>true);var a=s.Save(Project("A"));var before=File.ReadAllBytes(path);var events=0;s.Changed+=()=>events++;using(var locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){Throws(()=>s.Save(Project("B")));Throws(()=>s.Remove(a.Id));}Equal(a.Id,s.Snapshot()[0].Id);Equal(0,events);Bytes(before,File.ReadAllBytes(path));True(s.RecoveryWarning is not null);s.Remove(a.Id);Equal(0,s.Snapshot().Count);}Equal(1,Directory.GetFiles(d).Length);
    });
}
Test("Loaded activity validates duplicates, count, exact success and IDs rather than trusting JSON",()=>{
    foreach(var items in new[]{new[]{new ActivityRecord("app",Id(),DateTimeOffset.UtcNow,"failed")},new[]{new ActivityRecord("web",Id(),DateTimeOffset.UtcNow,"started")},new[]{new ActivityRecord("app","bad",DateTimeOffset.UtcNow,"started")},Enumerable.Range(0,51).Select(_=>new ActivityRecord("app",Id(),DateTimeOffset.UtcNow,"started")).ToArray()}){var d=Dir();File.WriteAllText(Path.Combine(d,"activity.json"),JsonSerializer.Serialize(new ActivityFile{Items=items.ToList()},Json()));Blocked(d,"activity.json");}
    var duplicate=new ActivityRecord("app",Id(),DateTimeOffset.UtcNow,"started");var directory=Dir();File.WriteAllText(Path.Combine(directory,"activity.json"),JsonSerializer.Serialize(new ActivityFile{Items=[duplicate,duplicate]},Json()));Blocked(directory,"activity.json");
});
Test("Loaded projects reject bad URL, duplicate IDs and invalid contiguous order but retain absent entities",()=>{
    foreach(var projects in new[]{new[]{Project("bad")with{Items=[new(){Type="web",Url="file:///C:/x"}]}},new[]{Project("A")with{DisplayOrder=1}},new[]{Project("A"),Project("B")with{DisplayOrder=0}}}){var d=Dir();File.WriteAllText(Path.Combine(d,"projects.json"),JsonSerializer.Serialize(new ProjectFile{Projects=projects.ToList()},Json()));Blocked(d,"projects.json");}
    var good=Project("retained")with{Items=[new(){Type="app",EntityId=Id()}]};var directory=Dir();File.WriteAllText(Path.Combine(directory,"projects.json"),JsonSerializer.Serialize(new ProjectFile{Projects=[good]},Json()));var service=new ProjectService(directory,(_,_)=>false);True(service.RecoveryWarning is null);Equal(good.Items[0].EntityId,service.Snapshot()[0].Items[0].EntityId);
});
Test("URL authority requires explicit unambiguous host and a valid nonempty port",()=>{
    foreach(var url in new[]{"https:example.com","https:/example.com","http:////example.com","https://example.com:/","https://example.com:65536/","https://example.com%5cevil/","https://example.com%40evil/"}) {
        try { ProjectUrlValidator.Normalize(url); } catch(SettingsValidationException) {continue;}throw new Exception("unexpected accepted URL: "+url);
    }
});
Test("Blocked files require explicit restart after repair and external unreadable/oversized swaps preserve last commit",()=>{
    foreach(var filename in new[]{"activity.json","projects.json"}) {
        var d=Dir();var path=Path.Combine(d,filename);File.WriteAllText(path,"{}");
        if(filename=="activity.json"){var s=new ActivityService(d);File.WriteAllText(path,"{\"schemaVersion\":1,\"items\":[]}");Throws(s.Clear);var writable=new ActivityService(d);writable.Record("app",Id(),"started");var before=writable.Snapshot()[0];using(var locked=new FileStream(path,FileMode.Open,FileAccess.ReadWrite,FileShare.None)){Throws(writable.Clear);}Equal(before,writable.Snapshot()[0]);}
        else{var s=new ProjectService(d,(_,_)=>true);File.WriteAllText(path,"{\"schemaVersion\":1,\"projects\":[]}");Throws(()=>s.Save(Project("denied")));var writable=new ProjectService(d,(_,_)=>true);var a=writable.Save(Project("A"));File.WriteAllBytes(path,new byte[16*1024*1024+1]);Throws(()=>writable.Remove(a.Id));Equal(a.Id,writable.Snapshot()[0].Id);Equal(16*1024*1024+1,new FileInfo(path).Length);}
    }
});
Test("Serialized schema1 byte ceiling rejects an oversized candidate without publishing or replacing",()=>{
    var projects=Enumerable.Range(0,200).Select(p=>Project(new string('名',80))with{DisplayOrder=p,Description=new string('描',500),Items=Enumerable.Range(0,100).Select(i=>new ProjectItem{Type="web",Name=new string('标',80),Url="https://example.com/"+i.ToString("D3")+new string('a',1001)}).ToList()}).ToList();
    // URL is exactly 1024 ASCII characters: prefix20 + index3 + tail1001.
    Equal(1024,projects[0].Items[0].Url!.Length);
    var options=Json();options.WriteIndented=true;byte[] Serialize(int count)=>JsonSerializer.SerializeToUtf8Bytes(new ProjectFile{Projects=projects.Take(count).ToList()},options);
    var low=0;var high=200;while(low+1<high){var mid=(low+high)/2;if(Serialize(mid).Length<=16*1024*1024)low=mid;else high=mid;}
    True(low>0&&low<200);var d=Dir();var path=Path.Combine(d,"projects.json");var original=Serialize(low);File.WriteAllBytes(path,original);var s=new ProjectService(d,(_,_)=>false);True(s.RecoveryWarning is null);var count=0;s.Changed+=()=>count++;Throws(()=>s.Save(projects[low]));Bytes(original,File.ReadAllBytes(path));Equal(low,s.Snapshot().Count);Equal(0,count);Equal(1,Directory.GetFiles(d).Length);
});
Test("Actual route budget admits bounded project saves without widening ordinary requests",()=>{
    True(DesktopHub.Windows.ResourceRequestParser.EnvelopeAllowed("projects.save",768*1024));
    True(!DesktopHub.Windows.ResourceRequestParser.EnvelopeAllowed("projects.save",768*1024+1));
    True(!DesktopHub.Windows.ResourceRequestParser.EnvelopeAllowed("activity.clear",16385));
    True(!DesktopHub.Windows.ResourceRequestParser.EnvelopeAllowed("projects.openWeb",16385));
});
Test("Strict activity/project parser rejects missing duplicate unknown and wrong-kind fields",()=>{
    object Parse(string route,string json){using var doc=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=16});var root=doc.RootElement;return route switch{"save"=>DesktopHub.Windows.ActivityProjectRequestParser.Save(root),"query"=>DesktopHub.Windows.ActivityProjectRequestParser.Query(root)??"none","remove"=>DesktopHub.Windows.ActivityProjectRequestParser.Remove(root),"web"=>DesktopHub.Windows.ActivityProjectRequestParser.OpenWeb(root),_=>Empty(root)};}
    object Empty(JsonElement root){DesktopHub.Windows.ActivityProjectRequestParser.Empty(root);return true;}
    Equal("none",Parse("query","{}"));foreach(var json in new[]{"[]","null","{\"unexpected\":true}","{\"projectId\":null}","{\"projectId\":\"bad\"}"})Throws(()=>Parse("query",json));
    foreach(var route in new[]{"remove","web","save"})Throws(()=>Parse(route,"{}"));Throws(()=>Parse("empty","{\"x\":true}"));
    var p=Project("valid")with{Description="line1\r\nline2\t",Items=[new(){Type="app",EntityId=Id()}]};var valid=JsonSerializer.Serialize(new{project=p},Json());Equal(p.Id,((Project)Parse("save",valid)).Id);
    foreach(var json in new[]{valid.Replace("\"name\":\"valid\"","\"name\":\"valid\",\"name\":\"second\""),valid.Replace("\"displayOrder\":0","\"displayOrder\":\"0\""),valid.Replace("\"pinnedToHome\":false","\"pinnedToHome\":null"),valid.Replace("\"description\":","\"unknown\":true,\"description\":"),valid.Replace("\"type\":\"app\"","\"type\":\"shell\""),valid.Replace("\"url\":null","\"url\":\"https://example.com/\""),valid.Replace("\"name\":null","\"name\":null,\"path\":\"C:/outside\""),"{\"project\":[]}"})Throws(()=>Parse("save",json));
    var id=Id();Throws(()=>Parse("web",$"{{\"projectId\":\"{id}\",\"itemId\":\"{id}\",\"url\":\"https://example.com\"}}"));Throws(()=>Parse("remove",$"{{\"projectId\":\"{id}\",\"projectId\":\"{Id()}\"}}"));
});
Test("Real worst-case serialized accepted project envelope fits only the targeted bounded route",()=>{
    var p=Project(new string('"',80))with{Description=new string('名',500),DisplayOrder=199,Items=Enumerable.Range(0,100).Select(i=>new ProjectItem{Type="web",Name=new string('"',80),Url="https://example.com/"+i.ToString("D3")+new string('&',1001)}).ToList()};
    var json=JsonSerializer.Serialize(new{version=1,id=new string('名',96),type="projects.save",payload=new{project=p}},Json());True(json.Length>16*1024);True(json.Length<=768*1024);True(json.Length<=1024*1024);True(DesktopHub.Windows.ResourceRequestParser.EnvelopeAllowed("projects.save",json.Length));True(!DesktopHub.Windows.ResourceRequestParser.EnvelopeAllowed("projects.query",json.Length));using var doc=JsonDocument.Parse(json);var parsed=DesktopHub.Windows.ActivityProjectRequestParser.Save(doc.RootElement.GetProperty("payload"));var service=new ProjectService(Dir(),(_,_)=>false);Equal(100,service.Save(parsed).Items.Count);Console.WriteLine("Maximal accepted project envelope chars="+json.Length);
});
Test("Current metadata resolution cannot retain deleted paths and uses current names statuses and types",()=>{
    var appId=Id();var sceneId=Id();var fileId=Id();var settings=new AppSettings{AppEntries=[new(){Id=appId,Name="Current app",Target="C:/current.exe"}],Scenes=[new(){Id=sceneId,Name="Current scene",Enabled=false}]};var resources=new ResourceLibrary{Resources=[new(){Id=fileId,DisplayName="Current file",CanonicalPath="C:/current.txt",Status="Missing"}]};
    var a=DesktopHub.Windows.ActivityProjectMetadata.Resolve("app",appId,settings,resources);Equal("Current app",a.Name);True(a.CanExecute);Equal("Disabled",DesktopHub.Windows.ActivityProjectMetadata.Resolve("scene",sceneId,settings,resources).Status);Equal("Missing",DesktopHub.Windows.ActivityProjectMetadata.Resolve("file",fileId,settings,resources).Status);True(!DesktopHub.Windows.ActivityProjectMetadata.Resolve("file",fileId,settings,resources).CanExecute);Equal("Removed",DesktopHub.Windows.ActivityProjectMetadata.Resolve("folder",fileId,settings,resources).Status);
    settings.AppEntries.Clear();var deleted=DesktopHub.Windows.ActivityProjectMetadata.Activity(new("app",appId,DateTimeOffset.UtcNow,"started"),settings,resources);Equal("Removed",deleted.Status);True(!deleted.CanExecute);True(!deleted.Detail.Contains("current.exe"));
    var web=DesktopHub.Windows.ActivityProjectMetadata.Item(new(){Type="web",Url="https://example.com",Name="Current label"},settings,resources);Equal("Current label",web.Name);Equal("https://example.com/",web.Item.Url);
});
Test("Resource history eligibility uses only exact requested indexed identity and actual content opens",()=>{
    var file=Id();var folder=Id();var library=new ResourceLibrary{Resources=[new(){Id=file,ResourceType="file"},new(){Id=folder,ResourceType="folder"}]};
    Equal(("file",file),DesktopHub.Windows.ResourceRequestParser.ActivityReference(library,file,"open")!.Value);Equal(("folder",folder),DesktopHub.Windows.ResourceRequestParser.ActivityReference(library,folder,"open")!.Value);Equal(("folder",folder),DesktopHub.Windows.ResourceRequestParser.ActivityReference(library,folder,"folder")!.Value);
    True(DesktopHub.Windows.ResourceRequestParser.ActivityReference(library,file,"folder") is null);True(DesktopHub.Windows.ResourceRequestParser.ActivityReference(library,file,"reveal") is null);True(DesktopHub.Windows.ResourceRequestParser.ActivityReference(library,null,"open") is null);True(DesktopHub.Windows.ResourceRequestParser.ActivityReference(library,Id(),"open") is null);
});
Test("Concurrent activity writes remain a capped readable transaction history",()=>{var d=Dir();var s=new ActivityService(d);Parallel.For(0,75,i=>s.Record("app",GuidFrom(i+1000),"started"));Equal(50,s.Snapshot().Count);Equal(50,new ActivityService(d).Snapshot().Count);Equal(50,s.Snapshot().Select(x=>x.Id).Distinct().Count());});
} finally {var resolved=Path.GetFullPath(root);if(!resolved.StartsWith(AppContext.BaseDirectory,StringComparison.OrdinalIgnoreCase))throw new Exception("unsafe fixture cleanup");Directory.Delete(resolved,true);}
Console.WriteLine($"ActivityProject: {passed} passed, {failed} failed");Environment.ExitCode=failed;

void Test(string name,Action body){try{body();passed++;Console.WriteLine("PASS "+name);}catch(Exception e){failed++;Console.WriteLine("FAIL "+name+": "+e);}}
string Dir(){var path=Path.Combine(root,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);return path;}
static string Id()=>Guid.NewGuid().ToString("D");
static string GuidFrom(int i)=>$"{i:x8}-0000-0000-0000-000000000001";
static Project Project(string name)=>new(){Name=name};
static void True(bool v){if(!v)throw new Exception("expected true");}
static void Equal<T>(T expected,T actual){if(!EqualityComparer<T>.Default.Equals(expected,actual))throw new Exception($"expected {expected}, got {actual}");}
static void Bytes(byte[] expected,byte[] actual)=>True(expected.SequenceEqual(actual));
static void Throws(Action a){try{a();}catch(Exception e)when(e is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or SettingsValidationException or JsonException){return;}throw new Exception("expected guarded rejection");}
void Blocked(string d,string filename){if(filename=="activity.json"){var s=new ActivityService(d);True(s.RecoveryWarning is not null);Throws(()=>s.Record("app",Id(),"started"));Throws(s.Clear);Equal(0,s.Snapshot().Count);}else{var s=new ProjectService(d,(_,_)=>true);True(s.RecoveryWarning is not null);Throws(()=>s.Save(Project("new")));Throws(()=>s.Remove(Id()));Equal(0,s.Snapshot().Count);}}
static JsonSerializerOptions Json()=>new(){PropertyNamingPolicy=JsonNamingPolicy.CamelCase};
sealed class FixedClock:TimeProvider{public override DateTimeOffset GetUtcNow()=>new(2026,10,14,12,0,0,TimeSpan.Zero);}
