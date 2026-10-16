using System.IO;
using DesktopHub.Core;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace DesktopHub.Windows;

public sealed record LayoutRect(int Left,int Top,int Width,int Height);
public sealed record LayoutMonitor(string Id,LayoutRect Screen,LayoutRect Work,double Dpi);
// Runtime identity remains private to the native service; none of these fields enter persistence or bridge DTOs.
public sealed record LayoutWindow(long Handle,int ProcessId,long Started,string ExecutablePath,string ClassName,string Title,string MonitorId,LayoutRect NormalBounds,string State,string SkipReason);
public interface IWindowLayoutBackend { IReadOnlyList<LayoutWindow> Enumerate(); IReadOnlyList<LayoutMonitor> Monitors(); string FallbackMonitorId(); bool Apply(LayoutWindow window,LayoutRect bounds,string state); }
public sealed record LayoutSelection(string CandidateToken,string AppEntryId,string TitleContains);
public sealed record LayoutSaveRequest(string SceneId,string CaptureToken,IReadOnlyList<LayoutSelection> Selections,bool RestoreAfterRun,int WaitTimeoutMs);
public sealed record LayoutChoice(string EntryToken,string CandidateToken);
public sealed record LayoutChooseRequest(string RestoreToken,IReadOnlyList<LayoutChoice> Choices);
public sealed record LayoutCandidate(string CandidateToken,string Title,IReadOnlyList<string> AppIds,string MonitorId,WindowBounds Bounds,string State,double Dpi,string SkipReason);
public sealed record LayoutCapture(string CaptureToken,IReadOnlyList<LayoutCandidate> Candidates,bool Truncated);
public sealed record LayoutChoiceCandidate(string CandidateToken,string Title);
public sealed record LayoutEntryResult(string EntryToken,string AppEntryId,string Status,string Code,string Message,IReadOnlyList<LayoutChoiceCandidate> Candidates);
public sealed record LayoutRestoreResult(string SceneId,string? RestoreToken,IReadOnlyList<LayoutEntryResult> Entries)
{
    public string? ResultId { get; init; }
    public long Order { get; init; }
    public string? Kind { get; init; }
    public string? ExecutionId { get; init; }
}

/// <summary>Only capture/choice tokens authorize interaction. HWNDs never cross the bridge.</summary>
public sealed class SceneWindowLayoutService
{
    private readonly SettingsStore store;
    private readonly Func<AppEntry,string> resolve;
    private readonly IWindowLayoutBackend backend;
    private readonly TimeProvider time;
    private readonly object gate = new();
    private readonly object operationGate = new();
    private long sessionOrdinal;
    private readonly Dictionary<string,CaptureSession> captures = [];
    private readonly Dictionary<string,ChoiceSession> choices = [];
    private readonly Dictionary<CancellationTokenSource,string> operations = [];
    private sealed record Captured(LayoutWindow Window,LayoutCandidate Dto);
    private sealed record CaptureSession(string Owner,Scene Scene,string Fingerprint,long Created,Dictionary<string,Captured> Candidates,long Ordinal);
    private sealed record ChoiceItem(SceneWindowPlacement Placement,Dictionary<string,LayoutWindow> Candidates);
    private sealed record ChoiceSession(string Owner,Scene Scene,string? LiveFingerprint,long Created,string Fallback,Dictionary<string,ChoiceItem> Items,HashSet<long> UsedHandles,List<LayoutEntryResult> Results,long Ordinal);
    public SceneWindowLayoutService(SettingsStore store,NativeLaunchService launcher,IWindowLayoutBackend backend,TimeProvider? time=null):this(store,app=>launcher.ResolveEntry(app).Target,backend,time) { }
    public SceneWindowLayoutService(SettingsStore store,Func<AppEntry,string> resolve,IWindowLayoutBackend backend,TimeProvider? time=null)
    {this.store=store;this.resolve=resolve;this.backend=backend;this.time=time??TimeProvider.System;}

    public Task<LayoutCapture> CaptureAsync(string owner, string sceneId, CancellationToken token = default) =>
        RunOwnedAsync(owner, cancellation => Task.FromResult(CaptureCore(owner, sceneId, cancellation)), token);

    public Task<AppSettings> SaveAsync(string owner, LayoutSaveRequest request, CancellationToken token = default) =>
        RunOwnedAsync(owner, cancellation => Task.FromResult(SaveCore(owner, request, cancellation)), token);

    public LayoutCapture Capture(string owner, string sceneId) => CaptureCore(owner, sceneId, CancellationToken.None);

    private LayoutCapture CaptureCore(string owner, string sceneId, CancellationToken cancellation)
    {
        Owner(owner); LayoutRequestParser.Id(sceneId); cancellation.ThrowIfCancellationRequested();
        var settings = store.Current;
        var scene = Find(settings, sceneId);
        var apps = AuthorizedApps(scene, settings);
        var monitors = backend.Monitors();
        var windows = backend.Enumerate().Where(window => apps.Values.Any(path => PathEquals(path, window.ExecutablePath))).Take(129).ToList();
        var records = new Dictionary<string, Captured>();
        foreach (var window in windows.Take(128))
        {
            cancellation.ThrowIfCancellationRequested();
            var appIds = apps.Where(app => PathEquals(app.Value, window.ExecutablePath)).Select(app => app.Key).ToArray();
            var monitor = monitors.FirstOrDefault(item => item.Id == window.MonitorId);
            var reason = window.SkipReason;
            if (monitor is null || !ValidMonitor(monitor)) reason = "monitorUnavailable";
            var bounds = monitor is not null && ValidMonitor(monitor) ? ToDip(window.NormalBounds, monitor) : new WindowBounds();
            var token = Token();
            var dto = new LayoutCandidate(token, window.Title, appIds, window.MonitorId, bounds, window.State, monitor?.Dpi ?? 96, reason);
            records[token] = new(window, dto);
        }
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            Prune();
            var captureToken = Token();
            captures.Add(captureToken, new(owner, SceneValidator.Clone(scene), Fingerprint(scene), time.GetTimestamp(), records, ++sessionOrdinal));
            TrimSessions();
            return new(captureToken, records.Values.Select(record => record.Dto).ToArray(), windows.Count > 128);
        }
    }

    public AppSettings Save(string owner, LayoutSaveRequest request) => SaveCore(owner, request, CancellationToken.None);

    private AppSettings SaveCore(string owner, LayoutSaveRequest request, CancellationToken cancellation)
    {
        Owner(owner); LayoutRequestParser.Validate(request); cancellation.ThrowIfCancellationRequested();
        CaptureSession session;
        lock (gate)
        {
            Prune();
            if (!captures.TryGetValue(request.CaptureToken, out session!) || session.Owner != owner || session.Scene.Id != request.SceneId)
                throw Invalid("布局预览已失效，请重新捕获。");
        }
        var observed = backend.Enumerate();
        var placements = new List<SceneWindowPlacement>();
        var selectedWindows = new HashSet<long>();
        foreach (var selection in request.Selections)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!session.Candidates.TryGetValue(selection.CandidateToken, out var record) || !record.Dto.AppIds.Contains(selection.AppEntryId) || record.Dto.SkipReason.Length > 0 || !selectedWindows.Add(record.Window.Handle))
                throw Invalid("窗口选择无效或重复。");
            if (!observed.Any(window => SameIdentity(window, record.Window) && window.SkipReason.Length == 0))
                throw Invalid("窗口已关闭或身份改变，请重新捕获。");
            placements.Add(new() { AppEntryId = selection.AppEntryId, ExecutablePath = record.Window.ExecutablePath, TitleContains = selection.TitleContains, MonitorId = record.Dto.MonitorId, Bounds = record.Dto.Bounds with { }, State = record.Dto.State, Dpi = record.Dto.Dpi });
        }
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!captures.TryGetValue(request.CaptureToken, out var current) || !ReferenceEquals(current, session))
                throw Invalid("布局预览已失效，请重新捕获。");
            // Reserve once before persistence. No filesystem or native operation holds the session lock.
            captures.Remove(request.CaptureToken);
        }
        return store.Update(settings =>
        {
            cancellation.ThrowIfCancellationRequested();
            var scene = Find(settings, request.SceneId);
            if (Fingerprint(scene) != session.Fingerprint) throw Invalid("场景已修改，请重新捕获。");
            foreach (var placement in placements)
                if (Authorization(scene, placement, settings) is { } error) throw Invalid(error);
            var index = settings.Scenes.FindIndex(item => item.Id == scene.Id);
            settings.Scenes[index] = SceneValidator.Normalize(scene with { WindowLayout = new() { RestoreAfterRun = request.RestoreAfterRun, WaitTimeoutMs = request.WaitTimeoutMs, Windows = placements } });
            return settings;
        });
    }

    private async Task<T> RunOwnedAsync<T>(string owner, Func<CancellationToken, Task<T>> operation, CancellationToken token)
    {
        Owner(owner);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (operationGate)
        {
            if (operations.Count >= 8) throw Invalid("正在处理的布局操作过多，请稍后重试。");
            operations.Add(linked, owner);
        }
        try
        {
            // Always queue the whole native operation, including the already-present restore path.
            return await Task.Run(() => operation(linked.Token)).ConfigureAwait(false);
        }
        finally { lock (operationGate) operations.Remove(linked); }
    }

    public Task<LayoutRestoreResult> RestoreAsync(string owner,string sceneId,CancellationToken token)
    {LayoutRequestParser.Id(sceneId);return RestoreAsync(owner,Find(store.Current,sceneId),token);}

    // The execution host passes its deep-frozen Scene here, never a live scene lookup after launch.
    public Task<LayoutRestoreResult> RestoreAsync(string owner,Scene frozenScene,CancellationToken token)
    {
        // Freeze before queueing so callers cannot race a queued operation by editing their DTO.
        var snapshot = SceneValidator.Clone(frozenScene);
        return RunOwnedAsync(owner, cancellation => RestoreCoreAsync(owner, snapshot, cancellation), token);
    }
    private async Task<LayoutRestoreResult> RestoreCoreAsync(string owner,Scene frozenScene,CancellationToken token)
    {
        Owner(owner); var scene=SceneValidator.Normalize(SceneValidator.Clone(frozenScene));
        var placements=scene.WindowLayout?.Windows??[];if(placements.Count>32)throw Invalid("布局最多保存 32 个窗口。");
        var ids=placements.Select(_=>Token()).ToArray();var fallback=backend.FallbackMonitorId();
        var start=time.GetTimestamp();var timeout=TimeSpan.FromMilliseconds(scene.WindowLayout?.WaitTimeoutMs??15000);
        List<LayoutWindow>[] matches=placements.Select(_=>new List<LayoutWindow>()).ToArray();
        string?[] errors=new string?[placements.Count];
        try
        {
            while(true)
            {
                token.ThrowIfCancellationRequested(); var settings=store.Current; var windows=backend.Enumerate();
                for(var i=0;i<placements.Count;i++)
                {
                    errors[i]=Authorization(scene,placements[i],settings);
                    matches[i]=errors[i] is null?windows.Where(w=>Matches(w,placements[i])).ToList():[];
                }
                if(Enumerable.Range(0,placements.Count).All(i=>errors[i] is not null||matches[i].Count>0)||time.GetElapsedTime(start)>=timeout)break;
                var remaining=timeout-time.GetElapsedTime(start);if(remaining<=TimeSpan.Zero)break;
                await Task.Delay(remaining<TimeSpan.FromMilliseconds(250)?remaining:TimeSpan.FromMilliseconds(250),time,token).ConfigureAwait(false);
            }
        }
        catch(OperationCanceledException)
        {return new(scene.Id,null,placements.Select((p,i)=>Result(ids[i],p,"cancelled","cancelled")).ToArray());}
        var shared=matches.SelectMany((list,i)=>list.Where(w=>w.SkipReason.Length==0).Select(w=>(w.Handle,i))).GroupBy(x=>x.Handle).Where(g=>g.Select(x=>x.i).Distinct().Count()>1).Select(g=>g.Key).ToHashSet();
        var items=new Dictionary<string,ChoiceItem>();var results=new List<LayoutEntryResult>();var used=new HashSet<long>();
        for(var i=0;i<placements.Count;i++)
        {
            var p=placements[i];var id=ids[i];
            if(token.IsCancellationRequested){results.Add(Result(id,p,"cancelled","cancelled"));continue;}
            if(errors[i] is { } code){results.Add(Result(id,p,"skipped",code));continue;}
            var eligible=matches[i].Where(w=>w.SkipReason.Length==0).ToList();
            if(eligible.Count==0){results.Add(Result(id,p,matches[i].Count>0?"skipped":"notFound",matches[i].FirstOrDefault()?.SkipReason??"windowNotFound"));continue;}
            if(eligible.Count>1||eligible.Any(w=>shared.Contains(w.Handle)))
            {
                var map=eligible.Take(128).ToDictionary(_=>Token(),w=>w);items[id]=new(p,map);
                results.Add(new(id,p.AppEntryId,"needsChoice",eligible.Any(w=>shared.Contains(w.Handle))?"sharedWindow":"multipleWindows","请选择窗口；同一窗口只能用于一个位置。",map.Select(x=>new LayoutChoiceCandidate(x.Key,x.Value.Title)).ToArray()));
                continue;
            }
            used.Add(eligible[0].Handle);results.Add(Move(scene,id,p,eligible[0],fallback));
        }
        string? restoreToken=null;
        if(items.Count>0)
        {
            lock(gate){if(token.IsCancellationRequested)return new(scene.Id,null,results.Select(r=>r.Status=="needsChoice"?r with{Status="cancelled",Code="cancelled",Message="布局恢复已取消。",Candidates=[]}:r).ToArray());Prune();restoreToken=Token();var live=store.Current.Scenes.FirstOrDefault(s=>s.Id==scene.Id);choices.Add(restoreToken,new(owner,scene,live is null?null:Fingerprint(live),time.GetTimestamp(),fallback,items,used,results,++sessionOrdinal));TrimSessions();}
        }
        return new(scene.Id,restoreToken,results);
    }

    public Task<LayoutRestoreResult> ChooseAsync(string owner, LayoutChooseRequest request, CancellationToken token = default) =>
        RunOwnedAsync(owner, cancellation => Task.FromResult(ChooseCore(owner, request, cancellation)), token);

    public LayoutRestoreResult Choose(string owner, LayoutChooseRequest request) => ChooseCore(owner, request, CancellationToken.None);

    private LayoutRestoreResult ChooseCore(string owner, LayoutChooseRequest request, CancellationToken cancellation)
    {
        Owner(owner); LayoutRequestParser.Validate(request); cancellation.ThrowIfCancellationRequested();
        ChoiceSession session;
        lock (gate)
        {
            Prune();
            if (!choices.TryGetValue(request.RestoreToken, out session!) || session.Owner != owner)
                throw Invalid("布局选择已失效，请重新恢复。");
        }
        var live = store.Current.Scenes.FirstOrDefault(scene => scene.Id == session.Scene.Id);
        if ((live is null ? null : Fingerprint(live)) != session.LiveFingerprint) throw Invalid("场景已修改，请重新恢复布局。");
        var handles = new HashSet<long>(session.UsedHandles);
        var selected = new List<(string Id, ChoiceItem Item, LayoutWindow Window)>();
        var observed = backend.Enumerate();
        foreach (var choice in request.Choices)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!session.Items.TryGetValue(choice.EntryToken, out var item) || !item.Candidates.TryGetValue(choice.CandidateToken, out var window) || !handles.Add(window.Handle))
                throw Invalid("窗口选择无效或同一窗口被重复使用。");
            if (!observed.Any(current => SameIdentity(current, window) && current.SkipReason.Length == 0 && Matches(current, item.Placement)) || Authorization(session.Scene, item.Placement, store.Current) is not null)
                throw Invalid("窗口身份或应用已改变，请重新恢复。");
            selected.Add((choice.EntryToken, item, window));
        }
        lock (gate)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!choices.TryGetValue(request.RestoreToken, out var current) || !ReferenceEquals(current, session))
                throw Invalid("布局选择已失效，请重新恢复。");
            // Validate the whole batch, then consume before moving. Verification never holds this lock.
            choices.Remove(request.RestoreToken);
        }
        var results = session.Results.Select(result => result.Status == "needsChoice" ? result with { Status = "skipped", Code = "notSelected", Message = "未选择窗口。", Candidates = [] } : result).ToList();
        foreach (var selection in selected)
        {
            var index = results.FindIndex(result => result.EntryToken == selection.Id);
            results[index] = cancellation.IsCancellationRequested
                ? Result(selection.Id, selection.Item.Placement, "cancelled", "cancelled")
                : Move(session.Scene, selection.Id, selection.Item.Placement, selection.Window, session.Fallback);
        }
        return new(session.Scene.Id, null, results);
    }

    public void CancelOwner(string owner)
    {
        lock (operationGate)
            foreach (var operation in operations.Where(item => item.Value == owner).Select(item => item.Key).ToArray()) operation.Cancel();
        lock (gate)
        {
            foreach (var key in captures.Where(item => item.Value.Owner == owner).Select(item => item.Key).ToArray()) captures.Remove(key);
            foreach (var key in choices.Where(item => item.Value.Owner == owner).Select(item => item.Key).ToArray()) choices.Remove(key);
        }
    }

    private LayoutEntryResult Move(Scene scene,string entry,SceneWindowPlacement p,LayoutWindow selected,string fallback)
    {
        if(Authorization(scene,p,store.Current) is { } error)return Result(entry,p,"skipped",error);
        var current=backend.Enumerate().FirstOrDefault(w=>SameIdentity(w,selected));
        if(current is null||!Matches(current,p))return Result(entry,p,"skipped","windowIdentityChanged");
        if(current.SkipReason.Length>0)return Result(entry,p,"skipped",current.SkipReason);
        var monitors=backend.Monitors();var m=monitors.FirstOrDefault(m=>m.Id==p.MonitorId)??monitors.FirstOrDefault(m=>m.Id==fallback)??monitors.FirstOrDefault();
        if(m is null||!ValidMonitor(m))return Result(entry,p,"skipped","monitorUnavailable");
        try{return Result(entry,p,backend.Apply(current,ToPixels(p.Bounds,m),p.State)?"restored":"failed","placement",m.Id==p.MonitorId?null:"原显示器缺失，已使用当前工作区。");}
        catch(Exception ex) when(ex is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException){return Result(entry,p,"failed","moveRejected");}
    }
    private Dictionary<string,string> AuthorizedApps(Scene scene,AppSettings settings)
    {
        var result=new Dictionary<string,string>();
        foreach(var app in settings.AppEntries.Where(a=>scene.Actions.Any(action=>action.Enabled&&action.Type=="application"&&action.AppEntryId==a.Id)))
        {try{result[app.Id]=resolve(app);}catch(Exception ex) when(ex is TargetValidationException or IOException or UnauthorizedAccessException or InvalidOperationException) { }}
        return result;
    }
    private string? Authorization(Scene scene,SceneWindowPlacement p,AppSettings settings)
    {
        if(!scene.Actions.Any(a=>a.Enabled&&a.Type=="application"&&a.AppEntryId==p.AppEntryId))return "appNotInScene";
        var app=settings.AppEntries.FirstOrDefault(a=>a.Id==p.AppEntryId);if(app is null)return "appNotRegistered";
        if(string.IsNullOrEmpty(p.ExecutablePath))return "recaptureRequired";
        try{return PathEquals(resolve(app),p.ExecutablePath)?null:"executableChanged";}catch(Exception ex) when(ex is TargetValidationException or IOException or UnauthorizedAccessException or InvalidOperationException){return "appUnavailable";}
    }
    private static bool Matches(LayoutWindow w,SceneWindowPlacement p)=>PathEquals(w.ExecutablePath,p.ExecutablePath)&&w.Title.Contains(p.TitleContains,StringComparison.OrdinalIgnoreCase);
    public static bool SameIdentity(LayoutWindow a,LayoutWindow b)=>a.Handle==b.Handle&&a.ProcessId==b.ProcessId&&a.Started==b.Started&&PathEquals(a.ExecutablePath,b.ExecutablePath)&&a.ClassName==b.ClassName;
    private static bool PathEquals(string a,string b)=>StringComparer.OrdinalIgnoreCase.Equals(a.Replace('/','\\'),b.Replace('/','\\'));
    private static LayoutEntryResult Result(string id,SceneWindowPlacement p,string status,string code,string? message=null)=>new(id,p.AppEntryId,status,code,message??Message(code,status),[]);
    private static string Message(string code,string status)=>code switch {"executableChanged"=>"登记应用的目标已修改，请重新捕获布局。","recaptureRequired"=>"旧布局缺少可验证的应用身份，请重新捕获。","fullscreen"=>"全屏窗口已跳过。","elevated"=>"更高权限窗口已跳过。","appNotRegistered"=>"登记应用已移除。","windowNotFound"=>"等待结束，未找到匹配窗口。","cancelled"=>"布局恢复已取消。","appNotInScene"=>"布局应用不在此场景的已登记动作中。",_=>status=="restored"?"窗口位置已恢复。":status=="failed"?"应用拒绝位置或恢复后位置不符。":"窗口不可控或身份已改变。"};
    private static bool ValidMonitor(LayoutMonitor m)=>m.Work.Width>0&&m.Work.Height>0&&double.IsFinite(m.Dpi)&&m.Dpi is >=48 and <=768;
    public static WindowBounds ToDip(LayoutRect r,LayoutMonitor m)=>new(){Left=(r.Left-m.Work.Left)*96d/m.Dpi,Top=(r.Top-m.Work.Top)*96d/m.Dpi,Width=r.Width*96d/m.Dpi,Height=r.Height*96d/m.Dpi};
    public static LayoutRect ToPixels(WindowBounds b,LayoutMonitor m)
    {
        var width=(int)Math.Clamp(Math.Round(b.Width*m.Dpi/96),1,m.Work.Width);var height=(int)Math.Clamp(Math.Round(b.Height*m.Dpi/96),1,m.Work.Height);
        var left=(int)Math.Clamp(Math.Round(m.Work.Left+b.Left*m.Dpi/96),m.Work.Left,(double)m.Work.Left+m.Work.Width-width);
        var top=(int)Math.Clamp(Math.Round(m.Work.Top+b.Top*m.Dpi/96),m.Work.Top,(double)m.Work.Top+m.Work.Height-height);return new(left,top,width,height);
    }
    // WINDOWPLACEMENT uses workspace coordinates for normal top-level windows.
    public static LayoutRect WorkspaceToScreen(LayoutRect r,LayoutMonitor m)=>r with{Left=r.Left+m.Work.Left-m.Screen.Left,Top=r.Top+m.Work.Top-m.Screen.Top};
    public static LayoutRect ScreenToWorkspace(LayoutRect r,LayoutMonitor m)=>r with{Left=r.Left-m.Work.Left+m.Screen.Left,Top=r.Top-m.Work.Top+m.Screen.Top};
    private static string Fingerprint(Scene scene)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(scene,SettingsStore.JsonOptions))));
    private static Scene Find(AppSettings settings,string id)=>settings.Scenes.FirstOrDefault(s=>s.Id==id)??throw Invalid("场景不存在。");
    private void Prune(){foreach(var k in captures.Where(x=>time.GetElapsedTime(x.Value.Created)>=TimeSpan.FromMinutes(5)).Select(x=>x.Key).ToArray())captures.Remove(k);foreach(var k in choices.Where(x=>time.GetElapsedTime(x.Value.Created)>=TimeSpan.FromMinutes(5)).Select(x=>x.Key).ToArray())choices.Remove(k);}
    private void TrimSessions(){while(captures.Count+choices.Count>8){var capture=captures.OrderBy(x=>x.Value.Ordinal).FirstOrDefault();var choice=choices.OrderBy(x=>x.Value.Ordinal).FirstOrDefault();if(choice.Value is null||capture.Value is not null&&capture.Value.Ordinal<choice.Value.Ordinal)captures.Remove(capture.Key);else choices.Remove(choice.Key);}}
    private static string Token()=>Guid.NewGuid().ToString("D");
    private static void Owner(string owner){if(string.IsNullOrWhiteSpace(owner)||owner.Length>200)throw Invalid("布局所有者无效。");}
    private static InvalidOperationException Invalid(string message)=>new(message);
}

public static class LayoutRequestParser
{
    public static string ParseSceneId(JsonElement root){Fields(root,"sceneId");return Id(Text(root,"sceneId",36));}
    public static LayoutSaveRequest ParseSave(JsonElement root)
    {
        Fields(root,"sceneId","captureToken","selections","restoreAfterRun","waitTimeoutMs");
        if(root.GetProperty("restoreAfterRun").ValueKind is not(JsonValueKind.True or JsonValueKind.False))throw Bad();
        if(!root.GetProperty("waitTimeoutMs").TryGetInt32(out var timeout))throw Bad();
        var rows=Array(root,"selections",32).Select(s=>{Fields(s,"candidateToken","appEntryId","titleContains");return new LayoutSelection(Id(Text(s,"candidateToken",36)),Id(Text(s,"appEntryId",36)),Text(s,"titleContains",200));}).ToArray();
        var request=new LayoutSaveRequest(Id(Text(root,"sceneId",36)),Id(Text(root,"captureToken",36)),rows,root.GetProperty("restoreAfterRun").GetBoolean(),timeout);Validate(request);return request;
    }
    public static LayoutChooseRequest ParseChoose(JsonElement root)
    {
        Fields(root,"restoreToken","choices");var rows=Array(root,"choices",32).Select(s=>{Fields(s,"entryToken","candidateToken");return new LayoutChoice(Id(Text(s,"entryToken",36)),Id(Text(s,"candidateToken",36)));}).ToArray();
        var request=new LayoutChooseRequest(Id(Text(root,"restoreToken",36)),rows);Validate(request);return request;
    }
    public static void Validate(LayoutSaveRequest request){Id(request.SceneId);Id(request.CaptureToken);if(request.Selections.Count>32||request.WaitTimeoutMs is <1 or >600000||request.Selections.Select(x=>x.CandidateToken).Distinct().Count()!=request.Selections.Count)throw Bad();foreach(var s in request.Selections){Id(s.CandidateToken);Id(s.AppEntryId);if(s.TitleContains is null||s.TitleContains.Length>200||s.TitleContains.Any(char.IsControl))throw Bad();}}
    public static void Validate(LayoutChooseRequest request){Id(request.RestoreToken);if(request.Choices.Count>32||request.Choices.Select(x=>x.EntryToken).Distinct().Count()!=request.Choices.Count||request.Choices.Select(x=>x.CandidateToken).Distinct().Count()!=request.Choices.Count)throw Bad();foreach(var s in request.Choices){Id(s.EntryToken);Id(s.CandidateToken);}}
    public static string Id(string value){if(!Guid.TryParseExact(value,"D",out var id)||id==Guid.Empty)throw Bad();return id.ToString("D");}
    private static void Fields(JsonElement root,params string[] fields){if(root.ValueKind!=JsonValueKind.Object)throw Bad();var seen=new HashSet<string>();foreach(var p in root.EnumerateObject())if(!fields.Contains(p.Name,StringComparer.Ordinal)||!seen.Add(p.Name))throw Bad();if(seen.Count!=fields.Length)throw Bad();}
    private static string Text(JsonElement root,string key,int max){var p=root.GetProperty(key);if(p.ValueKind!=JsonValueKind.String)throw Bad();var s=p.GetString()!;if(s.Length>max||s.Any(char.IsControl))throw Bad();return s;}
    private static IEnumerable<JsonElement> Array(JsonElement root,string key,int max){var p=root.GetProperty(key);if(p.ValueKind!=JsonValueKind.Array||p.GetArrayLength()>max)throw Bad();return p.EnumerateArray();}
    private static SettingsValidationException Bad()=>new("窗口布局请求包含无效、未知、重复或越界字段。");
}







