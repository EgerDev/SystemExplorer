using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SystemExplorer.Core;

public static class StorageExport
{
    public static string ToJson(StorageScanResult scan, DeveloperBloatReport? bloat = null) => JsonSerializer.Serialize(new { Scan = scan, Bloat = bloat },
        new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });

    public static string ToCsv(StorageScanResult scan, DeveloperBloatReport? bloat = null)
    {
        var output = new StringBuilder();
        void Row(params object?[] cells) => output.AppendLine(string.Join(",", cells.Select(c => Cell(c is IFormattable f ? f.ToString(null, CultureInfo.InvariantCulture) ?? "" : c?.ToString() ?? ""))));
        var columns = new[] { "RecordType", "Root", "Method", "State", "Completeness", "StartedAt", "Elapsed", "DeniedTotal", "Category", "Tool", "Path", "AllocatedBytes", "ReclaimableBytes (blank=unknown)", "LatestWrite", "Confidence", "Reason", "CleanupCommand (not executed)", "Warning", "Kind", "IsLead", "Kinds", "RegenerableBytes", "LastActivity", "IsStale", "HasAIMarkers", "LogicalBytes", "LastWriteUtc", "Extension", "Count" };
        Row(columns.Cast<object?>().ToArray());
        void Record(string kind, params (string Column, object? Value)[] cells)
        {
            var values = cells.ToDictionary(c => c.Column, c => c.Value);
            values["RecordType"] = kind; values["Root"] = scan.Root; values["Method"] = scan.Method; values["State"] = scan.State;
            values["Completeness"] = scan.IsPartial ? "Partial" : "Complete"; values["StartedAt"] = scan.StartedAt.ToString("O"); values["Elapsed"] = scan.Elapsed;
            values["DeniedTotal"] = scan.DeniedTotal;
            Row(columns.Select(c => values.GetValueOrDefault(c)).ToArray());
        }
        Record("Metadata");
        foreach (var x in bloat?.Items ?? []) Record("Bloat items", ("Category", x.Category), ("Tool", x.Tool), ("Path", x.Path), ("AllocatedBytes", x.AllocatedBytes), ("ReclaimableBytes (blank=unknown)", x.ReclaimableBytes), ("LatestWrite", x.LatestWrite?.ToString("O")), ("Confidence", x.Confidence), ("Reason", x.Reason), ("CleanupCommand (not executed)", x.CleanupCommand), ("Warning", x.CleanupWarning), ("Kind", x.Kind), ("IsLead", x.IsLead));
        foreach (var x in bloat?.Projects ?? []) Record("Projects", ("Path", x.Path), ("Kinds", x.Kinds), ("AllocatedBytes", x.AllocatedBytes), ("RegenerableBytes", x.RegenerableBytes), ("LastActivity", x.LastActivity?.ToString("O")), ("IsStale", x.IsStale), ("HasAIMarkers", x.HasAIMarkers));
        foreach (var x in scan.LargestFiles) Record("Largest files", ("Path", x.Path), ("LogicalBytes", x.LogicalBytes), ("AllocatedBytes", x.AllocatedBytes), ("LastWriteUtc", x.LastWriteUtc.ToString("O")));
        foreach (var x in scan.Extensions) Record("Extensions", ("Extension", x.Name), ("Count", x.Count), ("LogicalBytes", x.LogicalBytes), ("AllocatedBytes", x.AllocatedBytes));
        foreach (var x in scan.DeniedPaths) Record("Denied paths", ("Path", x.Path), ("Reason", x.Reason));
        return output.ToString();
    }
    private static string Cell(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
