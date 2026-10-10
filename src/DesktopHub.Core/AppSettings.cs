namespace DesktopHub.Core;

/// <summary>Versioned local settings shared by all three surfaces. Runtime focus handles are never persisted.</summary>
public sealed record AppSettings
{
    public int SchemaVersion { get; set; } = 4;
    public string Theme { get; set; } = "system";
    public bool ReducedMotion { get; set; }
    public bool Transparency { get; set; } = true;
    public string MiniPosition { get; set; } = "left";
    public string MiniSize { get; set; } = "normal";
    public string MiniMode { get; set; } = "icons";
    public string DockPosition { get; set; } = "left";
    public string DockSize { get; set; } = "normal";
    public string DockIconSize { get; set; } = "normal";
    public bool HoverEnabled { get; set; } = true;
    public int HoverDelay { get; set; } = 500;
    public bool AllowForeground { get; set; } = true;
    public bool MiniEnabled { get; set; } = true;
    public double MiniOpacity { get; set; } = .92;
    public string DockHotkey { get; set; } = "Ctrl+Alt+D";
    public string WorkspaceHotkey { get; set; } = "Ctrl+Alt+W";
    public List<string> ForegroundBlocklist { get; set; } = [];
    public bool StartWithWindows { get; set; }
    public string? StartupCommand { get; set; }
    public string? StartupPendingCommand { get; set; }
    public DesktopIconSettings DesktopIcons { get; set; } = new();
    public WindowBounds WorkspaceBounds { get; set; } = new() { Left = 100, Top = 80, Width = 1100, Height = 720 };
    public WindowBounds MiniBounds { get; set; } = new() { Left = 18, Top = 300, Width = 56, Height = 260 };
    public WindowBounds DockBounds { get; set; } = new() { Left = 18, Top = 260, Width = 370, Height = 540 };
    public LaunchTarget? LaunchTarget { get; set; }
    public List<AppEntry> AppEntries { get; set; } = [];
    public List<LauncherPin> LauncherPins { get; set; } = [];
    public List<Scene> Scenes { get; set; } = [];
}

/// <summary>One independently launchable application. Target remains the selected EXE or LNK path.</summary>
public sealed record AppEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Target { get; set; } = "";
    public string Arguments { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public string Icon { get; set; } = "default";
    public string EntryKind { get; set; } = "exe";
    public int DisplayOrder { get; set; }
    public bool PinnedToMini { get; set; }
    public bool PinnedToDock { get; set; }
}

/// <summary>Canonical layout reference; application paths are never copied into surface layouts.</summary>
public sealed record LauncherPin
{
    public string Id { get; set; } = "";
    public string Surface { get; set; } = "mini";
    public string ItemType { get; set; } = "app";
    public string ItemId { get; set; } = "";
    public int DisplayOrder { get; set; }
    public bool Enabled { get; set; } = true;
}

public interface IShortcutResolver
{
    ResolvedShortcut Resolve(string shortcutPath);
}

/// <summary>Only a local ordinary executable target is supported; Store/MSIX activation is outside M1.5.</summary>
public sealed record ResolvedShortcut(string Target, string Arguments, string WorkingDirectory,
    string? IconPath = null, int IconIndex = 0);

public sealed record WindowBounds
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}

/// <summary>Created by the native file picker registration path, never from a bridge launch request.</summary>
public sealed record LaunchTarget
{
    public string Id { get; set; } = "";
    public string Path { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public sealed record LaunchResult(bool Success, int? ProcessId, string Code, string Message);

public sealed class SettingsValidationException(string message) : Exception(message);

public sealed class TargetValidationException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

