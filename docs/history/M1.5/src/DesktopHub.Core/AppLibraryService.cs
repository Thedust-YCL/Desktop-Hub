namespace DesktopHub.Core;

/// <summary>Record management only. Removing an entry never touches the selected software or shortcut.</summary>
public sealed class AppLibraryService(SettingsStore store)
{
    /// <summary>Reusable stable-ID lookup for later scene references; returns an isolated snapshot.</summary>
    public AppEntry? FindById(string appId) => store.Current.AppEntries.FirstOrDefault(entry => entry.Id == appId);

    public AppSettings Add(AppEntry nativeRegisteredEntry) => store.Update(settings =>
    {
        var entry = SettingsValidator.NormalizeEntry(nativeRegisteredEntry);
        if (settings.AppEntries.Any(existing => existing.Id == entry.Id))
            throw new SettingsValidationException("应用 ID 已存在，新增不能覆盖已有应用。");
        entry = entry with { DisplayOrder = settings.AppEntries.Count };
        settings.AppEntries.Add(entry);
        if (nativeRegisteredEntry.PinnedToDock) AddPin(settings, "dock", "app", entry.Id);
        if (nativeRegisteredEntry.PinnedToMini) AddPin(settings, "mini", "app", entry.Id);
        return settings;
    });

    public AppSettings Update(string appId, string name, string arguments, string workingDirectory,
        bool pinnedToMini, bool pinnedToDock) => store.Update(settings =>
    {
        var index = FindEntry(settings, appId);
        NativeLaunchService.ValidateArguments(arguments);
        var directory = NativeLaunchService.ValidateWorkingDirectory(workingDirectory, requireExists: true);
        settings.AppEntries[index] = settings.AppEntries[index] with { Name = name, Arguments = arguments, WorkingDirectory = directory };
        ChangePin(settings, "mini", "app", appId, pinnedToMini);
        ChangePin(settings, "dock", "app", appId, pinnedToDock);
        return settings;
    });

    /// <summary>The replacement is selected and registered by the native picker, never supplied as a bridge path.</summary>
    public AppSettings ReplaceTarget(string appId, AppEntry nativeRegisteredReplacement) => store.Update(settings =>
    {
        var replacement = SettingsValidator.NormalizeEntry(nativeRegisteredReplacement);
        var index = FindEntry(settings, appId);
        settings.AppEntries[index] = settings.AppEntries[index] with
        {
            Target = replacement.Target, EntryKind = replacement.EntryKind, Icon = replacement.Icon
        };
        return settings;
    });

    public AppSettings Remove(string appId) => store.Update(settings =>
    {
        settings.AppEntries.RemoveAt(FindEntry(settings, appId));
        settings.LauncherPins.RemoveAll(pin => pin.ItemType == "app" && pin.ItemId == appId);
        return settings;
    });

    public AppSettings Reorder(IReadOnlyList<string> appIds) => store.Update(settings =>
    {
        RequirePermutation(appIds, settings.AppEntries.Select(entry => entry.Id).ToList());
        var byId = settings.AppEntries.ToDictionary(entry => entry.Id, StringComparer.Ordinal);
        settings.AppEntries = appIds.Select((id, order) => byId[id] with { DisplayOrder = order }).ToList();
        return settings;
    });

    public AppSettings SetPin(string surface, string itemType, string itemId, bool pinned) => store.Update(settings =>
    {
        ValidateSurface(surface);
        ChangePin(settings, surface, itemType, itemId, pinned);
        return settings;
    });

    /// <summary>Replace one surface layout atomically. Stable pin IDs survive movement or visibility changes.</summary>
    public AppSettings SetPins(string surface, IReadOnlyList<LauncherPin> pins) => store.Update(settings =>
    {
        ValidateSurface(surface);
        ArgumentNullException.ThrowIfNull(pins);
        var oldPins = settings.LauncherPins.Where(pin => pin.Surface == surface)
            .ToDictionary(pin => (pin.ItemType, pin.ItemId));
        var replacements = pins.Select((pin, order) =>
        {
            if (pin is null) throw new SettingsValidationException("固定入口不能为空。");
            return new LauncherPin
            {
                Id = oldPins.TryGetValue((pin.ItemType, pin.ItemId), out var existing) ? existing.Id : Guid.NewGuid().ToString("D"),
                Surface = surface, ItemType = pin.ItemType, ItemId = pin.ItemId, Enabled = pin.Enabled, DisplayOrder = order
            };
        }).ToList();
        settings.LauncherPins.RemoveAll(pin => pin.Surface == surface);
        settings.LauncherPins.AddRange(replacements);
        return settings;
    });

    public AppSettings ReorderPins(string surface, IReadOnlyList<string> pinIds) => store.Update(settings =>
    {
        ValidateSurface(surface);
        var affected = settings.LauncherPins.Where(pin => pin.Surface == surface).ToList();
        RequirePermutation(pinIds, affected.Select(pin => pin.Id).ToList());
        var byId = affected.ToDictionary(pin => pin.Id, StringComparer.Ordinal);
        settings.LauncherPins.RemoveAll(pin => pin.Surface == surface);
        settings.LauncherPins.AddRange(pinIds.Select((id, order) => byId[id] with { DisplayOrder = order }));
        return settings;
    });

    private static void ChangePin(AppSettings settings, string surface, string itemType, string itemId, bool pinned)
    {
        if (pinned) AddPin(settings, surface, itemType, itemId);
        else settings.LauncherPins.RemoveAll(pin => pin.Surface == surface && pin.ItemType == itemType && pin.ItemId == itemId);
    }

    private static void AddPin(AppSettings settings, string surface, string itemType, string itemId)
    {
        if (itemType is not ("app" or "scene")) throw new SettingsValidationException("固定入口类型无效。");
        if (itemType == "app") FindEntry(settings, itemId);
        var existing = settings.LauncherPins.FirstOrDefault(pin => pin.Surface == surface && pin.ItemType == itemType && pin.ItemId == itemId);
        if (existing is not null) existing.Enabled = true;
        else settings.LauncherPins.Add(new LauncherPin
        {
            Id = Guid.NewGuid().ToString("D"), Surface = surface, ItemType = itemType, ItemId = itemId,
            DisplayOrder = settings.LauncherPins.Count(pin => pin.Surface == surface)
        });
    }

    private static int FindEntry(AppSettings settings, string appId)
    {
        var index = settings.AppEntries.FindIndex(entry => entry.Id == appId);
        if (index < 0) throw new SettingsValidationException("应用记录不存在，可能已被移除。");
        return index;
    }

    private static void ValidateSurface(string surface)
    {
        if (surface is not ("mini" or "dock")) throw new SettingsValidationException("固定位置仅支持 Mini 和 Dock。");
    }

    private static void RequirePermutation(IReadOnlyList<string> requested, IReadOnlyList<string> existing)
    {
        ArgumentNullException.ThrowIfNull(requested);
        var existingIds = existing.ToHashSet(StringComparer.Ordinal);
        if (requested.Count != existing.Count || requested.Distinct(StringComparer.Ordinal).Count() != requested.Count ||
            requested.Any(id => !existingIds.Contains(id)))
            throw new SettingsValidationException("排序必须包含全部现有 ID，每条记录只能出现一次。");
    }
}
