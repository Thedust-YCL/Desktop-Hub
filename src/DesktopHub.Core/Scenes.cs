namespace DesktopHub.Core;

public sealed record Scene
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "scenes";
    public int DisplayOrder { get; set; }
    public bool Enabled { get; set; } = true;
    public string EditorMode { get; set; } = "simple";
    public string ExecutionMode { get; set; } = "simultaneous";
    public SceneWindowLayout? WindowLayout { get; set; }
    public List<SceneAction> Actions { get; set; } = [];
    public List<ActionDependency> Dependencies { get; set; } = [];
}

public sealed record SceneAction
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Type { get; set; } = "application";
    public string? AppEntryId { get; set; }
    public bool ReferenceInvalid { get; set; }
    public string Target { get; set; } = "";
    public string Arguments { get; set; } = "";
    // Script arguments are individual tokens, never an interpolated shell command.
    public List<string> ArgumentList { get; set; } = [];
    public string WorkingDirectory { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public int DisplayOrder { get; set; }
    public int DelayMs { get; set; }
    public int TimeoutMs { get; set; } = 10000;
    public string CompletionCondition { get; set; } = "submitted";
    public string WaitTargetPath { get; set; } = "";
    public string WindowTitle { get; set; } = "";
    public string RepeatPolicy { get; set; } = "default";
    public bool RunAsAdministrator { get; set; }
}

// Advanced ordering is presentation only; simple sequential mode derives completed edges.
public sealed record ActionDependency
{
    public string ActionId { get; set; } = "";
    public string DependsOnActionId { get; set; } = "";
    public string Condition { get; set; } = "completed";
    public int TimeoutMs { get; set; } = 10000;
}

public sealed record ActionExecution
{
    public string ActionId { get; init; } = "";
    public string Name { get; init; } = "";
    // Stable recovery identity from the frozen action; never resolve through an edited live scene.
    public string ActionType { get; init; } = "";
    public string? AppEntryId { get; init; }
    public string Status { get; set; } = "Pending";
    public string Code { get; set; } = "pending";
    public string Message { get; set; } = "等待执行";
    public int? ProcessId { get; set; }
    public string TargetPath { get; set; } = "";
    public string Diagnostic { get; set; } = "";
    public bool SatisfiesDependencies { get; set; }
    // Immediate frozen prerequisite that failed or could no longer be observed.
    public string? BlockedByActionId { get; set; }
    // A successful non-skipped launch receipt, retained even if later observation fails.
    public bool LaunchSubmitted { get; set; }
    public bool CarriedForward { get; set; }
    public bool RequiresScriptRepeat { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed record SceneExecution
{
    public string Id { get; init; } = Guid.NewGuid().ToString("D");
    public string SceneId { get; init; } = "";
    public string SceneName { get; init; } = "";
    public string? RetryOfExecutionId { get; init; }
    public bool CanRetry { get; set; }
    public string Status { get; set; } = "Pending";
    public string Summary { get; set; } = "等待前一次场景执行完成";
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
    public List<ActionExecution> Actions { get; init; } = [];
}

public static class SceneValidator
{
    public static Scene Clone(Scene scene) => scene with
    {
        Actions = scene.Actions.Select(action => action with { ArgumentList = [.. action.ArgumentList] }).ToList(),
        Dependencies = scene.Dependencies.Select(edge => edge with { }).ToList(),
        WindowLayout = scene.WindowLayout is { } layout ? layout with { Windows = layout.Windows.Select(window => window with { Bounds = window.Bounds with { } }).ToList() } : null
    };

    public static Scene Normalize(Scene scene)
    {
        if (scene is null) throw new SettingsValidationException("场景不能为空。");
        Id(scene.Id); Text(scene.Name, 200, false); Text(scene.Icon, 40, false);
        Choice(scene.EditorMode, "simple", "advanced"); Choice(scene.ExecutionMode, "simultaneous", "sequential");
        if (scene.WindowLayout is { } layout)
        {
            Range(layout.WaitTimeoutMs, 1, 600000);
            if (layout.Windows is { Count: > 32 }) throw new SettingsValidationException("窗口布局最多保存 32 个窗口。");
            if (layout.Windows is null || layout.Windows.Any(window => window is null || window.Bounds is null)) throw new SettingsValidationException("窗口布局包含空记录。");
            foreach (var window in layout.Windows)
            {
                Id(window.AppEntryId); Text(window.TitleContains, 200, true); Text(window.MonitorId, 200, true); Choice(window.State, "normal", "maximized");
                Text(window.ExecutablePath, 32767, true);
                if (window.ExecutablePath.Length > 0) NativeLaunchService.ValidatePath(window.ExecutablePath, checkDrive: false);
                if (!double.IsFinite(window.Dpi) || window.Dpi is < 48 or > 768 || !double.IsFinite(window.Bounds.Left) || !double.IsFinite(window.Bounds.Top) ||
                    !double.IsFinite(window.Bounds.Width) || !double.IsFinite(window.Bounds.Height) || window.Bounds.Width <= 0 || window.Bounds.Height <= 0)
                    throw new SettingsValidationException("窗口布局位置、尺寸或 DPI 无效。");
            }
        }
        if ((scene.Actions ?? []).Any(action => action is null) || (scene.Dependencies ?? []).Any(edge => edge is null))
            throw new SettingsValidationException("动作或依赖不能为空。");
        var copy = Clone(scene with { Actions = (scene.Actions ?? []).Select(action => action with { ArgumentList = action.ArgumentList ?? [] }).ToList(), Dependencies = scene.Dependencies ?? [] });
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var action in copy.Actions)
        {
            Id(action.Id);
            if (!ids.Add(action.Id)) throw new SettingsValidationException("动作 ID 重复。");
            Text(action.Name, 200, false);
            Text(action.Target, 32767, true); Text(action.WaitTargetPath, 32767, true);
            Choice(action.Type, "application", "web", "file", "folder", "script");
            Choice(action.CompletionCondition, "submitted", "process", "window");
            Choice(action.RepeatPolicy, "default", "always", "oncePerSession");
            Range(action.DelayMs, 0, 600000); Range(action.TimeoutMs, 1, 600000);
            Text(action.WindowTitle, 200, true);
            NativeLaunchService.ValidateArguments(action.Arguments);
            NativeLaunchService.ValidateWorkingDirectory(action.WorkingDirectory, checkDrive: false);
            action.ArgumentList ??= [];
            if (action.ArgumentList.Count > 256) throw new SettingsValidationException("脚本参数过多。");
            foreach (var arg in action.ArgumentList) Text(arg, 4096, true);
            if (action.AppEntryId is { } appId)
            {
                Id(appId);
                if (action.Type != "application") throw new SettingsValidationException("只有应用动作可引用应用库。");
                action.Target = ""; action.Arguments = ""; action.WorkingDirectory = "";
            }
            else if (action.Type == "web")
            {
                if (action.Target.Length > 8192 || action.Target.Any(char.IsControl) ||
                    !Uri.TryCreate(action.Target, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
                    throw new SettingsValidationException("网页地址必须是有效的 HTTP / HTTPS URL。");
            }
            else
            {
                action.Target = action.Type == "application"
                    ? NativeLaunchService.ValidateAppPath(action.Target, checkDrive: false)
                    : NativeLaunchService.ValidateLocalPath(action.Target, checkDrive: false);
                if (action.Type == "script" && Path.GetExtension(action.Target).ToLowerInvariant() is not (".bat" or ".cmd" or ".ps1"))
                    throw new SettingsValidationException("请选择 BAT、CMD 或 PowerShell 脚本。");
                if (action.Type == "file" && Path.GetExtension(action.Target).ToLowerInvariant() is ".exe" or ".lnk" or ".bat" or ".cmd" or ".ps1" or ".psm1" or ".vbs" or ".vbe" or ".js" or ".jse" or ".wsf" or ".wsh" or ".msi" or ".scr" or ".com" or ".reg" or ".hta" or ".cpl" or ".pif" or ".url" or ".appref-ms" or ".application" or ".py" or ".pyw" or ".sh" or ".jar")
                    throw new SettingsValidationException("可执行文件请使用应用或脚本动作，不能作为普通文件打开。");
            }
            if (action.WaitTargetPath.Length > 0)
                action.WaitTargetPath = NativeLaunchService.ValidateLocalPath(action.WaitTargetPath, checkDrive: false);
            if (action.RunAsAdministrator && action.Type != "script") throw new SettingsValidationException("提权仅可用于明确添加的脚本。");
            if (action.Type == "script" && action.Arguments.Length > 0) throw new SettingsValidationException("脚本参数必须按单独参数填写。");
        }
        var edges = new HashSet<(string, string)>();
        var predecessors = ids.ToDictionary(id => id, _ => new List<string>());
        foreach (var edge in copy.Dependencies)
        {
            if (edge is null || !ids.Contains(edge.ActionId) || !ids.Contains(edge.DependsOnActionId) || edge.ActionId == edge.DependsOnActionId || !edges.Add((edge.ActionId, edge.DependsOnActionId)))
                throw new SettingsValidationException("依赖引用无效、重复或依赖自身。");
            Choice(edge.Condition, "completed", "process", "window"); Range(edge.TimeoutMs, 1, 600000);
            predecessors[edge.ActionId].Add(edge.DependsOnActionId);
        }
        // Kahn's algorithm avoids recursive stack exhaustion for large user configurations.
        var remaining = predecessors.ToDictionary(pair => pair.Key, pair => pair.Value.Count);
        var successors = ids.ToDictionary(id => id, _ => new List<string>());
        foreach (var edge in copy.Dependencies) successors[edge.DependsOnActionId].Add(edge.ActionId);
        var ready = new Queue<string>(remaining.Where(pair => pair.Value == 0).Select(pair => pair.Key));
        var visited = 0;
        while (ready.TryDequeue(out var id)) { visited++; foreach (var next in successors[id]) if (--remaining[next] == 0) ready.Enqueue(next); }
        if (visited != ids.Count) throw new SettingsValidationException("动作存在循环依赖，请移除环中的等待关系。");
        // Validate the supplied graph before deriving simple edges. Invalid graphs must
        // never become valid merely because normalization would replace them.
        if (copy.EditorMode == "simple" && (copy.Actions.Any(action => action.Type == "script" || action.DelayMs != 0 ||
            action.CompletionCondition != "submitted" || action.WaitTargetPath.Length != 0 || action.WindowTitle.Length != 0 ||
            action.ArgumentList.Count != 0 || action.RunAsAdministrator) || copy.Dependencies.Any(edge => edge.Condition != "completed")))
            throw new SettingsValidationException("简单模式不支持脚本、自定义等待或延迟，请使用高级模式。");
        copy.Name = copy.Name.Trim();
        copy.Actions = copy.Actions.OrderBy(action => action.DisplayOrder).Select((action, index) => action with { DisplayOrder = index }).ToList();
        if (copy.EditorMode == "simple")
        {
            copy.Dependencies = [];
            if (copy.ExecutionMode == "sequential")
            {
                SceneAction? previous = null;
                foreach (var action in copy.Actions.Where(action => action.Enabled))
                {
                    if (previous is not null) copy.Dependencies.Add(new() { ActionId = action.Id, DependsOnActionId = previous.Id });
                    previous = action;
                }
            }
        }
        return copy;
    }
    private static void Id(string value) { if (!Guid.TryParseExact(value, "D", out var id) || id == Guid.Empty) throw new SettingsValidationException("场景或动作标识无效。"); }
    private static void Text(string? value, int max, bool empty) { if (value is null || value.Length > max || value.Any(char.IsControl) || !empty && string.IsNullOrWhiteSpace(value)) throw new SettingsValidationException("名称或参数无效。"); }
    private static void Choice(string value, params string[] values) { if (!values.Contains(value, StringComparer.Ordinal)) throw new SettingsValidationException("动作选项无效。"); }
    private static void Range(int value, int min, int max) { if (value < min || value > max) throw new SettingsValidationException("延迟或超时必须在允许范围内（最长 10 分钟）。"); }
}

public sealed class SceneLibraryService(SettingsStore store)
{
    public AppSettings Save(Scene requested) => store.Update(settings =>
    {
        var scene = SceneValidator.Normalize(requested);
        foreach (var action in scene.Actions) action.ReferenceInvalid = action.AppEntryId is { } id && settings.AppEntries.All(app => app.Id != id);
        if (scene.Actions.Any(action => action.ReferenceInvalid))
            throw new SettingsValidationException("引用的应用已移除，请重新关联后再保存场景。");
        var index = settings.Scenes.FindIndex(item => item.Id == scene.Id);
        if (index < 0) settings.Scenes.Add(scene with { DisplayOrder = settings.Scenes.Count });
        else settings.Scenes[index] = scene with { DisplayOrder = settings.Scenes[index].DisplayOrder };
        return settings;
    });
    public AppSettings Remove(string id) => store.Update(settings =>
    {
        if (settings.Scenes.RemoveAll(scene => scene.Id == id) == 0) throw new SettingsValidationException("场景不存在。");
        settings.LauncherPins.RemoveAll(pin => pin.ItemType == "scene" && pin.ItemId == id);
        return settings;
    });
    public AppSettings Reorder(IReadOnlyList<string> ids) => store.Update(settings =>
    {
        if (ids.Count != settings.Scenes.Count || ids.Distinct().Count() != ids.Count || ids.Any(id => settings.Scenes.All(scene => scene.Id != id)))
            throw new SettingsValidationException("排序必须包含全部场景且不能重复。");
        settings.Scenes = ids.Select((id, index) => settings.Scenes.Single(scene => scene.Id == id) with { DisplayOrder = index }).ToList();
        return settings;
    });
}
