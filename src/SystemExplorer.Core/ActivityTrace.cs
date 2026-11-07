using System.Collections.ObjectModel;
using System.Globalization;

namespace SystemExplorer.Core;

[Flags]
public enum TraceCategories { None = 0, Process = 1, Thread = 2, Image = 4, File = 8, Registry = 16, Network = 32, All = 63 }
public enum TraceOperation { ProcessStart, ProcessStop, ImageLoad, FileCreate, FileRead, FileWrite, FileDelete, FileRename, RegistryOpen, RegistryCreate, RegistrySetValue, RegistryDeleteKey, RegistryDeleteValue, NetConnect, NetAccept, NetSend, NetReceive, NetDisconnect }
public enum TraceStatus { NotStarted, RequiresAdministrator, SessionUnavailable, Running, Completed, Cancelled, Partial, Failed }

public sealed record TraceOptions
{
    public TraceCategories Categories { get; }
    public TimeSpan Duration { get; }
    public int EventCap { get; }
    public bool ExcludeSelf { get; }
    public TraceOptions(TraceCategories Categories = TraceCategories.All, TimeSpan? Duration = null, int EventCap = 200_000, bool ExcludeSelf = true)
    {
        this.Categories = Categories & TraceCategories.All;
        this.Duration = TimeSpan.FromTicks(Math.Clamp((Duration ?? TimeSpan.FromMinutes(2)).Ticks, TimeSpan.FromSeconds(5).Ticks, TimeSpan.FromMinutes(10).Ticks));
        this.EventCap = Math.Clamp(EventCap, 10_000, 500_000);
        this.ExcludeSelf = ExcludeSelf;
    }
}

public readonly record struct TraceEvent(long Sequence, long TimestampUtc, int Pid, int Tid, int ProcessIndex,
    TraceOperation Operation, string? Path = null, ulong Bytes = 0, string? Detail = null)
{
    public static TraceCategories Category(TraceOperation op) => op switch
    {
        TraceOperation.ProcessStart or TraceOperation.ProcessStop => TraceCategories.Process,
        TraceOperation.ImageLoad => TraceCategories.Image,
        >= TraceOperation.FileCreate and <= TraceOperation.FileRename => TraceCategories.File,
        >= TraceOperation.RegistryOpen and <= TraceOperation.RegistryDeleteValue => TraceCategories.Registry,
        >= TraceOperation.NetConnect and <= TraceOperation.NetDisconnect => TraceCategories.Network,
        _ => TraceCategories.None
    };
}

public sealed class TraceRingBuffer
{
    private readonly TraceEvent[] events;
    private readonly object gate = new();
    private int next, count;
    private long dropped;
    public TraceRingBuffer(int capacity) { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity); events = new TraceEvent[capacity]; }
    public int Count { get { lock (gate) return count; } }
    public long Dropped { get { lock (gate) return dropped; } }
    public void Add(TraceEvent item)
    {
        lock (gate) { events[next] = item; next = (next + 1) % events.Length; if (count == events.Length) dropped++; else count++; }
    }
    public TraceEvent[] Snapshot(int maximum = int.MaxValue)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximum);
        lock (gate)
        {
            var length = Math.Min(count, maximum);
            var result = new TraceEvent[length];
            var start = (next - length + events.Length) % events.Length;
            for (int i = 0; i < length; i++) result[i] = events[(start + i) % events.Length];
            return result;
        }
    }
}

// Owned by the recorder's ingestion lock, as are the process table and string pool.
public sealed class TraceStringPool
{
    private readonly Dictionary<string, string> strings = new(StringComparer.Ordinal);
    public int Capacity { get; }
    public int Count => strings.Count;
    public bool Capped { get; private set; }
    public TraceStringPool(int capacity = 100_000) { ArgumentOutOfRangeException.ThrowIfNegative(capacity); Capacity = capacity; }
    public string? Intern(string? value)
    {
        if (value is null) return null;
        if (strings.TryGetValue(value, out var existing)) return existing;
        if (strings.Count == Capacity) { Capped = true; return value; }
        strings.Add(value, value); return value;
    }
}

public sealed record TraceProcess(int Index, int Pid, long? CreateTimeFileTime, int ParentPid, int ParentIndex,
    string? ImageName, string? ImagePath, DateTimeOffset? StartUtc, DateTimeOffset? ExitUtc = null,
    int? ExitCode = null, bool Seeded = false, long ThreadsStarted = 0, long ThreadsStopped = 0);
public readonly record struct TraceProcessSeed(int Pid, int ParentPid, long? CreateTimeFileTime, string? ImageName, string? ImagePath = null);

public sealed class TraceProcessTable
{
    private readonly List<TraceProcess> processes = [];
    private readonly Dictionary<int, int> latest = [];
    public int Capacity { get; }
    public bool Truncated { get; private set; }
    public TraceProcessTable(int capacity = 16_384) { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity); Capacity = capacity; }
    public int Resolve(int pid) => latest.GetValueOrDefault(pid, -1);
    public void Seed(IEnumerable<TraceProcessSeed> seeds)
    {
        foreach (var s in seeds) Start(s, null, true);
        for (int i = 0; i < processes.Count; i++) processes[i] = processes[i] with { ParentIndex = FindParent(processes[i]) };
    }
    private int FindParent(TraceProcess child)
    {
        if (child.CreateTimeFileTime is not long created) return -1;
        for (int i = processes.Count - 1; i >= 0; i--)
        {
            var p = processes[i];
            if (p.Index != child.Index && p.Pid == child.ParentPid && p.CreateTimeFileTime is long parentCreated && parentCreated <= created
                && (p.ExitUtc is null || p.ExitUtc.Value.UtcDateTime.ToFileTimeUtc() >= created)) return i;
        }
        return -1;
    }
    public int Start(TraceProcessSeed seed, DateTimeOffset? at, bool seeded = false)
    {
        if (processes.Count == Capacity) { Truncated = true; latest.Remove(seed.Pid); return -1; }
        var index = processes.Count;
        var p = new TraceProcess(index, seed.Pid, seed.CreateTimeFileTime, seed.ParentPid, -1, seed.ImageName, seed.ImagePath, seeded ? null : at, Seeded: seeded);
        processes.Add(p);
        processes[index] = p with { ParentIndex = FindParent(p) };
        latest[seed.Pid] = index;
        return index;
    }
    // Keep the exited instance mapped until a new start: late events still belong to it.
    public void Stop(int pid, DateTimeOffset at, int? exitCode)
    {
        int index = Resolve(pid);
        if (index >= 0) processes[index] = processes[index] with { ExitUtc = at, ExitCode = exitCode };
    }
    public void Thread(int pid, bool started)
    {
        int index = Resolve(pid);
        if (index < 0) return;
        var p = processes[index];
        processes[index] = started ? p with { ThreadsStarted = p.ThreadsStarted + 1 } : p with { ThreadsStopped = p.ThreadsStopped + 1 };
    }
    public TraceProcess[] Snapshot() => processes.ToArray();
}

public readonly record struct TraceTreeRow(TraceProcess Process, int Depth);
public static class TraceTree
{
    public static IReadOnlyList<TraceTreeRow> Build(IReadOnlyList<TraceProcess> processes)
    {
        var children = processes.GroupBy(p => p.ParentIndex).ToDictionary(g => g.Key, g => g.OrderBy(p => p.CreateTimeFileTime ?? long.MinValue).ThenBy(p => p.Index).ToArray());
        var rows = new List<TraceTreeRow>(processes.Count);
        var seen = new HashSet<int>();
        var stack = new Stack<(TraceProcess Process, int Depth)>();
        void Walk(TraceProcess root)
        {
            stack.Push((root, 0));
            while (stack.TryPop(out var entry))
            {
                if (!seen.Add(entry.Process.Index)) continue;
                rows.Add(new(entry.Process, entry.Depth));
                if (children.TryGetValue(entry.Process.Index, out var list))
                    for (int i = list.Length - 1; i >= 0; i--) stack.Push((list[i], entry.Depth + 1));
            }
        }
        foreach (var p in processes.Where(p => p.ParentIndex < 0 || p.ParentIndex >= processes.Count).OrderBy(p => p.CreateTimeFileTime ?? long.MinValue)) Walk(p);
        foreach (var p in processes) if (!seen.Contains(p.Index)) Walk(p);
        return rows.AsReadOnly();
    }
    public static HashSet<int> Descendants(IReadOnlyList<TraceProcess> processes, int index)
    {
        var result = new HashSet<int> { index };
        var children = processes.ToLookup(p => p.ParentIndex);
        var pending = new Queue<int>(); pending.Enqueue(index);
        while (pending.TryDequeue(out int parent)) foreach (var p in children[parent]) if (result.Add(p.Index)) pending.Enqueue(p.Index);
        return result;
    }
}

public sealed record TraceFilter(int? ProcessIndex = null, bool IncludeDescendants = true, TraceCategories Categories = TraceCategories.All,
    string? Text = null, DateTimeOffset? FromUtc = null, DateTimeOffset? ToUtc = null)
{
    public int[] Match(IReadOnlyList<TraceEvent> events, IReadOnlyList<TraceProcess> processes)
    {
        var selected = ProcessIndex is int index ? IncludeDescendants ? TraceTree.Descendants(processes, index) : new HashSet<int> { index } : null;
        var text = Text?.Trim();
        var processMatches = new bool[processes.Count];
        bool Contains(string? value) => value?.Contains(text!, StringComparison.OrdinalIgnoreCase) == true;
        if (!string.IsNullOrEmpty(text)) for (int i = 0; i < processes.Count; i++) processMatches[i] = Contains(processes[i].ImageName) || Contains(processes[i].Pid.ToString(CultureInfo.InvariantCulture));
        long from = FromUtc?.UtcTicks ?? long.MinValue, to = ToUtc?.UtcTicks ?? long.MaxValue;
        var matches = new List<int>();
        for (int i = 0; i < events.Count; i++)
        {
            var e = events[i];
            if ((Categories & TraceEvent.Category(e.Operation)) == 0 || e.TimestampUtc < from || e.TimestampUtc > to || (selected is not null && !selected.Contains(e.ProcessIndex))) continue;
            if (!string.IsNullOrEmpty(text) && !Contains(e.Path) && !Contains(e.Detail) && !(e.ProcessIndex >= 0 && e.ProcessIndex < processMatches.Length && processMatches[e.ProcessIndex]) && !Contains(e.Pid.ToString(CultureInfo.InvariantCulture))) continue;
            matches.Add(i);
        }
        return matches.ToArray();
    }
}

public sealed record TraceResult(DateTimeOffset StartedUtc, DateTimeOffset StoppedUtc, TraceOptions Options, TraceStatus Status, string Detail,
    long EventsLost, long BufferDropped, long Malformed, long UnresolvedNames, bool ProcessTableTruncated, bool CapsHit,
    IReadOnlyList<TraceEvent> Events, IReadOnlyList<TraceProcess> Processes, IReadOnlyDictionary<TraceCategories, long> CategoryCounts,
    long EventsSeen = 0, long ExcludedSelf = 0, long ProcessesStarted = 0);
public sealed record TraceLiveSnapshot(TimeSpan Elapsed, long EventsSeen, int Stored, long BufferDropped, long ProcessesStarted,
    long Malformed, long UnresolvedNames, long ExcludedSelf, IReadOnlyDictionary<TraceCategories, long> CategoryCounts, IReadOnlyList<TraceEvent> Tail);

public sealed class TraceRecorderState
{
    private readonly object gate = new();
    private readonly TraceRingBuffer ring;
    private readonly TraceProcessTable processes;
    private readonly TraceStringPool strings;
    private readonly Dictionary<TraceCategories, long> counts = [];
    private readonly int ownPid;
    private long seen, malformed, unresolved, excluded, started;
    private bool externalCaps;
    private TraceResult? final;
    public TraceOptions Options { get; }
    public DateTimeOffset StartedUtc { get; }
    public TraceRecorderState(TraceOptions options, DateTimeOffset startedUtc, int ownPid = -1, int processCap = 16_384, int stringCap = 100_000)
    { Options = options; StartedUtc = startedUtc; this.ownPid = ownPid; ring = new(options.EventCap); processes = new(processCap); strings = new(stringCap); }
    public void Seed(IEnumerable<TraceProcessSeed> seeds)
    {
        lock (gate) { if (final is not null) return; processes.Seed(seeds.Select(s => s with { ImageName = strings.Intern(s.ImageName), ImagePath = strings.Intern(s.ImagePath) })); }
    }
    public void Record(DateTimeOffset at, int pid, int tid, TraceOperation operation, string? path = null, ulong bytes = 0, string? detail = null,
        TraceProcessSeed? processStart = null, int? exitCode = null)
    {
        lock (gate)
        {
            if (final is not null) return;
            seen++;
            if (Options.ExcludeSelf && pid == ownPid) { excluded++; return; }
            if (!Enum.IsDefined(operation)) { malformed++; return; }
            if (operation == TraceOperation.ProcessStart)
            {
                var seed = processStart ?? new TraceProcessSeed(pid, -1, null, null);
                if (seed.Pid != pid) { malformed++; return; }
                processes.Start(seed with { ImageName = strings.Intern(seed.ImageName), ImagePath = strings.Intern(seed.ImagePath) }, at); started++;
            }
            if (operation == TraceOperation.ProcessStop) processes.Stop(pid, at, exitCode);
            var category = TraceEvent.Category(operation);
            if ((Options.Categories & category) == 0) return;
            counts[category] = counts.GetValueOrDefault(category) + 1;
            ring.Add(new(seen, at.UtcTicks, pid, tid, processes.Resolve(pid), operation, strings.Intern(path), bytes, strings.Intern(detail)));
        }
    }
    public void RecordThread(int pid, bool isStart)
    {
        lock (gate)
        {
            if (final is not null) return;
            seen++;
            if (Options.ExcludeSelf && pid == ownPid) { excluded++; return; }
            if ((Options.Categories & TraceCategories.Thread) == 0) return;
            processes.Thread(pid, isStart); counts[TraceCategories.Thread] = counts.GetValueOrDefault(TraceCategories.Thread) + 1;
        }
    }
    public void ReportDiagnostics(long malformedSkipped = 0, long unresolvedNames = 0, bool capsHit = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(malformedSkipped); ArgumentOutOfRangeException.ThrowIfNegative(unresolvedNames);
        lock (gate) { if (final is not null) return; malformed += malformedSkipped; unresolved += unresolvedNames; externalCaps |= capsHit; }
    }
    private IReadOnlyDictionary<TraceCategories, long> Counts() => new ReadOnlyDictionary<TraceCategories, long>(new Dictionary<TraceCategories, long>(counts));
    public TraceLiveSnapshot Live(DateTimeOffset now, int tail = 100)
    {
        lock (gate) return new(now - StartedUtc, seen, ring.Count, ring.Dropped, started, malformed, unresolved, excluded, Counts(), Array.AsReadOnly(ring.Snapshot(Math.Clamp(tail, 0, 100))));
    }
    public TraceResult Complete(DateTimeOffset stoppedUtc, TraceStatus status = TraceStatus.Completed, string detail = "", long eventsLost = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(eventsLost);
        lock (gate)
        {
            if (final is not null) return final;
            var events = ring.Snapshot();
            bool capped = externalCaps || strings.Capped || processes.Truncated || TraceProcessSummary.HasCappedSets(events);
            if (status == TraceStatus.Completed && (eventsLost > 0 || ring.Dropped > 0 || capped || malformed > 0 || unresolved > 0)) status = TraceStatus.Partial;
            return final = new(StartedUtc, stoppedUtc, Options, status, detail, eventsLost, ring.Dropped, malformed, unresolved, processes.Truncated, capped,
                Array.AsReadOnly(events), Array.AsReadOnly(processes.Snapshot()), Counts(), seen, excluded, started);
        }
    }
}
