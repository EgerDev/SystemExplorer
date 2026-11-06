namespace SystemExplorer.Core;

public sealed record ProcessHistoryPoint(DateTimeOffset At, bool Observed, bool BreakBefore,
    ulong? PrivateBytes, ulong? WorkingSetBytes, ulong? PrivateWorkingSetBytes, double? CpuPercent);

public sealed record ProcessHistorySnapshot(ProcessIdentity Identity, string Name, IReadOnlyList<ProcessHistoryPoint> Points);

public sealed class SelectedProcessHistory
{
    public const int MaximumLifetimes = 8;
    public const int MaximumPoints = 180;
    private readonly Dictionary<ProcessIdentity, Entry> entries = [];
    private long selectionOrder;
    public bool HasHistory => entries.Count > 0;

    public IReadOnlyList<ProcessHistorySnapshot> Snapshots => entries.Values
        .OrderByDescending(e => e.Order).Select(e => new ProcessHistorySnapshot(e.Identity, e.Name, e.Points.ToArray())).ToArray();

    public void Track(ProcessIdentity identity, MonitorSnapshot snapshot)
    {
        if (!identity.IsStable || identity.Pid <= 0) return;
        var process = snapshot.Processes.FirstOrDefault(p => p.Identity == identity);
        if (process is null) return;
        if (!entries.TryGetValue(identity, out var entry))
        {
            if (entries.Count >= MaximumLifetimes)
                entries.Remove(entries.Values.MinBy(e => e.Order)!.Identity);
            entry = new Entry(identity, process.Name);
            entries.Add(identity, entry);
        }
        entry.Order = ++selectionOrder;
        Add(entry, snapshot.CapturedAt, process);
    }

    public void Observe(MonitorSnapshot snapshot)
    {
        var current = snapshot.Processes.Where(p => p.Identity.IsStable).GroupBy(p => p.Identity)
            .ToDictionary(g => g.Key, g => g.First());
        foreach (var entry in entries.Values)
            Add(entry, snapshot.CapturedAt, current.GetValueOrDefault(entry.Identity));
    }

    public void BreakContinuity()
    {
        foreach (var entry in entries.Values) entry.BreakPending = true;
    }

    private static void Add(Entry entry, DateTimeOffset at, ProcessSnapshot? process)
    {
        if (entry.LastSample is { } latest && at <= latest) return;
        bool gap = entry.BreakPending || entry.LastSample is { } last && at - last > TimeSpan.FromSeconds(10);
        entry.LastSample = at;
        // One missing observation marks the transition; absence is not proof of exit.
        if (process is null && entry.Points.LastOrDefault() is { Observed: false }) return;
        bool wasMissing = entry.Points.LastOrDefault() is { Observed: false };
        entry.Points.Enqueue(new(at, process is not null, gap || wasMissing,
            process?.PrivateBytes, process?.WorkingSetBytes, process?.PrivateWorkingSetBytes, process?.CpuPercent));
        entry.BreakPending = false;
        while (entry.Points.Count > MaximumPoints) entry.Points.Dequeue();
    }

    private sealed class Entry(ProcessIdentity identity, string name)
    {
        public ProcessIdentity Identity { get; } = identity;
        public string Name { get; } = name;
        public long Order { get; set; }
        public DateTimeOffset? LastSample { get; set; }
        public bool BreakPending { get; set; }
        public Queue<ProcessHistoryPoint> Points { get; } = new();
    }
}
