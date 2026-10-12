using System.IO;
using System.IO.Pipes;
using System.Text;
using DesktopHub.Windows;

internal static class Program
{
    private static int passed;
    private static int failed;

    [STAThread]
    private static int Main()
    {
        Run("hotkey parse normalizes safe independent combos", () =>
        {
            Check(GlobalHotkeyService.TryParse("alt + ctrl + d", out var binding, out _) && binding!.DisplayName == "Ctrl+Alt+D", "letter normalization");
            Check(GlobalHotkeyService.TryParse("Ctrl+Shift+F02", out binding, out _) && binding!.DisplayName == "Ctrl+Shift+F2", "function normalization");
        });
        Run("hotkey rejects invalid or reserved combos", () =>
        {
            foreach (var text in new[] { "Win+Ctrl+D", "Ctrl+D", "Ctrl+Alt+F12", "Ctrl+Alt+Delete", "Ctrl+Shift+Escape", "Ctrl+Ctrl+D", "Ctrl+Alt+", "Ctrl+Alt++D", "Ctrl+Alt+F25", "" })
                Check(!GlobalHotkeyService.TryParse(text, out _, out var error) && error is not null, text);
        });
        Run("hotkey apply is transactional and retains old registrations on conflict", () =>
        {
            var backend = new FakeHotkeyBackend();
            using var service = new GlobalHotkeyService(_ => { }, backend);
            Check(service.Apply("Ctrl+Alt+D", "Ctrl+Alt+W").Success, "initial pair");
            var previous = backend.Registered.ToArray();
            backend.Blocked.Add('Y');
            var conflict = service.Apply("Ctrl+Alt+X", "Ctrl+Alt+Y");
            Check(!conflict.Success && conflict.Error!.Contains("原有效快捷键已保留"), "conflict error");
            Check(service.DockHotkey == "Ctrl+Alt+D" && service.WorkspaceHotkey == "Ctrl+Alt+W", "effective settings retained");
            Check(previous.Length == backend.Registered.Count && previous.All(pair => backend.Registered.GetValueOrDefault(pair.Key) == pair.Value), "staged registration removed, old IDs unchanged");
            Check(!service.Apply("Ctrl+Alt+D", "Alt+Ctrl+D").Success && backend.Registered.Count == 2, "duplicate rejected");
        });
        Run("hotkey repeated config, swapping and disposal do not leak registrations", () =>
        {
            var backend = new FakeHotkeyBackend();
            var service = new GlobalHotkeyService(_ => { }, backend);
            Check(service.Apply("Ctrl+Alt+D", "Ctrl+Alt+W").Success, "initial");
            var calls = backend.RegisterCalls;
            Check(service.Apply("Ctrl+Alt+D", "Ctrl+Alt+W").Success && backend.RegisterCalls == calls, "idempotence");
            Check(service.Apply("Ctrl+Alt+W", "Ctrl+Alt+D").Success && backend.RegisterCalls == calls, "swap reuses existing IDs");
            Check(service.Apply("Ctrl+Alt+X", "Ctrl+Alt+Y").Success && backend.Registered.Count == 2, "old pair released");
            service.Dispose();
            service.Dispose();
            Check(backend.Registered.Count == 0 && !service.Apply("Ctrl+Alt+D", "Ctrl+Alt+W").Success, "disposal");
        });
        Run("foreground blocklist matches executable names without broad substring matching", () =>
        {
            Check(ForegroundProtectionService.ProcessMatches(" GAME.exe ", "game"), "exe normalization");
            Check(!ForegroundProtectionService.ProcessMatches("game", "gameLauncher"), "exact name");
            Check(!ForegroundProtectionService.ProcessMatches("C:\\game.exe", "game"), "no arbitrary path matching");
            Check(!ForegroundProtectionService.ProcessMatches(null, "game"), "null safe");
        });
        Run("foreground monitor geometry covers fullscreen and rejects ordinary or invalid bounds", () =>
        {
            Check(ForegroundProtectionService.CoversMonitor(0, 0, 1920, 1080, 0, 0, 1920, 1080), "borderless fullscreen");
            Check(ForegroundProtectionService.CoversMonitor(-8, -8, 1928, 1088, 0, 0, 1920, 1080), "fullscreen overhang");
            Check(!ForegroundProtectionService.CoversMonitor(0, 0, 1920, 1040, 0, 0, 1920, 1080), "maximized workarea browser");
            Check(!ForegroundProtectionService.CoversMonitor(0, 0, 1920, 1080, 0, 0, 0, 0), "invalid monitor");
        });
        Run("single instance fixed-command IPC rejects malformed requests and survives disconnect", () => TestPipeAsync().GetAwaiter().GetResult());
        Run("single instance identity remains stable across working directories and scope labels", () =>
        {
            var identity = "LifecycleCwd-" + Guid.NewGuid();
            using var primary = new SingleInstanceService(identity);
            Check(primary.IsPrimaryInstance, "primary");
            var initial = Environment.CurrentDirectory;
            try
            {
                Environment.CurrentDirectory = Path.Combine(initial, "tests");
                using var second = new SingleInstanceService(identity);
                Check(!second.IsPrimaryInstance, "cwd cannot create second host");
                using var isolated = new SingleInstanceService(identity + "-isolated");
                Check(isolated.IsPrimaryInstance, "diagnostic identity isolated");
            }
            finally { Environment.CurrentDirectory = initial; }
        });
        Console.WriteLine($"Lifecycle: {passed} passed, {failed} failed. No real hotkey, tray, desktop icon or registry modification performed.");
        return failed == 0 ? 0 : 1;
    }

    private static async Task TestPipeAsync()
    {
        var identity = "LifecyclePipe-" + Guid.NewGuid();
        using var primary = new SingleInstanceService(identity, allowForegroundGrant: false);
        using var second = new SingleInstanceService(identity, allowForegroundGrant: false);
        Check(primary.IsPrimaryInstance && !second.IsPrimaryInstance, "exclusive gate");
        var notifications = 0;
        primary.StartListening(() => Interlocked.Increment(ref notifications));
        Check(await second.NotifyExistingInstanceAsync(), $"valid request acknowledged; client={second.LastError}; server={primary.LastError}");
        Check(second.LastServerProcessId == Environment.ProcessId, "authenticated pipe peer PID discovered");
        Check(notifications == 1, "one activation");
        foreach (var bad in new[] { "WORKSPACE\ntrailing arbitrary data", "COMMAND!!\n", "" })
        {
            await using var client = new NamedPipeClientStream(".", primary.DiagnosticPipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(timeout.Token);
            if (bad.Length > 0) { await client.WriteAsync(Encoding.ASCII.GetBytes(bad), timeout.Token); await client.FlushAsync(timeout.Token); }
        }
        Check(await second.NotifyExistingInstanceAsync(), "listener survives rejected/disconnected clients");
        Check(notifications == 2, "malformed inputs caused no activation");
        primary.Dispose();
        Check(!await second.NotifyExistingInstanceAsync(), "disposed primary cannot acknowledge activation");
    }
    private static void Run(string name, Action test)
    {
        try { test(); passed++; Console.WriteLine("PASS " + name); }
        catch (Exception error) { failed++; Console.WriteLine("FAIL " + name + ": " + error); }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private sealed class FakeHotkeyBackend : IHotkeyRegistrationBackend
    {
        public Dictionary<int, (uint Modifiers, uint Key)> Registered { get; } = new();
        public HashSet<uint> Blocked { get; } = new();
        public int RegisterCalls { get; private set; }
        public bool Register(IntPtr hwnd, int id, uint modifiers, uint virtualKey, out int error)
        {
            RegisterCalls++;
            error = 0;
            if (Blocked.Contains(virtualKey) || Registered.Values.Any(value => value == (modifiers, virtualKey))) { error = 1409; return false; }
            Registered.Add(id, (modifiers, virtualKey));
            return true;
        }
        public bool Unregister(IntPtr hwnd, int id) => Registered.Remove(id);
    }
}
