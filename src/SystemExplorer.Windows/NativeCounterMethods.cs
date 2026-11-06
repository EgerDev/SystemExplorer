using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SystemExplorer.SystemCounters.Smoke")]

namespace SystemExplorer.Windows;

internal static class NativeCounterMethods
{
    internal const uint PDH_CSTATUS_VALID_DATA = 0, PDH_CSTATUS_NEW_DATA = 1,
        PDH_CSTATUS_NO_OBJECT = 0xC0000BB8, PDH_CSTATUS_NO_COUNTER = 0xC0000BB9, PDH_CSTATUS_INVALID_DATA = 0xC0000BBA,
        PDH_CSTATUS_NO_INSTANCE = 0x800007D1, PDH_MORE_DATA = 0x800007D2,
        PDH_NO_DATA = 0x800007D5, PDH_INVALID_DATA = 0xC0000BC6,
        PDH_CALC_NEGATIVE_DENOMINATOR = 0x800007D6, PDH_CALC_NEGATIVE_TIMEBASE = 0x800007D7,
        PDH_CALC_NEGATIVE_VALUE = 0x800007D8;
    internal const uint Format = 0x200 | 0x8000;

    [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern uint PdhOpenQueryW(string? source, UIntPtr userData, out SafePdhQueryHandle query);
    [DllImport("pdh.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    internal static extern uint PdhAddEnglishCounterW(SafePdhQueryHandle query, string path, UIntPtr userData, out IntPtr counter);
    [DllImport("pdh.dll", ExactSpelling = true)]
    internal static extern uint PdhCollectQueryData(SafePdhQueryHandle query);
    [DllImport("pdh.dll", ExactSpelling = true)]
    internal static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bytes, out uint count, IntPtr buffer);
    [DllImport("pdh.dll", ExactSpelling = true)]
    internal static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out FormattedValue value);
    [DllImport("pdh.dll", ExactSpelling = true)]
    internal static extern uint PdhRemoveCounter(IntPtr counter);
    [DllImport("pdh.dll", ExactSpelling = true)]
    internal static extern uint PdhCloseQuery(IntPtr query);
    [StructLayout(LayoutKind.Explicit, Size = 16)]
    internal struct FormattedValue
    {
        [FieldOffset(0)] internal uint CStatus;
        [FieldOffset(8)] internal double Value;
    }

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIfTable2(out IntPtr table);
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern void FreeMibTable(IntPtr table);

    internal static IfRow ParseIfRow(ReadOnlySpan<byte> row)
    {
        if (row.Length < 1352) throw new ArgumentException("MIB_IF_ROW2 requires 1352 bytes.", nameof(row));
        return new(BinaryPrimitives.ReadUInt64LittleEndian(row), U32(row, 8),
            ReadString(row.Slice(28, 514)), ReadString(row.Slice(542, 514)),
            U32(row, 1128), U32(row, 1132), U32(row, 1136), row[1152], U32(row, 1156),
            U64(row, 1192), U64(row, 1200), U64(row, 1208), U64(row, 1280));
    }

    private static uint U32(ReadOnlySpan<byte> row, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(row[offset..]);
    private static ulong U64(ReadOnlySpan<byte> row, int offset) => BinaryPrimitives.ReadUInt64LittleEndian(row[offset..]);
    private static string ReadString(ReadOnlySpan<byte> bytes)
    {
        var length = 0;
        while (length < bytes.Length && BinaryPrimitives.ReadUInt16LittleEndian(bytes[length..]) != 0) length += 2;
        return Encoding.Unicode.GetString(bytes[..length]);
    }

    internal static IfTableResult ReadInterfaces(int maximumRows = 256)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumRows);
        if (IntPtr.Size != 8) return new(50, [], false);
        IntPtr table = IntPtr.Zero;
        try
        {
            var status = GetIfTable2(out table);
            if (status != 0) return new(status, [], false);
            if (table == IntPtr.Zero) return new(13, [], false);
            var count = unchecked((uint)Marshal.ReadInt32(table));
            var retained = (int)Math.Min(count, (uint)maximumRows);
            var rows = new List<IfRow>(retained);
            var buffer = new byte[1352];
            for (var i = 0; i < retained; i++)
            {
                Marshal.Copy(IntPtr.Add(table, checked(8 + i * 1352)), buffer, 0, buffer.Length);
                rows.Add(ParseIfRow(buffer));
            }
            return new(0, rows, count > retained);
        }
        finally { if (table != IntPtr.Zero) FreeMibTable(table); }
    }

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern SafeCounterProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint WaitForSingleObject(SafeCounterProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeCounterProcessHandle handle, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(SafeCounterProcessHandle process, uint flags, StringBuilder name, ref uint size);

    internal static ProcessImageResult ReadProcessImage(int pid)
    {
        using var handle = OpenProcess(0x1000, false, (uint)pid);
        if (handle.IsInvalid) return new(false, Marshal.GetLastWin32Error(), false);
        var buffer = new StringBuilder(32768);
        uint size = (uint)buffer.Capacity;
        if (QueryFullProcessImageNameW(handle, 0, buffer, ref size)) return new(size != 0, 0);
        return new(false, Marshal.GetLastWin32Error());
    }

    internal static ProcessCreationResult ReadProcessCreationTime(int pid)
    {
        if (pid < 0) return new(ProcessCreationStatus.Failed, null, 87);
        using var handle = OpenProcess(0x1000 | 0x100000, false, (uint)pid);
        if (handle.IsInvalid) return Failure(Marshal.GetLastWin32Error());
        var before = WaitForSingleObject(handle, 0);
        if (before != 258) return before == 0 ? new(ProcessCreationStatus.Exited, null, 0) : Failure(Marshal.GetLastWin32Error());
        if (!GetProcessTimes(handle, out var creation, out _, out _, out _)) return Failure(Marshal.GetLastWin32Error());
        var after = WaitForSingleObject(handle, 0);
        if (after != 258) return after == 0 ? new(ProcessCreationStatus.Exited, null, 0) : Failure(Marshal.GetLastWin32Error());
        return creation > 0 ? new(ProcessCreationStatus.Available, creation, 0) : new(ProcessCreationStatus.Failed, null, 13);
    }

    private static ProcessCreationResult Failure(int error) => new(error switch
    {
        5 => ProcessCreationStatus.AccessDenied,
        87 => ProcessCreationStatus.Exited,
        _ => ProcessCreationStatus.Failed
    }, null, error);
}

internal sealed class SafePdhQueryHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafePdhQueryHandle() : base(true) { }
    protected override bool ReleaseHandle() => NativeCounterMethods.PdhCloseQuery(handle) == 0;
}

internal sealed class SafeCounterProcessHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeCounterProcessHandle() : base(true) { }
    protected override bool ReleaseHandle() => NativeCounterMethods.CloseHandle(handle);
}

internal sealed record IfRow(ulong Luid, uint Index, string Alias, string Description, uint Type, uint TunnelType,
    uint MediaType, byte Flags, uint OperStatus, ulong TransmitLinkSpeed, ulong ReceiveLinkSpeed, ulong InOctets, ulong OutOctets);
internal sealed record IfTableResult(uint Status, IReadOnlyList<IfRow> Rows, bool Truncated);
internal enum ProcessCreationStatus { Available, AccessDenied, Exited, Failed }
internal readonly record struct ProcessCreationResult(ProcessCreationStatus Status, long? CreationFileTime, int Error);
internal readonly record struct ProcessImageResult(bool HasImagePath, int Error, bool QueryAttempted = true);
internal sealed record PdhInstanceValue(string Instance, double Value, uint CStatus);
internal sealed record PdhArrayResult(uint FunctionStatus, IReadOnlyList<PdhInstanceValue> Items, bool Truncated, bool IsWarmup = false);
internal readonly record struct PdhSingleResult(uint FunctionStatus, uint CStatus, double? Value, bool IsWarmup = false);

internal sealed class PdhCounterQuery : IPdhCounterQuery
{
    private const int MaximumBufferBytes = 8 * 1024 * 1024;
    private readonly SafePdhQueryHandle query;
    private readonly Dictionary<string, IntPtr> counters = new(StringComparer.Ordinal);
    private bool disposed;

    internal PdhCounterQuery()
    {
        var status = NativeCounterMethods.PdhOpenQueryW(null, UIntPtr.Zero, out query);
        if (status != 0)
        {
            query.Dispose();
            throw new InvalidOperationException($"PdhOpenQueryW failed: 0x{status:X8}.");
        }
    }

    public uint AddCounter(string path)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (counters.ContainsKey(path)) return 0;
        var status = NativeCounterMethods.PdhAddEnglishCounterW(query, path, UIntPtr.Zero, out var counter);
        if (status == 0) counters.Add(path, counter);
        return status;
    }

    public uint RemoveCounter(string path)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!counters.TryGetValue(path, out var counter)) return 0;
        var status = NativeCounterMethods.PdhRemoveCounter(counter);
        if (status == 0) counters.Remove(path);
        return status;
    }

    public uint Collect()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        return NativeCounterMethods.PdhCollectQueryData(query);
    }

    public PdhArrayResult ReadArray(string path, int maximumItems = 4096)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumItems);
        if (IntPtr.Size != 8) return new(NativeCounterMethods.PDH_INVALID_DATA, [], false);
        var counter = counters[path];
        uint bytes = 0;
        var status = NativeCounterMethods.PdhGetFormattedCounterArrayW(counter, NativeCounterMethods.Format, ref bytes, out _, IntPtr.Zero);
        if (status != NativeCounterMethods.PDH_MORE_DATA) return new(status, [], false);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (bytes == 0 || bytes > MaximumBufferBytes) return new(NativeCounterMethods.PDH_MORE_DATA, [], true);
            var allocated = (int)bytes;
            var buffer = Marshal.AllocHGlobal(allocated);
            try
            {
                status = NativeCounterMethods.PdhGetFormattedCounterArrayW(counter, NativeCounterMethods.Format, ref bytes, out var count, buffer);
                if (status == NativeCounterMethods.PDH_MORE_DATA) continue;
                if (status != 0) return new(status, [], false);
                if (count > allocated / 24) return new(NativeCounterMethods.PDH_INVALID_DATA, [], false);
                var retained = (int)Math.Min(count, (uint)maximumItems);
                var items = new List<PdhInstanceValue>(retained);
                for (var i = 0; i < retained; i++)
                {
                    var item = IntPtr.Add(buffer, i * 24);
                    var name = Marshal.ReadIntPtr(item);
                    var offset = name.ToInt64() - buffer.ToInt64();
                    if (offset < count * 24L || offset > allocated - 2 || offset % 2 != 0)
                        return new(NativeCounterMethods.PDH_INVALID_DATA, [], false);
                    var maxCharacters = (allocated - (int)offset) / 2;
                    var length = 0;
                    while (length < maxCharacters && Marshal.ReadInt16(name, length * 2) != 0) length++;
                    if (length == maxCharacters) return new(NativeCounterMethods.PDH_INVALID_DATA, [], false);
                    items.Add(new(Marshal.PtrToStringUni(name, length)!, Marshal.PtrToStructure<NativeCounterMethods.FormattedValue>(IntPtr.Add(item, 8)).Value,
                        unchecked((uint)Marshal.ReadInt32(item, 8))));
                }
                return new(0, items, count > retained);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return new(status, [], true);
    }

    public PdhSingleResult ReadSingle(string path)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        var status = NativeCounterMethods.PdhGetFormattedCounterValue(counters[path], NativeCounterMethods.Format, out _, out var value);
        return new(status, value.CStatus, status == 0 && value.CStatus <= 1 ? value.Value : null);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        try
        {
            foreach (var counter in counters.Values) NativeCounterMethods.PdhRemoveCounter(counter);
        }
        finally
        {
            counters.Clear();
            query.Dispose();
        }
    }
}
