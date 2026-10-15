using System.Text.Json.Serialization;

namespace DesktopHub.Core;

public sealed record ActivityRecord(string Type, string Id, DateTimeOffset At, string Result);
public sealed record ActivityFile
{
    [JsonRequired] public int SchemaVersion { get; set; } = 1;
    [JsonRequired] public List<ActivityRecord> Items { get; set; } = [];
}

/// <summary>Success-bound, stable-ID history. Names and launch authority are resolved by the host.</summary>
public sealed class ActivityService
{
    private readonly object _gate = new();
    private readonly TimeProvider _clock;
    private readonly SchemaOneStore<ActivityFile> _store;
    public string? RecoveryWarning { get { lock (_gate) return _store.RecoveryWarning; } }
    public event Action? Changed;

    public ActivityService(string directory, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _store = new(directory, "activity.json", Normalize);
    }

    public bool Record(string type, string id, string result)
    {
        ValidateIdentity(type, id);
        if (!Successful(type, result)) return false;
        lock (_gate)
        {
            _store.EnsureWritable();
            var rows = _store.Current.Items.Where(row => row.Type != type || row.Id != id).Select(row => row with { }).ToList();
            rows.Insert(0, new(type, id, _clock.GetUtcNow(), result));
            if (rows.Count > 50) rows.RemoveRange(50, rows.Count - 50);
            _store.Save(new() { Items = rows });
        }
        Changed?.Invoke();
        return true;
    }

    public IReadOnlyList<ActivityRecord> Snapshot()
    {
        lock (_gate) return _store.Current.Items.Select(row => row with { }).ToList();
    }

    public void Clear()
    {
        lock (_gate) _store.Save(new());
        Changed?.Invoke();
    }

    private static ActivityFile Normalize(ActivityFile file)
    {
        if (file.SchemaVersion != 1 || file.Items is null || file.Items.Count > 50) throw new SettingsValidationException("最近使用数据无效。");
        var keys = new HashSet<(string, string)>();
        foreach (var row in file.Items)
        {
            if (row is null) throw new SettingsValidationException("最近使用项为空。");
            ValidateIdentity(row.Type, row.Id);
            if (!Successful(row.Type, row.Result) || row.At == default || !keys.Add((row.Type, row.Id))) throw new SettingsValidationException("最近使用项结果、时间或去重标识无效。");
        }
        return new() { Items = file.Items.Select(row => row with { }).ToList() };
    }
    private static void ValidateIdentity(string type, string id)
    {
        if (type is not ("app" or "scene" or "file" or "folder")) throw new SettingsValidationException("最近使用类型无效。");
        ActivityProjectIdentity.Entity(id);
    }
    private static bool Successful(string type, string result) => type switch
    {
        "app" => result == "started",
        "scene" => result == "execution_established",
        "file" or "folder" => result == "submitted",
        _ => false
    };
}
