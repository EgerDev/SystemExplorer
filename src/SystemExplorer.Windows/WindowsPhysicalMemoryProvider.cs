using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using SystemExplorer.Core;

namespace SystemExplorer.Windows;

public sealed class WindowsPhysicalMemoryProvider : IPhysicalMemoryProvider
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public async Task<PhysicalMemoryInspection> CaptureAsync(IProgress<(ulong Done, ulong Total)>? progress, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => Capture(progress, cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { Gate.Release(); }
    }

    private static PhysicalMemoryInspection Capture(IProgress<(ulong Done, ulong Total)>? progress, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var accumulator = new PhysicalMemoryAccumulator();
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8)
            return accumulator.Build(RamReadingStatus.Unavailable, "Native x64 Windows required.", null);
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return PhysicalMemoryParser.NotAdministrator();
        var clock = Stopwatch.StartNew();
        if (!OpenProcessToken(new IntPtr(-1), 0x20 | 0x8, out var processToken))
            return accumulator.Build(RamReadingStatus.Denied, $"Current-process token open failed (Win32 {Marshal.GetLastWin32Error()}).", null);
        using (processToken)
        {
            if (!LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out var luid))
                return accumulator.Build(RamReadingStatus.Denied, $"Privilege lookup failed (Win32 {Marshal.GetLastWin32Error()}).", null);
            var requested = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(processToken, false, ref requested, Marshal.SizeOf<TokenPrivileges>(), out var previous, out _) || Marshal.GetLastWin32Error() != 0)
                return accumulator.Build(RamReadingStatus.Denied, $"SeProfileSingleProcessPrivilege not held/enabled (Win32 {Marshal.GetLastWin32Error()}); no elevation attempted.", null);
            PhysicalMemoryInspection result;
            var restored = true;
            try { result = Query(accumulator, progress, token, clock); }
            finally
            {
                restored = AdjustTokenPrivileges(processToken, false, ref previous, Marshal.SizeOf<TokenPrivileges>(), out _, out _) && Marshal.GetLastWin32Error() == 0;
            }
            return restored ? result : result with { Status = RamReadingStatus.Unavailable, Detail = result.Detail + " WARNING: current-process privilege restoration failed." };
        }
    }

    private static PhysicalMemoryInspection Query(PhysicalMemoryAccumulator accumulator, IProgress<(ulong Done, ulong Total)>? progress,
        CancellationToken token, Stopwatch clock)
    {
        bool Expired() => clock.Elapsed >= TimeSpan.FromSeconds(90);
        var lists = new byte[176];
        var listStatus = RamNative.NtQuerySystemInformation(80, lists, lists.Length, out var listLength);
        token.ThrowIfCancellationRequested();
        var memoryLists = listStatus >= 0 && listLength >= 176 ? PhysicalMemoryParser.ParseMemoryLists(lists) : null;
        var rangeBytes = new byte[65536];
        var rangeStatus = 0; var rangeCount = 0; var header = 16;
        for (var version = 2; version >= 1; version--)
        {
            token.ThrowIfCancellationRequested();
            if (Expired()) return accumulator.Build(RamReadingStatus.Truncated, "90-second cooperative budget reached before ranges.", memoryLists);
            Array.Clear(rangeBytes);
            BinaryPrimitives.WriteUInt32LittleEndian(rangeBytes, (uint)version);
            rangeStatus = Superfetch(17, rangeBytes, out _);
            if (rangeStatus >= 0)
            {
                header = version == 2 ? 16 : 8;
                rangeCount = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(rangeBytes.AsSpan(version == 2 ? 8 : 4)));
                break;
            }
        }
        if (rangeStatus < 0) return accumulator.Build(RamReadingStatus.Unavailable,
            $"SuperfetchMemoryRangesQuery v2/v1 failed (NTSTATUS 0x{rangeStatus:X8}); 64 KiB range cap.", memoryLists);
        if (rangeCount <= 0 || rangeCount > (rangeBytes.Length - header) / 16)
            return accumulator.Build(RamReadingStatus.Unavailable, "Invalid or oversized physical range count.", memoryLists);
        var ranges = new List<(ulong Base, ulong Count)>();
        ulong total = 0;
        for (var i = 0; i < rangeCount; i++)
        {
            var start = BinaryPrimitives.ReadUInt64LittleEndian(rangeBytes.AsSpan(header + i * 16));
            var count = BinaryPrimitives.ReadUInt64LittleEndian(rangeBytes.AsSpan(header + i * 16 + 8));
            if (count > ulong.MaxValue - start || count > ulong.MaxValue - total)
                return accumulator.Build(RamReadingStatus.Unavailable, "Physical range overflow rejected.", memoryLists);
            total += count; ranges.Add((start, count));
        }
        const int batchSize = 65536;
        const ulong maximumPages = 268435456;
        foreach (var range in ranges)
        {
            for (ulong offset = 0; offset < range.Count;)
            {
                token.ThrowIfCancellationRequested();
                if (Expired() || accumulator.PagesScanned >= maximumPages)
                    return accumulator.Build(RamReadingStatus.Truncated, "PFN scan reached 90-second/268M-page cap; counts are lower bounds, not a complete RAM map.", memoryLists);
                var count = (int)Math.Min((ulong)batchSize, Math.Min(range.Count - offset, maximumPages - accumulator.PagesScanned));
                var batch = new byte[192 + count * 24];
                BinaryPrimitives.WriteUInt32LittleEndian(batch, 1);
                BinaryPrimitives.WriteUInt32LittleEndian(batch.AsSpan(4), 1);
                BinaryPrimitives.WriteUInt64LittleEndian(batch.AsSpan(8), (ulong)count);
                for (var p = 0; p < count; p++) BinaryPrimitives.WriteUInt64LittleEndian(batch.AsSpan(192 + p * 24 + 8), range.Base + offset + (ulong)p);
                var status = Superfetch(6, batch, out _);
                token.ThrowIfCancellationRequested();
                if (status < 0) return accumulator.Build(RamReadingStatus.Unavailable,
                    $"SuperfetchPfnQuery failed (NTSTATUS 0x{status:X8}); retained prefix only, no missing pages inferred.", memoryLists);
                if (BinaryPrimitives.ReadUInt64LittleEndian(batch.AsSpan(8)) != (ulong)count)
                    return accumulator.Build(RamReadingStatus.Unavailable, "PFN response count mismatch; batch rejected.", memoryLists);
                accumulator.Add(batch.AsSpan(192), token);
                offset += (ulong)count;
                progress?.Report((accumulator.PagesScanned, total));
            }
        }
        return accumulator.Build(memoryLists is null ? RamReadingStatus.Unavailable : RamReadingStatus.Available,
            $"Read-only PFN attribute query; {accumulator.PagesScanned:N0}/{total:N0} pages in {clock.Elapsed.TotalSeconds:F2}s. Memory-list NTSTATUS 0x{listStatus:X8}. Not an atomic snapshot.", memoryLists);
    }

    private static int Superfetch(int informationClass, byte[] bytes, out int returned)
    {
        var pin = GCHandle.Alloc(bytes, GCHandleType.Pinned);
        try
        {
            var information = new SuperfetchInformation { Version = 45, Magic = 0x6b756843, Class = informationClass,
                Data = pin.AddrOfPinnedObject(), Length = (uint)bytes.Length };
            return NtQuerySystemInformation(79, ref information, Marshal.SizeOf<SuperfetchInformation>(), out returned);
        }
        finally { pin.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)] private struct SuperfetchInformation
    { internal uint Version, Magic; internal int Class; internal IntPtr Data; internal uint Length; }
    [StructLayout(LayoutKind.Sequential, Pack = 4)] private struct TokenPrivileges
    { internal uint Count; internal long Luid; internal uint Attributes; }
    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int informationClass, ref SuperfetchInformation information, int length, out int returned);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool LookupPrivilegeValue(string? system, string name, out long luid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token, bool disableAll, ref TokenPrivileges privileges,
        int length, out TokenPrivileges previous, out int returned);
}
