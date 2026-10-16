using System.Text.Json;

namespace DesktopHub.Core;

public sealed record ActionOutcome(bool Success, bool Skipped, string Code, string Message,
    int? ProcessId = null, string TargetPath = "", string Diagnostic = "");

public interface ISceneActionRunner
{
    Task<ActionOutcome> LaunchAsync(SceneAction action, AppSettings settings, CancellationToken cancellation,
        Action<ActionOutcome> progress);
    Task<ActionOutcome> ObserveAsync(SceneAction action, ActionOutcome launched, string condition,
        int timeoutMs, CancellationToken cancellation);
}

/// <summary>Owns immutable execution snapshots, explicit DAG scheduling, and per-scene serialization.</summary>
public sealed class SceneExecutionService(SettingsStore store, ISceneActionRunner runner) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, SceneExecution> executions = [];
    private readonly Dictionary<string, Task> completions = [];
    private readonly Dictionary<string, Scene> executionScenes = [];
    private readonly Dictionary<string, SemaphoreSlim> sceneGates = [];
    private readonly HashSet<string> sessionScripts = [];
    private readonly CancellationTokenSource shutdown = new();
    public event Action? Changed;

    public IReadOnlyList<SceneExecution> Snapshot()
    {
        lock (gate) return executions.Values.OrderBy(execution => execution.CreatedAt).Select(Clone).ToArray();
    }
    public Task WaitForCompletionAsync(string id)
    {
        lock (gate) return completions.TryGetValue(id, out var task) ? task : Task.CompletedTask;
    }
    public SceneExecution Start(string sceneId)
    {
        var scene = store.Current.Scenes.FirstOrDefault(item => item.Id == sceneId)
            ?? throw new SettingsValidationException("场景不存在，可能已删除。");
        if (!scene.Enabled) throw new SettingsValidationException("场景已停用，请在编辑器启用。");
        scene = SceneValidator.Normalize(scene);
        SceneExecution execution;
        lock (gate) execution = Enqueue(scene, null, []);
        Publish();
        lock (gate) return Clone(execution);
    }

    /// <summary>Returns an isolated copy of the retained execution's original scene, including layout.</summary>
    public Scene GetExecutionScene(string executionId)
    {
        lock (gate) return SceneValidator.Clone(RetainedScene(executionId));
    }

    public SceneExecution Retry(string executionId) => RetryCore(executionId, []);

    /// <summary>Grants one attempt only for exact submitted, unsuccessful session-only scripts.</summary>
    public SceneExecution RetryWithScriptRepeat(string executionId, IReadOnlyList<string> actionIds)
    {
        if (actionIds is null || actionIds.Count == 0 || actionIds.Distinct(StringComparer.Ordinal).Count() != actionIds.Count ||
            actionIds.Any(id => !Guid.TryParseExact(id, "D", out var value) || value == Guid.Empty))
            throw new SettingsValidationException("请选择本次执行中需要明确重复的脚本，不能重复或为空。");
        return RetryCore(executionId, actionIds);
    }

    private SceneExecution RetryCore(string executionId, IReadOnlyList<string> authorizedScripts)
    {
        SceneExecution execution;
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(shutdown.IsCancellationRequested, this);
            var scene = RetainedScene(executionId);
            var previous = executions[executionId];
            if (previous.FinishedAt is null) throw new SettingsValidationException("请等待本次执行结束后再重试。");
            if (!previous.Actions.Any(IsRetryCandidate)) throw new SettingsValidationException("本次执行没有可重试的失败动作。");
            if (authorizedScripts.Any(id => !scene.Actions.Any(action => action.Id == id && NeedsScriptRepeat(action, previous.Actions.Single(result => result.ActionId == id)))))
                throw new SettingsValidationException("重复授权只能包含本次已提交且失败的会话脚本。");
            execution = Enqueue(scene, previous, authorizedScripts.ToHashSet(StringComparer.Ordinal));
        }
        Publish();
        lock (gate) return Clone(execution);
    }

    private Scene RetainedScene(string id)
    {
        if (id is null || !executionScenes.TryGetValue(id, out var scene))
            throw new SettingsValidationException("本次会话的执行快照不存在或已清理，请重新运行场景。");
        return scene;
    }

    // Caller holds gate; copied results and scene never share mutable state with the parent.
    private SceneExecution Enqueue(Scene snapshot, SceneExecution? previous, HashSet<string> authorizedScripts)
    {
        ObjectDisposedException.ThrowIf(shutdown.IsCancellationRequested, this);
        if (executions.Values.Count(item => item.SceneId == snapshot.Id && item.FinishedAt is null) >= 16)
            throw new SettingsValidationException("此场景已有 16 次执行排队，请等待完成。");
        var scene = SceneValidator.Clone(snapshot);
        var execution = new SceneExecution { SceneId = scene.Id, SceneName = scene.Name, RetryOfExecutionId = previous?.Id,
            Actions = scene.Actions.Select(action =>
            {
                if (previous is null) return new ActionExecution { ActionId = action.Id, Name = action.Name, ActionType = action.Type, AppEntryId = action.AppEntryId };
                var prior = previous.Actions.Single(result => result.ActionId == action.Id);
                return IsRetryCandidate(prior)
                    ? prior with { Status = "Pending", Code = "pending", Message = "等待重试失败项", StartedAt = null, FinishedAt = null,
                        SatisfiesDependencies = false, BlockedByActionId = null, CarriedForward = false, RequiresScriptRepeat = NeedsScriptRepeat(action, prior) }
                    : prior with { CarriedForward = true };
            }).ToList() };
        executions.Add(execution.Id, execution);
        executionScenes.Add(execution.Id, scene);
        if (!sceneGates.TryGetValue(scene.Id, out var serial)) sceneGates.Add(scene.Id, serial = new(1));
        completions[execution.Id] = Task.Run(() => RunAsync(scene, execution, serial, authorizedScripts, shutdown.Token));
        return execution;
    }

    private static bool IsRetryCandidate(ActionExecution result) => result.Status == "Failed" || result.Status == "Skipped" && !result.SatisfiesDependencies && result.Code == "dependency_failed";
    private static bool NeedsScriptRepeat(SceneAction action, ActionExecution result) => action.Type == "script" && action.RepeatPolicy == "oncePerSession" && result.LaunchSubmitted && IsRetryCandidate(result);
    private static ActionOutcome Outcome(ActionExecution result) => new(result.SatisfiesDependencies, result.Status == "Skipped", result.Code, result.Message, result.ProcessId, result.TargetPath, result.Diagnostic);

    private async Task RunAsync(Scene scene, SceneExecution execution, SemaphoreSlim serial, HashSet<string> authorizedScripts, CancellationToken token)
    {
        var acquired = false;
        try
        {
            await serial.WaitAsync(token).ConfigureAwait(false); acquired = true;
            Set(() => { execution.Status = "Running"; execution.Summary = "正在执行"; });
            var results = execution.Actions.ToDictionary(item => item.ActionId);
            var actions = scene.Actions.ToDictionary(item => item.Id);
            // Build completions before starting any worker; every Action ID has exactly one worker.
            var signals = scene.Actions.ToDictionary(item => item.Id, _ => new TaskCompletionSource<ActionOutcome>(TaskCreationOptions.RunContinuationsAsynchronously));
            foreach (var result in execution.Actions.Where(result => result.CarriedForward)) signals[result.ActionId].SetResult(Outcome(result));
            using var slots = new SemaphoreSlim(8);
            await Task.WhenAll(scene.Actions.Select(async action =>
            {
                var result = results[action.Id];
                if (result.CarriedForward) return;
                ActionOutcome outcome;
                try
                {
                    if (!action.Enabled) outcome = new(false, true, "disabled", "动作已停用");
                    else
                    {
                        foreach (var dependency in scene.Dependencies.Where(edge => edge.ActionId == action.Id))
                        {
                            var previous = await signals[dependency.DependsOnActionId].Task.WaitAsync(token).ConfigureAwait(false);
                            if (!previous.Success)
                            {
                                Set(() => result.BlockedByActionId = dependency.DependsOnActionId);
                                throw new DependencyException("前置动作未成功，已跳过此动作。");
                            }
                            if (dependency.Condition != "completed")
                            {
                                Set(() => { result.Status = "Running"; result.Code = "waiting_dependency"; result.Message = "等待前置动作的" + (dependency.Condition == "process" ? "进程" : "窗口"); });
                                var observed = await runner.ObserveAsync(actions[dependency.DependsOnActionId], previous,
                                    dependency.Condition, dependency.TimeoutMs, token).ConfigureAwait(false);
                                if (!observed.Success)
                                {
                                    Set(() => result.BlockedByActionId = dependency.DependsOnActionId);
                                    throw new TargetValidationException(observed.Code, observed.Message);
                                }
                            }
                        }
                        Set(() => { result.Status = "Running"; result.StartedAt = DateTimeOffset.UtcNow; result.Code = "delay"; result.Message = action.DelayMs > 0 ? $"延迟 {action.DelayMs} 毫秒" : "准备执行"; });
                        if (action.DelayMs > 0) await Task.Delay(action.DelayMs, token).ConfigureAwait(false);
                        await slots.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            var sessionKey = scene.Id + "/" + action.Id;
                            bool skipSession;
                            lock (gate) skipSession = action.Type == "script" && action.RepeatPolicy == "oncePerSession" && sessionScripts.Contains(sessionKey);
                            if (execution.RetryOfExecutionId is not null && result.RequiresScriptRepeat && !authorizedScripts.Contains(action.Id))
                                outcome = new(false, false, "script_repeat_confirmation_required", "此脚本先前已提交；请在工作台明确确认重复，之前的终端可能仍在运行。", result.ProcessId, result.TargetPath, result.Diagnostic);
                            else if (execution.RetryOfExecutionId is not null && skipSession && !authorizedScripts.Contains(action.Id))
                                outcome = new(false, false, "session_repeat_blocked", "另一次执行已使用此脚本的会话次数；本次失败没有启动回执，不能授权重复，请检查之前的结果或在新会话中运行。");
                            else if (skipSession && !authorizedScripts.Contains(action.Id)) outcome = new(true, true, "session_repeat_skipped", "本次 Desktop Hub 会话已执行此脚本");
                            else
                            {
                                void Receipt(ActionOutcome update)
                                {
                                    if (!update.Success || update.Skipped) return;
                                    lock (gate)
                                    {
                                        result.LaunchSubmitted = true;
                                        if (action.Type == "script" && action.RepeatPolicy == "oncePerSession") sessionScripts.Add(sessionKey);
                                    }
                                }
                                void Progress(ActionOutcome update)
                                {
                                    Receipt(update);
                                    Set(() => { result.Code = update.Code; result.Message = update.Message; result.ProcessId = update.ProcessId; result.TargetPath = update.TargetPath; result.Diagnostic = update.Diagnostic; });
                                }
                                // Resolve AppEntry from a fresh snapshot at launch time, including queued runs.
                                outcome = await runner.LaunchAsync(action, store.Current, token, Progress).ConfigureAwait(false);
                                Receipt(outcome);
                                if (outcome.Success && action.CompletionCondition != "submitted")
                                {
                                    Progress(outcome with { Code = "waiting_" + action.CompletionCondition, Message = (outcome.Skipped ? "已运行，" : "启动请求已提交，") + "等待" + (action.CompletionCondition == "process" ? "进程出现" : "窗口出现") });
                                    var observed = await runner.ObserveAsync(action, outcome, action.CompletionCondition, action.TimeoutMs, token).ConfigureAwait(false);
                                    outcome = observed with { Skipped = observed.Success && outcome.Skipped };
                                }
                            }
                        }
                        finally { slots.Release(); }
                    }
                }
                catch (DependencyException ex) { outcome = new(false, true, ex.Code, ex.Message); }
                catch (TargetValidationException ex) { outcome = new(false, false, ex.Code, ex.Message); }
                catch (OperationCanceledException) { outcome = new(false, false, "cancelled", "执行已取消"); }
                catch (Exception ex) { outcome = new(false, false, "action_error", ex.Message); }
                Set(() =>
                {
                    result.Status = outcome.Skipped ? "Skipped" : outcome.Success ? "Success" : "Failed";
                    result.Code = outcome.Code; result.Message = outcome.Message; result.ProcessId = outcome.ProcessId ?? (result.LaunchSubmitted ? result.ProcessId : null);
                    result.TargetPath = outcome.TargetPath.Length > 0 ? outcome.TargetPath : result.LaunchSubmitted ? result.TargetPath : "";
                    result.SatisfiesDependencies = outcome.Success; result.FinishedAt = DateTimeOffset.UtcNow;
                    result.Diagnostic = outcome.Diagnostic;
                    result.RequiresScriptRepeat = NeedsScriptRepeat(action, result);
                });
                signals[action.Id].TrySetResult(outcome);
            })).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            Set(() => { foreach (var action in execution.Actions.Where(action => action.Status is "Pending" or "Running")) { action.Status = "Failed"; action.Code = "cancelled"; action.Message = "执行已取消"; } });
        }
        finally
        {
            Set(() =>
            {
                var failures = execution.Actions.Count(item => item.Status == "Failed" || item.Status == "Skipped" && !item.SatisfiesDependencies && item.Code != "disabled");
                var successful = execution.Actions.Count(item => item.SatisfiesDependencies);
                execution.Status = failures == 0 ? "Success" : successful > 0 ? "PartialSuccess" : "Failed";
                execution.Summary = execution.Status switch { "Success" => "执行成功", "PartialSuccess" => "部分成功，请查看失败原因", _ => "执行失败，请查看原因" };
                execution.FinishedAt = DateTimeOffset.UtcNow;
                execution.CanRetry = execution.Actions.Any(IsRetryCandidate);
                // Prune snapshots, results and completion handles together, including
                // when a long queue drains without another Start call.
                foreach (var old in executions.Values.Where(item => item.FinishedAt is not null).OrderByDescending(item => item.CreatedAt).Skip(30).ToArray())
                { executions.Remove(old.Id); completions.Remove(old.Id); executionScenes.Remove(old.Id); }
            });
            Diagnose(execution);
            if (acquired) serial.Release();
        }
    }
    private void Set(Action change) { lock (gate) change(); Publish(); }
    private void Publish() { try { Changed?.Invoke(); } catch { /* A surface cannot stop scheduling. */ } }
    private static SceneExecution Clone(SceneExecution execution) => execution with { Actions = execution.Actions.Select(action => action with { }).ToList() };
    private void Diagnose(SceneExecution execution)
    {
        try
        {
            lock (gate)
            {
                var file = Path.Combine(Path.GetDirectoryName(store.SettingsPath)!, "scene-executions.jsonl");
                if (File.Exists(file) && new FileInfo(file).Length > 2 * 1024 * 1024) File.Move(file, file + ".previous", true);
                File.AppendAllText(file, JsonSerializer.Serialize(execution, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + Environment.NewLine);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public void Dispose() => shutdown.Cancel();
    private sealed class DependencyException(string message, string code = "dependency_failed") : Exception(message) { public string Code { get; } = code; }
}
