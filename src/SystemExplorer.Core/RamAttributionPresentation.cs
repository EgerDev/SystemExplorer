using System.Globalization;
using System.Text.Json;

namespace SystemExplorer.Core;

public sealed record RamExportRow(string Section, string Status, string Detail, IReadOnlyDictionary<string, string> Values);
public sealed record RamExportDocument(DateTimeOffset CapturedAt, string Tab, IReadOnlyList<RamExportRow> Rows);
public sealed record RamBarSegment(string Name, double Fraction);

public static class RamAttributionPresentation
{
    public static string StatusName(RamReadingStatus status) => status switch
    {
        RamReadingStatus.IdentityMismatch => "Exited / PID reused",
        RamReadingStatus.RequiresAdministrator => "Requires administrator",
        RamReadingStatus.TimedOut => "Timed out",
        _ => status.ToString()
    };

    public static string CategoryColor(string name) => name switch
    {
        "Process private working sets (excluding compression)" => "#477FDB",
        "Compression store" => "#9973C9",
        "System cache resident" => "#31A7A0",
        "Paged pool resident" => "#C37AC2",
        "Nonpaged pool" => "#D79239",
        "Driver images resident" => "#D66782",
        "Kernel code resident" => "#B8A23B",
        "Modified (including no-write)" => "#C86143",
        "Standby priority 0" => "#263F69", "Standby priority 1" => "#345A87",
        "Standby priority 2" => "#4174A5", "Standby priority 3" => "#508EC0",
        "Standby priority 4" => "#6BA6D0", "Standby priority 5" => "#88BFDF",
        "Standby priority 6" => "#A8D4EC", "Standby priority 7" => "#CEE8F5",
        "Free / zero" => "#9DAFA5",
        _ => "#87858C"
    };

    public static string PoolTag(uint tag) => string.Concat(Enumerable.Range(0, 4).Select(i =>
    {
        var value = (byte)(tag >> (i * 8));
        return value is >= 32 and <= 126 ? ((char)value).ToString() : $"\\x{value:X2}";
    }));

    public static string UseName(PhysicalPageUse use) => use is >= PhysicalPageUse.Unknown12 and <= PhysicalPageUse.Unknown15
        ? $"Use {(int)use} (undocumented)" : use switch
        {
            PhysicalPageUse.File => "File / mapped file", PhysicalPageUse.PageFileMapped => "Pagefile mapped / shareable",
            PhysicalPageUse.DriverLockedPage => "Driver locked page", _ => use.ToString()
        };

    public static IReadOnlyList<RamBarSegment> BarSegments(RamBreakdown breakdown)
    {
        if (breakdown.UsableBytes is not > 0 || breakdown.Categories.Any(c => c.Bytes is null or < 0) ||
            breakdown.Categories.Sum(c => (decimal)c.Bytes!.Value) != breakdown.UsableBytes.Value) return [];
        return breakdown.Categories.Where(c => c.Bytes > 0).Select(c => new RamBarSegment(c.Name, (double)c.Bytes!.Value / breakdown.UsableBytes.Value)).ToArray();
    }

    public static string Csv(RamExportDocument document)
    {
        static string Escape(string value) => "\"" + value.Replace("\"", "\"\"") + "\"";
        var columns = document.Rows.SelectMany(r => r.Values.Keys).Distinct(StringComparer.Ordinal).ToArray();
        var metadata = new[] { "CapturedAt", "Tab", "Section", "RowStatus", "RowDetail" };
        var names = new HashSet<string>(metadata, StringComparer.OrdinalIgnoreCase);
        var headers = columns.Select(column =>
        {
            string name = column;
            for (int suffix = 2; !names.Add(name); suffix++) name = column + " (" + suffix.ToString(CultureInfo.InvariantCulture) + ")";
            return name;
        }).ToArray();
        var lines = new List<string> { string.Join(",", metadata.Concat(headers).Select(Escape)) };
        foreach (var row in document.Rows)
            lines.Add(string.Join(",", new[] { document.CapturedAt.ToString("O", CultureInfo.InvariantCulture), document.Tab, row.Section, row.Status, row.Detail }
                .Concat(columns.Select(c => row.Values.GetValueOrDefault(c, ""))).Select(Escape)));
        return string.Join("\r\n", lines) + "\r\n";
    }

    public static string Json(RamExportDocument document) => JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = true });
}
