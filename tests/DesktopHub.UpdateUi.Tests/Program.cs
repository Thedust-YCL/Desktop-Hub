using System.IO;
using System.Text.Json;
using System.Windows;
using DesktopHub.Core;
using DesktopHub.Windows;
using Microsoft.Web.WebView2.Core;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var output = Path.GetFullPath(args[0]);
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        HubApplication? host = null;
        app.Startup += async (_, _) => {
            try {
                var data = Path.Combine(output, "data");
                new SettingsStore(data).Save(new AppSettings { OnboardingCompleted = true, HoverEnabled = false, ReducedMotion = true });
                host = new HubApplication(data, smoke: true);
                await host.StartAsync();
                await host.OpenWorkspaceAsync("settings");
                var window = host.Workspace!;
                for (var index = 0; index < 160; index++) {
                    if (await Js(window, "Boolean(window.DesktopHubSystem && DesktopHubDesktop.getState().hostState)") == "true") break;
                    await Task.Delay(50);
                }
                await Js(window, "Hub.navigate('settings');Hub.actions['settings-section']({dataset:{section:'system'}})");
                await Assert(window, "document.body.innerText.includes('软件更新')", "Chinese update section");
                await Assert(window, "document.body.innerText.includes('请安装正式安装版')", "Development build explanation");
                await Capture(window, output, "updates-zh.png");
                await Js(window, "DesktopHubDesktop.applyHostState({...DesktopHubDesktop.getState().hostState,updates:{currentVersion:'0.3.1',availableVersion:'0.3.2',stage:'ready',progress:100}})");
                await Js(window, "Hub.actions['sys-update-install']()");
                await Assert(window, "document.body.innerText.includes('你的本机数据会保留')", "Explicit restart confirmation");
                await Capture(window, output, "updates-confirm.png");
                await Js(window, "Hub.closeModal(); DesktopHubDesktop.applyHostState({...DesktopHubDesktop.getState().hostState,settings:{...DesktopHubDesktop.getState().hostState.settings,language:'en-US'},updates:{currentVersion:'0.3.1',availableVersion:'0.3.2',stage:'downloading',progress:42}})");
                window.Width = 640; window.Height = 480;
                await Task.Delay(150);
                await Assert(window, "document.body.innerText.includes('Software updates') && document.querySelector('[role=progressbar]').getAttribute('aria-valuenow')==='42'", "English progress state");
                await Assert(window, "document.querySelector('.settings-content').scrollWidth <= document.querySelector('.settings-content').clientWidth+1", "Minimum width fits");
                await Capture(window, output, "updates-en-compact.png");
                File.WriteAllText(Path.Combine(output, "result.json"), "{\"passed\":5,\"failed\":0}");
                host.Exit();
            } catch (Exception error) {
                File.WriteAllText(Path.Combine(output, "error.txt"), error.ToString());
                host?.Dispose(); app.Shutdown(1);
            }
        };
        app.Exit += (_, _) => host?.Dispose();
        return app.Run();
    }
    private static Task<string> Js(HubWindow window, string script) => window.Browser.ExecuteScriptAsync(script);
    private static async Task Assert(HubWindow window, string expression, string name) {
        if (await Js(window, expression) != "true") throw new Exception(name);
    }
    private static async Task Capture(HubWindow window, string output, string name) {
        using var file = File.Create(Path.Combine(output, name));
        await window.Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
    }
}
