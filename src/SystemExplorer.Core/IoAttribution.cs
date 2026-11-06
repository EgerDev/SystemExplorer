namespace SystemExplorer.Core;

public enum IoAttributionStatus { Completed, Cancelled, RequiresAdministrator, SessionUnavailable, Failed, Truncated }
public enum IoAttributionKind { NetworkSend, NetworkReceive, DiskRead, DiskWrite }

public readonly record struct IoAttributionEvent(int Pid, IoAttributionKind Kind, ulong Bytes);
public sealed record IoAttributionProcess(int Pid, ProcessIdentity? Identity, InspectionStatus IdentityStatus,
    string? Name, ulong NetworkSendBytes, ulong NetworkReceiveBytes, ulong DiskReadBytes, ulong DiskWriteBytes, ulong EventCount);
public sealed record IoAttributionCapture(DateTimeOffset StartedAt, TimeSpan ActualDuration, IoAttributionStatus Status,
    string Detail, long EventsLost, IReadOnlyList<IoAttributionProcess> Processes);

public interface IIoAttributionCapture
{
    Task<IoAttributionCapture> CaptureAsync(TimeSpan duration, CancellationToken token);
}

/// <summary>Single-consumer, bounded byte aggregation. No addresses, paths or event payloads are retained.</summary>
public sealed class IoAttributionAggregator
{
    public const int MaximumPids = 4096;
    private readonly Dictionary<int, ulong[]> counters = [];
    public bool IsTruncated { get; private set; }
    public static TimeSpan ClampDuration(TimeSpan duration) => TimeSpan.FromTicks(Math.Clamp(duration.Ticks,
        TimeSpan.TicksPerSecond, 60 * TimeSpan.TicksPerSecond));

    public void Add(IoAttributionEvent observation)
    {
        if (observation.Pid < 0 || !Enum.IsDefined(observation.Kind)) return;
        if (!counters.TryGetValue(observation.Pid, out var values))
        {
            if (counters.Count == MaximumPids) { IsTruncated = true; return; }
            counters.Add(observation.Pid, values = new ulong[5]);
        }
        var index = (int)observation.Kind;
        values[index] = SaturatingAdd(values[index], observation.Bytes);
        values[4] = SaturatingAdd(values[4], 1);
    }

    public IReadOnlyList<IoAttributionProcess> GetRows(Func<int, (ProcessIdentity? Identity, string? Name)> resolve)
        => counters.OrderBy(pair => pair.Key).Select(pair =>
        {
            var (identity, name) = resolve(pair.Key);
            if (identity is not { IsStable: true } || identity.Value.Pid != pair.Key) identity = null;
            var v = pair.Value;
            return new IoAttributionProcess(pair.Key, identity, identity is null ? InspectionStatus.Partial : InspectionStatus.Available,
                name, v[0], v[1], v[2], v[3], v[4]);
        }).ToArray();

    public IoAttributionStatus CompletionStatus => IsTruncated ? IoAttributionStatus.Truncated : IoAttributionStatus.Completed;
    private static ulong SaturatingAdd(ulong a, ulong b) => ulong.MaxValue - a < b ? ulong.MaxValue : a + b;
}
