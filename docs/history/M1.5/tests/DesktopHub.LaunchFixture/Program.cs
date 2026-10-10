using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        var app = new Application();
        var window = new Window { Title = "Desktop Hub Controlled Launch Fixture", Width = 420, Height = 180,
            Content = new TextBlock { Text = "Controlled test program is running.", Margin = new Thickness(24) } };
        window.Loaded += (_, _) =>
        {
            var marker = JsonSerializer.Serialize(new { processId = Environment.ProcessId, executable = Environment.ProcessPath,
                arguments = args, workingDirectory = Environment.CurrentDirectory, startedUtc = DateTime.UtcNow });
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "fixture-started.json"), marker);
            File.WriteAllText(Path.Combine(AppContext.BaseDirectory, $"fixture-started-{Environment.ProcessId}.json"), marker);
        };
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(180) };
        timer.Tick += (_, _) => app.Shutdown(); timer.Start();
        app.Run(window);
    }
}
