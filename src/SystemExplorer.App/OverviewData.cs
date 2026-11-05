using SystemExplorer.Core;

namespace SystemExplorer.App;

public enum ConsumerRanking { PrivateCommit, Cpu }
public readonly record struct TrendCoordinate(double X, double Y);
public sealed record ResourceTrend(IReadOnlyList<IReadOnlyList<TrendCoordinate>> Segments, double Maximum, int SampleCount);
public sealed record OverviewReading(
    DateTimeOffset CapturedAt,
    double? CpuPercent,
    double? PhysicalMemoryPercent,
    ulong? PhysicalUsedBytes,
    ulong? PhysicalTotalBytes,
    ulong? PhysicalAvailableBytes,
    ulong? SystemCacheBytes,
    ulong? CommitTotalBytes,
    ulong? CommitLimitBytes,
    double? DiskReadBytesPerSecond,
    double? DiskWriteBytesPerSecond,
    double? DiskTotalBytesPerSecond,
    int? ObservedEndpointCount,
    DateTimeOffset? RelationshipsCapturedAt,
    IReadOnlyList<ProcessGroup> Consumers,
    IReadOnlyList<Insight> Insights,
    ResourceTrend CpuTrend,
    ResourceTrend MemoryTrend,
    ResourceTrend DiskTrend);

public sealed class OverviewData
{
    private const int Capacity = 180;
    private readonly Queue<Point> points = new();
    private MonitorSnapshot? latest;
    private sealed record Point(DateTimeOffset At, double? Cpu, double? Memory, double? Disk);

    public void Observe(MonitorSnapshot snapshot)
    {
        if (latest is not null && snapshot.CapturedAt <= latest.CapturedAt) return;
        latest = snapshot;
        points.Enqueue(new(snapshot.CapturedAt, ValidPercent(snapshot.TotalCpuPercent), MemoryPercent(snapshot.Memory), DiskTotal(snapshot)));
        while (points.Count > Capacity) points.Dequeue();
    }

    public OverviewReading? Create(RelationshipSnapshot? relationships, DiagnosticsHistory history,
        ConsumerRanking ranking = ConsumerRanking.PrivateCommit, string search = "")
    {
        if (latest is not { } snapshot) return null;
        var query = search.Trim();
        var groups = ProcessGrouping.Create(snapshot.Processes).Where(g => query.Length == 0 || g.Members.Any(p =>
            $"{p.Name} {p.Identity.Pid} {p.ExecutablePath} {p.UserName}".Contains(query, StringComparison.OrdinalIgnoreCase)));
        groups = ranking == ConsumerRanking.Cpu
            ? groups.OrderByDescending(g => g.CpuPercent ?? -1).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key.Pid)
            : groups.OrderByDescending(g => g.PrivateBytes.HasValue).ThenByDescending(g => g.PrivateBytes).ThenBy(g => g.Name, StringComparer.OrdinalIgnoreCase).ThenBy(g => g.Key.Pid);
        var memory = snapshot.Memory;
        var freshRelationships = relationships is not null && Math.Abs((snapshot.CapturedAt - relationships.CapturedAt).TotalSeconds) <= 10;
        var samples = points.ToArray();
        return new(snapshot.CapturedAt, ValidPercent(snapshot.TotalCpuPercent), MemoryPercent(memory),
            memory.PhysicalUsedBytes, memory.PhysicalTotalBytes, memory.PhysicalAvailableBytes,
            memory.SystemCacheBytes, memory.CommitTotalBytes, memory.CommitLimitBytes,
            ValidRate(snapshot.DiskReadBytesPerSecond), ValidRate(snapshot.DiskWriteBytesPerSecond), DiskTotal(snapshot),
            freshRelationships ? relationships!.Connections.Count : null, relationships?.CapturedAt,
            groups.Take(50).ToArray(), history.Explain(snapshot, freshRelationships ? relationships : null),
            Trend(samples, p => p.Cpu, 100), Trend(samples, p => p.Memory, 100), Trend(samples, p => p.Disk, null));
    }

    private static double? ValidPercent(double? value) => value is >= 0 and <= 100 && double.IsFinite(value.Value) ? value : null;
    private static double? ValidRate(double? value) => value is >= 0 && double.IsFinite(value.Value) ? value : null;
    private static double? MemoryPercent(MemorySnapshot memory) => memory.PhysicalUsedBytes is { } used && memory.PhysicalTotalBytes is > 0
        ? used * 100d / memory.PhysicalTotalBytes.Value : null;
    private static double? DiskTotal(MonitorSnapshot snapshot) => ValidRate(snapshot.DiskReadBytesPerSecond) is { } read &&
        ValidRate(snapshot.DiskWriteBytesPerSecond) is { } write ? ValidRate(read + write) : null;

    private static ResourceTrend Trend(Point[] samples, Func<Point, double?> select, double? fixedMaximum)
    {
        var maximum = fixedMaximum ?? Math.Max(1, samples.Select(select).Max() ?? 0);
        var segments = new List<IReadOnlyList<TrendCoordinate>>();
        List<TrendCoordinate>? segment = null;
        var seconds = samples.Length > 1 ? (samples[^1].At - samples[0].At).TotalSeconds : 0;
        var validSamples = 0;
        for (var i = 0; i < samples.Length; i++)
        {
            var value = select(samples[i]);
            if (value is null || i > 0 && (samples[i].At - samples[i - 1].At).TotalSeconds > 10) segment = null;
            if (value is null) continue;
            if (segment is null) { segment = new(); segments.Add(segment); }
            segment.Add(new(seconds > 0 ? (samples[i].At - samples[0].At).TotalSeconds / seconds : 0.5, value.Value / maximum));
            validSamples++;
        }
        return new(segments, maximum, validSamples);
    }
}
