using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SystemExplorer.Core;
using static SystemExplorer.Windows.NativeMethods;

namespace SystemExplorer.Windows;

public sealed class WindowsMonitorProvider : IMonitorProvider
{
    private sealed record Sample(ulong Cpu, long CpuTimestamp, IoCounters? Io, long IoTimestamp, int Processors);
    private sealed record SystemSample(ulong Idle, ulong Kernel, ulong User, int Processors);
    private readonly SemaphoreSlim scanGate = new(1, 1);
    private readonly object lifetimeLock = new();
    private readonly CancellationTokenSource shutdown = new();
    private bool disposed;
    private Dictionary<ProcessIdentity, Sample> previous = new();
    private SystemSample? previousSystem;
    private PdhQueryHandle? diskQuery;
    private IntPtr diskReadCounter, diskWriteCounter;
    private bool diskPrimed;
    private long diskTimestamp;

    public WindowsMonitorProvider()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows monitoring requires Windows.");
    }

    public async Task<MonitorSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource linked;
        lock (lifetimeLock)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, shutdown.Token);
        }
        using (linked)
        {
            await scanGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                return await Task.Run(() => Capture(linked.Token), linked.Token).ConfigureAwait(false);
            }
            finally { scanGate.Release(); }
        }
    }

    public void Dispose()
    {
        lock (lifetimeLock)
        {
            if (disposed) return;
            disposed = true;
            shutdown.Cancel();
        }
        scanGate.Wait();
        try
        {
            previous.Clear();
            previousSystem = null;
            diskQuery?.Dispose();
            diskQuery = null;
            shutdown.Dispose();
        }
        finally { scanGate.Release(); }
        // Keep the managed semaphore alive for already-cancelled WaitAsync continuations.
    }

    private MonitorSnapshot Capture(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var capturedAt = DateTimeOffset.UtcNow;
        long enumerationStart = capturedAt.UtcDateTime.ToFileTimeUtc();
        var limitations = new List<string>
        {
            "Enumeration and per-handle reads are not atomic; relationships require separate lifetime validation.",
            "CPU and process I/O rates require two successful same-lifetime samples; first samples are unavailable.",
            "Process I/O is generic transfer activity, not physical disk or network throughput.",
            "Private working set requires Windows extended memory counters; unsupported or denied values remain unavailable.",
            "Standby memory, GPU, command lines and suspension state are unsupported.",
            "Working sets include shared pages; their sum is not distinct physical RAM. Private bytes are committed, not necessarily resident."
        };
        int processors = checked((int)GetActiveProcessorCount(ushort.MaxValue));
        if (processors == 0) limitations.Add("Logical processor count unavailable; process CPU is unavailable.");
        var next = new Dictionary<ProcessIdentity, Sample>();
        var rows = new List<ProcessSnapshot>();
        using (var snapshot = CreateToolhelp32Snapshot(2, 0))
        {
            if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Process enumeration failed.");
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Process32FirstW(snapshot, ref entry))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Process enumeration failed.");
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var row = ReadProcess(entry, enumerationStart, processors, next, cancellationToken);
                if (row is not null) rows.Add(row);
            } while (Process32NextW(snapshot, ref entry));
            int error = Marshal.GetLastWin32Error();
            if (error != 18) throw new Win32Exception(error, "Process enumeration was incomplete.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var memory = ReadMemory(limitations);
        var (systemCpu, systemSample) = ReadSystemCpu(processors, limitations);
        var (diskRead, diskWrite) = ReadDisk(limitations);
        cancellationToken.ThrowIfCancellationRequested();
        previous = next;
        previousSystem = systemSample;
        return new MonitorSnapshot
        {
            CapturedAt = capturedAt, Processes = rows.AsReadOnly(), Memory = memory,
            TotalCpuPercent = systemCpu, DiskReadBytesPerSecond = diskRead, DiskWriteBytesPerSecond = diskWrite,
            Limitations = limitations.AsReadOnly()
        };
    }

    private ProcessSnapshot? ReadProcess(ProcessEntry entry, long enumerationStart, int processors,
        Dictionary<ProcessIdentity, Sample> next, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        bool denied = false;
        void Error(string field, int error)
        {
            denied |= error == 5;
            errors.Add($"{field}: Win32 {error}");
        }
        var row = new ProcessSnapshot
        {
            Identity = new ProcessIdentity(checked((int)entry.Pid), null),
            Name = entry.Name ?? "Unknown", ParentPid = checked((int)entry.ParentPid),
            ThreadCount = checked((int)entry.Threads)
        };
        using var process = OpenProcess(QueryLimited, false, entry.Pid);
        if (process.IsInvalid)
        {
            Error("Identity, path, owner, memory and counters unavailable (OpenProcess)", Marshal.GetLastWin32Error());
            return row with { Availability = denied ? DataAvailability.AccessDenied : DataAvailability.Error,
                UnavailableReason = string.Join("; ", errors) };
        }
        long beforeCpu = Stopwatch.GetTimestamp();
        if (!GetProcessTimes(process, out var creation, out _, out var kernel, out var user))
        {
            Error("Identity unavailable (GetProcessTimes); handle metadata not joined", Marshal.GetLastWin32Error());
            return row with { Availability = denied ? DataAvailability.AccessDenied : DataAvailability.Error,
                UnavailableReason = string.Join("; ", errors) };
        }
        long cpuTime = beforeCpu + (Stopwatch.GetTimestamp() - beforeCpu) / 2;
        if (creation.Ticks == 0 || creation.Ticks > long.MaxValue)
            return row with { Availability = DataAvailability.Error, UnavailableReason = "Invalid process creation time; handle metadata not joined." };
        if (creation.Ticks > (ulong)enumerationStart) return null;
        var identity = new ProcessIdentity(row.Identity.Pid, (long)creation.Ticks);
        ulong cpuTicks = checked(kernel.Ticks + user.Ticks);
        cancellationToken.ThrowIfCancellationRequested();

        var path = new StringBuilder(32768);
        uint pathLength = (uint)path.Capacity;
        string? executable = null;
        if (QueryFullProcessImageNameW(process, 0, path, ref pathLength)) executable = path.ToString();
        else Error("Executable path", Marshal.GetLastWin32Error());

        bool memoryAvailable = ReadProcessMemory(process, out var memory, out var privateWorkingSet,
            out var privateWorkingSetReason, out int memoryError);
        if (!memoryAvailable)
        {
            if (memoryError == 5)
            {
                using var readable = OpenProcess(QueryInformationAndRead, false, entry.Pid);
                if (readable.IsInvalid) memoryError = Marshal.GetLastWin32Error();
                else if (!GetProcessTimes(readable, out var memoryCreation, out _, out _, out _))
                    memoryError = Marshal.GetLastWin32Error();
                else if (memoryCreation.Ticks != creation.Ticks)
                {
                    errors.Add("Private bytes and working set unavailable: PID lifetime changed while opening memory handle.");
                    memoryError = 0;
                }
                else
                {
                    memoryAvailable = ReadProcessMemory(readable, out memory, out privateWorkingSet,
                        out privateWorkingSetReason, out memoryError);
                }
            }
            if (!memoryAvailable && memoryError != 0) Error("Private bytes and working set", memoryError);
        }
        if (memoryAvailable && privateWorkingSetReason is not null) errors.Add(privateWorkingSetReason);

        cancellationToken.ThrowIfCancellationRequested();
        var (sid, account, session) = ReadOwner(process, Error, cancellationToken);
        int? handles = null;
        if (GetProcessHandleCount(process, out uint handleCount)) handles = checked((int)handleCount);
        else Error("Handle count", Marshal.GetLastWin32Error());
        long beforeIo = Stopwatch.GetTimestamp();
        IoCounters? io = null;
        if (GetProcessIoCounters(process, out var counters)) io = counters;
        else Error("Process I/O", Marshal.GetLastWin32Error());
        long ioTime = beforeIo + (Stopwatch.GetTimestamp() - beforeIo) / 2;
        double? cpu = null, read = null, write = null;
        if (previous.TryGetValue(identity, out var old))
        {
            if (old.Processors == processors)
                cpu = Sampling.CpuPercent(identity, identity, old.Cpu, cpuTicks,
                    (cpuTime - old.CpuTimestamp) / (double)Stopwatch.Frequency, processors);
            if (old.Io is { } oldIo && io is { } currentIo)
            {
                double elapsed = (ioTime - old.IoTimestamp) / (double)Stopwatch.Frequency;
                read = Sampling.Rate(oldIo.ReadBytes, currentIo.ReadBytes, elapsed);
                write = Sampling.Rate(oldIo.WriteBytes, currentIo.WriteBytes, elapsed);
            }
        }
        next[identity] = new Sample(cpuTicks, cpuTime, io, ioTime, processors);
        return row with
        {
            Identity = identity, ExecutablePath = executable, UserSid = sid, UserName = account,
            SessionId = session, HandleCount = handles,
            WorkingSetBytes = memoryAvailable ? memory.WorkingSet.ToUInt64() : null,
            PrivateBytes = memoryAvailable ? memory.PrivateUsage.ToUInt64() : null,
            PrivateWorkingSetBytes = memoryAvailable ? privateWorkingSet : null,
            CpuPercent = cpu, ReadBytesPerSecond = read, WriteBytesPerSecond = write,
            Availability = denied ? DataAvailability.AccessDenied : errors.Count > 0 ? DataAvailability.Partial : DataAvailability.Available,
            UnavailableReason = errors.Count > 0 ? string.Join("; ", errors) : null
        };
    }

    private static bool ReadProcessMemory(SafeProcessHandle process, out MemoryCounters memory,
        out ulong? privateWorkingSet, out string? privateWorkingSetReason, out int error)
    {
        var extended = new MemoryCountersEx2
        {
            Basic = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCountersEx2>() },
            PrivateWorkingSet = UIntPtr.MaxValue
        };
        bool extendedAvailable = GetProcessMemoryInfo(process, ref extended, extended.Basic.Size);
        int extendedError = extendedAvailable ? 0 : Marshal.GetLastWin32Error();
        privateWorkingSet = extendedAvailable ? extended.PrivateWorkingSetBytes : null;
        privateWorkingSetReason = privateWorkingSet.HasValue ? null : extendedAvailable
            ? "Private working set unavailable: Windows did not return extended memory counters."
            : $"Private working set unavailable: GetProcessMemoryInfo(EX2) Win32 {extendedError}.";
        if (extendedAvailable && extended.Basic.Size >= Marshal.SizeOf<MemoryCounters>())
        {
            memory = extended.Basic;
            error = 0;
            return true;
        }
        memory = new MemoryCounters { Size = (uint)Marshal.SizeOf<MemoryCounters>() };
        bool available = GetProcessMemoryInfo(process, ref memory, memory.Size);
        error = available ? 0 : Marshal.GetLastWin32Error();
        return available;
    }

    private static (string? Sid, string? Account, int? Session) ReadOwner(SafeProcessHandle process,
        Action<string, int> error, CancellationToken cancellationToken)
    {
        if (!OpenProcessToken(process, 8, out var token))
        {
            int code = Marshal.GetLastWin32Error();
            token.Dispose();
            error("Owner and session", code);
            return (null, null, null);
        }
        using (token)
        {
            int? session = null;
            IntPtr sessionBuffer = Marshal.AllocHGlobal(4);
            try
            {
                if (GetTokenInformation(token, 12, sessionBuffer, 4, out _)) session = Marshal.ReadInt32(sessionBuffer);
                else error("Session", Marshal.GetLastWin32Error());
            }
            finally { Marshal.FreeHGlobal(sessionBuffer); }
            GetTokenInformation(token, 1, IntPtr.Zero, 0, out uint required);
            int queryError = Marshal.GetLastWin32Error();
            if (queryError != 122 || required < IntPtr.Size || required > 65536)
            {
                error("Owner token buffer", queryError);
                return (null, null, session);
            }
            IntPtr buffer = Marshal.AllocHGlobal(checked((int)required));
            try
            {
                if (!GetTokenInformation(token, 1, buffer, required, out _))
                {
                    error("Owner", Marshal.GetLastWin32Error());
                    return (null, null, session);
                }
                IntPtr sidPointer = Marshal.ReadIntPtr(buffer);
                string? sid = null, account = null;
                if (ConvertSidToStringSidW(sidPointer, out var sidText))
                {
                    try { sid = Marshal.PtrToStringUni(sidText); }
                    finally { LocalFree(sidText); }
                }
                else error("Owner SID", Marshal.GetLastWin32Error());
                cancellationToken.ThrowIfCancellationRequested();
                uint nameLength = 0, domainLength = 0;
                LookupAccountSidW(null, sidPointer, null, ref nameLength, null, ref domainLength, out _);
                int lookupError = Marshal.GetLastWin32Error();
                if (lookupError == 122 && nameLength is > 0 and <= 32768 && domainLength <= 32768)
                {
                    var name = new StringBuilder((int)nameLength);
                    var domain = new StringBuilder(Math.Max(1, (int)domainLength));
                    if (LookupAccountSidW(null, sidPointer, name, ref nameLength, domain, ref domainLength, out _))
                        account = domain.Length == 0 ? name.ToString() : $"{domain}\\{name}";
                    else error("Owner account name", Marshal.GetLastWin32Error());
                }
                else error("Owner account name", lookupError);
                return (sid, account, session);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
    }

    private static MemorySnapshot ReadMemory(List<string> limitations)
    {
        var info = new PerformanceInfo { Size = (uint)Marshal.SizeOf<PerformanceInfo>() };
        if (!GetPerformanceInfo(ref info, info.Size))
        {
            limitations.Add($"System memory unavailable: GetPerformanceInfo Win32 {Marshal.GetLastWin32Error()}.");
            return new MemorySnapshot();
        }
        ulong page = info.PageSize.ToUInt64();
        if (page == 0) throw new InvalidOperationException("GetPerformanceInfo returned an invalid page size.");
        return new MemorySnapshot
        {
            PhysicalTotalBytes = checked(info.PhysicalTotal.ToUInt64() * page),
            PhysicalAvailableBytes = checked(info.PhysicalAvailable.ToUInt64() * page),
            CommitTotalBytes = checked(info.CommitTotal.ToUInt64() * page),
            CommitLimitBytes = checked(info.CommitLimit.ToUInt64() * page),
            SystemCacheBytes = checked(info.SystemCache.ToUInt64() * page),
            KernelPagedBytes = checked(info.KernelPaged.ToUInt64() * page),
            KernelNonpagedBytes = checked(info.KernelNonpaged.ToUInt64() * page)
        };
    }

    private (double? Cpu, SystemSample? Sample) ReadSystemCpu(int processors, List<string> limitations)
    {
        if (GetActiveProcessorGroupCount() != 1)
        {
            limitations.Add("Total CPU unavailable across multiple processor groups: GetSystemTimes only covers the caller's group.");
            return (null, null);
        }
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            limitations.Add($"Total CPU unavailable: GetSystemTimes Win32 {Marshal.GetLastWin32Error()}.");
            return (null, null);
        }
        var sample = new SystemSample(idle.Ticks, kernel.Ticks, user.Ticks, processors);
        double? cpu = null;
        if (previousSystem is { } old && processors > 0 && old.Processors == processors &&
            sample.Idle >= old.Idle && sample.Kernel >= old.Kernel && sample.User >= old.User)
        {
            ulong total = checked((sample.Kernel - old.Kernel) + (sample.User - old.User));
            ulong idleDelta = sample.Idle - old.Idle;
            if (total > 0 && idleDelta <= total) cpu = 100d * (total - idleDelta) / total;
        }
        return (cpu, sample);
    }

    private (double? Read, double? Write) ReadDisk(List<string> limitations)
    {
        if (diskQuery is null)
        {
            uint status = PdhOpenQueryW(null, UIntPtr.Zero, out var query);
            if (status == 0)
                status = PdhAddEnglishCounterW(query, @"\PhysicalDisk(_Total)\Disk Read Bytes/sec", UIntPtr.Zero, out diskReadCounter);
            if (status == 0)
                status = PdhAddEnglishCounterW(query, @"\PhysicalDisk(_Total)\Disk Write Bytes/sec", UIntPtr.Zero, out diskWriteCounter);
            if (status != 0)
            {
                query.Dispose();
                limitations.Add($"Physical disk rates unavailable: PDH query/counter setup 0x{status:X8}.");
                return (null, null);
            }
            diskQuery = query;
        }
        uint collected = PdhCollectQueryData(diskQuery);
        long now = Stopwatch.GetTimestamp();
        if (collected != 0)
        {
            diskPrimed = false;
            limitations.Add($"Physical disk rates unavailable: PDH collection 0x{collected:X8}.");
            return (null, null);
        }
        bool ready = diskPrimed && now > diskTimestamp;
        diskPrimed = true;
        diskTimestamp = now;
        if (!ready)
        {
            limitations.Add("Physical disk rates unavailable: PDH requires two successful samples with elapsed time.");
            return (null, null);
        }
        return (ReadDiskCounter(diskReadCounter, "read", limitations), ReadDiskCounter(diskWriteCounter, "write", limitations));
    }

    private static double? ReadDiskCounter(IntPtr counter, string direction, List<string> limitations)
    {
        uint status = PdhGetFormattedCounterValue(counter, 0x200, IntPtr.Zero, out var value);
        if (status != 0 || value.Status is not (0 or 1) || !double.IsFinite(value.Value) || value.Value < 0)
        {
            limitations.Add($"Physical disk {direction} rate unavailable: PDH format 0x{status:X8}, counter status 0x{value.Status:X8}, or invalid rate.");
            return null;
        }
        return value.Value;
    }
}
