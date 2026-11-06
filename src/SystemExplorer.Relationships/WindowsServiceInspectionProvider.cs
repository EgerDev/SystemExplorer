using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SystemExplorer.Core;

[assembly: InternalsVisibleTo("SystemExplorer.ServiceInspection.Smoke")]

namespace SystemExplorer.Relationships;

public sealed class WindowsServiceInspectionProvider : IServiceInspectionProvider
{
    internal const int MaximumConfigBytes = 8192;
    internal const int MaximumQueryCalls = 3;
    internal const int MaximumDependencies = 256;
    private readonly IServiceInspectionBackend backend;
    private readonly SemaphoreSlim gate = new(1, 1);

    public WindowsServiceInspectionProvider() : this(new WindowsServiceInspectionBackend()) { }
    internal WindowsServiceInspectionProvider(IServiceInspectionBackend backend) =>
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public async Task<ServiceInspection> CaptureAsync(string serviceName, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(serviceName);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(() => Capture(serviceName, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    private ServiceInspection Capture(string name, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var captured = DateTimeOffset.UtcNow;
        long started = backend.GetTimestamp();
        ServiceInspection Result(InspectionStatus status, string detail) =>
            new(name, captured, status, detail, null, null, null, null, null, null, []);
        ServiceInspection? CheckBudget()
        {
            token.ThrowIfCancellationRequested();
            return backend.GetElapsedTime(started) >= TimeSpan.FromSeconds(3)
                ? Result(InspectionStatus.TimedOut, "Cooperative three-second budget expired; synchronous SCM calls cannot be interrupted. No complete configuration returned.")
                : null;
        }
        ServiceInspection Error(int error) => Result(error switch
        {
            5 => InspectionStatus.AccessDenied,
            1060 or 1072 => InspectionStatus.Exited,
            50 or 120 => InspectionStatus.Unsupported,
            _ => InspectionStatus.Failed
        }, error is 1060 or 1072
            ? $"Service is missing/deleted (Windows error {error}); no configuration available."
            : $"Service configuration unavailable (Windows error {error}); no elevation or fallback.");

        if (!OperatingSystem.IsWindows()) return Result(InspectionStatus.Unsupported, "Local Windows SCM APIs are required.");
        if (name.Length is 0 or > 256 || name.IndexOfAny(['\0', '/', '\\']) >= 0)
            return Result(InspectionStatus.Unsupported, "A local SCM service name of 1–256 characters without NUL or slashes is required.");
        try
        {
            if (CheckBudget() is { } beforeOpen) return beforeOpen;
            using var manager = backend.OpenManager(out int error);
            if (CheckBudget() is { } afterManager) return afterManager;
            if (manager is null) return Error(error);
            using var service = manager.OpenService(name, out error);
            if (CheckBudget() is { } afterService) return afterService;
            if (service is null) return Error(error);

            int capacity = 0;
            for (int attempt = 0; attempt < MaximumQueryCalls; attempt++)
            {
                if (CheckBudget() is { } beforeQuery) return beforeQuery;
                IntPtr buffer = capacity == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(capacity);
                try
                {
                    // A missing native terminator must not be supplied by uninitialized allocation contents.
                    if (capacity != 0) Marshal.Copy(Enumerable.Repeat((byte)0xff, capacity).ToArray(), 0, buffer, capacity);
                    error = service.Query(buffer, (uint)capacity, out uint needed);
                    if (CheckBudget() is { } afterQuery) return afterQuery;
                    if (error == 122)
                    {
                        if (needed > MaximumConfigBytes)
                            return Result(InspectionStatus.Truncated, "Configuration exceeds the 8192-byte bound; no configuration returned.");
                        if (needed <= capacity || needed < Marshal.SizeOf<ServiceInspectionConfig>())
                            return Result(InspectionStatus.Failed, "Invalid configuration resize response; no progress within the buffer bound.");
                        capacity = (int)needed;
                        continue;
                    }
                    if (error != 0) return Error(error);
                    if (capacity < Marshal.SizeOf<ServiceInspectionConfig>())
                        return Result(InspectionStatus.Failed, "Configuration response is missing its header.");
                    var result = Decode(name, captured, buffer, capacity, token);
                    return CheckBudget() ?? result;
                }
                finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
            }
            return Result(InspectionStatus.Truncated, "Configuration kept changing during three bounded query calls; no configuration returned.");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or PlatformNotSupportedException)
        {
            return Result(InspectionStatus.Unsupported, "The required local SCM API is unavailable.");
        }
    }

    internal static ServiceInspection Decode(string name, DateTimeOffset captured, IntPtr buffer, int capacity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (buffer == IntPtr.Zero || capacity < Marshal.SizeOf<ServiceInspectionConfig>() || capacity > MaximumConfigBytes)
            throw new InvalidDataException("Configuration header is outside the owned buffer.");
        var config = Marshal.PtrToStructure<ServiceInspectionConfig>(buffer);
        var invalid = new List<string>();
        string? Read(IntPtr pointer, string field)
        {
            try { return ReadString(buffer, capacity, pointer, token); }
            catch (InvalidDataException) { invalid.Add(field); return null; }
        }
        string? binary = Read(config.BinaryPath, "binary path");
        string? account = Read(config.Account, "account");
        string? group = Read(config.LoadOrderGroup, "load-order group");
        _ = Read(config.DisplayName, "display name");
        IReadOnlyList<string> dependencies = [];
        bool truncated = false;
        try { dependencies = ReadDependencies(buffer, capacity, config.Dependencies, token, out truncated); }
        catch (InvalidDataException) { invalid.Add("dependencies"); }
        var status = invalid.Count > 0 ? InspectionStatus.Partial : truncated ? InspectionStatus.Truncated : InspectionStatus.Available;
        string detail = "Observed unexpanded local SCM configuration for a future start, not running-process ownership or delayed/trigger behavior. Binary path and account may contain sensitive local metadata; no credentials collected.";
        if (invalid.Count > 0) detail += $" Invalid bounded fields omitted: {string.Join(", ", invalid)}.";
        if (truncated) detail += " Dependencies limited to the first 256 entries.";
        return new(name, captured, status, detail, binary, account, group,
            config.ServiceType, config.StartType, config.ErrorControl, dependencies);
    }

    internal static string? ReadString(IntPtr buffer, int capacity, IntPtr pointer, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (pointer == IntPtr.Zero) return null;
        int offset = StringOffset(buffer, capacity, pointer);
        int length = StringLength(buffer, capacity, offset, token);
        return Marshal.PtrToStringUni(pointer, length);
    }

    internal static IReadOnlyList<string> ReadDependencies(IntPtr buffer, int capacity, IntPtr pointer,
        CancellationToken token, out bool truncated)
    {
        token.ThrowIfCancellationRequested();
        truncated = false;
        if (pointer == IntPtr.Zero) return [];
        int offset = StringOffset(buffer, capacity, pointer);
        var names = new List<string>();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            int length = StringLength(buffer, capacity, offset, token);
            if (length == 0) return names.ToArray();
            if (names.Count < MaximumDependencies)
                names.Add(Marshal.PtrToStringUni(IntPtr.Add(buffer, offset), length)!);
            else truncated = true;
            offset += (length + 1) * sizeof(char);
        }
    }

    private static int StringOffset(IntPtr buffer, int capacity, IntPtr pointer)
    {
        long offset = pointer.ToInt64() - buffer.ToInt64();
        if (buffer == IntPtr.Zero || capacity > MaximumConfigBytes || offset < Marshal.SizeOf<ServiceInspectionConfig>() ||
            offset >= capacity || offset % sizeof(char) != 0)
            throw new InvalidDataException("String pointer is outside the configuration payload.");
        return (int)offset;
    }

    private static int StringLength(IntPtr buffer, int capacity, int offset, CancellationToken token)
    {
        for (int i = offset; i <= capacity - sizeof(char); i += sizeof(char))
        {
            token.ThrowIfCancellationRequested();
            if (Marshal.ReadInt16(buffer, i) == 0) return (i - offset) / sizeof(char);
        }
        throw new InvalidDataException("String is not terminated within the owned configuration buffer.");
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct ServiceInspectionConfig
{
    internal uint ServiceType, StartType, ErrorControl;
    internal IntPtr BinaryPath, LoadOrderGroup;
    internal uint TagId;
    internal IntPtr Dependencies, Account, DisplayName;
}

internal interface IServiceInspectionBackend
{
    IServiceInspectionManager? OpenManager(out int error);
    long GetTimestamp();
    TimeSpan GetElapsedTime(long started);
}

internal interface IServiceInspectionManager : IDisposable
{
    IServiceInspectionQuery? OpenService(string name, out int error);
}

internal interface IServiceInspectionQuery : IDisposable
{
    int Query(IntPtr buffer, uint capacity, out uint needed);
}

internal sealed class WindowsServiceInspectionBackend : IServiceInspectionBackend
{
    public long GetTimestamp() => Stopwatch.GetTimestamp();
    public TimeSpan GetElapsedTime(long started) => Stopwatch.GetElapsedTime(started);
    public IServiceInspectionManager? OpenManager(out int error)
    {
        var handle = OpenSCManagerW(null, null, 0x1); // SC_MANAGER_CONNECT only.
        error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        if (!handle.IsInvalid) return new Manager(handle);
        handle.Dispose();
        return null;
    }

    private sealed class Manager(SafeServiceHandle handle) : IServiceInspectionManager
    {
        public IServiceInspectionQuery? OpenService(string name, out int error)
        {
            var service = OpenServiceW(handle, name, 0x1); // SERVICE_QUERY_CONFIG only.
            error = service.IsInvalid ? Marshal.GetLastWin32Error() : 0;
            if (!service.IsInvalid) return new QueryHandle(service);
            service.Dispose();
            return null;
        }
        public void Dispose() => handle.Dispose();
    }

    private sealed class QueryHandle(SafeServiceHandle handle) : IServiceInspectionQuery
    {
        public int Query(IntPtr buffer, uint capacity, out uint needed) =>
            QueryServiceConfigW(handle, buffer, capacity, out needed) ? 0 : Marshal.GetLastWin32Error();
        public void Dispose() => handle.Dispose();
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern SafeServiceHandle OpenServiceW(SafeServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(SafeServiceHandle service, IntPtr buffer, uint capacity, out uint needed);
}
