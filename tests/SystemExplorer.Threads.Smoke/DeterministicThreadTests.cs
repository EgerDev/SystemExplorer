using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SystemExplorer.Core;
using SystemExplorer.Windows;

internal static class DeterministicThreadTests
{
    private static readonly ProcessIdentity Identity = new(4210, 123456789);

    internal static async Task RunAsync()
    {
        CheckStableIdentity();
        CheckCpuOracle();
        await CheckProviderLifetimeAndRowsAsync();
        await CheckProviderBudgetAndCancellationAsync();
        CheckThreadLifetimeReader();
        await CheckNativeWaitChainParsingAndLifetimeAsync();
        await CheckProviderWaitChainAsync();
        Console.WriteLine("PASS: deterministic identity, CPU, provider lifetime/budget/cancellation/inventory, live-wait, and WCT parsing/cleanup groups.");
    }

    private static void CheckStableIdentity()
    {
        Check(new ThreadIdentity(new(1, 10), 2, 20).IsStable, "Positive process/thread identities with creation times must be stable.");
        Check(!new ThreadIdentity(new(0, 10), 2, 20).IsStable, "A zero process PID must be unstable.");
        Check(!new ThreadIdentity(new(1, null), 2, 20).IsStable, "A missing process creation time must be unstable.");
        Check(!new ThreadIdentity(new(1, 10), 0, 20).IsStable, "A zero thread ID must be unstable.");
        Check(!new ThreadIdentity(new(1, 10), 2, 0).IsStable, "A missing thread creation time must be unstable.");
    }

    private static void CheckCpuOracle()
    {
        var first = State(Identity.Pid, 9876, 0, 0);
        var second = State(Identity.Pid, 9876, 1_000_000, 2_000_000);
        var (cpu, _) = WindowsThreadDiagnosticsProvider.CalculateCpu(first, second, TimeSpan.FromSeconds(1));
        Check(cpu == 30, "One million kernel plus two million user ticks per second must equal 30% of one CPU.");
        Check(WindowsThreadDiagnosticsProvider.CalculateCpu(second, first, TimeSpan.FromSeconds(1)).Cpu is null,
            "Negative counter deltas must make CPU unavailable.");
        Check(WindowsThreadDiagnosticsProvider.CalculateCpu(first, State(Identity.Pid, 9876, 20_000_000, 0),
            TimeSpan.FromSeconds(1)).Cpu is null, "CPU above one logical CPU must be null rather than clamped.");
    }

    private static async Task CheckProviderLifetimeAndRowsAsync()
    {
        var initialMismatch = new FakeBackend(Identity)
        {
            ProcessCreationForRead = read => read == 0 ? Identity.CreationTimeFileTime!.Value + 1 : Identity.CreationTimeFileTime!.Value,
            ThreadIds = [71]
        };
        var before = await CaptureAsync(initialMismatch, TimeSpan.FromSeconds(1));
        Check(before.Status == InspectionStatus.IdentityMismatch && before.Threads.Count == 0 && initialMismatch.EnumerationCalls == 0,
            "A process lifetime mismatch before enumeration must discard all data.");

        var finalMismatch = new FakeBackend(Identity)
        {
            ProcessCreationForRead = read => read == 0 ? Identity.CreationTimeFileTime!.Value : Identity.CreationTimeFileTime!.Value + 1,
            ThreadIds = [71]
        };
        var after = await CaptureAsync(finalMismatch, TimeSpan.FromSeconds(1));
        Check(after.Status == InspectionStatus.IdentityMismatch && after.Threads.Count == 0,
            "A process lifetime mismatch after sampling must discard all rows.");
        Check(finalMismatch.ThreadLeases.Single().DisposeCount == 1, "Thread leases must be disposed when final process validation fails.");

        foreach (var (label, stateFactory, expected) in new (string, Func<int, int, ThreadDiagnosticState>, InspectionStatus)[]
        {
            ("owner mismatch", (_, read) => read == 0 ? State(Identity.Pid, 9876, 0, 0) : State(Identity.Pid + 1, 9876, 1, 1), InspectionStatus.IdentityMismatch),
            ("creation mismatch", (_, read) => read == 0 ? State(Identity.Pid, 9876, 0, 0) : State(Identity.Pid, 9877, 1, 1), InspectionStatus.IdentityMismatch),
            ("thread exit", (_, read) => read == 0 ? State(Identity.Pid, 9876, 0, 0) : throw new ThreadDiagnosticException(InspectionStatus.Exited, "synthetic exit"), InspectionStatus.Exited)
        })
        {
            var backend = new FakeBackend(Identity) { ThreadIds = [72], ThreadStateForRead = stateFactory };
            var capture = await CaptureAsync(backend, TimeSpan.FromSeconds(1));
            var row = capture.Threads.Single();
            Check(row.Status == expected && row.KernelTimeTicks is null && row.UserTimeTicks is null && row.CpuPercent is null,
                $"A second-sample {label} must clear the row counters and CPU.");
        }

        var denied = new FakeBackend(Identity)
        {
            ThreadIds = [73],
            OpenThreadFailure = _ => new ThreadDiagnosticException(InspectionStatus.AccessDenied, "synthetic access denied")
        };
        var deniedCapture = await CaptureAsync(denied, TimeSpan.FromSeconds(1));
        var deniedRow = deniedCapture.Threads.Single();
        Check(deniedRow.Status == InspectionStatus.AccessDenied && deniedRow.KernelTimeTicks is null &&
            deniedRow.UserTimeTicks is null && deniedRow.CpuPercent is null, "Access-denied thread rows must carry no counters or CPU.");

        var bounded = new FakeBackend(Identity) { ThreadIds = Enumerable.Range(1, 300).ToArray() };
        var bySelectedLimit = bounded.EnumerateThreads(Identity.Pid, 40, 50, () => false);
        var byEnumerationLimit = bounded.EnumerateThreads(Identity.Pid, 300, 50, () => false);
        Check(bySelectedLimit.ThreadIds.Count == 40 && bySelectedLimit.Status == InspectionStatus.Truncated,
            "Fake inventory must honor the selected-thread bound.");
        Check(byEnumerationLimit.ThreadIds.Count == 50 && byEnumerationLimit.Status == InspectionStatus.Truncated,
            "Fake inventory must honor the system-enumeration bound.");
        var boundedCapture = await CaptureAsync(bounded, TimeSpan.FromSeconds(1));
        Check(bounded.SelectedLimit == WindowsThreadDiagnosticsProvider.MaximumThreads &&
            bounded.EnumerationLimit == WindowsThreadDiagnosticsProvider.MaximumEnumeratedThreads,
            "The provider must pass bounds of 256 selected threads and 32768 enumerated threads.");
        Check(boundedCapture.Threads.Count == 256 && boundedCapture.Status == InspectionStatus.Truncated,
            "Provider capture must preserve the fake backend's bounded inventory result.");
    }

    private static async Task CheckProviderBudgetAndCancellationAsync()
    {
        var noBudget = new FakeBackend(Identity) { ThreadIds = [81] };
        var timedOut = await CaptureAsync(noBudget, TimeSpan.Zero);
        Check(timedOut.Status == InspectionStatus.TimedOut && timedOut.Threads.Count == 0 &&
            noBudget.EnumerationCalls == 0, "A zero budget must time out before thread enumeration.");

        var insufficient = new FakeBackend(Identity) { ThreadIds = [82] };
        var beforeDelay = await CaptureAsync(insufficient, TimeSpan.FromMilliseconds(200));
        Check(beforeDelay.Status == InspectionStatus.TimedOut && beforeDelay.Threads.Single().Status == InspectionStatus.TimedOut &&
            beforeDelay.Threads.Single().CpuPercent is null, "Insufficient pre-delay budget must label a missing CPU pair TimedOut.");

        var cancellation = new FakeBackend(Identity) { ThreadIds = [83] };
        var delayEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using (var source = new CancellationTokenSource())
        {
            var provider = new WindowsThreadDiagnosticsProvider(cancellation, TimeSpan.FromSeconds(2),
                async (_, token) =>
                {
                    delayEntered.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                });
            var pending = provider.CaptureAsync(Identity, source.Token);
            await delayEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            source.Cancel();
            await ExpectCancellationAsync(pending);
        }
        Check(cancellation.ProcessLeases.Single().DisposeCount == 1 &&
            cancellation.ThreadLeases.Single().DisposeCount == 1,
            "Cancellation during the injected delay must dispose process and thread leases.");
    }

    private static void CheckThreadLifetimeReader()
    {
        using var handle = new SafeProcessHandle(new IntPtr(123), false);
        var api = new FakeLifetimeApi([258, 258]);
        Check(ThreadLifetimeReader.ReadProcess(handle, api) == 123 && api.WaitCount == 2,
            "Process creation reads must require live waits before and after the native read.");

        api = new FakeLifetimeApi([258, 258]);
        var thread = ThreadLifetimeReader.ReadThread(handle, api);
        Check(thread.OwnerPid == 77 && thread.CreationTimeFileTime == 123 && thread.KernelTimeTicks == 100 &&
            thread.UserTimeTicks == 200 && api.WaitCount == 2,
            "Thread owner and times must be read between two live waits.");
        Check(api.LastTimes.Exit == ulong.MaxValue, "An undefined exit FILETIME must not invalidate a live thread.");

        ExpectDiagnosticStatus(() => ThreadDiagnosticsNative.ValidateWait(0, 0), InspectionStatus.Exited,
            "A signaled zero-duration wait means exited.");
        ExpectDiagnosticStatus(() => ThreadDiagnosticsNative.ValidateWait(0xFFFFFFFF, 5), InspectionStatus.Failed,
            "An unexpected wait result must fail liveness validation.");
    }

    private static async Task CheckNativeWaitChainParsingAndLifetimeAsync()
    {
        Check(NativeWaitChainOperation.NodeSize == 280, "The WCT x64 node stride must be 280 bytes.");
        foreach (uint status in new uint[] { 234, 565 })
        {
            var api = new FakeWaitChainApi(status, reportedCount: 20,
                [WaitNode.Thread(300, 400), WaitNode.Object("Mutex-A")], cycle: true, callbackOnStart: true);
            using var operation = new NativeWaitChainOperation(api, 91);
            var result = await operation.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Check(api.StartResult == 997 && result.Status == InspectionStatus.Truncated &&
                result.Nodes.Count <= NativeWaitChainOperation.MaximumNodes && result.Nodes.Count == 16,
                $"WCT status {status} must return a bounded truncated prefix.");
            Check(result.CycleDetected == true && result.Nodes[0].ObjectType == 8 &&
                result.Nodes[0].ProcessId == 300 && result.Nodes[0].ThreadId == 400,
                "WCT thread node fields must parse from offsets 8 and 12.");
            Check(result.Nodes[1].ObjectName == "Mutex-A", "WCT object names must parse as UTF-16 from offset 8.");
        }

        var deniedApi = new FakeWaitChainApi(5, reportedCount: 0, [], cycle: false, callbackOnStart: true);
        using (var operation = new NativeWaitChainOperation(deniedApi, 92))
        {
            var result = await operation.Completion.WaitAsync(TimeSpan.FromSeconds(2));
            Check(result.Status == InspectionStatus.AccessDenied && result.Nodes.Count == 0 && result.CycleDetected is null,
                "WCT access denied must expose no node or cycle data.");
        }

        var lifetimeApi = new FakeWaitChainApi(0, reportedCount: 1, [WaitNode.Thread((uint)Identity.Pid, 94)],
            cycle: false, callbackOnStart: false);
        var pending = new NativeWaitChainOperation(lifetimeApi, 94);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Check(lifetimeApi.CallbackIsAlive, "The native operation must root its callback while WCT is pending.");
        pending.Dispose();
        var completed = await pending.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Check(lifetimeApi.CloseSawLiveBuffers && lifetimeApi.CallbackReturnedBeforeCloseReturned &&
            lifetimeApi.CallbackThreadId != lifetimeApi.CloseThreadId && completed.Nodes.Count == 1,
            "Close must retain callback buffers through a callback on another thread and wait for callback return.");
    }

    private static async Task CheckProviderWaitChainAsync()
    {
        var cancelApi = new FakeWaitChainApi(0, reportedCount: 1, [WaitNode.Thread((uint)Identity.Pid, 95)],
            cycle: false, callbackOnStart: false);
        var cancelBackend = new FakeBackend(Identity)
        {
            ThreadIds = [95],
            StartWaitChainFactory = tid => new NativeWaitChainOperation(cancelApi, tid)
        };
        using (var source = new CancellationTokenSource())
        {
            var provider = new WindowsThreadDiagnosticsProvider(cancelBackend, TimeSpan.FromSeconds(2));
            var pending = provider.CaptureWaitChainAsync(new(Identity, 95, 9876), source.Token);
            await cancelApi.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
            source.Cancel();
            await ExpectCancellationAsync(pending);
        }
        Check(cancelApi.CloseSawLiveBuffers && cancelApi.CallbackReturnedBeforeCloseReturned,
            "Provider cancellation must not finish until native close has completed its callback.");

        var timeoutApi = new FakeWaitChainApi(0, reportedCount: 1, [WaitNode.Thread((uint)Identity.Pid, 96)],
            cycle: false, callbackOnStart: false);
        var timeoutBackend = new FakeBackend(Identity)
        {
            ThreadIds = [96],
            StartWaitChainFactory = tid => new NativeWaitChainOperation(timeoutApi, tid)
        };
        var timeoutProvider = new WindowsThreadDiagnosticsProvider(timeoutBackend, TimeSpan.FromMilliseconds(120));
        var timeout = await timeoutProvider.CaptureWaitChainAsync(new(Identity, 96, 9876), CancellationToken.None);
        Check(timeout.Status == InspectionStatus.TimedOut && timeout.Nodes.Count == 0 && timeout.CycleDetected is null &&
            timeoutApi.CloseSawLiveBuffers && timeoutApi.CallbackReturnedBeforeCloseReturned,
            "A pending WCT timeout must discard nodes/cycle and dispose after close callback completion.");

        var mismatchApi = new FakeWaitChainApi(0, reportedCount: 1, [WaitNode.Thread((uint)Identity.Pid, 97)],
            cycle: true, callbackOnStart: true);
        var mismatchBackend = new FakeBackend(Identity)
        {
            ThreadIds = [97],
            StartWaitChainFactory = tid => new NativeWaitChainOperation(mismatchApi, tid),
            ThreadStateForRead = (_, read) => read == 0
                ? State(Identity.Pid, 9876, 0, 0)
                : State(Identity.Pid, 9877, 1, 1)
        };
        var mismatchProvider = new WindowsThreadDiagnosticsProvider(mismatchBackend, TimeSpan.FromSeconds(2));
        var mismatch = await mismatchProvider.CaptureWaitChainAsync(new(Identity, 97, 9876), CancellationToken.None);
        Check(mismatch.Status == InspectionStatus.IdentityMismatch && mismatch.Nodes.Count == 0 &&
            mismatch.CycleDetected is null, "A post-WCT thread identity mismatch must discard nodes and cycle data.");
    }

    private static async Task<ThreadInspection> CaptureAsync(FakeBackend backend, TimeSpan budget) =>
        await new WindowsThreadDiagnosticsProvider(backend, budget, (_, _) => Task.CompletedTask)
            .CaptureAsync(Identity, CancellationToken.None);

    private static ThreadDiagnosticState State(int owner, long creation, long kernel, long user) =>
        new(owner, creation, kernel, user);

    private static async Task ExpectCancellationAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
            return;
        }
        throw new InvalidOperationException("Expected cancellation.");
    }

    private static void ExpectDiagnosticStatus(Action action, InspectionStatus expected, string message)
    {
        try
        {
            action();
        }
        catch (ThreadDiagnosticException ex) when (ex.Status == expected)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

internal sealed class FakeBackend(ProcessIdentity identity) : IThreadDiagnosticsBackend
{
    private readonly List<FakeProcessLease> processLeases = [];
    private readonly List<FakeThreadLease> threadLeases = [];
    internal bool IsSupported { get; set; } = true;
    bool IThreadDiagnosticsBackend.IsSupported => IsSupported;
    internal IReadOnlyList<FakeProcessLease> ProcessLeases => processLeases;
    internal IReadOnlyList<FakeThreadLease> ThreadLeases => threadLeases;
    internal int[] ThreadIds { get; set; } = [];
    internal Func<int, long> ProcessCreationForRead { get; set; } = _ => identity.CreationTimeFileTime!.Value;
    internal Func<int, int, ThreadDiagnosticState> ThreadStateForRead { get; set; } =
        (_, _) => new(identity.Pid, 9876, 0, 0);
    internal Func<int, Exception?> OpenThreadFailure { get; set; } = _ => null;
    internal Func<int, IWaitChainOperation>? StartWaitChainFactory { get; set; }
    internal int EnumerationCalls { get; private set; }
    internal int? SelectedLimit { get; private set; }
    internal int? EnumerationLimit { get; private set; }

    public IProcessDiagnosticLease OpenProcess(int pid)
    {
        var lease = new FakeProcessLease(ProcessCreationForRead);
        processLeases.Add(lease);
        return lease;
    }

    public IThreadDiagnosticLease OpenThread(int tid)
    {
        var error = OpenThreadFailure(tid);
        if (error is not null) throw error;
        var lease = new FakeThreadLease(tid, ThreadStateForRead);
        threadLeases.Add(lease);
        return lease;
    }

    public ThreadInventory EnumerateThreads(int pid, int selectedLimit, int enumerationLimit, Func<bool> expired)
    {
        EnumerationCalls++;
        SelectedLimit = selectedLimit;
        EnumerationLimit = enumerationLimit;
        var result = new List<int>();
        var visited = 0;
        foreach (int tid in ThreadIds)
        {
            if (expired()) return new(result, InspectionStatus.TimedOut, "fake timeout");
            if (visited++ >= enumerationLimit)
                return new(result, InspectionStatus.Truncated, "fake enumeration bound");
            if (result.Count >= selectedLimit)
                return new(result, InspectionStatus.Truncated, "fake selected bound");
            result.Add(tid);
        }
        return new(result, InspectionStatus.Available, "fake inventory");
    }

    public IWaitChainOperation StartWaitChain(int tid) =>
        StartWaitChainFactory?.Invoke(tid) ?? throw new InvalidOperationException("Unexpected fake WCT start.");

    internal sealed class FakeProcessLease(Func<int, long> creationForRead) : IProcessDiagnosticLease
    {
        private int reads;
        internal int DisposeCount { get; private set; }
        public long ReadCreationTime() => creationForRead(reads++);
        public void Dispose() => DisposeCount++;
    }

    internal sealed class FakeThreadLease(int tid, Func<int, int, ThreadDiagnosticState> stateForRead) : IThreadDiagnosticLease
    {
        private int reads;
        internal int DisposeCount { get; private set; }
        public ThreadDiagnosticState Read() => stateForRead(tid, reads++);
        public void Dispose() => DisposeCount++;
    }
}

internal sealed class FakeLifetimeApi(IEnumerable<uint> waits) : IThreadLifetimeApi
{
    private readonly Queue<uint> waits = new(waits);
    internal int WaitCount { get; private set; }
    internal DiagnosticNativeTimes LastTimes { get; private set; }

    public void RequireAlive(SafeProcessHandle handle)
    {
        WaitCount++;
        ThreadDiagnosticsNative.ValidateWait(waits.Dequeue(), 0);
    }

    public uint GetOwner(SafeProcessHandle handle) => 77;

    public DiagnosticNativeTimes ReadTimes(SafeProcessHandle handle, bool thread)
    {
        LastTimes = new(123, ulong.MaxValue, 100, 200);
        return LastTimes;
    }
}

internal sealed record WaitNode(uint Type, uint Status, string? Name, uint Pid, uint Tid)
{
    internal static WaitNode Thread(uint pid, uint tid) => new(8, 0, null, pid, tid);
    internal static WaitNode Object(string name) => new(2, 0, name, 0, 0);
}

internal sealed class FakeWaitChainApi : IThreadWaitChainApi
{
    private readonly uint callbackStatus;
    private readonly int reportedCount;
    private readonly WaitNode[] nodesToWrite;
    private readonly bool cycle;
    private readonly bool callbackOnStart;
    private WeakReference<ThreadWaitChainCallback>? callback;
    private IntPtr session, count, nodeArray, cyclePointer;
    private int callbackReturned;
    private int callbackThreadId;
    private int closeThreadId;

    internal FakeWaitChainApi(uint callbackStatus, int reportedCount, WaitNode[] nodesToWrite, bool cycle, bool callbackOnStart)
    {
        this.callbackStatus = callbackStatus;
        this.reportedCount = reportedCount;
        this.nodesToWrite = nodesToWrite;
        this.cycle = cycle;
        this.callbackOnStart = callbackOnStart;
    }

    internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal int StartResult { get; private set; }
    internal bool CallbackIsAlive => callback is not null && callback.TryGetTarget(out _);
    internal bool CloseSawLiveBuffers { get; private set; }
    internal bool CallbackReturnedBeforeCloseReturned => Volatile.Read(ref callbackReturned) == 1;
    internal int CallbackThreadId => Volatile.Read(ref callbackThreadId);
    internal int CloseThreadId => Volatile.Read(ref closeThreadId);

    public IntPtr Open(ThreadWaitChainCallback callback)
    {
        this.callback = new(callback);
        session = new IntPtr(1);
        return session;
    }

    public int Start(IntPtr session, int tid, IntPtr count, IntPtr nodes, IntPtr cycle)
    {
        this.session = session;
        this.count = count;
        nodeArray = nodes;
        cyclePointer = cycle;
        StartResult = 997;
        WriteBuffers();
        Started.TrySetResult();
        if (callbackOnStart) InvokeCallback();
        return StartResult;
    }

    public void Close(IntPtr session)
    {
        closeThreadId = Environment.CurrentManagedThreadId;
        CloseSawLiveBuffers = session == this.session && count != IntPtr.Zero && nodeArray != IntPtr.Zero &&
            cyclePointer != IntPtr.Zero && Marshal.ReadInt32(count) == reportedCount &&
            (nodesToWrite.Length == 0 || Marshal.ReadInt32(nodeArray) == (int)nodesToWrite[0].Type) &&
            Marshal.ReadInt32(cyclePointer) == (cycle ? 1 : 0);
        var done = new ManualResetEventSlim();
        var worker = new Thread(() =>
        {
            try
            {
                InvokeCallback();
                Interlocked.Exchange(ref callbackReturned, 1);
            }
            finally
            {
                done.Set();
            }
        }) { IsBackground = true };
        worker.Start();
        if (!done.Wait(TimeSpan.FromSeconds(3))) throw new TimeoutException("Fake WCT close did not wait for callback.");
        worker.Join();
        done.Dispose();
    }

    private void InvokeCallback()
    {
        if (callback is null || !callback.TryGetTarget(out var rootedCallback))
            throw new InvalidOperationException("Native callback was not rooted for the operation lifetime.");
        callbackThreadId = Environment.CurrentManagedThreadId;
        rootedCallback(session, UIntPtr.Zero, callbackStatus, count, nodeArray, cyclePointer);
    }

    private void WriteBuffers()
    {
        Marshal.WriteInt32(count, reportedCount);
        Marshal.WriteInt32(cyclePointer, cycle ? 1 : 0);
        for (var i = 0; i < Math.Min(nodesToWrite.Length, NativeWaitChainOperation.MaximumNodes); i++)
        {
            var item = nodesToWrite[i];
            var node = IntPtr.Add(nodeArray, i * NativeWaitChainOperation.NodeSize);
            Marshal.WriteInt32(node, unchecked((int)item.Type));
            Marshal.WriteInt32(node, 4, unchecked((int)item.Status));
            if (item.Type == 8)
            {
                Marshal.WriteInt32(node, 8, unchecked((int)item.Pid));
                Marshal.WriteInt32(node, 12, unchecked((int)item.Tid));
            }
            else if (item.Name is { } name)
            {
                Marshal.Copy(Encoding.Unicode.GetBytes(name + '\0'), 0, IntPtr.Add(node, 8), Encoding.Unicode.GetByteCount(name + '\0'));
            }
        }
        for (var i = nodesToWrite.Length; i < Math.Min(reportedCount, NativeWaitChainOperation.MaximumNodes); i++)
            Marshal.WriteInt32(IntPtr.Add(nodeArray, i * NativeWaitChainOperation.NodeSize), 8);
    }
}
