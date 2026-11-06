using System.Runtime.InteropServices;
using System.Text;
using SystemExplorer.Core;
using SystemExplorer.Relationships;

int checks = 0;
void Require(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
}
void Invalid(Action action)
{
    try { action(); }
    catch (InvalidDataException) { checks++; return; }
    throw new InvalidOperationException("Malformed buffer was accepted.");
}
async Task Canceled(Func<Task> action)
{
    try { await action(); }
    catch (OperationCanceledException) { checks++; return; }
    throw new InvalidOperationException("Cancellation was ignored.");
}

int headerSize = Marshal.SizeOf<ServiceInspectionConfig>();
Require(headerSize == (IntPtr.Size == 8 ? 64 : 36), "Native header size is wrong.");
Require(Marshal.OffsetOf<ServiceInspectionConfig>(nameof(ServiceInspectionConfig.BinaryPath)).ToInt32() == (IntPtr.Size == 8 ? 16 : 12), "Pointer alignment is wrong.");
using (var memory = new BufferFixture(1024))
{
    var binary = memory.Write("%LOCAL_TEST%\\工具.exe --fictional\0");
    var account = memory.Write("fictional\\用户\0");
    var group = memory.Write("组\0");
    var deps = memory.Write("service-α\0+Group-工具\0\0");
    var display = memory.Write("展示\0");
    var config = new ServiceInspectionConfig
    {
        ServiceType = 0x10, StartType = 2, ErrorControl = 1,
        BinaryPath = binary, Account = account, LoadOrderGroup = group, Dependencies = deps, DisplayName = display
    };
    Marshal.StructureToPtr(config, memory.Pointer, false);
    var result = WindowsServiceInspectionProvider.Decode("fictional", DateTimeOffset.UtcNow, memory.Pointer, memory.Capacity, default);
    Require(result.Status == InspectionStatus.Available && result.BinaryPath == "%LOCAL_TEST%\\工具.exe --fictional" &&
        result.Account == "fictional\\用户" && result.LoadOrderGroup == "组", "Unicode/configuration strings were changed.");
    Require(result.ServiceType == 0x10 && result.StartType == 2 && result.ErrorControl == 1 &&
        result.Dependencies.SequenceEqual(new[] { "service-α", "+Group-工具" }), "SCM fields or group dependency prefix lost.");
    Require(WindowsServiceInspectionProvider.ReadString(memory.Pointer, memory.Capacity, IntPtr.Zero, default) is null, "Null string should be unavailable.");
    Require(WindowsServiceInspectionProvider.ReadDependencies(memory.Pointer, memory.Capacity, IntPtr.Zero, default, out _).Count == 0, "Null dependencies should be empty.");
    foreach (IntPtr bad in new[] { IntPtr.Subtract(memory.Pointer, 2), memory.Pointer, IntPtr.Add(memory.Pointer, memory.Capacity), IntPtr.Add(binary, 1) })
    {
        Invalid(() => WindowsServiceInspectionProvider.ReadString(memory.Pointer, memory.Capacity, bad, default));
        Invalid(() => WindowsServiceInspectionProvider.ReadDependencies(memory.Pointer, memory.Capacity, bad, default, out _));
    }
    Invalid(() => WindowsServiceInspectionProvider.ReadString(memory.Pointer, memory.Capacity, IntPtr.Add(memory.Pointer, memory.Capacity - 2), default));
    config.Account = IntPtr.Add(memory.Pointer, memory.Capacity);
    Marshal.StructureToPtr(config, memory.Pointer, false);
    result = WindowsServiceInspectionProvider.Decode("fictional", DateTimeOffset.UtcNow, memory.Pointer, memory.Capacity, default);
    Require(result.Status == InspectionStatus.Partial && result.Account is null && result.BinaryPath is not null && result.Dependencies.Count == 2, "Malformed field did not yield explicit partial data.");
    config.DisplayName = memory.Pointer;
    Marshal.StructureToPtr(config, memory.Pointer, false);
    Require(WindowsServiceInspectionProvider.Decode("fictional", DateTimeOffset.UtcNow, memory.Pointer, memory.Capacity, default).Detail.Contains("display name"), "Unused native pointer was not validated.");
}
using (var memory = new BufferFixture(headerSize + 4))
{
    IntPtr unterminated = memory.Write("x\0");
    Invalid(() => WindowsServiceInspectionProvider.ReadDependencies(memory.Pointer, memory.Capacity, unterminated, default, out _));
    Marshal.WriteInt16(unterminated, 0);
    Require(WindowsServiceInspectionProvider.ReadDependencies(memory.Pointer, memory.Capacity, unterminated, default, out _).Count == 0, "Documented empty dependency string rejected.");
    Invalid(() => WindowsServiceInspectionProvider.Decode("fictional", DateTimeOffset.UtcNow, memory.Pointer, headerSize - 1, default));
}
foreach (int error in new[] { 5, 124, 87 })
{
    var fake = new FakeBackend { ExtensionResponse = (_, _, _, _) => (error, 0) };
    var result = await new WindowsServiceInspectionProvider(fake).CaptureAsync("fictional", default);
    var expected = error switch { 5 => InspectionStatus.AccessDenied, 124 => InspectionStatus.Unsupported, _ => InspectionStatus.Failed };
    Require(result.Status == InspectionStatus.Available && result.Extensions?.DelayedAutoStart.Status == expected &&
        result.Extensions.DelayedAutoStart.IsDelayed is null && result.Extensions.Description.Description is null && result.Extensions.ServiceSid.SidType is null,
        "Extension failure invented values or discarded base configuration.");
}
var extensionTimeout = new FakeBackend();
extensionTimeout.ExtensionResponse = (_, _, _, _) => { extensionTimeout.Elapsed = TimeSpan.FromSeconds(3); return (5, 0); };
var timedExtensions = await new WindowsServiceInspectionProvider(extensionTimeout).CaptureAsync("fictional", default);
Require(timedExtensions.Status == InspectionStatus.Available && timedExtensions.Extensions?.DelayedAutoStart.Status == InspectionStatus.TimedOut &&
    timedExtensions.Extensions.Triggers.Status == InspectionStatus.TimedOut && timedExtensions.Extensions.Description.Status == InspectionStatus.TimedOut &&
    timedExtensions.Extensions.ServiceSid.Status == InspectionStatus.TimedOut && timedExtensions.Dependents?.Status == InspectionStatus.TimedOut && extensionTimeout.ExtensionCalls == 1,
    "Extension cooperative budget did not skip remaining native calls.");
var extensionResize = new FakeBackend { ExtensionResponse = (level, call, _, _) => (122, (uint)(call * 64)) };
var exhausted = await new WindowsServiceInspectionProvider(extensionResize).CaptureAsync("fictional", default);
Require(exhausted.Extensions?.DelayedAutoStart.Status == InspectionStatus.Truncated && extensionResize.ExtensionCalls == 12, "Independent extension retry caps failed.");

using (var memory = new BufferFixture(8192))
{
    IntPtr deps = memory.Write(string.Concat(Enumerable.Repeat("x\0", 257)) + "\0");
    var names = WindowsServiceInspectionProvider.ReadDependencies(memory.Pointer, memory.Capacity, deps, default, out bool truncated);
    Require(truncated && names.Count == 256, "Dependency count cap was not enforced.");
    Marshal.StructureToPtr(new ServiceInspectionConfig { Dependencies = deps }, memory.Pointer, false);
    Require(WindowsServiceInspectionProvider.Decode("fictional", DateTimeOffset.UtcNow, memory.Pointer, memory.Capacity, default).Status == InspectionStatus.Truncated, "Dependency truncation was not reported.");
}

foreach (string name in new[] { "desktop.ini", "Desktop.INI", "DESKTOP.INI" })
{
    Require(!LocalInventory.IncludeStartupEntry(name, true), "Desktop metadata must be excluded from folder inventory.");
    Require(LocalInventory.IncludeStartupEntry(name, false), "Registry value names must not be filtered.");
}
foreach (string name in new[] { "ordinary.lnk", "unknown.data", ".hidden", "desktop.ini.lnk", "desktop.ini ", "desktop" })
    Require(LocalInventory.IncludeStartupEntry(name, true), "Unknown folder entry must remain unresolved inventory.");

foreach (int error in new[] { 5, 1060, 1072, 50, 120, 6 })
{
    var expected = error switch { 5 => InspectionStatus.AccessDenied, 1060 or 1072 => InspectionStatus.Exited, 50 or 120 => InspectionStatus.Unsupported, _ => InspectionStatus.Failed };
    foreach (int stage in new[] { 0, 1, 2 })
    {
        var fake = new FakeBackend { ManagerError = stage == 0 ? error : 0, ServiceError = stage == 1 ? error : 0,
            Response = (_, _, _) => (error, 0) };
        var result = await new WindowsServiceInspectionProvider(fake).CaptureAsync("fictional", default);
        Require(result.Status == expected && result.BinaryPath is null && result.ServiceType is null, "Native failure classification is wrong.");
        Require(fake.ManagerDisposals == (stage == 0 ? 0 : 1) && fake.QueryDisposals == (stage < 2 ? 0 : 1), "Failure leaked a handle.");
        if (error is 1060 or 1072) Require(result.Detail.Contains("missing/deleted"), "Missing service was not explicit.");
    }
}

var oversized = new FakeBackend { Response = (_, _, _) => (122, 8193) };
Require((await new WindowsServiceInspectionProvider(oversized).CaptureAsync("fictional", default)).Status == InspectionStatus.Truncated && oversized.Calls == 1, "Byte cap failed.");
var resizing = new FakeBackend { Response = (call, _, _) => (122, (uint)(headerSize + call * 16)) };
Require((await new WindowsServiceInspectionProvider(resizing).CaptureAsync("fictional", default)).Status == InspectionStatus.Truncated && resizing.Calls == 3, "Query retry cap failed.");
var noProgress = new FakeBackend { Response = (_, _, _) => (122, (uint)headerSize) };
Require((await new WindowsServiceInspectionProvider(noProgress).CaptureAsync("fictional", default)).Status == InspectionStatus.Failed && noProgress.Calls == 2, "Non-progressing resize accepted.");
var shortHeader = new FakeBackend { Response = (_, _, _) => (122, (uint)(headerSize - 1)) };
Require((await new WindowsServiceInspectionProvider(shortHeader).CaptureAsync("fictional", default)).Status == InspectionStatus.Failed, "Short header size accepted.");
var noHeader = new FakeBackend { Response = (_, _, _) => (0, 0) };
Require((await new WindowsServiceInspectionProvider(noHeader).CaptureAsync("fictional", default)).Status == InspectionStatus.Failed, "Success without a buffer accepted.");
var success = new FakeBackend();
var complete = await new WindowsServiceInspectionProvider(success).CaptureAsync("fictional", default);
Require(complete.Status == InspectionStatus.Available && complete.ServiceType == 0x10 && success.Calls == 2 && success.QueryDisposals == 1 && success.ManagerDisposals == 1, "Successful query/cleanup failed.");
var delayed = new FakeBackend();
delayed.Response = (_, _, _) => { delayed.Elapsed = TimeSpan.FromSeconds(3); return (122, (uint)headerSize); };
Require((await new WindowsServiceInspectionProvider(delayed).CaptureAsync("fictional", default)).Status == InspectionStatus.TimedOut && delayed.Calls == 1 && delayed.QueryDisposals == 1, "Cooperative budget/cleanup failed.");
using (var source = new CancellationTokenSource())
{
    source.Cancel();
    var fake = new FakeBackend();
    await Canceled(() => new WindowsServiceInspectionProvider(fake).CaptureAsync("fictional", source.Token));
    Require(fake.ManagerOpens == 0, "Pre-cancellation opened SCM.");
}
using (var source = new CancellationTokenSource())
{
    var fake = new FakeBackend { Response = (_, _, _) => { source.Cancel(); return (122, (uint)headerSize); } };
    await Canceled(() => new WindowsServiceInspectionProvider(fake).CaptureAsync("fictional", source.Token));
    Require(fake.ManagerDisposals == 1 && fake.QueryDisposals == 1 && fake.Calls == 1, "In-call cancellation did not clean up.");
}
foreach (string name in new[] { "", "bad\0name", "a/b", "a\\b", new string('a', 257) })
{
    var fake = new FakeBackend();
    Require((await new WindowsServiceInspectionProvider(fake).CaptureAsync(name, default)).Status == InspectionStatus.Unsupported && fake.ManagerOpens == 0, "Invalid service name reached native code.");
}

using (var memory = new BufferFixture(8192))
{
    Marshal.WriteInt32(memory.Pointer, 1);
    Require(WindowsServiceInspectionProvider.DecodeDelayedAutoStart(memory.Pointer, 4, default).IsDelayed == true, "Delayed true lost.");
    Marshal.WriteInt32(memory.Pointer, 0);
    Require(WindowsServiceInspectionProvider.DecodeDelayedAutoStart(memory.Pointer, 4, default).IsDelayed == false, "Delayed false lost.");
    Invalid(() => WindowsServiceInspectionProvider.DecodeDelayedAutoStart(memory.Pointer, 3, default));
    foreach (uint sid in new uint[] { 0, 1, 3, 99 })
    {
        Marshal.WriteInt32(memory.Pointer, (int)sid);
        var result = WindowsServiceInspectionProvider.DecodeSid(memory.Pointer, 4, default);
        Require(result.SidType == sid && (sid == 99 ? result.SidTypeName is null : result.SidTypeName is not null), "SID metadata wrong.");
    }
    var description = memory.Write(new string('x', 2050) + "\0");
    Marshal.WriteIntPtr(memory.Pointer, description);
    var text = WindowsServiceInspectionProvider.DecodeDescription(memory.Pointer, memory.Capacity, default);
    Require(text.Status == InspectionStatus.Truncated && text.Description?.Length == 2048, "Description cap ignored.");
    Marshal.WriteIntPtr(memory.Pointer, IntPtr.Zero);
    Require(WindowsServiceInspectionProvider.DecodeDescription(memory.Pointer, memory.Capacity, default).Description is null, "Absent description invented.");
    Marshal.WriteIntPtr(memory.Pointer, IntPtr.Add(memory.Pointer, memory.Capacity));
    Invalid(() => WindowsServiceInspectionProvider.DecodeDescription(memory.Pointer, memory.Capacity, default));
}
using (var memory = new BufferFixture(16384))
{
    int stride = Marshal.SizeOf<ServiceTriggerEntry>();
    IntPtr array = IntPtr.Add(memory.Pointer, 128);
    IntPtr guid = IntPtr.Add(memory.Pointer, 4096);
    Marshal.StructureToPtr(new Guid("4f27f2de-14e2-430b-a549-7cd48cbc8245"), guid, false);
    Marshal.StructureToPtr(new ServiceTriggerEntry { Type = 2, Action = 1, Subtype = guid, DataCount = 2, Data = IntPtr.Add(memory.Pointer, 8192) }, array, false);
    Marshal.StructureToPtr(new ServiceTriggerEntry { Type = 20, Action = 2, Subtype = IntPtr.Add(guid, 16) }, IntPtr.Add(array, stride), false);
    Marshal.StructureToPtr(Guid.Empty, IntPtr.Add(guid, 16), false);
    Marshal.StructureToPtr(new ServiceTriggerInfo { Count = 2, Triggers = array }, memory.Pointer, false);
    var triggers = WindowsServiceInspectionProvider.DecodeTriggers(memory.Pointer, memory.Capacity, default);
    Require(triggers.Status == InspectionStatus.Available && triggers.Triggers.Count == 2 && triggers.Triggers[0].SubtypeName == "NETWORK_MANAGER_FIRST_IP_ADDRESS_ARRIVAL" &&
        triggers.Triggers[0].DataItemCount == 2 && triggers.Triggers[1].SubtypeName is null && triggers.Triggers[1].ActionName == "STOP", "Trigger metadata wrong.");
    for (int i = 0; i < 65; i++) Marshal.StructureToPtr(new ServiceTriggerEntry { Type = 1, Action = 1 }, IntPtr.Add(array, i * stride), false);
    Marshal.StructureToPtr(new ServiceTriggerInfo { Count = 65, Triggers = array }, memory.Pointer, false);
    Require(WindowsServiceInspectionProvider.DecodeTriggers(memory.Pointer, memory.Capacity, default) is { Status: InspectionStatus.Truncated, Triggers.Count: 64 }, "Trigger cap ignored.");
    Marshal.StructureToPtr(new ServiceTriggerInfo { Count = 1, Triggers = IntPtr.Add(memory.Pointer, memory.Capacity) }, memory.Pointer, false);
    Invalid(() => WindowsServiceInspectionProvider.DecodeTriggers(memory.Pointer, memory.Capacity, default));
    Marshal.StructureToPtr(new ServiceTriggerInfo(), memory.Pointer, false);
    Require(WindowsServiceInspectionProvider.DecodeTriggers(memory.Pointer, memory.Capacity, default).Triggers.Count == 0, "Empty triggers wrong.");
}
using (var memory = new BufferFixture(65536))
{
    int stride = Marshal.SizeOf<ServiceDependentEntry>();
    IntPtr name = IntPtr.Add(memory.Pointer, 60000);
    Marshal.Copy(Encoding.Unicode.GetBytes("fictional\0"), 0, name, 20);
    for (int i = 0; i < 513; i++) Marshal.StructureToPtr(new ServiceDependentEntry { Name = name, Type = 16, State = 4 }, IntPtr.Add(memory.Pointer, i * stride), false);
    Require(WindowsServiceInspectionProvider.DecodeDependents(memory.Pointer, memory.Capacity, 513, default) is { Status: InspectionStatus.Truncated, Services.Count: 512 }, "Dependent cap ignored.");
    Require(WindowsServiceInspectionProvider.DecodeDependents(IntPtr.Zero, 0, 0, default).Services.Count == 0, "Empty dependent response rejected.");
    Marshal.StructureToPtr(new ServiceDependentEntry { Name = IntPtr.Add(memory.Pointer, memory.Capacity) }, memory.Pointer, false);
    Invalid(() => WindowsServiceInspectionProvider.DecodeDependents(memory.Pointer, memory.Capacity, 1, default));
}

ServiceGraphInput[] graphRows = [new("A", ["b", "+G", "missing"], "H"), new("B", ["A"], "G"),
    new("C", ["a"], "G"), new("D", ["C"], null), new("E", ["+g"], null), new("F", ["E"], null), new("Y", ["+H"], null)];
var graph = ServiceGraph.Build("a", graphRows);
Require(graph.Status == InspectionStatus.Available && graph.Root == "A" && graph.DependsOn.Any(e => e.From == "A" && e.To == "B"), "Forward graph wrong.");
Require(graph.Dependents.Any(e => e.From == "D" && e.To == "C") && !graph.Dependents.Any(e => e.From is "E" or "F") &&
    graph.Dependents.Any(e => e.From == "Y" && e.To == "H" && e.ToKind == ServiceGraphNodeKind.Group), "Transitive reverse/group graph wrong.");
Require(graph.Nodes.Single(n => n.Kind == ServiceGraphNodeKind.Group && n.Name == "G").GroupMembers.SequenceEqual(new[] { "B", "C" }) &&
    !graph.DependsOn.Any(e => e.From == "G"), "Group leaf semantics wrong.");
Require(graph.Nodes.Any(n => n.Name == "missing" && !n.InInventory), "Missing reference omitted.");
Require(graph.ObservedCycles.Count == 1 && graph.ObservedCycles[0].Path.SequenceEqual(new[] { "A", "B", "A" }) &&
    graph.Detail.Contains("observed configuration cycle", StringComparison.OrdinalIgnoreCase) && !graph.Detail.Contains("deadlock", StringComparison.OrdinalIgnoreCase), "Cycle semantics wrong.");
Require(ServiceGraph.Build("A", graphRows, maximumDepth: 0).Status == InspectionStatus.Truncated && ServiceGraph.Build("A", graphRows, maximumNodes: 1).Status == InspectionStatus.Truncated, "Graph bounds ignored.");
Require(ServiceGraph.Build("absent", []).Nodes.Single().InInventory == false && ServiceGraph.Build("", []).Status == InspectionStatus.Unsupported, "Empty/missing graph wrong.");
Require(graph.Nodes.Select(n => n.Name).SequenceEqual(ServiceGraph.Build("A", graphRows.Reverse().ToArray()).Nodes.Select(n => n.Name)), "Graph order not deterministic.");
using (var source = new CancellationTokenSource())
{
    source.Cancel();
    await Canceled(() => Task.Run(() => ServiceGraph.Build("A", graphRows, cancellationToken: source.Token)));
}

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("This smoke requires Windows for real local SCM verification.");
var provider = new WindowsServiceInspectionProvider();
foreach (string name in new[] { "EventLog", "Dnscache", "Winmgmt", "SystemExplorer-Nonexistent-4698c219" })
{
    var result = await provider.CaptureAsync(name, default);
    if (name.StartsWith("SystemExplorer-Nonexistent")) { Require(result.Status == InspectionStatus.Exited, "Missing real service not Exited."); continue; }
    Require(result.Status is InspectionStatus.Available or InspectionStatus.Partial or InspectionStatus.Truncated or InspectionStatus.AccessDenied or InspectionStatus.Exited or InspectionStatus.TimedOut or InspectionStatus.Failed or InspectionStatus.Unsupported, "Dishonest live status.");
    if (result.Extensions is { } extensions)
    {
        Require(extensions.DelayedAutoStart.Status != InspectionStatus.Available || extensions.DelayedAutoStart.IsDelayed is not null, "Available delayed observation lacks value.");
        Require(extensions.ServiceSid.Status != InspectionStatus.Available || extensions.ServiceSid.SidType is not null, "Available SID observation lacks value.");
        Require(extensions.Triggers.Triggers is not null && result.Dependents?.Services is not null, "Missing observed collections.");
    }
}
int observed = 0, inaccessible = 0;
foreach (string serviceName in EnumerateLocalServiceNames())
{
    var result = await provider.CaptureAsync(serviceName, default);
    if (result.Status == InspectionStatus.Available)
    {
        Require(result.Name == serviceName && result.ServiceType is not null && result.StartType is not null && result.ErrorControl is not null, "Real SCM header unavailable.");
        observed++;
        break;
    }
    if (result.Status is InspectionStatus.AccessDenied or InspectionStatus.Exited) { inaccessible++; continue; }
    throw new InvalidOperationException($"Real local service capture returned {result.Status}.");
}
Require(observed == 1, "No enumerated local service configuration could be observed with this token.");
Console.WriteLine($"PASS: {checks} assertions; native local configurations={observed}, inaccessible/missing candidates={inaccessible}. No metadata values logged.");

static IReadOnlyList<string> EnumerateLocalServiceNames()
{
    using var manager = NativeMethods.OpenSCManagerW(null, null, 0x5);
    if (manager.IsInvalid) throw new InvalidOperationException($"SCM enumeration unavailable: {Marshal.GetLastWin32Error()}.");
    const int capacity = 256 * 1024;
    IntPtr buffer = Marshal.AllocHGlobal(capacity);
    try
    {
        uint resume = 0;
        bool ok = NativeMethods.EnumServicesStatusExW(manager, 0, 0x30, 3, buffer, capacity, out _, out uint count, ref resume, null);
        int error = ok ? 0 : Marshal.GetLastWin32Error();
        if (!ok && error != 234) throw new InvalidOperationException($"Local service enumeration failed: {error}.");
        int stride = Marshal.SizeOf<NativeMethods.ServiceEntry>();
        if (count > capacity / stride) throw new InvalidDataException("Service count exceeds owned enumeration buffer.");
        var names = new List<string>();
        for (int i = 0; i < Math.Min(count, 32); i++)
        {
            var row = Marshal.PtrToStructure<NativeMethods.ServiceEntry>(IntPtr.Add(buffer, i * stride));
            names.Add(WindowsRelationshipProvider.ReadServiceString(buffer, capacity, row.Name));
        }
        return names;
    }
    finally { Marshal.FreeHGlobal(buffer); }
}

sealed class BufferFixture : IDisposable
{
    public IntPtr Pointer { get; }
    public int Capacity { get; }
    private int offset = Marshal.SizeOf<ServiceInspectionConfig>();
    public BufferFixture(int capacity)
    {
        Capacity = capacity;
        Pointer = Marshal.AllocHGlobal(capacity);
        Marshal.Copy(Enumerable.Repeat((byte)0xff, capacity).ToArray(), 0, Pointer, capacity);
    }
    public IntPtr Write(string value)
    {
        byte[] bytes = Encoding.Unicode.GetBytes(value);
        if (bytes.Length > Capacity - offset) throw new InvalidOperationException("Fixture overflow.");
        IntPtr result = IntPtr.Add(Pointer, offset);
        Marshal.Copy(bytes, 0, result, bytes.Length);
        offset += bytes.Length;
        return result;
    }
    public void Dispose() => Marshal.FreeHGlobal(Pointer);
}

sealed class FakeBackend : IServiceInspectionBackend
{
    public int ManagerError, ServiceError, Calls, ManagerOpens, ManagerDisposals, QueryDisposals;
    public int ExtensionCalls;
    public Func<uint, int, IntPtr, uint, (int Error, uint Needed)> ExtensionResponse = (_, _, _, _) => (124, 0);
    public TimeSpan Elapsed;
    public Func<int, IntPtr, uint, (int Error, uint Needed)> Response = (_, buffer, capacity) =>
    {
        if (capacity == 0) return (122, (uint)Marshal.SizeOf<ServiceInspectionConfig>());
        Marshal.StructureToPtr(new ServiceInspectionConfig { ServiceType = 0x10, StartType = 3, ErrorControl = 1 }, buffer, false);
        return (0, 0); // BytesNeeded is only defined on insufficient-buffer failure.
    };
    public long GetTimestamp() => 0;
    public TimeSpan GetElapsedTime(long started) => Elapsed;
    public IServiceInspectionManager? OpenManager(out int error)
    {
        ManagerOpens++;
        error = ManagerError;
        return error == 0 ? new Manager(this) : null;
    }
    private sealed class Manager(FakeBackend owner) : IServiceInspectionManager
    {
        public IServiceInspectionQuery? OpenService(string name, out int error)
        {
            error = owner.ServiceError;
            return error == 0 ? new QueryHandle(owner) : null;
        }
        public void Dispose() => owner.ManagerDisposals++;
    }
    private sealed class QueryHandle(FakeBackend owner) : IServiceInspectionQuery
    {
        public int QueryExtension(uint level, IntPtr buffer, uint capacity, out uint needed)
        {
            var response = owner.ExtensionResponse(level, ++owner.ExtensionCalls, buffer, capacity);
            needed = response.Needed;
            return response.Error;
        }
        public int Query(IntPtr buffer, uint capacity, out uint needed)
        {
            var response = owner.Response(++owner.Calls, buffer, capacity);
            needed = response.Needed;
            return response.Error;
        }
        public void Dispose() => owner.QueryDisposals++;
    }
}
