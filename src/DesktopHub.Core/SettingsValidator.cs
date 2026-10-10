namespace DesktopHub.Core;

public static class SettingsValidator
{
    private static readonly string[] Positions = ["left", "right", "top", "bottom"];
    private static readonly string[] Sizes = ["small", "normal", "large"];

    public static AppSettings Normalize(AppSettings settings) => Normalize(settings, out _);

    public static AppSettings Normalize(AppSettings settings, out string? migrationWarning)
    {
        ArgumentNullException.ThrowIfNull(settings);
        migrationWarning = null;
        if (settings.SchemaVersion is not (1 or 2 or 3))
            throw new SettingsValidationException("不支持此配置版本。请保留配置文件后升级应用。");
        CheckChoice(settings.Theme, ["system", "dark", "light"], "theme");
        CheckChoice(settings.MiniPosition, Positions, "miniPosition");
        CheckChoice(settings.DockPosition, Positions, "dockPosition");
        CheckChoice(settings.MiniSize, Sizes, "miniSize");
        CheckChoice(settings.DockSize, Sizes, "dockSize");
        CheckChoice(settings.DockIconSize, ["compact", "normal", "large"], "dockIconSize");
        CheckChoice(settings.MiniMode, ["icons", "labels"], "miniMode");
        var defaults = new AppSettings();
        var entries = (settings.AppEntries ?? []).Select(NormalizeEntry).OrderBy(entry => entry.DisplayOrder).ToList();
        if (entries.Select(entry => entry.Id).Distinct(StringComparer.Ordinal).Count() != entries.Count)
            throw new SettingsValidationException("应用 ID 重复，不能覆盖原有记录。");
        var pins = (settings.LauncherPins ?? []).Select(pin => pin is not null ? pin with { } :
            throw new SettingsValidationException("固定入口不能为空。")).ToList();
        // Migration runs once on schema 1 only. The retained legacy record is never used as the app library.
        if (settings.SchemaVersion == 1 && settings.LaunchTarget is { } candidate)
        {
            try
            {
                if (!Guid.TryParseExact(candidate.Id, "D", out var id) || id == Guid.Empty)
                    throw new SettingsValidationException("旧测试程序标识无效。");
                var path = NativeLaunchService.ValidatePath(candidate.Path);
                if (entries.All(entry => entry.Id != id.ToString("D")))
                {
                    entries.Add(NormalizeEntry(new AppEntry
                    {
                        Id = id.ToString("D"), Target = path, Name = System.IO.Path.GetFileNameWithoutExtension(path),
                        WorkingDirectory = System.IO.Path.GetDirectoryName(path)!, DisplayOrder = entries.Count,
                        PinnedToDock = true
                    }));
                    if (!pins.Any(pin => pin.Surface == "dock" && pin.ItemType == "app" && pin.ItemId == id.ToString("D")))
                        pins.Add(new LauncherPin { Id = Guid.NewGuid().ToString("D"), Surface = "dock", ItemType = "app",
                            ItemId = id.ToString("D"), DisplayOrder = pins.Count });
                }
                if (!File.Exists(path)) migrationWarning = "M1 应用记录已保留并迁移，但程序路径已经失效，请编辑应用修复。";
            }
            catch (Exception ex) when (ex is SettingsValidationException or TargetValidationException)
            {
                migrationWarning = $"M1 测试程序无法安全迁移，旧 launchTarget 已原样保留：{ex.Message}";
            }
        }
        for (var index = 0; index < entries.Count; index++) entries[index] = entries[index] with { DisplayOrder = index };
        pins = NormalizePins(pins, entries);
        foreach (var entry in entries)
        {
            entry.PinnedToMini = pins.Any(pin => pin.Enabled && pin.Surface == "mini" && pin.ItemType == "app" && pin.ItemId == entry.Id);
            entry.PinnedToDock = pins.Any(pin => pin.Enabled && pin.Surface == "dock" && pin.ItemType == "app" && pin.ItemId == entry.Id);
        }
        var scenes = (settings.Scenes ?? []).Select(SceneValidator.Normalize).OrderBy(scene => scene.DisplayOrder).ToList();
        if (scenes.Select(scene => scene.Id).Distinct().Count() != scenes.Count) throw new SettingsValidationException("场景 ID 重复。");
        foreach (var action in scenes.SelectMany(scene => scene.Actions))
            action.ReferenceInvalid = action.AppEntryId is { } id && entries.All(entry => entry.Id != id);
        return settings with
        {
            SchemaVersion = 3,
            HoverDelay = Math.Clamp(settings.HoverDelay, 100, 3000),
            WorkspaceBounds = NormalizeBounds(settings.WorkspaceBounds, defaults.WorkspaceBounds, 480, 320),
            MiniBounds = NormalizeBounds(settings.MiniBounds, defaults.MiniBounds, 40, 40),
            DockBounds = NormalizeBounds(settings.DockBounds, defaults.DockBounds, 200, 200),
            LaunchTarget = settings.LaunchTarget is { } legacy ? legacy with { } : null,
            AppEntries = entries,
            LauncherPins = pins,
            Scenes = scenes
        };
    }

    internal static AppEntry NormalizeEntry(AppEntry? entry)
    {
        if (entry is null) throw new SettingsValidationException("应用记录不能为空。");
        if (!Guid.TryParseExact(entry.Id, "D", out var id) || id == Guid.Empty)
            throw new SettingsValidationException("应用标识无效。");
        var name = entry.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 200 || name.Any(char.IsControl))
            throw new SettingsValidationException("应用名称不能为空、含控制字符或超过 200 个字符。");
        var path = NativeLaunchService.ValidateAppPath(entry.Target, checkDrive: false);
        NativeLaunchService.ValidateArguments(entry.Arguments);
        var directory = NativeLaunchService.ValidateWorkingDirectory(entry.WorkingDirectory, checkDrive: false);
        var icon = entry.Icon;
        if (icon is null || icon.Length > 160 || (icon != "default" &&
            (!icon.EndsWith(".png", StringComparison.OrdinalIgnoreCase) || icon.Contains("..", StringComparison.Ordinal) ||
             icon.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))))
            throw new SettingsValidationException("图标只能引用本地缓存 PNG 文件名或默认图标。");
        return entry with { Id = id.ToString("D"), Name = name, Target = path, WorkingDirectory = directory,
            EntryKind = System.IO.Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ? "lnk" : "exe" };
    }

    internal static List<LauncherPin> NormalizePins(IEnumerable<LauncherPin> pins, IReadOnlyList<AppEntry> entries)
    {
        var normalized = new List<LauncherPin>();
        var identifiers = new HashSet<string>(StringComparer.Ordinal);
        var references = new HashSet<string>(StringComparer.Ordinal);
        var appIds = entries.Select(entry => entry.Id).ToHashSet(StringComparer.Ordinal);
        var nextOrder = new Dictionary<string, int>(StringComparer.Ordinal) { ["mini"] = 0, ["dock"] = 0 };
        foreach (var pin in pins.OrderBy(pin => pin.Surface, StringComparer.Ordinal).ThenBy(pin => pin.DisplayOrder))
        {
            CheckChoice(pin.Surface, ["mini", "dock"], "surface");
            CheckChoice(pin.ItemType, ["app", "scene"], "itemType");
            if (!Guid.TryParseExact(pin.Id, "D", out var id) || id == Guid.Empty || !identifiers.Add(id.ToString("D")))
                throw new SettingsValidationException("固定入口 ID 无效或重复。");
            if (string.IsNullOrEmpty(pin.ItemId) || pin.ItemId.Length > 120 ||
                pin.ItemId.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_' or '.')))
                throw new SettingsValidationException("固定入口引用无效。");
            if (pin.ItemType == "app" && !appIds.Contains(pin.ItemId))
                throw new SettingsValidationException("固定入口引用的应用不存在。");
            if (!references.Add($"{pin.Surface}/{pin.ItemType}/{pin.ItemId}"))
                throw new SettingsValidationException("同一位置不能重复固定相同入口。");
            normalized.Add(pin with { Id = id.ToString("D"), DisplayOrder = nextOrder[pin.Surface]++ });
        }
        if (normalized.Count(pin => pin.Surface == "mini" && pin.Enabled) > 4)
            throw new SettingsValidationException("Mini 最多显示 4 个固定应用或场景；完整应用库没有此限制。");
        return normalized;
    }

    private static void CheckChoice(string? value, string[] allowed, string field)
    {
        if (value is null || !allowed.Contains(value, StringComparer.Ordinal))
            throw new SettingsValidationException($"配置字段 {field} 无效。");
    }

    private static WindowBounds NormalizeBounds(WindowBounds? bounds, WindowBounds fallback, double minWidth, double minHeight)
    {
        bounds ??= fallback;
        return bounds with
        {
            Left = FiniteClamp(bounds.Left, fallback.Left, -32768, 32768),
            Top = FiniteClamp(bounds.Top, fallback.Top, -32768, 32768),
            Width = FiniteClamp(bounds.Width, fallback.Width, minWidth, 7680),
            Height = FiniteClamp(bounds.Height, fallback.Height, minHeight, 4320)
        };
    }

    private static double FiniteClamp(double value, double fallback, double minimum, double maximum) =>
        double.IsFinite(value) ? Math.Clamp(value, minimum, maximum) : fallback;
}
