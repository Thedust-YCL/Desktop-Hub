namespace DesktopHub.Windows;

public sealed record LayoutHistoryItem(string ResultId, long Order, string Kind, string? ExecutionId, LayoutRestoreResult Result);

/// <summary>Memory-only host history. Manual outcomes cannot consume an automatic execution's history slot.</summary>
public sealed class SceneWindowLayoutHistory
{
    private readonly List<LayoutHistoryItem> items = [];
    private long nextOrder;

    public LayoutHistoryItem RecordAutomatic(string executionId, LayoutRestoreResult result)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executionId);
        var existing = items.Find(item => item.Kind == "automatic" && item.ExecutionId == executionId);
        return Store(existing?.ResultId ?? Guid.NewGuid().ToString("D"), "automatic", executionId, result);
    }

    public LayoutHistoryItem RecordManual(LayoutRestoreResult result) =>
        Store(Guid.NewGuid().ToString("D"), "manual", null, result);

    public LayoutHistoryItem RecordChoice(string token, LayoutRestoreResult result)
    {
        var existing = items.Find(item => item.Result.RestoreToken == token);
        return existing is null ? RecordManual(result) : Store(existing.ResultId, existing.Kind, existing.ExecutionId, result);
    }

    public bool IsAutomaticToken(string token) => items.Any(item => item.Kind == "automatic" && item.Result.RestoreToken == token);

    public IReadOnlyList<LayoutHistoryItem> Snapshot() => items.OrderBy(item => item.Order).ToArray();

    private LayoutHistoryItem Store(string resultId, string kind, string? executionId, LayoutRestoreResult result)
    {
        var order = ++nextOrder;
        var enriched = result with { ResultId = resultId, Order = order, Kind = kind, ExecutionId = executionId };
        var item = new LayoutHistoryItem(resultId, order, kind, executionId, enriched);
        items.RemoveAll(previous => previous.ResultId == resultId);
        items.Add(item);
        var maximum = kind == "automatic" ? 32 : 8;
        while (items.Count(previous => previous.Kind == kind) > maximum)
            items.Remove(items.Where(previous => previous.Kind == kind).MinBy(previous => previous.Order)!);
        return item;
    }
}
