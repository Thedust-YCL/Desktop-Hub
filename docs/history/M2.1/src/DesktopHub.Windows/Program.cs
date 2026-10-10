using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace DesktopHub.Windows;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        var smokeRestart = args.Contains("--smoke-test-restart");
        var smoke = args.Contains("--smoke-test") || smokeRestart;
        var sceneRestart = args.Contains("--scene-smoke-restart");
        var sceneSmoke = args.Contains("--scene-smoke") || sceneRestart;
        var resourceTest = args.Contains("--resource-test");
        var diagnostic = smoke || resourceTest || sceneSmoke;
        using var mutex = new Mutex(true, sceneSmoke ? "DesktopHub.M21.Smoke" : resourceTest ? "DesktopHub.M1.ResourceTest" : smoke ? "DesktopHub.M1.Smoke" : "DesktopHub.M1", out var owns);
        if (!owns) { MessageBox.Show("Desktop Hub 已在运行。", "Desktop Hub"); return 0; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        HubApplication? host = null;
        app.DispatcherUnhandledException += (_, e) =>
        {
            host?.Log("Unhandled", e.Exception.ToString());
            if (!diagnostic) MessageBox.Show("操作未完成：" + e.Exception.Message, "Desktop Hub", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        app.Startup += async (_, _) =>
        {
            try
            {
                var directory = sceneSmoke ? Path.Combine(AppContext.BaseDirectory, "m21-smoke-data") : resourceTest ? Path.Combine(AppContext.BaseDirectory, "resource-data") : smoke ? Path.Combine(AppContext.BaseDirectory, "m15-smoke-data") :
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopHub");
                // Explicit diagnostic only: never write resource-test settings into the user's profile.
                if (resourceTest) ResourceUsageProbe.Prepare(directory);
                if (smoke && !smokeRestart) SmokeTests.Prepare(directory);
                if (sceneSmoke && !sceneRestart) SceneSmokeTests.Prepare(directory);
                host = new HubApplication(directory, smoke || sceneSmoke);
                await host.StartAsync();
                if (smoke) await SmokeTests.RunAsync(host, smokeRestart);
                else if (sceneSmoke) await SceneSmokeTests.RunAsync(host, sceneRestart);
                else if (resourceTest) await ResourceUsageProbe.RunAsync(host);
            }
            catch (Exception e)
            {
                host?.Log("Startup", e.ToString());
                if (!diagnostic) MessageBox.Show("Desktop Hub 无法启动。请确认 .NET Desktop Runtime 8 和 WebView2 Runtime 已安装。\n" + e.Message, "Desktop Hub");
                else File.WriteAllText(Path.Combine(AppContext.BaseDirectory, sceneSmoke ? "m21-smoke-error.txt" : resourceTest ? "resource-error.txt" : "m15-smoke-error.txt"), e.ToString());
                app.Shutdown(1);
            }
        };
        app.Exit += (_, _) => { try { host?.Dispose(); } catch (Exception e) { host?.Log("Dispose", e.ToString()); } };
        return app.Run();
    }
}
