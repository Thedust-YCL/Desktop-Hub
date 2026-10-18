using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using DesktopHub.Core;
using DesktopHub.Windows;
using Microsoft.Web.WebView2.Core;

internal static class Program
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly List<string> Checks = [];
    private static string output = "";

    [STAThread]
    private static int Main(string[] args)
    {
        output = Path.GetFullPath(args.Single());
        Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        HubApplication? host = null;
        app.Startup += async (_, _) =>
        {
            try
            {
                var data = Path.Combine(output, "data");
                var executable = Process.GetCurrentProcess().MainModule!.FileName;
                new SettingsStore(data).Save(new AppSettings { Theme = "dark", HoverEnabled = false, ReducedMotion = true, OnboardingCompleted = true,
                    AppEntries = [new() { Id = Guid.NewGuid().ToString("D"), Name = "中文应用 Desktop Hub", Target = executable, WorkingDirectory = Path.GetDirectoryName(executable)! }] });
                host = new HubApplication(data, smoke: true);
                await host.StartAsync();
                await host.OpenWorkspaceAsync("settings");
                var window = host.Workspace!;
                await Wait(window, "Boolean(DesktopHubDesktop.getState().hostState && DesktopHubSystem)");
                Check("Existing settings default to Chinese", host.Store.Current.Language == "zh-CN");
                Check("Chinese brand and native title", (await Js(window, "document.querySelector('.brand').textContent.includes('桌面工作台')")).GetBoolean() && window.Title.StartsWith("桌面工作台"));
                await Capture(window, "settings-zh.png");
                await Js(window, "Hub.actions['sys-language']({value:'en-US'})");
                await Wait(window, "document.documentElement.lang==='en-US' && !DesktopHubSystem.getState().busy");
                Check("Settings action saves English and updates native title", host.Store.Current.Language == "en-US" && window.Title.StartsWith("Desktop Hub"));
                Check("English persisted to disk", new SettingsStore(data).Load().Language == "en-US");
                await Js(window, "Hub.showModal('设置','<p>user title</p>')");
                Check("User-owned modal titles stay opaque", (await Js(window, "document.querySelector('#dialog-title').textContent")).GetString() == "设置");
                await Js(window, "Hub.closeModal()");
                await Capture(window, "settings-en.png");
                var originalWidth = window.Width; var originalHeight = window.Height;
                window.Width = 640; window.Height = 480;
                await Task.Delay(150);
                Check("English settings fit the minimum window width", (await Js(window, "document.querySelector('.settings-content').scrollWidth <= document.querySelector('.settings-content').clientWidth + 1")).GetBoolean());
                await Capture(window, "settings-en-compact.png");
                window.Width = originalWidth; window.Height = originalHeight;
                var texts = new Dictionary<string, string>();
                foreach (var page in new[] { "home", "scenes", "library", "tags", "favorites", "search", "settings", "activity", "projects" })
                {
                    await Js(window, $"Hub.navigate({JsonSerializer.Serialize(page)})");
                    await Task.Delay(250);
                    texts[page] = (await Js(window, "document.body.innerText")).GetString()!;
                    if (page == "home")
                    {
                        Check("User application name remains intact in English", texts[page].Contains("中文应用 Desktop Hub"));
                        await Capture(window, "home-en.png");
                    }
                }
                File.WriteAllText(Path.Combine(output, "page-texts-en.json"), JsonSerializer.Serialize(texts, Json));
                Check("Built-in category labels are translated", !texts["tags"].Contains("主题") && !texts["tags"].Contains("时期") && !texts["tags"].Contains("自定义"));
                await host.OpenDockAsync();
                await Wait(host.Dock!, "document.documentElement.lang==='en-US'");
                await Wait(host.Mini, "document.documentElement.lang==='en-US'");
                Check("All three surfaces receive English", true);
                await Js(window, "Hub.navigate('settings');Hub.actions['settings-section']({dataset:{section:'appearance'}})");
                await Js(window, "Hub.actions['sys-language']({value:'zh-CN'})");
                await Wait(window, "document.documentElement.lang==='zh-CN' && !DesktopHubSystem.getState().busy");
                await Wait(host.Dock!, "document.documentElement.lang==='zh-CN'");
                host.CloseDock();
                await Wait(host.Mini, "document.documentElement.lang==='zh-CN'");
                Check("Switching back restores Chinese brand", (await Js(window, "document.querySelector('.brand').textContent.includes('桌面工作台')")).GetBoolean());
                Check("User data survives both switches", host.Store.Current.AppEntries.Single().Name == "中文应用 Desktop Hub");
                File.WriteAllText(Path.Combine(output, "checks.json"), JsonSerializer.Serialize(Checks, Json));
                host.Exit();
            }
            catch (Exception error)
            {
                File.WriteAllText(Path.Combine(output, "error.txt"), error.ToString());
                host?.Dispose();
                app.Shutdown(1);
            }
        };
        app.Exit += (_, _) => host?.Dispose();
        return app.Run();
    }

    private static async Task<JsonElement> Js(HubWindow window, string script) => JsonSerializer.Deserialize<JsonElement>(await window.Browser.ExecuteScriptAsync(script));
    private static async Task Wait(HubWindow window, string expression)
    {
        for (var index = 0; index < 160; index++) { if ((await Js(window, expression)).ValueKind == JsonValueKind.True) return; await Task.Delay(50); }
        throw new TimeoutException(expression);
    }
    private static void Check(string name, bool passed) { if (!passed) throw new InvalidOperationException(name); Checks.Add(name); }
    private static async Task Capture(HubWindow window, string name)
    {
        using var file = File.Create(Path.Combine(output, name));
        await window.Browser.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
    }
}
