using System.Buffers.Binary;
using System.Diagnostics;
using SystemExplorer.Core;
using SystemExplorer.Windows;

internal static class ThreadActivitySmoke
{
    private static readonly ProcessIdentity Identity = new(123, 456);
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void W32(byte[] b, int o, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), value);
    private static void W64(byte[] b, int o, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), value);
    private static NativeActivityThread Thread(int tid, long creation = 1000, uint switches = 100) => new(tid, creation, switches, 2, 5, 8, 10);
    private static NativeActivitySnapshot Snapshot(params NativeActivityThread[] threads) => new(InspectionStatus.Available, "fixed", threads);
    private static byte[] Buffer(params NativeActivityThread[] threads)
    {
        var b = new byte[256 + threads.Length * 80];
        W32(b, 4, (uint)threads.Length); W64(b, 32, 456); W64(b, 80, 123);
        for (var i = 0; i < threads.Length; i++)
        {
            var t = threads[i]; var o = 256 + 80 * i;
            W64(b, o, 0x1111); W64(b, o + 8, 0x2222); W64(b, o + 16, (ulong)t.CreationTime);
            W32(b, o + 24, 99); W64(b, o + 32, 0xdeadbeef); W64(b, o + 40, 123); W64(b, o + 48, (ulong)t.ThreadId);
            W32(b, o + 56, unchecked((uint)t.Priority)); W32(b, o + 60, unchecked((uint)t.BasePriority));
            W32(b, o + 64, t.ContextSwitches); W32(b, o + 68, (uint)t.State); W32(b, o + 72, (uint)t.WaitReason);
        }
        return b;
    }

    internal static async Task Run(Func<string, Func<Task>, Task> section)
    {
        await section("process/thread x64 layout, chain bounds, identity, thread cap and empty", () =>
        {
            var expected = Thread(789) with { BasePriority = -2, Priority = -1 };
            var bytes = Buffer(expected, Thread(790));
            var parsed = NativeSystemInformation.ParseProcessThreads(bytes, Identity);
            Assert(parsed.Status == InspectionStatus.Available && parsed.Threads.Count == 2 && parsed.Threads[0] == expected, "80-byte thread offsets / process offsets");
            Assert(NativeSystemInformation.ParseProcessThreads(bytes, Identity, 1).Status == InspectionStatus.Truncated, "thread cap");
            Assert(NativeSystemInformation.ParseProcessThreads(bytes.AsSpan(0, bytes.Length - 1), Identity).Status == InspectionStatus.Failed, "returned length");
            Assert(NativeSystemInformation.ParseProcessThreads(bytes, Identity with { CreationTimeFileTime = 457 }).Status == InspectionStatus.IdentityMismatch, "PID match creation mismatch");
            var chain = new byte[256 + bytes.Length];
            W32(chain, 0, 256); W64(chain, 80, 999); bytes.CopyTo(chain, 256);
            Assert(NativeSystemInformation.ParseProcessThreads(chain, Identity).Threads.Count == 2, "NextEntryOffset dispatch");
            foreach (var next in new uint[] { 1, 8, 255, 257, uint.MaxValue, (uint)chain.Length, 256 + 80 })
            {
                W32(chain, 0, next);
                Assert(NativeSystemInformation.ParseProcessThreads(chain, Identity).Status == InspectionStatus.Failed, $"invalid forward/self-loop offset {next}");
            }
            W32(bytes, 4, uint.MaxValue);
            Assert(NativeSystemInformation.ParseProcessThreads(bytes, Identity).Status == InspectionStatus.Failed, "NumberOfThreads overflow");
            Assert(NativeSystemInformation.ParseProcessThreads([], Identity).Status == InspectionStatus.Failed, "empty malformed buffer");
            Assert(NativeSystemInformation.ParseProcessThreads(Buffer(), Identity).Status == InspectionStatus.Available, "zero thread entry");
            Assert(NativeSystemInformation.ParseProcessThreads(Buffer(), Identity, expired: () => true).Status == InspectionStatus.TimedOut, "parsing budget");
            var zeroCreation = NativeSystemInformation.ParseProcessThreads(Buffer(Thread(1, 0)), Identity);
            Assert(zeroCreation.Threads[0].CreationTime == 0, "uncertain creation retained honestly");
            return Task.CompletedTask;
        });
        await section("thread classifications, fixed interval math, raw cycles, wrap and truncation", () =>
        {
            var first = Snapshot(Thread(1), Thread(2), Thread(3), Thread(5, 0), Thread(6, switches: uint.MaxValue));
            var second = Snapshot(Thread(1, switches: 125), Thread(2, 2000), Thread(4), Thread(5, 0), Thread(6, switches: 2));
            var cycles = new Dictionary<ThreadIdentity, ThreadCycleSamples>
            {
                [new(Identity, 1, 1000)] = new(new(1000, "first"), new(1600, "second")),
                [new(Identity, 6, 1000)] = new(new(100, "first"), new(200, "second"))
            };
            var r = WindowsThreadActivityProvider.Combine(Identity, first, second, TimeSpan.FromMilliseconds(250), new(10000, "first"), new(13000, "second"), cycles);
            var byId = r.Threads.ToDictionary(t => t.Identity.ThreadId);
            Assert(byId[1].Status == InspectionStatus.Available && byId[1].ContextSwitches == 125 && byId[1].ContextSwitchDelta == 25 && byId[1].ContextSwitchesPerSecond == 100, "fixed counter math");
            Assert(byId[1].CycleTime == 1600 && byId[1].CycleDelta == 600 && r.ProcessCycleTime == 13000 && r.ProcessCycleDelta == 3000, "raw cycle math");
            Assert(byId[1].ThreadState == 2 && byId[1].WaitReason == 5 && byId[1].BasePriority == 8 && byId[1].CurrentPriority == 10, "NT state counters");
            Assert(byId[2].Status == InspectionStatus.IdentityMismatch && byId[2].ContextSwitchDelta == null && byId[2].CycleTime == null, "TID reused");
            Assert(byId[3].Status == InspectionStatus.Exited && byId[3].ContextSwitches == null, "exited");
            Assert(byId[4].Status == InspectionStatus.Partial && byId[4].Detail.Contains("started between samples") && byId[4].ContextSwitchDelta == null, "new thread");
            Assert(byId[5].Status == InspectionStatus.Partial && byId[5].ContextSwitchDelta == null && byId[5].CycleDelta == null, "uncertain");
            Assert(byId[6].Status == InspectionStatus.Partial && byId[6].ContextSwitchDelta == null && byId[6].ContextSwitchesPerSecond == null && byId[6].Detail.Contains("wrap/reset"), "ULONG wrap not guessed");
            Assert(r.SampleInterval == TimeSpan.FromMilliseconds(250) && r.Status == InspectionStatus.Partial, "interval / overall");
            var capped = WindowsThreadActivityProvider.Combine(Identity, first, second, TimeSpan.FromSeconds(1), new(1, ""), new(2, ""), cycles, 2);
            Assert(capped.Status == InspectionStatus.Truncated && capped.Threads.Count == 2, "union cap");
            var refused = WindowsThreadActivityProvider.Combine(Identity, Snapshot(Thread(1)), Snapshot(Thread(1)), TimeSpan.FromSeconds(1), new(null, "access denied"), new(null, "access denied"), new Dictionary<ThreadIdentity, ThreadCycleSamples>
            { [new(Identity, 1, 1000)] = new(new(null, "AccessDenied OpenThread"), new(null, "unavailable")) });
            Assert(refused.Status == InspectionStatus.Partial && refused.Threads[0].ContextSwitchDelta == 0 && refused.Threads[0].Detail.Contains("AccessDenied") && refused.ProcessCycleTime == null, "refused cycles retain NT counters");
            var decreasing = WindowsThreadActivityProvider.Combine(Identity, Snapshot(Thread(1)), Snapshot(Thread(1)), TimeSpan.Zero, new(2, ""), new(1, ""), new Dictionary<ThreadIdentity, ThreadCycleSamples>
            { [new(Identity, 1, 1000)] = new(new(200, ""), new(100, "")) });
            Assert(decreasing.ProcessCycleDelta == null && decreasing.Threads[0].CycleDelta == null && decreasing.Threads[0].CycleTime == 100 && decreasing.Threads[0].ContextSwitchDelta == null, "decreasing cycles and invalid interval");
            var empty = WindowsThreadActivityProvider.Combine(Identity, Snapshot(), Snapshot(), TimeSpan.FromSeconds(1), new(1, ""), new(1, ""), cycles);
            Assert(empty.Status == InspectionStatus.Available && empty.Threads.Count == 0, "empty classification");
            var bounded = WindowsThreadActivityProvider.Combine(Identity, Snapshot(Thread(1)) with { Status = InspectionStatus.Truncated }, Snapshot(Thread(2)) with { Status = InspectionStatus.Truncated }, TimeSpan.FromSeconds(1), new(1, ""), new(2, ""), cycles);
            Assert(bounded.Threads.All(t => t.Status == InspectionStatus.Partial) && bounded.Threads.All(t => !t.Detail.Contains("exited between") && !t.Detail.Contains("started between")), "bounded absence not proof of exit/start");
            return Task.CompletedTask;
        });
        await section("activity fake lifecycle, cycle refusal, identity discard and cancellation cleanup", async () =>
        {
            var fake = new ActivityNative(Buffer(Thread(1)));
            var r = await new WindowsThreadActivityProvider(fake).CaptureAsync(Identity, default);
            Assert(r.Status == InspectionStatus.Available && r.Threads[0].CycleDelta == 100 && fake.ThreadDisposed && fake.ProcessDisposed && fake.Validations == 2, "retained leases disposed");
            fake = new ActivityNative(Buffer(Thread(1))) { RefuseThread = true };
            r = await new WindowsThreadActivityProvider(fake).CaptureAsync(Identity, default);
            Assert(r.Status == InspectionStatus.Partial && r.Threads[0].ContextSwitches == 100 && r.Threads[0].CycleTime == null && r.Threads[0].Detail.Contains("AccessDenied"), "OpenThread denial");
            foreach (var status in new[] { InspectionStatus.IdentityMismatch, InspectionStatus.Exited })
            {
                fake = new ActivityNative(Buffer(Thread(1))) { AfterStatus = status };
                r = await new WindowsThreadActivityProvider(fake).CaptureAsync(Identity, default);
                Assert(r.Status == status && r.Threads.Count == 0 && r.ProcessCycleTime == null && fake.ThreadDisposed, "post-sample identity discard");
                fake = new ActivityNative(Buffer(Thread(1))) { BeforeStatus = status };
                r = await new WindowsThreadActivityProvider(fake).CaptureAsync(Identity, default);
                Assert(r.Status == status && fake.Queries == 0, "pre-sample validation");
            }
            fake = new ActivityNative(Buffer(Thread(1)));
            Assert((await new WindowsThreadActivityProvider(fake).CaptureAsync(new(123, 0), default)).Status == InspectionStatus.IdentityMismatch && fake.Queries == 0, "unstable identity");
            using var cts = new CancellationTokenSource();
            fake = new ActivityNative(Buffer(Thread(1))) { AfterThreadRead = cts.Cancel };
            var canceled = false;
            try { await new WindowsThreadActivityProvider(fake).CaptureAsync(Identity, cts.Token); } catch (OperationCanceledException) { canceled = true; }
            Assert(canceled && fake.ThreadDisposed && fake.ProcessDisposed, "cancellation closes leases");
        });
        await section("live thread activity with busy worker and wrong identity", async () =>
        {
            using var stop = new CancellationTokenSource();
            using var ready = new ManualResetEventSlim();
            var worker = new System.Threading.Thread(() =>
            {
                ready.Set();
                while (!stop.IsCancellationRequested) System.Threading.Thread.SpinWait(10000);
            });
            worker.Start(); ready.Wait();
            try
            {
                using var own = Process.GetCurrentProcess();
                var identity = new ProcessIdentity(own.Id, own.StartTime.ToFileTimeUtc());
                var provider = new WindowsThreadActivityProvider();
                var r = await provider.CaptureAsync(identity, default);
                Console.WriteLine($"  activity: {r.Status}; interval={r.SampleInterval.TotalMilliseconds:F1}ms; rows={r.Threads.Count}; {r.Detail}");
                Assert(r.Status is InspectionStatus.Available or InspectionStatus.Partial or InspectionStatus.Truncated or InspectionStatus.AccessDenied or InspectionStatus.Unsupported or InspectionStatus.TimedOut, "honest activity status");
                Assert(r.Threads.Count <= WindowsThreadActivityProvider.MaximumThreads && r.Threads.All(t => t.Identity.IsStable && t.Identity.Process == identity), "stable live row identity / cap");
                Assert(r.Threads.All(t => t.ContextSwitchDelta is null or >= 0 && t.ContextSwitchesPerSecond is null or >= 0), "nonnegative live deltas");
                if (r.Status is InspectionStatus.Available or InspectionStatus.Partial or InspectionStatus.Truncated)
                    Assert(r.Threads.Count > 0 && r.SampleInterval > TimeSpan.Zero, "live activity observed");
                var wrong = await provider.CaptureAsync(identity with { CreationTimeFileTime = identity.CreationTimeFileTime + 1 }, default);
                Assert(wrong.Status == InspectionStatus.IdentityMismatch && wrong.Threads.Count == 0, "wrong live creation time");
            }
            finally { stop.Cancel(); worker.Join(); }
        });
    }

    private sealed class ActivityNative(byte[] bytes) : IThreadActivityNative
    {
        internal int Queries, Validations;
        internal bool ThreadDisposed, ProcessDisposed, RefuseThread;
        internal Action? AfterThreadRead;
        internal InspectionStatus BeforeStatus = InspectionStatus.Available, AfterStatus = InspectionStatus.Available;
        public bool IsSupported => true;
        public NativeInformation Query(int informationClass, bool objectTypes, CancellationToken token, Func<bool> expired)
        { Assert(informationClass == 5 && !objectTypes, "class 5 dispatch"); Queries++; return new(InspectionStatus.Available, "fixed", bytes); }
        public IInventoryProcessLease Open(ProcessIdentity identity) => new ProcessLease(this);
        public IActivityThreadLease OpenThread(int tid)
        {
            if (RefuseThread) throw new InventoryNativeException(InspectionStatus.AccessDenied, "OpenThread access denied");
            return new ThreadLease(this);
        }
        private sealed class ProcessLease(ActivityNative owner) : IInventoryProcessLease
        {
            private ulong cycles = 1000;
            public InspectionStatus Validate(ProcessIdentity identity) => owner.Validations++ == 0 ? owner.BeforeStatus : owner.AfterStatus;
            public CycleReading ReadCycles() => new(cycles += 100, "fixed");
            public void Dispose() => owner.ProcessDisposed = true;
        }
        private sealed class ThreadLease(ActivityNative owner) : IActivityThreadLease
        {
            private ulong cycles = 100;
            public CycleReading Read(ThreadIdentity identity)
            { owner.AfterThreadRead?.Invoke(); return new(cycles += 100, "fixed"); }
            public void Dispose() => owner.ThreadDisposed = true;
        }
    }
}
