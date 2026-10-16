using DesktopHub.Windows;

var failures = 0;
void Check(string name, Action test) { try { test(); Console.WriteLine("PASS " + name); } catch (Exception ex) { failures++; Console.WriteLine("FAIL " + name + ": " + ex.Message); } }
void Assert(bool condition) { if (!condition) throw new Exception("Unexpected diagnostic conclusion"); }
Check("p95 uses nearest rank on twenty independent samples", () => {
    var result = ResourceProbeStatistics.Latency(Enumerable.Range(1, 20).Reverse().Select(x => (double)x));
    Assert(result.Count == 20 && result.P95Milliseconds == 19 && result.MeanMilliseconds == 10.5);
});
Check("small sample cannot claim p95", () => {
    var result = ResourceProbeStatistics.Latency([41, 61]);
    Assert(result.Count == 2 && result.P95Milliseconds is null && result.MaximumMilliseconds == 61);
});
Check("invalid intervals are rejected instead of silently creating zero timing", () => {
    foreach (var value in new[] { double.NaN, double.PositiveInfinity, -1d }) {
        var rejected = false; try { ResourceProbeStatistics.Latency([value]); } catch (ArgumentException) { rejected = true; }
        Assert(rejected);
    }
});
return failures == 0 ? 0 : 1;
