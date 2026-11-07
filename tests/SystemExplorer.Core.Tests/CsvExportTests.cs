using Microsoft.VisualBasic.FileIO;
using SystemExplorer.Core;

internal static class CsvExportTests
{
    private static void Require(bool value)
    {
        if (!value) throw new InvalidOperationException("CSV header/round-trip regression failed.");
    }

    private static List<string[]> Parse(string csv)
    {
        using var parser = new TextFieldParser(new StringReader(csv)) { HasFieldsEnclosedInQuotes = true, TrimWhiteSpace = false };
        parser.SetDelimiters(",");
        var rows = new List<string[]>();
        while (!parser.EndOfData) rows.Add(parser.ReadFields()!);
        return rows;
    }

    private static void Header(string[] header)
    {
        Require(header.Length > 0 && header.All(h => !string.IsNullOrWhiteSpace(h)));
        Require(header.Distinct(StringComparer.OrdinalIgnoreCase).Count() == header.Length);
    }

    private static Dictionary<string, string> RoundTrip(string[] header, string[] row)
    {
        Header(header);
        Require(header.Length == row.Length);
        return header.Select((name, i) => (name, row[i])).ToDictionary(p => p.name, p => p.Item2, StringComparer.OrdinalIgnoreCase);
    }

    internal static void Run()
    {
        var at = DateTimeOffset.UnixEpoch;
        var tabs = new (string Name, string[] Columns)[]
        {
            ("Overview", ["Category", "Size", "Percent", "Source", "Status"]),
            ("Processes", ["Process", "PID", "Private resident", "Actually shared", "Shareable (sole)", "Proportional est.", "Shared commit", "Private commit", "Status"]),
            ("Pool tags", ["Tag", "Paged", "Nonpaged", "Total", "Candidate drivers (lead)"]),
            ("Physical memory", ["Use", "Total", "Active", "Standby", "Modified", "Modified no-write", "Transition", "Priority", "Standby pages", "Repurposed pages", "File object", "Zero", "Free", "Bad"])
        };
        foreach (var (tab, columns) in tabs)
        {
            var values = columns.ToDictionary(c => c, c => c + ",\"quoted\"\r\nline");
            var document = new RamExportDocument(at, tab, [new("section", "Available", "row detail", values)]);
            var rows = Parse(RamAttributionPresentation.Csv(document));
            var restored = RoundTrip(rows[0], rows[1]);
            Require(restored["RowStatus"] == "Available" && restored["RowDetail"] == "row detail");
            foreach (var (column, value) in values) Require(restored[column] == value);
            Header(Parse(RamAttributionPresentation.Csv(document with { Rows = [] }))[0]);
            Header(Parse(RamAttributionPresentation.Csv(document with { Rows = [new("unavailable", "Denied", "error", new Dictionary<string, string>())] }))[0]);
        }
        var collisions = new RamExportDocument(at, "Processes", [new("x", "Denied", "error", new Dictionary<string, string>
        {
            ["Status"] = "displayed", ["status"] = "lowercase", ["RowStatus"] = "value status", ["ROWDETAIL"] = "value detail",
            ["CapturedAt"] = "value time", ["Tab"] = "value tab", ["Section"] = "value section", ["Status (2)"] = "literal suffix"
        })]);
        var collisionRows = Parse(RamAttributionPresentation.Csv(collisions));
        var collisionRoundTrip = RoundTrip(collisionRows[0], collisionRows[1]);
        Require(collisionRoundTrip["Status"] == "displayed" && collisionRoundTrip["status (2)"] == "lowercase");
        Require(collisions.Rows[0].Values.Values.All(collisionRoundTrip.Values.Contains));

        foreach (var scan in Enum.GetValues<HiddenScanType>())
        foreach (bool empty in new[] { false, true })
        {
            var document = HiddenItemsExport.Build(scan, at, "admin", "summary", "source: Denied", empty ? [] :
                [new(LeadCategory.Persistence, LeadSeverity.Info, "title,\"quoted\"\r\nline", "reason", "evidence", "benign")]);
            var rows = Parse(HiddenItemsExport.Csv(document));
            Header(rows[0]);
            foreach (var row in rows.Skip(1)) Require(RoundTrip(rows[0], row)["Title"] == "title,\"quoted\"\r\nline");
        }

        var aggregate = new StorageScanAggregator("C:/fixture", new(), false);
        aggregate.AddListing(0, "C:/fixture", [new("file,quoted.txt", false, 1, 1, 0, 0, 0, DateTime.UnixEpoch, DateTime.UnixEpoch)]);
        aggregate.AddDenied(0, "C:/fixture/denied", "reason,\"quoted\"\r\nline");
        var storage = aggregate.Complete(StorageScanState.Cancelled, TimeSpan.Zero, "test", []);
        var bloat = new DeveloperBloatReport(
            [new("category", "tool", "C:/fixture/cache", 10, 5, null, BloatConfidence.High, "reason", "display only", "warning", "kind")],
            [new("C:/fixture/project", StorageMarkers.None, 10, 5, [], null, false, false)], [], [], 0);
        foreach (var state in new[] { storage, new StorageScanAggregator("C:/empty", new(), false).Complete(StorageScanState.Completed, TimeSpan.Zero, "test", []) })
        {
            var rows = Parse(StorageExport.ToCsv(state, state == storage ? bloat : null));
            Header(rows[0]);
            var restored = rows.Skip(1).Select(row => RoundTrip(rows[0], row)).ToArray();
            Require(restored.All(row => row["Root"] == state.Root));
            if (state == storage)
                Require(new[] { "Metadata", "Bloat items", "Projects", "Largest files", "Extensions", "Denied paths" }.All(section => restored.Any(row => row["RecordType"] == section)));
            else Require(restored.Length == 1 && restored[0]["RecordType"] == "Metadata");
        }
        foreach (bool empty in new[] { false, true })
        {
            var recorder = new TraceRecorderState(new(ExcludeSelf: false), at);
            if (!empty) recorder.Record(at, 1, 1, TraceOperation.FileRead, "path,\"quoted\"\r\nline");
            var trace = recorder.Complete(at, eventsLost: 1);
            using var writer = new StringWriter();
            TraceExport.WriteCsv(writer, trace);
            var rows = Parse(writer.ToString());
            Header(rows[0]);
            foreach (var row in rows.Skip(1))
            {
                var restored = RoundTrip(rows[0], row);
                Require(restored["TraceStatus"] == "Partial" && restored["EventsLost"] == "1");
                Require(restored["RecordType"] == (empty ? "Metadata" : "Event"));
                Require(restored["Path"] == (empty ? "" : "path,\"quoted\"\r\nline"));
            }
        }
    }
}
