using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using SystemExplorer.Core;
using SystemExplorer.Windows;

static void Check(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
static bool Active(string? name) => name is not null && TraceEventSession.GetActiveSessionNames().Contains(name);
if (args.Contains("--nonadmin"))
{
    await using var gated = await WindowsActivityTraceRecorder.StartAsync(new(), []);
    var gatedResult = await gated.Completion;
    Check(gatedResult.Status == TraceStatus.RequiresAdministrator && gated.SessionName is null, "Restricted token must return RequiresAdministrator with no session attempted.");
    Console.WriteLine("PASS: restricted-token RequiresAdministrator; no session created.");
    return;
}
using var own = Process.GetCurrentProcess();
TraceProcessSeed[] seeds = [new(Environment.ProcessId, -1, own.StartTime.ToUniversalTime().ToFileTimeUtc(), own.ProcessName)];
var before = TraceEventSession.GetActiveSessionNames().Where(n => n.StartsWith(WindowsActivityTraceRecorder.SessionPrefix, StringComparison.Ordinal)).ToHashSet();
using var preCancelled = new CancellationTokenSource(); preCancelled.Cancel();
await using (var pre = await WindowsActivityTraceRecorder.StartAsync(new(), seeds, preCancelled.Token))
{
    var result = await pre.Completion;
    Check(result.Status is TraceStatus.Cancelled or TraceStatus.RequiresAdministrator, "Pre-cancellation/admin gate.");
    Check(pre.SessionName is null, "Pre-cancellation/non-admin must not attempt a session.");
    if (result.Status == TraceStatus.RequiresAdministrator)
    {
        Console.WriteLine("PASS: RequiresAdministrator; no session attempted.");
        Console.WriteLine("SKIP: live activity tracing requires administrator; no elevation attempted.");
        return;
    }
}
Console.WriteLine("PASS: pre-cancellation creates no session.");

using (var dead = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false })!)
{
    int deadPid = dead.Id; await dead.WaitForExitAsync();
    string staleName = $"{WindowsActivityTraceRecorder.SessionPrefix}{deadPid}-{Guid.NewGuid():N}";
    string liveName = $"{WindowsActivityTraceRecorder.SessionPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}";
    string foreignName = $"SystemExplorer-Smoke-Unrelated-{Guid.NewGuid():N}";
    using var stale = new TraceEventSession(staleName) { StopOnDispose = true };
    using var live = new TraceEventSession(liveName) { StopOnDispose = true };
    using var foreign = new TraceEventSession(foreignName) { StopOnDispose = true };
    foreach (var s in new[] { stale, live, foreign }) s.EnableProvider("Microsoft-Windows-Kernel-Process", TraceEventLevel.Informational, 0x10);
    Check(Active(staleName) && Active(liveName) && Active(foreignName), "Cleanup fixtures must be active.");
    WindowsActivityTraceRecorder.CleanupStaleSessions();
    Check(!Active(staleName) && Active(liveName) && Active(foreignName), "Cleanup must remove only dead-owner prefixed sessions.");
    stale.StopOnDispose = false;
}
Console.WriteLine("PASS: stale dead-owner cleanup; live-owner and non-prefixed sessions untouched.");

var path = Path.Combine(Path.GetTempPath(), $"SystemExplorer-Trace-Smoke-{Guid.NewGuid():N}.bin");
var keyPath = $"Software\\SystemExplorer-Smoke-{Guid.NewGuid():N}";
var secretName = $"ValueName-{Guid.NewGuid():N}";
var secretData = $"ValueData-{Guid.NewGuid():N}";
int childPid = -1;
var endpoints = new List<string>();
byte[] bytes = new byte[1024 * 1024]; Random.Shared.NextBytes(bytes);
own.Refresh(); var initialCpu = own.TotalProcessorTime; long sampledPeak = own.WorkingSet64;
var watch = Stopwatch.StartNew();
await using (var trace = await WindowsActivityTraceRecorder.StartAsync(new(Duration: TimeSpan.FromSeconds(10), ExcludeSelf: false), seeds))
{
    Check(trace.Status == TraceStatus.Running && Active(trace.SessionName), "Private session must be running.");
    try
    {
        using (var child = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit 7") { CreateNoWindow = true, UseShellExecute = false })!)
        { childPid = child.Id; await child.WaitForExitAsync(); }
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough))
        { file.Write(bytes); file.Flush(true); }
        using (var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, (FileOptions)0x20000000))
        {
            var allocation = Marshal.AllocHGlobal(bytes.Length + 65536);
            try
            {
                var aligned = (nint)((allocation.ToInt64() + 65535) & ~65535L);
                Check(Native.ReadFile(handle, aligned, (uint)bytes.Length, out uint read, 0) && read == bytes.Length, "Unbuffered smoke read.");
            }
            finally { Marshal.FreeHGlobal(allocation); }
        }
        File.Move(path, path + ".renamed"); File.Delete(path + ".renamed");
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath)) { key.SetValue(secretName, secretData); key.DeleteValue(secretName); }
        using (var key = Registry.CurrentUser.OpenSubKey(keyPath)) Check(key is not null, "Open smoke registry key.");
        Registry.CurrentUser.DeleteSubKey(keyPath);
        foreach (var address in new[] { IPAddress.Loopback, IPAddress.IPv6Loopback })
        {
            using var listener = new TcpListener(address, 0); listener.Start();
            using var sender = new TcpClient(address.AddressFamily); var accept = listener.AcceptTcpClientAsync();
            await sender.ConnectAsync((IPEndPoint)listener.LocalEndpoint); using var receiver = await accept;
            var local = (IPEndPoint)sender.Client.LocalEndPoint!; var remote = (IPEndPoint)sender.Client.RemoteEndPoint!;
            endpoints.Add(address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]:{local.Port} -> [{address}]:{remote.Port}" : $"{address}:{local.Port} -> {address}:{remote.Port}");
            var reading = receiver.GetStream().ReadExactlyAsync(bytes).AsTask(); await sender.GetStream().WriteAsync(bytes); await reading;
        }
        // Bounded, repeatable load throughout the recording; no external connections.
        while (!trace.Completion.IsCompleted)
        {
            using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough)) file.Write(bytes, 0, 4096);
            File.Delete(path);
            own.Refresh(); sampledPeak = Math.Max(sampledPeak, own.WorkingSet64);
            Check(trace.Live().Tail.Count <= 100, "Live tail is bounded.");
            await Task.Delay(100);
        }
        var result = await trace.Completion; watch.Stop(); own.Refresh();
        Console.WriteLine($"10s capture: status={result.Status}; events={result.Events.Count}; lost={result.EventsLost}; dropped={result.BufferDropped}; malformed={result.Malformed}; unresolved={result.UnresolvedNames}");
        Console.WriteLine($"Measurement (smoke + recorder + generated load): elapsed={watch.Elapsed.TotalSeconds:F2}s; CPU={(own.TotalProcessorTime - initialCpu).TotalMilliseconds:F1}ms; sampled capture peak WS={sampledPeak / 1048576d:F1}MiB; process lifetime peak WS={own.PeakWorkingSet64 / 1048576d:F1}MiB");
        Check(result.Status is TraceStatus.Completed or TraceStatus.Partial, "Live capture must finish, not fail.");
        Check(result.Malformed == 0, "Host-supported event decoding must not be malformed.");
        if (result.EventsLost == 0 && result.BufferDropped == 0 && !result.CapsHit) Check(result.Status == TraceStatus.Completed, "Unresolved names alone must not make a capture Partial.");
        if (result.UnresolvedNames > 0) Check(result.Detail.Contains("events with unresolved names"), "Unresolved count must remain an explicit limitation.");
        var childInfo = result.Processes.Single(p => p.Pid == childPid && !p.Seeded);
        Check(childInfo.ParentPid == Environment.ProcessId && childInfo.ParentIndex == 0 && childInfo.ExitCode == 7 && childInfo.ExitUtc is not null && childInfo.ImageName?.Equals("cmd.exe", StringComparison.OrdinalIgnoreCase) == true, "Child start, verified parent, name and exit code.");
        Check(result.Events.Any(e => e.Pid == childPid && e.Operation == TraceOperation.ProcessStart) && result.Events.Any(e => e.Pid == childPid && e.Operation == TraceOperation.ProcessStop), "Child lifecycle rows.");
        Check(childInfo.ThreadsStarted > 0 && childInfo.ThreadsStopped > 0 && result.Events.Any(e => e.Pid == childPid && e.Operation == TraceOperation.ImageLoad), "Thread counters and image load rows.");
        bool FileEvent(TraceOperation op, bool sized = false) => result.Events.Any(e => e.Pid == Environment.ProcessId && e.Operation == op && e.Path?.Contains(Path.GetFileName(path), StringComparison.OrdinalIgnoreCase) == true && (!sized || e.Bytes > 0));
        foreach (var op in new[] { TraceOperation.FileCreate, TraceOperation.FileRead, TraceOperation.FileWrite, TraceOperation.FileRename, TraceOperation.FileDelete }) Check(FileEvent(op, op is TraceOperation.FileRead or TraceOperation.FileWrite), "File operation missing: " + op);
        foreach (var op in new[] { TraceOperation.RegistryCreate, TraceOperation.RegistryOpen, TraceOperation.RegistrySetValue, TraceOperation.RegistryDeleteValue, TraceOperation.RegistryDeleteKey })
            Check(result.Events.Any(e => e.Pid == Environment.ProcessId && e.Operation == op && e.Path?.Contains(keyPath, StringComparison.OrdinalIgnoreCase) == true), "Registry operation missing: " + op);
        foreach (var endpoint in endpoints) Check(result.Events.Any(e => e.Pid == Environment.ProcessId && e.Operation == TraceOperation.NetConnect && e.Path == endpoint), "Loopback address/port decode differs from socket endpoints.");
        foreach (var op in new[] { TraceOperation.NetConnect, TraceOperation.NetAccept, TraceOperation.NetSend, TraceOperation.NetReceive, TraceOperation.NetDisconnect })
            Check(result.Events.Any(e => e.Pid == Environment.ProcessId && e.Operation == op && (op is not (TraceOperation.NetSend or TraceOperation.NetReceive) || e.Bytes > 0)), "Network operation missing: " + op);
        using var csv = new StringWriter(); TraceExport.WriteCsv(csv, result); using var json = new MemoryStream(); TraceExport.WriteJson(json, result);
        string exported = csv + System.Text.Encoding.UTF8.GetString(json.ToArray());
        foreach (var forbidden in new[] { "/c exit 7", secretName, secretData }) Check(!exported.Contains(forbidden, StringComparison.Ordinal), "Forbidden payload leaked into exports.");
        Check(!Active(trace.SessionName), "Automatic completion must remove private session.");
        Check(ReferenceEquals(result, await trace.StopAsync()) && ReferenceEquals(result, await trace.StopAsync()), "Stop must be idempotent.");
    }
    finally { File.Delete(path); File.Delete(path + ".renamed"); Registry.CurrentUser.DeleteSubKeyTree(keyPath, false); }
}
Console.WriteLine("PASS: child exit 7/parent/name; file create/read/write/rename/delete; registry key-only operations; TCPv4/v6 endpoints and bytes; no sensitive payload leakage; duration completion and cleanup.");

using (var cancel = new CancellationTokenSource())
await using (var trace = await WindowsActivityTraceRecorder.StartAsync(new(Duration: TimeSpan.FromSeconds(30)), seeds, cancel.Token))
{
    await Task.Delay(300); var elapsed = Stopwatch.StartNew(); cancel.Cancel(); var result = await trace.Completion;
    Check(result.Status == TraceStatus.Cancelled && elapsed.Elapsed < TimeSpan.FromSeconds(5) && !Active(trace.SessionName), "Cancellation must promptly stop and remove session.");
}
Console.WriteLine("PASS: in-flight cancellation and session cleanup.");

await using (var trace = await WindowsActivityTraceRecorder.StartAsync(new(TraceCategories.File, TimeSpan.FromSeconds(30), 10000, false), seeds))
{
    try
    {
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1, FileOptions.WriteThrough))
            for (int i = 0; i < 14000; i++) file.Write(bytes, 0, 4096);
        await Task.Delay(1200);
        var result = await trace.StopAsync();
        Console.WriteLine($"Overflow: status={result.Status}; retained={result.Events.Count}; dropped={result.BufferDropped}; lost={result.EventsLost}");
        Check(result.Status == TraceStatus.Partial && result.BufferDropped > 0 && result.Events.Count == 10000 && !Active(trace.SessionName), "Real ETW ring overflow must be Partial and cleaned up.");
        Check(result.Events.All(e => SystemExplorer.Core.TraceEvent.Category(e.Operation) == TraceCategories.File), "Only selected category rows.");
    }
    finally { File.Delete(path); }
}
Console.WriteLine("PASS: bounded ring overflow yields Partial; selected category only; explicit stop cleanup.");

await using (var trace = await WindowsActivityTraceRecorder.StartAsync(new(TraceCategories.File, TimeSpan.FromSeconds(30), ExcludeSelf: true), seeds))
{
    try
    {
        using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough)) { file.Write(bytes); file.Flush(true); }
        await Task.Delay(1200); var result = await trace.StopAsync();
        Console.WriteLine($"ExcludeSelf: status={result.Status}; excluded={result.ExcludedSelf}; storedOwn={result.Events.Count(e => e.Pid == Environment.ProcessId)}; malformed={result.Malformed}; active={Active(trace.SessionName)}");
        Check(result.Events.All(e => e.Pid != Environment.ProcessId) && result.ExcludedSelf > 0 && !Active(trace.SessionName), "ExcludeSelf must count excluded events and store none.");
    }
    finally { File.Delete(path); }
}
Console.WriteLine("PASS: ExcludeSelf counts exclusions without storing own events.");

await using (var trace = await WindowsActivityTraceRecorder.StartCoreAsync(new(TraceCategories.File, TimeSpan.FromSeconds(30), ExcludeSelf: false), seeds, CancellationToken.None, 1))
{
    try
    {
        using (var first = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
        using (var second = new FileStream(path + ".second", FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough))
        { first.Write(bytes); second.Write(bytes); }
        await Task.Delay(1200); var result = await trace.StopAsync();
        Check(result.CapsHit && result.Status == TraceStatus.Partial && !Active(trace.SessionName), "Bounded name maps must report Partial on overflow.");
    }
    finally { File.Delete(path); File.Delete(path + ".second"); }
}
Console.WriteLine("PASS: bounded name-map cap reports Partial and cleans up.");

var disposed = await WindowsActivityTraceRecorder.StartAsync(new(TraceCategories.Process, TimeSpan.FromSeconds(30)), seeds);
string? disposedName = disposed.SessionName; disposed.Dispose(); disposed.Dispose();
Check(disposed.Completion.IsCompletedSuccessfully && !Active(disposedName), "Dispose stops the private session and is idempotent.");
Console.WriteLine("PASS: synchronous Dispose stops session idempotently.");
Check(!TraceEventSession.GetActiveSessionNames().Any(n => n.StartsWith(WindowsActivityTraceRecorder.SessionPrefix, StringComparison.Ordinal) && !before.Contains(n)), "No activity session left behind.");
Console.WriteLine("PASS: no SystemExplorer-Trace session left behind.");

static class Native
{
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadFile(SafeFileHandle file, nint buffer, uint count, out uint read, nint overlapped);
}
