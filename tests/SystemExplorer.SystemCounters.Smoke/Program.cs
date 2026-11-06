using System.Buffers.Binary;
using System.Reflection;
using System.Text;
using SystemExplorer.Core;
using SystemExplorer.Windows;
using static SystemExplorer.Windows.WindowsSystemCounterProvider;

if (args.Contains("--monitor-gpu-only"))
{
    using var counters = new WindowsSystemCounterProvider();
    using var monitor = new WindowsMonitorProvider();
    await counters.CaptureAsync(CancellationToken.None);
    await monitor.CaptureAsync(CancellationToken.None);
    await Task.Delay(1500);
    var observed = await counters.CaptureAsync(CancellationToken.None);
    var snapshot = await monitor.CaptureAsync(CancellationToken.None);
    Console.WriteLine($"GPU source: {observed.Gpu.InventoryDetail}; {string.Join(", ", observed.Gpu.Adapters.Select(a => $"{a.Description}: {a.HeadlineUtilizationPercent.Value}% ({a.Engines.Count} engine types)"))}");
    Check(observed.Gpu.Adapters.Any(a => a.HeadlineUtilizationPercent.State == CounterValueState.Measured), "VM prerequisite: existing provider must have a measured GPU adapter.");
    Console.WriteLine($"Monitor GPU: {snapshot.GpuPercent?.ToString() ?? "null"}");
    Check(snapshot.GpuPercent is >= 0 and <= 100, "Monitor must project existing provider GPU data instead of leaving it null.");
    Console.WriteLine("PASS: monitor GPU projection from live provider (including software adapters).");
    return;
}

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void Expect<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}.");
}

static async Task ExpectAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new InvalidOperationException($"Expected {typeof(T).Name}, not a published result.");
}

static CounterSample Sample(Dictionary<string, PdhArrayResult>? arrays = null, Dictionary<string, PdhSingleResult>? singles = null, bool warmup = false) =>
    new(warmup, arrays ?? [], singles ?? []);
static DxgiAdapterResult Inventory(params DxgiAdapter[] adapters) => new(DxgiInventoryStatus.Available, 0, adapters, false);
static GpuCounterSection Gpu(CounterSample sample, DxgiAdapterResult? inventory = null,
    Dictionary<int, ProcessCreationResult>? current = null, Dictionary<int, ProcessCreationResult>? previous = null) =>
    NormalizeGpu(sample, ParseGpu(sample), current ?? [], previous ?? [], inventory ?? Inventory());

Check(OperatingSystem.IsWindows() && IntPtr.Size == 8, "This native smoke requires x64 Windows.");
var bytes = new byte[1352];
var emptyRow = NativeCounterMethods.ParseIfRow(bytes);
Check(emptyRow.Index == 0 && emptyRow.Alias == "" && emptyRow.OutOctets == 0, "Zero row must parse without guessed fields.");
BinaryPrimitives.WriteUInt64LittleEndian(bytes, 0x123456789abcdef0);
BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8), 123);
Encoding.Unicode.GetBytes(new string('A', 257)).CopyTo(bytes, 28);
Encoding.Unicode.GetBytes("description\0ignored").CopyTo(bytes, 542);
foreach (var (offset, value) in new (int, uint)[] { (1128, 6), (1132, 0), (1136, 8), (1148, 2), (1156, 2) })
    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
bytes[1152] = 1;
foreach (var (offset, value) in new (int, ulong)[] { (1192, 1000), (1200, ulong.MaxValue), (1208, 333), (1280, 555) })
    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
var parsedRow = NativeCounterMethods.ParseIfRow(bytes);
Check(parsedRow == new IfRow(0x123456789abcdef0, 123, new string('A', 257), "description", 6, 0, 8, 1, 2, 1000, ulong.MaxValue, 333, 555),
    "MIB_IF_ROW2 offsets, 257-character alias bound and first-NUL description must match fixed bytes.");
Expect<ArgumentException>(() => NativeCounterMethods.ParseIfRow(new byte[1351]));
var fakeClock = new FakeClock();
var hardwareRow = parsedRow with { InOctets = 500, OutOctets = 900 };
var priorHardwareRow = hardwareRow with { InOctets = 300, OutOctets = 500 };
var parsedHardware = NormalizeNetwork(new(new(0, [hardwareRow], false), 2000), new(new(0, [priorHardwareRow], false), 0), fakeClock);
Check(parsedHardware.Adapters.Single().Kind == NetworkInterfaceKind.Hardware && parsedHardware.Adapters.Single().IncludedInTotal
    && parsedHardware.TotalReceiveBytesPerSecond.Value == 100 && parsedHardware.TotalTransmitBytesPerSecond.Value == 200,
    "SDK-layout DirectionType must not replace HardwareInterface flags or OperStatus.");
var largeTable = new InterfaceSample(new(0, Enumerable.Range(1, 257).Select(i => parsedRow with { Index = (uint)i }).ToArray(), false), 1000);
var cappedTable = NormalizeNetwork(largeTable, null, fakeClock);
Check(cappedTable.Status == InspectionStatus.Truncated && cappedTable.Adapters.Count == 256 && cappedTable.TotalReceiveBytesPerSecond.Value is null,
    "Fixed-row table must truncate at 256 without a partial total.");
Console.WriteLine("PASS: native byte offsets, bounded strings, short-row rejection and interface row cap.");

Check(NormalizeValue(0, 1, 150, false, true).Value == 150, "NEW_DATA and utility above 100 must be measured.");
var invalid = NormalizeValue(0, 0x800007D6, 10, true, true);
Check(invalid.State == CounterValueState.Unavailable && invalid.Reason!.Contains("0x800007D6"), "Invalid item status needs unavailable and hex reason even during warmup.");
foreach (var status in new uint[] { 0xC0000BBA, NativeCounterMethods.PDH_INVALID_DATA, NativeCounterMethods.PDH_NO_DATA })
    Check(NormalizeValue(status, 0, null, true, true).State == CounterValueState.Warmup, "First-query invalid/no-data function status must be warmup.");
Check(NormalizeValue(0, 0, 100, true, true).State == CounterValueState.Warmup && NormalizeValue(0, 0, 100, true, false).Value == 100,
    "Warmup masks rates, not instantaneous gauges.");
Check(NormalizeDisks(Sample()).Disks.Count == 0 && NormalizeCpu(Sample()).LogicalProcessors.Count == 0, "Empty arrays must not invent rows.");
var disks = SystemCounterPaths.DiskNames.ToDictionary(SystemCounterPaths.Disk,
    name => new PdhArrayResult(0, [new("_Total", name.StartsWith("Avg.", StringComparison.Ordinal) ? 0.002 : 5, 0), new("0 C:", name.StartsWith("Avg.", StringComparison.Ordinal) ? 0.002 : 5, 0)], false));
var disk = NormalizeDisks(Sample(disks));
Check(disk.Status == InspectionStatus.Available && disk.Total!.IsAggregate && disk.Disks.Count == 1 && !disk.Disks[0].IsAggregate &&
    disk.Disks[0].ReadLatencyMilliseconds.Value == 2 && disk.Disks[0].WriteLatencyMilliseconds.Value == 2, "Latency must convert seconds to milliseconds; aggregate must be separate.");
foreach (var name in new[] { "Disk Reads/sec", "Disk Writes/sec" }) disks[SystemCounterPaths.Disk(name)] = new(0, [new("0 C:", 0, 0)], false);
disk = NormalizeDisks(Sample(disks));
Check(disk.Disks[0].ReadLatencyMilliseconds.State == CounterValueState.NoActivityInInterval && disk.Disks[0].ReadLatencyMilliseconds.Value is null &&
    disk.Disks[0].WriteLatencyMilliseconds.State == CounterValueState.NoActivityInInterval, "No I/O is no activity, not zero latency.");
disks[SystemCounterPaths.Disk("Disk Reads/sec")] = new(0, [new("0 C:", 5, 5)], false);
Check(NormalizeDisks(Sample(disks)).Disks[0].ReadLatencyMilliseconds.State == CounterValueState.Unavailable, "Unavailable reads invalidate read latency.");
var cappedDisk = NormalizeDisks(Sample(new() { [SystemCounterPaths.Disk("Disk Reads/sec")] = new(0, Enumerable.Range(0, 257).Select(i => new PdhInstanceValue(i.ToString(), 1, 0)).ToArray(), false) }));
Check(cappedDisk.Status == InspectionStatus.Truncated && cappedDisk.Disks.Count == 256, "Disk instance cap must be explicit.");
Console.WriteLine("PASS: PDH value states, warmup, disk units/no-activity, invalid statuses and instance cap.");

IfRow Row(ulong luid, ulong incoming, ulong outgoing) => new(luid, 5, "Ethernet 2", "test", 6, 0, 0, 1, 1, 0, ulong.MaxValue, incoming, outgoing);
var before = new InterfaceSample(new(0, [Row(10, 100, 200)], false), 0);
var after = new InterfaceSample(new(0, [Row(10, 300, 600)], false), 2000);
var network = NormalizeNetwork(after, before, fakeClock);
Check(network.TotalReceiveBytesPerSecond.Value == 100 && network.TotalTransmitBytesPerSecond.Value == 200 && network.MeasuredInterval == TimeSpan.FromSeconds(2) &&
    network.Adapters[0].ReceiveLinkSpeedBitsPerSecond is null && network.Adapters[0].TransmitLinkSpeedBitsPerSecond is null, "Use table timestamps for rates; zero/max speed is unknown.");
Check(NormalizeNetwork(after, null, fakeClock).TotalReceiveBytesPerSecond.State == CounterValueState.Warmup, "First interface sample must be warmup.");
Check(NormalizeNetwork(after with { Table = new(0, [Row(11, 300, 600)], false) }, before, fakeClock).TotalReceiveBytesPerSecond.Reason!.Contains("identity changed"), "Changed LUID must invalidate total.");
Check(NormalizeNetwork(after with { Table = new(0, [Row(10, 50, 600)], false) }, before, fakeClock).TotalReceiveBytesPerSecond.Reason!.Contains("decreased/reset"), "Counter reset must invalidate total.");
foreach (var timestamp in new long[] { 0, -1000 })
    Check(NormalizeNetwork(after with { Timestamp = timestamp }, before, fakeClock).TotalReceiveBytesPerSecond.State == CounterValueState.Unavailable, "Non-positive table elapsed time is unavailable.");
Check(NormalizeNetwork(after with { Table = new(0, [], false) }, before, fakeClock).TotalReceiveBytesPerSecond.Reason == "no hardware interfaces", "Empty hardware set is not zero traffic.");
var classifications = new[] { Row(10, 0, 0), Row(11, 0, 0) with { Index = 9, Flags = 3 }, Row(12, 0, 0) with { Index = 10, Flags = 0 },
    Row(13, 0, 0) with { Index = 1, Type = 24 }, Row(14, 0, 0) with { Index = 6, Type = 131 }, Row(15, 0, 0) with { Index = 7, TunnelType = 1 } };
network = NormalizeNetwork(new(new(0, classifications.Select(r => r with { InOctets = 200, OutOctets = 400 }).ToArray(), false), 2000), new(new(0, classifications, false), 0), fakeClock);
Check(network.Adapters.Select(r => r.Kind).SequenceEqual(new[] { NetworkInterfaceKind.Hardware, NetworkInterfaceKind.Filter, NetworkInterfaceKind.Virtual, NetworkInterfaceKind.Loopback, NetworkInterfaceKind.Tunnel, NetworkInterfaceKind.Tunnel }) &&
    network.Adapters.Count(r => r.IncludedInTotal) == 1 && network.TotalReceiveBytesPerSecond.Value == 100, "Only hardware contributes, even if non-hardware rows carry traffic and hardware flags.");
Console.WriteLine("PASS: network monotonic deltas, unknown speeds, first sample/reset/identity/elapsed failures, classification and complete totals.");

var cpuArrays = SystemCounterPaths.CpuNames.ToDictionary(SystemCounterPaths.Cpu, _ => new PdhArrayResult(0,
    [new("_Total", 150, 0), new("0,_Total", 151, 0), new("1,2", 152, 1), new("0,3", 153, 0), new("odd", 154, 0)], false));
var cpu = NormalizeCpu(Sample(cpuArrays, new() { [SystemCounterPaths.Context] = new(0, 0, 10), [SystemCounterPaths.Queue] = new(0, 0, 2) }));
Check(cpu.Status == InspectionStatus.Available && cpu.TotalProcessorUtilityPercent.Value == 150 && cpu.LogicalProcessors.Count == 3 &&
    cpu.LogicalProcessors.All(c => !c.Instance.Contains("_Total") && c.ProcessorUtilityPercent.Value > 100) &&
    cpu.LogicalProcessors.Single(c => c.Instance == "odd").Group is null && cpu.LogicalProcessors.Where(c => c.Group is not null).Select(c => c.Instance).SequenceEqual(new[] { "0,3", "1,2" }),
    "CPU totals/exclusions, malformed core name retention, sorting and uncapped utility must hold.");
var memorySingles = SystemCounterPaths.MemoryNames.ToDictionary(SystemCounterPaths.Memory, _ => new PdhSingleResult(0, 0, 100));
memorySingles[SystemCounterPaths.CompressionPid] = new(0, 0, 123);
memorySingles[SystemCounterPaths.CompressionWorkingSet] = new(0, 0, 500);
Check(NormalizeMemory(Sample(singles: memorySingles), new(true, 0)).CompressionStatus == InspectionStatus.Unsupported, "Ordinary MemCompression process with a file path must be rejected.");
Check(NormalizeMemory(Sample(singles: memorySingles), new(false, 31)).CompressionStoreResidentWorkingSetBytes.Value == 500, "No-image attribution uses resident working set.");
Check(NormalizeMemory(Sample(singles: memorySingles), new(false, 5, false)).CompressionStatus == InspectionStatus.AccessDenied, "Denied open cannot attribute store.");
memorySingles[SystemCounterPaths.CompressionPid] = new(0xC0000BC6, 0x800007D1, null);
var memory = NormalizeMemory(Sample(singles: memorySingles), null);
Check(memory.CompressionStatus == InspectionStatus.Unsupported && memory.Status == InspectionStatus.Available, "Absent compression must not downgrade measured memory lists.");
Console.WriteLine("PASS: CPU totals/group exclusions/uncapped utility; compression absent/ordinary/no-image/denied attribution and independent memory status.");

Check(TryParseGpuInstance("PID_12_LUID_0xABCDEF01_0x12345678_PHYS_0_ENG_8_ENGTYPE_Video_Decode 1", GpuInstanceKind.Engine, out var parsed) &&
    parsed.Luid == 0xabcdef0112345678 && parsed.EngineType == "Video_Decode 1", "GPU parsing must be strict and case-insensitive, retaining the type rest.");
Check(TryParseGpuInstance("pid_1_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_", GpuInstanceKind.Engine, out parsed) && parsed.EngineType == "Unnamed", "Empty engine type must be Unnamed.");
Check(TryParseGpuInstance("pid_1_luid_0xFFFFFFFF_0xFFFFFFFF_phys_0", GpuInstanceKind.ProcessMemory, out parsed) && parsed.Luid == ulong.MaxValue &&
    TryParseGpuInstance("luid_0x00000000_0x00000001_phys_1", GpuInstanceKind.AdapterMemory, out _), "Process/adapter memory names must parse full unsigned LUIDs.");
foreach (var bad in new[] { "pid_0_luid_0x00000000_0x00000001_phys_0", "pid_2147483648_luid_0x00000000_0x00000001_phys_0", "pid_1_luid_0x0000000Z_0x00000001_phys_0", "pid_1_luid_0x000000000_0x00000001_phys_0", "pid_1_luid_0x00000000_0x00000001_phys_2147483648", "pid_1_luid_0x00000000_0x00000001_phys_0_suffix" })
    Check(!TryParseGpuInstance(bad, GpuInstanceKind.ProcessMemory, out _), "Malformed/overflow GPU name accepted: " + bad);
var gpuArrays = SystemCounterPaths.Arrays(CounterSection.Gpu).ToDictionary(p => p, _ => new PdhArrayResult(0, [], false));
string Engine(int pid, int engine, string type = "3D") => $"pid_{pid}_luid_0x00000000_0x00000001_phys_0_eng_{engine}_engtype_{type}";
gpuArrays[SystemCounterPaths.Engine] = new(0, [new(Engine(10, 0), 20, 0), new(Engine(11, 0), 30, 0), new(Engine(10, 1), 40, 0), new(Engine(10, 2, "Copy"), 80, 0), new("bad", 1, 0)], false);
gpuArrays[SystemCounterPaths.AdapterDedicated] = new(0, [new("luid_0x00000000_0x00000001_phys_0", 10, 0), new("luid_0x00000000_0x00000001_phys_1", 20, 0)], false);
var gpu = Gpu(Sample(gpuArrays));
Check(gpu.Adapters[0].Engines.Single(e => e.EngineType == "3D").UtilizationPercent.Value == 50 && gpu.Adapters[0].HeadlineUtilizationPercent.Value == 80 &&
    gpu.Adapters[0].DedicatedUsageBytes.Value == 30 && gpu.Processes.Single(p => p.Pid == 10).Engines.Single(e => e.EngineType == "3D").UtilizationPercent.Value == 40 &&
    gpu.MalformedInstanceCount == 1 && gpu.MalformedDetail!.Contains("bad"), "GPU must sum pids per engine, max across indices/types, sum physical memory and report malformed names.");
Check(NormalizeIdentity(10, new(ProcessCreationStatus.Available, 123, 0), new(ProcessCreationStatus.Available, 124, 0)).Status == InspectionStatus.IdentityMismatch, "Changed creation time must mismatch.");
Check(NormalizeIdentity(10, new(ProcessCreationStatus.Available, 123, 0), new(ProcessCreationStatus.Available, 123, 0)).Identity == new ProcessIdentity(10, 123), "Matched creation times must establish identity.");
Check(NormalizeIdentity(10, new(ProcessCreationStatus.Available, 123, 0), null).Status == InspectionStatus.Partial &&
    NormalizeIdentity(10, new(ProcessCreationStatus.Exited, null, 87), null).Status == InspectionStatus.Exited &&
    NormalizeIdentity(10, new(ProcessCreationStatus.AccessDenied, null, 5), null).Status == InspectionStatus.AccessDenied, "Single-sample/exited/denied identities must remain explicit.");
var emptyGpu = Gpu(Data.GpuEmpty(), Inventory(new DxgiAdapter("inventory only", 99, 10, 20, 2)));
Check(emptyGpu.Status == InspectionStatus.Available && emptyGpu.Adapters.Single().HeadlineUtilizationPercent.State == CounterValueState.Unavailable && emptyGpu.Detail.Contains("no counter instances"),
    "Inventory-only adapters must be visible without downgrading counters.");
Check(!emptyGpu.Adapters.Single().HasCounterInstances, "Inventory-only adapters must identify the absence of counter instances.");
var goodGpu = Data.Gpu();
Check(Gpu(goodGpu).Adapters.Single().HasCounterInstances, "Counter-backed adapters must identify their counter instances.");
var memoryOnlyGpu = goodGpu.Arrays.ToDictionary(k => k.Key, k => k.Value);
memoryOnlyGpu[SystemCounterPaths.Engine] = new(0, [], false);
Check(Gpu(Sample(memoryOnlyGpu)).Adapters.Single().HasCounterInstances, "Memory-only counter instances must not be mistaken for inventory-only adapters.");
var knownIdentities = new Dictionary<int, ProcessCreationResult> { [10] = new(ProcessCreationStatus.Available, 123, 0) };
Check(Gpu(goodGpu, Inventory(new DxgiAdapter("counter adapter", 1, 10, 20, 2), new DxgiAdapter("inventory only", 99, 10, 20, 0)), knownIdentities, knownIdentities).Status == InspectionStatus.Available,
    "Inventory-only adapter must not downgrade a measured active adapter.");
Check(Gpu(goodGpu, current: knownIdentities).Status == InspectionStatus.Partial, "After warmup an unconfirmed process identity must make GPU partial.");
var manyProcesses = Data.GpuEmpty().Arrays.ToDictionary(k => k.Key, k => k.Value);
manyProcesses[SystemCounterPaths.Engine] = new(0, Enumerable.Range(1, 257).Select(pid => new PdhInstanceValue(Engine(pid, 0), 1, 0)).ToArray(), false);
gpu = Gpu(Sample(manyProcesses));
Check(gpu.Status == InspectionStatus.Truncated && gpu.Processes.Count == 256, "GPU process cap must be explicit.");
Console.WriteLine("PASS: GPU strict parsing, overflow/empty type, engine aggregation, memory sums, identity states, inventory-only status and process cap.");

var nativeQueries = new List<FakeQuery>();
var realSampler = new SystemCounterSampler(() => { var query = new FakeQuery(); nativeQueries.Add(query); return query; });
var firstDisk = realSampler.Sample(CounterSection.Disk, CancellationToken.None, () => false);
var firstCpu = realSampler.Sample(CounterSection.Cpu, CancellationToken.None, () => false);
Check(firstDisk.IsWarmup && firstCpu.IsWarmup, "Each query's first collect must be warmup.");
Check(!realSampler.Sample(CounterSection.Disk, CancellationToken.None, () => false).IsWarmup &&
    !realSampler.Sample(CounterSection.Cpu, CancellationToken.None, () => false).IsWarmup && nativeQueries.Count == 2,
    "Healthy section queries must preserve their independent rate baselines.");
realSampler.Dispose();
Check(nativeQueries.All(q => q.DisposeCount == 1), "All created queries must be disposed exactly once.");

var missingCompression = new FakeQuery();
missingCompression.AddStatuses[SystemCounterPaths.CompressionPid] = NativeCounterMethods.PDH_CSTATUS_NO_INSTANCE;
var memorySampler = new SystemCounterSampler(() => missingCompression);
var memoryFirst = memorySampler.Sample(CounterSection.Memory, CancellationToken.None, () => false);
var memorySecond = memorySampler.Sample(CounterSection.Memory, CancellationToken.None, () => false);
var normalizedMemory = NormalizeMemory(memorySecond, null);
Check(memoryFirst.IsWarmup && !memorySecond.IsWarmup && missingCompression.DisposeCount == 0 &&
    missingCompression.AddCounts[SystemCounterPaths.CompressionPid] == 2 && normalizedMemory.CompressionStatus == InspectionStatus.Unsupported &&
    normalizedMemory.PageFaultsPerSecond.State == CounterValueState.Measured,
    "A persistently missing optional compression scalar must not keep healthy Memory rates warming.");
memorySampler.Dispose();
Check(missingCompression.DisposeCount == 1, "The Memory query must dispose once after optional-counter retries.");

var diskReadsPath = SystemCounterPaths.Disk("Disk Reads/sec");
var diskQuery = new FakeQuery();
diskQuery.ArrayStatuses[diskReadsPath] = NativeCounterMethods.PDH_CSTATUS_NO_INSTANCE;
var diskSampler = new SystemCounterSampler(() => diskQuery);
var diskWarmup = diskSampler.Sample(CounterSection.Disk, CancellationToken.None, () => false);
var diskFailure = diskSampler.Sample(CounterSection.Disk, CancellationToken.None, () => false);
var diskFailureRows = NormalizeDisks(diskFailure);
var diskFailureTotal = diskFailureRows.Total!;
Check(diskWarmup.IsWarmup && !diskFailure.IsWarmup && diskQuery.DisposeCount == 0 &&
    diskQuery.AddCounts[diskReadsPath] == 2 && diskQuery.RemoveCounts[diskReadsPath] == 2 &&
    diskQuery.RegisteredAtCollect[1].Contains(diskReadsPath) && diskQuery.RegisteredAtCollect[1].Contains(SystemCounterPaths.Disk("Disk Writes/sec")) &&
    diskFailureTotal.ReadsPerSecond.State == CounterValueState.Unavailable && diskFailureTotal.WritesPerSecond.State == CounterValueState.Measured,
    "A persistently failing array counter must be retried alone while its healthy sibling reaches measured.");
diskQuery.ArrayStatuses.Remove(diskReadsPath);
var recoveredDisk = diskSampler.Sample(CounterSection.Disk, CancellationToken.None, () => false);
var recoveredDiskRows = NormalizeDisks(recoveredDisk);
var recoveredDiskTotal = recoveredDiskRows.Total!;
Check(recoveredDisk.Arrays[diskReadsPath].IsWarmup && diskQuery.RegisteredAtCollect[2].Contains(diskReadsPath) &&
    recoveredDiskTotal.ReadsPerSecond.State == CounterValueState.Warmup &&
    recoveredDiskTotal.WritesPerSecond.State == CounterValueState.Measured,
    "A re-registered array counter's first valid rate must be warmup without warming healthy siblings.");
var measuredDisk = NormalizeDisks(diskSampler.Sample(CounterSection.Disk, CancellationToken.None, () => false));
Check(measuredDisk.Total!.ReadsPerSecond.State == CounterValueState.Measured && measuredDisk.Total.WritesPerSecond.State == CounterValueState.Measured,
    "A recovered counter must become measured after its individual warmup sample.");
diskSampler.Dispose();
Check(diskQuery.DisposeCount == 1, "The isolated array query must dispose once.");

var contextPath = SystemCounterPaths.Context;
var recoveringScalar = new FakeQuery();
recoveringScalar.AddStatuses[contextPath] = NativeCounterMethods.PDH_CSTATUS_NO_COUNTER;
recoveringScalar.SingleResults[contextPath] = new Queue<PdhSingleResult>([
    new(NativeCounterMethods.PDH_CSTATUS_NO_INSTANCE, NativeCounterMethods.PDH_CSTATUS_NO_INSTANCE, null),
    new(0, 0, 10), new(0, 0, 12)]);
var scalarSampler = new SystemCounterSampler(() => recoveringScalar);
var missingScalar = scalarSampler.Sample(CounterSection.Cpu, CancellationToken.None, () => false);
recoveringScalar.AddStatuses.Remove(contextPath);
var scalarReadFailure = scalarSampler.Sample(CounterSection.Cpu, CancellationToken.None, () => false);
var recoveredScalar = scalarSampler.Sample(CounterSection.Cpu, CancellationToken.None, () => false);
var recoveredCpu = NormalizeCpu(recoveredScalar);
var measuredCpu = NormalizeCpu(scalarSampler.Sample(CounterSection.Cpu, CancellationToken.None, () => false));
Check(missingScalar.Singles[contextPath].FunctionStatus == NativeCounterMethods.PDH_CSTATUS_NO_COUNTER &&
    scalarReadFailure.Singles[contextPath].FunctionStatus == NativeCounterMethods.PDH_CSTATUS_NO_INSTANCE &&
    recoveringScalar.RegisteredAtCollect[1].Contains(contextPath) && recoveringScalar.RemoveCounts[contextPath] == 1 && recoveringScalar.DisposeCount == 0 &&
    recoveredScalar.Singles[contextPath].IsWarmup && recoveredCpu.ContextSwitchesPerSecond.State == CounterValueState.Warmup &&
    recoveredCpu.TotalProcessorUtilityPercent.State == CounterValueState.Measured &&
    measuredCpu.ContextSwitchesPerSecond.State == CounterValueState.Measured,
    "A missing scalar must recover independently, suppress its first valid rate, then become measured.");
scalarSampler.Dispose();
Check(recoveringScalar.DisposeCount == 1, "The scalar recovery query must dispose once.");

var warmQuery = new FakeQuery { ArrayStatus = 0xC0000BBA };
using (var warmSampler = new SystemCounterSampler(() => warmQuery))
{
    Check(warmSampler.Sample(CounterSection.Disk, CancellationToken.None, () => false).IsWarmup && warmQuery.DisposeCount == 0, "First invalid-data sample is not query churn.");
    warmQuery.ArrayStatus = 0;
    Check(!warmSampler.Sample(CounterSection.Disk, CancellationToken.None, () => false).IsWarmup, "Second collect of same query is not warmup.");
}
Check(warmQuery.DisposeCount == 1, "Warmup query must close on sampler disposal.");

var collectQueries = new List<FakeQuery>();
var collectSampler = new SystemCounterSampler(() => { var query = new FakeQuery(); collectQueries.Add(query); return query; });
collectSampler.Sample(CounterSection.Disk, CancellationToken.None, () => false);
collectQueries[0].CollectStatus = 0xDEADBEEF;
collectSampler.Sample(CounterSection.Disk, CancellationToken.None, () => false);
Check(collectQueries[0].DisposeCount == 1 && collectSampler.Sample(CounterSection.Disk, CancellationToken.None, () => false).IsWarmup && collectQueries.Count == 2,
    "A whole-query collect failure must dispose and rebuild the affected section.");
collectSampler.Dispose();
Check(collectQueries.All(q => q.DisposeCount == 1), "Collect-failed queries must dispose exactly once.");

var timeoutQueries = new List<FakeQuery>();
var timeoutSampler = new SystemCounterSampler(() => { var query = new FakeQuery(); timeoutQueries.Add(query); return query; });
timeoutSampler.Sample(CounterSection.Disk, CancellationToken.None, () => false);
Check(timeoutSampler.Sample(CounterSection.Disk, CancellationToken.None, () => true).TimedOut && timeoutQueries[0].DisposeCount == 1 &&
    timeoutSampler.Sample(CounterSection.Disk, CancellationToken.None, () => false).IsWarmup && timeoutQueries.Count == 2,
    "A whole-query timeout must dispose the affected query before the next warmup.");
timeoutSampler.Dispose();
Check(timeoutQueries.All(q => q.DisposeCount == 1), "Timed-out queries must dispose exactly once.");

var cancellationQueries = new List<FakeQuery>();
var cancellationSampler = new SystemCounterSampler(() => { var query = new FakeQuery(); cancellationQueries.Add(query); return query; });
cancellationSampler.Sample(CounterSection.Cpu, CancellationToken.None, () => false);
using (var samplingCancellation = new CancellationTokenSource())
{
    cancellationQueries[0].AfterCollect = samplingCancellation.Cancel;
    var cancellationObserved = false;
    try { cancellationSampler.Sample(CounterSection.Cpu, samplingCancellation.Token, () => false); }
    catch (OperationCanceledException) { cancellationObserved = true; }
    Check(cancellationObserved && cancellationQueries[0].DisposeCount == 1 &&
        cancellationSampler.Sample(CounterSection.Cpu, CancellationToken.None, () => false).IsWarmup && cancellationQueries.Count == 2,
        "Cancellation during collection must dispose the partial query before the next warmup.");
}
cancellationSampler.Dispose();
Check(cancellationQueries.All(q => q.DisposeCount == 1), "Cancelled queries must dispose exactly once.");
Console.WriteLine("PASS: isolated scalar/array failures, healthy sibling baselines, recovery warmup, and query-level disposal.");

var source = new FakeSampler();
var tableSource = new FakeInterfaces(fakeClock);
var processSource = new FakeProcesses();
var inventorySource = new FakeInventory();
using (var provider = new WindowsSystemCounterProvider(source, tableSource, processSource, inventorySource, fakeClock))
{
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    await ExpectAsync<OperationCanceledException>(() => provider.CaptureAsync(canceled.Token));
    Check(source.Calls == 0, "Pre-canceled request must not sample.");
    using var during = new CancellationTokenSource();
    source.BeforeSample = (_, _) => during.Cancel();
    await ExpectAsync<OperationCanceledException>(() => provider.CaptureAsync(during.Token));
    source.BeforeSample = null;
    var first = await provider.CaptureAsync(CancellationToken.None);
    Check(first.IsWarmup && first.Interval is null, "Canceled capture must not publish prior-sample state.");
    source.DelayMilliseconds = 20;
    fakeClock.Ticks += 2000;
    var snapshots = await Task.WhenAll(provider.CaptureAsync(CancellationToken.None), provider.CaptureAsync(CancellationToken.None));
    Check(source.MaxActive == 1 && snapshots.All(s => !s.IsWarmup), "Concurrent captures must serialize sampler calls.");
    Check(inventorySource.Calls == 1, "Known LUIDs must use cached inventory.");
    provider.Dispose();
    await ExpectAsync<ObjectDisposedException>(() => provider.CaptureAsync(CancellationToken.None));
    Check(source.DisposeCount == 1, "Provider must dispose sampler exactly once.");
}
var budgetClock = new FakeClock();
var budgetSampler = new FakeSampler { BeforeSample = (_, _) => budgetClock.Ticks += 4000 };
using (var provider = new WindowsSystemCounterProvider(budgetSampler, new FakeInterfaces(budgetClock), new FakeProcesses(), new FakeInventory(), budgetClock))
{
    var timed = await provider.CaptureAsync(CancellationToken.None);
    Check(timed.Network.Status == InspectionStatus.TimedOut && timed.Cpu.Status == InspectionStatus.TimedOut && timed.Memory.Status == InspectionStatus.TimedOut && timed.Gpu.Status == InspectionStatus.TimedOut,
        "Fake clock expiry must mark unfinished sections timed out.");
}
var cappedReader = new FakeProcesses();
var capSampler = new FakeSampler { GpuSample = Sample(manyProcesses) };
using (var provider = new WindowsSystemCounterProvider(capSampler, new FakeInterfaces(fakeClock), cappedReader, new FakeInventory(), fakeClock))
{
    var capped = await provider.CaptureAsync(CancellationToken.None);
    Check(capped.Gpu.Status == InspectionStatus.Truncated && capped.Gpu.Processes.Count == 256 && cappedReader.ReadPids.Count == 256 && !cappedReader.ReadPids.Contains(257),
        "Identity reads must be limited to capped process set.");
}
var refreshClock = new FakeClock();
var refreshInventory = new FakeInventory { Result = Inventory() };
using (var provider = new WindowsSystemCounterProvider(new FakeSampler(), new FakeInterfaces(refreshClock), new FakeProcesses(), refreshInventory, refreshClock))
{
    await provider.CaptureAsync(CancellationToken.None);
    refreshClock.Ticks = 29999;
    await provider.CaptureAsync(CancellationToken.None);
    Check(refreshInventory.Calls == 1, "Unknown LUID must not refresh inventory before 30 seconds.");
    refreshClock.Ticks = 30000;
    await provider.CaptureAsync(CancellationToken.None);
    Check(refreshInventory.Calls == 2, "Unknown LUID must refresh inventory at 30 seconds.");
}

var unavailableInventory = new DxgiAdapterResult(DxgiInventoryStatus.Failed, unchecked((int)0x80004005), [], false);
var recoveredInventory = Inventory(new DxgiAdapter("Recovered adapter", 1, 100, 200, 0));
var fullRetryClock = new FakeClock();
var fullRetryInventory = new FakeInventory { Result = unavailableInventory };
var fullRetrySampler = new FakeSampler { GpuSample = Data.GpuEmpty() };
using (var provider = new WindowsSystemCounterProvider(fullRetrySampler, new FakeInterfaces(fullRetryClock), new FakeProcesses(), fullRetryInventory, fullRetryClock))
{
    var initial = await provider.CaptureAsync(CancellationToken.None);
    Check(fullRetryInventory.Calls == 1 && initial.Gpu.InventoryStatus == InspectionStatus.Failed,
        "Full capture must retain the initial failed inventory result when no counter LUIDs exist.");
    fullRetryInventory.Result = recoveredInventory;
    fullRetryClock.Ticks = 29999;
    var early = await provider.CaptureAsync(CancellationToken.None);
    Check(fullRetryInventory.Calls == 1 && early.Gpu.InventoryStatus == InspectionStatus.Failed,
        "Full capture must not retry failed inventory before 30 seconds without counter LUIDs.");
    fullRetryClock.Ticks = 30000;
    var recovered = await provider.CaptureAsync(CancellationToken.None);
    Check(fullRetryInventory.Calls == 2 && recovered.Gpu.InventoryStatus == InspectionStatus.Available &&
        recovered.Gpu.Adapters.Single().Description == "Recovered adapter",
        "Full capture must retry and publish recovered inventory at 30 seconds without counter LUIDs.");
}

var gpuOnlyRetryClock = new FakeClock();
var gpuOnlyRetryInventory = new FakeInventory { Result = unavailableInventory };
var gpuOnlyRetrySampler = new FakeSampler { GpuSample = Data.GpuEmpty() };
var unusedFullSampler = new FakeSampler();
using (var provider = new WindowsSystemCounterProvider(unusedFullSampler, new FakeInterfaces(gpuOnlyRetryClock), new FakeProcesses(),
    gpuOnlyRetryInventory, gpuOnlyRetryClock, gpuOnlyRetrySampler))
{
    var initial = await provider.CaptureGpuAsync(CancellationToken.None);
    Check(gpuOnlyRetryInventory.Calls == 1 && initial.InventoryStatus == InspectionStatus.Failed && unusedFullSampler.Calls == 0,
        "GPU-only capture must retain its initial failed inventory result independently of full capture.");
    gpuOnlyRetryInventory.Result = recoveredInventory;
    gpuOnlyRetryClock.Ticks = 29999;
    var early = await provider.CaptureGpuAsync(CancellationToken.None);
    Check(gpuOnlyRetryInventory.Calls == 1 && early.InventoryStatus == InspectionStatus.Failed,
        "GPU-only capture must not retry failed inventory before 30 seconds without counter LUIDs.");
    gpuOnlyRetryClock.Ticks = 30000;
    var recovered = await provider.CaptureGpuAsync(CancellationToken.None);
    Check(gpuOnlyRetryInventory.Calls == 2 && recovered.InventoryStatus == InspectionStatus.Available &&
        recovered.Adapters.Single().Description == "Recovered adapter",
        "GPU-only capture must retry and publish recovered inventory at 30 seconds without counter LUIDs.");
}
Console.WriteLine("PASS: cancellation without publication, disposed rejection, concurrent serialization, fake-clock budget, capped identity reads and inventory refresh throttle.");

var isolatedClock = new FakeClock();
var fullSampler = new FakeSampler();
var gpuSampler = new FakeSampler();
var isolatedProcesses = new FakeProcesses();
var isolatedInterfaces = new FakeInterfaces(isolatedClock);
var isolatedInventory = new FakeInventory();
using (var provider = new WindowsSystemCounterProvider(fullSampler, isolatedInterfaces, isolatedProcesses, isolatedInventory, isolatedClock, gpuSampler))
{
    using var canceled = new CancellationTokenSource();
    canceled.Cancel();
    await ExpectAsync<OperationCanceledException>(() => provider.CaptureGpuAsync(canceled.Token));
    Check(gpuSampler.Calls == 0, "Canceled GPU-only captures must not sample.");
    var firstGpu = await provider.CaptureGpuAsync(CancellationToken.None);
    Check(firstGpu.Adapters.Single().HeadlineUtilizationPercent.State == CounterValueState.Warmup && fullSampler.Calls == 0 && isolatedInterfaces.Calls == 0,
        "GPU-only capture must warm up without sampling CPU, disks, memory or network.");
    var firstFull = await provider.CaptureAsync(CancellationToken.None);
    Check(firstFull.IsWarmup && firstFull.Interval is null, "GPU-only capture must not prime full-capture rates.");
    isolatedClock.Ticks = 2000;
    isolatedProcesses.CreationTime = 456;
    await provider.CaptureAsync(CancellationToken.None);
    isolatedProcesses.CreationTime = 123;
    var secondGpu = await provider.CaptureGpuAsync(CancellationToken.None);
    Check(secondGpu.Adapters.Single().HeadlineUtilizationPercent.Value == 20 && secondGpu.Processes.Single().Identity is not null,
        "Interleaved full captures must not change GPU-only warmup, engine deltas or identity history.");
    isolatedClock.Ticks = 4000;
    var secondFull = await provider.CaptureAsync(CancellationToken.None);
    Check(secondFull.Interval == TimeSpan.FromSeconds(2) && secondFull.Gpu.Processes.Single().Identity is null,
        "GPU-only captures must not change full-capture intervals or identity history.");
    Check(gpuSampler.Sections.All(s => s == CounterSection.Gpu) && isolatedInventory.Calls == 2,
        "GPU-only sampler must read only GPU counters and maintain its own inventory cache.");
    provider.Dispose();
    Check(gpuSampler.DisposeCount == 1 && fullSampler.DisposeCount == 1, "Both independent samplers must be disposed exactly once.");
    await ExpectAsync<ObjectDisposedException>(() => provider.CaptureGpuAsync(CancellationToken.None));
}
Console.WriteLine("PASS: GPU-only sampling excludes other sections and isolates warmup, intervals, identity history and inventory from full captures.");

foreach (var error in new Exception[] { new InvalidOperationException("GPU failure"), new ObjectDisposedException("GPU shutdown"), new OperationCanceledException("Unrelated GPU cancellation") })
{
    using var monitor = new WindowsMonitorProvider(_ => Task.FromException<GpuCounterSection>(error));
    var snapshot = await monitor.CaptureAsync(CancellationToken.None);
    Check(snapshot.Processes.Count > 0 && snapshot.Memory.PhysicalTotalBytes is > 0 && snapshot.GpuAdapters.Count == 0 && snapshot.GpuPercent is null,
        "GPU failures must leave the rest of the monitor snapshot available without substituted utilization.");
}
using (var canceled = new CancellationTokenSource())
using (var monitor = new WindowsMonitorProvider(token =>
{
    canceled.Cancel();
    return Task.FromCanceled<GpuCounterSection>(token);
}))
    await ExpectAsync<OperationCanceledException>(() => monitor.CaptureAsync(canceled.Token));
Console.WriteLine("PASS: monitor contains GPU failures including disposed providers and preserves caller cancellation.");

static IEnumerable<CounterValue> CounterValues(object? value)
{
    if (value is CounterValue counter) { yield return counter; yield break; }
    if (value is null || value is string || value.GetType().IsValueType) yield break;
    if (value is System.Collections.IEnumerable items)
    {
        foreach (var item in items) foreach (var nested in CounterValues(item)) yield return nested;
        yield break;
    }
    foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        foreach (var nested in CounterValues(property.GetValue(value))) yield return nested;
}

static void ValidateValue(CounterValue value)
{
    Check(value.State == CounterValueState.Measured ? value.Value is { } number && double.IsFinite(number) : value.Value is null && !string.IsNullOrWhiteSpace(value.Reason),
        "Every measured value must be finite; every absent value must have a non-Measured state and reason.");
}
static bool Near(double left, double right) => Math.Abs(left - right) <= 1e-8 * Math.Max(1, Math.Max(Math.Abs(left), Math.Abs(right)));

using (var live = new WindowsSystemCounterProvider())
{
    for (var capture = 1; capture <= 3; capture++)
    {
        if (capture > 1) await Task.Delay(1500);
        var snapshot = await live.CaptureAsync(CancellationToken.None);
        Check(snapshot.IsWarmup == (capture == 1) && (capture == 1 ? snapshot.Interval is null : snapshot.Interval > TimeSpan.Zero), "Live capture warmup and measured interval must be honest.");
        foreach (var value in CounterValues(snapshot)) ValidateValue(value);
        if (capture == 1)
        {
            var rates = new List<CounterValue> { snapshot.Cpu.TotalProcessorUtilityPercent, snapshot.Cpu.ContextSwitchesPerSecond, snapshot.Network.TotalReceiveBytesPerSecond, snapshot.Memory.PageFaultsPerSecond };
            if (snapshot.Disks.Total is { } total) rates.Add(total.ReadsPerSecond);
            rates.AddRange(snapshot.Gpu.Adapters.Select(a => a.HeadlineUtilizationPercent));
            Check(rates.All(v => v.State == CounterValueState.Warmup), "First live rate sample must be warmup, never measured zero.");
        }
        Check(snapshot.Disks.Total is null || snapshot.Disks.Total.IsAggregate && snapshot.Disks.Total.Instance == "_Total", "Disk total must identify its aggregate.");
        Check(snapshot.Disks.Disks.All(d => !d.IsAggregate && d.Instance != "_Total"), "Disk rows must exclude aggregate.");
        Check(snapshot.Network.Adapters.All(n => n.IncludedInTotal == (n.Kind == NetworkInterfaceKind.Hardware)), "Only hardware rows may enter network total.");
        foreach (var receive in new[] { true, false })
        {
            var total = receive ? snapshot.Network.TotalReceiveBytesPerSecond : snapshot.Network.TotalTransmitBytesPerSecond;
            if (total.State != CounterValueState.Measured) continue;
            var included = snapshot.Network.Adapters.Where(n => n.IncludedInTotal).Select(n => receive ? n.ReceiveBytesPerSecond : n.TransmitBytesPerSecond).ToArray();
            Check(included.Length > 0 && included.All(v => v.State == CounterValueState.Measured) && Near(total.Value!.Value, included.Sum(v => v.Value!.Value)), "Measured network total must equal complete included sum.");
        }
        Check(snapshot.Cpu.LogicalProcessors.All(c => !c.Instance.Contains("_Total", StringComparison.OrdinalIgnoreCase)), "Live core list cannot contain group/system totals.");
        foreach (var adapter in snapshot.Gpu.Adapters)
            if (adapter.HeadlineUtilizationPercent.State == CounterValueState.Measured)
                Check(adapter.Engines.Count > 0 && adapter.Engines.All(e => e.UtilizationPercent.State == CounterValueState.Measured) && Near(adapter.HeadlineUtilizationPercent.Value!.Value, adapter.Engines.Max(e => e.UtilizationPercent.Value!.Value)), "GPU adapter headline must be max, not sum.");
        foreach (var process in snapshot.Gpu.Processes)
        {
            Check(process.Identity is null || process.IdentityStatus == InspectionStatus.Available && process.Identity.Value.Pid == process.Pid && process.Identity.Value.CreationTimeFileTime is > 0, "Non-null GPU process identity must be validated.");
            if (process.MaxUtilizationPercent.State == CounterValueState.Measured)
                Check(process.Engines.Count > 0 && process.Engines.All(e => e.UtilizationPercent.State == CounterValueState.Measured) && Near(process.MaxUtilizationPercent.Value!.Value, process.Engines.Max(e => e.UtilizationPercent.Value!.Value)), "GPU process headline must be max, not sum.");
        }
        Check(snapshot.Memory.CompressionStatus is InspectionStatus.Available or InspectionStatus.Unsupported, "Live development host compression should be available or honestly unsupported.");
        Console.WriteLine($"PASS: live {capture} disks={snapshot.Disks.Status}; rows={snapshot.Disks.Disks.Count}; aggregate={snapshot.Disks.Total is not null}; warmup={snapshot.IsWarmup}");
        Console.WriteLine($"PASS: live {capture} network={snapshot.Network.Status}; rows={snapshot.Network.Adapters.Count}; hardware={snapshot.Network.Adapters.Count(n => n.IncludedInTotal)}; totals={snapshot.Network.TotalReceiveBytesPerSecond.State}/{snapshot.Network.TotalTransmitBytesPerSecond.State}");
        Console.WriteLine($"PASS: live {capture} CPU={snapshot.Cpu.Status}; cores={snapshot.Cpu.LogicalProcessors.Count}; utility={snapshot.Cpu.TotalProcessorUtilityPercent.State}");
        Console.WriteLine($"PASS: live {capture} memory={snapshot.Memory.Status}; compression={snapshot.Memory.CompressionStatus}");
        Console.WriteLine($"PASS: live {capture} GPU={snapshot.Gpu.Status}; adapters={snapshot.Gpu.Adapters.Count}; processes={snapshot.Gpu.Processes.Count}; malformed={snapshot.Gpu.MalformedInstanceCount}; inventory={snapshot.Gpu.InventoryStatus}");
    }
}

sealed class FakeClock : ISystemCounterClock
{
    internal long Ticks;
    public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch.AddMilliseconds(Ticks);
    public long Timestamp => Ticks;
    public TimeSpan Elapsed(long start, long end) => TimeSpan.FromMilliseconds(end - start);
}
sealed class FakeQuery : IPdhCounterQuery
{
    internal uint ArrayStatus;
    internal uint CollectStatus;
    internal int DisposeCount;
    internal Action? AfterCollect;
    internal readonly Dictionary<string, uint> AddStatuses = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, uint> ArrayStatuses = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, Queue<PdhArrayResult>> ArrayResults = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, Queue<PdhSingleResult>> SingleResults = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, int> AddCounts = new(StringComparer.Ordinal);
    internal readonly Dictionary<string, int> RemoveCounts = new(StringComparer.Ordinal);
    internal readonly HashSet<string> Registered = new(StringComparer.Ordinal);
    internal readonly List<HashSet<string>> RegisteredAtCollect = [];
    public uint AddCounter(string path)
    {
        AddCounts[path] = AddCounts.GetValueOrDefault(path) + 1;
        var status = AddStatuses.GetValueOrDefault(path);
        if (status == 0) Registered.Add(path);
        return status;
    }
    public uint RemoveCounter(string path)
    {
        RemoveCounts[path] = RemoveCounts.GetValueOrDefault(path) + 1;
        Registered.Remove(path);
        return 0;
    }
    public uint Collect()
    {
        RegisteredAtCollect.Add(new(Registered, StringComparer.Ordinal));
        AfterCollect?.Invoke();
        return CollectStatus;
    }
    public PdhArrayResult ReadArray(string path, int maximumItems)
    {
        if (ArrayResults.TryGetValue(path, out var queued) && queued.Count != 0) return queued.Dequeue();
        var status = ArrayStatuses.GetValueOrDefault(path, ArrayStatus);
        return new(status, status == 0 ? [new("_Total", 1, 0)] : [], false);
    }
    public PdhSingleResult ReadSingle(string path) => SingleResults.TryGetValue(path, out var queued) && queued.Count != 0
        ? queued.Dequeue() : new(0, 0, 1);
    public void Dispose() { DisposeCount++; Registered.Clear(); }
}
sealed class FakeSampler : ISystemCounterSampler
{
    internal readonly List<CounterSection> Sections = [];
    internal Action<CounterSection, CancellationToken>? BeforeSample;
    internal CounterSample? GpuSample;
    internal int DelayMilliseconds, Calls, DisposeCount, MaxActive;
    private int active;
    public CounterSample Sample(CounterSection section, CancellationToken token, Func<bool> expired)
    {
        Sections.Add(section);
        var current = Interlocked.Increment(ref active);
        MaxActive = Math.Max(MaxActive, current);
        Interlocked.Increment(ref Calls);
        try
        {
            BeforeSample?.Invoke(section, token);
            token.ThrowIfCancellationRequested();
            if (DelayMilliseconds > 0) Thread.Sleep(DelayMilliseconds);
            return section == CounterSection.Gpu ? GpuSample ?? Data.Gpu() : Data.Section(section);
        }
        finally { Interlocked.Decrement(ref active); }
    }
    public void Dispose() => DisposeCount++;
}
sealed class FakeInterfaces(FakeClock clock) : IInterfaceTableSource
{
    internal int Calls;
    public InterfaceSample Read() { Calls++; return new(new(0, [new(1, 5, "hardware", "test", 6, 0, 0, 1, 1, 1000, 1000, 0, 0)], false), clock.Timestamp); }
}
sealed class FakeProcesses : IProcessCreationReader
{
    internal long CreationTime = 123;
    internal readonly List<int> ReadPids = [];
    public ProcessCreationResult Read(int pid) { ReadPids.Add(pid); return new(ProcessCreationStatus.Available, CreationTime, 0); }
    public ProcessImageResult ReadImage(int pid) => new(false, 31);
}
sealed class FakeInventory : IDxgiInventorySource
{
    internal int Calls;
    internal DxgiAdapterResult Result = new(DxgiInventoryStatus.Available, 0, [new("adapter", 1, 100, 200, 2)], false);
    public DxgiAdapterResult Read() { Calls++; return Result; }
}
static class Data
{
    internal static CounterSample GpuEmpty() => new(false, SystemCounterPaths.Arrays(CounterSection.Gpu).ToDictionary(p => p, _ => new PdhArrayResult(0, [], false)), new Dictionary<string, PdhSingleResult>());
    internal static CounterSample Gpu()
    {
        var arrays = GpuEmpty().Arrays.ToDictionary(k => k.Key, k => k.Value);
        arrays[SystemCounterPaths.Engine] = new(0, [new("pid_10_luid_0x00000000_0x00000001_phys_0_eng_0_engtype_3D", 20, 0)], false);
        foreach (var path in new[] { SystemCounterPaths.AdapterDedicated, SystemCounterPaths.AdapterShared }) arrays[path] = new(0, [new("luid_0x00000000_0x00000001_phys_0", 100, 0)], false);
        foreach (var path in new[] { SystemCounterPaths.ProcessDedicated, SystemCounterPaths.ProcessShared }) arrays[path] = new(0, [new("pid_10_luid_0x00000000_0x00000001_phys_0", 100, 0)], false);
        return new(false, arrays, new Dictionary<string, PdhSingleResult>());
    }
    internal static CounterSample Section(CounterSection section)
    {
        var arrays = SystemCounterPaths.Arrays(section).ToDictionary(p => p, _ => new PdhArrayResult(0, [new("_Total", 1, 0)], false));
        var singles = SystemCounterPaths.Singles(section).ToDictionary(p => p, _ => new PdhSingleResult(0, 0, 1));
        if (section == CounterSection.Memory)
        {
            singles[SystemCounterPaths.CompressionPid] = new(0xC0000BC6, NativeCounterMethods.PDH_CSTATUS_NO_INSTANCE, null);
            singles[SystemCounterPaths.CompressionWorkingSet] = new(0xC0000BC6, NativeCounterMethods.PDH_CSTATUS_NO_INSTANCE, null);
        }
        return new(false, arrays, singles);
    }
}
