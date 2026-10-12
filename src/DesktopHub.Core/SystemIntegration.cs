namespace DesktopHub.Core;

/// <summary>Durable intent is saved before Shell changes. Never infer the initial desktop state.</summary>
public sealed record DesktopIconSettings
{
    public string? ExitPolicy { get; set; }
    public bool FirstHideAuthorized { get; set; }
    public bool? InitialVisible { get; set; }
    public bool? LastSuccessfulVisible { get; set; }
    public bool? LastObservedVisible { get; set; }
    public bool NeedsRecovery { get; set; }
    public bool Conflict { get; set; }
    public bool OperationInProgress { get; set; }
    public bool? PendingVisible { get; set; }
    public DateTimeOffset? LastChangedUtc { get; set; }
}

public sealed record DesktopIconBackendResult(bool Success, bool? Visible, string Code = "ok", string Message = "");
public interface IDesktopIconBackend
{
    DesktopIconBackendResult Query();
    DesktopIconBackendResult SetVisible(bool visible);
}
public sealed record DesktopIconOperationResult(bool Success, string Code, string Message, bool? Visible,
    bool Supported, bool FirstHideAuthorized, string? ExitPolicy, bool Conflict, bool NeedsRecovery, string RecoveryPath);

/// <summary>Testable policy layer. Only explicitly requested operations write Shell state.</summary>
public class DesktopIconVisibilityController(SettingsStore store, IDesktopIconBackend backend)
{
    public const string NativeRecoveryPath = "桌面空白处右键 → 查看 → 显示桌面图标（菜单文字随 Windows 系统语言变化）。";
    private readonly object _gate = new();
    private bool _observedConflict;

    public DesktopIconOperationResult Query()
    {
        lock (_gate)
        {
            var observed = Read();
            var metadata = store.Current.DesktopIcons;
            var conflict = HasConflict(metadata, observed.Visible);
            _observedConflict |= conflict;
            if (metadata.FirstHideAuthorized && observed.Visible.HasValue &&
                (metadata.LastObservedVisible != observed.Visible || conflict && !metadata.Conflict))
            {
                try
                {
                    store.Update(s => s with { DesktopIcons = s.DesktopIcons with
                        { LastObservedVisible = observed.Visible, Conflict = conflict || s.DesktopIcons.Conflict } });
                    metadata = store.Current.DesktopIcons;
                }
                catch (Exception ex) when (IsPersistenceFailure(ex))
                { observed = observed with { Message = "图标核对元数据未能保存；本次进程仍保留冲突保护：" + ex.Message }; }
            }
            return Result(observed.Success, conflict ? "external_change" : observed.Code,
                conflict ? "检测到应用外的桌面图标变更，自动恢复已暂停；请明确选择显示或隐藏。" : observed.Message,
                observed.Visible, metadata, conflict);
        }
    }

    public DesktopIconOperationResult ReconcileOnStartup()
    {
        // No Shell writes: even after a crash the user's external change wins.
        var result = Query();
        return result.Success && result.NeedsRecovery && result.ExitPolicy == "restore" && !result.Conflict
            ? result with { Code = "recovery_pending", Message = "存在未结束的图标操作记录。已核对当前状态；请在设置中选择显示/恢复，启动不会自动改变图标。" }
            : result;
    }

    public DesktopIconOperationResult SetExitPolicy(string policy)
    {
        lock (_gate)
        {
            if (policy is not ("restore" or "keepHidden")) return Result(false, "invalid_policy", "退出策略无效。", null, store.Current.DesktopIcons);
            try { store.Update(s => s with { DesktopIcons = s.DesktopIcons with { ExitPolicy = policy } }); }
            catch (Exception ex) when (IsPersistenceFailure(ex)) { return Result(false, "settings_write_failed", ex.Message, null, store.Current.DesktopIcons); }
            return Query();
        }
    }

    public DesktopIconOperationResult SetVisible(bool visible, string? firstHidePolicy = null)
    {
        lock (_gate)
        {
            var metadata = store.Current.DesktopIcons;
            if (!visible && !metadata.FirstHideAuthorized && firstHidePolicy is not ("restore" or "keepHidden"))
                return Result(false, "authorization_required", "隐藏前请确认不会删除文件并选择完整退出策略。" + NativeRecoveryPath, null, metadata);
            if (firstHidePolicy is not null && firstHidePolicy is not ("restore" or "keepHidden"))
                return Result(false, "invalid_policy", "退出策略无效。", null, metadata);
            var before = Read();
            if (!before.Success || before.Visible is null) return Result(false, before.Code, before.Message, null, metadata);
            var initial = metadata.NeedsRecovery && metadata.InitialVisible.HasValue ? metadata.InitialVisible : before.Visible;
            var intent = metadata with
            {
                FirstHideAuthorized = metadata.FirstHideAuthorized || !visible,
                ExitPolicy = !visible && firstHidePolicy is not null ? firstHidePolicy : metadata.ExitPolicy,
                InitialVisible = initial,
                LastObservedVisible = before.Visible,
                PendingVisible = visible,
                OperationInProgress = before.Visible != visible,
                // A restore/show attempt must not discharge the existing recovery duty before
                // Shell's actual state is verified. Complete is the only successful discharge.
                NeedsRecovery = metadata.NeedsRecovery || !visible && initial == true,
                Conflict = false
            };
            // If this transaction fails, do not touch the desktop.
            try { store.Update(s => s with { DesktopIcons = intent }); }
            catch (Exception ex) when (IsPersistenceFailure(ex)) { return Result(false, "settings_write_failed", ex.Message, before.Visible, metadata); }
            _observedConflict = false; // An explicit target-state operation is the only conflict acknowledgement.
            if (before.Visible == visible) return Complete(intent, visible, "already_set", "桌面图标已处于目标状态。");
            DesktopIconBackendResult applied;
            try { applied = backend.SetVisible(visible); }
            catch (Exception ex) { applied = new(false, null, "shell_unavailable", "Windows Shell 操作失败：" + ex.Message); }
            // Re-read regardless of HRESULT; success means the actual state matches the requested target.
            var after = Read();
            if (after.Success && after.Visible == visible)
                return Complete(intent, visible, "changed", visible ? "桌面图标已显示。" : "桌面图标已隐藏，文件未被更改。");
            if (after.Success && after.Visible == before.Visible)
            {
                // A failed call with a verified unchanged state is our failed operation, not evidence
                // of an external edit. Preserve the previous recovery duty and allow a safe retry.
                var unchanged = intent with { OperationInProgress = metadata.OperationInProgress, PendingVisible = metadata.PendingVisible,
                    NeedsRecovery = metadata.NeedsRecovery, LastSuccessfulVisible = metadata.LastSuccessfulVisible,
                    LastObservedVisible = after.Visible };
                try { store.Update(s => s with { DesktopIcons = unchanged }); }
                catch (Exception ex) when (IsPersistenceFailure(ex))
                { return Result(false, "metadata_commit_failed", "系统状态未改变，但无法保存失败记录；保留操作前恢复意图。" + ex.Message + " " + NativeRecoveryPath, after.Visible, store.Current.DesktopIcons); }
            }
            return Result(false, applied.Success ? "verification_failed" : applied.Code,
                (string.IsNullOrEmpty(applied.Message) ? "无法确认 Windows 桌面图标变更，保留恢复记录。" : applied.Message) + " " + NativeRecoveryPath,
                after.Visible, store.Current.DesktopIcons);
        }
    }

    public DesktopIconOperationResult RestoreForExit(bool sessionEnding = false)
    {
        lock (_gate)
        {
            // Query also latches an observed external change durably. Exit may be the first time
            // we see it, and that evidence must survive even if the user toggles again later.
            var observed = Query();
            var metadata = store.Current.DesktopIcons;
            if (!metadata.NeedsRecovery || metadata.ExitPolicy != "restore" || !metadata.FirstHideAuthorized)
                return Result(observed.Success, "no_restore_required", "退出策略无需恢复图标。", observed.Visible, metadata, HasConflict(metadata, observed.Visible));
            if (sessionEnding)
                return Result(false, "session_ending", "Windows 正在注销或关机；保留恢复记录，避免在 Shell 关闭中修改状态。" + NativeRecoveryPath, observed.Visible, metadata);
            if (!observed.Success || observed.Visible is null || metadata.InitialVisible is null)
                return Result(false, "state_unknown", "当前状态未知，未执行自动恢复。" + NativeRecoveryPath, observed.Visible, metadata);
            if (HasConflict(metadata, observed.Visible))
                return Result(false, "external_change", "用户在应用外修改过图标状态，退出时不会覆盖该设置。" + NativeRecoveryPath, observed.Visible, metadata, true);
            return SetVisible(metadata.InitialVisible.Value);
        }
    }

    private DesktopIconOperationResult Complete(DesktopIconSettings intent, bool visible, string code, string message)
    {
        var completed = intent with { LastSuccessfulVisible = visible, LastObservedVisible = visible,
            OperationInProgress = false, PendingVisible = null, NeedsRecovery = !visible && intent.InitialVisible == true,
            LastChangedUtc = DateTimeOffset.UtcNow, Conflict = false };
        try { store.Update(s => s with { DesktopIcons = completed }); }
        catch (Exception ex) when (IsPersistenceFailure(ex))
        { return Result(false, "metadata_commit_failed", "系统状态已改变，但保存完成记录失败；已保留操作前恢复意图。" + ex.Message, visible, intent); }
        return Result(true, code, message, visible, completed);
    }

    private DesktopIconBackendResult Read()
    {
        try
        {
            var result = backend.Query();
            return result.Success && result.Visible is null ? result with { Success = false, Code = "state_unknown", Message = "当前桌面图标状态未知。" } : result;
        }
        catch (Exception ex) { return new(false, null, "shell_unavailable", "Windows Shell 当前不可用：" + ex.Message); }
    }
    private bool HasConflict(DesktopIconSettings metadata, bool? visible)
    {
        if (!metadata.NeedsRecovery || visible is null) return metadata.Conflict || _observedConflict;
        var expected = metadata.OperationInProgress ? metadata.PendingVisible : metadata.LastSuccessfulVisible;
        return metadata.Conflict || _observedConflict || expected.HasValue && visible != expected;
    }
    private static DesktopIconOperationResult Result(bool success, string code, string message, bool? visible,
        DesktopIconSettings metadata, bool conflict = false) => new(success, code, message, visible, visible.HasValue,
            metadata.FirstHideAuthorized, metadata.ExitPolicy, conflict, metadata.NeedsRecovery, NativeRecoveryPath);
    private static bool IsPersistenceFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or SettingsValidationException;
}

public sealed record StartupBackendResult(bool Success, string? Command, string Code = "ok", string Message = "");
public interface IStartupRegistrationBackend
{
    StartupBackendResult Query();
    StartupBackendResult CompareExchange(string? expectedCommand, string? replacementCommand);
}
public sealed record StartupRegistrationResult(bool Success, string Code, string Message, bool Enabled,
    string? RegisteredCommand, string ExpectedCommand, bool Owned, bool NeedsRepair);

/// <summary>Only an exact command recorded before our write proves ownership; foreign entries are never deleted.</summary>
public class StartupRegistrationController(SettingsStore store, IStartupRegistrationBackend backend, string executablePath)
{
    private readonly object _gate = new();
    public string ExpectedCommand { get; } = QuoteExecutable(executablePath) + " --startup";
    public StartupRegistrationResult Query()
    {
        lock (_gate) { return Build(Read()); }
    }
    public StartupRegistrationResult SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            var state = Read();
            if (!state.Success) return Build(state);
            var settings = store.Current;
            var owned = IsOwned(state.Command, settings);
            if (state.Command is not null && !owned)
                return Build(state, false, "startup_entry_conflict", "同名启动项没有 Desktop Hub 的所有权记录，未覆盖或删除。");
            if (enabled && state.Command == ExpectedCommand)
            {
                try { store.Update(s => s with { StartWithWindows = true, StartupCommand = ExpectedCommand, StartupPendingCommand = null }); }
                catch (Exception ex) when (IsPersistenceFailure(ex)) { return Build(state, false, "settings_write_failed", ex.Message); }
                return Query();
            }
            // Persist consent and the precise prospective command before changing HKCU.
            if (enabled && ExpectedCommand.Length > 260)
                return Build(state, false, "startup_path_too_long", "HKCU Run 命令超过 Windows 官方 260 字符上限，请将便携目录移到更短路径。");
            try { store.Update(s => s with { StartWithWindows = enabled, StartupPendingCommand = enabled ? ExpectedCommand : s.StartupPendingCommand }); }
            catch (Exception ex) when (IsPersistenceFailure(ex)) { return Build(state, false, "settings_write_failed", ex.Message); }
            StartupBackendResult applied;
            try { applied = backend.CompareExchange(state.Command, enabled ? ExpectedCommand : null); }
            catch (Exception ex) { applied = new(false, null, "registry_unavailable", ex.Message); }
            if (!applied.Success)
            {
                var actual = Read();
                return Build(actual, operationSuccess: false, operationCode: applied.Code, operationMessage: applied.Message);
            }
            var final = Read();
            if (!final.Success || final.Command != (enabled ? ExpectedCommand : null))
                return Build(final, false, "verification_failed", "启动项变更后的系统状态未通过核对。");
            try { store.Update(s => s with { StartupCommand = enabled ? ExpectedCommand : null, StartupPendingCommand = null }); }
            catch (Exception ex) when (IsPersistenceFailure(ex)) { return Build(final, false, "metadata_commit_failed", "启动项已变更，但配置保存失败：" + ex.Message); }
            return Build(final with { Code = enabled ? "registered" : "removed", Message = enabled ? "已注册当前用户开机自启；登录后安静常驻。" : "已禁用当前用户开机自启。" });
        }
    }
    private StartupBackendResult Read()
    {
        try { return backend.Query(); }
        catch (Exception ex) { return new(false, null, "registry_unavailable", ex.Message); }
    }
    private StartupRegistrationResult Build(StartupBackendResult state, bool? operationSuccess = null,
        string? operationCode = null, string? operationMessage = null)
    {
        var settings = store.Current;
        var owned = IsOwned(state.Command, settings);
        var enabled = state.Success && state.Command == ExpectedCommand;
        var needsRepair = state.Success && (settings.StartWithWindows != enabled || state.Command is not null && !owned || owned && state.Command != ExpectedCommand);
        var code = operationCode ?? (needsRepair && state.Code == "ok" ? "startup_mismatch" : state.Code);
        var message = operationMessage ?? (needsRepair && string.IsNullOrEmpty(state.Message)
            ? "应用设置与当前用户启动项不一致；便携版移动后请重新开启修复路径。" : state.Message);
        return new(operationSuccess ?? state.Success, code, message,
            enabled, state.Command, ExpectedCommand, owned, needsRepair);
    }
    private static string QuoteExecutable(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.Any(char.IsControl) || path.Contains('"') || !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("启动项必须使用完整的本地 EXE 路径。", nameof(path));
        return "\"" + Path.GetFullPath(path) + "\"";
    }
    private static bool IsOwned(string? command, AppSettings settings) => command is not null &&
        (command == settings.StartupCommand || command == settings.StartupPendingCommand);
    private static bool IsPersistenceFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or SettingsValidationException;
}
