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
        Row("Metadata", "Root", "Method", "State", "Completeness", "StartedAt", "Elapsed");
        Row("Metadata", scan.Root, scan.Method, scan.State, scan.IsPartial ? "Partial" : "Complete", scan.StartedAt.ToString("O"), scan.Elapsed);
        Row("Bloat items", "Category", "Tool", "Path", "AllocatedBytes", "ReclaimableBytes (blank=unknown)", "LatestWrite", "Confidence", "Reason", "CleanupCommand (not executed)", "Warning", "Kind", "IsLead");
        foreach (var x in bloat?.Items ?? []) Row("Bloat items", x.Category, x.Tool, x.Path, x.AllocatedBytes, x.ReclaimableBytes, x.LatestWrite?.ToString("O"), x.Confidence, x.Reason, x.CleanupCommand, x.CleanupWarning, x.Kind, x.IsLead);
        Row("Projects", "Path", "Kinds", "AllocatedBytes", "RegenerableBytes", "LastActivity", "IsStale", "HasAIMarkers");
        foreach (var x in bloat?.Projects ?? []) Row("Projects", x.Path, x.Kinds, x.AllocatedBytes, x.RegenerableBytes, x.LastActivity?.ToString("O"), x.IsStale, x.HasAIMarkers);
        Row("Largest files", "Path", "LogicalBytes", "AllocatedBytes", "LastWriteUtc");
        foreach (var x in scan.LargestFiles) Row("Largest files", x.Path, x.LogicalBytes, x.AllocatedBytes, x.LastWriteUtc.ToString("O"));
        Row("Extensions", "Extension", "Count", "LogicalBytes", "AllocatedBytes");
        foreach (var x in scan.Extensions) Row("Extensions", x.Name, x.Count, x.LogicalBytes, x.AllocatedBytes);
        Row("Denied paths", "Path", "Reason");
        foreach (var x in scan.DeniedPaths) Row("Denied paths", x.Path, x.Reason);
        return output.ToString();
    }
    private static string Cell(string value)
    {
        if (value.Length > 0 && "=+-@\t\r".Contains(value[0])) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
