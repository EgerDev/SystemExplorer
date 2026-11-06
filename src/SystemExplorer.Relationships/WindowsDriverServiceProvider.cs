using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SystemExplorer.Core;

[assembly: InternalsVisibleTo("SystemExplorer.Persistence.Smoke")]

namespace SystemExplorer.Relationships;

public sealed class WindowsDriverServiceProvider : IDriverServiceProvider
{
    internal const int MaximumRows = 2048, MaximumBytes = 256 * 1024, MaximumCalls = 16;
    private readonly IDriverServiceBackend backend;
    private readonly SemaphoreSlim gate = new(1, 1);
    public WindowsDriverServiceProvider() : this(new WindowsDriverServiceBackend()) { }
    internal WindowsDriverServiceProvider(IDriverServiceBackend backend) => this.backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public async Task<DriverServiceInventory> CaptureAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(() => Capture(cancellationToken), cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }

    private DriverServiceInventory Capture(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var captured = DateTimeOffset.UtcNow;
        long started = backend.GetTimestamp();
        var rows = new List<DriverServiceObservation>();
        bool Expired() { token.ThrowIfCancellationRequested(); return backend.GetElapsedTime(started) >= TimeSpan.FromSeconds(5); }
        DriverServiceInventory Result(InspectionStatus status, string detail) => new(captured, status,
            detail + " Local current-token driver service configuration only; paths are raw and unexpanded, not loaded-driver or ownership evidence. Five-second cooperative budget cannot interrupt synchronous calls.", rows.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Name, StringComparer.Ordinal).ToArray());
        if (!OperatingSystem.IsWindows()) return Result(InspectionStatus.Unsupported, "Windows SCM is required.");
        try
        {
            if (Expired()) return Result(InspectionStatus.TimedOut, "Budget expired.");
            using var manager = backend.OpenManager(out int error);
            if (Expired()) return Result(InspectionStatus.TimedOut, "Budget expired opening SCM.");
            if (manager is null) return Result(ErrorStatus(error), $"SCM unavailable (Windows error {error}).");
            bool truncated = false, completed = false, enumerationTimedOut = false;
            uint resume = 0;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IntPtr buffer = Marshal.AllocHGlobal(MaximumBytes);
            try
            {
                for (int call = 0; call < MaximumCalls; call++)
                {
                    if (Expired()) { enumerationTimedOut = true; break; }
                    Marshal.Copy(Enumerable.Repeat((byte)0xff, MaximumBytes).ToArray(), 0, buffer, MaximumBytes);
                    uint previous = resume;
                    error = manager.Enumerate(buffer, MaximumBytes, out _, out uint count, ref resume);
                    token.ThrowIfCancellationRequested();
                    if (error is not (0 or 234)) return Result(rows.Count > 0 ? InspectionStatus.Partial : ErrorStatus(error), $"Enumeration failed (Windows error {error}).");
                    var page = Decode(buffer, MaximumBytes, count, token);
                    truncated |= page.Status == InspectionStatus.Truncated;
                    foreach (var row in page.Drivers)
                    {
                        if (!seen.Add(row.Name)) continue;
                        if (rows.Count == MaximumRows) { truncated = true; break; }
                        rows.Add(row);
                    }
                    if (error == 0) { completed = true; break; }
                    if (rows.Count >= MaximumRows || resume == previous) { truncated = true; break; }
                }
            }
            finally { Marshal.FreeHGlobal(buffer); }
            truncated |= !completed;
            for (int i = 0; i < rows.Count; i++)
            {
                token.ThrowIfCancellationRequested();
                var row = rows[i];
                if (Expired()) { rows[i] = row with { ConfigStatus = InspectionStatus.TimedOut, ConfigDetail = "Cooperative budget expired; configuration not read." }; continue; }
                ServiceInspection config = ReadConfig(manager, row.Name, Expired, token);
                rows[i] = row with { StartType = config.StartType, ErrorControl = config.ErrorControl, ImagePath = config.BinaryPath,
                    LoadOrderGroup = config.LoadOrderGroup, ConfigStatus = config.Status, ConfigDetail = config.Detail };
            }
            return Result(enumerationTimedOut ? InspectionStatus.TimedOut : truncated ? InspectionStatus.Truncated : rows.Any(r => r.ConfigStatus != InspectionStatus.Available) ? InspectionStatus.Partial : InspectionStatus.Available,
                truncated ? "Driver enumeration reached a row, byte or paging bound." : "Observed bounded driver service inventory.");
        }
        catch (InvalidDataException) { return Result(rows.Count > 0 ? InspectionStatus.Partial : InspectionStatus.Failed, "Invalid native enumeration buffer; unread rows omitted."); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        { return Result(InspectionStatus.Unsupported, "Required SCM API unavailable."); }
    }

    private static ServiceInspection ReadConfig(IDriverServiceManager manager, string name, Func<bool> expired, CancellationToken token)
    {
        ServiceInspection Failure(InspectionStatus status, string detail) => new(name, DateTimeOffset.UtcNow, status, detail, null, null, null, null, null, null, []);
        using var service = manager.OpenConfig(name, out int error);
        if (expired()) return Failure(InspectionStatus.TimedOut, "Budget expired opening configuration.");
        if (service is null) return Failure(ErrorStatus(error), $"Driver configuration unavailable (Windows error {error}).");
        int capacity = 0;
        for (int call = 0; call < 3; call++)
        {
            if (expired()) return Failure(InspectionStatus.TimedOut, "Budget expired before configuration query.");
            IntPtr buffer = capacity == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(capacity);
            try
            {
                if (capacity > 0) Marshal.Copy(Enumerable.Repeat((byte)0xff, capacity).ToArray(), 0, buffer, capacity);
                error = service.Query(buffer, (uint)capacity, out uint needed);
                if (expired()) return Failure(InspectionStatus.TimedOut, "Budget expired during configuration query.");
                if (error == 122)
                {
                    if (needed > 8192) return Failure(InspectionStatus.Truncated, "Configuration exceeds 8192 bytes.");
                    if (needed <= capacity || needed < Marshal.SizeOf<ServiceInspectionConfig>()) return Failure(InspectionStatus.Failed, "Invalid configuration resize response.");
                    capacity = (int)needed;
                    continue;
                }
                if (error != 0) return Failure(ErrorStatus(error), $"Driver configuration unavailable (Windows error {error}).");
                try { return WindowsServiceInspectionProvider.Decode(name, DateTimeOffset.UtcNow, buffer, capacity, token); }
                catch (InvalidDataException) { return Failure(InspectionStatus.Failed, "Invalid bounded configuration buffer."); }
            }
            finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
        }
        return Failure(InspectionStatus.Truncated, "Configuration changed during three bounded calls.");
    }

    internal static DriverServiceInventory Decode(IntPtr buffer, int capacity, uint count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int stride = Marshal.SizeOf<DriverServiceEntry>();
        if (capacity < 0 || capacity > MaximumBytes || (count > 0 && (buffer == IntPtr.Zero || (long)count * stride > capacity)))
            throw new InvalidDataException("Driver enumeration array outside owned buffer.");
        string? Text(IntPtr pointer, bool required)
        {
            if (pointer == IntPtr.Zero && !required) return null;
            long offset = pointer.ToInt64() - buffer.ToInt64();
            if (pointer == IntPtr.Zero || offset < (long)count * stride || offset >= capacity || offset % 2 != 0) throw new InvalidDataException("Driver string outside payload.");
            for (int length = 0; offset + length * 2L + 2 <= capacity; length++)
            {
                token.ThrowIfCancellationRequested();
                if (Marshal.ReadInt16(pointer, length * 2) == 0)
                {
                    if (required && length == 0) throw new InvalidDataException("Missing driver name.");
                    return Marshal.PtrToStringUni(pointer, length);
                }
            }
            throw new InvalidDataException("Unterminated driver string.");
        }
        var rows = new List<DriverServiceObservation>();
        for (int i = 0; i < Math.Min(count, MaximumRows); i++)
        {
            token.ThrowIfCancellationRequested();
            var row = Marshal.PtrToStructure<DriverServiceEntry>(IntPtr.Add(buffer, i * stride));
            rows.Add(new(Text(row.Name, true)!, Text(row.DisplayName, false), row.Type, row.State, null, null, null, null,
                InspectionStatus.Unsupported, "Configuration not yet read."));
        }
        return new(DateTimeOffset.UtcNow, count > MaximumRows ? InspectionStatus.Truncated : InspectionStatus.Available, "Decoded bounded enumeration page.", rows);
    }

    private static InspectionStatus ErrorStatus(int error) => error switch { 5 => InspectionStatus.AccessDenied, 1060 or 1072 => InspectionStatus.Exited, 50 or 120 or 124 => InspectionStatus.Unsupported, _ => InspectionStatus.Failed };
}

[StructLayout(LayoutKind.Sequential)]
internal struct DriverServiceEntry
{
    internal IntPtr Name, DisplayName;
    internal uint Type, State, Controls, ExitCode, SpecificExitCode, CheckPoint, WaitHint, ProcessId, Flags;
}

internal interface IDriverServiceBackend
{
    long GetTimestamp();
    TimeSpan GetElapsedTime(long started);
    IDriverServiceManager? OpenManager(out int error);
}
internal interface IDriverServiceManager : IDisposable
{
    int Enumerate(IntPtr buffer, uint capacity, out uint needed, out uint count, ref uint resume);
    IServiceInspectionQuery? OpenConfig(string name, out int error);
}
internal sealed class WindowsDriverServiceBackend : IDriverServiceBackend
{
    public long GetTimestamp() => Stopwatch.GetTimestamp();
    public TimeSpan GetElapsedTime(long started) => Stopwatch.GetElapsedTime(started);
    public IDriverServiceManager? OpenManager(out int error)
    {
        var handle = NativeServiceMethods.OpenSCManagerW(null, null, 0x5);
        error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        if (!handle.IsInvalid) return new Manager(handle);
        handle.Dispose();
        return null;
    }
    private sealed class Manager(SafeServiceHandle handle) : IDriverServiceManager
    {
        public int Enumerate(IntPtr buffer, uint capacity, out uint needed, out uint count, ref uint resume) =>
            NativeServiceMethods.EnumServicesStatusExW(handle, 0, 0xB, 3, buffer, capacity, out needed, out count, ref resume, null) ? 0 : Marshal.GetLastWin32Error();
        public IServiceInspectionQuery? OpenConfig(string name, out int error)
        {
            var service = NativeServiceMethods.OpenServiceW(handle, name, 1);
            error = service.IsInvalid ? Marshal.GetLastWin32Error() : 0;
            if (!service.IsInvalid) return new QueryHandle(service);
            service.Dispose();
            return null;
        }
        public void Dispose() => handle.Dispose();
    }
    private sealed class QueryHandle(SafeServiceHandle handle) : IServiceInspectionQuery
    {
        public int Query(IntPtr buffer, uint capacity, out uint needed) => NativeServiceMethods.QueryServiceConfigW(handle, buffer, capacity, out needed) ? 0 : Marshal.GetLastWin32Error();
        public void Dispose() => handle.Dispose();
    }
}
