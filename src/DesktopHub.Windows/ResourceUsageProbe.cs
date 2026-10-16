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
    private static DateTimeOffset Started;
    private static readonly List<object> Latencies = new();
    private static long processStart;
    private static double preManagedMilliseconds;
    internal static void MarkProcessStart()
    {
        processStart = Stopwatch.GetTimestamp();
        using var process = Process.GetCurrentProcess();
        preManagedMilliseconds = Math.Max(0, (DateTime.UtcNow - process.StartTime.ToUniversalTime()).TotalMilliseconds);
    }
    private static string Version => typeof(HubApplication).Assembly.GetName().Version?.ToString() ?? "unknown";

    public static void Prepare(string directory)
    {
        Directory.CreateDirectory(Output);
        // The fresh summary acts as the completion marker; never reuse an older completed run.
        var summary = Path.Combine(Output, "summary.json");
        if (File.Exists(summary)) File.Delete(summary);
        var area = SystemParameters.WorkArea;
        var store = new SettingsStore(directory);
        var settings = new AppSettings
        {
            Theme = "dark", ReducedMotion = false, Transparency = true, HoverEnabled = false,
            WorkspaceBounds = new WindowBounds { Left = area.Left + 32, Top = area.Top + 24, Width = area.Width - 64, Height = area.Height - 48 }
        };
        // This opt-in diagnostic is also copied to the immutable product source's
        // supplementary build; reflection keeps its older settings schema intact.
        var mode = Environment.GetEnvironmentVariable("DESKTOP_HUB_PROBE_MODE") ?? "quick";
        if (mode is not ("quick" or "lightweight")) throw new ArgumentException("Unknown diagnostic performance mode.");
        var modeProperty = typeof(AppSettings).GetProperty("PerformanceMode");
        if (mode == "lightweight" && modeProperty is null) throw new InvalidOperationException("The baseline has no lightweight mode.");
        modeProperty?.SetValue(settings, mode);
        store.Save(settings);
    }

    public static async Task RunAsync(HubApplication host)
    {
        try
        {
            Started = DateTimeOffset.UtcNow; Clock.Restart(); Samples.Clear(); Summaries.Clear(); Latencies.Clear();
            await WaitFrontend(host.Mini);
            if (processStart != 0) Latencies.Add(new { scenario = "process-cold", summary = ResourceProbeStatistics.Latency([preManagedMilliseconds + Stopwatch.GetElapsedTime(processStart).TotalMilliseconds]) });
            var dpi = VisualTreeHelper.GetDpi(host.Mini);
            Write("environment.json", new
            {
                version = Version, startedUtc = Started, hostProcessId = Environment.ProcessId,
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
                latencyDefinition = "Stopwatch through fresh state and two renderer frames; nearest-rank p95 only when n>=20; OS file cache not cleared",
                processStartDefinition = "OS creation UTC to managed entry offset, followed by monotonic Stopwatch to UI ready",
                notMeasured = new[] { "GPU memory/utilization", "disk IO", "network", "exact physical RAM", "cold surface p95 (one first construction per run)" }
            });
            if (Environment.GetEnvironmentVariable("DESKTOP_HUB_PROBE_LATENCY_ONLY") == "1")
            {
                var firstSurface = Stopwatch.GetTimestamp();
                await host.OpenDockAsync(); await WaitFrontend(host.Dock!);
                Latencies.Add(new { scenario = "dock-surface-cold", summary = ResourceProbeStatistics.Latency([Stopwatch.GetElapsedTime(firstSurface).TotalMilliseconds]) });
                host.CloseDock(); firstSurface = Stopwatch.GetTimestamp();
                await host.OpenWorkspaceAsync("home"); await WaitFrontend(host.Workspace!);
                Latencies.Add(new { scenario = "workspace-surface-cold", summary = ResourceProbeStatistics.Latency([Stopwatch.GetElapsedTime(firstSurface).TotalMilliseconds]) });
                Write("latency.json", Latencies);
                Write("summary.json", new { version = Version, startedUtc = Started, finishedUtc = DateTimeOffset.UtcNow, latencies = Latencies, scope = "new process and first native surface creation; retained WebView2 disk profile and OS file cache" });
                host.Exit(); return;
            }
            await MeasureStage(host, "mini-first", false, false);
            var surfaceStart = Stopwatch.GetTimestamp();
            await host.OpenDockAsync(); await WaitFrontend(host.Dock!);
            Latencies.Add(new { scenario = "dock-surface-cold", summary = ResourceProbeStatistics.Latency([Stopwatch.GetElapsedTime(surfaceStart).TotalMilliseconds]) });
            await MeasureStage(host, "dock-open", true, false);
            host.CloseDock(); surfaceStart = Stopwatch.GetTimestamp();
            await host.OpenWorkspaceAsync("home"); await WaitFrontend(host.Workspace!);
            Latencies.Add(new { scenario = "workspace-surface-cold", summary = ResourceProbeStatistics.Latency([Stopwatch.GetElapsedTime(surfaceStart).TotalMilliseconds]) });
            await MeasureStage(host, "workspace-home", false, true);
            host.CloseWorkspace();
            // Both modes use the actual product's sixty-second hide deadline and
            // the same subsequent settling interval. No forced GC or trim.
            Progress("mini-after-use: waiting real hidden 60 seconds"); await Task.Delay(60000);
            await MeasureStage(host, "mini-after-use", false, false);
            var reopenStart = Stopwatch.GetTimestamp(); var wasReleased = host.Workspace is null;
            await host.OpenWorkspaceAsync("home"); await WaitFrontend(host.Workspace!);
            Latencies.Add(new { scenario = wasReleased ? "workspace-released-reopen" : "workspace-retained-reopen", summary = ResourceProbeStatistics.Latency([Stopwatch.GetElapsedTime(reopenStart).TotalMilliseconds]) });
            var hot = new List<double>();
            for (var index = 0; index < 20; index++)
            {
                host.CloseWorkspace(); await Task.Delay(200);
                reopenStart = Stopwatch.GetTimestamp(); await host.OpenWorkspaceAsync("home"); await WaitFrontend(host.Workspace!);
                hot.Add(Stopwatch.GetElapsedTime(reopenStart).TotalMilliseconds);
            }
            Latencies.Add(new { scenario = "workspace-retained-hot", summary = ResourceProbeStatistics.Latency(hot), rawMilliseconds = hot });
            Write("latency.json", Latencies);
            Write("samples.json", Samples);
            Write("summary.json", new { version = Version, startedUtc = Started, finishedUtc = DateTimeOffset.UtcNow, scenarios = Summaries, latencies = Latencies });
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
        await window.Browser.ExecuteScriptAsync("window.__resourceProbeReady=false;window.DesktopHubNative.bridge.request('app.getState',{}).then(state=>{window.DesktopHubDesktop.applyHostState(state);requestAnimationFrame(()=>requestAnimationFrame(()=>{window.__resourceProbeReady=true}))})");
        for (var i = 0; i < 200; i++)
        {
            if (await window.Browser.ExecuteScriptAsync("Boolean(window.DesktopHubDesktop?.getState().hostState && window.__resourceProbeReady)") == "true") return;
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
            dockReleased = host.Dock is null, workspaceReleased = host.Workspace is null,
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
        // The current host deliberately hides Mini while either expanded surface is visible.
        var mini = host.Store.Current.MiniEnabled && !dock && !workspace;
        if (host.Mini.IsVisible != mini || (host.Dock?.IsVisible == true) != dock || (host.Workspace?.IsVisible == true) != workspace)
            throw new InvalidOperationException($"Window visibility changed during measurement: expected Mini={mini}, Dock={dock}, Workspace={workspace}; actual Mini={host.Mini.IsVisible}, Dock={host.Dock?.IsVisible == true}, Workspace={host.Workspace?.IsVisible == true}.");
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
