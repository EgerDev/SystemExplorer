using System.Diagnostics;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SystemExplorer.Core;
using SystemExplorer.Windows;

if (args.Length == 2 && args[0] == "--churn-child" &&
    int.TryParse(args[1], out int childDelay) && childDelay is 1200 or 2200)
{
    Thread.Sleep(childDelay);
    return;
}

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

static async Task CheckProcessChurn(WindowsMonitorProvider provider, CancellationToken outerToken)
{
    const int rounds = 2, childrenPerRound = 8;
    var clock = Stopwatch.StartNew();
    using var budget = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
    budget.CancelAfter(TimeSpan.FromSeconds(15));
    int started = 0, captures = 0, observed = 0, exitWindowCaptures = 0;
    var allIdentities = new HashSet<ProcessIdentity>();

    void Validate(MonitorSnapshot snapshot)
    {
        captures++;
        Check(snapshot.Processes.Select(p => p.Identity.Pid).Distinct().Count() == snapshot.Processes.Count,
            "Churn snapshot contains duplicate PIDs.");
        long captureStart = snapshot.CapturedAt.UtcDateTime.ToFileTimeUtc();
        Check(snapshot.Processes.All(p => !p.Identity.IsStable || p.Identity.CreationTimeFileTime <= captureStart),
            "Churn snapshot joined a process lifetime newer than capture start.");
    }

    for (int round = 0; round < rounds; round++)
    {
        var children = new List<Process>();
        var identities = new HashSet<ProcessIdentity>();
        var seen = new HashSet<ProcessIdentity>();
        Task<MonitorSnapshot>? birthCapture = null;
        try
        {
            for (int index = 0; index < childrenPerRound; index++)
            {
                budget.Token.ThrowIfCancellationRequested();
                if (index == childrenPerRound / 2) birthCapture = provider.CaptureAsync(budget.Token);
                string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing smoke executable path.");
                var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
                if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                    start.ArgumentList.Add(System.Reflection.Assembly.GetExecutingAssembly().Location);
                start.ArgumentList.Add("--churn-child");
                start.ArgumentList.Add(index < childrenPerRound / 2 ? "1200" : "2200");
                var child = Process.Start(start) ?? throw new InvalidOperationException("Failed to start owned churn child.");
                children.Add(child);
                started++;
                if (!ChurnProcessTimes.GetProcessTimes(child.SafeHandle, out long created, out _, out _, out _))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read owned child's creation time.");
                Check(created > 0, "Owned child requires a real creation FILETIME.");
                var identity = new ProcessIdentity(child.Id, created);
                Check(identities.Add(identity), "Duplicate owned child lifetime.");
                allIdentities.Add(identity);
            }

            var birth = await birthCapture!;
            Validate(birth);
            seen.UnionWith(birth.Processes.Select(p => p.Identity).Where(identities.Contains));
            while (children.Any(child => !child.HasExited))
            {
                budget.Token.ThrowIfCancellationRequested();
                int alive = children.Count(child => !child.HasExited);
                if (alive == 0) break;
                var duringExit = await provider.CaptureAsync(budget.Token);
                Validate(duringExit);
                if (alive < children.Count) exitWindowCaptures++;
                seen.UnionWith(duringExit.Processes.Select(p => p.Identity).Where(identities.Contains));
                await Task.Delay(100, budget.Token);
            }
            await Task.WhenAll(children.Select(child => child.WaitForExitAsync(budget.Token)));
            Check(children.All(child => child.ExitCode == 0), "Owned churn child failed.");
            Check(seen.Count > 0, "Churn scenario did not observe any owned live child lifetime.");
            observed += seen.Count;
            var afterExit = await provider.CaptureAsync(budget.Token);
            Validate(afterExit);
            Check(afterExit.Processes.All(p => !allIdentities.Contains(p.Identity)),
                "A terminated owned child lifetime remained in a post-exit snapshot.");
        }
        finally
        {
            // Children exit themselves; never terminate a process by a potentially reused PID.
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                try
                {
                    if (birthCapture is not null) await birthCapture.WaitAsync(cleanup.Token);
                }
                finally
                {
                    await Task.WhenAll(children.Select(child => child.WaitForExitAsync(cleanup.Token)));
                }
            }
            finally
            {
                foreach (var child in children) child.Dispose();
            }
        }
    }
    Check(exitWindowCaptures > 0, "Churn scenario did not capture a partial-exit window.");
    Check(clock.Elapsed < TimeSpan.FromSeconds(20), "Churn fixture exceeded its 20-second bound.");
    Console.WriteLine($"PASS: real process churn; rounds={rounds}; children={started}; observed_lifetimes={observed}; snapshots={captures}; partial_exit_snapshots={exitWindowCaptures}; elapsed_ms={clock.ElapsedMilliseconds}; PID reuse not induced or asserted.");
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
await CheckProcessChurn(provider, timeout.Token);
provider.Dispose();
try
{
    await provider.CaptureAsync(CancellationToken.None);
    throw new InvalidOperationException("Disposed provider accepted capture.");
}
catch (ObjectDisposedException) { }

Console.WriteLine($"PASS: live self identity/memory/owner/CPU, memory consistency, PDH warmup/availability, null unsupported metrics, cancellation, serialized scans, disposed rejection. rows={second.Processes.Count}; stable={second.Processes.Count(p => p.Identity.IsStable)}; partial={second.Processes.Count(p => p.Availability != DataAvailability.Available)}; physical_disk_available={second.DiskReadBytesPerSecond.HasValue && second.DiskWriteBytesPerSecond.HasValue}");

internal static class ChurnProcessTimes
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessTimes(SafeProcessHandle process, out long creation,
        out long exit, out long kernel, out long user);
}
