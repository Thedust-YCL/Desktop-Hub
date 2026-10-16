using System.Text.Json;
using DesktopHub.Core;

var root = Path.Combine(AppContext.BaseDirectory, "m23-system-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var passed = 0;
var failed = 0;
try
{
    Check("Legacy schema 3 migrates to 5 without losing apps, scenes or launcher pins", () =>
    {
        var store = Store("legacy");
        var appId = Guid.NewGuid().ToString("D");
        var sceneId = Guid.NewGuid().ToString("D");
        var old = new AppSettings { SchemaVersion = 3, AppEntries = [new() { Id = appId, Name = "Saved app", Target = @"C:\Apps\saved.exe", WorkingDirectory = @"C:\Apps" }],
            Scenes = [new() { Id = sceneId, Name = "Saved scene" }], LauncherPins = [new() { Id = Guid.NewGuid().ToString("D"), ItemId = appId }] };
        Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
        File.WriteAllText(store.SettingsPath, JsonSerializer.Serialize(old, SettingsStore.JsonOptions));
        var loaded = store.Load();
        Equal(5, loaded.SchemaVersion); Equal(appId, loaded.AppEntries.Single().Id); Equal(sceneId, loaded.Scenes.Single().Id);
        Equal(appId, loaded.LauncherPins.Single().ItemId); True(!loaded.StartWithWindows); True(!loaded.DesktopIcons.FirstHideAuthorized);
        Equal(5, JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(store.SettingsPath), SettingsStore.JsonOptions)!.SchemaVersion);
    });
    Check("System configuration snapshots are deep and invalid changes preserve the saved file", () =>
    {
        var store = Store("settings"); store.Save(new AppSettings { ForegroundBlocklist = ["GAME.exe"], MiniOpacity = 3 });
        Equal(1d, store.Current.MiniOpacity); Equal("game.exe", store.Current.ForegroundBlocklist.Single());
        var snapshot = store.Current; snapshot.DesktopIcons.ExitPolicy = "restore"; snapshot.ForegroundBlocklist.Clear();
        True(store.Current.DesktopIcons.ExitPolicy is null); Equal(1, store.Current.ForegroundBlocklist.Count);
        var before = File.ReadAllText(store.SettingsPath);
        Throws(() => store.Save(store.Current with { ForegroundBlocklist = [@"C:\game.exe"] }));
        Throws(() => store.Save(store.Current with { DockHotkey = "bad", }));
        Throws(() => store.Save(store.Current with { WorkspaceHotkey = store.Current.DockHotkey }));
        Equal(before, File.ReadAllText(store.SettingsPath));
    });
    Check("First hide without a selected exit policy is cancelled before any system write", () =>
    {
        var backend = new Icons(true); var store = Store("cancel"); var service = new DesktopIconVisibilityController(store, backend);
        Equal("authorization_required", service.SetVisible(false).Code); Equal(0, backend.Writes); True(backend.Visible == true);
        True(!store.Current.DesktopIcons.FirstHideAuthorized); True(!File.Exists(store.SettingsPath));
    });
    Check("Hide is idempotent, persists recovery before writes, and restores original visible state", () =>
    {
        var backend = new Icons(true); var store = Store("restore");
        backend.BeforeWrite = () => { var saved = store.Load().DesktopIcons; True(saved.OperationInProgress && saved.NeedsRecovery); True(saved.InitialVisible == true); };
        var service = new DesktopIconVisibilityController(store, backend);
        var hidden = service.SetVisible(false, "restore"); if (!hidden.Success) throw new Exception(hidden.Code + ": " + hidden.Message); backend.BeforeWrite = null;
        var repeated = service.SetVisible(false); if (!repeated.Success) throw new Exception(repeated.Code + ": " + repeated.Message); Equal(1, backend.Writes); True(service.Query().NeedsRecovery);
        var restored = service.RestoreForExit(); if (!restored.Success) throw new Exception(restored.Code + ": " + restored.Message); Equal(2, backend.Writes); True(backend.Visible == true); True(!service.Query().NeedsRecovery);
    });
    Check("Initially hidden desktop is preserved and keepHidden never restores on exit", () =>
    {
        var hidden = new Icons(false); var service = new DesktopIconVisibilityController(Store("already-hidden"), hidden);
        True(service.SetVisible(false, "restore").Success); True(service.RestoreForExit().Success); Equal(0, hidden.Writes); True(hidden.Visible == false);
        var backend = new Icons(true); var keep = new DesktopIconVisibilityController(Store("keep"), backend);
        True(keep.SetVisible(false, "keepHidden").Success); True(keep.RestoreForExit().Success); Equal(1, backend.Writes); True(backend.Visible == false);
    });
    Check("Startup, crash and Explorer reconciliation perform zero writes; external changes are respected", () =>
    {
        var store = Store("crash"); var backend = new Icons(true); var service = new DesktopIconVisibilityController(store, backend);
        True(service.SetVisible(false, "restore").Success);
        var restarted = new DesktopIconVisibilityController(store, backend); Equal("recovery_pending", restarted.ReconcileOnStartup().Code); Equal(1, backend.Writes);
        Equal("session_ending", restarted.RestoreForExit(true).Code); Equal(1, backend.Writes);
        backend.Visible = true; True(restarted.Query().Conflict); Equal("external_change", restarted.RestoreForExit().Code); Equal(1, backend.Writes);
        backend.Visible = false; // An observed conflict remains latched even if the user later hides outside the app.
        // Re-open the actual persisted config, as a new process would.
        var reloadedStore = new SettingsStore(Path.GetDirectoryName(store.SettingsPath)!); reloadedStore.Load();
        restarted = new DesktopIconVisibilityController(reloadedStore, backend);
        Equal("external_change", restarted.RestoreForExit().Code); Equal(1, backend.Writes);
        True(restarted.SetVisible(true).Success); True(!restarted.Query().NeedsRecovery);
    });
    Check("Unknown Shell state cannot be hidden or auto-restored and failure is truthful", () =>
    {
        var store = Store("unknown"); var backend = new Icons(null); var service = new DesktopIconVisibilityController(store, backend);
        True(!service.SetVisible(false, "restore").Success); Equal(0, backend.Writes); True(service.Query().Visible is null);
        backend.Visible = true; backend.FailWrite = true; var result = service.SetVisible(false, "restore");
        True(!result.Success); True(result.Visible == true); True(!store.Current.DesktopIcons.OperationInProgress); True(!service.Query().Conflict);
        True(!store.Current.DesktopIcons.NeedsRecovery);
    });
    Check("A verified failed restore keeps recovery duty and can be retried without a false external conflict", () =>
    {
        var store = Store("restore-failure"); var backend = new Icons(true); var service = new DesktopIconVisibilityController(store, backend);
        True(service.SetVisible(false, "restore").Success);
        backend.BeforeWrite = () => True(store.Current.DesktopIcons.NeedsRecovery);
        backend.FailWrite = true; var failure = service.RestoreForExit(); True(!failure.Success); True(failure.Visible == false);
        True(store.Current.DesktopIcons.NeedsRecovery); True(!store.Current.DesktopIcons.OperationInProgress); True(!service.Query().Conflict);
        backend.BeforeWrite = null; backend.FailWrite = false;
        var restart = new SettingsStore(Path.GetDirectoryName(store.SettingsPath)!); restart.Load();
        True(new DesktopIconVisibilityController(restart, backend).RestoreForExit().Success); True(backend.Visible == true);
        True(!restart.Current.DesktopIcons.NeedsRecovery);
    });
    Check("An unverified restore keeps durable pending recovery rather than falsely discharging it", () =>
    {
        var store = Store("restore-unverified"); var backend = new Icons(true); var service = new DesktopIconVisibilityController(store, backend);
        True(service.SetVisible(false, "restore").Success);
        backend.BeforeWrite = () => backend.Visible = null; backend.FailWrite = true;
        var result = service.RestoreForExit(); True(!result.Success); True(result.Visible is null);
        True(store.Current.DesktopIcons.NeedsRecovery && store.Current.DesktopIcons.OperationInProgress);
        True(store.Current.DesktopIcons.PendingVisible == true);
    });
    Check("A failed restore after an interrupted hide preserves the prior pending hide evidence for retry", () =>
    {
        var store = Store("pending-hide-restore-failure"); store.Save(new() { DesktopIcons = new() { FirstHideAuthorized = true,
            ExitPolicy = "restore", InitialVisible = true, NeedsRecovery = true, OperationInProgress = true, PendingVisible = false } });
        var backend = new Icons(false) { FailWrite = true }; var service = new DesktopIconVisibilityController(store, backend);
        True(!service.RestoreForExit().Success); True(store.Current.DesktopIcons.PendingVisible == false);
        True(store.Current.DesktopIcons.NeedsRecovery); True(!service.Query().Conflict);
        backend.FailWrite = false; True(service.RestoreForExit().Success); True(backend.Visible == true);
    });
    Check("External conflict first observed during exit is durable across another external toggle and restart", () =>
    {
        var store = Store("exit-observed-conflict"); var backend = new Icons(true);
        var service = new DesktopIconVisibilityController(store, backend); True(service.SetVisible(false, "restore").Success);
        backend.Visible = true;
        Equal("external_change", service.RestoreForExit().Code); True(store.Current.DesktopIcons.Conflict); Equal(1, backend.Writes);
        backend.Visible = false;
        var restart = new SettingsStore(Path.GetDirectoryName(store.SettingsPath)!); restart.Load();
        Equal("external_change", new DesktopIconVisibilityController(restart, backend).RestoreForExit().Code);
        True(backend.Visible == false); Equal(1, backend.Writes);
    });
    Check("A failed atomic metadata write prevents desktop state changes", () =>
    {
        var store = Store("locked-icons"); store.Save(new());
        using var locked = new FileStream(store.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var backend = new Icons(true); var service = new DesktopIconVisibilityController(store, backend);
        Equal("settings_write_failed", service.SetVisible(false, "restore").Code); Equal(0, backend.Writes); True(!store.Current.DesktopIcons.FirstHideAuthorized);
    });
    Check("Post-Shell metadata failure reports actual state and retains durable recovery intent", () =>
    {
        var store = Store("commit-icons"); var backend = new Icons(true); FileStream? held = null;
        backend.BeforeWrite = () => held = new FileStream(store.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var service = new DesktopIconVisibilityController(store, backend);
        try
        {
            var result = service.SetVisible(false, "restore"); Equal("metadata_commit_failed", result.Code); True(result.Visible == false);
            True(store.Current.DesktopIcons.OperationInProgress && store.Current.DesktopIcons.NeedsRecovery);
        }
        finally { held?.Dispose(); }
        backend.BeforeWrite = null;
        var restart = new SettingsStore(Path.GetDirectoryName(store.SettingsPath)!); restart.Load();
        True(new DesktopIconVisibilityController(restart, backend).RestoreForExit().Success); True(backend.Visible == true);
    });
    Check("Pending durable hide can be safely restored after crash without assuming a successful commit", () =>
    {
        var store = Store("pending"); store.Save(new() { DesktopIcons = new() { FirstHideAuthorized = true, ExitPolicy = "restore", InitialVisible = true,
            NeedsRecovery = true, OperationInProgress = true, PendingVisible = false } });
        var backend = new Icons(false); var service = new DesktopIconVisibilityController(store, backend);
        Equal("recovery_pending", service.ReconcileOnStartup().Code); Equal(0, backend.Writes);
        True(service.RestoreForExit().Success); True(backend.Visible == true);
    });
    Check("Current-user startup defaults to read only; quotes spaced paths and persists consent before write", () =>
    {
        var store = Store("startup"); var backend = new Startup(); var service = new StartupRegistrationController(store, backend, @"C:\Portable Apps\DesktopHub.exe");
        True(!service.Query().Enabled); Equal(0, backend.Writes); True(!store.Current.StartWithWindows);
        backend.BeforeWrite = () => { True(store.Current.StartWithWindows); Equal(service.ExpectedCommand, store.Current.StartupPendingCommand); };
        True(service.SetEnabled(true).Success); backend.BeforeWrite = null;
        Equal(@"""C:\Portable Apps\DesktopHub.exe"" --startup", backend.Command); True(service.Query().Owned); True(service.Query().Enabled);
        var repeated = service.SetEnabled(true); if (!repeated.Success) throw new Exception(repeated.Code + ": " + repeated.Message); Equal(1, backend.Writes);
        True(service.SetEnabled(false).Success); Equal(2, backend.Writes); True(backend.Command is null); True(store.Current.StartupCommand is null);
    });
    Check("Foreign startup entries, including identical commands without evidence, are never deleted or overwritten", () =>
    {
        var store = Store("foreign"); var backend = new Startup { Command = @"""C:\Other\program.exe""" }; var service = new StartupRegistrationController(store, backend, @"C:\Hub\DesktopHub.exe");
        Equal("startup_entry_conflict", service.SetEnabled(false).Code); Equal("startup_entry_conflict", service.SetEnabled(true).Code); Equal(0, backend.Writes);
        backend.Command = service.ExpectedCommand; Equal("startup_entry_conflict", service.SetEnabled(false).Code); Equal(0, backend.Writes);
    });
    Check("Portable move detects stale owned path and repairs only after explicit enable", () =>
    {
        var store = Store("portable"); var backend = new Startup(); var old = new StartupRegistrationController(store, backend, @"C:\Old Hub\DesktopHub.exe"); True(old.SetEnabled(true).Success);
        var moved = new StartupRegistrationController(store, backend, @"C:\New Hub\DesktopHub.exe"); True(moved.Query().NeedsRepair); True(moved.Query().Owned); Equal(1, backend.Writes);
        True(moved.SetEnabled(true).Success); Equal(moved.ExpectedCommand, backend.Command); True(!moved.Query().NeedsRepair); Equal(2, backend.Writes);
    });
    Check("Failed system startup write reports mismatch and retains both old and pending ownership", () =>
    {
        var store = Store("startup-failed"); var backend = new Startup(); var old = new StartupRegistrationController(store, backend, @"C:\Old\DesktopHub.exe"); True(old.SetEnabled(true).Success);
        backend.FailWrite = true; var moved = new StartupRegistrationController(store, backend, @"C:\New\DesktopHub.exe"); True(!moved.SetEnabled(true).Success);
        Equal(old.ExpectedCommand, backend.Command); True(moved.Query().Owned); True(moved.Query().NeedsRepair);
        backend.FailWrite = false; True(moved.SetEnabled(false).Success); True(backend.Command is null);
    });
    Check("Failed disable reports the still-registered real state", () =>
    {
        var store = Store("failed-disable"); var backend = new Startup();
        var service = new StartupRegistrationController(store, backend, @"C:\Hub\DesktopHub.exe"); True(service.SetEnabled(true).Success);
        backend.FailWrite = true; var failedDisable = service.SetEnabled(false);
        True(!failedDisable.Success); True(failedDisable.Enabled && failedDisable.Owned && failedDisable.NeedsRepair);
        Equal(service.ExpectedCommand, failedDisable.RegisteredCommand);
    });
    Check("Failed disable preserves pending-only ownership after an interrupted successful registration", () =>
    {
        var store = Store("pending-disable-failure"); var backend = new Startup();
        var service = new StartupRegistrationController(store, backend, @"C:\Hub\DesktopHub.exe");
        backend.Command = service.ExpectedCommand;
        store.Save(new AppSettings { StartWithWindows = true, StartupPendingCommand = service.ExpectedCommand });
        backend.FailWrite = true; var failure = service.SetEnabled(false);
        True(!failure.Success); True(failure.Enabled && failure.Owned); Equal(service.ExpectedCommand, store.Current.StartupPendingCommand);
        True(store.Current.StartupCommand is null);
        backend.FailWrite = false;
        var restart = new SettingsStore(Path.GetDirectoryName(store.SettingsPath)!); restart.Load();
        True(new StartupRegistrationController(restart, backend, @"C:\Hub\DesktopHub.exe").SetEnabled(false).Success);
        True(backend.Command is null); True(restart.Current.StartupCommand is null && restart.Current.StartupPendingCommand is null);
    });
    Check("Failed settings persistence prevents registry mutation and long paths are rejected", () =>
    {
        var store = Store("locked-startup"); store.Save(new());
        var backend = new Startup(); var service = new StartupRegistrationController(store, backend, @"C:\Hub\DesktopHub.exe");
        using (var locked = new FileStream(store.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        { Equal("settings_write_failed", service.SetEnabled(true).Code); Equal(0, backend.Writes); }
        var tooLong = new StartupRegistrationController(store, backend, @"C:\" + new string('x', 240) + @"\DesktopHub.exe");
        Equal("startup_path_too_long", tooLong.SetEnabled(true).Code); Equal(0, backend.Writes);
    });
    Check("Post-registry metadata failure retains prospective ownership for a safe explicit disable", () =>
    {
        var store = Store("commit-startup"); var backend = new Startup(); FileStream? held = null;
        var service = new StartupRegistrationController(store, backend, @"C:\Hub\DesktopHub.exe");
        backend.BeforeWrite = () => held = new FileStream(store.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            Equal("metadata_commit_failed", service.SetEnabled(true).Code); Equal(service.ExpectedCommand, backend.Command);
            True(service.Query().Owned); Equal(service.ExpectedCommand, store.Current.StartupPendingCommand);
        }
        finally { held?.Dispose(); }
        backend.BeforeWrite = null;
        var restart = new SettingsStore(Path.GetDirectoryName(store.SettingsPath)!); restart.Load();
        True(new StartupRegistrationController(restart, backend, @"C:\Hub\DesktopHub.exe").SetEnabled(false).Success); True(backend.Command is null);
    });
    Check("An external registry change between query and mutation causes cancellation", () =>
    {
        var store = Store("race"); var backend = new Startup { BeforeWrite = null }; var service = new StartupRegistrationController(store, backend, @"C:\Hub\DesktopHub.exe");
        backend.BeforeWrite = () => backend.Command = "foreign"; True(!service.SetEnabled(true).Success); Equal("foreign", backend.Command); Equal(0, backend.Writes);
    });
}
finally
{
    // root is the unique test-owned child of this suite's output directory.
    if (!Path.GetFullPath(root).StartsWith(Path.GetFullPath(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Refusing cleanup outside test output directory.");
    Directory.Delete(root, recursive: true);
}
Console.WriteLine($"RESULT {passed} passed; {failed} failed; Shell and HKCU writes were mocked.");
return failed == 0 ? 0 : 1;

SettingsStore Store(string name) => new(Path.Combine(root, name));
void Check(string name, Action test) { try { test(); passed++; Console.WriteLine("PASS " + name); } catch (Exception ex) { failed++; Console.WriteLine("FAIL " + name + ": " + ex); } }
void True(bool value) { if (!value) throw new Exception("Assertion failed."); }
void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Exception($"Expected {expected}, got {actual}"); }
void Throws(Action action) { try { action(); } catch (SettingsValidationException) { return; } throw new Exception("Expected validation error"); }

sealed class Icons(bool? visible) : IDesktopIconBackend
{
    public bool? Visible { get; set; } = visible;
    public int Writes { get; private set; }
    public bool FailWrite { get; set; }
    public Action? BeforeWrite { get; set; }
    public DesktopIconBackendResult Query() => new(Visible.HasValue, Visible, Visible.HasValue ? "ok" : "state_unknown", "");
    public DesktopIconBackendResult SetVisible(bool value)
    { BeforeWrite?.Invoke(); Writes++; if (FailWrite) return new(false, Visible, "mock_failure", "Mock Shell failure"); Visible = value; return Query(); }
}
sealed class Startup : IStartupRegistrationBackend
{
    public string? Command { get; set; }
    public int Writes { get; private set; }
    public bool FailWrite { get; set; }
    public Action? BeforeWrite { get; set; }
    public StartupBackendResult Query() => new(true, Command);
    public StartupBackendResult CompareExchange(string? expectedCommand, string? replacementCommand)
    {
        BeforeWrite?.Invoke();
        if (Command != expectedCommand) return new(false, Command, "startup_entry_changed", "External change");
        if (FailWrite) return new(false, Command, "mock_failure", "Mock registry failure");
        Command = replacementCommand; Writes++; return Query();
    }
}
