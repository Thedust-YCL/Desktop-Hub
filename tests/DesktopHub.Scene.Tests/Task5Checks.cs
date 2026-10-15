using System.Collections.Concurrent;
using DesktopHub.Core;

static class Task5Checks
{
    public static async Task Run(Func<string, Func<Task>, Task> check, string root)
    {
        await check("Simple sequential save derives enabled chain and reorder changes actual submission order", async () =>
        {
            var store = Store(root); var a = Action("A"); var b = Action("disabled") with { Enabled = false }; var c = Action("C");
            var scene = Scene(a, b, c) with { EditorMode = "simple", ExecutionMode = "sequential" };
            var library = new SceneLibraryService(store); library.Save(scene);
            var saved = store.Current.Scenes.Single(); Equal(1, saved.Dependencies.Count); Equal(c.Id, saved.Dependencies[0].ActionId); Equal(a.Id, saved.Dependencies[0].DependsOnActionId);
            var order = new ConcurrentQueue<string>(); var runner = new Runner { Launch = async (action, _, _) => { await Task.Delay(15); order.Enqueue(action.Name); return Ok(); } };
            using var engine = new SceneExecutionService(store, runner); var first = await Finish(engine, engine.Start(scene.Id));
            Equal("A,C", string.Join(",", order)); Equal("disabled", first.Actions.Single(x => x.ActionId == b.Id).Code);
            scene.Actions = [c with { DisplayOrder = 0 }, b with { DisplayOrder = 1 }, a with { DisplayOrder = 2 }]; library.Save(scene);
            order.Clear(); await Finish(engine, engine.Start(scene.Id)); Equal("C,A", string.Join(",", order));
            // Start must also enforce the current mode on stale in-memory payloads.
            scene.ExecutionMode = "simultaneous"; library.Save(scene); Equal(0, store.Current.Scenes.Single().Dependencies.Count);
        });
        await check("Simple simultaneous ignores valid stale order edges and allows independent overlap", async () =>
        {
            var store = Store(root); var a = Action("A"); var b = Action("B");
            var scene = Scene(a, b) with { EditorMode = "simple", Dependencies = [Edge(b, a)] }; new SceneLibraryService(store).Save(scene);
            Equal(0, store.Current.Scenes.Single().Dependencies.Count);
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runner = new Runner { Launch = async (action, _, _) => { if (action.Id == a.Id) await barrier.Task.WaitAsync(TimeSpan.FromSeconds(1)); else barrier.SetResult(); return Ok(); } };
            using var engine = new SceneExecutionService(store, runner); Equal("Success", (await Finish(engine, engine.Start(scene.Id))).Status);
        });
        await check("Simple rejects scripts and advanced scheduling without erasing malformed or cyclic graphs", () =>
        {
            foreach (var action in new[] { Action("script") with { Type = "script", Target = @"C:\Tests\safe.cmd" }, Action("delay") with { DelayMs = 1 }, Action("wait") with { CompletionCondition = "process" }, Action("path") with { WaitTargetPath = @"C:\Tests\wait.exe" }, Action("title") with { WindowTitle = "match" } })
                Reject(() => SceneValidator.Normalize(Scene(action) with { EditorMode = "simple" }));
            var a = Action("A"); var b = Action("B");
            Reject(() => SceneValidator.Normalize(Scene(a, b) with { EditorMode = "simple", Dependencies = [Edge(b, a), Edge(a, b)] }));
            Reject(() => SceneValidator.Normalize(Scene(a, b) with { EditorMode = "simple", Dependencies = [Edge(b, a) with { Condition = "window" }] }));
            Reject(() => SceneValidator.Normalize(Scene(a) with { EditorMode = "simple", Dependencies = [new() { ActionId = a.Id, DependsOnActionId = Id() }] }));
            return Task.CompletedTask;
        });
        await check("Retry creates a retained snapshot child rather than relaunching successful work", async () =>
        {
            var store = Store(root); var a = Action("ok"); var b = Action("failed"); var scene = Scene(a, b); new SceneLibraryService(store).Save(scene);
            var fail = true; var runner = new Runner { Launch = (action, _, _) => Task.FromResult(action.Id == b.Id && fail ? Fail() : Ok()) };
            using var engine = new SceneExecutionService(store, runner); var first = await Finish(engine, engine.Start(scene.Id)); fail = false;
            True(first.CanRetry); var second = await Finish(engine, engine.Retry(first.Id));
            Equal("Success", second.Status); True(!second.CanRetry); True(second.Actions[0].CarriedForward); True(!second.Actions[1].CarriedForward);
            Equal(1, runner.Calls.Count(id => id == a.Id)); Equal(2, runner.Calls.Count(id => id == b.Id));
        });
        await check("Retry freezes deleted scene actions, dependencies, names, arguments and layout and keeps parent immutable", async () =>
        {
            var store = Store(root); var a = Action("ok") with { RepeatPolicy = "always" }; var b = Action("failed") with { Arguments = "--original" }; var c = Action("child");
            var scene = Scene(a, b, c) with { Dependencies = [Edge(b, a), Edge(c, b)], WindowLayout = new() { Windows = [new() { AppEntryId = Id(), Bounds = new() { Width = 300, Height = 200 } }] } };
            new SceneLibraryService(store).Save(scene); var fail = true;
            var runner = new Runner { Launch = (action, _, _) => { if (action.Id == b.Id) { Equal("--original", action.Arguments); return Task.FromResult(fail ? Fail() : Ok()); } return Task.FromResult(Ok()); } };
            using var engine = new SceneExecutionService(store, runner); var first = await Finish(engine, engine.Start(scene.Id));
            var originalJson = System.Text.Json.JsonSerializer.Serialize(first); new SceneLibraryService(store).Remove(scene.Id); fail = false;
            var frozen = engine.GetExecutionScene(first.Id); frozen.Actions[1].Arguments = "--mutated"; frozen.Dependencies.Clear(); frozen.WindowLayout!.Windows[0].Bounds.Width = 1;
            Equal(300d, engine.GetExecutionScene(first.Id).WindowLayout!.Windows[0].Bounds.Width);
            var second = await Finish(engine, engine.Retry(first.Id)); Equal("Success", second.Status); Equal(first.Id, second.RetryOfExecutionId);
            Equal(1, runner.Calls.Count(id => id == a.Id)); Equal(2, runner.Calls.Count(id => id == b.Id)); Equal(1, runner.Calls.Count(id => id == c.Id));
            Equal(originalJson, System.Text.Json.JsonSerializer.Serialize(engine.Snapshot().Single(x => x.Id == first.Id)));
            Equal("--original", engine.GetExecutionScene(second.Id).Actions[1].Arguments); Equal(2, engine.GetExecutionScene(second.Id).Dependencies.Count);
        });
        await check("Retry resolves a stable app reference against repaired current library", async () =>
        {
            var store = Store(root); var app = new AppEntry { Id = Id(), Name = "repair", Target = @"C:\Tests\before.exe" }; new AppLibraryService(store).Add(app);
            var action = Action("ref") with { AppEntryId = app.Id, Target = "" }; var scene = Scene(action); new SceneLibraryService(store).Save(scene);
            var runner = new Runner { Launch = (_, settings, _) => Task.FromResult(settings.AppEntries.Single().Arguments == "--repaired" ? Ok() : Fail()) };
            using var engine = new SceneExecutionService(store, runner); var first = await Finish(engine, engine.Start(scene.Id));
            new AppLibraryService(store).Update(app.Id, "repair", "--repaired", "", false, false);
            Equal("Success", (await Finish(engine, engine.Retry(first.Id))).Status); Equal(2, runner.Calls.Count);
        });
        await check("Retry reobserves carried skipped process or window identity without relaunching the prerequisite", async () =>
        {
            foreach (var condition in new[] { "process", "window" })
            foreach (var skipped in new[] { false, true })
            {
                var store = Store(root); var a = Action("already") with { WaitTargetPath = @"C:\Tests\wait.exe", WindowTitle = "original" }; var b = Action("next"); var c = Action("child");
                var scene = Scene(a, b, c) with { Dependencies = [Edge(b, a) with { Condition = condition }, Edge(c, b)] }; new SceneLibraryService(store).Save(scene);
                var alive = true; var fail = true; var observed = 0;
                var runner = new Runner { Launch = (action, _, _) => Task.FromResult(action.Id == a.Id ? Ok() with { Skipped = skipped, Code = skipped ? "already_running" : "submitted", ProcessId = 876, TargetPath = @"C:\Tests\identity.exe", Diagnostic = "original identity" } : fail ? Fail() : Ok()), Observe = (action, prior, actualCondition, _) => {
                    Equal(a.Id, action.Id); Equal(@"C:\Tests\wait.exe", action.WaitTargetPath); Equal("original", action.WindowTitle); Equal(condition, actualCondition);
                    Equal(876, prior.ProcessId); Equal(@"C:\Tests\identity.exe", prior.TargetPath); Equal("original identity", prior.Diagnostic); True(prior.Success); Equal(skipped, prior.Skipped); observed++;
                    return Task.FromResult(alive ? prior : prior with { Success = false, Code = "wait_" + condition + "_timeout" }); } };
                using var engine = new SceneExecutionService(store, runner); var first = await Finish(engine, engine.Start(scene.Id)); fail = false; alive = false;
                var second = await Finish(engine, engine.Retry(first.Id)); Equal("Failed", second.Actions[1].Status); Equal("dependency_failed", second.Actions[2].Code); Equal(1, runner.Calls.Count(id => id == a.Id)); Equal(2, observed);
                alive = true; var third = await Finish(engine, engine.Retry(second.Id)); Equal("Success", third.Status); Equal(1, runner.Calls.Count(id => id == a.Id)); Equal(3, observed);
            }
        });
        await check("Retry completed historical prerequisites do not require process observations", async () =>
        {
            var store = Store(root); var a = Action("ok"); var b = Action("bad"); var scene = Scene(a, b) with { Dependencies = [Edge(b, a)] }; new SceneLibraryService(store).Save(scene); var fail = true;
            var runner = new Runner { Launch = (action, _, _) => Task.FromResult(action.Id == b.Id && fail ? Fail() : Ok()), Observe = (_, _, _, _) => throw new Exception("completed edge cannot observe") };
            using var engine = new SceneExecutionService(store, runner); var first = await Finish(engine, engine.Start(scene.Id)); fail = false; Equal("Success", (await Finish(engine, engine.Retry(first.Id))).Status); Equal(1, runner.Calls.Count(id => id == a.Id));
        });
        await check("Submitted failed oncePerSession scripts require exact single-attempt permission across retry chains", async () =>
        {
            var store = Store(root); var script = Action("script") with { Type = "script", Target = @"C:\Tests\safe.cmd", RepeatPolicy = "oncePerSession" }; var child = Action("dependent"); var scene = Scene(script, child) with { Dependencies = [Edge(child, script)] }; new SceneLibraryService(store).Save(scene);
            var runner = new Runner { Launch = (action, _, progress) => { if (action.Id != script.Id) return Task.FromResult(Ok()); progress(Ok() with { TargetPath = script.Target }); return Task.FromResult(new ActionOutcome(false, false, "script_timeout", "Timed out")); } };
            using var engine = new SceneExecutionService(store, runner); var first = await Finish(engine, engine.Start(scene.Id)); True(first.Actions[0].LaunchSubmitted); True(first.Actions[0].RequiresScriptRepeat);
            var denied = await Finish(engine, engine.Retry(first.Id)); Equal(1, runner.Calls.Count); Equal("Failed", denied.Actions[0].Status); True(!denied.Actions[0].SatisfiesDependencies); Equal("dependency_failed", denied.Actions[1].Code); True(denied.Actions[0].LaunchSubmitted);
            Reject(() => engine.RetryWithScriptRepeat(denied.Id, [])); Reject(() => engine.RetryWithScriptRepeat(denied.Id, [script.Id, script.Id])); Reject(() => engine.RetryWithScriptRepeat(denied.Id, [child.Id])); Reject(() => engine.RetryWithScriptRepeat(denied.Id, [Id()]));
            var explicitRun = await Finish(engine, engine.RetryWithScriptRepeat(denied.Id, [script.Id])); Equal(2, runner.Calls.Count); Equal("Failed", explicitRun.Actions[0].Status);
            var deniedAgain = await Finish(engine, engine.Retry(explicitRun.Id)); Equal(2, runner.Calls.Count); True(deniedAgain.Actions[0].RequiresScriptRepeat);
        });
        await check("Missing before submission script retries normally while explicit permission retains runner live-script guard", async () =>
        {
            var store = Store(root); var script = Action("script") with { Type = "script", Target = @"C:\Tests\safe.ps1", RepeatPolicy = "oncePerSession" }; var scene = Scene(script); new SceneLibraryService(store).Save(scene); var attempt = 0;
            var runner = new Runner { Launch = (_, _, progress) => { attempt++; if (attempt == 1) return Task.FromResult(Fail()); if (attempt == 2) { progress(Ok()); return Task.FromResult(Fail() with { Code = "script_exit_error" }); } return Task.FromResult(Fail() with { Code = "previous_script_running" }); } };
            using var engine = new SceneExecutionService(store, runner); var first = await Finish(engine, engine.Start(scene.Id)); True(!first.Actions[0].LaunchSubmitted);
            Reject(() => engine.RetryWithScriptRepeat(first.Id, [script.Id])); var second = await Finish(engine, engine.Retry(first.Id)); Equal(2, attempt);
            var third = await Finish(engine, engine.RetryWithScriptRepeat(second.Id, [script.Id])); Equal("previous_script_running", third.Actions[0].Code); Equal(3, attempt); True(third.Actions[0].LaunchSubmitted);
        });
        await check("Retry preserves successful scripts and truthful session skips and cannot retry a successful execution", async () =>
        {
            var store = Store(root); var script = Action("script") with { Type = "script", Target = @"C:\Tests\safe.cmd", RepeatPolicy = "oncePerSession" }; var bad = Action("bad"); var scene = Scene(script, bad); new SceneLibraryService(store).Save(scene); var fail = true;
            var runner = new Runner { Launch = (action, _, _) => Task.FromResult(action.Id == bad.Id && fail ? Fail() : Ok()) };
            using var engine = new SceneExecutionService(store, runner); await Finish(engine, engine.Start(scene.Id)); var second = await Finish(engine, engine.Start(scene.Id)); Equal("session_repeat_skipped", second.Actions[0].Code);
            fail = false; var third = await Finish(engine, engine.Retry(second.Id)); Equal("Success", third.Status); Equal("session_repeat_skipped", third.Actions[0].Code); Equal(1, runner.Calls.Count(id => id == script.Id)); Reject(() => engine.Retry(third.Id));
        });
        await check("Retry rejects absent, unfinished, restarted and pruned snapshots and retains only thirty completed records", async () =>
        {
            var store = Store(root); var scene = Scene(Action("bad")); new SceneLibraryService(store).Save(scene);
            var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); var block = true;
            var runner = new Runner { Launch = async (_, _, _) => { if (block) await barrier.Task; return Fail(); } }; using var engine = new SceneExecutionService(store, runner);
            Reject(() => engine.Retry(Id())); var pending = engine.Start(scene.Id); Reject(() => engine.Retry(pending.Id)); barrier.SetResult(); block = false; var first = await Finish(engine, pending);
            using (var restarted = new SceneExecutionService(store, runner)) { Reject(() => restarted.Retry(first.Id)); Reject(() => restarted.GetExecutionScene(first.Id)); }
            for (var i = 0; i < 32; i++) await Finish(engine, engine.Start(scene.Id)); Equal(30, engine.Snapshot().Count); Reject(() => engine.Retry(first.Id)); Reject(() => engine.GetExecutionScene(first.Id));
        });
        await check("A session consumed by another execution cannot manufacture retry success or authorize an unsubmitted script", async () =>
        {
            var store = Store(root); var script = Action("script") with { Type = "script", Target = @"C:\Tests\safe.cmd", RepeatPolicy = "oncePerSession" }; var scene = Scene(script); new SceneLibraryService(store).Save(scene); var fail = true;
            var runner = new Runner { Launch = (_, _, _) => Task.FromResult(fail ? Fail() : Ok()) }; using var engine = new SceneExecutionService(store, runner);
            var missing = await Finish(engine, engine.Start(scene.Id)); fail = false; await Finish(engine, engine.Start(scene.Id));
            var retry = await Finish(engine, engine.Retry(missing.Id)); Equal("Failed", retry.Actions[0].Status); Equal("session_repeat_blocked", retry.Actions[0].Code); True(!retry.Actions[0].SatisfiesDependencies); True(!retry.Actions[0].RequiresScriptRepeat); Equal(2, runner.Calls.Count);
            Reject(() => engine.RetryWithScriptRepeat(retry.Id, [script.Id]));
        });
        await check("Exact script permission succeeds only named candidates and retry diamond children run once", async () =>
        {
            var store = Store(root); var a = Action("script A") with { Type = "script", Target = @"C:\Tests\a.cmd", RepeatPolicy = "oncePerSession" }; var b = Action("script B") with { Type = "script", Target = @"C:\Tests\b.cmd", RepeatPolicy = "oncePerSession" }; var c = Action("child C"); var d = Action("child D"); var e = Action("diamond");
            var scene = Scene(a, b, c, d, e) with { Dependencies = [Edge(c, a), Edge(d, a), Edge(e, c), Edge(e, d)] }; new SceneLibraryService(store).Save(scene); var fail = true;
            var runner = new Runner { Launch = (action, _, progress) => { if (action.Type == "script") { progress(Ok()); return Task.FromResult(fail ? Fail() with { Code = "script_timeout" } : Ok() with { Code = "script_completed" }); } return Task.FromResult(Ok()); } }; using var engine = new SceneExecutionService(store, runner);
            var first = await Finish(engine, engine.Start(scene.Id)); fail = false; var second = await Finish(engine, engine.RetryWithScriptRepeat(first.Id, [a.Id]));
            Equal("PartialSuccess", second.Status); Equal("Success", second.Actions[0].Status); True(!second.Actions[0].RequiresScriptRepeat); Equal("Failed", second.Actions[1].Status); True(second.Actions[1].RequiresScriptRepeat); Equal(2, runner.Calls.Count(id => id == a.Id)); Equal(1, runner.Calls.Count(id => id == b.Id));
            foreach (var child in new[] { c, d, e }) Equal(1, runner.Calls.Count(id => id == child.Id));
            var third = await Finish(engine, engine.RetryWithScriptRepeat(second.Id, [b.Id])); Equal("Success", third.Status); Equal(2, runner.Calls.Count(id => id == a.Id)); Equal(2, runner.Calls.Count(id => id == b.Id));
        });
        await check("Retry shares the sixteen unfinished execution limit and disposed-service boundary", async () =>
        {
            var store = Store(root); var scene = Scene(Action("bad")); new SceneLibraryService(store).Save(scene); var block = false; var barrier = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var runner = new Runner { Launch = async (_, _, _) => { if (block) await barrier.Task; return Fail(); } }; using var engine = new SceneExecutionService(store, runner); var first = await Finish(engine, engine.Start(scene.Id)); block = true;
            var queued = Enumerable.Range(0, 16).Select(_ => engine.Start(scene.Id)).ToArray(); Reject(() => engine.Retry(first.Id)); Reject(() => engine.Start(scene.Id)); barrier.SetResult();
            await Task.WhenAll(queued.Select(x => engine.WaitForCompletionAsync(x.Id))); engine.Dispose();
            try { engine.Retry(first.Id); throw new Exception("Expected disposed rejection"); } catch (ObjectDisposedException) { }
        });
        await check("Explicit Save rejects a missing registered application atomically while removal and reload preserve repairable scenes", () =>
        {
            var store = Store(root); var apps = new AppLibraryService(store); var app = new AppEntry { Id = Id(), Name = "registered", Target = @"C:\Tests\registered.exe" }; apps.Add(app);
            var action = Action("reference") with { AppEntryId = app.Id, Target = "" }; var scene = Scene(action); var library = new SceneLibraryService(store); library.Save(scene);
            var before = File.ReadAllText(store.SettingsPath); var forgedId = Id();
            Reject(() => library.Save(scene with { Name = "must not replace", Actions = [action with { AppEntryId = forgedId }] })); Equal(before, File.ReadAllText(store.SettingsPath)); Equal(app.Id, store.Current.Scenes.Single().Actions.Single().AppEntryId);
            Reject(() => library.Save(Scene(action with { AppEntryId = forgedId }))); Equal(before, File.ReadAllText(store.SettingsPath)); Equal(1, store.Current.Scenes.Count);
            apps.Remove(app.Id); True(store.Current.Scenes.Single().Actions.Single().ReferenceInvalid);
            var restart = new SettingsStore(Path.GetDirectoryName(store.SettingsPath)!); restart.Load(); Equal(1, restart.Current.Scenes.Count); True(restart.Current.Scenes.Single().Actions.Single().ReferenceInvalid); Equal(app.Id, restart.Current.Scenes.Single().Actions.Single().AppEntryId);
            var removedBytes = File.ReadAllText(store.SettingsPath); Reject(() => new SceneLibraryService(restart).Save(restart.Current.Scenes.Single())); Equal(removedBytes, File.ReadAllText(store.SettingsPath));
            apps.Add(app); library.Save(scene); True(!store.Current.Scenes.Single().Actions.Single().ReferenceInvalid);
            return Task.CompletedTask;
        });
        await check("Execution reports the actual failed prerequisite and clears it after successful retry", async () =>
        {
            var store = Store(root); var a = Action("successful first parent"); var b = Action("failed second parent"); var c = Action("blocked child"); var d = Action("descendant");
            var scene = Scene(a, b, c, d) with { Dependencies = [Edge(c, a), Edge(c, b), Edge(d, c)] }; new SceneLibraryService(store).Save(scene); var fail = true;
            var runner = new Runner { Launch = (action, _, _) => Task.FromResult(action.Id == b.Id && fail ? Fail() : Ok()) };
            using var engine = new SceneExecutionService(store, runner); var first = await Finish(engine, engine.Start(scene.Id));
            Equal(b.Id, first.Actions[2].BlockedByActionId); Equal(c.Id, first.Actions[3].BlockedByActionId);
            fail = false; var retried = await Finish(engine, engine.Retry(first.Id)); Equal("Success", retried.Status);
            Equal<string?>(null, retried.Actions[2].BlockedByActionId);
        });
        await check("Execution recovery metadata identifies the original registered app after live scene reassociation", async () =>
        {
            var store = Store(root); var apps = new AppLibraryService(store); var original = new AppEntry { Id = Id(), Name = "original", Target = @"C:\Tests\original.exe" }; var replacement = original with { Id = Id(), Name = "replacement" }; apps.Add(original); apps.Add(replacement);
            var action = Action("registered") with { AppEntryId = original.Id, Target = "" }; var web = Action("web") with { Type = "web", Target = "https://example.test/" }; var scene = Scene(action, web); var library = new SceneLibraryService(store); library.Save(scene);
            using var engine = new SceneExecutionService(store, new Runner { Launch = (_, _, _) => Task.FromResult(Fail()) }); var first = await Finish(engine, engine.Start(scene.Id));
            scene.Actions[0].AppEntryId = replacement.Id; library.Save(scene);
            var metadata = first.Actions.Single(x => x.ActionId == action.Id); Equal("application", metadata.ActionType); Equal(original.Id, metadata.AppEntryId); Equal("web", first.Actions.Single(x => x.ActionId == web.Id).ActionType); Equal<string?>(null, first.Actions.Single(x => x.ActionId == web.Id).AppEntryId);
            var retried = await Finish(engine, engine.Retry(first.Id)); Equal(original.Id, retried.Actions.Single(x => x.ActionId == action.Id).AppEntryId); Equal("application", retried.Actions.Single(x => x.ActionId == action.Id).ActionType);
        });
    }
    static string Id() => Guid.NewGuid().ToString("D");
    static SettingsStore Store(string root) { var store = new SettingsStore(Path.Combine(root, Id())); store.Load(); return store; }
    static SceneAction Action(string name) => new() { Id = Id(), Name = name, Target = @"C:\Tests\A.exe" };
    static Scene Scene(params SceneAction[] actions) => new() { Id = Id(), Name = "Task 5", EditorMode = "advanced", Actions = actions.Select((a, i) => a with { DisplayOrder = i }).ToList() };
    static ActionDependency Edge(SceneAction child, SceneAction parent) => new() { ActionId = child.Id, DependsOnActionId = parent.Id };
    static ActionOutcome Ok() => new(true, false, "submitted", "Submitted", 123, @"C:\Tests\A.exe", "launch receipt");
    static ActionOutcome Fail() => new(false, false, "target_missing", "Missing target");
    static async Task<SceneExecution> Finish(SceneExecutionService engine, SceneExecution run) { await engine.WaitForCompletionAsync(run.Id).WaitAsync(TimeSpan.FromSeconds(5)); return engine.Snapshot().Single(item => item.Id == run.Id); }
    static void Equal<T>(T expected, T actual) { if (!Equals(expected, actual)) throw new Exception($"Expected {expected}; got {actual}"); }
    static void True(bool value) { if (!value) throw new Exception("Expected true"); }
    static void Reject(System.Action action) { try { action(); } catch (SettingsValidationException) { return; } throw new Exception("Expected validation rejection"); }
    sealed class Runner : ISceneActionRunner
    {
        public ConcurrentBag<string> Calls { get; } = [];
        public Func<SceneAction, AppSettings, System.Action<ActionOutcome>, Task<ActionOutcome>> Launch { get; init; } = (_, _, _) => Task.FromResult(Ok());
        public Func<SceneAction, ActionOutcome, string, int, Task<ActionOutcome>> Observe { get; init; } = (_, outcome, _, _) => Task.FromResult(outcome);
        public Task<ActionOutcome> LaunchAsync(SceneAction action, AppSettings settings, CancellationToken cancellation, System.Action<ActionOutcome> progress) { Calls.Add(action.Id); return Launch(action, settings, progress); }
        public Task<ActionOutcome> ObserveAsync(SceneAction action, ActionOutcome outcome, string condition, int timeoutMs, CancellationToken cancellation) => Observe(action, outcome, condition, timeoutMs);
    }
}
