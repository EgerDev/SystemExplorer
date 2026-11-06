using System.ComponentModel;
using System.Runtime.InteropServices;
using SystemExplorer.Core;

namespace SystemExplorer.Relationships;

public sealed class WindowsRelationshipProvider : IRelationshipProvider
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly ServiceConfigReader serviceConfigReader = new();
    private int disposed;
    private const uint MaximumTableBytes = 16 * 1024 * 1024;

    public Task<RelationshipSnapshot> CaptureAsync(IReadOnlyList<ProcessSnapshot> processes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(processes);
        var copy = processes.ToArray();
        return RunAsync(() => Capture(copy, cancellationToken), cancellationToken);
    }

    public Task<PublisherInfo> GetPublisherAsync(string executablePath, CancellationToken cancellationToken) =>
        RunAsync(() => LocalInventory.GetPublisher(executablePath, cancellationToken), cancellationToken);

    public Task<IReadOnlyList<StartupItem>> GetStartupItemsAsync(CancellationToken cancellationToken) =>
        RunAsync(() => LocalInventory.GetStartupItems(cancellationToken), cancellationToken);

    public void Dispose() => Interlocked.Exchange(ref disposed, 1);

    private async Task<T> RunAsync<T>(Func<T> operation, CancellationToken cancellationToken)
    {
        Check(cancellationToken);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Check(cancellationToken);
            var result = await Task.Run(operation, cancellationToken).ConfigureAwait(false);
            Check(cancellationToken);
            return result;
        }
        finally { gate.Release(); }
    }

    private void Check(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows relationship APIs are required.");
    }

    public static readonly IReadOnlyList<string> BaselineLimitations =
    [
        "Endpoint and service observations are not an atomic snapshot; CapturedAt marks capture start.",
        "PID-only owners are unverified unless matched to a live handle creation time from the supplied process snapshot.",
        "Services inaccessible to this token can be silently omitted by Windows; configured base start types do not include delayed/trigger modes or runtime behavior.",
        "UDP rows are local endpoints, not connections. DNS, geography and bandwidth are not collected."
    ];

    private RelationshipSnapshot Capture(ProcessSnapshot[] processes, CancellationToken token)
    {
        Check(token);
        var capturedAt = DateTimeOffset.UtcNow;
        var limitations = new List<string>(BaselineLimitations);
        var endpoints = new List<ConnectionSnapshot>();
        foreach (bool tcp in new[] { true, false })
        foreach (bool ipv6 in new[] { false, true })
        {
            Check(token);
            string source = $"{(tcp ? "TCP" : "UDP")} IPv{(ipv6 ? 6 : 4)}";
            try { endpoints.AddRange(ReadEndpoints(tcp, ipv6, token)); }
            catch (Win32Exception ex) { limitations.Add($"{source}: {ErrorKind(ex.NativeErrorCode)} (Win32 {ex.NativeErrorCode})."); }
            catch (InvalidDataException ex) { limitations.Add($"{source}: {ex.Message}"); }
        }

        var services = ReadServices(limitations, token);
        var candidates = processes.Where(p => p.Identity.IsStable && p.Identity.Pid > 0)
            .GroupBy(p => p.Identity.Pid)
            .Where(g => g.Select(p => p.Identity).Distinct().Count() == 1)
            .ToDictionary(g => g.Key, g => g.First().Identity);
        var owners = new Dictionary<int, ProcessIdentity>();
        int denied = 0, changed = 0;
        foreach (int pid in endpoints.Select(e => e.OwningPid).Concat(services.Select(s => s.ProcessId ?? 0)).Distinct())
        {
            Check(token);
            if (!candidates.TryGetValue(pid, out var expected)) continue;
            using var handle = NativeMethods.OpenProcess(0x1000 | 0x00100000, false, pid);
            if (handle.IsInvalid || !NativeMethods.GetProcessTimes(handle, out long created, out _, out _, out _))
            {
                denied++;
                continue;
            }
            if (WaitForSingleObject(handle, 0) == 258 && IdentityMatches(expected, created, capturedAt.UtcDateTime.ToFileTimeUtc())) owners.Add(pid, expected);
            else changed++;
        }
        if (denied > 0) limitations.Add($"{denied} candidate owner identities unavailable (process exited or query denied/failed).");
        if (changed > 0) limitations.Add($"{changed} candidate owners exited, changed identity, or started after capture began; not joined.");
        Check(token);
        return new RelationshipSnapshot
        {
            CapturedAt = capturedAt,
            Connections = endpoints.Select(e => e with { OwnerIdentity = owners.TryGetValue(e.OwningPid, out var id) ? id : null }).ToArray(),
            Services = services.Select(s => s with { OwnerIdentity = s.ProcessId is { } pid && owners.TryGetValue(pid, out var id) ? id : null }).ToArray(),
            Limitations = limitations.ToArray()
        };
    }

    internal static bool IdentityMatches(ProcessIdentity expected, long actualCreation, long captureStarted) =>
        expected.IsStable && expected.CreationTimeFileTime == actualCreation && actualCreation <= captureStarted;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(Microsoft.Win32.SafeHandles.SafeProcessHandle process, uint milliseconds);

    private IReadOnlyList<ConnectionSnapshot> ReadEndpoints(bool tcp, bool ipv6, CancellationToken token)
    {
        uint capacity = 0;
        for (int attempt = 0; attempt < 4; attempt++)
        {
            Check(token);
            if (capacity > MaximumTableBytes) throw new InvalidDataException("Endpoint table exceeds the 16 MiB bound.");
            IntPtr buffer = capacity == 0 ? IntPtr.Zero : Marshal.AllocHGlobal((int)capacity);
            try
            {
                uint size = capacity;
                uint error = tcp ? NativeMethods.GetExtendedTcpTable(buffer, ref size, false, ipv6 ? 23u : 2u, 5, 0)
                    : NativeMethods.GetExtendedUdpTable(buffer, ref size, false, ipv6 ? 23u : 2u, 1, 0);
                Check(token);
                if (error == 122)
                {
                    if (size <= capacity) throw new InvalidDataException("Endpoint buffer retry made no progress.");
                    capacity = size;
                    continue;
                }
                if (error != 0) throw new Win32Exception((int)error);
                if (size < 4 || size > capacity) throw new InvalidDataException("Endpoint buffer length is invalid.");
                byte[] bytes = new byte[size];
                Marshal.Copy(buffer, bytes, 0, bytes.Length);
                return EndpointTable.Parse(bytes, tcp, ipv6, token);
            }
            finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
        }
        throw new InvalidDataException("Endpoint table changed during four bounded attempts.");
    }

    private List<ServiceSnapshot> ReadServices(List<string> limitations, CancellationToken token)
    {
        var result = new List<ServiceSnapshot>();
        Check(token);
        using var manager = NativeMethods.OpenSCManagerW(null, null, 0x5);
        Check(token);
        if (manager.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            limitations.Add($"Services: {ErrorKind(error)} (Win32 {error}).");
            return result;
        }
        result = EnumerateServices(manager, limitations, token);
        serviceConfigReader.Enrich(manager, result, limitations, token);
        return result;
    }

    private List<ServiceSnapshot> EnumerateServices(
        SafeServiceHandle manager,
        List<string> limitations,
        CancellationToken token)
    {
        var result = new List<ServiceSnapshot>();
        const int capacity = 256 * 1024;
        IntPtr buffer = Marshal.AllocHGlobal(capacity);
        try
        {
            uint resume = 0;
            int stride = Marshal.SizeOf<NativeMethods.ServiceEntry>();
            for (int page = 0; page < 16; page++)
            {
                Check(token);
                uint oldResume = resume;
                bool success = NativeMethods.EnumServicesStatusExW(manager, 0, 0x30, 3, buffer, capacity,
                    out _, out uint count, ref resume, null);
                int error = success ? 0 : Marshal.GetLastWin32Error();
                Check(token);
                if (!success && error != 234)
                {
                    limitations.Add($"Services: {ErrorKind(error)} (Win32 {error}); observations may be partial.");
                    return result;
                }
                if (count > capacity / stride) throw new InvalidDataException("Service count exceeds its buffer.");
                for (int i = 0; i < count; i++)
                {
                    Check(token);
                    var row = Marshal.PtrToStructure<NativeMethods.ServiceEntry>(IntPtr.Add(buffer, i * stride));
                    // Windows defines PIDs only for running/paused and their stop/pause/continue pending states.
                    int? pid = row.State is 3 or 4 or 5 or 6 or 7 && row.Pid is > 0 and <= int.MaxValue ? (int)row.Pid : null;
                    result.Add(new ServiceSnapshot
                    {
                        Name = ReadServiceString(buffer, capacity, row.Name),
                        DisplayName = ReadServiceString(buffer, capacity, row.DisplayName),
                        State = ServiceState(row.State),
                        ProcessId = pid
                    });
                }
                if (success) return result;
                if (count == 0 || resume == oldResume)
                {
                    limitations.Add("Services: enumeration made no progress; observations are partial.");
                    return result;
                }
            }
            limitations.Add("Services: sixteen-page limit reached; observations are partial.");
            return result;
        }
        catch (InvalidDataException ex)
        {
            limitations.Add($"Services: {ex.Message} Observations are partial.");
            return result;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static string ReadServiceString(IntPtr buffer, int capacity, IntPtr pointer)
    {
        long offset = pointer.ToInt64() - buffer.ToInt64();
        if (offset < 0 || offset >= capacity || offset % 2 != 0) throw new InvalidDataException("Service string pointer is outside the buffer.");
        int maximum = Math.Min(257, (capacity - (int)offset) / 2);
        for (int i = 0; i < maximum; i++)
            if (Marshal.ReadInt16(pointer, i * 2) == 0) return Marshal.PtrToStringUni(pointer, i)!;
        throw new InvalidDataException("Service string is not terminated within its bound.");
    }

    private static string ErrorKind(int error) => error switch
    {
        5 => "access denied", 50 or 120 => "unsupported", _ => "enumeration failed"
    };

    private static string ServiceState(uint state) => state switch
    {
        1 => "Stopped", 2 => "StartPending", 3 => "StopPending", 4 => "Running",
        5 => "ContinuePending", 6 => "PausePending", 7 => "Paused", _ => $"Unknown ({state})"
    };
}
