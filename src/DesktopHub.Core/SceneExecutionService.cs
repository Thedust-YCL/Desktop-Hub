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
        var execution = new SceneExecution { SceneId = scene.Id, SceneName = scene.Name,
            Actions = scene.Actions.Select(action => new ActionExecution { ActionId = action.Id, Name = action.Name }).ToList() };
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(shutdown.IsCancellationRequested, this);
            // Bound queued work without imposing a limit on saved scenes.
            if (executions.Values.Count(item => item.SceneId == sceneId && item.FinishedAt is null) >= 16)
                throw new SettingsValidationException("此场景已有 16 次执行排队，请等待完成。");
            foreach (var old in executions.Values.Where(item => item.FinishedAt is not null).OrderByDescending(item => item.CreatedAt).Skip(29).ToArray())
            { executions.Remove(old.Id); completions.Remove(old.Id); }
            executions.Add(execution.Id, execution);
            if (!sceneGates.TryGetValue(sceneId, out var serial)) sceneGates.Add(sceneId, serial = new(1));
            completions[execution.Id] = Task.Run(() => RunAsync(scene, execution, serial, shutdown.Token));
        }
        Publish();
        lock (gate) return Clone(execution);
    }

    private async Task RunAsync(Scene scene, SceneExecution execution, SemaphoreSlim serial, CancellationToken token)
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
            using var slots = new SemaphoreSlim(8);
            await Task.WhenAll(scene.Actions.Select(async action =>
            {
                var result = results[action.Id];
                ActionOutcome outcome;
                try
                {
                    if (!action.Enabled) outcome = new(false, true, "disabled", "动作已停用");
                    else
                    {
                        foreach (var dependency in scene.Dependencies.Where(edge => edge.ActionId == action.Id))
                        {
                            var previous = await signals[dependency.DependsOnActionId].Task.WaitAsync(token).ConfigureAwait(false);
                            if (!previous.Success) throw new DependencyException("前置动作未成功，已跳过此动作。");
                            if (dependency.Condition != "completed")
                            {
                                Set(() => { result.Status = "Running"; result.Code = "waiting_dependency"; result.Message = "等待前置动作的" + (dependency.Condition == "process" ? "进程" : "窗口"); });
                                var observed = await runner.ObserveAsync(actions[dependency.DependsOnActionId], previous,
                                    dependency.Condition, dependency.TimeoutMs, token).ConfigureAwait(false);
                                if (!observed.Success) throw new TargetValidationException(observed.Code, observed.Message);
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
                            if (skipSession) outcome = new(true, true, "session_repeat_skipped", "本次 Desktop Hub 会话已执行此脚本");
                            else
                            {
                                void Progress(ActionOutcome update)
                                {
                                    if (action.Type == "script" && update.Success && action.RepeatPolicy == "oncePerSession")
                                        lock (gate) sessionScripts.Add(sessionKey);
                                    Set(() => { result.Code = update.Code; result.Message = update.Message; result.ProcessId = update.ProcessId; result.TargetPath = update.TargetPath; result.Diagnostic = update.Diagnostic; });
                                }
                                // Resolve AppEntry from a fresh snapshot at launch time, including queued runs.
                                outcome = await runner.LaunchAsync(action, store.Current, token, Progress).ConfigureAwait(false);
                                if (action.Type == "script" && outcome.Success && action.RepeatPolicy == "oncePerSession") lock (gate) sessionScripts.Add(sessionKey);
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
                    result.Code = outcome.Code; result.Message = outcome.Message; result.ProcessId = outcome.ProcessId;
                    result.TargetPath = outcome.TargetPath; result.SatisfiesDependencies = outcome.Success; result.FinishedAt = DateTimeOffset.UtcNow;
                    result.Diagnostic = outcome.Diagnostic;
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
