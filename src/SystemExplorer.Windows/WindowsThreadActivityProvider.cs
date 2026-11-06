using System.Diagnostics;
using SystemExplorer.Core;

namespace SystemExplorer.Windows;

internal readonly record struct ThreadCycleSamples(CycleReading First, CycleReading Second);

public sealed class WindowsThreadActivityProvider : IThreadActivityProvider
{
    internal const int MaximumThreads = 1024;
    private const string Scope = " Context switches are from SystemProcessInformation. Cycles are raw CPU cycle counts, not time; no frequency conversion is performed.";
    private readonly IThreadActivityNative native;
    public WindowsThreadActivityProvider() : this(new NativeSystemInformation()) { }
    internal WindowsThreadActivityProvider(IThreadActivityNative native) => this.native = native;

    public Task<ThreadActivityInspection> CaptureAsync(ProcessIdentity identity, CancellationToken cancellationToken) =>
        Task.Run(() => Capture(identity, cancellationToken), cancellationToken);

    private async Task<ThreadActivityInspection> Capture(ProcessIdentity identity, CancellationToken token)
    {
        var captured = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var interval = TimeSpan.Zero;
        bool Expired() => clock.Elapsed >= TimeSpan.FromSeconds(5);
        void Check()
        {
            token.ThrowIfCancellationRequested();
            if (Expired()) throw new InventoryNativeException(InspectionStatus.TimedOut,
                "Five-second cooperative activity budget exceeded; blocking native calls cannot be interrupted.");
        }
        ThreadActivityInspection Empty(InspectionStatus status, string detail) => new(identity, captured, interval, status, detail + Scope, null, null, []);
        NativeActivitySnapshot Sample()
        {
            Check();
            var result = native.Query(5, false, token, Expired);
            if (result.Status != InspectionStatus.Available) return new(result.Status, result.Detail, []);
            return NativeSystemInformation.ParseProcessThreads(result.Bytes, identity, MaximumThreads, token, Expired);
        }
        static bool Usable(NativeActivitySnapshot sample) => sample.Status is InspectionStatus.Available or InspectionStatus.Truncated;

        var leases = new Dictionary<int, IActivityThreadLease>();
        token.ThrowIfCancellationRequested();
        if (!identity.IsStable) return Empty(InspectionStatus.IdentityMismatch, "Stable process creation time required; no rows attributed.");
        if (!native.IsSupported) return Empty(InspectionStatus.Unsupported, "Only Windows x64 layouts are supported.");
        try
        {
            using var process = native.Open(identity);
            var validation = process.Validate(identity);
            if (validation != InspectionStatus.Available) return Empty(validation, "Process identity/liveness validation failed before sampling.");
            var first = Sample();
            if (!Usable(first)) return Empty(first.Status, first.Detail);
            var firstStamp = Stopwatch.GetTimestamp();
            Check();
            var processFirst = process.ReadCycles();
            var cycles = new Dictionary<ThreadIdentity, ThreadCycleSamples>();
            foreach (var thread in first.Threads)
            {
                Check();
                var key = new ThreadIdentity(identity, thread.ThreadId, thread.CreationTime);
                CycleReading reading;
                if (!key.IsStable) reading = new(null, "Thread creation time is uncertain; cycles not attributed.");
                else
                {
                    try
                    {
                        var lease = native.OpenThread(thread.ThreadId);
                        leases.Add(thread.ThreadId, lease);
                        reading = lease.Read(key);
                    }
                    catch (InventoryNativeException error) { reading = new(null, $"{error.Status}: {error.Message}"); }
                }
                cycles[key] = new(reading, new(null, "No matching second thread sample."));
            }
            var remaining = TimeSpan.FromMilliseconds(300) - Stopwatch.GetElapsedTime(firstStamp);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining, token).ConfigureAwait(false);
            Check();
            var second = Sample();
            interval = Stopwatch.GetElapsedTime(firstStamp);
            var processSecond = process.ReadCycles();
            if (Usable(second))
            {
                foreach (var thread in second.Threads)
                {
                    Check();
                    var key = new ThreadIdentity(identity, thread.ThreadId, thread.CreationTime);
                    if (cycles.TryGetValue(key, out var pair) && leases.TryGetValue(thread.ThreadId, out var lease))
                        cycles[key] = pair with { Second = lease.Read(key) };
                }
            }
            Check();
            validation = process.Validate(identity);
            if (validation != InspectionStatus.Available) return Empty(validation, "Process identity/liveness validation failed after sampling; data discarded.");
            if (!Usable(second)) return Empty(second.Status, second.Detail);
            return Combine(identity, first, second, interval, processFirst, processSecond, cycles) with { CapturedAt = captured };
        }
        catch (InventoryNativeException error) { return Empty(error.Status, error.Message); }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return Empty(InspectionStatus.Unsupported, "Required native API unavailable; no fallback attempted."); }
        finally
        {
            foreach (var lease in leases.Values) lease.Dispose();
        }
    }

    internal static ThreadActivityInspection Combine(ProcessIdentity identity, NativeActivitySnapshot first, NativeActivitySnapshot second,
        TimeSpan interval, CycleReading processFirst, CycleReading processSecond,
        IReadOnlyDictionary<ThreadIdentity, ThreadCycleSamples> cycles, int cap = MaximumThreads)
    {
        var rows = new List<ThreadActivityObservation>();
        var previous = first.Threads.ToDictionary(t => t.ThreadId);
        var current = second.Threads.ToDictionary(t => t.ThreadId);
        var ids = previous.Keys.Union(current.Keys).Order().ToArray();
        foreach (var tid in ids.Take(cap))
        {
            previous.TryGetValue(tid, out var a);
            current.TryGetValue(tid, out var b);
            var t = b ?? a!;
            var key = new ThreadIdentity(identity, tid, t.CreationTime);
            var status = InspectionStatus.Available;
            var detail = "Same thread creation time in both samples.";
            long? delta = null;
            double? rate = null;
            ulong? cycleTime = null;
            ulong? cycleDelta = null;
            if (!key.IsStable || a?.CreationTime == 0)
            { status = InspectionStatus.Partial; detail = "Thread creation time is uncertain; no deltas attributed."; }
            else if (a == null)
            {
                status = InspectionStatus.Partial;
                detail = first.Status == InspectionStatus.Truncated ? "Thread absent from bounded first sample; start time not established." : "Thread started between samples; no deltas.";
            }
            else if (b == null)
            {
                status = second.Status == InspectionStatus.Truncated ? InspectionStatus.Partial : InspectionStatus.Exited;
                detail = second.Status == InspectionStatus.Truncated ? "Thread absent from bounded second sample; exit not established." : "Thread exited between samples; second counters unavailable.";
            }
            else if (a.CreationTime != b.CreationTime)
            { status = InspectionStatus.IdentityMismatch; detail = "Thread ID reused with a different creation time; no deltas or cycles attributed."; }
            else
            {
                if (b.ContextSwitches < a.ContextSwitches)
                { status = InspectionStatus.Partial; detail += " Context switch counter decreased (wrap/reset); delta unavailable."; }
                else if (interval <= TimeSpan.Zero)
                { status = InspectionStatus.Partial; detail += " Non-positive sample interval; delta/rate unavailable."; }
                else
                {
                    delta = (long)b.ContextSwitches - a.ContextSwitches;
                    rate = delta / interval.TotalSeconds;
                }
                if (cycles.TryGetValue(key, out var pair))
                {
                    cycleTime = pair.Second.Value;
                    if (pair.First.Value.HasValue && pair.Second.Value.HasValue && pair.Second.Value >= pair.First.Value)
                        cycleDelta = pair.Second.Value - pair.First.Value;
                    else
                    {
                        status = InspectionStatus.Partial;
                        detail += " Cycle delta unavailable (refused, missing or decreasing counter). " + pair.First.Detail + " " + pair.Second.Detail;
                    }
                }
                else { status = InspectionStatus.Partial; detail += " Thread cycle samples unavailable."; }
            }
            rows.Add(new(key, status, detail, b?.ContextSwitches, delta, rate, b?.State, b?.WaitReason, b?.BasePriority, b?.Priority, cycleTime, cycleDelta));
        }
        ulong? processDelta = processFirst.Value.HasValue && processSecond.Value.HasValue && processSecond.Value >= processFirst.Value
            ? processSecond.Value - processFirst.Value : null;
        var truncated = ids.Length > cap || first.Status == InspectionStatus.Truncated || second.Status == InspectionStatus.Truncated;
        var overall = truncated ? InspectionStatus.Truncated : rows.Any(r => r.Status != InspectionStatus.Available) || !processDelta.HasValue
            ? InspectionStatus.Partial : InspectionStatus.Available;
        var detailOverall = $"Two on-demand snapshots, thread union capped at {cap}." + Scope;
        if (!processDelta.HasValue) detailOverall += " Process cycle delta unavailable (missing or decreasing counter). " + processFirst.Detail + " " + processSecond.Detail;
        return new(identity, DateTimeOffset.UtcNow, interval, overall, detailOverall, processSecond.Value, processDelta, rows.ToArray());
    }
}
