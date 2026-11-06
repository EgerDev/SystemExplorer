namespace SystemExplorer.Core;

public enum ProcessLifecycleKind { RunningAtFirstObservation, Started, NoLongerObserved }

public sealed record ProcessLifecycleEvent(DateTimeOffset At, ProcessLifecycleKind Kind, ProcessIdentity Identity, string Name,
    string? ExecutablePath, int? ParentPid, DateTimeOffset? CreatedAt, DateTimeOffset FirstObserved, DateTimeOffset LastObserved,
    int Observations, ulong? PeakPrivateBytes, ulong? PeakWorkingSetBytes, double? PeakCpuPercent, bool AfterGap);

/// <summary>Session-only start/disappearance timeline for stable (PID, creation FILETIME) identities.</summary>
public sealed class ProcessLifecycleTimeline
{
    public const int MaximumEvents = 2000;
    public const int MaximumTracked = 4096;
    private static readonly TimeSpan GapThreshold = TimeSpan.FromSeconds(10);
    private readonly Queue<ProcessLifecycleEvent> events = new();
    private Dictionary<ProcessIdentity, Lifetime> live = [];
    private DateTimeOffset? latest;
    private bool breakPending;

    public IReadOnlyList<ProcessLifecycleEvent> Events => events.ToArray();
    public int UnstableIdentitiesExcluded { get; private set; }
    public int TrackingOverflow { get; private set; }

    public void BreakContinuity() => breakPending = true;

    public void Observe(MonitorSnapshot snapshot)
    {
        if (latest is { } last && snapshot.CapturedAt <= last) return;
        var first = latest is null;
        var gap = breakPending || latest is { } previous && snapshot.CapturedAt - previous > GapThreshold;
        latest = snapshot.CapturedAt;
        breakPending = false;
        UnstableIdentitiesExcluded = snapshot.Processes.Count(p => !p.Identity.IsStable || p.Identity.Pid <= 0);
        var current = snapshot.Processes.Where(p => p.Identity.IsStable && p.Identity.Pid > 0)
            .GroupBy(p => p.Identity).ToDictionary(g => g.Key, g => g.First());
        var next = new Dictionary<ProcessIdentity, Lifetime>();
        foreach (var (identity, process) in current)
        {
            if (!live.TryGetValue(identity, out var lifetime))
            {
                if (live.Count + next.Count >= MaximumTracked * 2 || next.Count >= MaximumTracked) { TrackingOverflow++; continue; }
                lifetime = new Lifetime(identity, process, snapshot.CapturedAt);
                Add(lifetime.ToEvent(snapshot.CapturedAt, first ? ProcessLifecycleKind.RunningAtFirstObservation : ProcessLifecycleKind.Started, gap));
            }
            lifetime.Observe(process, snapshot.CapturedAt);
            next[identity] = lifetime;
        }
        foreach (var (identity, lifetime) in live)
            if (!next.ContainsKey(identity))
                Add(lifetime.ToEvent(snapshot.CapturedAt, ProcessLifecycleKind.NoLongerObserved, gap));
        live = next;
    }

    private void Add(ProcessLifecycleEvent item)
    {
        events.Enqueue(item);
        while (events.Count > MaximumEvents) events.Dequeue();
    }

    private sealed class Lifetime(ProcessIdentity identity, ProcessSnapshot process, DateTimeOffset at)
    {
        private readonly string name = process.Name;
        private readonly string? path = process.ExecutablePath;
        private readonly int? parent = process.ParentPid;
        private readonly DateTimeOffset firstObserved = at;
        private DateTimeOffset lastObserved = at;
        private int observations;
        private ulong? peakPrivate, peakWorkingSet;
        private double? peakCpu;

        public void Observe(ProcessSnapshot snapshot, DateTimeOffset when)
        {
            lastObserved = when;
            observations++;
            if (snapshot.PrivateBytes is { } p && (peakPrivate is null || p > peakPrivate)) peakPrivate = p;
            if (snapshot.WorkingSetBytes is { } w && (peakWorkingSet is null || w > peakWorkingSet)) peakWorkingSet = w;
            if (snapshot.CpuPercent is { } c && double.IsFinite(c) && (peakCpu is null || c > peakCpu)) peakCpu = c;
        }

        public ProcessLifecycleEvent ToEvent(DateTimeOffset when, ProcessLifecycleKind kind, bool afterGap) => new(when, kind, identity, name, path, parent,
            CreatedAt(identity.CreationTimeFileTime), firstObserved, lastObserved, observations, peakPrivate, peakWorkingSet, peakCpu, afterGap);

        private static DateTimeOffset? CreatedAt(long? fileTime)
        {
            try { return fileTime is > 0 ? DateTimeOffset.FromFileTime(fileTime.Value) : null; }
            catch (ArgumentOutOfRangeException) { return null; }
        }
    }
}
