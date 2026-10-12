using System.Diagnostics;
using System.Text.Json;
using DesktopHub.Core;

// No test framework/NuGet dependency. Launch integration is opt-in and must use the project's inert fixture.
var testRoot = Path.Combine(AppContext.BaseDirectory, "test-data-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(testRoot);
var passed = 0;
var failed = 0;
var launcher = new NativeLaunchService();
try
{
    Check("Missing configuration uses defaults", () =>
    {
        var store = Store("missing");
        var settings = store.Load();
        Equal("system", settings.Theme);
        Equal(500, settings.HoverDelay);
        Equal(4, settings.SchemaVersion);
        Equal(1d, settings.WorkspaceOpacity);
        True(!settings.OnboardingCompleted);
        True(settings.LaunchTarget is null && store.RecoveryWarning is null);
    });
    Check("Camel-case JSON round trip persists theme, geometry and target", () =>
    {
        var store = Store("roundtrip");
        var settings = new AppSettings
        {
            Theme = "light", MiniPosition = "right", ReducedMotion = true, OnboardingCompleted = true, Transparency = false, WorkspaceOpacity = .65,
            WorkspaceBounds = new() { Left = 320, Top = 120, Width = 1260, Height = 800 },
            MiniBounds = new() { Left = 200, Top = 240, Width = 56, Height = 320 },
            DockBounds = new() { Left = 300, Top = 160, Width = 400, Height = 620 },
            LaunchTarget = new() { Id = Guid.NewGuid().ToString("D"), Path = @"C:\Example\Selected.exe", DisplayName = "untrusted label" }
        };
        store.Save(settings);
        var restart = new SettingsStore(Path.GetDirectoryName(store.SettingsPath)!);
        var loaded = restart.Load();
        Equal("light", loaded.Theme);
        Equal(.65, loaded.WorkspaceOpacity);
        True(loaded.OnboardingCompleted);
        Equal(settings.WorkspaceBounds, loaded.WorkspaceBounds);
        Equal(settings.MiniBounds, loaded.MiniBounds);
        Equal(settings.DockBounds, loaded.DockBounds);
        Equal(settings.LaunchTarget.Id, loaded.LaunchTarget!.Id);
        Equal("untrusted label", loaded.LaunchTarget.DisplayName);
        True(!loaded.Transparency && loaded.ReducedMotion);
        var json = File.ReadAllText(store.SettingsPath);
        True(json.Contains("\"schemaVersion\"") && json.Contains("\"workspaceBounds\""));
        True(!json.Contains("\"SchemaVersion\""));
    });
    Check("Workspace opacity covers the full range and recovers non-finite input", () =>
    {
        foreach (var value in new[] { 0d, .01, .65, 1d }) Equal(value, SettingsValidator.Normalize(new AppSettings { WorkspaceOpacity = value }).WorkspaceOpacity);
        Equal(0d, SettingsValidator.Normalize(new AppSettings { WorkspaceOpacity = -1 }).WorkspaceOpacity);
        Equal(1d, SettingsValidator.Normalize(new AppSettings { WorkspaceOpacity = 2 }).WorkspaceOpacity);
        Equal(1d, SettingsValidator.Normalize(new AppSettings { WorkspaceOpacity = double.NaN }).WorkspaceOpacity);
    });
    Check("Corrupted JSON is preserved and safely recovered", () =>
    {
        var store = Store("corrupt");
        Write(store, "{ broken JSON");
        Equal("system", store.Load().Theme);
        True(store.RecoveryWarning is not null);
        True(!File.Exists(store.SettingsPath));
        var backups = Directory.GetFiles(Path.GetDirectoryName(store.SettingsPath)!, "settings.corrupt-*.json");
        Equal(1, backups.Length);
        Equal("{ broken JSON", File.ReadAllText(backups[0]));
        store.Save(store.Current);
        Equal("system", store.Load().Theme);
        True(store.RecoveryWarning is null);
    });
    Check("Unsupported schema and unknown fields are quarantined", () =>
    {
        foreach (var invalidJson in new[] { "{\"schemaVersion\":99}", "{\"theme\":\"dark\",\"shellCommand\":\"cmd.exe\"}", "null", "[]" })
        {
            var store = Store(Guid.NewGuid().ToString("N"));
            Write(store, invalidJson);
            Equal("system", store.Load().Theme);
            True(store.RecoveryWarning is not null);
        }
    });
    Check("Oversized configuration is not deserialized", () =>
    {
        var store = Store("oversized");
        Write(store, new string(' ', SettingsStore.MaximumFileBytes + 1));
        store.Load();
        True(store.RecoveryWarning is not null);
        True(!File.Exists(store.SettingsPath));
    });
    Check("Invalid settings choices cannot replace saved data", () =>
    {
        var store = Store("validation");
        store.Save(new AppSettings { Theme = "dark" });
        var before = File.ReadAllText(store.SettingsPath);
        Throws<SettingsValidationException>(() => store.Save(new AppSettings { Theme = "unknown" }));
        Throws<SettingsValidationException>(() => store.Save(new AppSettings { MiniMode = "unknown" }));
        Equal(before, File.ReadAllText(store.SettingsPath));
        Equal("dark", store.Current.Theme);
    });
    Check("Non-finite and unreasonable bounds are repaired", () =>
    {
        var settings = SettingsValidator.Normalize(new AppSettings
        {
            WorkspaceBounds = new() { Left = double.NaN, Top = double.PositiveInfinity, Width = -10, Height = 1e10 },
            MiniBounds = new() { Left = -1e10, Top = 1e10, Width = double.NaN, Height = -1 },
            HoverDelay = -200
        });
        Equal(100d, settings.WorkspaceBounds.Left);
        Equal(80d, settings.WorkspaceBounds.Top);
        Equal(480d, settings.WorkspaceBounds.Width);
        Equal(4320d, settings.WorkspaceBounds.Height);
        Equal(-32768d, settings.MiniBounds.Left);
        Equal(32768d, settings.MiniBounds.Top);
        Equal(56d, settings.MiniBounds.Width);
        Equal(40d, settings.MiniBounds.Height);
        Equal(100, settings.HoverDelay);
        Equal(3000, SettingsValidator.Normalize(new AppSettings { HoverDelay = int.MaxValue }).HoverDelay);
    });
    Check("Null geometry from JSON safely uses defaults", () =>
    {
        var store = Store("null-geometry");
        Write(store, "{\"workspaceBounds\":null,\"miniBounds\":null,\"dockBounds\":null}");
        Equal(new AppSettings().WorkspaceBounds, store.Load().WorkspaceBounds);
        True(store.RecoveryWarning is null);
    });
    Check("Store snapshots cannot mutate the saved Current", () =>
    {
        var store = Store("snapshots");
        store.Save(new AppSettings { Theme = "dark" });
        var external = store.Current;
        external.Theme = "light";
        external.MiniBounds.Left = 999;
        Equal("dark", store.Current.Theme);
        Equal(18d, store.Current.MiniBounds.Left);
    });
    Check("Atomic replacement leaves no temporary files", () =>
    {
        var store = Store("atomic");
        for (var i = 0; i < 10; i++) store.Save(new AppSettings { Theme = i % 2 == 0 ? "dark" : "light" });
        Equal("light", store.Load().Theme);
        Equal(0, Directory.GetFiles(Path.GetDirectoryName(store.SettingsPath)!, "*.tmp").Length);
    });
    Check("Failed replacement preserves original JSON and Current", () =>
    {
        var store = Store("locked");
        store.Save(new AppSettings { Theme = "dark" });
        var original = File.ReadAllText(store.SettingsPath);
        using (var locked = new FileStream(store.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            Throws<IOException>(() => store.Save(new AppSettings { Theme = "light" }));
        Equal(original, File.ReadAllText(store.SettingsPath));
        Equal("dark", store.Current.Theme);
        Equal(0, Directory.GetFiles(Path.GetDirectoryName(store.SettingsPath)!, "*.tmp").Length);
    });
    Check("Unregistered and command-string launches are rejected", () =>
    {
        Equal("target_not_selected", launcher.Launch(new AppSettings(), "cmd.exe").Code);
        var settings = new AppSettings { LaunchTarget = new() { Id = Guid.NewGuid().ToString("D"), Path = @"C:\Example\Selected.exe" } };
        foreach (var request in new[] { "cmd.exe /c echo unauthorized", settings.LaunchTarget.Path, Guid.NewGuid().ToString("D"), "", Guid.Empty.ToString("D") })
        {
            var result = launcher.Launch(settings, request);
            True(!result.Success && result.ProcessId is null);
            Equal("target_not_registered", result.Code);
        }
    });
    Check("Relative, protocol, UNC and alternate stream paths are rejected", () =>
    {
        foreach (var invalid in new[] { "app.exe", @"C:app.exe", @"\\server\share\app.exe", @"\\?\C:\app.exe", "file:///C:/app.exe", "https://example.com/app.exe", @"C:\app.exe:stream.exe", @"C:\Example\CON.exe", @"C:\Example\COM1.exe" })
            Equal("invalid_target", Throws<TargetValidationException>(() => launcher.RegisterTarget(invalid)).Code);
    });
    Check("Script, shortcut and command-host registration is denied", () =>
    {
        foreach (var extension in new[] { "ps1", "bat", "cmd", "vbs", "js", "lnk" })
            Equal("unsupported_target", Throws<TargetValidationException>(() => launcher.RegisterTarget(@"C:\Example\script." + extension)).Code);
        foreach (var name in new[] { "powershell", "PowerShell_ISE", "pwsh", "cmd", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "dotnet", "python", "pythonw", "python3.12", "node", "bash", "wsl" })
            Equal("blocked_host", Throws<TargetValidationException>(() => launcher.RegisterTarget(@"C:\Example\" + name + ".exe")).Code);
    });
    Check("Unsafe legacy migration preserves the exact old target and reports a warning", () =>
    {
        foreach (var target in new[]
        {
            new LaunchTarget { Id = "not-a-guid", Path = @"C:\Example\app.exe" },
            new LaunchTarget { Id = Guid.NewGuid().ToString("D"), Path = @"C:\Example\cmd.exe" }
        })
        {
            var store = Store(Guid.NewGuid().ToString("N"));
            Write(store, JsonSerializer.Serialize(new AppSettings { SchemaVersion = 1, Theme = "dark", LaunchTarget = target }, SettingsStore.JsonOptions));
            var loaded = store.Load();
            Equal(target, loaded.LaunchTarget);
            Equal("dark", loaded.Theme);
            Equal(0, loaded.AppEntries.Count);
            True(store.RecoveryWarning is not null);
            True(File.Exists(store.SettingsPath));
            Equal(target, new SettingsStore(Path.GetDirectoryName(store.SettingsPath)!).Load().LaunchTarget);
        }
    });
    Check("Missing program reports an actual failure", () =>
    {
        var path = Path.Combine(testRoot, "does-not-exist.exe");
        Equal("target_missing", Throws<TargetValidationException>(() => launcher.RegisterTarget(path)).Code);
        var target = new LaunchTarget { Id = Guid.NewGuid().ToString("D"), Path = path };
        var result = launcher.Launch(new AppSettings { LaunchTarget = target }, target.Id);
        True(!result.Success && result.ProcessId is null);
        Equal("target_missing", result.Code);
    });
    Check("Renamed text file cannot masquerade as an executable", () =>
    {
        var path = Path.Combine(testRoot, "not-a-binary.exe");
        File.WriteAllText(path, "This is an inert text file, not a PE executable.");
        Equal("invalid_executable", Throws<TargetValidationException>(() => launcher.RegisterTarget(path)).Code);
        var target = new LaunchTarget { Id = Guid.NewGuid().ToString("D"), Path = path };
        Equal("invalid_executable", launcher.Launch(new AppSettings { LaunchTarget = target }, target.Id).Code);
    });
    Check("New application stays outside Dock until explicitly favorited; opacity supports zero", () =>
    {
        var store = Store("favorites"); var library = new AppLibraryService(store);
        var fixture = Registered("favorite-fixture"); var app = launcher.RegisterApp(fixture.Target);
        True(!app.PinnedToDock); library.Add(app); Equal(0, store.Current.LauncherPins.Count);
        library.SetPin("dock", "app", app.Id, true); True(library.FindById(app.Id)!.PinnedToDock);
        library.SetPin("dock", "app", app.Id, false); Equal(0, store.Current.LauncherPins.Count); Equal(1, store.Current.AppEntries.Count);
        foreach (var value in new[] { 0d, .01, .5, 1d }) Equal(value, SettingsValidator.Normalize(new AppSettings { MiniOpacity = value }).MiniOpacity);
    });
    Check("Five independently registered apps append, persist, and survive deep snapshot mutation", () =>
    {
        var store = Store("five-apps");
        var library = new AppLibraryService(store);
        var originals = Enumerable.Range(0, 5).Select(index => Registered("five-" + index)).ToList();
        foreach (var app in originals) library.Add(app);
        Equal(5, store.Current.AppEntries.Count);
        Equal(5, store.Current.AppEntries.Select(app => app.Id).Distinct().Count());
        Sequence(originals.Select(app => app.Target), store.Current.AppEntries.Select(app => app.Target));
        Equal(5, store.Current.LauncherPins.Count(pin => pin.Surface == "dock"));
        var external = store.Current;
        external.AppEntries[0].Target = @"C:\Example\cmd.exe";
        external.AppEntries.RemoveAt(1);
        external.LauncherPins[0].ItemId = "changed";
        var queried = library.FindById(originals[0].Id)!;
        queried.Name = "changed";
        Equal(originals[0].Name, library.FindById(originals[0].Id)!.Name);
        var restarted = new SettingsStore(Path.GetDirectoryName(store.SettingsPath)!).Load();
        Sequence(originals.Select(app => app.Id), restarted.AppEntries.Select(app => app.Id));
        Sequence(originals.Select(app => app.Target), restarted.AppEntries.Select(app => app.Target));
        True(restarted.AppEntries.All(app => app.PinnedToDock));
    });
    Check("Editing and removing one app leaves the other records and original software intact", () =>
    {
        var store = Store("independent-edit");
        var library = new AppLibraryService(store);
        var originals = Enumerable.Range(0, 5).Select(index => Registered("edit-" + index)).ToList();
        foreach (var app in originals) library.Add(app);
        library.Update(originals[2].Id, "My updated app", "--open \"hello world\"", testRoot, true, false);
        var edited = library.FindById(originals[2].Id)!;
        Equal("My updated app", edited.Name);
        Equal("--open \"hello world\"", edited.Arguments);
        True(edited.PinnedToMini && !edited.PinnedToDock);
        foreach (var original in originals.Where((_, index) => index != 2))
            Equal(original.Target, library.FindById(original.Id)!.Target);
        library.Remove(originals[2].Id);
        Equal(4, store.Current.AppEntries.Count);
        True(library.FindById(originals[2].Id) is null);
        True(!store.Current.LauncherPins.Any(pin => pin.ItemId == originals[2].Id));
        True(originals.All(app => File.Exists(app.Target)));
        Equal("app_not_registered", launcher.LaunchApp(store.Current, originals[2].Id).Code);
    });
    Check("Workspace, Dock and Mini orders are independent and pin IDs stay stable", () =>
    {
        var store = Store("independent-order");
        var library = new AppLibraryService(store);
        var originals = Enumerable.Range(0, 5).Select(index => Registered("order-" + index)).ToList();
        foreach (var app in originals) library.Add(app);
        var originalDockIds = store.Current.LauncherPins.Where(pin => pin.Surface == "dock").Select(pin => pin.Id).ToList();
        library.Reorder(originals.AsEnumerable().Reverse().Select(app => app.Id).ToList());
        Sequence(originals.Select(app => app.Id), store.Current.LauncherPins.Where(pin => pin.Surface == "dock").Select(pin => pin.ItemId));
        library.ReorderPins("dock", originalDockIds.AsEnumerable().Reverse().ToList());
        var sceneId = Guid.NewGuid().ToString("D");
        new SceneLibraryService(store).Save(new Scene { Id = sceneId, Name = "创作" });
        library.SetPins("mini", [new() { ItemId = originals[0].Id }, new() { ItemType = "scene", ItemId = sceneId }]);
        var miniIds = store.Current.LauncherPins.Where(pin => pin.Surface == "mini").Select(pin => pin.Id).ToList();
        library.SetPins("mini", [new() { ItemType = "scene", ItemId = sceneId }, new() { ItemId = originals[0].Id, Enabled = false }]);
        Sequence(miniIds.AsEnumerable().Reverse(), store.Current.LauncherPins.Where(pin => pin.Surface == "mini").Select(pin => pin.Id));
        True(!library.FindById(originals[0].Id)!.PinnedToMini);
        Sequence(originals.AsEnumerable().Reverse().Select(app => app.Id), store.Current.AppEntries.Select(app => app.Id));
        Sequence(originalDockIds.AsEnumerable().Reverse(), store.Current.LauncherPins.Where(pin => pin.Surface == "dock").Select(pin => pin.Id));
        Equal(5, store.Current.AppEntries.Count);
    });
    Check("Mini cap applies to mixed visible pins without limiting the application library", () =>
    {
        var store = Store("mini-cap");
        var library = new AppLibraryService(store);
        var originals = Enumerable.Range(0, 5).Select(index => Registered("mini-" + index)).ToList();
        foreach (var app in originals) library.Add(app);
        var sceneId = Guid.NewGuid().ToString("D");
        new SceneLibraryService(store).Save(new Scene { Id = sceneId, Name = "创作" });
        library.SetPins("mini", [new() { ItemId = originals[0].Id }, new() { ItemId = originals[1].Id },
            new() { ItemId = originals[2].Id }, new() { ItemType = "scene", ItemId = sceneId }]);
        var before = File.ReadAllText(store.SettingsPath);
        Throws<SettingsValidationException>(() => library.SetPin("mini", "app", originals[3].Id, true));
        Equal(before, File.ReadAllText(store.SettingsPath));
        Equal(5, store.Current.AppEntries.Count);
        Equal(4, store.Current.LauncherPins.Count(pin => pin.Surface == "mini" && pin.Enabled));
        library.SetPin("mini", "app", originals[0].Id, false);
        Equal(5, store.Current.AppEntries.Count);
        Equal(3, store.Current.LauncherPins.Count(pin => pin.Surface == "mini"));
    });
    Check("Duplicate targets append while duplicate IDs and malformed mutations cannot overwrite saved data", () =>
    {
        var store = Store("mutation-validation");
        var library = new AppLibraryService(store);
        var app = Registered("duplicate-target");
        library.Add(app);
        library.Add(app with { Id = Guid.NewGuid().ToString("D"), Name = "Another configuration" });
        Equal(2, store.Current.AppEntries.Count);
        var before = File.ReadAllText(store.SettingsPath);
        Throws<SettingsValidationException>(() => library.Add(app));
        Throws<SettingsValidationException>(() => library.Reorder([app.Id, app.Id]));
        Throws<SettingsValidationException>(() => library.SetPins("mini", [new() { ItemId = Guid.NewGuid().ToString("D") }]));
        Throws<SettingsValidationException>(() => library.SetPins("mini", [new() { ItemId = app.Id }, new() { ItemId = app.Id }]));
        Throws<SettingsValidationException>(() => library.SetPins("workspace", []));
        Throws<TargetValidationException>(() => library.Update(app.Id, app.Name, "--safe\ncmd.exe", testRoot, false, true));
        Throws<TargetValidationException>(() => library.Update(app.Id, app.Name, "", Path.Combine(testRoot, "missing-working-dir"), false, true));
        Throws<SettingsValidationException>(() => library.Add(app with { Id = Guid.NewGuid().ToString("D"), Icon = "../escape.png" }));
        Equal(before, File.ReadAllText(store.SettingsPath));
    });
    Check("Missing app and missing shortcut records survive restart with truthful launch errors", () =>
    {
        var store = Store("stale-records");
        var library = new AppLibraryService(store);
        var app = Registered("stale-app");
        library.Add(app);
        var shortcut = app with { Id = Guid.NewGuid().ToString("D"), Name = "Stale shortcut", Target = Path.Combine(testRoot, "missing.lnk"), EntryKind = "lnk" };
        library.Add(shortcut);
        File.Delete(app.Target); // Only this suite's unique, copied test executable.
        var restart = new SettingsStore(Path.GetDirectoryName(store.SettingsPath)!);
        var loaded = restart.Load();
        Equal(2, loaded.AppEntries.Count);
        True(restart.RecoveryWarning is null);
        Equal("target_missing", launcher.LaunchApp(loaded, app.Id).Code);
        Equal("target_missing", launcher.LaunchApp(loaded, shortcut.Id).Code);
    });
    Check("Legacy migration is additive, uses the stable ID and cannot resurrect removed apps", () =>
    {
        var store = Store("legacy-additive");
        var existing = Registered("existing-before-migration");
        var legacy = new LaunchTarget { Id = Guid.NewGuid().ToString("D"), Path = Path.Combine(testRoot, "old-stale.exe"), DisplayName = "old" };
        Write(store, JsonSerializer.Serialize(new AppSettings { SchemaVersion = 1, AppEntries = [existing], LaunchTarget = legacy }, SettingsStore.JsonOptions));
        var loaded = store.Load();
        Equal(4, loaded.SchemaVersion);
        Equal(2, loaded.AppEntries.Count);
        Equal(existing.Id, loaded.AppEntries[0].Id);
        Equal(legacy.Id, loaded.AppEntries[1].Id);
        Equal(legacy, loaded.LaunchTarget);
        True(store.RecoveryWarning is not null);
        Equal(2, store.Load().AppEntries.Count);
        Equal(2, SettingsValidator.Normalize(loaded with { SchemaVersion = 1 }).AppEntries.Count);
        new AppLibraryService(store).Remove(legacy.Id);
        var restarted = new SettingsStore(Path.GetDirectoryName(store.SettingsPath)!).Load();
        Equal(1, restarted.AppEntries.Count);
        Equal(legacy, restarted.LaunchTarget);
    });
    Check("Locked migration cannot replace the old file and leaves usable in-memory data", () =>
    {
        var store = Store("migration-locked");
        var app = Registered("locked-legacy");
        var original = JsonSerializer.Serialize(new AppSettings { SchemaVersion = 1,
            LaunchTarget = new() { Id = app.Id, Path = app.Target, DisplayName = app.Name } }, SettingsStore.JsonOptions);
        Write(store, original);
        using (var held = new FileStream(store.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Equal(1, store.Load().AppEntries.Count);
            True(store.RecoveryWarning!.Contains("写入失败", StringComparison.Ordinal));
            Equal(original, File.ReadAllText(store.SettingsPath));
        }
        Equal(1, store.Load().AppEntries.Count);
        Equal(4, JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(store.SettingsPath), SettingsStore.JsonOptions)!.SchemaVersion);
    });
    Check("Local persistence supports libraries beyond the M1 file budget and concurrent additive writes", () =>
    {
        var store = Store("large-library");
        var seed = Registered("large-seed");
        var settings = new AppSettings { AppEntries = Enumerable.Range(0, 1500).Select(index => seed with
            { Id = Guid.NewGuid().ToString("D"), Name = "Application " + index, DisplayOrder = index }).ToList() };
        store.Save(settings);
        True(new FileInfo(store.SettingsPath).Length > 256 * 1024);
        Equal(1500, store.Load().AppEntries.Count);
        var concurrent = Store("concurrent-add");
        var library = new AppLibraryService(concurrent);
        Parallel.For(0, 12, index => library.Add(seed with { Id = Guid.NewGuid().ToString("D"), Name = "Concurrent " + index }));
        Equal(12, concurrent.Current.AppEntries.Count);
        Equal(12, concurrent.Load().AppEntries.Count);
    });
    Check("Shortcut resolver is required and fresh shortcut targets cannot bypass executable validation", () =>
    {
        var selected = Path.Combine(testRoot, "selected.lnk");
        File.WriteAllText(selected, "inert injected resolver fixture");
        Equal("shortcut_unsupported", Throws<TargetValidationException>(() => launcher.RegisterApp(selected)).Code);
        var exe = Registered("shortcut-safe-target");
        var resolver = new TestShortcutResolver(new(exe.Target, "--imported", testRoot));
        var shortcutLauncher = new NativeLaunchService(resolver);
        var app = shortcutLauncher.RegisterApp(selected);
        Equal("lnk", app.EntryKind);
        Equal(selected, app.Target);
        Equal("--imported", app.Arguments);
        resolver.Result = new(@"C:\Example\cmd.exe", "", "");
        Equal("blocked_host", shortcutLauncher.LaunchApp(new AppSettings { AppEntries = [app] }, app.Id).Code);
        Equal(2, resolver.Calls);
        resolver.Result = new("shell:AppsFolder\\StoreApp", "", "");
        Equal("invalid_target", shortcutLauncher.LaunchApp(new AppSettings { AppEntries = [app] }, app.Id).Code);
        resolver.Result = new(exe.Target, "\0unsafe", testRoot);
        Equal("invalid_arguments", Throws<TargetValidationException>(() => shortcutLauncher.RegisterApp(selected)).Code);
    });
    if (args.Length == 2 && args[0] == "--fixture")
    {
        Check("Explicit inert fixture launches through Process.Start and returns PID", () =>
        {
            // A successful result means Windows created a process, not that it remained running or is ready.
            True(Path.GetFileName(args[1]).Equals("DesktopHub.LaunchFixture.exe", StringComparison.OrdinalIgnoreCase));
            var target = launcher.RegisterTarget(Path.GetFullPath(args[1]));
            var result = launcher.Launch(new AppSettings { LaunchTarget = target }, target.Id);
            True(result.Success && result.ProcessId is > 0);
            Equal("started", result.Code);
            using var fixtureProcess = Process.GetProcessById(result.ProcessId!.Value);
            True(string.Equals(fixtureProcess.MainModule?.FileName, target.Path, StringComparison.OrdinalIgnoreCase));
            True(SpinWait.SpinUntil(() =>
            {
                fixtureProcess.Refresh();
                return fixtureProcess.HasExited || fixtureProcess.MainWindowHandle != 0;
            }, TimeSpan.FromSeconds(5)));
            if (!fixtureProcess.HasExited)
            {
                True(fixtureProcess.CloseMainWindow());
                True(fixtureProcess.WaitForExit(5000));
            }
        });
        Check("App ID launch passes literal arguments and working directory without a command shell", () =>
        {
            True(Path.GetFileName(args[1]).Equals("DesktopHub.LaunchFixture.exe", StringComparison.OrdinalIgnoreCase));
            var store = Store("fixture-app-launch");
            var library = new AppLibraryService(store);
            var app = launcher.RegisterApp(Path.GetFullPath(args[1]));
            library.Add(app);
            library.Update(app.Id, "Controlled fixture", "--library \"hello world\" & benign", testRoot, false, true);
            VerifyControlledApp(launcher, store.Current, app.Id, ["--library", "hello world", "&", "benign"]);
        });
        Check("Shortcut launch re-resolves targets while saved empty arguments override shortcut defaults", () =>
        {
            True(Path.GetFileName(args[1]).Equals("DesktopHub.LaunchFixture.exe", StringComparison.OrdinalIgnoreCase));
            var shortcutPath = Path.Combine(testRoot, "fixture-import.lnk");
            File.WriteAllText(shortcutPath, "inert injected shortcut fixture");
            var resolver = new TestShortcutResolver(new(Path.GetFullPath(args[1]), "--imported", testRoot));
            var shortcutLauncher = new NativeLaunchService(resolver);
            var app = shortcutLauncher.RegisterApp(shortcutPath) with { Arguments = "" };
            resolver.Result = resolver.Result with { Arguments = "--changed-shortcut-default" };
            VerifyControlledApp(shortcutLauncher, new AppSettings { AppEntries = [app] }, app.Id, []);
            Equal(2, resolver.Calls);
        });
    }
    else Console.WriteLine("SKIP controlled launch integration: pass --fixture <DesktopHub.LaunchFixture.exe>.");
}
finally
{
    // testRoot was created by this suite below its build output directory with a unique GUID.
    Directory.Delete(testRoot, recursive: true);
}
Console.WriteLine($"RESULT {passed} passed; {failed} failed.");
return failed == 0 ? 0 : 1;

SettingsStore Store(string name) => new(Path.Combine(testRoot, name));
AppEntry Registered(string name)
{
    // Copies this suite's known PE apphost solely for registration and persistence tests; it is never launched.
    var path = Path.Combine(testRoot, name + ".exe");
    File.Copy(Path.Combine(AppContext.BaseDirectory, "DesktopHub.Core.Tests.exe"), path);
    return launcher.RegisterApp(path) with { PinnedToDock = true }; // These fixtures explicitly request a Dock favorite.
}
void Sequence<T>(IEnumerable<T> expected, IEnumerable<T> actual)
{
    if (!expected.SequenceEqual(actual)) throw new Exception("Sequences differ.");
}
void VerifyControlledApp(NativeLaunchService service, AppSettings settings, string appId, string[] expectedArguments)
{
    var result = service.LaunchApp(settings, appId);
    True(result.Success && result.ProcessId is > 0);
    using var process = Process.GetProcessById(result.ProcessId!.Value);
    var markerPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args[1]))!, $"fixture-started-{result.ProcessId}.json");
    try
    {
        True(SpinWait.SpinUntil(() => File.Exists(markerPath) && process.MainWindowHandle != 0, TimeSpan.FromSeconds(8)));
        using var marker = JsonDocument.Parse(File.ReadAllText(markerPath));
        Equal(result.ProcessId.Value, marker.RootElement.GetProperty("processId").GetInt32());
        Equal(Path.GetFullPath(args[1]), marker.RootElement.GetProperty("executable").GetString());
        Equal(testRoot, marker.RootElement.GetProperty("workingDirectory").GetString());
        Sequence(expectedArguments, marker.RootElement.GetProperty("arguments").EnumerateArray().Select(value => value.GetString()!));
    }
    finally
    {
        process.Refresh();
        if (!process.HasExited)
        {
            process.CloseMainWindow();
            if (!process.WaitForExit(5000))
            {
                process.Kill(); // Only the PID this test just launched from the inert fixture.
                process.WaitForExit(5000);
            }
        }
        if (File.Exists(markerPath)) File.Delete(markerPath);
    }
}
void Write(SettingsStore store, string json)
{
    Directory.CreateDirectory(Path.GetDirectoryName(store.SettingsPath)!);
    File.WriteAllText(store.SettingsPath, json);
}
void Check(string name, Action test)
{
    try { test(); passed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception exception) { failed++; Console.WriteLine($"FAIL {name}: {exception}"); }
}
void True(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new Exception($"Expected '{expected}', got '{actual}'.");
}
T Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T exception) { return exception; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

sealed class TestShortcutResolver(ResolvedShortcut result) : IShortcutResolver
{
    public ResolvedShortcut Result { get; set; } = result;
    public int Calls { get; private set; }
    public ResolvedShortcut Resolve(string shortcutPath) { Calls++; return Result; }
}
