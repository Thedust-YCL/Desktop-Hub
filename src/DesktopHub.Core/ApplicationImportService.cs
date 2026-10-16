using System.Diagnostics;

namespace DesktopHub.Core;

public sealed record ApplicationScanRoot(string Path, string Name);
public sealed record ApplicationCandidate(string Id, string Name, string Path, string Source, string EntryKind);
public sealed record ApplicationScan(string ScanId, IReadOnlyList<ApplicationScanRoot> Roots,
    IReadOnlyList<ApplicationCandidate> Candidates, bool Recursive, IReadOnlyList<string> Warnings, bool Truncated);
public sealed record ApplicationImportSkip(string Name, string Code, string Message);
public sealed record ApplicationImportResult(IReadOnlyList<AppEntry> Added, IReadOnlyList<ApplicationImportSkip> Skipped, AppSettings Settings);

/// <summary>Read-only discovery in native-authorized roots; confirmation accepts opaque IDs, never frontend paths.</summary>
public sealed class ApplicationImportService(SettingsStore store, NativeLaunchService launcher, Func<AppEntry, string>? icon = null, Func<DateTime>? utcNow = null)
{
    private ApplicationScan? current;
    private DateTime expires;
    private ApplicationScan? staged;
    private DateTime stagedExpires;
    private bool currentShortcutsOnly;
    private IReadOnlyList<string>? currentFiles;
    private DateTime Now => utcNow?.Invoke() ?? DateTime.UtcNow;
    private readonly AppLibraryService library = new(store);

    public static IReadOnlyList<ApplicationScanRoot> DesktopRoots() => new[]
    {
        new ApplicationScanRoot(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "个人桌面"),
        new ApplicationScanRoot(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "公共桌面")
    }.Where(root => !string.IsNullOrWhiteSpace(root.Path)).DistinctBy(root => root.Path, StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<ApplicationScanRoot> StartMenuRoots() => new[]
    {
        new ApplicationScanRoot(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "个人开始菜单"),
        new ApplicationScanRoot(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "公共开始菜单")
    }.Where(root => !string.IsNullOrWhiteSpace(root.Path)).DistinctBy(root => root.Path, StringComparer.OrdinalIgnoreCase).ToArray();

    public ApplicationScan Scan(IReadOnlyList<ApplicationScanRoot> roots, bool recursive, bool shortcutsOnly = false)
    {
        var candidates = new List<ApplicationCandidate>(); var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var timer = Stopwatch.StartNew(); var examined = 0; var truncated = false;
        var normalized = new List<ApplicationScanRoot>();
        foreach (var root in roots)
        {
            try
            {
                var path = NativeLaunchService.ValidateLocalPath(root.Path);
                if (normalized.All(existing => !existing.Path.Equals(path, StringComparison.OrdinalIgnoreCase))) normalized.Add(root with { Path = path });
            }
            catch (TargetValidationException ex) { if (warnings.Count < 20) warnings.Add($"无法读取 {root.Name}：{ex.Message}"); }
        }
        foreach (var root in normalized)
        {
            var directories = new Stack<(string Path, int Depth)>(); directories.Push((root.Path, 0));
            while (directories.Count > 0 && !truncated)
            {
                var (directory, depth) = directories.Pop();
                if (!visited.Add(directory)) continue;
                try
                {
                    foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        if (++examined > 50000 || candidates.Count >= 1000 || timer.Elapsed > TimeSpan.FromSeconds(10))
                        { truncated = true; break; }
                        try
                        {
                            var attributes = File.GetAttributes(entry);
                            // Never descend through junctions/symlinks or hydrate offline/cloud placeholders.
                            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0) continue;
                            if ((attributes & FileAttributes.Directory) != 0)
                            {
                                if (recursive && depth < 16) directories.Push((entry, depth + 1));
                                else if (recursive && warnings.Count < 20) warnings.Add($"目录层级过深，已跳过：{entry}");
                                continue;
                            }
                            var extension = Path.GetExtension(entry);
                            if (shortcutsOnly && !extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase)) continue;
                            if (!extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                            var path = NativeLaunchService.ValidateLocalPath(entry);
                            if (seen.Add(path)) candidates.Add(new(Guid.NewGuid().ToString("N"), Path.GetFileNameWithoutExtension(path), path,
                                root.Name, extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? "lnk" : "exe"));
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TargetValidationException)
                        { if (warnings.Count < 20) warnings.Add($"无法读取：{entry}（{ex.Message}）"); }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                { if (warnings.Count < 20) warnings.Add($"无法读取 {root.Name}：{directory}（{ex.Message}）"); }
            }
            if (truncated) break;
        }
        current = new(Guid.NewGuid().ToString("N"), normalized,
            candidates.OrderBy(candidate => candidate.EntryKind == "lnk" ? 0 : 1).ThenBy(candidate => candidate.Name, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(candidate => candidate.Path, StringComparer.OrdinalIgnoreCase).ToArray(), recursive, warnings.Take(20).ToArray(), truncated);
        expires = Now.AddMinutes(15);
        currentShortcutsOnly = shortcutsOnly; currentFiles = null; staged = null;
        return current;
    }

    public ApplicationScan Rescan(string scanId, bool recursive)
    {
        var scan = RequireScan(scanId);
        return currentFiles is { } files ? ScanFiles(files, scan.Candidates.FirstOrDefault()?.Source ?? "所选文件") : Scan(scan.Roots, recursive, currentShortcutsOnly);
    }

    /// <summary>Exact files selected by a native picker; no containing directory is authorized.</summary>
    public ApplicationScan ScanFiles(IReadOnlyList<string> paths, string source)
    {
        var scan = StageFiles(paths, source);
        return ActivateStaged(scan.ScanId);
    }

    /// <summary>One detached native drop preview; current import authorization survives until explicit activation.</summary>
    public ApplicationScan StageFiles(IReadOnlyList<string> paths, string source)
    {
        if (paths.Count is < 1 or > 100) throw new TargetValidationException("invalid_selection", "每次请选择或拖入 1–100 个应用文件。");
        var candidates = new List<ApplicationCandidate>(); var warnings = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var original in paths)
        {
            try
            {
                var path = NativeLaunchService.ValidateLocalPath(original);
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                    throw new TargetValidationException("unavailable_target", "仅支持已下载的普通 EXE 或 LNK 文件。");
                var extension = Path.GetExtension(path);
                if (!extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
                    throw new TargetValidationException("unsupported_target", "仅支持 EXE 或普通 LNK 文件。");
                if (seen.Add(path)) candidates.Add(new(Guid.NewGuid().ToString("N"), Path.GetFileNameWithoutExtension(path), path, source,
                    extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? "lnk" : "exe"));
            }
            catch (Exception ex) when (ex is TargetValidationException or IOException or UnauthorizedAccessException)
            { if (warnings.Count < 20) warnings.Add($"无法添加 {Path.GetFileName(original)}：{ex.Message}"); }
        }
        staged = new(Guid.NewGuid().ToString("N"), [], candidates, false, warnings, false);
        stagedExpires = Now.AddMinutes(15);
        return staged;
    }

    public ApplicationScan ActivateStaged(string scanId)
    {
        if (staged is not { } scan || scan.ScanId != scanId || Now > stagedExpires)
            throw new TargetValidationException("scan_expired", "拖入列表已过期，请重新选择或拖入。");
        current = scan; expires = stagedExpires; currentFiles = scan.Candidates.Select(candidate => candidate.Path).ToArray();
        currentShortcutsOnly = false; staged = null;
        return scan;
    }

    public ApplicationImportResult Import(string scanId, IReadOnlyList<string> candidateIds)
    {
        var scan = RequireScan(scanId);
        var byId = scan.Candidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        if (candidateIds.Count is < 1 or > 100 || candidateIds.Distinct(StringComparer.Ordinal).Count() != candidateIds.Count || candidateIds.Any(id => !byId.ContainsKey(id)))
            throw new TargetValidationException("invalid_selection", "请选择当前列表中的 1–100 个应用，每项只能选择一次。");
        var entries = new List<AppEntry>(); var skipped = new List<ApplicationImportSkip>();
        var known = new HashSet<(string Target, string Arguments, string Directory)>();
        foreach (var app in store.Current.AppEntries)
        {
            try { known.Add(Identity(app)); }
            catch (Exception ex) when (ex is TargetValidationException or IOException or UnauthorizedAccessException) { }
        }
        foreach (var id in candidateIds)
        {
            var candidate = byId[id];
            try
            {
                var attributes = File.GetAttributes(candidate.Path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Offline)) != 0)
                    throw new TargetValidationException("unavailable_target", "文件是链接或尚未下载，请先在资源管理器中检查。");
                var app = SettingsValidator.NormalizeEntry(launcher.RegisterApp(candidate.Path));
                if (!known.Add(Identity(app))) { skipped.Add(new(candidate.Name, "duplicate", "应用库或本次选择中已有相同程序和启动参数。")); continue; }
                if (icon is not null) app.Icon = icon(app);
                entries.Add(app);
            }
            catch (Exception ex) when (ex is TargetValidationException or SettingsValidationException or IOException or UnauthorizedAccessException)
            { skipped.Add(new(candidate.Name, ex is TargetValidationException validation ? validation.Code : "invalid_application", ex.Message)); }
        }
        // Validation finishes before one atomic write. A write failure keeps the preview available for retry.
        var settings = entries.Count > 0 ? library.AddMany(entries) : store.Current;
        current = null;
        return new(entries, skipped, settings);
    }

    public IReadOnlyList<ApplicationCandidate> GetCandidates(string scanId, IReadOnlyList<string> candidateIds)
    {
        var scan = RequireScan(scanId); var byId = scan.Candidates.ToDictionary(candidate => candidate.Id, StringComparer.Ordinal);
        if (candidateIds.Count is < 1 or > 40 || candidateIds.Distinct().Count() != candidateIds.Count || candidateIds.Any(id => !byId.ContainsKey(id)))
            throw new TargetValidationException("invalid_selection", "请选择当前应用列表中的项目。");
        return candidateIds.Select(id => byId[id]).ToArray();
    }

    private (string Target, string Arguments, string Directory) Identity(AppEntry app)
    {
        var target = launcher.ResolveEntry(app).Target;
        var directory = string.IsNullOrEmpty(app.WorkingDirectory) ? Path.GetDirectoryName(target)! : app.WorkingDirectory;
        return (target.ToUpperInvariant(), app.Arguments, directory.ToUpperInvariant());
    }

    private ApplicationScan RequireScan(string scanId) => current is { } scan && scan.ScanId == scanId && Now <= expires ? scan :
        throw new TargetValidationException("scan_expired", "应用列表已过期，请重新扫描后选择。");
}
