using System.Diagnostics;
using System.Security.Principal;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using SystemExplorer.Core;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SystemExplorer.IoAttribution.Smoke")]

namespace SystemExplorer.Windows;

/// <summary>
/// Opt-in real-time private ETW session. Kernel-Network send/receive payload PID and size include TCP/UDP v4/v6.
/// Kernel-File Read/Write IoSize and event-header PID measure requested file I/O, not physical device transfers
/// or successfully completed bytes. This source works in a private session without kernel logger privileges.
/// Cached requests can be counted; lazy-writer/paging work may be attributed to System or omitted. Neither
/// file names nor network endpoints are retained. PID identity is conservative: only a still-running process
/// created before capture began is resolved; exited/reused/new PIDs remain unstable.
/// </summary>
public sealed class WindowsIoAttributionCapture : IIoAttributionCapture
{
    private static readonly Guid NetworkProvider = new("7dd42a49-5329-4832-8dfd-43d979153a88");
    private static readonly Guid FileProvider = new("edd08927-9cc4-4e65-b970-c2560fb5c289");
    private const string Limits = "Measured ETW TCP/UDP send/receive bytes and Kernel-File requested read/write IoSize (not physical disk transfers or completed bytes). Cached requests may count; lazy-writer/paging I/O may belong to System or be omitted. Exited/reused/new PIDs have unstable identity. No endpoints or file names retained.";

    public async Task<IoAttributionCapture> CaptureAsync(TimeSpan duration, CancellationToken token)
    {
        var started = DateTimeOffset.UtcNow;
        var elapsed = new Stopwatch();
        var aggregate = new IoAttributionAggregator();
        var status = IoAttributionStatus.Completed;
        var detail = Limits;
        long lost = 0;
        long malformed = 0;
        long cutoffUtcTicks = DateTime.MaxValue.Ticks;
        TraceEventSession? session = null;
        Task? processing = null;
        var stopLock = new object();
        void Stop()
        {
            lock (stopLock)
            {
                try { session?.Stop(); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { }
            }
        }
        EventHandler exit = (_, _) => Stop();
        ConsoleCancelEventHandler cancel = (_, _) => Stop();
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
                return new(started, TimeSpan.Zero, IoAttributionStatus.RequiresAdministrator, "Unavailable: administrator token required. No session attempted.", 0, []);
            if (token.IsCancellationRequested)
                return new(started, TimeSpan.Zero, IoAttributionStatus.Cancelled, "Cancelled before session creation.", 0, []);

            session = new TraceEventSession($"SystemExplorer-IO-{Guid.NewGuid():N}") { StopOnDispose = true };
            AppDomain.CurrentDomain.ProcessExit += exit;
            Console.CancelKeyPress += cancel;
            session.Source.Dynamic.All += data =>
            {
                var timestamp = data.TimeStamp.ToUniversalTime().Ticks;
                if (timestamp < started.UtcTicks || timestamp > Interlocked.Read(ref cutoffUtcTicks)) return;
                IoAttributionKind kind;
                int pid;
                ulong bytes;
                if (data.ProviderGuid == NetworkProvider)
                {
                    kind = (int)data.ID switch
                    {
                        10 or 26 or 42 or 58 => IoAttributionKind.NetworkSend,
                        11 or 27 or 43 or 59 => IoAttributionKind.NetworkReceive,
                        _ => (IoAttributionKind)(-1)
                    };
                    if ((int)kind < 0) return;
                    if (data.PayloadByName("PID") is not { } p || data.PayloadByName("size") is not { } n) { Interlocked.Increment(ref malformed); return; }
                    pid = Convert.ToInt32(p);
                    bytes = Convert.ToUInt64(n);
                }
                else if (data.ProviderGuid == FileProvider && (int)data.ID is 15 or 16)
                {
                    kind = (int)data.ID == 15 ? IoAttributionKind.DiskRead : IoAttributionKind.DiskWrite;
                    pid = data.ProcessID;
                    if (data.PayloadByName("IOSize") is not { } n) { Interlocked.Increment(ref malformed); return; }
                    bytes = Convert.ToUInt64(n);
                }
                else return;
                aggregate.Add(new(pid, kind, bytes));
            };
            session.EnableProvider(NetworkProvider, TraceEventLevel.Informational, 0x30);
            session.EnableProvider(FileProvider, TraceEventLevel.Informational, 0x300);
            started = DateTimeOffset.UtcNow;
            elapsed.Start();
            processing = Task.Factory.StartNew(() => session.Source.Process(), CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            var delay = Task.Delay(IoAttributionAggregator.ClampDuration(duration), token);
            if (await Task.WhenAny(delay, processing).ConfigureAwait(false) == processing)
            {
                await processing.ConfigureAwait(false);
                status = token.IsCancellationRequested ? IoAttributionStatus.Cancelled : IoAttributionStatus.Failed;
                detail = "Partial: session processing ended before the capture deadline. " + Limits;
            }
            else
            {
                try { await delay.ConfigureAwait(false); }
                catch (OperationCanceledException) { status = IoAttributionStatus.Cancelled; }
            }
        }
        catch (UnauthorizedAccessException ex) { status = IoAttributionStatus.SessionUnavailable; detail = "Unavailable: " + ex.Message + " " + Limits; }
        catch (System.ComponentModel.Win32Exception ex) { status = IoAttributionStatus.SessionUnavailable; detail = "Unavailable: " + ex.Message + " " + Limits; }
        catch (Exception ex) when (ex is not OutOfMemoryException) { status = IoAttributionStatus.Failed; detail = "Partial/failed: " + ex.Message + " " + Limits; }
        finally
        {
            elapsed.Stop();
            Interlocked.Exchange(ref cutoffUtcTicks, started.UtcTicks + elapsed.Elapsed.Ticks);
            if (session is not null)
            {
                try { lost = session.EventsLost; }
                catch (Exception ex) when (ex is not OutOfMemoryException) { detail += " EventsLost unavailable: " + ex.Message; status = IoAttributionStatus.Failed; }
                Stop();
                if (processing is not null)
                {
                    try { await processing.ConfigureAwait(false); }
                    catch (Exception ex) when (ex is not OutOfMemoryException) { status = IoAttributionStatus.Failed; detail += " Processing failed: " + ex.Message; }
                }
                lock (stopLock) { session.Dispose(); session = null; }
            }
            AppDomain.CurrentDomain.ProcessExit -= exit;
            Console.CancelKeyPress -= cancel;
        }
        if (status == IoAttributionStatus.Completed && (aggregate.IsTruncated || lost > 0 || malformed > 0)) status = IoAttributionStatus.Truncated;
        if (status != IoAttributionStatus.Completed) detail = $"{status}: partial/unavailable capture. " + detail;
        if (aggregate.IsTruncated) detail += " PID limit reached; new PIDs dropped.";
        if (lost > 0) detail += $" ETW lost {lost} events.";
        if (malformed > 0) detail += $" Skipped {malformed} events missing PID/size fields.";
        var rows = aggregate.GetRows(pid => Resolve(pid, started));
        return new(started, elapsed.Elapsed, status, detail, lost, rows);
    }

    internal static (ProcessIdentity? Identity, string? Name) Resolve(int pid, DateTimeOffset started)
    {
        if (pid < 0) return (null, null);
        using var process = NativeMethods.OpenProcess(NativeMethods.QueryLimited | 0x100000, false, (uint)pid);
        if (process.IsInvalid || NativeSecurityMethods.WaitForSingleObject(process, 0) != 258) return (null, null);
        if (!NativeMethods.GetProcessTimes(process, out var creation, out _, out _, out _)
            || creation.Ticks == 0 || creation.Ticks > (ulong)started.UtcDateTime.ToFileTimeUtc()) return (null, null);
        var path = new StringBuilder(32768);
        uint length = (uint)path.Capacity;
        var name = NativeMethods.QueryFullProcessImageNameW(process, 0, path, ref length)
            ? Path.GetFileNameWithoutExtension(path.ToString()) : null;
        if (NativeSecurityMethods.WaitForSingleObject(process, 0) != 258) return (null, null);
        return (new(pid, (long)creation.Ticks), name);
    }

}
