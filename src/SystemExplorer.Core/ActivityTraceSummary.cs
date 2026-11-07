using System.Collections.ObjectModel;

namespace SystemExplorer.Core;

public sealed record TraceTopItem(string Name, long Count);
public sealed record TraceDistinctSummary(int DistinctRetained, long UntrackedObservations, bool Capped, IReadOnlyList<TraceTopItem> Top);
public sealed record TraceProcessSummary(int ProcessIndex, IReadOnlyDictionary<TraceCategories, long> CategoryCounts,
    TraceDistinctSummary Files, TraceDistinctSummary RegistryKeys, TraceDistinctSummary Endpoints,
    ulong BytesRead, ulong BytesWritten, ulong BytesSent, ulong BytesReceived, long ImagesLoaded)
{
    internal static bool HasCappedSets(IReadOnlyList<TraceEvent> events, int cap = 1024)
    {
        var sets = new Dictionary<(int, TraceCategories), HashSet<string>>();
        foreach (var e in events)
        {
            var category = TraceEvent.Category(e.Operation);
            if (category is not (TraceCategories.File or TraceCategories.Registry or TraceCategories.Network)) continue;
            var name = e.Path ?? (category == TraceCategories.Network ? e.Detail : null);
            if (name is null) continue;
            var key = (e.ProcessIndex, category);
            if (!sets.TryGetValue(key, out var set)) sets.Add(key, set = new(StringComparer.OrdinalIgnoreCase));
            if (!set.Contains(name) && set.Count == cap) return true;
            set.Add(name);
        }
        return false;
    }
    public bool Capped => Files.Capped || RegistryKeys.Capped || Endpoints.Capped;
    public static TraceProcessSummary Build(TraceResult result, int processIndex, int distinctCap = 1024, int top = 10)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(distinctCap); ArgumentOutOfRangeException.ThrowIfNegative(top);
        var files = new BoundedCounts(distinctCap); var keys = new BoundedCounts(distinctCap); var endpoints = new BoundedCounts(distinctCap);
        var counts = new Dictionary<TraceCategories, long>();
        ulong read = 0, written = 0, sent = 0, received = 0; long images = 0;
        static ulong Add(ulong a, ulong b) => ulong.MaxValue - a < b ? ulong.MaxValue : a + b;
        foreach (var e in result.Events)
        {
            if (e.ProcessIndex != processIndex) continue;
            var category = TraceEvent.Category(e.Operation); counts[category] = counts.GetValueOrDefault(category) + 1;
            if (category == TraceCategories.File) files.Add(e.Path);
            if (category == TraceCategories.Registry) keys.Add(e.Path);
            if (category == TraceCategories.Network) endpoints.Add(e.Path ?? e.Detail);
            switch (e.Operation)
            {
                case TraceOperation.FileRead: read = Add(read, e.Bytes); break;
                case TraceOperation.FileWrite: written = Add(written, e.Bytes); break;
                case TraceOperation.NetSend: sent = Add(sent, e.Bytes); break;
                case TraceOperation.NetReceive: received = Add(received, e.Bytes); break;
                case TraceOperation.ImageLoad: images++; break;
            }
        }
        if (processIndex >= 0 && processIndex < result.Processes.Count)
        {
            var p = result.Processes[processIndex]; counts[TraceCategories.Thread] = p.ThreadsStarted + p.ThreadsStopped;
        }
        return new(processIndex, new ReadOnlyDictionary<TraceCategories, long>(counts), files.Snapshot(top), keys.Snapshot(top), endpoints.Snapshot(top), read, written, sent, received, images);
    }
    private sealed class BoundedCounts(int cap)
    {
        private readonly Dictionary<string, long> counts = new(StringComparer.OrdinalIgnoreCase);
        private long untracked;
        public void Add(string? name)
        {
            if (name is null) return;
            if (counts.TryGetValue(name, out long count)) counts[name] = count + 1;
            else if (counts.Count < cap) counts.Add(name, 1);
            else untracked++;
        }
        public TraceDistinctSummary Snapshot(int top) => new(counts.Count, untracked, untracked > 0,
            Array.AsReadOnly(counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Take(top).Select(p => new TraceTopItem(p.Key, p.Value)).ToArray()));
    }
}
