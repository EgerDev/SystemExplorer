using System.Text;
using System.Text.Json;
using SystemExplorer.Core;

internal static class TraceJsonExportTests
{
    private static void Require(bool value)
    {
        if (!value) throw new InvalidOperationException("Filtered Trace JSON process-table regression failed.");
    }

    internal static void Run()
    {
        var at = DateTimeOffset.UnixEpoch;
        var trace = new TraceRecorderState(new(ExcludeSelf: false), at).Complete(at) with
        {
            Processes = new TraceProcess[]
            {
                new(0, 10, 100, 0, -1, "unrelated", "C:/private/unrelated.exe", null),
                new(1, 11, 101, 0, -1, "grandparent", "C:/grandparent.exe", null),
                new(2, 12, 102, 11, 1, "parent", "C:/parent.exe", null),
                new(3, 13, 103, 12, 2, "selected", "C:/selected.exe", null),
                new(4, 14, 104, 13, 3, "child", "C:/child.exe", null),
                new(5, 13, 90, 0, -1, "old-lifetime", "C:/private/old-lifetime.exe", null, at)
            },
            Events = new TraceEvent[]
            {
                new(1, at.UtcTicks, 10, 1, 0, TraceOperation.FileRead, "unrelated-path"),
                new(2, at.UtcTicks, 13, 1, 3, TraceOperation.FileRead, "keep,\"quoted\"\r\nline"),
                new(3, at.UtcTicks, 14, 1, 4, TraceOperation.FileRead, "child-path"),
                new(4, at.UtcTicks, 13, 1, 5, TraceOperation.FileRead, "old-path"),
                new(5, at.UtcTicks, 99, 1, -1, TraceOperation.FileRead, "unresolved-path")
            }
        };
        foreach (var filter in new[] { new TraceFilter(ProcessIndex: 3, IncludeDescendants: false), new(Text: "keep"), new TraceFilter(ProcessIndex: 3, Text: "keep") })
        {
            var (root, text) = Export(trace, filter);
            Require(!text.Contains("unrelated") && !text.Contains("old-lifetime") && !text.Contains("child.exe"));
            Require(root.GetProperty("processes").GetArrayLength() == 3 && root.GetProperty("events").GetArrayLength() == 1);
            Require(root.GetProperty("events")[0].GetProperty("processIndex").GetInt32() == 2);
            if (filter.ProcessIndex.HasValue) Require(root.GetProperty("filter").GetProperty("processIndex").GetInt32() == 2);
        }
        var (descendants, descendantText) = Export(trace, new(ProcessIndex: 3));
        Require(descendants.GetProperty("processes").GetArrayLength() == 4 && descendants.GetProperty("events").GetArrayLength() == 2);
        Require(descendantText.Contains("child.exe") && !descendantText.Contains("unrelated") && !descendantText.Contains("old-lifetime"));
        foreach (var filter in new[] { new TraceFilter(ProcessIndex: 0, Text: "no match"), new TraceFilter(ProcessIndex: 900), new TraceFilter(Categories: TraceCategories.Network) })
        {
            var (root, text) = Export(trace, filter);
            Require(root.GetProperty("processes").GetArrayLength() == 0 && root.GetProperty("events").GetArrayLength() == 0);
            Require(root.GetProperty("filter").GetProperty("processIndex").ValueKind == JsonValueKind.Null);
            Require(!text.Contains(".exe"));
        }
        var (unresolved, _) = Export(trace, new(Text: "unresolved"));
        Require(unresolved.GetProperty("processes").GetArrayLength() == 0 && unresolved.GetProperty("events")[0].GetProperty("processIndex").GetInt32() == -1);
        Export(trace, new());
        var empty = trace with { Events = [] };
        Require(Export(empty, new()).Root.GetProperty("processes").GetArrayLength() == 0);
        var broken = trace with { Processes = trace.Processes.Select(p => p.Index == 1 ? p with { ParentIndex = 99 } : p).ToArray() };
        Require(Export(broken, new(ProcessIndex: 3)).Root.GetProperty("processes")[0].GetProperty("parentIndex").GetInt32() == -1);
        var cycle = trace with { Processes = trace.Processes.Select(p => p.Index == 1 ? p with { ParentIndex = 3, ParentPid = 13 } : p).ToArray() };
        Export(cycle, new(ProcessIndex: 3));
        Require(trace.Processes[3].Index == 3 && trace.Events[1].ProcessIndex == 3);
    }

    private static (JsonElement Root, string Text) Export(TraceResult trace, TraceFilter filter)
    {
        using var stream = new MemoryStream();
        TraceExport.WriteJson(stream, trace, filter);
        string text = Encoding.UTF8.GetString(stream.ToArray());
        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var processes = root.GetProperty("processes").EnumerateArray().ToArray();
        for (int i = 0; i < processes.Length; i++)
        {
            Require(processes[i].GetProperty("index").GetInt32() == i);
            int parent = processes[i].GetProperty("parentIndex").GetInt32();
            Require(parent == -1 || parent >= 0 && parent < processes.Length);
            if (parent >= 0) Require(processes[parent].GetProperty("pid").GetInt32() == processes[i].GetProperty("parentPid").GetInt32());
        }
        var events = root.GetProperty("events").EnumerateArray().ToArray();
        Require(events.Length == filter.Match(trace.Events, trace.Processes).Length);
        foreach (var e in events)
        {
            int index = e.GetProperty("processIndex").GetInt32();
            Require(index == -1 || index >= 0 && index < processes.Length);
            if (index >= 0) Require(processes[index].GetProperty("pid").GetInt32() == e.GetProperty("pid").GetInt32());
        }
        int? selected = root.GetProperty("filter").GetProperty("processIndex").ValueKind == JsonValueKind.Null ? null : root.GetProperty("filter").GetProperty("processIndex").GetInt32();
        Require(selected is null || selected >= 0 && selected < processes.Length);
        return (root.Clone(), text);
    }
}
