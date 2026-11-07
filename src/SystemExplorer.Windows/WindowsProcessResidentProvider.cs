using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SystemExplorer.Core;

namespace SystemExplorer.Windows;

public sealed class WindowsProcessResidentProvider : IProcessResidentProvider
{
    private const int MaximumEntries = 4 * 1024 * 1024;
    private static readonly TimeSpan ProcessBudget = TimeSpan.FromSeconds(3), TotalBudget = TimeSpan.FromSeconds(30);

    public Task<ProcessResidentBreakdown> CaptureAsync(ProcessIdentity identity, string name, CancellationToken cancellationToken) =>
        Task.Run(() => Capture(identity, name, cancellationToken, () => false), cancellationToken);

    public Task<IReadOnlyList<ProcessResidentBreakdown>> CaptureAllAsync(IReadOnlyList<ProcessSnapshot> processes,
        IProgress<(int Done, int Total)>? progress, CancellationToken cancellationToken) => Task.Run<IReadOnlyList<ProcessResidentBreakdown>>(() =>
    {
        var clock = Stopwatch.StartNew();
        var rows = new List<ProcessResidentBreakdown>();
        var count = Math.Min(4096, processes.Count);
        for (var i = 0; i < count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var process = processes[i];
            rows.Add(Capture(process.Identity, process.Name, cancellationToken, () => clock.Elapsed >= TotalBudget));
            progress?.Report((i + 1, processes.Count));
        }
        if (count < processes.Count) rows.Add(Failure(default, "Remaining processes", RamReadingStatus.Truncated,
            $"Process cap 4096 reached; {processes.Count - count} identities omitted.", TimeSpan.Zero));
        return rows;
    }, cancellationToken);

    private static ProcessResidentBreakdown Failure(ProcessIdentity identity, string name, RamReadingStatus status, string detail, TimeSpan duration) =>
        new(identity, name, status, detail, 0, 0, 0, 0, 0, 0, status == RamReadingStatus.Truncated, null, null, duration);

    private static ProcessResidentBreakdown Capture(ProcessIdentity identity, string name, CancellationToken token, Func<bool> totalExpired)
    {
        var clock = Stopwatch.StartNew();
        token.ThrowIfCancellationRequested();
        ProcessResidentBreakdown Fail(RamReadingStatus status, string detail) => Failure(identity, name, status, detail, clock.Elapsed);
        bool Expired() => clock.Elapsed >= ProcessBudget || totalExpired();
        if (!OperatingSystem.IsWindows() || IntPtr.Size != 8) return Fail(RamReadingStatus.Unavailable, "Native x64 Windows required.");
        if (Expired()) return Fail(RamReadingStatus.TimedOut, "Total cooperative scan budget reached; process not queried.");
        if (!identity.IsStable) return Fail(RamReadingStatus.IdentityMismatch, "Creation time is unavailable; PID-only attribution is unsafe.");
        using var limited = RamNative.OpenProcess(0x1000, false, identity.Pid);
        if (limited.IsInvalid) return Fail(RamReadingStatus.Denied, $"OpenProcess limited query denied (Win32 {Marshal.GetLastWin32Error()}).");
        if (!RamNative.Matches(limited, identity)) return Fail(RamReadingStatus.IdentityMismatch, "Process creation time could not be verified or changed.");
        ulong? privateCommit = null, sharedCommit = null;
        var vm = new byte[112];
        var vmStatus = RamNative.NtQueryInformationProcess(limited, 3, vm, vm.Length, out var vmLength);
        if (vmStatus >= 0 && vmLength >= 112)
        {
            privateCommit = BitConverter.ToUInt64(vm, 88); sharedCommit = BitConverter.ToUInt64(vm, 104);
        }
        var commitDetail = privateCommit.HasValue ? " VM_COUNTERS_EX2 commit observed." : $" VM_COUNTERS_EX2 commit unavailable (NTSTATUS 0x{vmStatus:X8}, length {vmLength}).";
        using var handle = RamNative.OpenProcess(0x0400, false, identity.Pid);
        if (handle.IsInvalid) return Fail(RamReadingStatus.Denied, $"OpenProcess query information denied (Win32 {Marshal.GetLastWin32Error()}); no VM_READ requested.")
            with { PrivateCommitBytes = privateCommit, SharedCommitBytes = sharedCommit };
        if (!RamNative.Matches(handle, identity)) return Fail(RamReadingStatus.IdentityMismatch, "Creation time mismatch on working-set handle.");
        var capacity = 16384;
        for (var attempt = 0; attempt < 12; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (Expired()) return Fail(RamReadingStatus.TimedOut, "Cooperative process/total budget reached; native calls cannot be interrupted.");
            var bytes = checked(8 + capacity * 8);
            var buffer = ArrayPool<byte>.Shared.Rent(bytes);
            try
            {
                Array.Clear(buffer, 0, bytes);
                var pinned = GCHandle.Alloc(buffer, GCHandleType.Pinned);
                int status; nuint returned;
                try { status = RamNative.NtQueryVirtualMemory(handle, 0, 1, pinned.AddrOfPinnedObject(), (nuint)bytes, out returned); }
                finally { pinned.Free(); }
                token.ThrowIfCancellationRequested();
                if (Expired()) return Fail(RamReadingStatus.TimedOut, "Working-set native query exceeded cooperative budget.");
                if (status is unchecked((int)0xC0000004) or unchecked((int)0xC0000023))
                {
                    if (capacity == MaximumEntries) return Fail(RamReadingStatus.Truncated, "Working set exceeds 4M-entry / 32 MiB cap. No complete native prefix available; zero scanned is a lower bound.");
                    capacity = (int)Math.Min(MaximumEntries, Math.Max((long)capacity * 2, returned > 8 && returned <= int.MaxValue ? ((long)returned - 8 + 7) / 8 : 0));
                    continue;
                }
                if (status < 0) return Fail(status == unchecked((int)0xC0000022) ? RamReadingStatus.Denied : RamReadingStatus.Unavailable,
                    $"MemoryWorkingSetInformation failed (NTSTATUS 0x{status:X8}).");
                var count = BitConverter.ToUInt64(buffer, 0);
                if (count > (ulong)capacity || returned > (nuint)bytes || returned < 8 + count * 8)
                    return Fail(RamReadingStatus.Unavailable, "Invalid native working-set length; no values trusted.");
                if (!RamNative.Matches(handle, identity)) return Fail(RamReadingStatus.IdentityMismatch, "Creation time verification failed after scan.");
                var entries = MemoryMarshal.Cast<byte, ulong>(buffer.AsSpan(8, checked((int)count * 8)));
                return RamAttribution.AggregateWorkingSet(identity, name, entries, (uint)Environment.SystemPageSize, false, token)
                    with { PrivateCommitBytes = privateCommit, SharedCommitBytes = sharedCommit, Duration = clock.Elapsed,
                        Detail = RamAttribution.ProportionalDetail + commitDetail };
            }
            finally { ArrayPool<byte>.Shared.Return(buffer, true); }
        }
        return Fail(RamReadingStatus.Truncated, "Working-set growth attempt cap reached; no complete prefix available, lower bound zero.");
    }
}

internal static class RamNative
{
    internal static bool Matches(SafeProcessHandle handle, ProcessIdentity identity) =>
        GetProcessTimes(handle, out var created, out _, out _, out _) && created == identity.CreationTimeFileTime;

    [DllImport("kernel32.dll", SetLastError = true)] internal static extern SafeProcessHandle OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(SafeProcessHandle handle, out long created, out long exited, out long kernel, out long user);
    [DllImport("ntdll.dll")] internal static extern int NtQueryVirtualMemory(SafeProcessHandle handle, nuint address, int informationClass, IntPtr buffer, nuint length, out nuint returned);
    [DllImport("ntdll.dll")] internal static extern int NtQueryInformationProcess(SafeProcessHandle handle, int informationClass, byte[] buffer, int length, out int returned);
    [DllImport("ntdll.dll")] internal static extern int NtQuerySystemInformation(int informationClass, byte[] buffer, int length, out int returned);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GetPhysicallyInstalledSystemMemory(out ulong kilobytes);
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [StructLayout(LayoutKind.Sequential)] internal struct MemoryStatus
    {
        internal uint Length, Load;
        internal ulong TotalPhys, AvailablePhys, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
}
