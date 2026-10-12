using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopHub.Core;

/// <summary>Atomic local JSON persistence. A failed write leaves Current and the original file intact.</summary>
public sealed class SettingsStore
{
    // Capacity is a serialized-byte safety bound, not an application-count limit.
    public const int MaximumFileBytes = 16 * 1024 * 1024;
    public static JsonSerializerOptions JsonOptions { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 16
    };

    private readonly object _gate = new();
    private AppSettings _current = new();
    public string SettingsPath { get; }
    public string? RecoveryWarning { get; private set; }
    public AppSettings Current { get { lock (_gate) return Snapshot(_current); } }

    public SettingsStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        SettingsPath = Path.Combine(Path.GetFullPath(directory), "settings.json");
    }

    public AppSettings Load()
    {
        lock (_gate)
        {
            RecoveryWarning = null;
            if (!File.Exists(SettingsPath)) { _current = new(); return Snapshot(_current); }
            try
            {
                AppSettings parsed;
                using (var input = new FileStream(SettingsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (input.Length > MaximumFileBytes)
                        throw new SettingsValidationException("配置文件超过 16 MiB 安全限制。");
                    // Isolate damaged scene records so the validated M1.5 app library still loads.
                    using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 16 });
                    if (document.RootElement.ValueKind != JsonValueKind.Object) throw new SettingsValidationException("配置必须是对象。");
                    var node = System.Text.Json.Nodes.JsonNode.Parse(document.RootElement.GetRawText())!.AsObject();
                    var sceneNode = node["scenes"];
                    node.Remove("scenes");
                    parsed = node.Deserialize<AppSettings>(JsonOptions)
                        ?? throw new SettingsValidationException("配置内容为空。");
                    var scenes = new List<Scene>();
                    if (sceneNode is System.Text.Json.Nodes.JsonArray array)
                    {
                        foreach (var item in array)
                        {
                            try
                            {
                                var scene = SceneValidator.Normalize(item?.Deserialize<Scene>(JsonOptions)
                                    ?? throw new SettingsValidationException("空场景"));
                                if (scenes.Any(existing => existing.Id == scene.Id)) throw new SettingsValidationException("重复场景 ID");
                                scenes.Add(scene);
                            }
                            catch (Exception ex) when (ex is JsonException or SettingsValidationException or TargetValidationException)
                            { RecoveryWarning = JoinWarning(RecoveryWarning, $"一个损坏场景未载入，原配置保留：{ex.Message}"); }
                        }
                    }
                    else if (sceneNode is not null) RecoveryWarning = "场景列表损坏，应用库仍可使用。原配置保留。";
                    parsed.Scenes = scenes;
                    if (RecoveryWarning is not null)
                    {
                        try { File.Copy(SettingsPath, Path.Combine(Path.GetDirectoryName(SettingsPath)!, $"settings.scene-recovery-{Guid.NewGuid():N}.json")); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { RecoveryWarning += " 备份未写入：" + ex.Message; }
                    }
                }
                _current = SettingsValidator.Normalize(parsed, out var migrationWarning);
                RecoveryWarning = JoinWarning(RecoveryWarning, migrationWarning ?? "");
                if (parsed.SchemaVersion < 4 && RecoveryWarning is null)
                {
                    try { Save(_current); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SettingsValidationException)
                    {
                        RecoveryWarning = JoinWarning(RecoveryWarning, $"迁移配置写入失败，原文件未替换：{ex.Message}");
                    }
                }
                return Snapshot(_current);
            }
            catch (Exception ex) when (ex is JsonException or SettingsValidationException or TargetValidationException)
            {
                _current = new();
                RecoveryWarning = "配置损坏或版本不受支持，已恢复默认设置。";
                PreserveCorruptFile();
                return Snapshot(_current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _current = new();
                RecoveryWarning = $"配置读取失败，正在使用默认设置：{ex.Message}";
                return Snapshot(_current);
            }
        }
    }

    private static AppSettings Snapshot(AppSettings settings) => settings with
    {
        WorkspaceBounds = settings.WorkspaceBounds with { },
        MiniBounds = settings.MiniBounds with { },
        DockBounds = settings.DockBounds with { },
        DesktopIcons = settings.DesktopIcons with { },
        ForegroundBlocklist = [.. settings.ForegroundBlocklist],
        LaunchTarget = settings.LaunchTarget is { } target ? target with { } : null,
        AppEntries = settings.AppEntries.Select(entry => entry with { }).ToList(),
        LauncherPins = settings.LauncherPins.Select(pin => pin with { }).ToList(),
        Scenes = settings.Scenes.Select(SceneValidator.Clone).ToList()
    };

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            var normalized = SettingsValidator.Normalize(settings);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(normalized, JsonOptions);
            if (bytes.Length > MaximumFileBytes)
                throw new SettingsValidationException("配置超过安全限制，未写入。");
            var directory = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(directory);
            var temporary = Path.Combine(directory, $"settings.{Guid.NewGuid():N}.tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                {
                    output.Write(bytes);
                    output.Flush(flushToDisk: true);
                }
                if (File.Exists(SettingsPath)) File.Replace(temporary, SettingsPath, null);
                else File.Move(temporary, SettingsPath);
                _current = normalized;
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    /// <summary>One atomic read/modify/write transaction; callers never expose mutable saved lists.</summary>
    public AppSettings Update(Func<AppSettings, AppSettings> mutation)
    {
        ArgumentNullException.ThrowIfNull(mutation);
        lock (_gate)
        {
            Save(mutation(Snapshot(_current)));
            return Snapshot(_current);
        }
    }

    private static string JoinWarning(string? existing, string additional) =>
        string.IsNullOrEmpty(additional) ? existing! : string.IsNullOrEmpty(existing) ? additional : existing + " " + additional;

    private void PreserveCorruptFile()
    {
        var backup = Path.Combine(Path.GetDirectoryName(SettingsPath)!,
            $"settings.corrupt-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.json");
        try
        {
            File.Move(SettingsPath, backup);
            RecoveryWarning += $" 原文件已保留为 {Path.GetFileName(backup)}。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecoveryWarning += $" 无法备份原配置：{ex.Message}";
        }
    }
}
