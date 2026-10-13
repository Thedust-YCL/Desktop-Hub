namespace DesktopHub.Core;

public sealed record AppGroup(string Id, string Name, int DisplayOrder = 0);

public sealed record SavedResourceFilter
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Query { get; set; } = "";
    public List<string> TagIds { get; set; } = [];
    public string? Extension { get; set; }
    public bool FavoritesOnly { get; set; }
    public string Sort { get; set; } = "type";
}

public sealed record SavedResourceFilterStatus(SavedResourceFilter Filter, List<string> MissingTagIds)
{
    public bool Valid => MissingTagIds.Count == 0;
}

public sealed record SceneWindowLayout
{
    public bool RestoreAfterRun { get; set; }
    public int WaitTimeoutMs { get; set; } = 15000;
    public List<SceneWindowPlacement> Windows { get; set; } = [];
}

/// <summary>Bounds are relative to the saved monitor work area, in DIP. No runtime HWND/PID is persisted.</summary>
public sealed record SceneWindowPlacement
{
    public string AppEntryId { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public string TitleContains { get; set; } = "";
    public string MonitorId { get; set; } = "";
    public WindowBounds Bounds { get; set; } = new();
    public string State { get; set; } = "normal";
    public double Dpi { get; set; } = 96;
}
