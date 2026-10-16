namespace DesktopHub.Windows;

internal sealed record ProbeLatencySummary(int Count, double? MeanMilliseconds, double? MaximumMilliseconds, double? P95Milliseconds);

internal static class ResourceProbeStatistics
{
    public static ProbeLatencySummary Latency(IEnumerable<double> milliseconds)
    {
        var samples = milliseconds.ToArray();
        if (samples.Any(value => !double.IsFinite(value) || value < 0))
            throw new ArgumentException("Latency samples must be finite and nonnegative.", nameof(milliseconds));
        Array.Sort(samples);
        return samples.Length == 0 ? new(0, null, null, null) : new(samples.Length, samples.Average(), samples[^1],
            samples.Length >= 20 ? samples[(int)Math.Ceiling(samples.Length * .95) - 1] : null);
    }
}
