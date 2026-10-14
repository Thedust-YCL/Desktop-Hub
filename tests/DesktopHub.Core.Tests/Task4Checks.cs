using DesktopHub.Core;

internal static class Task4Checks
{
    internal static void Run(Action<string, Action> check, string root)
    {
        var launcher = new NativeLaunchService();
        SettingsStore Store(string name) { var s = new SettingsStore(Path.Combine(root, name)); s.Load(); return s; }
        string Exe(string name) { var path = Path.Combine(root, name + ".exe"); File.Copy(Path.Combine(AppContext.BaseDirectory, "DesktopHub.Core.Tests.exe"), path); return path; }
        void True(bool value) { if (!value) throw new Exception("Task4 assertion failed"); }
        void Reject(Action action) { try { action(); } catch (TargetValidationException) { return; } throw new Exception("Expected rejection"); }
        check("Task4 exact native file selection never authorizes siblings or parent recursion", () =>
        {
            var selected = Exe("task4-selected"); Exe("task4-sibling");
            var imports = new ApplicationImportService(Store("task4-files"), launcher);
            var scan = imports.ScanFiles([selected], "手动选择");
            True(scan.Candidates.Count == 1 && scan.Candidates[0].Path == selected && !scan.Recursive && scan.Roots.Count == 0);
            var repeated = imports.Rescan(scan.ScanId, true);
            True(repeated.Candidates.Count == 1 && !repeated.Recursive && repeated.Roots.Count == 0);
        });
        check("Task4 detached drop requires activation and preserves current token until accepted", () =>
        {
            var selected = Exe("task4-drop"); var imports = new ApplicationImportService(Store("task4-drop-store"), launcher);
            var active = imports.Scan([new(root, "旧预览")], false);
            var staged = imports.StageFiles([selected], "拖入应用");
            Reject(() => imports.GetCandidates(staged.ScanId, [staged.Candidates[0].Id]));
            True(imports.GetCandidates(active.ScanId, [active.Candidates[0].Id]).Count == 1);
            var newer = imports.StageFiles([selected], "拖入应用");
            Reject(() => imports.ActivateStaged(staged.ScanId));
            imports.ActivateStaged(newer.ScanId);
            Reject(() => imports.GetCandidates(active.ScanId, [active.Candidates[0].Id]));
            True(imports.Import(newer.ScanId, [newer.Candidates[0].Id]).Added.Count == 1);
            Reject(() => imports.ActivateStaged(newer.ScanId));
        });
        check("Task4 StartMenu recursive discovery offers only LNK and survives unreadable roots", () =>
        {
            var folder = Path.Combine(root, "task4-programs"); Directory.CreateDirectory(Path.Combine(folder, "nested"));
            File.WriteAllText(Path.Combine(folder, "nested", "tool.LNK"), "metadata"); File.WriteAllText(Path.Combine(folder, "hidden.exe"), "metadata");
            var imports = new ApplicationImportService(Store("task4-programs-store"), launcher);
            var scan = imports.Scan([new(folder, "个人开始菜单"), new(Path.Combine(root, "absent"), "公共开始菜单")], true, true);
            True(scan.Candidates.Count == 1 && scan.Candidates[0].Source == "个人开始菜单" && scan.Warnings.Count == 1);
            True(imports.Rescan(scan.ScanId, true).Candidates.Count == 1);
        });
        check("Task4 editor atomically saves Favorite with pins and preserves omitted Favorite", () =>
        {
            var store = Store("task4-editor"); var library = new AppLibraryService(store); var app = launcher.RegisterApp(Exe("task4-editor-app")); library.Add(app);
            library.Update(app.Id, "Renamed", "--safe", root, false, true, true);
            True(store.Load().AppEntries.Single().Favorite && store.Current.AppEntries.Single().PinnedToDock);
            library.Update(app.Id, "Again", "", root, false, false); True(store.Load().AppEntries.Single().Favorite);
            var bytes = File.ReadAllBytes(store.SettingsPath);
            Reject(() => library.Update(app.Id, "Broken", "--bad\n", root, true, true, false));
            True(bytes.SequenceEqual(File.ReadAllBytes(store.SettingsPath)) && store.Current.AppEntries.Single().Favorite && !store.Current.AppEntries.Single().PinnedToDock);
        });
        check("Task4 staged and active tokens expire without extending the original authorization", () =>
        {
            var now = new DateTime(2026, 10, 14, 0, 0, 0, DateTimeKind.Utc); var path = Exe("task4-expiry");
            var imports = new ApplicationImportService(Store("task4-expiry-store"), launcher, utcNow: () => now);
            var staged = imports.StageFiles([path], "拖入应用"); now = now.AddMinutes(14); imports.ActivateStaged(staged.ScanId);
            now = now.AddMinutes(2); Reject(() => imports.Import(staged.ScanId, [staged.Candidates[0].Id]));
            staged = imports.StageFiles([path], "拖入应用"); now = now.AddMinutes(16); Reject(() => imports.ActivateStaged(staged.ScanId));
        });
        check("Task4 exact selection bounds, directory refusal and import write failure preserve the valid preview", () =>
        {
            var store = Store("task4-write-failure"); store.Save(new()); var imports = new ApplicationImportService(store, launcher); var path = Exe("task4-write-app");
            Reject(() => imports.StageFiles(Enumerable.Repeat(path, 101).ToArray(), "拖入应用"));
            var denied = imports.StageFiles([root, Path.Combine(root, "absent.exe")], "拖入应用"); True(denied.Candidates.Count == 0 && denied.Warnings.Count == 2);
            var scan = imports.ScanFiles([path, path], "手动选择"); True(scan.Candidates.Count == 1);
            Reject(() => imports.Import(scan.ScanId, [scan.Candidates[0].Id, scan.Candidates[0].Id]));
            var before = File.ReadAllBytes(store.SettingsPath);
            using (var locked = File.Open(store.SettingsPath, FileMode.Open, FileAccess.Read, FileShare.None))
            { try { imports.Import(scan.ScanId, [scan.Candidates[0].Id]); throw new Exception("Expected locked write"); } catch (IOException) { } }
            True(before.SequenceEqual(File.ReadAllBytes(store.SettingsPath)) && store.Current.AppEntries.Count == 0);
            True(imports.Import(scan.ScanId, [scan.Candidates[0].Id]).Added.Count == 1);
        });
        check("Task4 desktop and Programs entries deduplicate effective executable arguments and directory", () =>
        {
            var exe = Exe("task4-dedup-target"); var folder = Path.Combine(root, "task4-dedup-links"); Directory.CreateDirectory(folder);
            var names = new[] { "desktop", "programs", "arguments", "directory", "store" }; foreach (var name in names) File.WriteAllText(Path.Combine(folder, name + ".lnk"), "inert resolver fixture");
            var other = Path.Combine(root, "task4-other-working"); Directory.CreateDirectory(other);
            var shortcutLauncher = new NativeLaunchService(new Task4ShortcutResolver(path => Path.GetFileNameWithoutExtension(path) switch
            { "arguments" => new(exe, "--different", root), "directory" => new(exe, "", other), "store" => new("shell:AppsFolder\\StoreApp", "", ""), _ => new(exe, "", root) }));
            var store = Store("task4-dedup-store"); new AppLibraryService(store).Add(shortcutLauncher.RegisterApp(Path.Combine(folder, "desktop.lnk")));
            var imports = new ApplicationImportService(store, shortcutLauncher); var scan = imports.Scan([new(folder, "公共开始菜单")], true, true);
            var result = imports.Import(scan.ScanId, scan.Candidates.Select(c => c.Id).ToArray());
            True(result.Added.Count == 2 && result.Skipped.Count(s => s.Code == "duplicate") == 2 && result.Skipped.Count(s => s.Code != "duplicate") == 1);
            True(store.Load().AppEntries.Count == 3);
        });
        check("Task4 atomic editor Mini cap and invalid directory leave Favorite pins metadata and disk unchanged", () =>
        {
            var store = Store("task4-cap-store"); var library = new AppLibraryService(store); var apps = Enumerable.Range(0, 5).Select(i => launcher.RegisterApp(Exe("task4-cap-" + i))).ToArray();
            foreach (var app in apps) library.Add(app); foreach (var app in apps.Take(4)) library.SetPin("mini", "app", app.Id, true);
            var before = File.ReadAllBytes(store.SettingsPath); var last = apps[4];
            try { library.Update(last.Id, "New", "", root, true, true, true); throw new Exception("Expected Mini cap"); } catch (SettingsValidationException) { }
            True(before.SequenceEqual(File.ReadAllBytes(store.SettingsPath)) && !store.Current.AppEntries[4].Favorite);
            Reject(() => library.Update(last.Id, "New", "", Path.Combine(root, "absent"), false, true, true));
            True(before.SequenceEqual(File.ReadAllBytes(store.SettingsPath)));
            foreach (var favorite in new[] { false, true }) foreach (var dock in new[] { false, true })
            { library.Update(last.Id, "Last", "", root, false, dock, favorite); var loaded = store.Load().AppEntries[4]; True(loaded.Favorite == favorite && loaded.PinnedToDock == dock); }
        });
        check("Task4 group-local app order preserves other positions and independent pin orders across restart", () =>
        {
            var store = Store("task4-order-store"); var library = new AppLibraryService(store); var apps = Enumerable.Range(0, 4).Select(i => launcher.RegisterApp(Exe("task4-order-" + i))).ToArray();
            foreach (var app in apps) library.Add(app); var group = library.SaveGroup(null, "Work").AppGroups.Single();
            library.MoveToGroup(apps[0].Id, group.Id); library.MoveToGroup(apps[2].Id, group.Id);
            foreach (var app in apps.Take(2)) { library.SetPin("dock", "app", app.Id, true); library.SetPin("mini", "app", app.Id, true); }
            var pins = store.Current.LauncherPins; library.Reorder([apps[2].Id, apps[1].Id, apps[0].Id, apps[3].Id]); var loaded = store.Load();
            True(loaded.AppEntries.Select(a => a.Id).SequenceEqual(new[] { apps[2].Id, apps[1].Id, apps[0].Id, apps[3].Id }) && loaded.LauncherPins.SequenceEqual(pins));
            var before = File.ReadAllBytes(store.SettingsPath); try { library.Reorder([apps[0].Id, apps[2].Id]); throw new Exception("Expected full permutation"); } catch (SettingsValidationException) { }
            True(before.SequenceEqual(File.ReadAllBytes(store.SettingsPath)));
        });
    }
}

internal sealed class Task4ShortcutResolver(Func<string, ResolvedShortcut> resolve) : IShortcutResolver
{ public ResolvedShortcut Resolve(string shortcutPath) => resolve(shortcutPath); }
