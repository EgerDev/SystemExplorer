using System.Globalization;
using System.Text.Json;

namespace SystemExplorer.Core;

public enum HiddenScanType { ProcessesCrossView, ServicesCrossView, ScheduledTasksCrossView, SuspiciousLeads, AlternateDataStreams }
public sealed record HiddenExportMetadata(string ScanType, string CapturedAtUtc, string Elevation, string Status, string Detail, string SourceStatuses, bool Truncated);
public sealed record HiddenExportProcess(int Pid, long? CreationTimeFileTime);
public sealed record HiddenExportRow(string ScanType, string CapturedAtUtc, string Severity, string Category, string Title,
    string Reason, string Evidence, string BenignNote, IReadOnlyList<HiddenExportProcess> Processes, string? ImagePath,
    IReadOnlyList<string> SourcesSeen, IReadOnlyList<string> SourcesMissing, string Status, string Detail, bool ProcessesTruncated);
public sealed record HiddenExportDocument(HiddenExportMetadata Metadata, IReadOnlyList<HiddenExportRow> Rows);

public static class HiddenItemsExport
{
    public const int MaximumRows = 2000;
    public static string ScanName(HiddenScanType scan) => scan switch
    {
        HiddenScanType.ProcessesCrossView => "Processes cross-view", HiddenScanType.ServicesCrossView => "Services cross-view",
        HiddenScanType.ScheduledTasksCrossView => "Scheduled tasks cross-view", HiddenScanType.SuspiciousLeads => "Suspicious leads",
        HiddenScanType.AlternateDataStreams => "Alternate data streams", _ => throw new ArgumentOutOfRangeException(nameof(scan))
    };

    private static string CategoryName(LeadCategory category, HiddenScanType scan) => category switch
    {
        LeadCategory.CrossView => scan == HiddenScanType.ServicesCrossView ? "Service" : scan == HiddenScanType.ScheduledTasksCrossView ? "Scheduled task" : "Cross-view process",
        LeadCategory.UnusualLocation => "Unusual location", LeadCategory.ParentMismatch => "Parent relationship",
        LeadCategory.EndpointOwner => "Listening port", LeadCategory.NoVisibleWindow => "No visible window",
        LeadCategory.AlternateDataStream => "Alternate data stream", _ => category.ToString()
    };

    public static HiddenExportDocument Build(HiddenScanType scan, DateTimeOffset capturedAt, string elevation, string summary,
        string sourceStatuses, IEnumerable<SuspiciousLead> leads, bool truncated = false, int cap = MaximumRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cap);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cap, MaximumRows);
        string scanName = ScanName(scan), timestamp = capturedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        var bounded = new BoundedItems<SuspiciousLead>(leads, cap, truncated);
        var metadata = new HiddenExportMetadata(scanName, timestamp, elevation, bounded.Truncated ? "Truncated" : "Frozen", summary, sourceStatuses, bounded.Truncated);
        var rows = bounded.Items.Select(lead =>
        {
            var identities = new BoundedItems<ProcessIdentity>(lead.ProcessIdentities?.Items ?? (lead.ProcessIdentity is { } identity ? [identity] : []), 32, lead.ProcessIdentities?.Truncated == true);
            return new HiddenExportRow(scanName, timestamp, lead.Severity.ToString(), CategoryName(lead.Category, scan), lead.Title,
                lead.Reason, lead.Evidence, lead.BenignNote, identities.Items.Select(i => new HiddenExportProcess(i.Pid, i.CreationTimeFileTime)).ToArray(), scan is HiddenScanType.ServicesCrossView or HiddenScanType.ScheduledTasksCrossView ? null : lead.Path,
                lead.SourcesSeen?.Items.Select(s => s.ToString()).ToArray() ?? [], lead.SourcesMissing?.Items.Select(s => s.ToString()).ToArray() ?? [],
                lead.Status ?? metadata.Status, lead.StatusDetail ?? summary, identities.Truncated);
        }).ToArray();
        return new(metadata, rows);
    }

    private static string Escape(string value)
    {
        var trimmed = value.TrimStart();
        if (value.Length > 0 && (value[0] is '\t' or '\r' or '\n' || trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    public static string Csv(HiddenExportDocument document)
    {
        document = Limit(document);
        var lines = new List<string> { string.Join(",", new[] { "ScanType", "CapturedAtUtc", "Severity", "Category", "Title", "Reason", "Evidence", "BenignNote", "Pids", "CreationTimeFileTimes", "ImagePath", "SourcesSeen", "SourcesMissing", "Status", "Detail", "ProcessesTruncated", "Elevation", "SourceStatuses", "RowsTruncated" }.Select(Escape)) };
        foreach (var row in document.Rows.Take(MaximumRows))
            lines.Add(string.Join(",", new[] { row.ScanType, row.CapturedAtUtc, row.Severity, row.Category, row.Title, row.Reason, row.Evidence, row.BenignNote,
                string.Join("; ", row.Processes.Select(p => p.Pid.ToString(CultureInfo.InvariantCulture))),
                string.Join("; ", row.Processes.Select(p => $"{p.Pid}:{p.CreationTimeFileTime?.ToString(CultureInfo.InvariantCulture) ?? "unknown"}")), row.ImagePath ?? "",
                string.Join("; ", row.SourcesSeen), string.Join("; ", row.SourcesMissing), row.Status, row.Detail, row.ProcessesTruncated.ToString(), document.Metadata.Elevation,
                document.Metadata.SourceStatuses, document.Metadata.Truncated.ToString() }.Select(Escape)));
        return string.Join("\r\n", lines) + "\r\n";
    }

    private static HiddenExportDocument Limit(HiddenExportDocument document) => document.Rows.Count <= MaximumRows ? document
        : document with { Rows = document.Rows.Take(MaximumRows).ToArray(), Metadata = document.Metadata with { Truncated = true, Status = "Truncated" } };

    public static string Json(HiddenExportDocument document) => JsonSerializer.Serialize(Limit(document), new JsonSerializerOptions { WriteIndented = true });
}
