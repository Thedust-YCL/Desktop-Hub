using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using DesktopHub.Core;

namespace DesktopHub.Windows;

// Opt-in diagnostics. Uses the real host/windows, with an isolated settings/profile directory.
// No screenshots, forced GC, working-set trimming, external program launches or DevTools.
internal static class ResourceUsageProbe
{
    private const int SettleSeconds = 15, SampleCount = 60;
    private static readonly JsonSerializerOptions Json = new(HubApplication.Json) { WriteIndented = true };
    private static readonly List<Sample> Samples = new();
    private static readonly List<object> Summaries = new();
    private static readonly string Output = Path.Combine(AppContext.BaseDirectory, "resource-evidence");
    private static readonly Stopwatch Clock = new();
    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow;

    public static void Prepare(string directory)
    {
        Directory.CreateDirectory(Output);
        // The fresh summary acts as the completion marker; never reuse an older completed run.
        var summary = Path.Combine(Output, "summary.json");
        if (File.Exists(summary)) File.Delete(summary);
        var area = SystemParameters.WorkArea;
        var store = new SettingsStore(directory);
        store.Save(new AppSettings
        {
            Theme = "dark", ReducedMotion = false, Transparency = true, HoverEnabled = false,
            WorkspaceBounds = new WindowBounds { Left = area.Left + 32, Top = area.Top + 24, Width = area.Width - 64, Height = area.Height - 48 }
        });
    }

    public static async Task RunAsync(HubApplication host)
    {
        try
        {
            Clock.Start();
            await WaitFrontend(host.Mini);
            var dpi = VisualTreeHelper.GetDpi(host.Mini);
            Write("environment.json", new
            {
                version = "M1 / 0.1.0", startedUtc = Started, hostProcessId = Environment.ProcessId,
                os = RuntimeInformation.OSDescription, osVersion = Environment.OSVersion.Version.ToString(),
                framework = RuntimeInformation.FrameworkDescription, logicalProcessorCount = Environment.ProcessorCount,
                webView2Runtime = host.Mini.Browser.CoreWebView2.Environment.BrowserVersionString,
                workArea = new { SystemParameters.WorkArea.Width, SystemParameters.WorkArea.Height },
                dpi = new { dpi.DpiScaleX, dpi.DpiScaleY }, settings = host.Store.Current,
                executableSha256 = Hash("DesktopHub.Windows.exe"), assemblySha256 = Hash("DesktopHub.Windows.dll"),
                coreSha256 = Hash("DesktopHub.Core.dll"), settleSeconds = SettleSeconds, sampleCount = SampleCount,
                intervalTargetSeconds = 1, profile = "isolated resource-data, reusable WebView2 disk cache",
                attribution = "Environment.GetProcessInfos() plus msedgewebview2 descendants (includes crashpad), deduplicated PID/start time",
                cpuDefinition = "100 * sum(delta processor seconds) / actual interval seconds / logical processors",
                notMeasured = new[] { "GPU memory/utilization", "disk IO", "network", "cold-start latency", "exact physical RAM" }
            });
            await MeasureStage(host, "mini-first", false, false);
            await host.OpenDockAsync(); await WaitFrontend(host.Dock!);
            await MeasureStage(host, "dock-open", true, false);
            host.CloseDock(); await host.OpenWorkspaceAsync("home"); await WaitFrontend(host.Workspace!);
            await MeasureStage(host, "workspace-home", false, true);
            host.CloseWorkspace();
            await MeasureStage(host, "mini-after-use", false, false);
            Write("samples.json", Samples);
            Write("summary.json", new { version = "M1", startedUtc = Started, finishedUtc = DateTimeOffset.UtcNow, scenarios = Summaries });
            var validCpuIntervals = Samples.Count(s => s.Index > 0 && s.CpuMachinePercent.HasValue);
            File.WriteAllText(Path.Combine(Output, "summary.txt"), $"4 real-window scenarios completed; {Samples.Count} memory frames, {validCpuIntervals}/{4 * SampleCount} valid CPU intervals. See summary.json for skipped intervals.\n", Encoding.UTF8);
            Progress("completed"); host.Exit();
        }
        catch (Exception e)
        {
            Write("samples.json", Samples);
            File.WriteAllText(Path.Combine(Output, "error.txt"), e.ToString());
            host.Log("ResourceProbe", e.ToString()); Application.Current.Shutdown(1);
        }
    }

    private static async Task WaitFrontend(HubWindow window)
    {
        for (var i = 0; i < 200; i++)
        {
            if (await window.Browser.ExecuteScriptAsync("Boolean(window.DesktopHubDesktop?.getState().hostState)") == "true") return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Frontend handshake: " + window.Surface);
    }

    private static async Task MeasureStage(HubApplication host, string scenario, bool dockVisible, bool workspaceVisible)
    {
        Progress(scenario + ": settling"); await Task.Delay(SettleSeconds * 1000);
        VerifyWindows(host, dockVisible, workspaceVisible);
        var previous = Capture(host, scenario, 0, null);
        var baseline = previous.ElapsedSeconds;
        Samples.Add(previous);
        var stage = new List<Sample>();
        Progress(scenario + ": sampling");
        for (var index = 1; index <= SampleCount; index++)
        {
            // Schedule against the original baseline instead of accumulating capture delays.
            var remaining = baseline + index - Clock.Elapsed.TotalSeconds;
            if (remaining > 0) await Task.Delay(TimeSpan.FromSeconds(remaining));
            VerifyWindows(host, dockVisible, workspaceVisible);
            var sample = Capture(host, scenario, index, previous);
            Samples.Add(sample); stage.Add(sample); previous = sample;
        }
        if (stage.Any(s => s.Errors.Count != 0)) throw new InvalidOperationException("Incomplete process read in " + scenario);
        var cpu = stage.Where(s => s.CpuMachinePercent.HasValue).ToArray();
        if (cpu.Length == 0) throw new InvalidOperationException("No valid CPU interval in " + scenario);
        var totalSeconds = cpu.Sum(s => s.IntervalSeconds!.Value);
        var cpuSeconds = cpu.Sum(s => s.CpuSeconds!.Value);
        Summaries.Add(new
        {
            scenario, startedUtc = stage[0].TimestampUtc, endedUtc = stage[^1].TimestampUtc,
            settleSeconds = SettleSeconds, memorySamples = SampleCount + 1, cpuIntervals = cpu.Length,
            skippedCpuIntervals = SampleCount - cpu.Length, measuredSeconds = stage[^1].ElapsedSeconds - baseline,
            hostPrivateMiBMean = stage.Average(s => s.HostPrivateBytes) / 1048576.0,
            webViewPrivateMiBMean = stage.Average(s => s.WebViewPrivateBytes) / 1048576.0,
            totalPrivateMiBMean = stage.Average(s => s.TotalPrivateBytes) / 1048576.0,
            totalPrivateMiBMin = stage.Min(s => s.TotalPrivateBytes) / 1048576.0,
            totalPrivateMiBPeak = stage.Max(s => s.TotalPrivateBytes) / 1048576.0,
            workingSetSumMiBMean = stage.Average(s => s.WorkingSetSumBytes) / 1048576.0,
            cpuMachinePercentMean = 100 * cpuSeconds / totalSeconds / Environment.ProcessorCount,
            cpuMachinePercentPeak = cpu.Max(s => s.CpuMachinePercent),
            cpuOneCorePercentMean = 100 * cpuSeconds / totalSeconds,
            cpuSeconds, validCpuSeconds = totalSeconds,
            processCountMin = stage.Min(s => s.Processes.Count), processCountMax = stage.Max(s => s.Processes.Count),
            windows = host.Windows.Select(w => new { w.Surface, hwnd = w.Handle.ToInt64(), w.IsVisible, w.Width, w.Height, w.Topmost, suspended = w.Browser.CoreWebView2.IsSuspended }).ToArray()
        });
        Write("samples.json", Samples); Write("progress-summary.json", Summaries);
        Progress(scenario + ": completed");
    }

    private static void VerifyWindows(HubApplication host, bool dock, bool workspace)
    {
        if (!host.Mini.IsVisible || (host.Dock?.IsVisible == true) != dock || (host.Workspace?.IsVisible == true) != workspace)
            throw new InvalidOperationException("Window visibility changed during measurement.");
        foreach (var window in host.Windows)
            if (!NativeMethods.IsWindow(window.Handle)) throw new InvalidOperationException("Missing native HWND: " + window.Surface);
    }

    private static Sample Capture(HubApplication host, string scenario, int index, Sample? previous)
    {
        var sample = new Sample { Scenario = scenario, Index = index, TimestampUtc = DateTimeOffset.UtcNow };
        var kinds = host.Mini.Browser.CoreWebView2.Environment.GetProcessInfos()
            .ToDictionary(info => checked((int)info.ProcessId), info => info.Kind.ToString());
        // GetProcessInfos omits crashpad. Include only msedgewebview2 descendants of those known processes.
        foreach (var pid in Descendants(kinds.Keys)) kinds.TryAdd(pid, "CrashpadOrOtherWebViewChild");
        kinds[Environment.ProcessId] = "Host";
        foreach (var (pid, kind) in kinds.OrderBy(p => p.Key))
        {
            try
            {
                using var process = Process.GetProcessById(pid); process.Refresh();
                var point = new ProcessPoint(pid, process.StartTime.ToUniversalTime().Ticks, kind,
                    process.PrivateMemorySize64, process.WorkingSet64, process.TotalProcessorTime.TotalSeconds);
                sample.Processes.Add(point);
                if (kind == "Host") sample.HostPrivateBytes += point.PrivateBytes;
                else sample.WebViewPrivateBytes += point.PrivateBytes;
                sample.WorkingSetSumBytes += point.WorkingSetBytes;
            }
            catch (Exception e) { sample.Errors.Add($"PID {pid}: {e.GetType().Name}"); }
        }
        sample.ElapsedSeconds = Clock.Elapsed.TotalSeconds;
        if (previous is not null)
        {
            sample.IntervalSeconds = sample.ElapsedSeconds - previous.ElapsedSeconds;
            var before = previous.Processes.ToDictionary(p => (p.ProcessId, p.StartUtcTicks));
            if (sample.Errors.Count == 0 && previous.Errors.Count == 0 && before.Count == sample.Processes.Count &&
                sample.Processes.All(p => before.ContainsKey((p.ProcessId, p.StartUtcTicks))))
            {
                sample.CpuSeconds = sample.Processes.Sum(p => p.ProcessorSeconds - before[(p.ProcessId, p.StartUtcTicks)].ProcessorSeconds);
                sample.CpuMachinePercent = 100 * sample.CpuSeconds / sample.IntervalSeconds / Environment.ProcessorCount;
            }
        }
        return sample;
    }

    private static IEnumerable<int> Descendants(IEnumerable<int> roots)
    {
        var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new nint(-1)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var entries = new List<ProcessEntry>();
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32First(snapshot, ref entry)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            do { entries.Add(entry); } while (Process32Next(snapshot, ref entry));
        }
        finally { CloseHandle(snapshot); }
        var owned = roots.ToHashSet();
        bool changed;
        do
        {
            changed = false;
            foreach (var entry in entries)
                if (owned.Contains((int)entry.ParentProcessId) && string.Equals(entry.ExeFile, "msedgewebview2.exe", StringComparison.OrdinalIgnoreCase))
                    changed |= owned.Add((int)entry.ProcessId);
        } while (changed);
        return owned;
    }

    private static string Hash(string filename) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, filename))));
    private static void Write(string name, object value) => File.WriteAllText(Path.Combine(Output, name), JsonSerializer.Serialize(value, Json));
    private static void Progress(string message) => Write("progress.json", new { message, timestampUtc = DateTimeOffset.UtcNow });

    private sealed class Sample
    {
        public string Scenario { get; init; } = "";
        public int Index { get; init; }
        public DateTimeOffset TimestampUtc { get; init; }
        public double ElapsedSeconds { get; set; }
        public long HostPrivateBytes { get; set; }
        public long WebViewPrivateBytes { get; set; }
        public long TotalPrivateBytes => HostPrivateBytes + WebViewPrivateBytes;
        public long WorkingSetSumBytes { get; set; }
        public double? IntervalSeconds { get; set; }
        public double? CpuSeconds { get; set; }
        public double? CpuMachinePercent { get; set; }
        public List<ProcessPoint> Processes { get; } = new();
        public List<string> Errors { get; } = new();
    }
    private sealed record ProcessPoint(int ProcessId, long StartUtcTicks, string Kind, long PrivateBytes, long WorkingSetBytes, double ProcessorSeconds);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId, Threads, ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExeFile;
    }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);
    [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32First(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool Process32Next(nint snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(nint handle);
}
