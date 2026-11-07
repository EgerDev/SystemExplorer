namespace SystemExplorer.Core;

public static class InventoryLayout
{
    public const double PreferredMinimumMetricWidth = 96;
    public const double MaximumMetricWidth = 168;
    public const double MaximumMetricShare = 0.6;

    /// <summary>Width of each visible metric column; bounded by the available width so the name column always keeps at least 40%.</summary>
    public static double MetricWidth(double available, int visibleColumns)
    {
        if (visibleColumns <= 0 || !double.IsFinite(available) || available <= 0) return 0;
        var width = Math.Clamp(available * 0.17, PreferredMinimumMetricWidth, MaximumMetricWidth);
        var budget = available * MaximumMetricShare / visibleColumns;
        return Math.Floor(Math.Min(width, budget));
    }
}

public static class CounterSampling
{
    public static readonly TimeSpan MaximumRateInterval = TimeSpan.FromSeconds(2);

    /// <summary>True when a stateful counter capture is a warmup or averages rates over an interval too long to describe the present.</summary>
    public static bool NeedsFreshInterval(bool isWarmup, TimeSpan? interval) =>
        isWarmup || interval is not { } value || value <= TimeSpan.Zero || value > MaximumRateInterval;
}
