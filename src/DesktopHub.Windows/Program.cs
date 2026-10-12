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
        var indexRestart = args.Contains("--index-smoke-restart");
        var indexSmoke = args.Contains("--index-smoke") || indexRestart;
        var resourceTest = args.Contains("--resource-test");
        var systemRestart = args.Contains("--system-smoke-restart");
        var systemSmoke = args.Contains("--system-smoke") || systemRestart;
        var diagnostic = smoke || resourceTest || sceneSmoke || indexSmoke || systemSmoke;
        using var instance = new SingleInstanceService(systemSmoke ? "M23.Smoke" : indexSmoke ? "M22.Smoke" : sceneSmoke ? "M21.Smoke" : resourceTest ? "ResourceTest" : smoke ? "M15.Smoke" : "DesktopHub");
        if (!instance.IsPrimaryInstance)
        {
            var activated = instance.NotifyExistingInstanceAsync().GetAwaiter().GetResult();
            if (!activated && !diagnostic) MessageBox.Show("Desktop Hub 已在运行，暂时无法传递唤出请求。请使用系统托盘打开。", "Desktop Hub");
            return activated ? 0 : 1;
        }
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
                var directory = systemSmoke ? Path.Combine(AppContext.BaseDirectory, "m23-smoke-data") : indexSmoke ? Path.Combine(AppContext.BaseDirectory, "m22-smoke-data") : sceneSmoke ? Path.Combine(AppContext.BaseDirectory, "m21-smoke-data") : resourceTest ? Path.Combine(AppContext.BaseDirectory, "resource-data") : smoke ? Path.Combine(AppContext.BaseDirectory, "m15-smoke-data") :
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopHub");
                // Explicit diagnostic only: never write resource-test settings into the user's profile.
                if (resourceTest) ResourceUsageProbe.Prepare(directory);
                if (smoke && !smokeRestart) SmokeTests.Prepare(directory);
                if (sceneSmoke && !sceneRestart) SceneSmokeTests.Prepare(directory);
                if (indexSmoke && !indexRestart) ResourceSmokeTests.Prepare(directory);
                if (systemSmoke && !systemRestart) SystemSmokeTests.Prepare(directory);
                host = new HubApplication(directory, diagnostic, systemSmoke);
                await host.StartAsync(loginStartup: args.Contains("--startup"));
                instance.StartListening(() => _ = app.Dispatcher.BeginInvoke(() => _ = host.OpenFromSystemAsync("activation")));
                if (smoke) await SmokeTests.RunAsync(host, smokeRestart);
                else if (sceneSmoke) await SceneSmokeTests.RunAsync(host, sceneRestart);
                else if (indexSmoke) await ResourceSmokeTests.RunAsync(host, indexRestart);
                else if (systemSmoke) await SystemSmokeTests.RunAsync(host, systemRestart);
                else if (resourceTest) await ResourceUsageProbe.RunAsync(host);
            }
            catch (Exception e)
            {
                host?.Log("Startup", e.ToString());
                if (!diagnostic) MessageBox.Show("Desktop Hub 无法启动。请确认 .NET Desktop Runtime 8 和 WebView2 Runtime 已安装。\n" + e.Message, "Desktop Hub");
                else File.WriteAllText(Path.Combine(AppContext.BaseDirectory, systemSmoke ? "m23-smoke-error.txt" : indexSmoke ? "m22-smoke-error.txt" : sceneSmoke ? "m21-smoke-error.txt" : resourceTest ? "resource-error.txt" : "m15-smoke-error.txt"), e.ToString());
                app.Shutdown(1);
            }
        };
        app.Exit += (_, _) => { try { host?.Dispose(); } catch (Exception e) { host?.Log("Dispose", e.ToString()); } };
        return app.Run();
    }
}
