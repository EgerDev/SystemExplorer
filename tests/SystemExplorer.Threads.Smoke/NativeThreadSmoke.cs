using System.Diagnostics;
using System.Runtime.InteropServices;
using SystemExplorer.Core;
using SystemExplorer.Windows;

internal static class NativeThreadSmoke
{
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    internal static async Task RunAsync()
    {
        using var process = Process.GetCurrentProcess();
        var identity = new ProcessIdentity(process.Id, process.StartTime.ToFileTimeUtc());
        using var ready = new CountdownEvent(2);
        using var stop = new ManualResetEventSlim();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var errors = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        int busyTid = 0, waitingTid = 0;
        var busy = new Thread(() =>
        {
            try
            {
                busyTid = checked((int)GetCurrentThreadId());
                ready.Signal();
                while (!stop.Wait(0)) Thread.SpinWait(10_000);
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        }) { IsBackground = true, Name = "Thread diagnostics busy fixture" };
        var waiting = new Thread(() =>
        {
            try
            {
                waitingTid = checked((int)GetCurrentThreadId());
                ready.Signal();
                stop.Wait();
            }
            catch (Exception ex) { errors.Enqueue(ex); }
        }) { IsBackground = true, Name = "Thread diagnostics waiting fixture" };

        busy.Start();
        waiting.Start();
        try
        {
            Check(ready.Wait(TimeSpan.FromSeconds(5)), "Owned busy/waiting threads did not become ready.");
            Check(identity.IsStable && busyTid > 0 && waitingTid > 0 && busyTid != waitingTid,
                "Self process and native thread identities must be stable and distinct.");

            var provider = new WindowsThreadDiagnosticsProvider();
            var wrongLifetime = await provider.CaptureAsync(
                new(identity.Pid, checked(identity.CreationTimeFileTime!.Value + 1)), timeout.Token);
            Check(wrongLifetime.Status == InspectionStatus.IdentityMismatch && wrongLifetime.Threads.Count == 0,
                "Native self-process creation mismatch must return IdentityMismatch with no rows.");
            Console.WriteLine($"PASS: native self-process lifetime mismatch; status={wrongLifetime.Status}; rows={wrongLifetime.Threads.Count}.");

            var first = await provider.CaptureAsync(identity, timeout.Token);
            var busyBefore = RequireThread(first, identity, busyTid);
            var waitingBefore = RequireThread(first, identity, waitingTid);
            await Task.Delay(TimeSpan.FromMilliseconds(600), timeout.Token);
            var second = await provider.CaptureAsync(identity, timeout.Token);
            var busyAfter = RequireThread(second, identity, busyTid);
            var waitingAfter = RequireThread(second, identity, waitingTid);
            long busyDelta = CounterDelta(busyBefore, busyAfter);
            long waitingDelta = CounterDelta(waitingBefore, waitingAfter);
            Check(busyDelta > waitingDelta, "Owned busy-thread counter growth must exceed waiting-thread growth.");

            var chain = await provider.CaptureWaitChainAsync(waitingAfter.Identity, timeout.Token);
            Check(chain.Identity == waitingAfter.Identity, "Selected waiting-thread WCT must retain its full identity.");
            if (chain.Status == InspectionStatus.AccessDenied)
            {
                Check(chain.Nodes.Count == 0 && chain.CycleDetected is null,
                    "Explicit WCT access denial must not expose nodes or a cycle value.");
            }
            else
            {
                Check(chain.Status is InspectionStatus.Available or InspectionStatus.Partial or InspectionStatus.Truncated,
                    $"Selected waiting-thread WCT returned unexpected status {chain.Status}: {chain.Detail}");
                Check(chain.Nodes.Count is > 0 and <= NativeWaitChainOperation.MaximumNodes &&
                    chain.Nodes.Select((node, index) => node.Ordinal == index).All(valid => valid),
                    "Successful WCT must return a non-empty, ordered, bounded managed node list.");
            }
            Console.WriteLine($"PASS: native self busy/waiting threads; pid={identity.Pid}; busy_tid={busyTid}; waiting_tid={waitingTid}; busy_delta={busyDelta}; waiting_delta={waitingDelta}; busy_cpu={busyAfter.CpuPercent?.ToString("F2") ?? "null"}; waiting_cpu={waitingAfter.CpuPercent?.ToString("F2") ?? "null"}; WCT={chain.Status}; nodes={chain.Nodes.Count}; cycle={chain.CycleDetected?.ToString() ?? "null"}; detail={chain.Detail}");
        }
        finally
        {
            stop.Set();
            bool busyJoined = busy.Join(TimeSpan.FromSeconds(5));
            bool waitingJoined = waiting.Join(TimeSpan.FromSeconds(5));
            Check(busyJoined && waitingJoined, "Owned busy/waiting threads must be joined during cleanup.");
            if (errors.TryDequeue(out var error)) throw new InvalidOperationException("Owned native thread fixture failed.", error);
        }
    }

    internal static async Task CheckExit259Async()
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing smoke executable path.");
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(NativeThreadSmoke).Assembly.Location);
        start.ArgumentList.Add("--exit259");

        using var child = new Process { StartInfo = start };
        Check(child.Start(), "Could not start the owned exit-259 child fixture.");
        IProcessDiagnosticLease? lease = null;
        try
        {
            string? ready = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(ready == "READY", $"Owned child did not become ready: {ready ?? "no output"}.");
            Check(!child.HasExited, "Owned child must be running before opening its retained process lease.");
            var openedLease = new NativeThreadDiagnosticsBackend().OpenProcess(child.Id);
            lease = openedLease;
            Check(openedLease.ReadCreationTime() > 0, "The running child must have a readable retained creation time.");

            child.StandardInput.WriteLine("exit");
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Check(child.ExitCode == 259, $"Owned child must naturally exit with 259, got {child.ExitCode}.");
            ThreadDiagnosticException? result = null;
            try { _ = lease.ReadCreationTime(); }
            catch (ThreadDiagnosticException ex) { result = ex; }
            var exitStatus = result?.Status;
            Check(exitStatus == InspectionStatus.Exited,
                "A retained lease must classify the naturally exited code-259 process as Exited, not still active.");
            Console.WriteLine($"PASS: owned child exit-259 lifetime regression; pid={child.Id}; exit_code={child.ExitCode}; retained_lease={exitStatus}.");
        }
        finally
        {
            lease?.Dispose();
            if (!child.HasExited)
            {
                try { child.StandardInput.WriteLine("release"); }
                catch (InvalidOperationException) { }
                await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            }
        }
    }

    private static ThreadObservation RequireThread(ThreadInspection capture, ProcessIdentity process, int tid)
    {
        var row = capture.Threads.SingleOrDefault(thread => thread.Identity.ThreadId == tid)
            ?? throw new InvalidOperationException($"Native provider did not return owned TID {tid} ({capture.Status}: {capture.Detail}).");
        Check(row.Identity.IsStable && row.Identity.Process == process && row.Identity.ThreadId == tid,
            $"Native provider returned a mismatched identity for TID {tid}.");
        Check(row.KernelTimeTicks.HasValue && row.UserTimeTicks.HasValue,
            $"Native provider did not map kernel/user counters for owned TID {tid}: {row.Detail}.");
        return row;
    }

    private static long CounterDelta(ThreadObservation first, ThreadObservation second)
    {
        long firstKernel = first.KernelTimeTicks ?? throw new InvalidOperationException("Missing first kernel counter.");
        long firstUser = first.UserTimeTicks ?? throw new InvalidOperationException("Missing first user counter.");
        long secondKernel = second.KernelTimeTicks ?? throw new InvalidOperationException("Missing second kernel counter.");
        long secondUser = second.UserTimeTicks ?? throw new InvalidOperationException("Missing second user counter.");
        Check(secondKernel >= firstKernel && secondUser >= firstUser,
            "Owned native thread counters must be monotonic.");
        return checked(secondKernel - firstKernel + secondUser - firstUser);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
