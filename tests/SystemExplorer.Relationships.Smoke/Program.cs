using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SystemExplorer.Core;
using SystemExplorer.Relationships;

int checks = 0;
void Require(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
}

void Invalid(Action action, string description)
{
    try { action(); }
    catch (InvalidDataException) { checks++; return; }
    throw new InvalidOperationException(description);
}

async Task Cancelled(Func<CancellationToken, Task> operation)
{
    using var source = new CancellationTokenSource();
    source.Cancel();
    try { await operation(source.Token); }
    catch (OperationCanceledException) { checks++; return; }
    throw new InvalidOperationException("Pre-cancelled operation was not cancelled.");
}

ServiceSnapshot ServiceRow(string name, string? startType = null) => new()
{
    Name = name,
    DisplayName = $"Display {name}",
    State = "Running",
    ProcessId = 12,
    StartType = startType
};

void RunServiceConfigReaderChecks()
{
    using var manager = new SafeServiceHandle();
    int headerSize = Marshal.SizeOf<NativeMethods.QueryServiceConfig>();
    int pointerSize = IntPtr.Size;
    int binaryPathOffset = pointerSize == 8 ? 16 : 12;
    int tagOffset = binaryPathOffset + 2 * pointerSize;
    int dependenciesOffset = (tagOffset + sizeof(uint) + pointerSize - 1) / pointerSize * pointerSize;
    Require(Marshal.OffsetOf<NativeMethods.QueryServiceConfig>(nameof(NativeMethods.QueryServiceConfig.StartType)).ToInt32() == 4 &&
        Marshal.OffsetOf<NativeMethods.QueryServiceConfig>(nameof(NativeMethods.QueryServiceConfig.BinaryPathName)).ToInt32() == binaryPathOffset &&
        Marshal.OffsetOf<NativeMethods.QueryServiceConfig>(nameof(NativeMethods.QueryServiceConfig.TagId)).ToInt32() == tagOffset &&
        Marshal.OffsetOf<NativeMethods.QueryServiceConfig>(nameof(NativeMethods.QueryServiceConfig.Dependencies)).ToInt32() == dependenciesOffset &&
        headerSize == dependenciesOffset + 3 * pointerSize, "QueryServiceConfig header layout does not match native alignment.");
    uint[] values = [0, 1, 2, 3, 4, 99];
    string[] expected = ["Boot", "System", "Automatic", "Manual", "Disabled", "Unknown(99)"];
    var mappingRows = values.Select((_, i) => ServiceRow($"map-{i}")).ToList();
    var mappingBackend = new FakeServiceConfigBackend();
    for (int i = 0; i < values.Length; i++)
        mappingBackend.AddResponses(mappingRows[i].Name,
            new FakeQueryResponse(122, (uint)headerSize),
            new FakeQueryResponse(0, (uint)headerSize, values[i]));
    var mappingLimitations = new List<string>();
    new ServiceConfigReader(mappingBackend).Enrich(manager, mappingRows, mappingLimitations, default);
    for (int i = 0; i < expected.Length; i++)
        Require(mappingRows[i].StartType == expected[i], $"Service start type {values[i]} mapped incorrectly.");
    Require(mappingBackend.RequestedAccess.All(access => access == 0x1), "Service open requested rights beyond SERVICE_QUERY_CONFIG.");
    Require(mappingBackend.ActiveHandles == 0 && mappingBackend.ActiveBuffers == 0, "Successful configuration reads leaked native resources.");
    Require(mappingLimitations.Single().Contains("observed=6, attempted=6, available=6", StringComparison.Ordinal), "Successful service coverage was not reported.");

    string longName = new('l', 100);
    var failureRows = new[]
    {
        ServiceRow("denied", "Disabled"),
        ServiceRow("deleted", "Disabled"),
        ServiceRow(longName, "Disabled"),
        ServiceRow("oversize", "Disabled"),
        ServiceRow("no-growth", "Disabled"),
        ServiceRow("growing", "Disabled")
    }.Select(row => row with { State = "Running", ProcessId = 42 }).ToList();
    var failureBackend = new FakeServiceConfigBackend();
    failureBackend.OpenErrors["denied"] = 5;
    failureBackend.OpenErrors["deleted"] = 1060;
    failureBackend.AddResponses(longName, new FakeQueryResponse(122, (uint)(headerSize - 1)));
    failureBackend.AddResponses("oversize", new FakeQueryResponse(122, 8193));
    failureBackend.AddResponses("no-growth",
        new FakeQueryResponse(122, (uint)headerSize),
        new FakeQueryResponse(122, (uint)headerSize));
    failureBackend.AddResponses("growing",
        new FakeQueryResponse(122, (uint)headerSize),
        new FakeQueryResponse(122, (uint)(headerSize + 8)),
        new FakeQueryResponse(0, (uint)(headerSize + 8), 2));
    var failureLimitations = new List<string>();
    new ServiceConfigReader(failureBackend).Enrich(manager, failureRows, failureLimitations, default);
    Require(failureRows.Take(5).All(row => row.StartType is null) && failureRows[5].StartType == "Automatic",
        "Failed, malformed, or growing configuration reads produced an incorrect value.");
    Require(failureRows.All(row => row.State == "Running" && row.ProcessId == 42), "Configuration enrichment changed service status data.");
    string failureSummary = failureLimitations.Single();
    Require(failureSummary.Contains("attempted=6, available=1, denied=1, missing/deleted=1, errors=3", StringComparison.Ordinal),
        "Failure categories were not summarized.");
    string exampleText = failureSummary[(failureSummary.IndexOf("; examples=", StringComparison.Ordinal) + 11)..];
    if (exampleText.EndsWith(".", StringComparison.Ordinal)) exampleText = exampleText[..^1];
    string[] examples = exampleText.Split(" | ");
    Require(examples.Length == 3 && examples[^1].StartsWith("error:", StringComparison.Ordinal) &&
        examples[^1]["error:".Length..].Length == 80 && examples[^1].EndsWith("...", StringComparison.Ordinal),
        "Service failure examples were not bounded and truncated.");
    Require(failureBackend.ActiveHandles == 0 && failureBackend.ActiveBuffers == 0, "Failed or retried configuration reads leaked native resources.");

    var rotationRows = Enumerable.Range(0, 257)
        .Select(i => ServiceRow($"svc{i:D3}", "Automatic")).ToList();
    var rotationBackend = new FakeServiceConfigBackend { DefaultOpenError = 5 };
    var rotationReader = new ServiceConfigReader(rotationBackend);
    var rotationLimitations = new List<string>();
    rotationReader.Enrich(manager, rotationRows, rotationLimitations, default);
    Require(rotationBackend.OpenedNames.Count == 256 && rotationBackend.OpenedNames[0] == "svc000" &&
        rotationBackend.OpenedNames[^1] == "svc255", "Service-attempt count bound was not enforced.");
    Require(rotationRows.All(row => row.StartType is null), "Budget-skipped services retained stale start types.");
    Require(rotationLimitations.Single().Contains("budget-skipped=1", StringComparison.Ordinal), "Count-limited coverage was not reported.");
    rotationBackend.OpenedNames.Clear();
    rotationLimitations.Clear();
    rotationReader.Enrich(manager, rotationRows, rotationLimitations, default);
    Require(rotationBackend.OpenedNames.Count == 256 && rotationBackend.OpenedNames[0] == "svc256" &&
        rotationBackend.OpenedNames[1] == "svc000" && rotationBackend.OpenedNames[^1] == "svc254",
        "Service cursor did not rotate to the next name after the count limit.");
    Require(rotationBackend.RequestedAccess.All(access => access == 0x1), "Rotating service reader requested additional access rights.");

    var probeClock = new FakeServiceConfigBackend { ClockMilliseconds = 50000 };
    probeClock.AddResponses("alpha", new FakeQueryResponse(122, (uint)headerSize, AfterCall: () => probeClock.ClockMilliseconds += 100));
    probeClock.OpenErrors["beta"] = 5;
    var probeRows = new List<ServiceSnapshot> { ServiceRow("alpha"), ServiceRow("beta") };
    var probeReader = new ServiceConfigReader(probeClock);
    var probeLimitations = new List<string>();
    probeReader.Enrich(manager, probeRows, probeLimitations, default);
    Require(probeClock.QueryCalls == 1 && probeRows.All(row => row.StartType is null), "Expired probe launched another native call or fabricated a value.");
    Require(probeLimitations.Single().Contains("budget-skipped=2", StringComparison.Ordinal), "Probe deadline coverage was not reported.");
    probeClock.OpenErrors.Remove("beta");
    probeClock.AddResponses("beta",
        new FakeQueryResponse(122, (uint)headerSize),
        new FakeQueryResponse(0, (uint)headerSize, 3));
    var nextRows = new List<ServiceSnapshot> { ServiceRow("beta") };
    probeLimitations.Clear();
    probeReader.Enrich(manager, nextRows, probeLimitations, default);
    Require(probeClock.OpenedNames[^1] == "beta" && nextRows[0].StartType == "Manual",
        "Deadline-skipped service rotation or per-pass timing failed.");

    var dataClock = new FakeServiceConfigBackend { ClockMilliseconds = 900 };
    dataClock.AddResponses("gamma",
        new FakeQueryResponse(122, (uint)headerSize),
        new FakeQueryResponse(122, (uint)(headerSize + 8), AfterCall: () => dataClock.ClockMilliseconds += 100),
        new FakeQueryResponse(0, (uint)(headerSize + 8), 1));
    var dataRows = new List<ServiceSnapshot> { ServiceRow("gamma"), ServiceRow("zeta") };
    var dataLimitations = new List<string>();
    new ServiceConfigReader(dataClock).Enrich(manager, dataRows, dataLimitations, default);
    Require(dataClock.QueryCalls == 2 && dataClock.OpenedNames.SequenceEqual(new[] { "gamma" }) &&
        dataRows.All(row => row.StartType is null), "Expired data retry launched another native call or service open, or fabricated a value.");
    Require(dataClock.ActiveHandles == 0 && dataClock.ActiveBuffers == 0, "Deadline stopped a data retry without releasing resources.");

    using var cancellation = new CancellationTokenSource();
    var cancelBackend = new FakeServiceConfigBackend();
    cancelBackend.AddResponses("cancel",
        new FakeQueryResponse(122, (uint)headerSize),
        new FakeQueryResponse(0, (uint)headerSize, 2, () => cancellation.Cancel()));
    var cancelRows = new List<ServiceSnapshot> { ServiceRow("cancel") };
    var cancelLimitations = new List<string>();
    bool canceled = false;
    try { new ServiceConfigReader(cancelBackend).Enrich(manager, cancelRows, cancelLimitations, cancellation.Token); }
    catch (OperationCanceledException) { canceled = true; }
    Require(canceled && cancelRows[0].StartType is null, "Cancellation during a configuration call parsed output or was ignored.");
    Require(cancelBackend.ActiveHandles == 0 && cancelBackend.ActiveBuffers == 0, "Cancellation leaked an active service handle or buffer.");
    Require(cancelLimitations.Single().Contains("canceled=1", StringComparison.Ordinal), "Canceled enrichment summary was not retained.");
}

async Task CompareServiceStartType(ServiceSnapshot service)
{
    string timestamp = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    var start = new ProcessStartInfo
    {
        FileName = "sc.exe",
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true
    };
    start.ArgumentList.Add("qc");
    start.ArgumentList.Add(service.Name);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start independent sc.exe comparison.");
    Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
    Task<string> stderrTask = process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    string stdout = await stdoutTask;
    _ = await stderrTask;
    uint? independent = process.ExitCode == 0 ? ParseScStartType(stdout) : null;
    string source = "sc.exe qc";
    if (independent is null)
    {
        Console.WriteLine($"sc.exe numeric START_TYPE unavailable; using independent SCM declaration (service={service.Name}; utc={timestamp}).");
        independent = IndependentScm.ReadStartType(service.Name);
        source = "independent SCM declaration";
    }

    uint observed = ParseStartTypeCode(service.StartType!);
    Require(independent.Value == observed, $"Independent start type disagrees for service {service.Name}.");
    Console.WriteLine($"Service start-type comparison: service={service.Name}; utc={timestamp}; configured={service.StartType}; numeric={independent.Value}; source={source}.");
}

uint? ParseScStartType(string output)
{
    Match match = Regex.Match(output, @"(?m)^\s*START_TYPE\s*:\s*(\d+)\b", RegexOptions.CultureInvariant);
    return match.Success && uint.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out uint value)
        ? value
        : null;
}

uint ParseStartTypeCode(string value)
{
    return value switch
    {
        "Boot" => 0,
        "System" => 1,
        "Automatic" => 2,
        "Manual" => 3,
        "Disabled" => 4,
        _ when value.StartsWith("Unknown(", StringComparison.Ordinal) && value.EndsWith(')') &&
            value.Length > 9 && uint.TryParse(value.AsSpan(8, value.Length - 9), NumberStyles.None, CultureInfo.InvariantCulture, out uint code) => code,
        _ => throw new InvalidOperationException($"Unrecognized service start type value: {value}")
    };
}

RunServiceConfigReaderChecks();

Require(LocalInventory.StartupViews(RegistryHive.CurrentUser, true).SequenceEqual(new[] { RegistryView.Registry64 }), "Shared HKCU Run keys enumerated twice.");
Require(LocalInventory.StartupViews(RegistryHive.LocalMachine, true).SequenceEqual(new[] { RegistryView.Registry64, RegistryView.Registry32 }), "Distinct machine startup views lost.");
foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
    Require(LocalInventory.StartupViews(hive, false).SequenceEqual(new[] { RegistryView.Registry32 }), "32-bit startup view incorrect.");

Invalid(() => EndpointTable.Parse(new byte[3], true, false, default), "Short header accepted.");
byte[] invalidCount = new byte[4];
BinaryPrimitives.WriteUInt32LittleEndian(invalidCount, uint.MaxValue);
Invalid(() => EndpointTable.Parse(invalidCount, true, true, default), "Overflowing count accepted.");
foreach ((bool tcp, bool ipv6, int stride) in new[] { (true, false, 24), (true, true, 56), (false, false, 12), (false, true, 28) })
{
    var bytes = new byte[4 + stride];
    BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
    int local = 4 + (tcp && !ipv6 ? 4 : 0);
    (ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback).GetAddressBytes().CopyTo(bytes, local);
    int port = 4 + (ipv6 ? 20 : tcp ? 8 : 4);
    BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(port), 54321);
    if (ipv6) BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(local + 16), 7);
    if (tcp) BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 + (ipv6 ? 48 : 0)), 2);
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 + stride - 4), 1234);
    var row = EndpointTable.Parse(bytes, tcp, ipv6, default).Single();
    Require(row.LocalPort == 54321 && row.OwningPid == 1234 && row.LocalAddress == (ipv6 ? "::1%7" : "127.0.0.1"), "Endpoint byte order/layout failed.");
    Require(row.RemoteAddress is null && row.RemotePort is null, "UDP/listener remote must be null.");
    Invalid(() => EndpointTable.Parse(bytes[..^1], tcp, ipv6, default), "Truncated row accepted.");
    if (tcp)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(4 + (ipv6 ? 48 : 0)), 5);
        int remoteOffset = 4 + (ipv6 ? 24 : 12);
        (ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback).GetAddressBytes().CopyTo(bytes, remoteOffset);
        BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(4 + (ipv6 ? 44 : 16)), 12345);
        var connected = EndpointTable.Parse(bytes, true, ipv6, default).Single();
        Require(connected.State == "Established" && connected.RemotePort == 12345 && connected.RemoteAddress == (ipv6 ? "::1" : "127.0.0.1"), "TCP remote decoding failed.");
    }
}

Require(WindowsRelationshipProvider.IdentityMatches(new(42, 100), 100, 101), "Matching identity rejected.");
Require(!WindowsRelationshipProvider.IdentityMatches(new(42, 99), 100, 101), "Reused PID accepted.");
Require(!WindowsRelationshipProvider.IdentityMatches(new(42, 102), 102, 101), "Post-capture process accepted.");
Require(!WindowsRelationshipProvider.IdentityMatches(new(42, null), 100, 101), "Unstable identity accepted.");
IntPtr buffer = Marshal.AllocHGlobal(8);
try
{
    for (int i = 0; i < 8; i++) Marshal.WriteByte(buffer, i, 65);
    Invalid(() => WindowsRelationshipProvider.ReadServiceString(buffer, 8, IntPtr.Add(buffer, 8)), "Out-of-range service string accepted.");
    Invalid(() => WindowsRelationshipProvider.ReadServiceString(buffer, 8, buffer), "Unterminated service string accepted.");
    Marshal.WriteInt16(buffer, 0, 0);
    Require(WindowsRelationshipProvider.ReadServiceString(buffer, 8, buffer) == "", "Empty service string rejected.");
}
finally { Marshal.FreeHGlobal(buffer); }

using var current = Process.GetCurrentProcess();
var identity = new ProcessIdentity(current.Id, current.StartTime.ToUniversalTime().ToFileTimeUtc());
var supplied = new[] { new ProcessSnapshot { Identity = identity, Name = "Smoke process" } };
using var provider = new WindowsRelationshipProvider();
var tcp4 = new TcpListener(IPAddress.Loopback, 0);
using var udp4 = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
TcpListener? tcp6 = null;
UdpClient? udp6 = null;
try
{
    tcp4.Start();
    if (Socket.OSSupportsIPv6)
    {
        tcp6 = new TcpListener(IPAddress.IPv6Loopback, 0);
        tcp6.Server.DualMode = false;
        tcp6.Start();
        udp6 = new UdpClient(AddressFamily.InterNetworkV6);
        udp6.Client.DualMode = false;
        udp6.Client.Bind(new IPEndPoint(IPAddress.IPv6Loopback, 0));
    }
    var snapshot = await provider.CaptureAsync(supplied, default);
    void Own(string protocol, IPEndPoint endpoint)
    {
        var rows = snapshot.Connections.Where(e => e.Protocol == protocol && e.LocalPort == endpoint.Port && e.LocalAddress == endpoint.Address.ToString() && e.OwningPid == current.Id).ToArray();
        Require(rows.Length > 0 && rows.All(e => e.OwnerIdentity == identity), "Own exact endpoint or creation identity not observed.");
        if (protocol == "UDP") Require(rows.All(e => e.RemoteAddress is null && e.RemotePort is null), "UDP remote fabricated.");
    }
    Own("TCP", (IPEndPoint)tcp4.LocalEndpoint);
    Own("UDP", (IPEndPoint)udp4.Client.LocalEndPoint!);
    if (tcp6 is not null && udp6 is not null)
    {
        Own("TCP", (IPEndPoint)tcp6.LocalEndpoint);
        Own("UDP", (IPEndPoint)udp6.Client.LocalEndPoint!);
    }
    else Console.WriteLine("IPv6 live case unsupported by this host.");
    Require(snapshot.Services.Count > 0, "No services observed on this Windows host.");
    Require(snapshot.Services.All(s => !string.IsNullOrWhiteSpace(s.Name) && !string.IsNullOrWhiteSpace(s.State)), "Invalid service observations.");
    var availableStartType = snapshot.Services.FirstOrDefault(s => s.StartType is not null);
    Require(availableStartType is not null, "No service start type was available for independent comparison.");
    await CompareServiceStartType(availableStartType!);
    Require(snapshot.Limitations.Any(limitation =>
        limitation.StartsWith("Service start types (configured base only):", StringComparison.Ordinal)),
        "Service start-type coverage summary missing.");
    Require(snapshot.Limitations.Count > 0, "Observation limitations missing.");
    var mismatch = new[] { supplied[0] with { Identity = identity with { CreationTimeFileTime = identity.CreationTimeFileTime + 1 } } };
    var rejected = await provider.CaptureAsync(mismatch, default);
    Require(rejected.Connections.Where(c => c.OwningPid == current.Id).All(c => c.OwnerIdentity is null), "Mismatched live creation time joined.");
    await Cancelled(t => provider.CaptureAsync(supplied, t));
    await Cancelled(t => provider.GetStartupItemsAsync(t));
    await Cancelled(t => provider.GetPublisherAsync(Environment.ProcessPath!, t));
    var startup = await provider.GetStartupItemsAsync(default);
    Require(startup.All(s => !string.IsNullOrEmpty(s.Source) && !string.IsNullOrEmpty(s.Scope)), "Startup provenance missing.");
    Require(startup.Where(s => s.Scope == "Current user" && s.Source.StartsWith("CurrentUser\\")).All(s => !Environment.Is64BitOperatingSystem || s.Source.EndsWith("(Registry64)")), "Shared HKCU startup keys have duplicate 32-bit provenance.");
    var unsigned = await provider.GetPublisherAsync(typeof(WindowsRelationshipProvider).Assembly.Location, default);
    Require(unsigned.Publisher is null && !unsigned.VerificationStatus.StartsWith("Verified", StringComparison.Ordinal), "Unsigned assembly claimed trusted.");
    var remote = await provider.GetPublisherAsync(@"\\invalid.example\share\file.exe", default);
    Require(remote.VerificationStatus.StartsWith("Unsupported", StringComparison.Ordinal), "Remote file not rejected before access.");
    var system = await provider.GetPublisherAsync(Path.Combine(Environment.SystemDirectory, "kernel32.dll"), default);
    Require(!string.IsNullOrEmpty(system.VerificationStatus), "System file verification returned no status.");
    Require(system.Publisher is null || system.VerificationStatus.StartsWith("Verified", StringComparison.Ordinal), "Publisher claimed without verified signature.");
    Console.WriteLine($"Observed endpoint rows={snapshot.Connections.Count}; services={snapshot.Services.Count}; startup entries={startup.Count}; limitations={snapshot.Limitations.Count}; IPv6={tcp6 is not null}.");
    Console.WriteLine($"System binary trust result: {system.VerificationStatus}");
}
finally
{
    tcp4.Stop();
    tcp6?.Stop();
    udp6?.Dispose();
}
provider.Dispose();
try { await provider.CaptureAsync(supplied, default); throw new InvalidOperationException("Disposed provider accepted capture."); }
catch (ObjectDisposedException) { checks++; }
Console.WriteLine($"PASS: {checks} checks (deterministic parser/identity checks and real Windows observations).");

internal sealed record FakeQueryResponse(int Error, uint RequiredSize, uint StartType = 0, Action? AfterCall = null);

internal sealed class FakeServiceConfigBackend : IServiceConfigBackend
{
    private readonly Dictionary<string, Queue<FakeQueryResponse>> responses = new(StringComparer.OrdinalIgnoreCase);

    internal int DefaultOpenError { get; set; }
    internal long ClockMilliseconds { get; set; }
    internal int QueryCalls { get; private set; }
    internal int ActiveHandles { get; private set; }
    internal int ActiveBuffers { get; private set; }
    internal List<string> OpenedNames { get; } = new();
    internal List<uint> RequestedAccess { get; } = new();
    internal Dictionary<string, int> OpenErrors { get; } = new(StringComparer.OrdinalIgnoreCase);

    internal void AddResponses(string name, params FakeQueryResponse[] items)
    {
        if (!responses.TryGetValue(name, out var queue))
        {
            queue = new Queue<FakeQueryResponse>();
            responses.Add(name, queue);
        }
        foreach (var item in items) queue.Enqueue(item);
    }

    public IServiceConfigQueryHandle? Open(SafeServiceHandle manager, string serviceName, uint desiredAccess, out int error)
    {
        OpenedNames.Add(serviceName);
        RequestedAccess.Add(desiredAccess);
        if (desiredAccess != 0x1) throw new InvalidOperationException("Fake SCM received unexpected service access.");
        int openError = OpenErrors.TryGetValue(serviceName, out int configuredError) ? configuredError : DefaultOpenError;
        if (openError != 0)
        {
            error = openError;
            return null;
        }
        error = 0;
        ActiveHandles++;
        return new FakeServiceConfigHandle(this, serviceName);
    }

    public IntPtr AllocateBuffer(int size)
    {
        IntPtr buffer = Marshal.AllocHGlobal(size);
        ActiveBuffers++;
        return buffer;
    }

    public void FreeBuffer(IntPtr buffer)
    {
        Marshal.FreeHGlobal(buffer);
        ActiveBuffers--;
    }

    public long GetTimestamp() => ClockMilliseconds;

    public double GetElapsedMilliseconds(long startedAt) => ClockMilliseconds - startedAt;

    private int Query(string serviceName, IntPtr buffer, uint bufferSize, out uint requiredSize)
    {
        QueryCalls++;
        if (!responses.TryGetValue(serviceName, out var queue) || queue.Count == 0)
            throw new InvalidOperationException($"No fake query response for {serviceName}.");
        var response = queue.Dequeue();
        requiredSize = response.RequiredSize;
        if (response.Error == 0 && buffer != IntPtr.Zero && bufferSize >= (uint)Marshal.SizeOf<NativeMethods.QueryServiceConfig>())
        {
            Marshal.StructureToPtr(new NativeMethods.QueryServiceConfig
            {
                ServiceType = 0x30,
                StartType = response.StartType,
                ErrorControl = 1,
                BinaryPathName = new IntPtr(1),
                LoadOrderGroup = new IntPtr(2),
                TagId = 3,
                Dependencies = new IntPtr(4),
                ServiceStartName = new IntPtr(5),
                DisplayName = new IntPtr(6)
            }, buffer, false);
        }
        response.AfterCall?.Invoke();
        return response.Error;
    }

    private void HandleClosed() => ActiveHandles--;

    private sealed class FakeServiceConfigHandle(FakeServiceConfigBackend backend, string serviceName) : IServiceConfigQueryHandle
    {
        private bool disposed;

        public int Query(IntPtr buffer, uint bufferSize, out uint requiredSize) =>
            backend.Query(serviceName, buffer, bufferSize, out requiredSize);

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            backend.HandleClosed();
        }
    }
}

internal static class IndependentScm
{
    private const int ErrorInsufficientBuffer = 122;
    private const int MaximumConfigBytes = 8192;

    internal static uint ReadStartType(string serviceName)
    {
        using var manager = OpenSCManagerW(null, null, 0x5);
        if (manager.IsInvalid) throw new InvalidOperationException($"Independent SCM manager open failed ({Marshal.GetLastWin32Error()}).");
        using var service = OpenServiceW(manager, serviceName, 0x1);
        if (service.IsInvalid) throw new InvalidOperationException($"Independent service query open failed ({Marshal.GetLastWin32Error()}).");

        uint requiredSize;
        bool success = QueryServiceConfigW(service, IntPtr.Zero, 0, out requiredSize);
        int error = success ? 0 : Marshal.GetLastWin32Error();
        int headerSize = Marshal.SizeOf<IndependentQueryServiceConfig>();
        if (success || error != ErrorInsufficientBuffer || requiredSize < (uint)headerSize || requiredSize > MaximumConfigBytes)
            throw new InvalidOperationException("Independent SCM probe returned an invalid size.");

        uint bufferSize = requiredSize;
        IntPtr buffer = Marshal.AllocHGlobal((int)bufferSize);
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                success = QueryServiceConfigW(service, buffer, bufferSize, out uint nextSize);
                if (success) return Marshal.PtrToStructure<IndependentQueryServiceConfig>(buffer).StartType;
                error = Marshal.GetLastWin32Error();
                if (error != ErrorInsufficientBuffer || attempt == 1 || nextSize <= bufferSize ||
                    nextSize < (uint)headerSize || nextSize > MaximumConfigBytes)
                    throw new InvalidOperationException("Independent SCM query did not complete within its bound.");
                IntPtr replacement = Marshal.AllocHGlobal((int)nextSize);
                Marshal.FreeHGlobal(buffer);
                buffer = replacement;
                bufferSize = nextSize;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
        throw new InvalidOperationException("Independent SCM query exhausted its attempts.");
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeServiceHandle OpenServiceW(SafeServiceHandle manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceConfigW(SafeServiceHandle service, IntPtr buffer, uint bufferSize, out uint bytesNeeded);

    [StructLayout(LayoutKind.Sequential)]
    private struct IndependentQueryServiceConfig
    {
        internal uint ServiceType, StartType, ErrorControl;
        internal IntPtr BinaryPathName, LoadOrderGroup;
        internal uint TagId;
        internal IntPtr Dependencies, ServiceStartName, DisplayName;
    }
}
