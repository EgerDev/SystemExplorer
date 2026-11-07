using System.Text.Json;
using SystemExplorer.Core;

internal static class HiddenExportTests
{
    public static void Run()
    {
        static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Hidden export assertion failed."); }
        var at = new DateTimeOffset(2026, 1, 2, 3, 4, 5, TimeSpan.FromHours(2));
        var lead = new SuspiciousLead(LeadCategory.CrossView, LeadSeverity.High, "=SUM(1,2)", "quoted \"reason\"", "first\nsecond", "benign", new(42, 100), @"C:\safe\file.exe",
            SourcesSeen: new([ProcessSightingSource.Toolhelp], 8), SourcesMissing: new([ProcessSightingSource.EtwOwner], 8), Status: "Unknown", StatusDetail: "recheck unavailable");
        HiddenExportDocument Build(IEnumerable<SuspiciousLead> leads, int cap = 2000) => HiddenItemsExport.Build(HiddenScanType.ProcessesCrossView, at, "Requires administrator", "summary", "ETW owners: Denied; reason", leads, cap: cap);
        var document = Build([lead]);
        var csv = HiddenItemsExport.Csv(document);
        Require(csv.Contains("\"'=SUM(1,2)\"") && csv.Contains("\"quoted \"\"reason\"\"\"") && csv.Contains("\"first\nsecond\""));
        foreach (var formula in new[] { "+cmd", "-cmd", "@cmd", "  =cmd", "\tcmd", "\rcmd" })
            Require(HiddenItemsExport.Csv(Build([lead with { Title = formula }])).Contains("\"'" + formula + "\""));
        using var json = JsonDocument.Parse(HiddenItemsExport.Json(document));
        var metadata = json.RootElement.GetProperty("Metadata");
        Require(metadata.GetProperty("CapturedAtUtc").GetString() == "2026-01-02T01:04:05.0000000Z");
        Require(metadata.GetProperty("Elevation").GetString() == "Requires administrator" && metadata.GetProperty("SourceStatuses").GetString()!.Contains("Denied"));
        var row = json.RootElement.GetProperty("Rows")[0];
        Require(row.GetProperty("Processes")[0].GetProperty("Pid").GetInt32() == 42 && row.GetProperty("Processes")[0].GetProperty("CreationTimeFileTime").GetInt64() == 100);
        Require(row.GetProperty("SourcesSeen")[0].GetString() == "Toolhelp" && row.GetProperty("SourcesMissing")[0].GetString() == "EtwOwner");
        Require(Build([lead, lead], 1).Metadata.Truncated && Build([lead, lead], 1).Rows.Count == 1);
        Require(Build([lead], 0).Rows.Count == 0 && Build([lead], 0).Metadata.Truncated);
        Require(Build([]).Rows.Count == 0 && !Build([]).Metadata.Truncated && HiddenItemsExport.Csv(Build([])).Split("\r\n").Length == 2);
        Require(JsonDocument.Parse(HiddenItemsExport.Json(Build([]))).RootElement.GetProperty("Rows").GetArrayLength() == 0);
        bool rejected = false;
        try { Build([], -1); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Require(rejected);
        rejected = false;
        try { Build([], 2001); } catch (ArgumentOutOfRangeException) { rejected = true; }
        Require(rejected);
        foreach (var type in new[] { typeof(HiddenExportDocument), typeof(HiddenExportMetadata), typeof(HiddenExportRow), typeof(HiddenExportProcess) })
            Require(!type.GetProperties().Any(p => p.Name.Contains("command", StringComparison.OrdinalIgnoreCase)));
        var parsed = SuspiciousLeadRules.ParsePersistenceTarget("\"C:\\safe\\file.exe\" --secret-command-arguments");
        var safe = Build([lead with { Path = parsed.Path }]);
        Require(!HiddenItemsExport.Json(safe).Contains("secret-command-arguments") && !HiddenItemsExport.Csv(safe).Contains("secret-command-arguments"));
        var manyProcesses = Build([lead with { ProcessIdentities = new(Enumerable.Range(1, 40).Select(pid => new ProcessIdentity(pid, null)), 40) }]);
        Require(manyProcesses.Rows[0].Processes.Count == 32 && manyProcesses.Rows[0].ProcessesTruncated && manyProcesses.Rows[0].Processes[0].CreationTimeFileTime is null);
        var aliasedPath = @"C:\Users\ADMINI~1\AppData\Local\Temp\svchost.exe";
        var canonical = new ImageProvenance(@"C:\Users\Administrator\AppData\Local\Temp\svchost.exe", InspectionStatus.Available, "", null, null, null, null, null, null, null, SignatureState.CatalogTrusted, 0, "Microsoft Windows", "catalog", at);
        var process = new ProcessSnapshot { Identity = new(42, 100), Name = "svchost.exe", ExecutablePath = aliasedPath };
        var aliased = new LeadSweepInput(new([new ProcessLeadInput(process, null, false, null, canonical with { Path = aliasedPath })], 1), new([], 1), new([], 1), true);
        Require(SuspiciousLeadRules.Evaluate(aliased).Items.Single(l => l.Category == LeadCategory.Masquerading).Severity == LeadSeverity.Medium);
        Console.WriteLine("PASS hidden export CSV quoting/newlines/formula guards, JSON metadata/identities/sources, bounds, empty/error cases and no command-line schema/value");
    }
}
