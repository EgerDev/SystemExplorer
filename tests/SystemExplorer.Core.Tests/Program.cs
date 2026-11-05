using SystemExplorer.Core;
using SystemExplorer.App;

var tests = new (string Name, Action Run)[]
{
    ("Application descriptions reject unsafe labels and uncertain attribution", () =>
    {
        Check(ApplicationDescriptions.Normalize(" Example app ") == "Example app");
        Check(ApplicationDescriptions.Normalize("Example\napp") is null);
        Check(ApplicationDescriptions.Normalize("Example\u202Eapp") is null);
        var descriptions = new ApplicationDescriptions();
        Check(descriptions.LoadAsync(P(10, 100) with { Identity = new(10, null) }, default).GetAwaiter().GetResult() is null);
        Check(descriptions.LoadAsync(P(10, 100) with { Name = "example.exe", ExecutablePath = @"\\server\share\example.exe" }, default).GetAwaiter().GetResult() is null);
    }),
    ("Overview preserves physical/private and zero/unavailable distinctions", () =>
    {
        var data = new OverviewData();
        var history = new DiagnosticsHistory();
        Check(data.Create(null, history) is null);
        data.Observe(Snapshot(0, P(10, 100) with { PrivateBytes = 10_000 }) with
        {
            Memory = new() { PhysicalTotalBytes = 100, PhysicalAvailableBytes = 25 },
            TotalCpuPercent = 0, DiskReadBytesPerSecond = 0, DiskWriteBytesPerSecond = 0
        });
        var reading = data.Create(null, history)!;
        Equal(75d, reading.PhysicalMemoryPercent);
        Equal(0d, reading.CpuPercent);
        Equal(0d, reading.DiskTotalBytesPerSecond);
        Check(reading.ObservedEndpointCount is null && reading.Consumers.Single().PrivateBytes == 10_000);
        Check(reading.DiskTrend.SampleCount == 1 && reading.DiskTrend.Segments.Single().Single().Y == 0);
    }),
    ("Overview partial groups stay unavailable and rank after known zero", () =>
    {
        var data = new OverviewData();
        data.Observe(Snapshot(0, P(10, 100), P(11, 200, 10) with { PrivateBytes = null }, P(20, 300) with { PrivateBytes = 0 }));
        var reading = data.Create(null, new())!;
        Check(reading.Consumers.Count == 2 && reading.Consumers[0].Key.Pid == 20 && reading.Consumers[1].PrivateBytes is null);
        Check(data.Create(null, new(), search: "11")!.Consumers.Single().Members.Count == 2);
        Check(data.Create(null, new(), search: "no-such-app")!.Consumers.Count == 0);
    }),
    ("Overview history is bounded and does not duplicate rerenders", () =>
    {
        var data = new OverviewData();
        for (var i = 0; i < 250; i++) data.Observe(Snapshot(i * 2, P(10, 100)) with { TotalCpuPercent = 10 });
        data.Observe(Snapshot(498, P(10, 100)) with { TotalCpuPercent = 90 });
        data.Observe(Snapshot(0, P(10, 100)) with { TotalCpuPercent = 90 });
        var reading = data.Create(null, new())!;
        Check(reading.CpuTrend.SampleCount == 180 && reading.CpuPercent == 10);
        Check(data.Create(null, new(), ConsumerRanking.Cpu)!.CpuTrend.SampleCount == 180);
    }),
    ("Overview charts use elapsed time and break on missing samples and pauses", () =>
    {
        var data = new OverviewData();
        foreach (var (at, cpu) in new (int, double?)[] { (0, 10), (2, 20), (4, null), (6, 30), (20, 40) })
            data.Observe(Snapshot(at, P(10, 100)) with { TotalCpuPercent = cpu });
        var trend = data.Create(null, new())!.CpuTrend;
        Check(trend.Segments.Count == 3 && trend.Segments[0].Count == 2 && trend.Segments[1].Count == 1);
        Equal(0.1, trend.Segments[0][1].X);
        Equal(0.3, trend.Segments[1][0].X);
        Equal(1d, trend.Segments[2][0].X);
        Equal(100d, trend.Maximum);
        Equal(10d, trend.MinimumObserved);
        Equal(40d, trend.MaximumObserved);
        data.BreakContinuity();
        data.Observe(Snapshot(22, P(10, 100)) with { TotalCpuPercent = 45 });
        Check(data.Create(null, new())!.CpuTrend.Segments.Count == 4);
        Check(data.Create(null, new(), search: "Friendly application", descriptionLookup: _ => "Friendly application")!.Consumers.Count == 1);
    }),
    ("Overview validates rates and timestamps without estimating network bandwidth", () =>
    {
        var data = new OverviewData();
        var snapshot = Snapshot(0, P(10, 100)) with { TotalCpuPercent = double.NaN, DiskReadBytesPerSecond = 5, DiskWriteBytesPerSecond = null };
        data.Observe(snapshot);
        var relationships = new RelationshipSnapshot { CapturedAt = snapshot.CapturedAt };
        var reading = data.Create(relationships, new())!;
        Check(reading.CpuPercent is null && reading.DiskTotalBytesPerSecond is null && reading.DiskReadBytesPerSecond == 5);
        Check(reading.ObservedEndpointCount == 0);
        Check(data.Create(relationships with { CapturedAt = snapshot.CapturedAt.AddSeconds(-11) }, new())!.ObservedEndpointCount is null);
        Check(data.Create(relationships with { CapturedAt = snapshot.CapturedAt.AddSeconds(11) }, new())!.ObservedEndpointCount is null);
        data.Observe(snapshot with { CapturedAt = snapshot.CapturedAt.AddSeconds(2), DiskReadBytesPerSecond = double.PositiveInfinity, DiskWriteBytesPerSecond = 2 });
        Check(data.Create(null, new())!.DiskTotalBytesPerSecond is null);
    }),
    ("PID reuse resets CPU", () => Equal(null, Sampling.CpuPercent(new(20, 10), new(20, 30), 0, 100, 1, 4))),
    ("CPU normalized to total machine", () => Equal(25d, Sampling.CpuPercent(new(20, 10), new(20, 10), 0, 10_000_000, 1, 4))),
    ("Unknown identity cannot sample", () => Equal(null, Sampling.CpuPercent(new(20, null), new(20, null), 0, 100, 1, 4))),
    ("Counter rollback unavailable", () => Equal(null, Sampling.Rate(100, 0, 1))),
    ("Grouping uses ancestry path and owner", () =>
    {
        var groups = ProcessGrouping.Create([P(10, 100), P(11, 200, 10), P(12, 300, null), P(13, 400, 10) with { UserSid = "other" }]);
        Check(groups.Count == 3 && groups.Single(g => g.Key.Pid == 10).Members.Count == 2);
    }),
    ("Reused parent PID is not related", () => Check(ProcessGrouping.Create([P(10, 300), P(11, 200, 10)]).Count == 2)),
    ("Missing metadata remains singleton", () => Check(ProcessGrouping.Create([P(10, 100), P(11, 200, 10) with { UserSid = null }]).Count == 2)),
    ("Unknown sessions are not evidence of shared ownership", () =>
    {
        Check(ProcessGrouping.Create([P(10, 100) with { SessionId = null },
            P(11, 200, 10) with { SessionId = null }]).Count == 2);
        Check(ProcessGrouping.Create([P(10, 100), P(11, 200, 10) with { SessionId = null }]).Count == 2);
        Check(ProcessGrouping.Create([P(10, 100) with { SessionId = 0 },
            P(11, 200, 10) with { SessionId = 0 }]).Single().Members.Count == 2);
    }),
    ("Group memory is private commit, not working set", () =>
    {
        var group = ProcessGrouping.Create([P(10, 100), P(11, 200, 10)]).Single();
        Check(group.PrivateBytes == 200);
        Check(ProcessGrouping.Create([P(10, 100), P(11, 200, 10) with { PrivateBytes = null }]).Single().PrivateBytes is null);
    }),
    ("Physical accounting rejects invalid available", () => Check(new MemorySnapshot { PhysicalTotalBytes = 10, PhysicalAvailableBytes = 11 }.PhysicalUsedBytes is null)),
    ("History bounded to 180 samples", () =>
    {
        var history = new DiagnosticsHistory();
        for (var i = 0; i < 250; i++) history.Add(Snapshot(i * 2, P(10, 100)));
        Check(history.System.Count == 180);
    }),
    ("Growth evidence needs sustained same-lifetime samples", () =>
    {
        var history = new DiagnosticsHistory();
        MonitorSnapshot sample = Snapshot(0, P(10, 100));
        for (var i = 0; i <= 30; i++)
        {
            sample = Snapshot(i * 2, P(10, 100) with { PrivateBytes = (100UL + (ulong)i * 2) * 1024 * 1024 });
            history.Add(sample);
        }
        Check(history.Explain(sample).Any(i => i.Title.StartsWith("Sustained")));
        sample = Snapshot(62, P(10, 200) with { PrivateBytes = 200UL * 1024 * 1024 });
        history.Add(sample);
        Check(!history.Explain(sample).Any(i => i.Title.StartsWith("Sustained")));
    }),
    ("Unavailable memory breaks trend continuity", () =>
    {
        var history = new DiagnosticsHistory();
        for (var i = 0; i <= 30; i++) history.Add(Snapshot(i * 2, P(10, 100) with { PrivateBytes = (100UL + (ulong)i * 2) * 1024 * 1024 }));
        history.Add(Snapshot(62, P(10, 100) with { PrivateBytes = null }));
        var sample = Snapshot(64, P(10, 100) with { PrivateBytes = 300UL * 1024 * 1024 });
        history.Add(sample);
        Check(!history.Explain(sample).Any(i => i.Title.StartsWith("Sustained")));
    }),
    ("Replacement heuristic uses distinct lifetimes and resets after gaps", () =>
    {
        var history = new DiagnosticsHistory();
        MonitorSnapshot sample = Snapshot(0, P(10, 100));
        for (var i = 0; i < 4; i++) { sample = Snapshot(i * 2, P(10, 100 + i)); history.Add(sample); }
        Check(history.Explain(sample).Any(i => i.Title.StartsWith("Repeated")));
        sample = Snapshot(30, P(10, 200)); history.Add(sample);
        Check(!history.Explain(sample).Any(i => i.Title.StartsWith("Repeated")));
    }),
    ("Parallel and unknown processes do not imply restarts", () =>
    {
        var history = new DiagnosticsHistory();
        MonitorSnapshot sample = Snapshot(0, P(10, 100));
        for (var i = 0; i < 8; i++)
        {
            sample = Snapshot(i * 2, P(10, 100 + i)) with { Processes = [P(10, 100 + i), P(11, 200 + i)] };
            history.Add(sample);
        }
        Check(!history.Explain(sample).Any(i => i.Title.StartsWith("Repeated")));
        for (var i = 8; i < 16; i++)
        {
            sample = Snapshot(i * 2, P(10, 100 + i) with { Identity = new(10, null) }); history.Add(sample);
        }
        Check(!history.Explain(sample).Any(i => i.Title.StartsWith("Repeated")));
    }),
    ("Duplicate timestamps do not grow history", () =>
    {
        var history = new DiagnosticsHistory();
        history.Add(Snapshot(0, P(10, 100)));
        history.Add(Snapshot(0, P(10, 100)));
        Check(history.System.Count == 1);
    })
};
var failures = 0;
foreach (var (name, run) in tests)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception ex) { failures++; Console.Error.WriteLine($"FAIL {name}: {ex.Message}"); }
}
Console.WriteLine($"{tests.Length - failures}/{tests.Length} passed");
return failures == 0 ? 0 : 1;

static ProcessSnapshot P(int pid, long started, int? parent = null) => new()
{
    Identity = new(pid, started),
    ParentPid = parent,
    Name = "example",
    ExecutablePath = @"C:\Apps\example.exe",
    UserSid = "S-1-5-21-example",
    SessionId = 1,
    PrivateBytes = 100,
    WorkingSetBytes = 500
};
static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }
static void Equal(double? expected, double? actual) => Check(expected == actual);
static MonitorSnapshot Snapshot(int seconds, params ProcessSnapshot[] processes) => new()
{
    CapturedAt = DateTimeOffset.UnixEpoch.AddSeconds(seconds),
    Processes = processes
};
