using System.Diagnostics;
using System.Text;
using SystemExplorer.Core;
using SystemExplorer.Relationships;
using SystemExplorer.Windows;

static void Require(bool condition, string detail)
{
    if (!condition) throw new InvalidOperationException(detail);
}

using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
var token = timeout.Token;
var provider = new WindowsHiddenProcessProvider();
var elevation = WindowsHiddenEnvironment.GetElevation();
Console.WriteLine($"Elevation: {elevation.Status}; elevated={elevation.IsElevated}; {elevation.Detail}");
using var self = Process.GetCurrentProcess();
var identity = new ProcessIdentity(self.Id, self.StartTime.ToUniversalTime().ToFileTimeUtc());
using var child = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c exit 0") { UseShellExecute = false, CreateNoWindow = true })!;
_ = child.Handle;
await child.WaitForExitAsync(token);
var childIdentity = new ProcessIdentity(child.Id, child.StartTime.ToUniversalTime().ToFileTimeUtc());
var first = await provider.CaptureAsync(token);
Console.WriteLine($"Process capture: {first.Status}; {first.Detail}");
foreach (var source in first.Sources.Items)
    Console.WriteLine($"  {source.Source}: count={first.Sightings.Items.Count(s => s.Source == source.Source)}; {source.Status}; {source.ElapsedMilliseconds:F1} ms; {source.Detail}");
foreach (var source in new[] { ProcessSightingSource.Toolhelp, ProcessSightingSource.NtSystemInformation, ProcessSightingSource.NtGetNextProcess, ProcessSightingSource.PidProbe })
{
    Require(first.Sources.Items.Single(s => s.Source == source).Status == InspectionStatus.Available, $"Required source unavailable: {source}");
    Require(first.Sightings.Items.Any(s => s.Source == source && s.Identity == identity), $"Own PID and creation time missing in {source}: {identity}");
}
Require(first.Sources.Items.Count == 8, "All eight process sources must report status.");
Require(first.Sightings.Items.Any(s => s.Identity == childIdentity && s.Exited), "Held exited child must be observed as exited.");
var candidates = first.Sightings.Items.Select(s => s.Identity).DistinctBy(i => i.Pid).ToArray();
await Task.Delay(300, token);
var second = await provider.RecheckAsync(new(candidates, 8192), token);
var results = ProcessCrossView.Compare(first, second);
Require(results.Items.Single(r => r.Identity.Pid == identity.Pid).Classification == ProcessDiscrepancyKind.Consistent, "Own process must be consistent.");
Require(results.Items.Single(r => r.Identity.Pid == child.Id).Classification == ProcessDiscrepancyKind.Zombie, "Held exited child must classify Zombie.");
foreach (var result in results.Items.Where(r => r.Classification != ProcessDiscrepancyKind.Consistent))
    Console.WriteLine($"Process discrepancy: {result.Identity} {result.Classification}; sources={string.Join(',', result.Evidence.Items.Select(e => e.Source).Distinct())}");
Console.WriteLine($"PASS own identity in four independent sources; held child {childIdentity} classified Zombie.");
var windows = WindowsHiddenEnvironment.GetVisibleWindowOwners(token);
Console.WriteLine($"Visible window owners: {windows.Status}; count={windows.Pids.Items.Count}; {windows.Detail}");

var ads = new WindowsAdsScanProvider();
string directory = Path.Combine(Path.GetTempPath(), "SystemExplorer-hidden-smoke-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
try
{
    string path = Path.Combine(directory, "file.txt");
    await File.WriteAllTextAsync(path, "own smoke file", token);
    await File.WriteAllBytesAsync(path + ":se-smoke", [1, 2, 3, 4, 5], token);
    await File.WriteAllBytesAsync(path + ":Zone.Identifier", Encoding.ASCII.GetBytes("[ZoneTransfer]\r\nZoneId=3\r\n"), token);
    var scan = await ads.ScanAsync(new(directory), token);
    Console.WriteLine($"ADS: {scan.Status}; count={scan.Streams.Items.Count}; {scan.Detail}");
    foreach (var stream in scan.Streams.Items) Console.WriteLine($"  {stream.StreamName}: {stream.SizeBytes} bytes; {stream.Severity}");
    Require(scan.Status == InspectionStatus.Available, "Own folder ADS scan must be available.");
    Require(scan.Streams.Items.Any(s => s.StreamName == ":se-smoke:$DATA" && s.SizeBytes == 5), "Named stream name/size missing.");
    Require(scan.Streams.Items.Any(s => s.IsDownloadMarker && s.Severity == LeadSeverity.Info), "Zone.Identifier must be informational.");
    var bounded = await ads.ScanAsync(new(directory, MaxResults: 1), token);
    Require(bounded.Streams.Items.Count == 1 && bounded.Streams.Truncated, "ADS result cap must be enforced.");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    bool cancellationObserved = false;
    try { await ads.ScanAsync(new(directory), cancelled.Token); } catch (OperationCanceledException) { cancellationObserved = true; }
    Require(cancellationObserved, "ADS cancellation must propagate.");
    Console.WriteLine("PASS ADS names/sizes, download-marker severity, bounds and cancellation.");
}
finally { Directory.Delete(directory, true); }

var configuration = new WindowsHiddenConfigurationProvider();
var services = await configuration.CaptureServicesAsync(token);
Console.WriteLine($"Services: {services.Detail} Ignored without Type={services.IgnoredWithoutType}");
Require(services.RegistryStatus == InspectionStatus.Available && services.ScmStatus == InspectionStatus.Available, "Service views must be available on this host.");
Require(services.Registry.Items.Count > 0 && services.Scm.Items.Count > 0, "Service inventories must not be empty.");
Require(services.Registry.Items.Any(s => s.Name.Equals("EventLog", StringComparison.OrdinalIgnoreCase)) && services.Scm.Items.Any(s => s.Name.Equals("EventLog", StringComparison.OrdinalIgnoreCase)), "EventLog must be in both views.");
var serviceDiff = ConfigurationCrossView.CompareServices(services);
Require(serviceDiff.Items.All(d => !d.Name.Equals("EventLog", StringComparison.OrdinalIgnoreCase)), "EventLog must not be discrepant.");
foreach (var diff in serviceDiff.Items) Console.WriteLine($"Service discrepancy: {diff.Name} {diff.Kind}; {diff.Reason}");
Console.WriteLine($"PASS service views; discrepancies={serviceDiff.Items.Count}; truncated={serviceDiff.Truncated}");

var tasks = await configuration.CaptureTasksAsync(token);
Console.WriteLine($"Tasks: elevated={tasks.IsElevated}; {tasks.Detail}");
if (tasks.IsElevated)
{
    Require(new[] { tasks.ComStatus, tasks.TreeStatus, tasks.CacheStatus, tasks.FilesStatus }.All(s => s == InspectionStatus.Available), "Elevated task views must be available on this host.");
    Require(tasks.Com.Items.Count > 0 && tasks.Tree.Items.Count > 0 && tasks.Cache.Items.Count > 0 && tasks.Files.Items.Count > 0, "Elevated task views must not be empty.");
}
else Console.WriteLine("Full task coverage requires administrator; no elevation attempted.");
var taskDiff = ConfigurationCrossView.CompareTasks(tasks);
foreach (var group in taskDiff.Items.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
    Console.WriteLine($"Task discrepancy: {group.Key} [{string.Join(", ", group.Select(d => d.Kind))}]");
Console.WriteLine($"PASS task view status reporting; discrepancies={taskDiff.Items.Count}; truncated={taskDiff.Truncated}");
Console.WriteLine("PASS Hidden items native smoke checks");
