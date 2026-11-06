namespace SystemExplorer.Core;

public sealed record HistoryPoint(DateTimeOffset At, ulong? PhysicalUsedBytes, ulong? CommitBytes, double? CpuPercent, bool BreakBefore = false);
public sealed record Insight(string Title, string Evidence, string Kind = "Observation");

public sealed class DiagnosticsHistory
{
    private const int Capacity = 180;
    private readonly Queue<HistoryPoint> system = new();
    private readonly Dictionary<ProcessIdentity, Queue<(DateTimeOffset At, ulong Bytes)>> memory = new();
    private Dictionary<string, ProcessIdentity> singleInstances = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<(DateTimeOffset At, string Key, string Path)> replacements = new();
    private DateTimeOffset? latest;
    private bool breakBefore;

    public void BreakContinuity() => breakBefore = true;

    public IReadOnlyList<HistoryPoint> System => system.ToArray();

    public void Add(MonitorSnapshot snapshot)
    {
        if (latest is { } last && snapshot.CapturedAt <= last) return;
        if (breakBefore || latest is { } previous && snapshot.CapturedAt - previous > TimeSpan.FromSeconds(10))
        {
            breakBefore = true;
            singleInstances.Clear();
            replacements.Clear();
            memory.Clear();
        }
        latest = snapshot.CapturedAt;
        system.Enqueue(new(snapshot.CapturedAt, snapshot.Memory.PhysicalUsedBytes, snapshot.Memory.CommitTotalBytes, snapshot.TotalCpuPercent, breakBefore));
        breakBefore = false;
        while (system.Count > Capacity) system.Dequeue();
        var currentInstances = new Dictionary<string, ProcessIdentity>(StringComparer.OrdinalIgnoreCase);
        var candidates = snapshot.Processes.Where(p => p.Identity.IsStable && p.ExecutablePath is not null && p.UserSid is not null && p.SessionId.HasValue)
            .GroupBy(p => $"{p.ExecutablePath}\0{p.UserSid}\0{p.SessionId}", StringComparer.OrdinalIgnoreCase);
        foreach (var group in candidates)
        {
            if (group.Count() != 1) continue;
            var process = group.Single();
            currentInstances[group.Key] = process.Identity;
            if (singleInstances.TryGetValue(group.Key, out var previousIdentity) && previousIdentity != process.Identity)
                replacements.Enqueue((snapshot.CapturedAt, group.Key, process.ExecutablePath!));
        }
        singleInstances = currentInstances;
        while (replacements.Count > Capacity || replacements.Count > 0 && replacements.Peek().At < system.Peek().At) replacements.Dequeue();
        var live = snapshot.Processes.Where(p => p.Identity.IsStable).Select(p => p.Identity).ToHashSet();
        foreach (var key in memory.Keys.Where(k => !live.Contains(k)).ToArray()) memory.Remove(key);
        foreach (var process in snapshot.Processes.Where(p => p.Identity.IsStable))
        {
            if (process.PrivateBytes is not { } bytes)
            {
                memory.Remove(process.Identity);
                continue;
            }
            if (!memory.TryGetValue(process.Identity, out var points)) memory[process.Identity] = points = new();
            if (points.Count > 0 && snapshot.CapturedAt - points.Last().At > TimeSpan.FromSeconds(10)) points.Clear();
            points.Enqueue((snapshot.CapturedAt, bytes));
            while (points.Count > Capacity) points.Dequeue();
        }
    }

    public IReadOnlyList<Insight> Explain(MonitorSnapshot snapshot, RelationshipSnapshot? relationships = null)
    {
        var result = new List<Insight>();
        var largest = ProcessGrouping.Create(snapshot.Processes).Where(g => g.PrivateBytes.HasValue).OrderByDescending(g => g.PrivateBytes).FirstOrDefault();
        if (largest is not null)
            result.Add(new($"Largest measured private commit: {largest.Name}",
                $"{MiB(largest.PrivateBytes!.Value)} MiB across {largest.Members.Count} process(es). Private commit is not physical RAM; groups with missing values are excluded."));

        var mem = snapshot.Memory;
        if (mem.PhysicalAvailableBytes is { } available && mem.PhysicalTotalBytes is > 0)
        {
            var availablePercent = available * 100d / mem.PhysicalTotalBytes.Value;
            result.Add(new("Physical memory availability", $"{MiB(available)} MiB ({availablePercent:F1}%) available. Available memory includes reclaimable pages; cache and commit are not additional physical totals."));
            if (availablePercent < 10)
                result.Add(new("Low available-memory signal", "Available physical memory is below 10% in this sample. This alone does not prove sustained pressure; compare the history and workload before taking action.", "Heuristic"));
        }
        if (mem.CommitTotalBytes is { } commit && mem.CommitLimitBytes is > 0)
            result.Add(new("Commit headroom", $"{MiB(commit)} / {MiB(mem.CommitLimitBytes.Value)} MiB committed. Commit is backed by RAM and paging capacity; it is not resident memory."));

        foreach (var group in replacements.GroupBy(r => r.Key, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() >= 3))
            result.Add(new("Repeated process-lifetime replacements",
                $"{group.First().Path}: {group.Count()} single-instance identity changes observed for the same executable, owner and session within the retained history. Each change used consecutive samples no more than 10 seconds apart. This may indicate restarts, not a crash diagnosis; parallel instances and unknown identities are excluded.", "Heuristic"));

        foreach (var process in snapshot.Processes)
        {
            if (!memory.TryGetValue(process.Identity, out var queue) || queue.Count < 4) continue;
            var points = queue.ToArray();
            var first = points[0];
            var last = points[^1];
            var duration = (last.At - first.At).TotalSeconds;
            if (duration < 60 || last.Bytes <= first.Bytes) continue;
            var growth = last.Bytes - first.Bytes;
            var increases = points.Zip(points.Skip(1)).Count(pair => pair.Second.Bytes > pair.First.Bytes);
            if (growth >= 32UL * 1024 * 1024 && growth >= first.Bytes * 0.1 && increases >= (points.Length - 1) * 0.75)
                result.Add(new($"Sustained private-commit growth: {process.Name} (PID {process.Identity.Pid})",
                    $"+{MiB(growth)} MiB over {duration:F0} seconds; {increases}/{points.Length - 1} intervals increased for the same creation-time identity. This is a trend, not a memory-leak diagnosis.", "Heuristic"));
        }

        if (relationships is not null && Math.Abs((snapshot.CapturedAt - relationships.CapturedAt).TotalSeconds) <= 10)
        {
            var identities = snapshot.Processes.Where(p => p.Identity.IsStable).Select(p => p.Identity).ToHashSet();
            var active = relationships.Connections.Where(c => c.OwnerIdentity is { } owner && identities.Contains(owner)).ToArray();
            result.Add(new("Observed network ownership", $"{active.Length} TCP/UDP entries mapped to {active.Select(c => c.OwnerIdentity).Distinct().Count()} current process identities. Endpoints do not imply active bandwidth or remote location."));
        }
        return result;
    }

    private static string MiB(ulong bytes) => (bytes / (1024d * 1024)).ToString("F1", global::System.Globalization.CultureInfo.InvariantCulture);
}
