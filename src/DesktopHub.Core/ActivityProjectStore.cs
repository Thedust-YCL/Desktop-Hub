using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopHub.Core;

/// <summary>Private persistence for the two independent schema-1 metadata files.</summary>
internal sealed class SchemaOneStore<T> where T : class, new()
{
    internal const int MaximumFileBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = true,
        MaxDepth = 16
    };
    private readonly Func<T, T> _validate;
    private readonly string _path;
    private bool _blocked;
    internal T Current { get; private set; } = new();
    internal string? RecoveryWarning { get; private set; }

    internal SchemaOneStore(string directory, string filename, Func<T, T> validate)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _path = Path.Combine(Path.GetFullPath(directory), filename);
        _validate = validate;
        try { Current = Read() ?? new(); }
        catch (Exception ex) when (IsReadFailure(ex)) { Block(ex); }
    }

    internal void EnsureWritable()
    {
        if (_blocked) throw new InvalidOperationException(RecoveryWarning);
        // Validate again on every mutation, including corrupt/unreadable files swapped in after initialization.
        try { _ = Read(); }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            Block(ex);
            throw new InvalidOperationException(RecoveryWarning, ex);
        }
    }

    internal void Save(T candidate)
    {
        EnsureWritable();
        var normalized = _validate(candidate);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(normalized, Json);
        if (bytes.Length > MaximumFileBytes) throw new SettingsValidationException("元数据超过 16 MiB 安全限制，未写入。");
        var directory = Path.GetDirectoryName(_path)!;
        string? temporary = null;
        try
        {
            Directory.CreateDirectory(directory);
            temporary = Path.Combine(directory, $"{Path.GetFileNameWithoutExtension(_path)}.{Guid.NewGuid():N}.tmp");
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            if (File.Exists(_path)) AtomicFileReplacement.Replace(temporary, _path);
            else File.Move(temporary, _path);
            Current = normalized;
            RecoveryWarning = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A replacement failure is retryable; Current still describes the last committed transaction.
            RecoveryWarning = $"{Path.GetFileName(_path)} 保存失败，原数据保留。请重试：{ex.Message}";
            throw;
        }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private T? Read()
    {
        byte[] bytes;
        try
        {
            using var input = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length > MaximumFileBytes) throw new SettingsValidationException("文件超过 16 MiB 安全限制。");
            bytes = new byte[checked((int)input.Length)];
            input.ReadExactly(bytes);
            // Do not treat a racing append as a supported bounded file.
            if (input.ReadByte() != -1) throw new SettingsValidationException("读取期间文件发生变化。");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        var offset = bytes.Length >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
        var content = bytes.AsSpan(offset);
        ValidateHeader(content);
        using var document = JsonDocument.Parse(bytes.AsMemory(offset), new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicateFields(document.RootElement);
        return _validate(document.RootElement.Deserialize<T>(Json) ?? throw new JsonException("元数据内容为空。"));
    }

    private static void ValidateHeader(ReadOnlySpan<byte> bytes)
    {
        // Independently scan the header before current-schema depth/field parsing, even when the header follows a deep future payload.
        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { MaxDepth = int.MaxValue });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) throw new JsonException("元数据必须是对象。");
        var found = false;
        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;
            var schema = reader.ValueTextEquals("schemaVersion");
            if (!reader.Read()) throw new JsonException("元数据字段缺少值。");
            if (schema)
            {
                if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var version) || version != 1)
                    throw new SettingsValidationException("元数据版本不受支持，请升级应用。");
                if (found) throw new JsonException("元数据版本字段重复。");
                found = true;
            }
            reader.Skip();
        }
        if (!found) throw new JsonException("元数据缺少版本字段。");
    }

    private static void RejectDuplicateFields(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new JsonException("元数据字段重复。");
                RejectDuplicateFields(property.Value);
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (var item in node.EnumerateArray()) RejectDuplicateFields(item);
    }

    private static bool IsReadFailure(Exception ex) => ex is JsonException or SettingsValidationException or IOException or UnauthorizedAccessException;
    private void Block(Exception ex)
    {
        _blocked = true;
        RecoveryWarning = $"{Path.GetFileName(_path)} 无法载入，原文件保留且停止写入。修复文件后重启或升级应用：{ex.Message}";
    }
}

internal static class ActivityProjectIdentity
{
    internal static string Entity(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 96 || id.Any(char.IsControl) || !Guid.TryParse(id, out var value) || value == Guid.Empty)
            throw new SettingsValidationException("实体 ID 无效。");
        return id;
    }
    internal static string Stable(string? id)
    {
        if (id is null || id.Length != 36 || !Guid.TryParseExact(id, "D", out var value) || value == Guid.Empty)
            throw new SettingsValidationException("项目或关联 ID 必须是非空 D 格式 GUID。");
        return id;
    }
}
