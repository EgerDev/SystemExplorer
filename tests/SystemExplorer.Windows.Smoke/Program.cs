using System.Diagnostics;
using SystemExplorer.Core;
using SystemExplorer.Windows;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static async Task ExpectCancellation(Func<Task> action)
{
    try { await action(); }
    catch (OperationCanceledException) { return; }
    throw new InvalidOperationException("Expected cancellation.");
}

using var selfProcess = Process.GetCurrentProcess();
int selfPid = Environment.ProcessId;
long creation = selfProcess.StartTime.ToUniversalTime().ToFileTimeUtc();
using var provider = new WindowsMonitorProvider();
using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var first = await provider.CaptureAsync(timeout.Token);
var self = first.Processes.Single(p => p.Identity.Pid == selfPid);
Check(self.Identity.CreationTimeFileTime == creation, "Self lifetime must match independently observed process creation.");
Check(self.CpuPercent is null && self.ReadBytesPerSecond is null, "First deltas must be unavailable.");
Check(self.PrivateBytes > 0 && self.WorkingSetBytes > 0, "Live self memory must be available.");
Check(!string.IsNullOrWhiteSpace(self.ExecutablePath), "Live self path must be available.");
Check(!string.IsNullOrWhiteSpace(self.UserSid) && !string.IsNullOrWhiteSpace(self.UserName), "Live self owner must be available.");
Check(self.SessionId is not null && self.ThreadCount > 0 && self.HandleCount > 0, "Live self metadata must be available.");
Check(first.Memory.PhysicalTotalBytes > 0 && first.Memory.PhysicalAvailableBytes is not null &&
    first.Memory.PhysicalTotalBytes >= first.Memory.PhysicalAvailableBytes, "Physical memory must be consistent.");
Check(first.Memory.CommitTotalBytes is not null && first.Memory.CommitLimitBytes > 0, "Commit metrics must be available.");
Check(first.TotalCpuPercent is null, "First system CPU sample must be unavailable.");
Check(first.DiskReadBytesPerSecond is null && first.DiskWriteBytesPerSecond is null, "First PDH rate sample must be unavailable.");
Check(first.Processes.All(p => p.PrivateWorkingSetBytes is null) && first.Memory.StandbyBytes is null &&
    first.DiskReadBytesPerSecond is null && first.GpuPercent is null, "Unsupported metrics must stay null.");
Check(first.Processes.All(p => p.Identity.IsStable ||
    (p.CpuPercent is null && !string.IsNullOrWhiteSpace(p.UnavailableReason))), "Unknown identity must not have CPU history.");
Check(first.Processes.Select(p => p.Identity.Pid).Distinct().Count() == first.Processes.Count, "No duplicate PID rows.");

await Task.Run(() =>
{
    var clock = Stopwatch.StartNew();
    while (clock.Elapsed < TimeSpan.FromSeconds(1.1))
    {
        timeout.Token.ThrowIfCancellationRequested();
        Thread.SpinWait(10000);
    }
}, timeout.Token);
var second = await provider.CaptureAsync(timeout.Token);
var selfSecond = second.Processes.Single(p => p.Identity == self.Identity);
Check(selfSecond.CpuPercent is > 0 and <= 100, "Live self CPU delta must be positive after work.");
Check(selfSecond.ReadBytesPerSecond is >= 0 && selfSecond.WriteBytesPerSecond is >= 0, "Live self I/O rates must be available.");
Check(second.TotalCpuPercent is >= 0 and <= 100 ||
    second.Limitations.Any(l => l.Contains("multiple processor groups")), "System CPU must be available or explicitly group-limited.");
Check((second.DiskReadBytesPerSecond is >= 0 && double.IsFinite(second.DiskReadBytesPerSecond.Value) &&
    second.DiskWriteBytesPerSecond is >= 0 && double.IsFinite(second.DiskWriteBytesPerSecond.Value)) ||
    second.Limitations.Any(l => l.StartsWith("Physical disk")), "Physical disk rates must be valid or explicitly unavailable.");

using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    await ExpectCancellation(() => provider.CaptureAsync(cancelled.Token));
}
using (var cancelled = new CancellationTokenSource())
{
    Task<MonitorSnapshot> pending = provider.CaptureAsync(cancelled.Token);
    cancelled.Cancel();
    await ExpectCancellation(() => pending);
}
var concurrent = await Task.WhenAll(provider.CaptureAsync(timeout.Token), provider.CaptureAsync(timeout.Token));
Check(concurrent.All(s => s.Processes.Any(p => p.Identity == self.Identity)), "Serialized concurrent captures must retain self.");
provider.Dispose();
try
{
    await provider.CaptureAsync(CancellationToken.None);
    throw new InvalidOperationException("Disposed provider accepted capture.");
}
catch (ObjectDisposedException) { }

Console.WriteLine($"PASS: live self identity/memory/owner/CPU, memory consistency, PDH warmup/availability, null unsupported metrics, cancellation, serialized scans, disposed rejection. rows={second.Processes.Count}; stable={second.Processes.Count(p => p.Identity.IsStable)}; partial={second.Processes.Count(p => p.Availability != DataAvailability.Available)}; physical_disk_available={second.DiskReadBytesPerSecond.HasValue && second.DiskWriteBytesPerSecond.HasValue}");
