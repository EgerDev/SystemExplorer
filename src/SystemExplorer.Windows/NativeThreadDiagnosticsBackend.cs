using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SystemExplorer.Core;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SystemExplorer.Threads.Smoke")]

namespace SystemExplorer.Windows;

internal sealed class NativeThreadDiagnosticsBackend : IThreadDiagnosticsBackend
{
    private readonly IThreadLifetimeApi lifetime;
    internal NativeThreadDiagnosticsBackend(IThreadLifetimeApi? lifetime = null) => this.lifetime = lifetime ?? new WindowsThreadLifetimeApi();
    public bool IsSupported => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    public IProcessDiagnosticLease OpenProcess(int pid)
    {
        var handle = NativeMethods.OpenProcess(0x1000 | ThreadDiagnosticsNative.Synchronize, false, (uint)pid);
        if (!handle.IsInvalid) return new ProcessLease(handle, lifetime);
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw ThreadDiagnosticsNative.Error(error, "OpenProcess (query limited + synchronize)");
    }

    public IThreadDiagnosticLease OpenThread(int tid)
    {
        var handle = ThreadDiagnosticsNative.OpenThread(0x0800 | ThreadDiagnosticsNative.Synchronize, false, (uint)tid);
        if (!handle.IsInvalid) return new ThreadLease(handle, lifetime);
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw ThreadDiagnosticsNative.Error(error, "OpenThread (query limited + synchronize)");
    }

    public ThreadInventory EnumerateThreads(int pid, int selectedLimit, int enumerationLimit, Func<bool> expired)
    {
        var ids = new List<int>();
        using var snapshot = NativeMethods.CreateToolhelp32Snapshot(0x04, 0);
        if (snapshot.IsInvalid) throw ThreadDiagnosticsNative.Error(Marshal.GetLastWin32Error(), "Thread snapshot");
        var entry = new ThreadDiagnosticsNative.ThreadEntry { Size = (uint)Marshal.SizeOf<ThreadDiagnosticsNative.ThreadEntry>() };
        var more = ThreadDiagnosticsNative.Thread32First(snapshot, ref entry);
        var error = more ? 0 : Marshal.GetLastWin32Error();
        var visited = 0;
        while (more)
        {
            if (expired()) return new(ids, InspectionStatus.TimedOut, "Thread enumeration reached its cooperative budget.");
            if (visited++ >= enumerationLimit) return new(ids, InspectionStatus.Truncated, $"System thread enumeration bounded at {enumerationLimit} entries.");
            if (entry.Size < 16) return new(ids, InspectionStatus.Partial, "Toolhelp thread entry lacks owner identity fields.");
            if (entry.OwnerPid == (uint)pid)
            {
                if (ids.Count >= selectedLimit) return new(ids, InspectionStatus.Truncated, $"Selected process thread inventory bounded at {selectedLimit} threads.");
                if (entry.ThreadId is 0 or > int.MaxValue) return new(ids, InspectionStatus.Partial, "Toolhelp returned an unrepresentable thread identity.");
                ids.Add((int)entry.ThreadId);
            }
            entry.Size = (uint)Marshal.SizeOf<ThreadDiagnosticsNative.ThreadEntry>();
            more = ThreadDiagnosticsNative.Thread32Next(snapshot, ref entry);
            error = more ? 0 : Marshal.GetLastWin32Error();
        }
        return error == 18
            ? new(ids, InspectionStatus.Available, "On-demand Toolhelp thread inventory; threads starting after the snapshot are not included.")
            : new(ids, InspectionStatus.Partial, $"Thread enumeration incomplete (Windows error {error}).");
    }

    public IWaitChainOperation StartWaitChain(int tid) => new NativeWaitChainOperation(new WindowsWaitChainApi(), tid);

    private sealed class ProcessLease(SafeProcessHandle handle, IThreadLifetimeApi lifetime) : IProcessDiagnosticLease
    {
        public long ReadCreationTime() => ThreadLifetimeReader.ReadProcess(handle, lifetime);
        public void Dispose() => handle.Dispose();
    }

    private sealed class ThreadLease(SafeProcessHandle handle, IThreadLifetimeApi lifetime) : IThreadDiagnosticLease
    {
        public ThreadDiagnosticState Read() => ThreadLifetimeReader.ReadThread(handle, lifetime);
        public void Dispose() => handle.Dispose();
    }
}

internal readonly record struct DiagnosticNativeTimes(ulong Creation, ulong Exit, ulong Kernel, ulong User);
internal interface IThreadLifetimeApi
{
    void RequireAlive(SafeProcessHandle handle);
    uint GetOwner(SafeProcessHandle handle);
    DiagnosticNativeTimes ReadTimes(SafeProcessHandle handle, bool thread);
}

internal static class ThreadLifetimeReader
{
    internal static long ReadProcess(SafeProcessHandle handle, IThreadLifetimeApi api)
    {
        api.RequireAlive(handle);
        var times = api.ReadTimes(handle, false);
        api.RequireAlive(handle);
        if (times.Creation is 0 or > long.MaxValue)
            throw new ThreadDiagnosticException(InspectionStatus.Failed, "Process creation time is unavailable.");
        return (long)times.Creation;
    }

    internal static ThreadDiagnosticState ReadThread(SafeProcessHandle handle, IThreadLifetimeApi api)
    {
        api.RequireAlive(handle);
        var owner = api.GetOwner(handle);
        var times = api.ReadTimes(handle, true);
        api.RequireAlive(handle);
        if (owner is 0 or > int.MaxValue || times.Creation is 0 or > long.MaxValue || times.Kernel > long.MaxValue || times.User > long.MaxValue)
            throw new ThreadDiagnosticException(InspectionStatus.Failed, "Thread identity or counters are unrepresentable.");
        // Exit FILETIME is undefined for a live object; only the zero-duration handle waits establish liveness.
        return new((int)owner, (long)times.Creation, (long)times.Kernel, (long)times.User);
    }
}

internal sealed class WindowsThreadLifetimeApi : IThreadLifetimeApi
{
    public void RequireAlive(SafeProcessHandle handle) => ThreadDiagnosticsNative.RequireAlive(handle);
    public uint GetOwner(SafeProcessHandle handle)
    {
        var owner = ThreadDiagnosticsNative.GetProcessIdOfThread(handle);
        if (owner == 0) throw ThreadDiagnosticsNative.Error(Marshal.GetLastWin32Error(), "Thread owner");
        return owner;
    }
    public DiagnosticNativeTimes ReadTimes(SafeProcessHandle handle, bool thread)
    {
        NativeMethods.FileTime creation, exit, kernel, user;
        var success = thread
            ? ThreadDiagnosticsNative.GetThreadTimes(handle, out creation, out exit, out kernel, out user)
            : NativeMethods.GetProcessTimes(handle, out creation, out exit, out kernel, out user);
        if (!success) throw ThreadDiagnosticsNative.Error(Marshal.GetLastWin32Error(), "Process/thread times");
        return new(creation.Ticks, exit.Ticks, kernel.Ticks, user.Ticks);
    }
}

internal static class ThreadDiagnosticsNative
{
    internal const uint Synchronize = 0x00100000;

    internal static void RequireAlive(SafeProcessHandle handle) => ValidateWait(WaitForSingleObject(handle, 0), Marshal.GetLastWin32Error());

    internal static void ValidateWait(uint wait, int error)
    {
        if (wait == 258) return;
        if (wait == 0) throw new ThreadDiagnosticException(InspectionStatus.Exited, "The retained process/thread lifetime exited; data discarded.");
        throw new ThreadDiagnosticException(InspectionStatus.Failed, $"Liveness could not be verified (wait result {wait}, Windows error {error}); data discarded.");
    }

    internal static ThreadDiagnosticException Error(int error, string operation) => new(error switch
    {
        5 => InspectionStatus.AccessDenied,
        50 or 120 => InspectionStatus.Unsupported,
        1168 or 4312 => InspectionStatus.Exited,
        _ => InspectionStatus.Failed
    }, $"{operation} unavailable (Windows error {error}); no privilege adjustment, elevation or driver fallback.");

    [StructLayout(LayoutKind.Sequential)]
    internal struct ThreadEntry
    {
        internal uint Size, Usage, ThreadId, OwnerPid;
        internal int BasePriority, DeltaPriority;
        internal uint Flags;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint tid);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetProcessIdOfThread(SafeProcessHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetThreadTimes(SafeProcessHandle handle, out NativeMethods.FileTime creation,
        out NativeMethods.FileTime exit, out NativeMethods.FileTime kernel, out NativeMethods.FileTime user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Thread32First(SafeFileHandle snapshot, ref ThreadEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool Thread32Next(SafeFileHandle snapshot, ref ThreadEntry entry);
}
