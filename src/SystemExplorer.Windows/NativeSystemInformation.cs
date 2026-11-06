using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SystemExplorer.Core;

[assembly: InternalsVisibleTo("SystemExplorer.ObjectInventories.Smoke")]

namespace SystemExplorer.Windows;

internal sealed record NativeInformation(InspectionStatus Status, string Detail, byte[] Bytes, ulong BaseAddress = 0);
internal interface ISystemInformationApi
{
    int Query(int informationClass, bool objectTypes, IntPtr buffer, int size, out int returned);
}
internal interface IInventoryProcessLease : IDisposable
{
    InspectionStatus Validate(ProcessIdentity identity);
    CycleReading ReadCycles() => new(null, "Process cycle query is unavailable.");
}
internal readonly record struct CycleReading(ulong? Value, string Detail);
internal interface IActivityThreadLease : IDisposable
{
    CycleReading Read(ThreadIdentity identity);
}
internal interface IThreadActivityNative : IInventoryNative
{
    IActivityThreadLease OpenThread(int tid);
}
internal sealed record NativeActivityThread(int ThreadId, long CreationTime, uint ContextSwitches,
    int State, int WaitReason, int BasePriority, int Priority);
internal sealed record NativeActivitySnapshot(InspectionStatus Status, string Detail, IReadOnlyList<NativeActivityThread> Threads);
internal sealed record NativeProcessInventory(InspectionStatus Status, string Detail, IReadOnlyList<ProcessIdentity> Processes);
internal interface IFileUsageNative
{
    uint DriveType(string root);
    uint StartSession(out uint session);
    uint RegisterFile(uint session, string path);
    uint GetList(uint session, out uint needed, ref uint count, NativeSystemInformation.RestartManagerProcess[] processes);
    uint EndSession(uint session);
}
internal interface IInventoryNative
{
    bool IsSupported { get; }
    NativeInformation Query(int informationClass, bool objectTypes, CancellationToken token, Func<bool> expired);
    IInventoryProcessLease Open(ProcessIdentity identity);
}

internal sealed class InventoryNativeException(InspectionStatus status, string message) : Exception(message)
{
    internal InspectionStatus Status { get; } = status;
}

internal sealed class NativeSystemInformation : IThreadActivityNative, ISystemInformationApi, IFileUsageNative
{
    public bool IsSupported => OperatingSystem.IsWindows() && RuntimeInformation.ProcessArchitecture == Architecture.X64;
    public NativeInformation Query(int informationClass, bool objectTypes, CancellationToken token, Func<bool> expired) =>
        QueryBounded(this, informationClass, objectTypes, token, expired, objectTypes ? 4 * 1024 * 1024 : 64 * 1024 * 1024);

    internal static NativeInformation QueryBounded(ISystemInformationApi api, int informationClass, bool objectTypes,
        CancellationToken token, Func<bool> expired, int maximumBytes, int maximumAttempts = 8)
    {
        var size = Math.Min(65536, maximumBytes);
        for (var attempt = 0; attempt < maximumAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (expired()) return new(InspectionStatus.TimedOut, "Cooperative query budget exceeded; native calls cannot be interrupted.", []);
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                var status = api.Query(informationClass, objectTypes, buffer, size, out var returned);
                token.ThrowIfCancellationRequested();
                if (expired()) return new(InspectionStatus.TimedOut, "Native query exceeded cooperative budget.", []);
                if (status == unchecked((int)0xC0000004) || status == unchecked((int)0xC0000023))
                {
                    long next = Math.Max((long)size * 2, returned);
                    if (next > maximumBytes) return new(InspectionStatus.Truncated, "Native query exceeded byte cap.", []);
                    size = (int)next;
                    continue;
                }
                if (status < 0) return new(MapStatus(status), $"Native query refused (NTSTATUS 0x{status:X8}); no fallback or privilege adjustment.", []);
                if (returned < 0 || returned > size) return new(InspectionStatus.Failed, "Invalid native returned length.", []);
                var bytes = new byte[returned];
                Marshal.Copy(buffer, bytes, 0, returned);
                return new(InspectionStatus.Available, "Native snapshot.", bytes, unchecked((ulong)buffer.ToInt64()));
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return new(InspectionStatus.Truncated, "Native query exceeded attempt cap.", []);
    }

    internal static InspectionStatus MapStatus(int status) => unchecked((uint)status) switch
    {
        0xC0000022 => InspectionStatus.AccessDenied,
        0xC0000002 or 0xC0000003 or 0xC00000BB => InspectionStatus.Unsupported,
        _ => InspectionStatus.Failed
    };

    public int Query(int informationClass, bool objectTypes, IntPtr buffer, int size, out int returned) => objectTypes
        ? NtQueryObject(IntPtr.Zero, 3, buffer, size, out returned)
        : NtQuerySystemInformation(informationClass, buffer, size, out returned);

    public IInventoryProcessLease Open(ProcessIdentity identity)
    {
        var handle = OpenProcess(0x1000 | 0x00100000, false, identity.Pid);
        if (!handle.IsInvalid) return new ProcessLease(handle);
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new InventoryNativeException(error == 5 ? InspectionStatus.AccessDenied : error == 87 ? InspectionStatus.Exited : InspectionStatus.Failed,
            $"OpenProcess query-limited + synchronize failed (Windows error {error}).");
    }

    private sealed class ProcessLease(SafeProcessHandle handle) : IInventoryProcessLease
    {
        public InspectionStatus Validate(ProcessIdentity identity)
        {
            InspectionStatus Alive() => WaitForSingleObject(handle, 0) switch
            {
                258 => InspectionStatus.Available,
                0 => InspectionStatus.Exited,
                _ => InspectionStatus.Failed
            };
            var before = Alive();
            if (before != InspectionStatus.Available) return before;
            if (!GetProcessTimes(handle, out var creation, out _, out _, out _))
                return Marshal.GetLastWin32Error() == 5 ? InspectionStatus.AccessDenied : InspectionStatus.Failed;
            var after = Alive();
            if (after != InspectionStatus.Available) return after;
            return creation == identity.CreationTimeFileTime ? InspectionStatus.Available : InspectionStatus.IdentityMismatch;
        }
        public void Dispose() => handle.Dispose();
        public CycleReading ReadCycles() => QueryProcessCycleTime(handle, out var cycles)
            ? new(cycles, "Raw process CPU cycles.") : new(null, $"QueryProcessCycleTime failed (Windows error {Marshal.GetLastWin32Error()}).");
    }

    public IActivityThreadLease OpenThread(int tid)
    {
        var handle = OpenThread(0x0800 | 0x00100000, false, tid);
        if (!handle.IsInvalid) return new ActivityThreadLease(handle);
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new InventoryNativeException(error == 5 ? InspectionStatus.AccessDenied : error == 87 ? InspectionStatus.Exited : InspectionStatus.Failed,
            $"OpenThread query-limited + synchronize failed (Windows error {error}).");
    }

    private sealed class ActivityThreadLease(SafeProcessHandle handle) : IActivityThreadLease
    {
        public CycleReading Read(ThreadIdentity identity)
        {
            if (!identity.IsStable) return new(null, "Thread creation time is uncertain; cycles not attributed.");
            var wait = WaitForSingleObject(handle, 0);
            if (wait != 258) return new(null, wait == 0 ? "Thread exited; cycles unavailable." : "Thread liveness check failed; cycles unavailable.");
            if (!GetThreadTimes(handle, out var creation, out _, out _, out _))
                return new(null, $"GetThreadTimes failed (Windows error {Marshal.GetLastWin32Error()}).");
            if (creation != identity.CreationTimeFileTime || GetProcessIdOfThread(handle) != (uint)identity.Process.Pid)
                return new(null, "Thread identity mismatch; cycles not attributed.");
            if (!QueryThreadCycleTime(handle, out var cycles))
                return new(null, $"QueryThreadCycleTime failed (Windows error {Marshal.GetLastWin32Error()}).");
            if (WaitForSingleObject(handle, 0) != 258) return new(null, "Thread exited or liveness unavailable after cycle query.");
            return new(cycles, "Raw thread CPU cycles.");
        }
        public void Dispose() => handle.Dispose();
    }

    internal static NativeActivitySnapshot ParseProcessThreads(ReadOnlySpan<byte> bytes, ProcessIdentity identity,
        int cap = 1024, CancellationToken token = default, Func<bool>? expired = null)
    {
        if (!identity.IsStable) return new(InspectionStatus.IdentityMismatch, "Stable process identity required.", []);
        try
        {
            var offset = 0;
            var visited = 0;
            NativeActivitySnapshot? selected = null;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (expired?.Invoke() == true) return new(InspectionStatus.TimedOut, "Process snapshot parsing exceeded cooperative budget.", []);
                if (++visited > bytes.Length / 256) throw new FormatException("Process chain exceeds entry bound.");
                RequireEntries(bytes.Length - offset, 256, 0, 80);
                var next = U32(bytes, offset);
                if (next != 0 && (next < 256 || (next & 7) != 0 || next > (uint)(bytes.Length - offset - 256)))
                    throw new FormatException("Invalid NextEntryOffset in process chain.");
                var entryLength = next == 0 ? bytes.Length - offset : checked((int)next);
                var threadCount = U32(bytes, offset + 4);
                RequireEntries(entryLength, 256, threadCount, 80);
                if (U64(bytes, offset + 80) == (ulong)identity.Pid)
                {
                    if (selected != null) throw new FormatException("Duplicate process ID in snapshot.");
                    if (U64(bytes, offset + 32) != (ulong)identity.CreationTimeFileTime!.Value)
                        selected = new(InspectionStatus.IdentityMismatch, "Process snapshot creation time differs from requested identity.", []);
                    else
                    {
                        var threads = new List<NativeActivityThread>();
                        var ids = new HashSet<int>();
                        for (var i = 0; i < Math.Min((long)threadCount, cap); i++)
                        {
                            token.ThrowIfCancellationRequested();
                            if (expired?.Invoke() == true) return new(InspectionStatus.TimedOut, "Thread parsing exceeded cooperative budget.", []);
                            var t = checked(offset + 256 + i * 80);
                            var tid = U64(bytes, t + 48);
                            var creation = U64(bytes, t + 16);
                            if (U64(bytes, t + 40) != (ulong)identity.Pid || tid is 0 or > int.MaxValue || creation > long.MaxValue || !ids.Add((int)tid))
                                throw new FormatException("Invalid thread identity in process snapshot.");
                            threads.Add(new((int)tid, (long)creation, U32(bytes, t + 64), unchecked((int)U32(bytes, t + 68)),
                                unchecked((int)U32(bytes, t + 72)), unchecked((int)U32(bytes, t + 60)), unchecked((int)U32(bytes, t + 56))));
                        }
                        selected = new(threadCount > cap ? InspectionStatus.Truncated : InspectionStatus.Available,
                            $"SystemProcessInformation threads capped at {cap}.", threads.ToArray());
                    }
                }
                if (next == 0) break;
                offset = checked(offset + (int)next);
            }
            return selected ?? new(InspectionStatus.Partial, "Requested process absent from system snapshot; no rows attributed.", []);
        }
        catch (Exception error) when (error is FormatException or ArgumentException or OverflowException)
        { return new(InspectionStatus.Failed, "Malformed process snapshot: " + error.Message, []); }
    }

    internal static NativeProcessInventory ParseProcesses(ReadOnlySpan<byte> bytes, CancellationToken token = default, Func<bool>? expired = null)
    {
        try
        {
            var processes = new List<ProcessIdentity>();
            var ids = new HashSet<int>();
            var offset = 0;
            var visited = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                if (expired?.Invoke() == true) return new(InspectionStatus.TimedOut, "Process enumeration exceeded cooperative budget.", []);
                if (++visited > bytes.Length / 256) throw new FormatException("Process chain exceeds entry bound.");
                RequireEntries(bytes.Length - offset, 256, 0, 80);
                var next = U32(bytes, offset);
                if (next != 0 && (next < 256 || (next & 7) != 0 || next > (uint)(bytes.Length - offset - 256)))
                    throw new FormatException("Invalid NextEntryOffset in process chain.");
                RequireEntries(next == 0 ? bytes.Length - offset : (int)next, 256, U32(bytes, offset + 4), 80);
                var pid = U64(bytes, offset + 80);
                var creation = U64(bytes, offset + 32);
                if (pid > int.MaxValue || creation > long.MaxValue || !ids.Add((int)pid)) throw new FormatException("Invalid/duplicate process identity.");
                processes.Add(new((int)pid, creation == 0 ? null : (long)creation));
                if (next == 0) break;
                offset = checked(offset + (int)next);
            }
            return new(InspectionStatus.Available, "SystemProcessInformation process identities.", processes.ToArray());
        }
        catch (Exception error) when (error is FormatException or ArgumentException or OverflowException)
        { return new(InspectionStatus.Failed, "Malformed process snapshot: " + error.Message, []); }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RestartManagerIdentity
    {
        internal uint ProcessId, CreationLow, CreationHigh;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct RestartManagerProcess
    {
        internal RestartManagerIdentity Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] internal string ServiceName;
        internal int AppType;
        internal uint AppStatus, SessionId;
        [MarshalAs(UnmanagedType.Bool)] internal bool Restartable;
    }

    public uint DriveType(string root) => GetDriveTypeW(root);
    public uint StartSession(out uint session) => RmStartSession(out session, 0, new StringBuilder(33));
    public uint RegisterFile(uint session, string path) => RmRegisterResources(session, 1, [path], 0, IntPtr.Zero, 0, IntPtr.Zero);
    public uint GetList(uint session, out uint needed, ref uint count, RestartManagerProcess[] processes)
    {
        uint reboot = 0;
        return RmGetList(session, out needed, ref count, processes, ref reboot);
    }
    public uint EndSession(uint session) => RmEndSession(session);

    internal static uint U32(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(offset, 4));
    internal static ulong U64(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(b.Slice(offset, 8));
    internal static ushort U16(ReadOnlySpan<byte> b, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(b.Slice(offset, 2));
    internal static void RequireEntries(int length, int header, ulong count, int entrySize)
    {
        if (length < header || count > (ulong)((length - header) / entrySize))
            throw new FormatException("Count exceeds returned snapshot length.");
    }

    internal static Dictionary<int, string> ParseTypeNames(ReadOnlySpan<byte> bytes, ulong baseAddress,
        CancellationToken token = default, Func<bool>? expired = null)
    {
        RequireEntries(bytes.Length, 8, 0, 104);
        var count = U32(bytes, 0);
        RequireEntries(bytes.Length, 8, count, 104);
        var names = new Dictionary<int, string>();
        var offset = 8;
        for (uint i = 0; i < count; i++)
        {
            token.ThrowIfCancellationRequested();
            if (expired?.Invoke() == true)
                throw new InventoryNativeException(InspectionStatus.TimedOut, "Object type parsing exceeded cooperative budget.");
            RequireEntries(bytes.Length - offset, 104, 0, 1);
            var length = U16(bytes, offset);
            var maximum = U16(bytes, offset + 2);
            var pointer = U64(bytes, offset + 8);
            if ((length & 1) != 0 || (maximum & 1) != 0 || length > maximum || pointer < baseAddress ||
                pointer - baseAddress > (ulong)bytes.Length || maximum > (ulong)bytes.Length - (pointer - baseAddress))
                throw new FormatException("Object type name pointer/length outside returned snapshot.");
            var next = checked((offset + 104 + maximum + 7) & ~7);
            if (next > bytes.Length) throw new FormatException("Object type record exceeds returned snapshot.");
            var index = bytes[offset + 90]; // phnt: TypeIndex since Windows 8.1; no ordinal fallback.
            if (index != 0 && length != 0) names.Add(index, Encoding.Unicode.GetString(bytes.Slice((int)(pointer - baseAddress), length)));
            offset = next;
        }
        return names;
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQuerySystemInformation(int informationClass, IntPtr buffer, int size, out int returned);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryObject(IntPtr handle, int informationClass, IntPtr buffer, int size, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle handle, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeProcessHandle OpenThread(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int tid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetProcessIdOfThread(SafeProcessHandle handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetThreadTimes(SafeProcessHandle handle, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryProcessCycleTime(SafeProcessHandle handle, out ulong cycles);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryThreadCycleTime(SafeProcessHandle handle, out ulong cycles);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetDriveTypeW(string root);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RmStartSession(out uint session, uint flags, StringBuilder key);
    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RmRegisterResources(uint session, uint fileCount, string[] files,
        uint applicationCount, IntPtr applications, uint serviceCount, IntPtr services);
    [DllImport("rstrtmgr.dll", ExactSpelling = true)]
    private static extern uint RmGetList(uint session, out uint needed, ref uint count,
        [In, Out] RestartManagerProcess[] processes, ref uint rebootReasons);
    [DllImport("rstrtmgr.dll", ExactSpelling = true)]
    private static extern uint RmEndSession(uint session);
}
