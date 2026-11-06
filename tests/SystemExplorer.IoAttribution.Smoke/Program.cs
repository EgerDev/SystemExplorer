using System.Net;
using System.Diagnostics;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Microsoft.Diagnostics.Tracing.Session;
using SystemExplorer.Core;
using SystemExplorer.Windows;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

var empty = new IoAttributionAggregator();
Check(empty.GetRows(_ => (null, null)).Count == 0 && empty.CompletionStatus == IoAttributionStatus.Completed, "Empty capture.");
Check(IoAttributionAggregator.ClampDuration(TimeSpan.MinValue) == TimeSpan.FromSeconds(1)
    && IoAttributionAggregator.ClampDuration(TimeSpan.MaxValue) == TimeSpan.FromSeconds(60)
    && IoAttributionAggregator.ClampDuration(TimeSpan.FromSeconds(3)) == TimeSpan.FromSeconds(3), "Duration clamp.");
var aggregate = new IoAttributionAggregator();
foreach (var kind in Enum.GetValues<IoAttributionKind>()) aggregate.Add(new(12, kind, 100));
aggregate.Add(new(12, IoAttributionKind.NetworkSend, 25));
aggregate.Add(new(-1, IoAttributionKind.DiskRead, 10));
aggregate.Add(new(99, (IoAttributionKind)100, 10));
var row = aggregate.GetRows(pid => (new ProcessIdentity(pid, 123), "fake")).Single();
Check(row.NetworkSendBytes == 125 && row.NetworkReceiveBytes == 100 && row.DiskReadBytes == 100 && row.DiskWriteBytes == 100 && row.EventCount == 5 && row.Identity?.IsStable == true, "Byte aggregation and stable identity.");
foreach (var identity in new ProcessIdentity?[] { null, new(12, null), new(12, 0), new(13, 123) })
    Check(aggregate.GetRows(_ => (identity, null)).Single().Identity is null, "Unresolvable/mismatched identity must stay unstable.");
for (var pid = 0; pid <= IoAttributionAggregator.MaximumPids; pid++) empty.Add(new(pid, IoAttributionKind.DiskRead, 1));
empty.Add(new(0, IoAttributionKind.DiskRead, 1));
var bounded = empty.GetRows(_ => (null, null));
Check(empty.CompletionStatus == IoAttributionStatus.Truncated && bounded.Count == 4096 && bounded[0].DiskReadBytes == 2, "PID bound must drop only new PIDs.");
Console.WriteLine("PASS: pure aggregation, empty/invalid events, duration clamp, stable/unstable identity and 4096-PID truncation.");

var resolverStart = DateTimeOffset.UtcNow;
using (var monitor = new WindowsMonitorProvider())
{
    var snapshot = await monitor.CaptureAsync(CancellationToken.None);
    var candidate = snapshot.Processes.First(p => p.Identity.IsStable && p.Identity.Pid != Environment.ProcessId
        && p.Identity.CreationTimeFileTime < resolverStart.UtcDateTime.ToFileTimeUtc()
        && WindowsIoAttributionCapture.Resolve(p.Identity.Pid, resolverStart).Name is not null);
    var resolved = WindowsIoAttributionCapture.Resolve(candidate.Identity.Pid, resolverStart);
    Check(resolved.Identity == candidate.Identity && !string.IsNullOrWhiteSpace(resolved.Name), "Pre-existing non-self identity must match native monitor.");
    Check(WindowsIoAttributionCapture.Resolve(candidate.Identity.Pid, DateTimeOffset.FromFileTime(candidate.Identity.CreationTimeFileTime!.Value).AddTicks(-1)).Identity is null,
        "New PID must not acquire stable identity.");
    Console.WriteLine($"PASS: native non-self resolver PID={candidate.Identity.Pid}; name={resolved.Name}; identity={resolved.Identity}; matches WindowsMonitorProvider.");
}
Check(WindowsIoAttributionCapture.Resolve(-1, resolverStart) == (null, null), "Invalid PID stays unstable.");
using (var exited = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!)
{
    var exitedPid = exited.Id;
    await exited.WaitForExitAsync();
    Check(WindowsIoAttributionCapture.Resolve(exitedPid, DateTimeOffset.UtcNow) == (null, null), "Exited PID stays unstable.");
}
Console.WriteLine("PASS: new, invalid and exited PIDs remain unstable.");

var provider = new WindowsIoAttributionCapture();
using var alreadyCancelled = new CancellationTokenSource();
alreadyCancelled.Cancel();
var cancelled = await provider.CaptureAsync(TimeSpan.FromSeconds(3), alreadyCancelled.Token);
Check(cancelled.Status is IoAttributionStatus.Cancelled or IoAttributionStatus.RequiresAdministrator, "Pre-cancellation.");
var before = TraceEventSession.GetActiveSessionNames().Where(n => n.StartsWith("SystemExplorer-IO-", StringComparison.Ordinal)).ToHashSet();
var captureTask = provider.CaptureAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
await Task.Delay(350);
var during = TraceEventSession.GetActiveSessionNames().Where(n => n.StartsWith("SystemExplorer-IO-", StringComparison.Ordinal) && !before.Contains(n)).ToArray();
if (!captureTask.IsCompleted)
{
    var path = Path.Combine(Path.GetTempPath(), $"SystemExplorer-IO-Smoke-{Guid.NewGuid():N}.bin");
    try
    {
        var bytes = new byte[4 * 1024 * 1024];
        Random.Shared.NextBytes(bytes);
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            file.Write(bytes);
            file.Flush(true);
        }
        using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, (FileOptions)0x20000000))
        {
            var allocation = Marshal.AllocHGlobal(bytes.Length + 65536);
            try
            {
                var aligned = (nint)((allocation.ToInt64() + 65535) & ~65535L);
                Check(Native.ReadFile(handle, aligned, (uint)bytes.Length, out var readBytes, 0) && readBytes == bytes.Length,
                    $"Unbuffered disk read failed: {Marshal.GetLastWin32Error()}.");
            }
            finally { Marshal.FreeHGlobal(allocation); }
        }
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var sender = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await sender.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        using var receiver = await accept;
        var read = receiver.GetStream().ReadExactlyAsync(bytes).AsTask();
        await sender.GetStream().WriteAsync(bytes);
        await read;
    }
    finally { File.Delete(path); }
}
var capture = await captureTask;
Console.WriteLine($"Live: status={capture.Status}; duration={capture.ActualDuration}; lost={capture.EventsLost}; {capture.Detail}");
Check(during.All(name => !TraceEventSession.GetActiveSessionNames().Contains(name)), "Capture session must be removed.");
if (capture.Status == IoAttributionStatus.RequiresAdministrator)
{
    Console.WriteLine("SKIP: live attribution requires administrator; no elevation attempted.");
    return;
}
Check(capture.Status == IoAttributionStatus.Completed, "Elevated live capture must complete.");
Check(during.Length == 1, "Exactly one private session must be observed.");
var own = capture.Processes.SingleOrDefault(p => p.Pid == Environment.ProcessId);
Console.WriteLine($"Own PID {Environment.ProcessId}: send={own?.NetworkSendBytes}; receive={own?.NetworkReceiveBytes}; read={own?.DiskReadBytes}; write={own?.DiskWriteBytes}; identity={own?.Identity}");
Check(own is not null && own.NetworkSendBytes > 0 && own.NetworkReceiveBytes > 0 && own.DiskReadBytes > 0 && own.DiskWriteBytes > 0 && own.Identity?.IsStable == true, "Own PID network and file read/write attribution.");
Console.WriteLine("PASS: live 3-second own-PID TCP and disk read/write attribution and private session cleanup.");
using var cancelLive = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
var partial = await provider.CaptureAsync(TimeSpan.FromSeconds(3), cancelLive.Token);
Console.WriteLine($"Cancellation: status={partial.Status}; duration={partial.ActualDuration}; {partial.Detail}");
Check(partial.Status == IoAttributionStatus.Cancelled && partial.ActualDuration < TimeSpan.FromSeconds(3), "In-flight cancellation must return partial capture.");
Check(!TraceEventSession.GetActiveSessionNames().Any(n => n.StartsWith("SystemExplorer-IO-", StringComparison.Ordinal) && !before.Contains(n)), "Cancellation session cleanup.");
Console.WriteLine("PASS: in-flight cancellation and session cleanup.");

static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadFile(SafeFileHandle file, nint buffer, uint count, out uint read, nint overlapped);
}
