using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using SystemExplorer.Core;
using static SystemExplorer.Windows.NativeMethods;

namespace SystemExplorer.Windows;

public sealed class WindowsProcessInspectionProvider : IProcessInspectionProvider
{
    public const int MaximumRecords = 4096;
    private readonly int maximumRecords;
    private readonly TimeSpan budget;
    public WindowsProcessInspectionProvider() : this(MaximumRecords, TimeSpan.FromSeconds(3)) { }
    internal WindowsProcessInspectionProvider(int maximumRecords, TimeSpan budget)
    {
        if (maximumRecords is < 1 or > MaximumRecords || budget < TimeSpan.Zero || budget > TimeSpan.FromSeconds(3))
            throw new ArgumentOutOfRangeException(nameof(maximumRecords));
        this.maximumRecords = maximumRecords;
        this.budget = budget;
    }

    public Task<ProcessInspection> CaptureAsync(ProcessIdentity identity, ProcessInspectionKind kind, CancellationToken cancellationToken) =>
        Task.Run(() => Capture(identity, kind, cancellationToken), cancellationToken);

    private ProcessInspection Capture(ProcessIdentity identity, ProcessInspectionKind kind, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var captured = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var modules = new List<ModuleObservation>();
        var regions = new List<MemoryRegionObservation>();
        ProcessInspection Result(InspectionStatus status, string detail, bool discard = false) =>
            new(identity, kind, captured, status, detail, discard ? [] : modules.ToArray(), discard ? [] : regions.ToArray());
        if (!OperatingSystem.IsWindows() || RuntimeInformation.ProcessArchitecture != Architecture.X64)
            return Result(InspectionStatus.Unsupported, "This adapter requires an x64 Windows host process.");
        if (!identity.IsStable || identity.Pid <= 0)
            return Result(InspectionStatus.Unsupported, "A positive PID and observed creation FILETIME are required. PID alone is never inspected.");
        if (!Enum.IsDefined(kind)) return Result(InspectionStatus.Unsupported, "Unknown inspection kind.");
        using var process = OpenProcess(kind == ProcessInspectionKind.MemoryRegions ? 0x0400u : QueryLimited, false, (uint)identity.Pid);
        if (process.IsInvalid)
        {
            var error = Marshal.GetLastWin32Error();
            return Result(ErrorStatus(error), $"Cannot query this process (Windows error {error}); no elevation or driver fallback.");
        }
        if (Validate(process, identity) is { } invalid) return Result(invalid.Status, invalid.Detail, true);
        if (clock.Elapsed >= budget) return Result(InspectionStatus.TimedOut, "Cooperative observation budget expired before enumeration.");
        var outcome = kind == ProcessInspectionKind.Modules
            ? ReadModules((uint)identity.Pid, modules, clock, token)
            : ReadRegions(process, regions, clock, token);
        token.ThrowIfCancellationRequested();
        if (Validate(process, identity) is { } changed) return Result(changed.Status, changed.Detail, true);
        if (clock.Elapsed >= budget && outcome.Status == InspectionStatus.Available)
            outcome = (InspectionStatus.TimedOut, "Observation exceeded its cooperative time budget; completeness is not asserted.");
        return Result(outcome.Status, outcome.Detail, outcome.Status is InspectionStatus.Failed or InspectionStatus.AccessDenied or InspectionStatus.Unsupported);
    }

    private static (InspectionStatus Status, string Detail)? Validate(SafeProcessHandle process, ProcessIdentity identity)
    {
        if (!GetProcessTimes(process, out var created, out var exited, out _, out _))
        {
            var error = Marshal.GetLastWin32Error();
            return (ErrorStatus(error), $"Lifetime validation unavailable (Windows error {error}); results discarded.");
        }
        if (created.Ticks != (ulong)identity.CreationTimeFileTime!.Value)
            return (InspectionStatus.IdentityMismatch, "The PID belongs to a different creation-time identity; results discarded.");
        if (exited.Ticks != 0) return (InspectionStatus.Exited, "This process lifetime exited; results discarded.");
        return null;
    }

    private (InspectionStatus Status, string Detail) ReadModules(uint pid, List<ModuleObservation> rows, Stopwatch clock, CancellationToken token)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed >= budget) return (InspectionStatus.TimedOut, "Module observation reached its cooperative time budget.");
            using var snapshot = CreateToolhelp32Snapshot(0x08 | 0x10, pid);
            if (snapshot.IsInvalid)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 24 && attempt < 2) continue;
                return (ErrorStatus(error), $"Module snapshot unavailable (Windows error {error}); loader changes may prevent a consistent snapshot.");
            }
            var entry = new ModuleEntry { Size = (uint)Marshal.SizeOf<ModuleEntry>() };
            var more = Module32FirstW(snapshot, ref entry);
            var lastError = more ? 0 : Marshal.GetLastWin32Error();
            while (more)
            {
                token.ThrowIfCancellationRequested();
                if (clock.Elapsed >= budget) return (InspectionStatus.TimedOut, "Partial module inventory: cooperative time budget reached.");
                if (rows.Count >= maximumRecords) return (InspectionStatus.Truncated, $"Module inventory truncated at {maximumRecords} records.");
                if (entry.Pid != pid) return (InspectionStatus.Failed, "Snapshot returned an inconsistent module owner.");
                rows.Add(new(entry.Name, entry.Path, entry.BaseAddress.ToUInt64(), entry.BaseSize));
                entry.Size = (uint)Marshal.SizeOf<ModuleEntry>();
                more = Module32NextW(snapshot, ref entry);
                lastError = more ? 0 : Marshal.GetLastWin32Error();
            }
            return lastError == 18
                ? (InspectionStatus.Available, "Toolhelp snapshot observed. Loader changes and data-file-only mappings may be omitted; paths use the Windows MAX_PATH snapshot field.")
                : (InspectionStatus.Partial, $"Module enumeration stopped (Windows error {lastError}); retained records are incomplete.");
        }
        return (InspectionStatus.Failed, "Module snapshot could not stabilize within three attempts.");
    }

    private (InspectionStatus Status, string Detail) ReadRegions(SafeProcessHandle process, List<MemoryRegionObservation> rows, Stopwatch clock, CancellationToken token)
    {
        GetNativeSystemInfo(out var system);
        var maximum = system.MaximumAddress.ToUInt64();
        ulong address = 0;
        while (address <= maximum)
        {
            token.ThrowIfCancellationRequested();
            if (clock.Elapsed >= budget) return (InspectionStatus.TimedOut, "Partial region inventory: cooperative time budget reached.");
            if (rows.Count >= maximumRecords) return (InspectionStatus.Truncated, $"Region inventory truncated at {maximumRecords} records.");
            var size = (UIntPtr)(uint)Marshal.SizeOf<MemoryInformation>();
            var returned = VirtualQueryEx(process, (UIntPtr)address, out var info, size);
            if (returned == UIntPtr.Zero)
            {
                var error = Marshal.GetLastWin32Error();
                if (error == 87 && address > 0) break; // End of the target's accessible address space, including WOW64.
                return (rows.Count > 0 ? InspectionStatus.Partial : ErrorStatus(error), $"Region query stopped at 0x{address:X} (Windows error {error}).");
            }
            var start = info.BaseAddress.ToUInt64();
            var length = info.RegionSize.ToUInt64();
            if (returned != size || length == 0 || start > address || length > ulong.MaxValue - start || start + length <= address)
                return (InspectionStatus.Partial, "Region query returned an invalid layout or non-progressing range; enumeration stopped.");
            rows.Add(new(start, length, info.State == 0x10000 ? null : info.AllocationBase.ToUInt64(), info.State,
                info.State == 0x1000 ? info.Protect : null, info.State == 0x10000 ? null : info.Type));
            address = start + length;
        }
        return (InspectionStatus.Available, "Virtual address ranges observed, not a resident-memory census. Mappings can change during collection; no memory contents were read.");
    }

    private static InspectionStatus ErrorStatus(int error) => error switch
    {
        5 => InspectionStatus.AccessDenied,
        87 or 1168 => InspectionStatus.Exited,
        299 or 50 => InspectionStatus.Unsupported,
        _ => InspectionStatus.Failed
    };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct ModuleEntry
    {
        internal uint Size, ModuleId, Pid, GlobalUsage, ProcessUsage;
        internal UIntPtr BaseAddress;
        internal uint BaseSize;
        internal IntPtr Module;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] internal string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Path;
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryInformation
    {
        internal UIntPtr BaseAddress, AllocationBase;
        internal uint AllocationProtect;
        internal ushort PartitionId;
        internal UIntPtr RegionSize;
        internal uint State, Protect, Type;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct SystemInformation
    {
        internal uint Architecture, PageSize;
        internal UIntPtr MinimumAddress, MaximumAddress, ActiveProcessorMask;
        internal uint ProcessorCount, ProcessorType, AllocationGranularity;
        internal ushort ProcessorLevel, ProcessorRevision;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Module32FirstW(SafeFileHandle snapshot, ref ModuleEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Module32NextW(SafeFileHandle snapshot, ref ModuleEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern UIntPtr VirtualQueryEx(SafeProcessHandle process, UIntPtr address, out MemoryInformation info, UIntPtr length);
    [DllImport("kernel32.dll")]
    private static extern void GetNativeSystemInfo(out SystemInformation info);
}
