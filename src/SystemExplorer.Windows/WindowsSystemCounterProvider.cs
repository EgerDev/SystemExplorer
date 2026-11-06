using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using SystemExplorer.Core;

namespace SystemExplorer.Windows;

internal enum CounterSection { Disk, Cpu, Memory, Gpu }
internal interface ISystemCounterSampler : IDisposable
{
    CounterSample Sample(CounterSection section, CancellationToken token, Func<bool> expired);
}
internal interface IPdhCounterQuery : IDisposable
{
    uint AddCounter(string path);
    uint Collect();
    PdhArrayResult ReadArray(string path, int maximumItems);
    PdhSingleResult ReadSingle(string path);
}
internal interface IInterfaceTableSource { InterfaceSample Read(); }
internal interface IProcessCreationReader
{
    ProcessCreationResult Read(int pid);
    ProcessImageResult ReadImage(int pid);
}
internal interface IDxgiInventorySource { DxgiAdapterResult Read(); }
internal interface ISystemCounterClock
{
    DateTimeOffset UtcNow { get; }
    long Timestamp { get; }
    TimeSpan Elapsed(long start, long end);
}
internal sealed record InterfaceSample(IfTableResult Table, long Timestamp);
internal sealed record CounterSample(bool IsWarmup, IReadOnlyDictionary<string, PdhArrayResult> Arrays,
    IReadOnlyDictionary<string, PdhSingleResult> Singles, bool TimedOut = false)
{
    internal static CounterSample Timeout => new(false, new Dictionary<string, PdhArrayResult>(), new Dictionary<string, PdhSingleResult>(), true);
}

internal static class SystemCounterPaths
{
    internal static readonly string[] DiskNames = ["Avg. Disk sec/Read", "Avg. Disk sec/Write", "Disk Reads/sec", "Disk Writes/sec",
        "Current Disk Queue Length", "Disk Read Bytes/sec", "Disk Write Bytes/sec", "% Idle Time"];
    internal static readonly string[] CpuNames = ["% Processor Utility", "% Privileged Time"];
    internal static readonly string[] MemoryNames = ["Standby Cache Reserve Bytes", "Standby Cache Normal Priority Bytes", "Standby Cache Core Bytes",
        "Modified Page List Bytes", "Free & Zero Page List Bytes", "Page Faults/sec", "Page Reads/sec", "Pages Input/sec"];
    internal const string Context = @"\System\Context Switches/sec", Queue = @"\System\Processor Queue Length",
        CompressionWorkingSet = @"\Process(MemCompression)\Working Set", CompressionPid = @"\Process(MemCompression)\ID Process",
        Engine = @"\GPU Engine(*)\Utilization Percentage", AdapterDedicated = @"\GPU Adapter Memory(*)\Dedicated Usage",
        AdapterShared = @"\GPU Adapter Memory(*)\Shared Usage", ProcessDedicated = @"\GPU Process Memory(*)\Dedicated Usage",
        ProcessShared = @"\GPU Process Memory(*)\Shared Usage";
    internal static string Disk(string name) => @"\PhysicalDisk(*)\" + name;
    internal static string Cpu(string name) => @"\Processor Information(*)\" + name;
    internal static string Memory(string name) => @"\Memory\" + name;
    internal static string[] Arrays(CounterSection section) => section switch
    {
        CounterSection.Disk => DiskNames.Select(Disk).ToArray(),
        CounterSection.Cpu => CpuNames.Select(Cpu).ToArray(),
        CounterSection.Gpu => [Engine, AdapterDedicated, AdapterShared, ProcessDedicated, ProcessShared],
        _ => []
    };
    internal static string[] Singles(CounterSection section) => section switch
    {
        CounterSection.Cpu => [Context, Queue],
        CounterSection.Memory => [.. MemoryNames.Select(Memory), CompressionWorkingSet, CompressionPid],
        _ => []
    };
}

internal sealed class SystemCounterClock : ISystemCounterClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    public long Timestamp => Stopwatch.GetTimestamp();
    public TimeSpan Elapsed(long start, long end) => Stopwatch.GetElapsedTime(start, end);
}
internal sealed class InterfaceTableSource(ISystemCounterClock clock) : IInterfaceTableSource
{
    public InterfaceSample Read()
    {
        var table = NativeCounterMethods.ReadInterfaces();
        return new(table, clock.Timestamp);
    }
}
internal sealed class ProcessCreationReader : IProcessCreationReader
{
    public ProcessCreationResult Read(int pid) => NativeCounterMethods.ReadProcessCreationTime(pid);
    public ProcessImageResult ReadImage(int pid) => NativeCounterMethods.ReadProcessImage(pid);
}
internal sealed class DxgiInventorySource : IDxgiInventorySource
{
    public DxgiAdapterResult Read() => DxgiAdapterReader.Read();
}

internal sealed class SystemCounterSampler : ISystemCounterSampler
{
    private sealed class QueryState(IPdhCounterQuery query) : IDisposable
    {
        internal readonly IPdhCounterQuery Query = query;
        internal readonly Dictionary<string, uint> Added = new(StringComparer.Ordinal);
        internal bool Collected;
        public void Dispose() => Query.Dispose();
    }
    private readonly Dictionary<CounterSection, QueryState> queries = [];
    private readonly Func<IPdhCounterQuery> createQuery;

    internal SystemCounterSampler(Func<IPdhCounterQuery>? createQuery = null) => this.createQuery = createQuery ?? (() => new PdhCounterQuery());

    public CounterSample Sample(CounterSection section, CancellationToken token, Func<bool> expired)
    {
        token.ThrowIfCancellationRequested();
        if (expired()) return CounterSample.Timeout;
        if (!queries.TryGetValue(section, out var state))
        {
            state = new(createQuery());
            try
            {
                foreach (var path in SystemCounterPaths.Arrays(section).Concat(SystemCounterPaths.Singles(section)))
                {
                    token.ThrowIfCancellationRequested();
                    if (expired()) { state.Dispose(); return CounterSample.Timeout; }
                    state.Added[path] = state.Query.AddCounter(path);
                }
                queries.Add(section, state);
            }
            catch { state.Dispose(); throw; }
        }
        var warmup = !state.Collected;
        var arrays = new Dictionary<string, PdhArrayResult>(StringComparer.Ordinal);
        var singles = new Dictionary<string, PdhSingleResult>(StringComparer.Ordinal);
        var rebuild = false;
        var timedOut = false;
        try
        {
            var collect = state.Query.Collect();
            state.Collected = true;
            foreach (var path in SystemCounterPaths.Arrays(section))
            {
                token.ThrowIfCancellationRequested();
                if (expired()) { timedOut = true; break; }
                var add = state.Added[path];
                var result = add != 0 ? new PdhArrayResult(add, [], false) : collect != 0
                    ? new PdhArrayResult(collect, [], false) : state.Query.ReadArray(path, section == CounterSection.Gpu ? 65536 : 257);
                arrays[path] = result;
                if (result.FunctionStatus != 0 && !(warmup && WindowsSystemCounterProvider.IsWarmupStatus(result.FunctionStatus)) &&
                    !(result.FunctionStatus == NativeCounterMethods.PDH_MORE_DATA && result.Truncated)) rebuild = true;
            }
            foreach (var path in SystemCounterPaths.Singles(section))
            {
                token.ThrowIfCancellationRequested();
                if (expired()) { timedOut = true; break; }
                var add = state.Added[path];
                singles[path] = add != 0 ? new(add, add, null) : collect != 0 ? new(collect, collect, null) : state.Query.ReadSingle(path);
            }
            return new(warmup, arrays, singles, timedOut);
        }
        finally
        {
            if (rebuild || timedOut) { queries.Remove(section); state.Dispose(); }
        }
    }

    public void Dispose()
    {
        foreach (var query in queries.Values) query.Dispose();
        queries.Clear();
    }
}

public sealed class WindowsSystemCounterProvider : ISystemCounterProvider
{
    internal const int MaximumRows = 256;
    internal const string WarmupDetail = "warmup sample; rates available from the next refresh";
    private readonly ISystemCounterSampler sampler;
    private readonly IInterfaceTableSource interfaces;
    private readonly IProcessCreationReader processes;
    private readonly IDxgiInventorySource inventorySource;
    private readonly ISystemCounterClock clock;
    private readonly SemaphoreSlim gate = new(1, 1);
    private int disposed;
    private long? previousCapture;
    private InterfaceSample? previousInterfaces;
    private Dictionary<int, ProcessCreationResult> previousIdentities = [];
    private DxgiAdapterResult? inventory;
    private long? inventoryTimestamp;

    public WindowsSystemCounterProvider()
    {
        clock = new SystemCounterClock();
        sampler = new SystemCounterSampler();
        interfaces = new InterfaceTableSource(clock);
        processes = new ProcessCreationReader();
        inventorySource = new DxgiInventorySource();
    }

    internal WindowsSystemCounterProvider(ISystemCounterSampler sampler, IInterfaceTableSource interfaces,
        IProcessCreationReader processes, IDxgiInventorySource inventory, ISystemCounterClock clock)
    {
        this.sampler = sampler;
        this.interfaces = interfaces;
        this.processes = processes;
        inventorySource = inventory;
        this.clock = clock;
    }

    public Task<SystemCounterSnapshot> CaptureAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        return Task.Run(async () =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
                return Capture(cancellationToken);
            }
            finally { gate.Release(); }
        }, cancellationToken);
    }

    private SystemCounterSnapshot Capture(CancellationToken token)
    {
        var start = clock.Timestamp;
        var captured = clock.UtcNow;
        var first = previousCapture is null;
        bool Expired() => clock.Elapsed(start, clock.Timestamp) >= TimeSpan.FromSeconds(3);
        CounterSample Sample(CounterSection section)
        {
            token.ThrowIfCancellationRequested();
            if (Expired()) return CounterSample.Timeout;
            try
            {
                var result = sampler.Sample(section, token, Expired);
                return first ? result with { IsWarmup = true } : result;
            }
            catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or InvalidOperationException)
            {
                return new(first, SystemCounterPaths.Arrays(section).ToDictionary(p => p,
                    _ => new PdhArrayResult(NativeCounterMethods.PDH_INVALID_DATA, [], false)),
                    SystemCounterPaths.Singles(section).ToDictionary(p => p, _ => new PdhSingleResult(NativeCounterMethods.PDH_INVALID_DATA, NativeCounterMethods.PDH_INVALID_DATA, null)));
            }
        }
        var disks = NormalizeDisks(Sample(CounterSection.Disk));
        token.ThrowIfCancellationRequested();
        InterfaceSample? networkSample = null;
        if (!Expired()) networkSample = interfaces.Read();
        var network = networkSample is null ? NetworkTimeout() : NormalizeNetwork(networkSample, previousInterfaces, clock);
        var cpu = NormalizeCpu(Sample(CounterSection.Cpu));
        var memorySample = Sample(CounterSection.Memory);
        ProcessImageResult? image = null;
        var compressionPid = Single(memorySample, SystemCounterPaths.CompressionPid, false);
        if (!Expired() && compressionPid.State == CounterValueState.Measured && compressionPid.Value is > 0 and <= int.MaxValue &&
            compressionPid.Value == Math.Truncate(compressionPid.Value.Value)) image = processes.ReadImage((int)compressionPid.Value.Value);
        var memory = NormalizeMemory(memorySample, image);
        if (Expired() && compressionPid.State == CounterValueState.Measured && image is null)
            memory = memory with { Status = InspectionStatus.TimedOut, CompressionStatus = InspectionStatus.TimedOut,
                CompressionDetail = "Capture budget exceeded before store attribution.", Detail = memory.Detail + " Capture budget exceeded before store attribution." };
        var gpuSample = Sample(CounterSection.Gpu);
        token.ThrowIfCancellationRequested();
        var gpuInputs = ParseGpu(gpuSample);
        var nextIdentities = new Dictionary<int, ProcessCreationResult>();
        var identityTimedOut = false;
        foreach (var pid in gpuInputs.ProcessKeys.Take(MaximumRows).Select(k => k.Pid).Distinct())
        {
            token.ThrowIfCancellationRequested();
            if (Expired()) { identityTimedOut = true; break; }
            nextIdentities[pid] = processes.Read(pid);
        }
        var unknownLuid = gpuInputs.AdapterLuids.Any(luid => inventory is null || !inventory.Adapters.Any(a => a.Luid == luid));
        if (!Expired() && (inventory is null || (unknownLuid && inventoryTimestamp is { } last && clock.Elapsed(last, clock.Timestamp) >= TimeSpan.FromSeconds(30))))
        {
            inventory = inventorySource.Read();
            inventoryTimestamp = clock.Timestamp;
        }
        identityTimedOut |= Expired();
        var gpu = NormalizeGpu(gpuSample, gpuInputs, nextIdentities, previousIdentities,
            inventory ?? new(DxgiInventoryStatus.Failed, unchecked((int)0x800705B4), [], false), identityTimedOut);
        var sections = new (string Name, InspectionStatus Status)[] { ("Disks", disks.Status), ("Network", network.Status), ("CPU", cpu.Status), ("Memory", memory.Status), ("GPU", gpu.Status) };
        var status = sections.All(s => s.Status == InspectionStatus.Available) ? InspectionStatus.Available :
            sections.All(s => s.Status == InspectionStatus.Failed) ? InspectionStatus.Failed : InspectionStatus.Partial;
        var detail = status == InspectionStatus.Available ? "All system counter sections available." :
            string.Join("; ", sections.Where(s => s.Status != InspectionStatus.Available).Select(s => $"{s.Name}: {s.Status}"));
        token.ThrowIfCancellationRequested();
        var snapshot = new SystemCounterSnapshot(captured, first, previousCapture is { } previous ? clock.Elapsed(previous, start) : null,
            status, detail, disks, network, cpu, memory, gpu);
        previousCapture = start;
        previousInterfaces = networkSample is { Table.Status: 0 } ? networkSample : null;
        previousIdentities = nextIdentities;
        return snapshot;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        gate.Wait();
        try { sampler.Dispose(); }
        finally { gate.Release(); }
    }

    internal static bool IsWarmupStatus(uint status) => status is NativeCounterMethods.PDH_CSTATUS_INVALID_DATA or NativeCounterMethods.PDH_INVALID_DATA or NativeCounterMethods.PDH_NO_DATA;
    private static CounterValue Missing(string reason) => CounterValue.Unavailable(reason);
    private static CounterValue Warmup() => CounterValue.Warmup(WarmupDetail);
    internal static CounterValue NormalizeValue(uint functionStatus, uint cStatus, double? value, bool warmup, bool rate)
    {
        if (functionStatus != 0)
            return warmup && IsWarmupStatus(functionStatus) ? Warmup() : Missing($"PDH function status 0x{functionStatus:X8}; item status 0x{cStatus:X8}");
        if (cStatus > 1) return Missing($"PDH item status 0x{cStatus:X8}");
        if (warmup && rate) return Warmup();
        return value is { } measured && double.IsFinite(measured) ? CounterValue.Measured(measured) : Missing("PDH returned no finite value");
    }

    private static CounterValue Single(CounterSample sample, string path, bool rate)
    {
        if (sample.TimedOut) return Missing("capture budget exceeded");
        return sample.Singles.TryGetValue(path, out var value) ? NormalizeValue(value.FunctionStatus, value.CStatus, value.Value, sample.IsWarmup, rate) : Missing("counter not read");
    }
    private static CounterValue ArrayValue(CounterSample sample, string path, string instance, bool rate)
    {
        if (sample.TimedOut) return Missing("capture budget exceeded");
        if (!sample.Arrays.TryGetValue(path, out var array)) return Missing("counter not read");
        if (array.FunctionStatus != 0) return NormalizeValue(array.FunctionStatus, 0, null, sample.IsWarmup, rate);
        var item = array.Items.FirstOrDefault(i => i.Instance == instance);
        return item is null ? Missing($"instance not present (0x{NativeCounterMethods.PDH_CSTATUS_NO_INSTANCE:X8})") : NormalizeValue(0, item.CStatus, item.Value, sample.IsWarmup, rate);
    }
    private static string[] Instances(CounterSample sample) => sample.Arrays.Values.Where(a => a.FunctionStatus == 0)
        .SelectMany(a => a.Items).Select(i => i.Instance).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
    private static InspectionStatus SectionStatus(CounterSample sample, IEnumerable<CounterValue> values, bool truncated = false)
    {
        if (sample.TimedOut) return InspectionStatus.TimedOut;
        if (truncated || sample.Arrays.Values.Any(a => a.Truncated)) return InspectionStatus.Truncated;
        if (sample.IsWarmup) return InspectionStatus.Partial;
        var all = values.ToArray();
        if (all.Length == 0) return sample.Arrays.Values.Any(a => a.FunctionStatus != 0) ? InspectionStatus.Failed : InspectionStatus.Partial;
        return all.All(v => v.State is CounterValueState.Measured or CounterValueState.NoActivityInInterval) ? InspectionStatus.Available :
            all.All(v => v.State == CounterValueState.Unavailable) ? InspectionStatus.Failed : InspectionStatus.Partial;
    }
    private static string Detail(CounterSample sample, InspectionStatus status, string detail) => detail +
        (sample.IsWarmup ? " " + WarmupDetail + "." : "") + (status == InspectionStatus.Truncated ? " Instance cap 256 or native buffer limit reached; incomplete inventory." : "") +
        (status == InspectionStatus.TimedOut ? " Capture budget exceeded; unfinished values unavailable." : "") +
        (status is InspectionStatus.Partial or InspectionStatus.Failed && !sample.IsWarmup ? " Some counters unavailable; see value reasons." : "") +
        string.Concat(sample.Arrays.Where(a => a.Value.FunctionStatus != 0).Select(a => $" {a.Key}: 0x{a.Value.FunctionStatus:X8}."));

    internal static DiskCounterSection NormalizeDisks(CounterSample sample)
    {
        var names = Instances(sample);
        var rows = new List<DiskCounterRow>();
        foreach (var name in names.Where(n => n != "_Total").Take(MaximumRows).Concat(names.Contains("_Total") ? ["_Total"] : Array.Empty<string>()))
        {
            CounterValue Read(string counter, bool rate = true) => ArrayValue(sample, SystemCounterPaths.Disk(counter), name, rate);
            var reads = Read("Disk Reads/sec");
            var writes = Read("Disk Writes/sec");
            CounterValue Latency(CounterValue operations, string path, string activity)
            {
                if (operations.State != CounterValueState.Measured) return operations.State == CounterValueState.Warmup ? Warmup() : Missing($"{activity} rate unavailable: {operations.Reason}");
                if (operations.Value == 0) return CounterValue.NoActivity($"no {activity} completed in interval");
                var latency = Read(path);
                return latency.State == CounterValueState.Measured ? CounterValue.Measured(latency.Value!.Value * 1000) : latency;
            }
            rows.Add(new(name, name == "_Total", Latency(reads, "Avg. Disk sec/Read", "reads"), Latency(writes, "Avg. Disk sec/Write", "writes"),
                reads, writes, Read("Current Disk Queue Length", false), Read("Disk Read Bytes/sec"), Read("Disk Write Bytes/sec"), Read("% Idle Time")));
        }
        var status = SectionStatus(sample, rows.SelectMany(r => new[] { r.ReadLatencyMilliseconds, r.WriteLatencyMilliseconds, r.ReadsPerSecond, r.WritesPerSecond,
            r.CurrentQueueLength, r.ReadBytesPerSecond, r.WriteBytesPerSecond, r.IdleTimePercent }), names.Count(n => n != "_Total") > MaximumRows);
        return new(status, Detail(sample, status, "Aggregate disk values are system-wide, not process attribution."), rows.FirstOrDefault(r => r.IsAggregate), rows.Where(r => !r.IsAggregate).ToArray());
    }

    private static NetworkCounterSection NetworkTimeout() => new(InspectionStatus.TimedOut, "Capture budget exceeded before interface table read.", [], Missing("capture budget exceeded"), Missing("capture budget exceeded"), null);
    internal static NetworkCounterSection NormalizeNetwork(InterfaceSample current, InterfaceSample? previous, ISystemCounterClock clock)
    {
        const string detail = "Virtual/filter/loopback rows are listed but excluded from totals; only hardware interfaces are included.";
        if (current.Table.Status != 0) return new(InspectionStatus.Failed, $"GetIfTable2 failed: {current.Table.Status}. {detail}", [], Missing("interface table unavailable"), Missing("interface table unavailable"), null);
        var elapsed = previous is { Table.Status: 0 } ? clock.Elapsed(previous.Timestamp, current.Timestamp) : (TimeSpan?)null;
        var old = previous?.Table.Rows.GroupBy(r => r.Index).ToDictionary(g => g.Key, g => g.First()) ?? [];
        var rows = current.Table.Rows.Take(MaximumRows).Select(row =>
        {
            CounterValue Rate(bool receive)
            {
                if (!old.TryGetValue(row.Index, out var prior)) return Warmup();
                if (prior.Luid != row.Luid) return Missing("interface identity changed");
                var now = receive ? row.InOctets : row.OutOctets;
                var then = receive ? prior.InOctets : prior.OutOctets;
                if (now < then) return Missing("counter decreased/reset");
                if (elapsed is null || elapsed.Value.TotalSeconds <= 0) return Missing("non-positive interface sample elapsed time");
                return CounterValue.Measured((now - then) / elapsed.Value.TotalSeconds);
            }
            var kind = (row.Flags & 2) != 0 ? NetworkInterfaceKind.Filter : row.Type == 24 ? NetworkInterfaceKind.Loopback :
                row.Type == 131 || row.TunnelType != 0 ? NetworkInterfaceKind.Tunnel : (row.Flags & 1) != 0 ? NetworkInterfaceKind.Hardware : NetworkInterfaceKind.Virtual;
            static ulong? Speed(ulong speed) => speed is 0 or ulong.MaxValue ? null : speed;
            return new NetworkAdapterRow(row.Index, row.Luid, row.Alias, row.Description, row.Type, row.OperStatus switch
            {
                1 => "Up", 2 => "Down", 3 => "Testing", 4 => "Unknown", 5 => "Dormant", 6 => "NotPresent", 7 => "LowerLayerDown", _ => $"Unknown ({row.OperStatus})"
            }, kind, Speed(row.ReceiveLinkSpeed), Speed(row.TransmitLinkSpeed), Rate(true), Rate(false), kind == NetworkInterfaceKind.Hardware);
        }).ToArray();
        var truncated = current.Table.Truncated || current.Table.Rows.Count > MaximumRows;
        CounterValue Total(bool receive)
        {
            if (truncated) return Missing("interface table truncated; complete hardware total unavailable");
            var included = rows.Where(r => r.IncludedInTotal).Select(r => receive ? r.ReceiveBytesPerSecond : r.TransmitBytesPerSecond).ToArray();
            if (included.Length == 0) return Missing("no hardware interfaces");
            var unavailable = included.FirstOrDefault(v => v.State == CounterValueState.Unavailable);
            if (unavailable.State == CounterValueState.Unavailable) return Missing("hardware interface rate unavailable: " + unavailable.Reason);
            if (included.Any(v => v.State != CounterValueState.Measured)) return CounterValue.Warmup("hardware interface rates need two samples");
            return CounterValue.Measured(included.Sum(v => v.Value!.Value));
        }
        var values = rows.SelectMany(r => new[] { r.ReceiveBytesPerSecond, r.TransmitBytesPerSecond }).Concat([Total(true), Total(false)]).ToArray();
        var status = truncated ? InspectionStatus.Truncated : previous is null ? InspectionStatus.Partial :
            values.All(v => v.State == CounterValueState.Measured) ? InspectionStatus.Available : InspectionStatus.Partial;
        return new(status, detail + (previous is null ? " " + WarmupDetail + "." : "") + (truncated ? " Instance cap 256 reached; incomplete inventory." : ""), rows, Total(true), Total(false), elapsed);
    }

    internal static CpuCounterSection NormalizeCpu(CounterSample sample)
    {
        var names = Instances(sample).Where(n => !n.Contains("_Total", StringComparison.OrdinalIgnoreCase)).ToArray();
        CounterValue Read(string counter, string instance) => ArrayValue(sample, SystemCounterPaths.Cpu(counter), instance, true);
        var cores = names.Select(name =>
        {
            int? group = null, number = null;
            var parts = name.Split(',');
            if (parts.Length == 2 && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var g) && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var n)) { group = g; number = n; }
            return new LogicalProcessorRow(name, group, number, Read("% Processor Utility", name), Read("% Privileged Time", name));
        }).OrderBy(r => r.Group).ThenBy(r => r.Number).ThenBy(r => r.Instance, StringComparer.Ordinal).Take(MaximumRows).ToArray();
        var utility = Read("% Processor Utility", "_Total");
        var privileged = Read("% Privileged Time", "_Total");
        var context = Single(sample, SystemCounterPaths.Context, true);
        var queue = Single(sample, SystemCounterPaths.Queue, false);
        var status = SectionStatus(sample, cores.SelectMany(r => new[] { r.ProcessorUtilityPercent, r.PrivilegedTimePercent }).Concat([utility, privileged, context, queue]), names.Length > MaximumRows);
        return new(status, Detail(sample, status, "% Processor Utility can exceed 100% when running above nominal frequency; shown as measured"), utility, privileged, cores, context, queue);
    }

    internal static MemoryListSection NormalizeMemory(CounterSample sample, ProcessImageResult? image)
    {
        var values = SystemCounterPaths.MemoryNames.Select((name, index) => Single(sample, SystemCounterPaths.Memory(name), index >= 5)).ToArray();
        var workingSet = Single(sample, SystemCounterPaths.CompressionWorkingSet, false);
        var pid = Single(sample, SystemCounterPaths.CompressionPid, false);
        bool Absent(string path) => sample.Singles.TryGetValue(path, out var r) && (r.FunctionStatus == NativeCounterMethods.PDH_CSTATUS_NO_INSTANCE || r.CStatus == NativeCounterMethods.PDH_CSTATUS_NO_INSTANCE);
        InspectionStatus compressionStatus;
        string compressionDetail;
        if (Absent(SystemCounterPaths.CompressionWorkingSet) || Absent(SystemCounterPaths.CompressionPid))
        {
            compressionStatus = InspectionStatus.Unsupported;
            compressionDetail = "Memory Compression store process not present (compression disabled or not running on this host); no value inferred";
        }
        else if (image is { HasImagePath: true })
        {
            compressionStatus = InspectionStatus.Unsupported;
            compressionDetail = "instance not attributable to the Memory Compression store";
        }
        else if (image is { Error: 5 })
        {
            compressionStatus = InspectionStatus.AccessDenied;
            compressionDetail = "Memory Compression store attribution denied (Windows error 5)";
        }
        else if (pid.State == CounterValueState.Measured && workingSet.State == CounterValueState.Measured && image is { QueryAttempted: true, HasImagePath: false })
        {
            compressionStatus = InspectionStatus.Available;
            compressionDetail = "Memory Compression store resident working set (not bytes saved, not a compression ratio)";
        }
        else
        {
            compressionStatus = sample.TimedOut ? InspectionStatus.TimedOut : InspectionStatus.Partial;
            compressionDetail = $"Memory Compression store attribution unavailable; PID: {pid.Reason}; working set: {workingSet.Reason}; image error: {image?.Error}";
        }
        if (compressionStatus != InspectionStatus.Available) workingSet = Missing(compressionDetail);
        var status = SectionStatus(sample, values);
        return new(status, Detail(sample, status, "Memory list gauges are bytes; paging activity is system-wide."), values[0], values[1], values[2], values[3], values[4], values[5], values[6], values[7], compressionStatus, compressionDetail, workingSet);
    }
    internal enum GpuInstanceKind { Engine, ProcessMemory, AdapterMemory }
    internal readonly record struct GpuInstance(int? Pid, ulong Luid, int PhysicalAdapter, int? Engine, string? EngineType);
    internal sealed record GpuInput(GpuInstance Instance, CounterValue Value);
    internal sealed record GpuInputs(IReadOnlyList<GpuInput> Engines, IReadOnlyList<GpuInput> AdapterDedicated,
        IReadOnlyList<GpuInput> AdapterShared, IReadOnlyList<GpuInput> ProcessDedicated, IReadOnlyList<GpuInput> ProcessShared,
        IReadOnlyList<(int Pid, ulong Luid)> ProcessKeys, IReadOnlyList<ulong> AdapterLuids, int MalformedCount, string? FirstMalformed);

    private const RegexOptions InstanceOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;
    private static readonly Regex EnginePattern = new(@"\Apid_([0-9]+)_luid_0x([0-9a-f]{8})_0x([0-9a-f]{8})_phys_([0-9]+)_eng_([0-9]+)_engtype_(.*)\z", InstanceOptions);
    private static readonly Regex ProcessPattern = new(@"\Apid_([0-9]+)_luid_0x([0-9a-f]{8})_0x([0-9a-f]{8})_phys_([0-9]+)\z", InstanceOptions);
    private static readonly Regex AdapterPattern = new(@"\Aluid_0x([0-9a-f]{8})_0x([0-9a-f]{8})_phys_([0-9]+)\z", InstanceOptions);

    internal static bool TryParseGpuInstance(string name, GpuInstanceKind kind, out GpuInstance instance)
    {
        instance = default;
        var match = (kind == GpuInstanceKind.Engine ? EnginePattern : kind == GpuInstanceKind.ProcessMemory ? ProcessPattern : AdapterPattern).Match(name);
        if (!match.Success) return false;
        var offset = kind == GpuInstanceKind.AdapterMemory ? 0 : 1;
        int? pid = null;
        if (offset != 0)
        {
            if (!int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsedPid) || parsedPid <= 0) return false;
            pid = parsedPid;
        }
        if (!uint.TryParse(match.Groups[1 + offset].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var high) ||
            !uint.TryParse(match.Groups[2 + offset].Value, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var low) ||
            !int.TryParse(match.Groups[3 + offset].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var physical)) return false;
        int? engine = null;
        string? type = null;
        if (kind == GpuInstanceKind.Engine)
        {
            if (!int.TryParse(match.Groups[5].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var index)) return false;
            engine = index;
            type = match.Groups[6].Value.Length == 0 ? "Unnamed" : match.Groups[6].Value;
        }
        instance = new(pid, ((ulong)high << 32) | low, physical, engine, type);
        return true;
    }

    internal static GpuInputs ParseGpu(CounterSample sample)
    {
        var malformed = 0;
        string? firstMalformed = null;
        IReadOnlyList<GpuInput> Parse(string path, GpuInstanceKind kind, bool rate)
        {
            var result = new List<GpuInput>();
            if (!sample.Arrays.TryGetValue(path, out var array) || array.FunctionStatus != 0) return result;
            foreach (var item in array.Items)
            {
                if (!TryParseGpuInstance(item.Instance, kind, out var parsed))
                {
                    malformed++;
                    firstMalformed ??= item.Instance;
                    continue;
                }
                result.Add(new(parsed, NormalizeValue(0, item.CStatus, item.Value, sample.IsWarmup, rate)));
            }
            return result;
        }
        var engines = Parse(SystemCounterPaths.Engine, GpuInstanceKind.Engine, true);
        var dedicated = Parse(SystemCounterPaths.AdapterDedicated, GpuInstanceKind.AdapterMemory, false);
        var shared = Parse(SystemCounterPaths.AdapterShared, GpuInstanceKind.AdapterMemory, false);
        var processDedicated = Parse(SystemCounterPaths.ProcessDedicated, GpuInstanceKind.ProcessMemory, false);
        var processShared = Parse(SystemCounterPaths.ProcessShared, GpuInstanceKind.ProcessMemory, false);
        var processKeys = engines.Concat(processDedicated).Concat(processShared).Select(v => (Pid: v.Instance.Pid!.Value, v.Instance.Luid))
            .Distinct().OrderBy(k => k.Pid).ThenBy(k => k.Luid).ToArray();
        var luids = engines.Concat(dedicated).Concat(shared).Concat(processDedicated).Concat(processShared).Select(v => v.Instance.Luid).Distinct().Order().ToArray();
        return new(engines, dedicated, shared, processDedicated, processShared, processKeys, luids, malformed, firstMalformed);
    }

    private static CounterValue Aggregate(IEnumerable<CounterValue> source, bool maximum)
    {
        var values = source.ToArray();
        if (values.Length == 0) return Missing("no counter instances");
        var unavailable = values.FirstOrDefault(v => v.State == CounterValueState.Unavailable);
        if (unavailable.State == CounterValueState.Unavailable) return Missing("incomplete counter aggregation: " + unavailable.Reason);
        if (values.Any(v => v.State == CounterValueState.Warmup)) return Warmup();
        if (values.Any(v => v.State != CounterValueState.Measured)) return Missing("aggregation requires measured counters");
        var result = maximum ? values.Max(v => v.Value!.Value) : values.Sum(v => v.Value!.Value);
        return double.IsFinite(result) ? CounterValue.Measured(result) : Missing("counter aggregation is not finite");
    }

    internal static (ProcessIdentity? Identity, InspectionStatus Status, string Detail) NormalizeIdentity(int pid,
        ProcessCreationResult? current, ProcessCreationResult? previous)
    {
        if (current is not { } now) return (null, InspectionStatus.TimedOut, "creation time not read before capture budget expired");
        if (now.Status != ProcessCreationStatus.Available || now.CreationFileTime is not > 0)
            return (null, now.Status switch
            {
                ProcessCreationStatus.AccessDenied => InspectionStatus.AccessDenied,
                ProcessCreationStatus.Exited => InspectionStatus.Exited,
                _ => InspectionStatus.Failed
            }, $"creation time unavailable: {now.Status} (Windows error {now.Error})");
        if (previous is not { Status: ProcessCreationStatus.Available, CreationFileTime: > 0 } prior)
            return (null, InspectionStatus.Partial, "identity confirmed at one sample only");
        return now.CreationFileTime == prior.CreationFileTime ? (new ProcessIdentity(pid, now.CreationFileTime), InspectionStatus.Available, "creation FILETIME matched at both samples") :
            (null, InspectionStatus.IdentityMismatch, "process creation FILETIME changed between samples");
    }

    internal static GpuCounterSection NormalizeGpu(CounterSample sample, GpuInputs inputs,
        IReadOnlyDictionary<int, ProcessCreationResult> currentIdentities, IReadOnlyDictionary<int, ProcessCreationResult> previousIdentities,
        DxgiAdapterResult inventory, bool identityTimedOut = false)
    {
        CounterValue Absent(string path, bool rate)
        {
            if (sample.TimedOut) return Missing("capture budget exceeded");
            if (sample.Arrays.TryGetValue(path, out var array) && array.FunctionStatus != 0)
                return NormalizeValue(array.FunctionStatus, 0, null, sample.IsWarmup, rate);
            return sample.IsWarmup && rate ? Warmup() : Missing("no counter instances");
        }
        CounterValue Memory(IReadOnlyList<GpuInput> source, string path, ulong luid, int? pid = null)
        {
            var matching = source.Where(v => v.Instance.Luid == luid && (pid is null || v.Instance.Pid == pid)).ToArray();
            return matching.Length == 0 ? Absent(path, false) : Aggregate(matching.Select(v => v.Value), false);
        }
        GpuEngineUtilization[] Engines(ulong luid, int? pid = null) => inputs.Engines
            .Where(v => v.Instance.Luid == luid && (pid is null || v.Instance.Pid == pid))
            .GroupBy(v => v.Instance.EngineType!, StringComparer.OrdinalIgnoreCase)
            .Select(type => new GpuEngineUtilization(type.Key, Aggregate(type.GroupBy(v => (v.Instance.PhysicalAdapter, v.Instance.Engine))
                .Select(engine => Aggregate(engine.Select(v => v.Value), pid is not null)), true)))
            .OrderBy(e => e.EngineType, StringComparer.OrdinalIgnoreCase).ToArray();
        (CounterValue Value, string? Type) Headline(GpuEngineUtilization[] engines)
        {
            if (engines.Length == 0) return (Absent(SystemCounterPaths.Engine, true), null);
            var value = Aggregate(engines.Select(e => e.UtilizationPercent), true);
            return (value, value.State == CounterValueState.Measured ? engines.OrderByDescending(e => e.UtilizationPercent.Value).First().EngineType : null);
        }
        var adapterLuids = inputs.AdapterLuids.Concat(inventory.Adapters.Select(a => a.Luid)).Distinct().Order().ToArray();
        var adapters = adapterLuids.Take(MaximumRows).Select(luid =>
        {
            var native = inventory.Adapters.FirstOrDefault(a => a.Luid == luid);
            var engines = Engines(luid);
            var headline = Headline(engines);
            return new GpuAdapterRow(luid, native is null ? null : native.Description + ((native.Flags & 2) != 0 ? " (software adapter)" : ""),
                native?.DedicatedVideoMemory, native?.SharedSystemMemory, Memory(inputs.AdapterDedicated, SystemCounterPaths.AdapterDedicated, luid),
                Memory(inputs.AdapterShared, SystemCounterPaths.AdapterShared, luid), engines, headline.Value, headline.Type);
        }).ToArray();
        var processes = inputs.ProcessKeys.Take(MaximumRows).Select(key =>
        {
            var identity = NormalizeIdentity(key.Pid, currentIdentities.TryGetValue(key.Pid, out var current) ? current : null,
                previousIdentities.TryGetValue(key.Pid, out var previous) ? previous : null);
            var engines = Engines(key.Luid, key.Pid);
            var headline = Headline(engines);
            return new GpuProcessRow(key.Pid, identity.Identity, identity.Status, identity.Detail, key.Luid, engines, headline.Value, headline.Type,
                Memory(inputs.ProcessDedicated, SystemCounterPaths.ProcessDedicated, key.Luid, key.Pid), Memory(inputs.ProcessShared, SystemCounterPaths.ProcessShared, key.Luid, key.Pid));
        }).ToArray();
        var values = adapters.Where(a => inputs.AdapterLuids.Contains(a.Luid)).SelectMany(a => new[] { a.DedicatedUsageBytes, a.SharedUsageBytes, a.HeadlineUtilizationPercent })
            .Concat(processes.SelectMany(p => new[] { p.DedicatedUsageBytes, p.SharedUsageBytes, p.MaxUtilizationPercent })).ToArray();
        var status = SectionStatus(sample, values, inputs.ProcessKeys.Count > MaximumRows || adapterLuids.Length > MaximumRows || inventory.Truncated);
        if (values.Length == 0 && !sample.IsWarmup && !sample.TimedOut && status == InspectionStatus.Partial &&
            SystemCounterPaths.Arrays(CounterSection.Gpu).All(p => sample.Arrays.TryGetValue(p, out var a) && a.FunctionStatus == 0)) status = InspectionStatus.Available;
        if (identityTimedOut) status = InspectionStatus.TimedOut;
        if (status == InspectionStatus.Available && (inputs.MalformedCount != 0 || inventory.Status != DxgiInventoryStatus.Available || processes.Any(p => p.IdentityStatus != InspectionStatus.Available) ||
            sample.Arrays.Values.Any(a => a.FunctionStatus != 0))) status = InspectionStatus.Partial;
        var inventoryStatus = inventory.Status switch
        {
            DxgiInventoryStatus.Available => inventory.Truncated ? InspectionStatus.Truncated : InspectionStatus.Available,
            DxgiInventoryStatus.Unsupported => InspectionStatus.Unsupported,
            _ => InspectionStatus.Failed
        };
        var inventoryOnly = adapters.Count(a => !inputs.AdapterLuids.Contains(a.Luid));
        return new(status, Detail(sample, status, "Shared GPU memory is system RAM shared with the GPU, not additional physical memory; utilization is max over engine types." +
            (inventoryOnly == 0 ? "" : $" {inventoryOnly} inventory adapter(s) have no counter instances; listed with unavailable values, excluded from counter availability status.")),
            adapters, processes, inputs.MalformedCount, inputs.MalformedCount == 0 ? null : $"{inputs.MalformedCount} malformed GPU instance(s); first example: {inputs.FirstMalformed}",
            inventoryStatus, $"DXGI inventory {inventory.Status}; HRESULT 0x{inventory.HResult:X8}" + (inventory.Truncated ? "; adapter cap 16 reached" : ""));
    }
}
