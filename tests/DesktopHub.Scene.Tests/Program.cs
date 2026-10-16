using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using DesktopHub.Core;

var root = Path.Combine(AppContext.BaseDirectory, "scene-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0; var failed = 0;
try
{
    await Task5Checks.Run(Check, root);
    await Check("Five stable app references persist, isolate snapshots, and read updated entries", async () =>
    {
        var store = Store(); var library = new AppLibraryService(store);
        var apps = Enumerable.Range(0, 5).Select(i => new AppEntry { Id = Id(), Name = "App " + i, Target = @"C:\Tests\App" + i + ".exe" }).ToArray();
        foreach (var app in apps) library.Add(app);
        var scene = Scene(apps.Select(app => Action(app.Name) with { AppEntryId = app.Id, Target = "" }).ToArray());
        new SceneLibraryService(store).Save(scene);
        var restart = new SettingsStore(Path.GetDirectoryName(store.SettingsPath)!); restart.Load();
        Equal(5, restart.Current.Scenes[0].Actions.Count);
        var snapshot = restart.Current; snapshot.Scenes[0].Actions[0].Name = "mutated";
        Equal(apps[0].Name, restart.Current.Scenes[0].Actions[0].Name);
        library.Update(apps[0].Id, "Updated", "--new", "", false, false);
        var runner = new FakeRunner { Launch = (action, settings) => { Equal("Updated", settings.AppEntries.Single(app => app.Id == apps[0].Id).Name); return Task.FromResult(Ok()); } };
        using var engine = new SceneExecutionService(store, runner);
        await Run(engine, scene.Id); Equal(5, runner.Calls.Count);
    });
    await Check("Deletion preserves scenes and marks broken references; re-association repairs them", () =>
    {
        var store = Store(); var app = new AppEntry { Id = Id(), Name = "A", Target = @"C:\A.exe" }; var apps = new AppLibraryService(store); apps.Add(app);
        var scene = Scene(Action("ref") with { AppEntryId = app.Id, Target = "" }); var library = new SceneLibraryService(store); library.Save(scene);
        apps.Remove(app.Id); Equal(1, store.Current.Scenes.Count); True(store.Current.Scenes[0].Actions[0].ReferenceInvalid);
        var replacement = app with { Id = Id() }; apps.Add(replacement); scene.Actions[0].AppEntryId = replacement.Id; library.Save(scene);
        True(!store.Current.Scenes[0].Actions[0].ReferenceInvalid); return Task.CompletedTask;
    });
    await Check("Explicit cycles and invalid references cannot overwrite a saved graph", () =>
    {
        var store = Store(); var a = Action("A"); var b = Action("B"); var scene = Scene(a, b); var library = new SceneLibraryService(store); library.Save(scene);
        var before = File.ReadAllText(store.SettingsPath);
        scene.Dependencies = [Edge(b, a), Edge(a, b)]; Throws(() => library.Save(scene)); Equal(before, File.ReadAllText(store.SettingsPath));
        scene.Dependencies = [new() { ActionId = a.Id, DependsOnActionId = Id() }]; Throws(() => library.Save(scene)); return Task.CompletedTask;
    });
    await Check("A corrupt scene is quarantined without losing the app library or valid scenes", () =>
    {
        var store = Store(); var settings = new AppSettings { AppEntries = [new() { Id = Id(), Name = "Kept", Target = @"C:\Kept.exe" }], Scenes = [Scene(Action("valid"))] };
        store.Save(settings);
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(store.SettingsPath))!.AsObject();
        node["scenes"]!.AsArray().Add(System.Text.Json.Nodes.JsonNode.Parse("{\"id\":\"invalid\",\"actions\":[null]}"));
        File.WriteAllText(store.SettingsPath, node.ToJsonString()); var restart = new SettingsStore(Path.GetDirectoryName(store.SettingsPath)!);
        restart.Load(); Equal(1, restart.Current.AppEntries.Count); Equal(1, restart.Current.Scenes.Count); True(restart.RecoveryWarning is not null);
        True(Directory.GetFiles(Path.GetDirectoryName(store.SettingsPath)!, "settings.scene-recovery-*.json").Length == 1); return Task.CompletedTask;
    });
    await Check("Independent actions overlap, explicit dependencies wait, and display order is irrelevant", async () =>
    {
        var store = Store(); var a = Action("A"); var b = Action("B"); var c = Action("C"); var scene = Scene(c, a, b); scene.Dependencies = [Edge(c, a)]; new SceneLibraryService(store).Save(scene);
        var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var aDone = false;
        var runner = new FakeRunner { Launch = async (action, _) => { if (action.Name == "A") { await barrier.Task.WaitAsync(TimeSpan.FromSeconds(2)); aDone = true; } else if (action.Name == "B") barrier.SetResult(); else True(aDone); return Ok(); } };
        using var engine = new SceneExecutionService(store, runner); var result = await Run(engine, scene.Id); Equal("Success", result.Status); Equal(3, runner.Calls.Count);
    });
    await Check("Failure skips its transitive dependents while independent work continues", async () =>
    {
        var store = Store(); var a = Action("fail"); var b = Action("B"); var c = Action("C"); var d = Action("independent"); var scene = Scene(a, b, c, d); scene.Dependencies = [Edge(b, a), Edge(c, b)]; new SceneLibraryService(store).Save(scene);
        var runner = new FakeRunner { Launch = (action, _) => Task.FromResult(action.Name == "fail" ? new ActionOutcome(false, false, "missing", "missing") : Ok()) };
        using var engine = new SceneExecutionService(store, runner); var result = await Run(engine, scene.Id); Equal("PartialSuccess", result.Status); Equal(2, result.Actions.Count(action => action.Status == "Skipped")); Equal(2, runner.Calls.Count);
    });
    await Check("Fixed delay is measured from satisfied prerequisites", async () =>
    {
        var store = Store(); var scene = Scene(Action("delayed") with { DelayMs = 180 }); new SceneLibraryService(store).Save(scene);
        var watch = Stopwatch.StartNew(); var elapsed = 0L; var runner = new FakeRunner { Launch = (_, _) => { elapsed = watch.ElapsedMilliseconds; return Task.FromResult(Ok()); } };
        using var engine = new SceneExecutionService(store, runner); await Run(engine, scene.Id); True(elapsed >= 170);
    });
    await Check("Skipped already-running app satisfies process dependencies", async () =>
    {
        var store = Store(); var a = Action("running"); var b = Action("next"); var scene = Scene(a, b); scene.Dependencies = [Edge(b, a) with { Condition = "process" }]; new SceneLibraryService(store).Save(scene);
        var runner = new FakeRunner { Launch = (action, _) => Task.FromResult(Ok() with { Skipped = action.Id == a.Id }), Observe = (_, outcome, condition, timeout) => { Equal("process", condition); Equal(10000, timeout); return Task.FromResult(outcome); } };
        using var engine = new SceneExecutionService(store, runner); var result = await Run(engine, scene.Id); Equal("Success", result.Status); Equal("Skipped", result.Actions[0].Status); Equal("Success", result.Actions[1].Status);
    });
    await Check("Window timeout is Failed, and downstream actions skip", async () =>
    {
        var store = Store(); var a = Action("window") with { CompletionCondition = "window" }; var b = Action("next"); var scene = Scene(a, b); scene.Dependencies = [Edge(b, a)]; new SceneLibraryService(store).Save(scene);
        var runner = new FakeRunner { Observe = (_, outcome, condition, timeout) => { Equal(10000, timeout); return Task.FromResult(outcome with { Success = false, Code = "wait_window_timeout", Message = "10 seconds" }); } };
        using var engine = new SceneExecutionService(store, runner); var result = await Run(engine, scene.Id); Equal("Failed", result.Actions[0].Status); Equal("Skipped", result.Actions[1].Status); Equal("Failed", result.Status);
    });
    await Check("Dependency observation timeout fails the waiting action and skips its downstream", async () =>
    {
        var store = Store(); var a = Action("A"); var b = Action("B"); var c = Action("C"); var scene = Scene(a, b, c); scene.Dependencies = [Edge(b, a) with { Condition = "window" }, Edge(c, b)]; new SceneLibraryService(store).Save(scene);
        var runner = new FakeRunner { Observe = (_, outcome, _, _) => Task.FromResult(outcome with { Success = false, Code = "wait_window_timeout" }) };
        using var engine = new SceneExecutionService(store, runner); var result = await Run(engine, scene.Id); Equal("Failed", result.Actions[1].Status); Equal("Skipped", result.Actions[2].Status); Equal(1, runner.Calls.Count);
    });
    await Check("Concurrent same-scene executions are isolated and do not overlap scripts", async () =>
    {
        var store = Store(); var scene = Scene(Action("script") with { Type = "script", Target = @"C:\Tests\safe.cmd" }); new SceneLibraryService(store).Save(scene);
        var active = 0; var peak = 0; var runner = new FakeRunner { Launch = async (_, _) => { var count = Interlocked.Increment(ref active); peak = Math.Max(peak, count); await Task.Delay(50); Interlocked.Decrement(ref active); return Ok(); } };
        using var engine = new SceneExecutionService(store, runner); var runs = Enumerable.Range(0, 8).Select(_ => engine.Start(scene.Id)).ToArray();
        await Task.WhenAll(runs.Select(run => engine.WaitForCompletionAsync(run.Id))); Equal(8, runner.Calls.Count); Equal(1, peak); Equal(8, runs.Select(run => run.Id).Distinct().Count()); True(engine.Snapshot().All(run => run.Status == "Success"));
    });
    await Check("Each action runs once in a diamond dependency graph; new executions rerun scripts", async () =>
    {
        var store = Store(); var a = Action("script") with { Type = "script", Target = @"C:\Tests\safe.ps1" }; var b = Action("B"); var c = Action("C"); var d = Action("D"); var scene = Scene(a, b, c, d); scene.Dependencies = [Edge(b, a), Edge(c, a), Edge(d, b), Edge(d, c)]; new SceneLibraryService(store).Save(scene);
        var runner = new FakeRunner(); using var engine = new SceneExecutionService(store, runner); await Run(engine, scene.Id); await Run(engine, scene.Id); Equal(8, runner.Calls.Count); Equal(2, runner.Calls.Count(id => id == a.Id));
    });
    await Check("Script session repeat rule can be configured per action", async () =>
    {
        var store = Store(); var scene = Scene(Action("script") with { Type = "script", Target = @"C:\Tests\safe.cmd", RepeatPolicy = "oncePerSession" }); new SceneLibraryService(store).Save(scene);
        var runner = new FakeRunner(); using var engine = new SceneExecutionService(store, runner); await Run(engine, scene.Id); var next = await Run(engine, scene.Id); Equal(1, runner.Calls.Count); Equal("Skipped", next.Actions[0].Status);
    });
    await Check("Disabled predecessors propagate skip without launching dependents", async () =>
    {
        var store = Store(); var a = Action("disabled") with { Enabled = false }; var b = Action("B"); var scene = Scene(a, b); scene.Dependencies = [Edge(b, a)]; new SceneLibraryService(store).Save(scene);
        var runner = new FakeRunner(); using var engine = new SceneExecutionService(store, runner); var result = await Run(engine, scene.Id); Equal(0, runner.Calls.Count); Equal("Skipped", result.Actions[1].Status);
    });
    await Check("Shutdown cancellation ends long delays and queued scene instances", async () =>
    {
        var store = Store(); var scene = Scene(Action("delayed") with { DelayMs = 600000 }); new SceneLibraryService(store).Save(scene);
        var runner = new FakeRunner(); using var engine = new SceneExecutionService(store, runner); var first = engine.Start(scene.Id); var queued = engine.Start(scene.Id);
        await Task.Delay(30); engine.Dispose(); await Task.WhenAll(engine.WaitForCompletionAsync(first.Id), engine.WaitForCompletionAsync(queued.Id)).WaitAsync(TimeSpan.FromSeconds(2));
        Equal(0, runner.Calls.Count); True(engine.Snapshot().All(run => run.Status == "Failed"));
    });
    await Check("Settings accept only legal web URLs and explicitly typed safe script targets", () =>
    {
        foreach (var url in new[] { "file:///C:/a.txt", "javascript:alert(1)", "https://" }) Throws(() => SceneValidator.Normalize(Scene(Action("url") with { Type = "web", Target = url })));
        Throws(() => SceneValidator.Normalize(Scene(Action("script") with { Type = "script", Target = @"C:\Tests\bad.vbs" })));
        Throws(() => SceneValidator.Normalize(Scene(Action("file") with { Type = "file", Target = @"C:\Tests\bad.cmd" })));
        Throws(() => SceneValidator.Normalize(Scene(Action("script") with { Type = "script", Target = @"C:\Tests\a.ps1", Arguments = "unsafe raw command" })));
        return Task.CompletedTask;
    });
    await Check("Scene CRUD and independent pin layouts have no low scene-count limit", () =>
    {
        var store = Store(); var library = new SceneLibraryService(store); var saved = Enumerable.Range(0, 40).Select(i => Scene(Action("A")) with { Name = "Scene " + i }).ToArray();
        foreach (var scene in saved) library.Save(scene); Equal(40, store.Current.Scenes.Count); var apps = new AppLibraryService(store); apps.SetPin("mini", "scene", saved[0].Id, true); apps.SetPin("dock", "scene", saved[1].Id, true);
        library.Reorder(saved.Reverse().Select(scene => scene.Id).ToArray()); Equal(saved[^1].Id, store.Current.Scenes[0].Id); library.Remove(saved[0].Id); Equal(39, store.Current.Scenes.Count); Equal(1, store.Current.LauncherPins.Count); return Task.CompletedTask;
    });
}
finally { Directory.Delete(root, true); }
Console.WriteLine($"Scene scheduler/storage: {passed} passed, {failed} failed. Runner is controlled; this is not Windows launch acceptance.");
return failed == 0 ? 0 : 1;

SettingsStore Store() { var store = new SettingsStore(Path.Combine(root, Id())); store.Load(); return store; }
static string Id() => Guid.NewGuid().ToString("D");
static SceneAction Action(string name) => new() { Id = Id(), Name = name, Type = "application", Target = @"C:\Tests\A.exe" };
static Scene Scene(params SceneAction[] actions) => new() { Id = Id(), Name = "Test scene", EditorMode = "advanced", Actions = actions.Select((action, order) => action with { DisplayOrder = order }).ToList() };
static ActionDependency Edge(SceneAction child, SceneAction parent) => new() { ActionId = child.Id, DependsOnActionId = parent.Id };
static ActionOutcome Ok() => new(true, false, "submitted", "Submitted", 123, @"C:\Tests\A.exe");
static async Task<SceneExecution> Run(SceneExecutionService engine, string sceneId) { var run = engine.Start(sceneId); await engine.WaitForCompletionAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(5)); return engine.Snapshot().Single(item => item.Id == run.Id); }
async Task Check(string name, Func<Task> test) { try { await test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); } }
static void True(bool value) { if (!value) throw new Exception("Expected true"); }
static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
static void Throws(Action test) { try { test(); } catch (SettingsValidationException) { return; } catch (TargetValidationException) { return; } throw new Exception("Expected validation error"); }

sealed class FakeRunner : ISceneActionRunner
{
    public ConcurrentBag<string> Calls { get; } = [];
    public Func<SceneAction, AppSettings, Task<ActionOutcome>> Launch { get; init; } = (_, _) => Task.FromResult(new ActionOutcome(true, false, "submitted", "Submitted", 123, @"C:\Tests\A.exe"));
    public Func<SceneAction, ActionOutcome, string, int, Task<ActionOutcome>> Observe { get; init; } = (_, outcome, _, _) => Task.FromResult(outcome);
    public Task<ActionOutcome> LaunchAsync(SceneAction action, AppSettings settings, CancellationToken token, Action<ActionOutcome> progress) { Calls.Add(action.Id); return Launch(action, settings); }
    public Task<ActionOutcome> ObserveAsync(SceneAction action, ActionOutcome outcome, string condition, int timeout, CancellationToken token) => Observe(action, outcome, condition, timeout);
}
