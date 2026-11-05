using SystemExplorer.Core;

var tests = new (string Name, Action Run)[]
{
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
    Identity = new(pid, started), ParentPid = parent, Name = "example", ExecutablePath = @"C:\Apps\example.exe",
    UserSid = "S-1-5-21-example", SessionId = 1, PrivateBytes = 100, WorkingSetBytes = 500
};
static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }
static void Equal(double? expected, double? actual) => Check(expected == actual);
static MonitorSnapshot Snapshot(int seconds, ProcessSnapshot process) => new()
{
    CapturedAt = DateTimeOffset.UnixEpoch.AddSeconds(seconds), Processes = [process]
};
