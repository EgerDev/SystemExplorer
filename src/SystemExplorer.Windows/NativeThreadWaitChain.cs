using System.Runtime.InteropServices;
using SystemExplorer.Core;

namespace SystemExplorer.Windows;

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
internal delegate void ThreadWaitChainCallback(IntPtr session, UIntPtr context, uint status, IntPtr count, IntPtr nodes, IntPtr cycle);

internal interface IThreadWaitChainApi
{
    IntPtr Open(ThreadWaitChainCallback callback);
    int Start(IntPtr session, int tid, IntPtr count, IntPtr nodes, IntPtr cycle);
    void Close(IntPtr session);
}

internal sealed class NativeWaitChainOperation : IWaitChainOperation
{
    internal const int MaximumNodes = 16;
    // x64 wct.h: two DWORD enums, then a union with WCHAR[128], LARGE_INTEGER and BOOL; 8-byte alignment.
    internal const int NodeSize = 280;
    private readonly IThreadWaitChainApi api;
    private readonly ThreadWaitChainCallback callback;
    private readonly TaskCompletionSource<WaitChainResult> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private IntPtr session, count, nodes, cycle;
    public Task<WaitChainResult> Completion => completion.Task;

    internal NativeWaitChainOperation(IThreadWaitChainApi api, int tid)
    {
        this.api = api;
        callback = OnCompleted;
        try
        {
            count = Marshal.AllocHGlobal(sizeof(int));
            cycle = Marshal.AllocHGlobal(sizeof(int));
            nodes = Marshal.AllocHGlobal(MaximumNodes * NodeSize);
            Marshal.WriteInt32(count, MaximumNodes);
            Marshal.WriteInt32(cycle, 0);
            Marshal.Copy(new byte[MaximumNodes * NodeSize], 0, nodes, MaximumNodes * NodeSize);
            session = api.Open(callback);
            if (session == IntPtr.Zero) throw ThreadDiagnosticsNative.Error(Marshal.GetLastWin32Error(), "Open asynchronous WCT session");
            var error = api.Start(session, tid, count, nodes, cycle);
            if (error != 997)
                completion.TrySetResult(error == 0
                    ? new(InspectionStatus.Failed, "Asynchronous WCT did not return ERROR_IO_PENDING; no synchronous result assumed.", null, [])
                    : Failure((uint)error));
        }
        catch { Dispose(); throw; }
    }

    private void OnCompleted(IntPtr unusedSession, UIntPtr context, uint status, IntPtr nodeCount, IntPtr nodeArray, IntPtr isCycle)
    {
        try
        {
            if (status is not (0 or 234 or 565)) { completion.TrySetResult(Failure(status)); return; }
            if (nodeCount == IntPtr.Zero || nodeArray == IntPtr.Zero || isCycle == IntPtr.Zero)
            {
                completion.TrySetResult(new(InspectionStatus.Failed, "WCT returned missing callback buffers.", null, []));
                return;
            }
            var reported = unchecked((uint)Marshal.ReadInt32(nodeCount));
            var length = (int)Math.Min(reported, MaximumNodes);
            var rows = new List<WaitChainNode>(length);
            for (var i = 0; i < length; i++)
            {
                var node = IntPtr.Add(nodeArray, i * NodeSize);
                var type = unchecked((uint)Marshal.ReadInt32(node));
                var objectStatus = unchecked((uint)Marshal.ReadInt32(node, 4));
                if (type == 8)
                    rows.Add(new(i, type, objectStatus, null, unchecked((uint)Marshal.ReadInt32(node, 8)), unchecked((uint)Marshal.ReadInt32(node, 12))));
                else
                {
                    var name = Marshal.PtrToStringUni(IntPtr.Add(node, 8), 128) ?? "";
                    var end = name.IndexOf('\0');
                    if (end >= 0) name = name[..end];
                    rows.Add(new(i, type, objectStatus, name.Length == 0 ? null : name, null, null));
                }
            }
            var truncated = status is 234 or 565 || reported > MaximumNodes;
            var partial = rows.Any(row => row.ObjectStatus is 1 or 4 or 5 or 9 or 10);
            var resultStatus = truncated ? InspectionStatus.Truncated : length == 0 ? InspectionStatus.Failed : partial ? InspectionStatus.Partial : InspectionStatus.Available;
            completion.TrySetResult(new(resultStatus,
                truncated ? "WCT returned a bounded prefix (at most 16 nodes)." : partial ? "WCT contains inaccessible or incompletely described nodes." : "Asynchronous WCT observation with flags 0; no COM callbacks or out-of-process expansion.",
                length > 0 ? Marshal.ReadInt32(isCycle) != 0 : null, rows.ToArray()));
        }
        catch (Exception ex)
        {
            completion.TrySetResult(new(InspectionStatus.Failed, $"WCT callback could not be copied ({ex.GetType().Name}); results discarded.", null, []));
        }
    }

    private static WaitChainResult Failure(uint error)
    {
        var failure = ThreadDiagnosticsNative.Error(unchecked((int)error), "Wait chain");
        return new(failure.Status, failure.Message, null, []);
    }

    public void Dispose()
    {
        if (session != IntPtr.Zero)
        {
            // Microsoft guarantees cancellation callbacks have returned before Close returns.
            api.Close(session);
            session = IntPtr.Zero;
        }
        GC.KeepAlive(callback);
        if (nodes != IntPtr.Zero) { Marshal.FreeHGlobal(nodes); nodes = IntPtr.Zero; }
        if (count != IntPtr.Zero) { Marshal.FreeHGlobal(count); count = IntPtr.Zero; }
        if (cycle != IntPtr.Zero) { Marshal.FreeHGlobal(cycle); cycle = IntPtr.Zero; }
    }
}

internal sealed class WindowsWaitChainApi : IThreadWaitChainApi
{
    public IntPtr Open(ThreadWaitChainCallback callback) => OpenThreadWaitChainSession(1, callback);
    public int Start(IntPtr session, int tid, IntPtr count, IntPtr nodes, IntPtr cycle) =>
        GetThreadWaitChain(session, UIntPtr.Zero, 0, (uint)tid, count, nodes, cycle) ? 0 : Marshal.GetLastWin32Error();
    public void Close(IntPtr session) => CloseThreadWaitChainSession(session);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern IntPtr OpenThreadWaitChainSession(uint flags, ThreadWaitChainCallback callback);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadWaitChain(IntPtr session, UIntPtr context, uint flags, uint tid, IntPtr count, IntPtr nodes, IntPtr cycle);
    [DllImport("advapi32.dll")]
    private static extern void CloseThreadWaitChainSession(IntPtr session);
}
