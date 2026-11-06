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
                    if (CheckBudget() is { } expired) return expired;
                    return result with
                    {
                        Extensions = ReadExtensions(service, () => backend.GetElapsedTime(started) >= TimeSpan.FromSeconds(3), token),
                        Dependents = ReadDependents(service, () => backend.GetElapsedTime(started) >= TimeSpan.FromSeconds(3), token)
                    };
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

    private delegate int BufferQuery(IntPtr buffer, uint capacity, out uint needed, out uint count);

    private static T Observe<T>(BufferQuery query, int limit, Func<IntPtr, int, uint, T> decode,
        Func<InspectionStatus, string, T> failure, Func<bool> expired, CancellationToken token)
    {
        int capacity = 0;
        for (int call = 0; call < MaximumQueryCalls; call++)
        {
            token.ThrowIfCancellationRequested();
            if (expired()) return failure(InspectionStatus.TimedOut, "Cooperative three-second budget expired.");
            IntPtr buffer = capacity == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(capacity);
            try
            {
                if (capacity > 0) Marshal.Copy(Enumerable.Repeat((byte)0xff, capacity).ToArray(), 0, buffer, capacity);
                int error = query(buffer, (uint)capacity, out uint needed, out uint count);
                token.ThrowIfCancellationRequested();
                if (expired()) return failure(InspectionStatus.TimedOut, "Cooperative three-second budget expired after synchronous SCM call.");
                if (error is 122 or 234)
                {
                    if (needed > limit) return failure(InspectionStatus.Truncated, "Native response exceeds byte bound; no values returned.");
                    if (needed <= capacity) return failure(InspectionStatus.Failed, "Invalid non-progressing resize response.");
                    capacity = (int)needed;
                    continue;
                }
                if (error != 0) return failure(error switch
                {
                    5 => InspectionStatus.AccessDenied,
                    50 or 120 or 124 => InspectionStatus.Unsupported,
                    _ => InspectionStatus.Failed
                }, $"Local SCM observation unavailable (Windows error {error}); no fallback.");
                try { return decode(buffer, capacity, count); }
                catch (InvalidDataException) { return failure(InspectionStatus.Failed, "Invalid pointer, header or terminator in owned native buffer; no values returned."); }
            }
            finally { if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer); }
        }
        return failure(InspectionStatus.Truncated, "Response kept changing during three bounded calls; no values returned.");
    }

    private static ServiceConfigExtensions ReadExtensions(IServiceInspectionQuery service, Func<bool> expired, CancellationToken token)
    {
        T Read<T>(uint level, int limit, Func<IntPtr, int, CancellationToken, T> decode, Func<InspectionStatus, string, T> failure) =>
            Observe((IntPtr b, uint c, out uint n, out uint count) => { count = 0; return service.QueryExtension(level, b, c, out n); },
                limit, (b, c, _) => decode(b, c, token), failure, expired, token);
        return new(
            Read(3, 8192, DecodeDelayedAutoStart, (s, d) => new ServiceDelayedAutoStartObservation(s, d, null)),
            Read(8, 16384, DecodeTriggers, (s, d) => new ServiceTriggerObservation(s, d, [])),
            Read(1, 8192, DecodeDescription, (s, d) => new ServiceDescriptionObservation(s, d, null)),
            Read(5, 8192, DecodeSid, (s, d) => new ServiceSidObservation(s, d, null, null)));
    }

    private static ServiceDependentsObservation ReadDependents(IServiceInspectionQuery service, Func<bool> expired, CancellationToken token) =>
        Observe(service.QueryDependents, 65536, (b, c, count) => DecodeDependents(b, c, count, token),
            (s, d) => new ServiceDependentsObservation(s, d, []), expired, token);

    private static void Range(IntPtr buffer, int capacity, IntPtr pointer, long length)
    {
        long offset = pointer.ToInt64() - buffer.ToInt64();
        if (buffer == IntPtr.Zero || capacity < 0 || capacity > 65536 || pointer == IntPtr.Zero || offset < 0 || length < 0 || offset > capacity || length > capacity - offset)
            throw new InvalidDataException("Native field is outside owned buffer.");
    }

    private static T Header<T>(IntPtr buffer, int capacity) where T : struct
    {
        Range(buffer, capacity, buffer, Marshal.SizeOf<T>());
        return Marshal.PtrToStructure<T>(buffer);
    }

    private static string? ExtensionString(IntPtr buffer, int capacity, IntPtr pointer, int minimumOffset, int maximumChars,
        CancellationToken token, out bool truncated)
    {
        truncated = false;
        if (pointer == IntPtr.Zero) return null;
        Range(buffer, capacity, pointer, 2);
        long offset = pointer.ToInt64() - buffer.ToInt64();
        if (offset < minimumOffset || offset % 2 != 0) throw new InvalidDataException("Invalid string payload pointer.");
        for (int i = 0; i < maximumChars; i++)
        {
            token.ThrowIfCancellationRequested();
            Range(buffer, capacity, IntPtr.Add(pointer, i * 2), 2);
            if (Marshal.ReadInt16(pointer, i * 2) == 0) return Marshal.PtrToStringUni(pointer, i);
        }
        // Validate a terminator in the owned buffer even when the displayed value is capped.
        for (int i = maximumChars; offset + (long)i * 2 + 2 <= capacity; i++)
        {
            token.ThrowIfCancellationRequested();
            if (Marshal.ReadInt16(pointer, i * 2) == 0)
            {
                truncated = i > maximumChars;
                return Marshal.PtrToStringUni(pointer, maximumChars);
            }
        }
        throw new InvalidDataException("Unterminated native string.");
    }

    internal static ServiceDelayedAutoStartObservation DecodeDelayedAutoStart(IntPtr buffer, int capacity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int value = Header<int>(buffer, capacity);
        return new(InspectionStatus.Available, "Observed delayed-auto-start configuration.", value != 0);
    }

    internal static ServiceSidObservation DecodeSid(IntPtr buffer, int capacity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        uint value = Header<uint>(buffer, capacity);
        return new(InspectionStatus.Available, "Observed configured service SID type.", value,
            value switch { 0 => "NONE", 1 => "UNRESTRICTED", 3 => "RESTRICTED", _ => null });
    }

    internal static ServiceDescriptionObservation DecodeDescription(IntPtr buffer, int capacity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        IntPtr pointer = Header<IntPtr>(buffer, capacity);
        string? description = ExtensionString(buffer, capacity, pointer, IntPtr.Size, 2048, token, out bool truncated);
        return new(truncated ? InspectionStatus.Truncated : InspectionStatus.Available,
            truncated ? "Description limited to 2048 characters." : "Observed configured description; null means no configured description.", description);
    }

    internal static ServiceTriggerObservation DecodeTriggers(IntPtr buffer, int capacity, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var info = Header<ServiceTriggerInfo>(buffer, capacity);
        int stride = Marshal.SizeOf<ServiceTriggerEntry>();
        if (info.Count > 0) Range(buffer, capacity, info.Triggers, (long)info.Count * stride);
        var triggers = new List<ServiceTrigger>();
        for (int i = 0; i < Math.Min(info.Count, 64); i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = Marshal.PtrToStructure<ServiceTriggerEntry>(IntPtr.Add(info.Triggers, i * stride));
            Guid? subtype = null;
            if (entry.Subtype != IntPtr.Zero) { Range(buffer, capacity, entry.Subtype, 16); subtype = Marshal.PtrToStructure<Guid>(entry.Subtype); }
            if (entry.DataCount > int.MaxValue) throw new InvalidDataException("Invalid data count.");
            // Validate the data-item array only; never inspect its blobs.
            if (entry.DataCount > 0) Range(buffer, capacity, entry.Data, (long)entry.DataCount * (IntPtr.Size == 8 ? 16 : 12));
            string type = entry.Type switch
            {
                1 => "DEVICE_INTERFACE_ARRIVAL", 2 => "IP_ADDRESS_AVAILABILITY", 3 => "DOMAIN_JOIN", 4 => "FIREWALL_PORT_EVENT",
                5 => "GROUP_POLICY", 6 => "NETWORK_ENDPOINT", 7 => "CUSTOM_SYSTEM_STATE_CHANGE", 20 => "CUSTOM", 30 => "AGGREGATE", _ => $"Unknown({entry.Type})"
            };
            triggers.Add(new(entry.Type, type, entry.Action, entry.Action switch { 1 => "START", 2 => "STOP", _ => $"Unknown({entry.Action})" },
                subtype, subtype is { } guid && SubtypeNames.TryGetValue(guid, out var name) ? name : null, (int)entry.DataCount));
        }
        return new(info.Count > 64 ? InspectionStatus.Truncated : InspectionStatus.Available,
            info.Count > 64 ? "Trigger metadata limited to 64 entries; data blobs not decoded." : "Observed trigger metadata only; data blobs not decoded.", triggers);
    }

    private static readonly IReadOnlyDictionary<Guid, string> SubtypeNames = new Dictionary<Guid, string>
    {
        [new("4f27f2de-14e2-430b-a549-7cd48cbc8245")] = "NETWORK_MANAGER_FIRST_IP_ADDRESS_ARRIVAL",
        [new("cc4ba62a-162e-4648-847a-b6bdf993e335")] = "NETWORK_MANAGER_LAST_IP_ADDRESS_REMOVAL",
        [new("1ce20aba-9851-4421-9430-1ddeb766e809")] = "DOMAIN_JOIN",
        [new("ddaf516e-58c2-4866-9574-c3b615d42ea1")] = "DOMAIN_LEAVE",
        [new("b7569e07-8421-4ee0-ad10-86915afdad09")] = "FIREWALL_PORT_OPEN",
        [new("a144ed38-8e12-4de4-9d96-e64740b1a524")] = "FIREWALL_PORT_CLOSE",
        [new("659fcae6-5bdb-4da9-b1ff-ca2a178d46e0")] = "MACHINE_POLICY_PRESENT",
        [new("54fb46c8-f089-464c-b1fd-59d1b62c3b50")] = "USER_POLICY_PRESENT",
        [new("bc90d167-9470-4139-a9ba-be0bbbf5b74d")] = "RPC_INTERFACE_EVENT",
        [new("1f81d131-3fac-4537-9e0c-7e7b0c2f4b55")] = "NAMED_PIPE_EVENT"
    };

    internal static ServiceDependentsObservation DecodeDependents(IntPtr buffer, int capacity, uint count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        int stride = Marshal.SizeOf<ServiceDependentEntry>();
        if (count > 0) Range(buffer, capacity, buffer, (long)count * stride);
        var services = new List<ServiceDependent>();
        for (int i = 0; i < Math.Min(count, 512); i++)
        {
            token.ThrowIfCancellationRequested();
            var entry = Marshal.PtrToStructure<ServiceDependentEntry>(IntPtr.Add(buffer, i * stride));
            string? name = ExtensionString(buffer, capacity, entry.Name, checked((int)count * stride), 256, token, out bool nameTruncated);
            string? display = ExtensionString(buffer, capacity, entry.DisplayName, checked((int)count * stride), 256, token, out bool displayTruncated);
            if (string.IsNullOrEmpty(name) || nameTruncated || displayTruncated) throw new InvalidDataException("Invalid dependent name.");
            services.Add(new(name, display, entry.Type, entry.State));
        }
        return new(count > 512 ? InspectionStatus.Truncated : InspectionStatus.Available,
            count > 512 ? "Dependent rows limited to 512." : "Observed configured dependent services, not process ownership.",
            services.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ThenBy(s => s.Name, StringComparer.Ordinal).ToArray());
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
        string detail = "Observed unexpanded local SCM configuration for a future start, not running-process ownership. Delayed/trigger configuration is separately observed in Extensions. Binary path and account may contain sensitive local metadata; no credentials collected.";
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
    int QueryExtension(uint level, IntPtr buffer, uint capacity, out uint needed) { needed = 0; return 124; }
    int QueryDependents(IntPtr buffer, uint capacity, out uint needed, out uint count) { needed = count = 0; return 124; }
}

internal sealed class WindowsServiceInspectionBackend : IServiceInspectionBackend
{
    public long GetTimestamp() => Stopwatch.GetTimestamp();
    public TimeSpan GetElapsedTime(long started) => Stopwatch.GetElapsedTime(started);
    public IServiceInspectionManager? OpenManager(out int error)
    {
        var handle = OpenSCManagerW(null, null, 0x5);
        error = handle.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        if (!handle.IsInvalid) return new Manager(handle);
        handle.Dispose();
        return null;
    }

    private sealed class Manager(SafeServiceHandle handle) : IServiceInspectionManager
    {
        public IServiceInspectionQuery? OpenService(string name, out int error)
        {
            var service = OpenServiceW(handle, name, 0x9);
            error = service.IsInvalid ? Marshal.GetLastWin32Error() : 0;
            if (!service.IsInvalid) return new QueryHandle(service, false);
            service.Dispose();
            if (error == 5)
            {
                service = OpenServiceW(handle, name, 0x1);
                error = service.IsInvalid ? Marshal.GetLastWin32Error() : 0;
                if (!service.IsInvalid) return new QueryHandle(service, true);
                service.Dispose();
            }
            return null;
        }
        public void Dispose() => handle.Dispose();
    }

    private sealed class QueryHandle(SafeServiceHandle handle, bool dependentsDenied) : IServiceInspectionQuery
    {
        public int Query(IntPtr buffer, uint capacity, out uint needed) =>
            QueryServiceConfigW(handle, buffer, capacity, out needed) ? 0 : Marshal.GetLastWin32Error();
        public int QueryExtension(uint level, IntPtr buffer, uint capacity, out uint needed) =>
            NativeServiceMethods.QueryServiceConfig2W(handle, level, buffer, capacity, out needed) ? 0 : Marshal.GetLastWin32Error();
        public int QueryDependents(IntPtr buffer, uint capacity, out uint needed, out uint count)
        {
            needed = count = 0;
            if (dependentsDenied) return 5;
            return NativeServiceMethods.EnumDependentServicesW(handle, 3, buffer, capacity, out needed, out count) ? 0 : Marshal.GetLastWin32Error();
        }
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
