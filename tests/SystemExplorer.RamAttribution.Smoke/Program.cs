using System.Diagnostics;
using System.Security.Principal;
using SystemExplorer.Core;
using SystemExplorer.Windows;

static void Check(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
var token = timeout.Token;
var residentProvider = new WindowsProcessResidentProvider();
using var self = Process.GetCurrentProcess();
var ownIdentity = new ProcessIdentity(self.Id, self.StartTime.ToFileTimeUtc());
var own = await residentProvider.CaptureAsync(ownIdentity, self.ProcessName, token);
Console.WriteLine($"Self resident: {own.Status}; pages={own.PagesScanned:N0}; private={own.PrivateResidentBytes:N0}; shareable={own.ShareableResidentBytes:N0}; shared commit={own.SharedCommitBytes}; private commit={own.PrivateCommitBytes}; {own.Detail}");
Check(own.Status == RamReadingStatus.Available && own.PagesScanned > 0, "Self resident scan failed.");
Check(own.PrivateResidentBytes + own.ShareableResidentBytes == (ulong)own.PagesScanned * (uint)Environment.SystemPageSize, "Self resident byte accounting failed.");
var mismatch = await residentProvider.CaptureAsync(new(self.Id, 1), "self-wrong-lifetime", token);
Check(mismatch.Status == RamReadingStatus.IdentityMismatch, "Stale identity accepted.");
try { await residentProvider.CaptureAsync(ownIdentity, "self", new CancellationToken(true)); throw new Exception("Cancellation ignored."); }
catch (OperationCanceledException) { }
var machineProvider = new WindowsRamBreakdownProvider();
var snapshot = await machineProvider.CaptureProcessesAsync(token);
var clock = Stopwatch.StartNew();
var residents = await residentProvider.CaptureAllAsync(snapshot.Processes, null, token);
Console.WriteLine($"All process scans: {residents.Count} rows in {clock.Elapsed.TotalSeconds:F2}s; {string.Join(", ", residents.GroupBy(r => r.Status).Select(g => $"{g.Key}={g.Count()}"))}");
Check(clock.Elapsed < TimeSpan.FromSeconds(40), "All-process cooperative budget exceeded.");
foreach (var row in residents.Where(r => r.Status == RamReadingStatus.Available).OrderByDescending(r => r.PrivateResidentBytes).Take(5))
    Console.WriteLine($"  {row.Identity.Pid,6} {row.Name,-24} private={row.PrivateResidentBytes,14:N0} shareable={row.ShareableResidentBytes,14:N0}");
var machine = await machineProvider.CaptureAsync(snapshot, token);
Console.WriteLine($"Machine: installed={machine.InstalledBytes:N0}; usable={machine.UsableBytes:N0}; hardware reserved={machine.HardwareReservedBytes:N0}");
Console.WriteLine(machine.Detail);
foreach (var row in machine.Categories) Console.WriteLine($"  {row.Name}: {row.Bytes?.ToString("N0") ?? "unavailable"} [{row.Status}]");
Check(machine.Categories.All(r => r.Bytes.HasValue), "Machine inputs unavailable: " + machine.Categories.Last().Detail);
Check(machine.Categories.Sum(r => r.Bytes!.Value) == (long)machine.UsableBytes!.Value, "Machine reconciliation failed.");
Console.WriteLine($"Machine reconciliation: {machine.Categories.Sum(r => r.Bytes!.Value):N0} = usable {machine.UsableBytes:N0}");
var tags = await new WindowsPoolTagProvider().CaptureAsync(token);
var leads = await new WindowsPoolTagDriverProvider().CaptureAsync(tags.Tags.Select(t => t.RawTag).ToArray(), null, token);
Console.WriteLine($"Pool driver leads: {leads.Status}; {leads.Detail}");
foreach (var lead in leads.Leads.Where(l => l.TotalMatches > 0).Take(6))
    Console.WriteLine($"  {RamAttributionPresentation.PoolTag(lead.Tag)}: {string.Join(", ", lead.CandidateFiles)} ({lead.TotalMatches} matches)");
Check(leads.Leads.Any(l => l.TotalMatches > 0), "No locally verified tag/driver string matches.");
var physical = await new WindowsPhysicalMemoryProvider().CaptureAsync(null, token);
Console.WriteLine($"RAMMap: {physical.Status}; {physical.Detail}");
foreach (var use in physical.Counts.GroupBy(c => c.Use)) Console.WriteLine($"  {RamAttributionPresentation.UseName(use.Key)} [Superfetch PFN attributes]: " + string.Join(", ", use.Select(c => $"{c.List}={c.Pages:N0}")));
if (physical.MemoryLists is { } lists)
{
    Console.WriteLine("Standby priorities: " + string.Join(", ", lists.StandbyByPriority));
    Console.WriteLine("Repurposed priorities: " + string.Join(", ", lists.RepurposedByPriority));
}
using var currentIdentity = WindowsIdentity.GetCurrent();
if (new WindowsPrincipal(currentIdentity).IsInRole(WindowsBuiltInRole.Administrator))
{
    Check(physical.Status == RamReadingStatus.Available, "Elevated PFN query failed: " + physical.Detail);
    var usablePages = machine.UsableBytes!.Value / (uint)Environment.SystemPageSize;
    var difference = Math.Abs((double)physical.PagesScanned - usablePages) / usablePages;
    Console.WriteLine($"PFN pages={physical.PagesScanned:N0}; usable pages={usablePages:N0}; difference={difference:P2}");
    Check(difference < .05, "Implausible PFN coverage compared with usable memory.");
}
else Check(physical.Status == RamReadingStatus.RequiresAdministrator, "Non-admin provider must refuse physical query.");
Console.WriteLine("PASS RAM attribution native smoke");
