using SystemExplorer.Core;

internal static class StorageCompletenessTests
{
    internal static void Run()
    {
        static void Require(bool value) { if (!value) throw new InvalidOperationException("Storage denied-child completeness regression failed."); }
        foreach (var requested in new[] { StorageScanState.Completed, StorageScanState.Cancelled, StorageScanState.Failed })
        {
            var aggregate = new StorageScanAggregator("C:/fixture", new(), false);
            var child = aggregate.AddListing(0, "C:/fixture", [new("denied", true, 0, 0, 0, 0, 0, default, default)]).Single();
            aggregate.AddDenied(child.Node, child.Path, "Access is denied.");
            var result = aggregate.Complete(requested, TimeSpan.Zero, "native directory enumeration", []);
            Require(result.IsPartial);
            Require(result.State.ToString() == (requested == StorageScanState.Completed ? "Partial" : requested.ToString()));
            Require(result.DeniedTotal == 1 && result.DeniedPaths.Single().Path == @"C:\fixture\denied");
            Require(result.Nodes[child.Node].Denied == 1);
            using var json = System.Text.Json.JsonDocument.Parse(StorageExport.ToJson(result));
            Require(json.RootElement.GetProperty("Scan").GetProperty("IsPartial").GetBoolean());
            Require(json.RootElement.GetProperty("Scan").GetProperty("DeniedTotal").GetInt64() == 1);
            var csv = StorageExport.ToCsv(result);
            Require(csv.Contains("\"Partial\"") && csv.Contains("Access is denied.") && csv.Contains(@"C:\fixture\denied"));
        }
        var empty = new StorageScanAggregator("C:/empty", new(), false).Complete(StorageScanState.Completed, TimeSpan.Zero, "test", []);
        Require(!empty.IsPartial && empty.State == StorageScanState.Completed && empty.DeniedTotal == 0);
        var capped = new StorageScanAggregator("C:/fixture", new(Problems: 0), false);
        capped.AddDenied(0, "C:/fixture/denied", "Access is denied.");
        var cappedResult = capped.Complete(StorageScanState.Completed, TimeSpan.Zero, "test", []);
        Require(cappedResult.IsPartial && cappedResult.DeniedTotal == 1 && cappedResult.DeniedPaths.Count == 0);
    }
}
