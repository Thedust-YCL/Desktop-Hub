namespace DesktopHub.Windows;

internal static class StartupPresentationPolicy
{
    public static bool ShouldOpenWorkspace(bool diagnostic, bool loginStartup) => !diagnostic && !loginStartup;
}
