using System.Diagnostics;
using SystemExplorer.Core;

namespace SystemExplorer.Windows;

public sealed class WindowsThreadDiagnosticsProvider : IThreadDiagnosticsProvider
{
    public const int MaximumThreads = 256;
    public const int MaximumEnumeratedThreads = 32768;
    private static readonly SemaphoreSlim WaitChainGate = new(1, 1);
    private readonly IThreadDiagnosticsBackend backend;
    private readonly TimeSpan budget;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;
    private static readonly TimeSpan SamplingDelay = TimeSpan.FromMilliseconds(300);

    public WindowsThreadDiagnosticsProvider() : this(new NativeThreadDiagnosticsBackend(), TimeSpan.FromSeconds(3), Task.Delay) { }

    internal WindowsThreadDiagnosticsProvider(IThreadDiagnosticsBackend backend, TimeSpan budget,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (budget < TimeSpan.Zero || budget > TimeSpan.FromSeconds(3)) throw new ArgumentOutOfRangeException(nameof(budget));
        this.backend = backend;
        this.budget = budget;
        this.delay = delay ?? Task.Delay;
    }

    public Task<ThreadInspection> CaptureAsync(ProcessIdentity identity, CancellationToken cancellationToken) =>
        Task.Run(() => Capture(identity, cancellationToken), cancellationToken);

    private async Task<ThreadInspection> Capture(ProcessIdentity identity, CancellationToken token)
    {
        var captured = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var rows = new List<ThreadObservation>();
        var samples = new List<PendingSample>();
        ThreadInspection Result(InspectionStatus status, string detail, bool discard = false) =>
            new(identity, captured, clock.Elapsed, status, detail, discard ? [] : rows.ToArray());
        token.ThrowIfCancellationRequested();
        if (!backend.IsSupported) return Result(InspectionStatus.Unsupported, "Thread diagnostics require an x64 Windows host.");
        if (identity.Pid <= 0 || !identity.IsStable)
            return Result(InspectionStatus.Unsupported, "A positive PID and creation FILETIME are required; PID alone is never inspected.");
        try
        {
            using var process = backend.OpenProcess(identity.Pid);
            ValidateProcess(process, identity);
            if (clock.Elapsed >= budget) return Result(InspectionStatus.TimedOut, "Cooperative budget expired before enumeration.");
            var inventory = backend.EnumerateThreads(identity.Pid, MaximumThreads, MaximumEnumeratedThreads, () =>
            {
                token.ThrowIfCancellationRequested();
                return clock.Elapsed >= budget;
            });
            var status = inventory.Status;
            var detail = inventory.Detail;
            foreach (var tid in inventory.ThreadIds)
            {
                token.ThrowIfCancellationRequested();
                if (clock.Elapsed >= budget) { status = InspectionStatus.TimedOut; break; }
                IThreadDiagnosticLease? thread = null;
                try
                {
                    thread = backend.OpenThread(tid);
                    var state = thread.Read();
                    ValidateThread(state, identity, null);
                    var threadIdentity = new ThreadIdentity(identity, tid, state.CreationTimeFileTime);
                    var rowIndex = rows.Count;
                    rows.Add(new(threadIdentity, state.KernelTimeTicks, state.UserTimeTicks, null, InspectionStatus.Partial, "Second counter sample not yet available."));
                    samples.Add(new(thread, threadIdentity, state, Stopwatch.GetTimestamp(), rowIndex));
                    thread = null;
                }
                catch (ThreadDiagnosticException ex)
                {
                    rows.Add(new(new(identity, tid, 0), null, null, null, ex.Status, ex.Message));
                }
                finally { thread?.Dispose(); }
            }
            if (samples.Count > 0 && clock.Elapsed + SamplingDelay < budget)
            {
                await delay(SamplingDelay, token).ConfigureAwait(false);
                foreach (var sample in samples)
                {
                    token.ThrowIfCancellationRequested();
                    if (clock.Elapsed >= budget) { status = InspectionStatus.TimedOut; break; }
                    try
                    {
                        var state = sample.Lease.Read();
                        var elapsed = Stopwatch.GetElapsedTime(sample.Timestamp);
                        ValidateThread(state, identity, sample.Identity);
                        var (cpu, reason) = CalculateCpu(sample.State, state, elapsed);
                        rows[sample.RowIndex] = new(sample.Identity, state.KernelTimeTicks, state.UserTimeTicks, cpu,
                            cpu.HasValue ? InspectionStatus.Available : InspectionStatus.Partial, reason);
                    }
                    catch (ThreadDiagnosticException ex)
                    {
                        rows[sample.RowIndex] = new(sample.Identity, null, null, null, ex.Status, ex.Message);
                    }
                }
            }
            else if (samples.Count > 0) status = InspectionStatus.TimedOut;
            token.ThrowIfCancellationRequested();
            ValidateProcess(process, identity);
            if (clock.Elapsed >= budget) status = InspectionStatus.TimedOut;
            if (status == InspectionStatus.TimedOut)
            {
                for (var i = 0; i < rows.Count; i++)
                    if (rows[i].Status == InspectionStatus.Partial && rows[i].CpuPercent is null)
                        rows[i] = rows[i] with { Status = InspectionStatus.TimedOut, Detail = "No complete counter pair before the cooperative budget expired." };
                detail = "Cooperative three-second budget reached; synchronous native calls are not hard-deadline bounded. Inventory or counter pairs may be incomplete.";
            }
            else if (status == InspectionStatus.Available && rows.Any(row => row.Status != InspectionStatus.Available))
                status = InspectionStatus.Partial;
            return Result(status, detail + " CPU uses per-thread monotonic elapsed time and one logical CPU as 100%; SampleInterval is the total capture span, not a shared per-thread denominator.");
        }
        catch (ThreadDiagnosticException ex) { return Result(ex.Status, ex.Message, true); }
        finally { foreach (var sample in samples) sample.Lease.Dispose(); }
    }

    internal static (double? Cpu, string Detail) CalculateCpu(ThreadDiagnosticState first, ThreadDiagnosticState second, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero || first.KernelTimeTicks < 0 || first.UserTimeTicks < 0 ||
            second.KernelTimeTicks < first.KernelTimeTicks || second.UserTimeTicks < first.UserTimeTicks)
            return (null, "Counter delta or monotonic sample interval is invalid; CPU is unavailable.");
        var cpu = 100d * ((double)(second.KernelTimeTicks - first.KernelTimeTicks) +
            (second.UserTimeTicks - first.UserTimeTicks)) / (elapsed.TotalSeconds * TimeSpan.TicksPerSecond);
        return !double.IsFinite(cpu) || cpu > 100
            ? (null, "Counter granularity or sample timing exceeds one logical CPU; CPU is unavailable, not clamped.")
            : (cpu, "Two lifetime-validated counter samples; 100% is one logical CPU, not the whole machine.");
    }

    public Task<WaitChainInspection> CaptureWaitChainAsync(ThreadIdentity identity, CancellationToken cancellationToken) =>
        Task.Run(() => CaptureWaitChain(identity, cancellationToken), cancellationToken);

    private async Task<WaitChainInspection> CaptureWaitChain(ThreadIdentity identity, CancellationToken token)
    {
        var captured = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        WaitChainInspection Result(InspectionStatus status, string detail) => new(identity, captured, status, detail, null, []);
        token.ThrowIfCancellationRequested();
        if (!backend.IsSupported) return Result(InspectionStatus.Unsupported, "Wait chains require an x64 Windows host.");
        if (!identity.IsStable) return Result(InspectionStatus.Unsupported, "Stable positive process and thread creation-time identities are required.");
        var entered = false;
        try
        {
            entered = await WaitChainGate.WaitAsync(Remaining(clock), token).ConfigureAwait(false);
            if (!entered || clock.Elapsed >= budget) return Result(InspectionStatus.TimedOut, "Cooperative budget expired waiting for the single WCT request slot.");
            using var process = backend.OpenProcess(identity.Process.Pid);
            using var thread = backend.OpenThread(identity.ThreadId);
            ValidateProcess(process, identity.Process);
            ValidateThread(thread.Read(), identity.Process, identity);
            if (clock.Elapsed >= budget) return Result(InspectionStatus.TimedOut, "Cooperative budget expired before WCT start.");
            WaitChainResult result;
            // Dispose closes on this worker, never from the callback. Close can wait for callbacks to return.
            using (var operation = backend.StartWaitChain(identity.ThreadId))
                result = await operation.Completion.WaitAsync(Remaining(clock), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            ValidateProcess(process, identity.Process);
            ValidateThread(thread.Read(), identity.Process, identity);
            if (clock.Elapsed >= budget) return Result(InspectionStatus.TimedOut, "Cooperative budget expired; WCT results discarded. Native close may wait for callbacks.");
            return new(identity, captured, result.Status, result.Detail +
                " A single node means unblocked OR an unsupported wait. Cycles are API observations, not proven root causes. Foreign-node PID/TID values are unverified and are not attributed to process snapshots.",
                result.CycleDetected, result.Nodes);
        }
        catch (TimeoutException) { return Result(InspectionStatus.TimedOut, "WCT cooperative budget expired; results discarded. Native close may wait for callbacks and is not a hard deadline."); }
        catch (ThreadDiagnosticException ex) { return Result(ex.Status, ex.Message); }
        finally { if (entered) WaitChainGate.Release(); }
    }

    private TimeSpan Remaining(Stopwatch clock)
    {
        var remaining = budget - clock.Elapsed;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    private static void ValidateProcess(IProcessDiagnosticLease process, ProcessIdentity identity)
    {
        if (process.ReadCreationTime() != identity.CreationTimeFileTime)
            throw new ThreadDiagnosticException(InspectionStatus.IdentityMismatch, "Process creation identity changed; all results discarded.");
    }

    private static void ValidateThread(ThreadDiagnosticState state, ProcessIdentity process, ThreadIdentity? identity)
    {
        if (state.OwnerPid != process.Pid || state.CreationTimeFileTime <= 0 ||
            (identity is { } expected && state.CreationTimeFileTime != expected.CreationTimeFileTime))
            throw new ThreadDiagnosticException(InspectionStatus.IdentityMismatch, "Thread owner or creation identity changed; counters and wait chain discarded.");
    }

    private sealed record PendingSample(IThreadDiagnosticLease Lease, ThreadIdentity Identity, ThreadDiagnosticState State, long Timestamp, int RowIndex);
}

internal sealed class ThreadDiagnosticException(InspectionStatus status, string detail) : Exception(detail)
{
    internal InspectionStatus Status { get; } = status;
}

internal readonly record struct ThreadDiagnosticState(int OwnerPid, long CreationTimeFileTime, long KernelTimeTicks, long UserTimeTicks);
internal sealed record ThreadInventory(IReadOnlyList<int> ThreadIds, InspectionStatus Status, string Detail);
internal sealed record WaitChainResult(InspectionStatus Status, string Detail, bool? CycleDetected, IReadOnlyList<WaitChainNode> Nodes);
internal interface IProcessDiagnosticLease : IDisposable { long ReadCreationTime(); }
internal interface IThreadDiagnosticLease : IDisposable { ThreadDiagnosticState Read(); }
internal interface IWaitChainOperation : IDisposable { Task<WaitChainResult> Completion { get; } }
internal interface IThreadDiagnosticsBackend
{
    bool IsSupported { get; }
    IProcessDiagnosticLease OpenProcess(int pid);
    IThreadDiagnosticLease OpenThread(int tid);
    ThreadInventory EnumerateThreads(int pid, int selectedLimit, int enumerationLimit, Func<bool> expired);
    IWaitChainOperation StartWaitChain(int tid);
}
