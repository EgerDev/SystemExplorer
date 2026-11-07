using System.Reflection;
using SystemExplorer.Core;
using SystemExplorer.App;

var tests = new (string Name, Action Run)[]
{
    ("CSV exports have unique headers and round-trip RAM tabs, Hidden, Storage and Trace", CsvExportTests.Run),
    ("Trace and Storage CSV capture metadata occurs once with event-only linear growth", CsvExportTests.MetadataSize),
    ("Filtered Trace JSON includes only referenced process lifetimes and ancestors with valid remapped indices", TraceJsonExportTests.Run),
    ("Storage denied child folders mark completed scans partial and preserve evidence", StorageCompletenessTests.Run),
    ("Task cross-view comparisons never infer absence from unavailable or truncated views", HiddenTaskAvailabilityTests.Run),
    ("RAM process status distinguishes Idle and unverifiable identities from exited or reused lifetimes", () =>
    {
        foreach (long? creation in new long?[] { null, 0 })
        {
            Check(RamAttributionPresentation.ProcessStatusName(new(0, creation), RamReadingStatus.IdentityMismatch) == "Idle (no creation time)");
            Check(RamAttributionPresentation.ProcessStatusName(new(123, creation), RamReadingStatus.IdentityMismatch) == "Unverifiable identity");
        }
        Check(RamAttributionPresentation.ProcessStatusName(new(123, 1), RamReadingStatus.IdentityMismatch) == "Exited / PID reused");
        foreach (var status in Enum.GetValues<RamReadingStatus>().Where(s => s != RamReadingStatus.IdentityMismatch))
            Check(RamAttributionPresentation.ProcessStatusName(new(123, null), status) == RamAttributionPresentation.StatusName(status));
    }),
    ("Activity trace Core model", ActivityTraceTests.Run),
    ("Byte and rate formatting preserve small nonzero values and exact zero", () =>
    {
        Check(MetricFormatting.Bytes(0) == "0 B" && MetricFormatting.Rate(0) == "0 B/s");
        Check(MetricFormatting.Rate(-0d) == "0 B/s");
        Check(MetricFormatting.Bytes(1) == "1 B" && MetricFormatting.Bytes(191) == "191 B");
        Check(MetricFormatting.Bytes(1024) == "1.0 KiB" && MetricFormatting.Bytes(1536) == "1.5 KiB");
        Check(DiagnosticData.Rate(191) == "191 B/s" && DiagnosticData.Rate(1024) == "1.0 KiB/s");
        Check(DiagnosticData.Bytes(191) == MetricFormatting.Bytes(191));
        foreach (var value in new[] { double.Epsilon, 0.000001, 0.005, 0.01, 0.5, 1, 191, 1023, 1024, 1024 * 1024d })
        {
            var formatted = MetricFormatting.Rate(value);
            Check(!formatted.StartsWith("0 ") && !formatted.StartsWith("0.0 ") && DiagnosticData.Rate(value) == formatted);
        }
        Check(MetricFormatting.Bytes(null) == "—" && DiagnosticData.Rate(null) == "—");
        Check(MetricFormatting.Rate(double.NaN) == "—" && MetricFormatting.Rate(double.PositiveInfinity) == "—" && MetricFormatting.Rate(-1) == "—");
    }),
    ("Chart scales round up by binary units and hold until below half", () =>
    {
        Check(ChartScale.Next(0, 0) == 1 && ChartScale.Next(0, 191) == 200);
        Check(ChartScale.Next(0, 501) == 1000 && ChartScale.Next(0, 1500, true) == 2048);
        Check(ChartScale.Next(1000, 1001, true) == 1024);
        Check(ChartScale.Next(200, 201) == 500);
        Check(ChartScale.Next(1000, 800) == 1000 && ChartScale.Next(1000, 500) == 1000);
        Check(ChartScale.Next(1000, 499) == 500 && ChartScale.Next(1000, 0) == 1);
        Check(ChartScale.Next(10240, 5120, true) == 10240 && ChartScale.Next(10240, 4096, true) == 5120);
        Check(ChartScale.Next(0, 14, minimum: 100) == 100 && ChartScale.Next(100, 101, minimum: 100) == 200);
    }),
    ("Chart scales retain unavailable history and bound extreme values", () =>
    {
        foreach (var invalid in new[] { double.NaN, double.PositiveInfinity, -1d })
            Check(ChartScale.Next(500, invalid) == 500 && ChartScale.Next(0, invalid) == 1);
        foreach (var value in new[] { double.Epsilon, 1d, 1023, 1024, 1024 * 1024d, (double)ulong.MaxValue, double.MaxValue })
        {
            var scale = ChartScale.Next(0, value, true);
            Check(double.IsFinite(scale) && scale >= value && scale > 0);
        }
    }),
    ("Keyed live rows retain identities across colliding labels, refreshes and eviction", () =>
    {
        var first = (Key: 101L, Name: "12:00:00", Value: 1);
        var second = (Key: 102L, Name: "12:00:00", Value: 2);
        var live = new System.Collections.ObjectModel.ObservableCollection<(long Key, string Name, int Value)>();
        KeyedRows.Reconcile(live, new[] { (first.Key, first.Name, first.Value), (second.Key, second.Name, second.Value) }, r => r.Key);
        var changes = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        live.CollectionChanged += (_, e) => changes.Add(e.Action);
        KeyedRows.Reconcile(live, new[] { (101L, "12:00:00", 1), (102L, "12:00:00", 2), (103L, "12:00:01", 3) }, r => r.Item1);
        Check(live[1].Key == second.Key && changes.SequenceEqual(new[] { System.Collections.Specialized.NotifyCollectionChangedAction.Add }));
        changes.Clear();
        KeyedRows.Reconcile(live, new[] { (102L, "12:00:00", 2), (103L, "12:00:01", 3), (104L, "12:00:02", 4) }, r => r.Item1);
        Check(live[0].Key == second.Key && !changes.Contains(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        KeyedRows.Reconcile(live, live.Reverse().ToArray(), r => r.Key);
        Check(live.Select(r => r.Key).SequenceEqual(new long[] { 104, 103, 102 }));
        KeyedRows.Reconcile(live, live.Where(r => r.Value == 2).ToArray(), r => r.Key);
        Check(live.Single().Key == second.Key);
    }),
    ("Keyed live rows reuse equal objects, replace changes, clear and reject duplicate keys", () =>
    {
        var original = new KeyedTestRow(1, "sample");
        var live = new System.Collections.ObjectModel.ObservableCollection<KeyedTestRow> { original };
        KeyedRows.Reconcile(live, new[] { new KeyedTestRow(1, "sample") }, row => row.Key);
        Check(ReferenceEquals(live[0], original));
        var updated = new KeyedTestRow(1, "updated");
        KeyedRows.Reconcile(live, new[] { updated }, row => row.Key);
        Check(ReferenceEquals(live[0], updated));
        bool rejected = false;
        try { KeyedRows.Reconcile(live, new[] { updated, updated }, row => row.Key); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected && live.Count == 1 && ReferenceEquals(live[0], updated));
        KeyedRows.Reconcile(live, Array.Empty<KeyedTestRow>(), row => row.Key);
        Check(live.Count == 0);
    }),
    ("Inventory loading keeps only progress and applies the pending filter on completion", () =>
    {
        var state = new InventoryPresentationState();
        Check(state.Phase == InventoryPhase.Loading);
        state.Filter = "physical";
        state.Filter = "  DiSk 1  ";
        Check(state.StatusText("Collecting…", 0, 0, "Disk", false, false) == "Collecting…");
        Check(state.StatusText("Resampling…", 0, 0, "Disk", false, false) == "Resampling…");
        state.ShowRows(false);
        Check(state.Phase == InventoryPhase.Rows && state.Filter == "  DiSk 1  ");
        var rows = new[] { "All physical disks", "Disk 0", "Disk 1" };
        Check(rows.Where(state.Matches).SequenceEqual(new[] { "Disk 1" }));
        Check(state.StatusText("Captured", 1, 3, "Disk", true, false).Contains("1 of 3 observations"));
        Check(state.StatusText("Captured", 1, 3, "Disk", true, false).Contains("Frozen at capture"));
        state.ShowRows(true);
        Check(state.Filter == "  DiSk 1  ");
        state.ShowRows(false);
        Check(state.Filter == "" && rows.All(state.Matches));
    }),
    ("Inventory loading distinguishes empty, unmatched and failed captures", () =>
    {
        var state = new InventoryPresentationState();
        state.Filter = "missing";
        state.ShowRows(false);
        Check(!state.Matches("Disk 0"));
        Check(state.StatusText("Captured", 0, 3, "Disk", false, false).Contains("No matching observations"));
        Check(state.StatusText("Captured", 0, 0, "Disk", false, false).Contains("No observations returned"));
        Check(state.StatusText("Updated", 1, 1, "Time", true, true).Contains("Live; updates in place"));
        state.BeginLoading();
        Check(state.Filter == "" && state.Phase == InventoryPhase.Loading);
        state.Filter = "new capture";
        Check(state.StatusText("Collecting…", 0, 0, "Disk", false, false) == "Collecting…");
        state.ShowMessage();
        Check(state.Filter == "" && state.Phase == InventoryPhase.Message);
        foreach (var message in new[] { "Cancelled", "Unavailable: access denied" })
            Check(state.StatusText(message, 0, 0, "Disk", false, false) == message);
        state.ShowRows(false);
        Check(state.Filter == "" && state.Phase == InventoryPhase.Rows);
    }),
    ("Inventory identifier keys sort numerically with nulls last in both directions", () =>
    {
        var rows = new (string Text, decimal? Key)[] { ("0xFFC", 0xFFC), ("Unavailable", null), ("0x1010", 0x1010) };
        foreach (var descending in new[] { false, true })
        {
            var ordered = InventoryOrdering.Order(rows, r => r.Text, r => r.Key, true, descending).ToArray();
            Check(ordered[0].Text == (descending ? "0x1010" : "0xFFC"));
            Check(ordered[1].Text == (descending ? "0xFFC" : "0x1010") && ordered[2].Key is null);
            var text = InventoryOrdering.Order(rows.Where(r => r.Key is not null), r => r.Text, r => r.Key, false, descending).ToArray();
            Check(text[0].Text != ordered[0].Text);
        }
        Check((decimal)ulong.MaxValue == 18446744073709551615m);
    }),
    ("Inventory CPU keys order group and processor number rather than labels", () =>
    {
        var rows = new (string Text, decimal? Key)[] { ("CPU 0,10", 10), ("CPU 1,0", 65536), ("CPU 0,2", 2) };
        var ordered = InventoryOrdering.Order(rows, r => r.Text, r => r.Key, true, false).ToArray();
        Check(ordered.Select(r => r.Text).SequenceEqual(new[] { "CPU 0,2", "CPU 0,10", "CPU 1,0" }));
        Check(InventoryOrdering.Order(rows, r => r.Text, r => r.Key, false, false).First().Text == "CPU 0,10");
    }),
    ("Inventory ordering preserves empty, missing-key and text-only rows", () =>
    {
        foreach (var descending in new[] { false, true })
        {
            Check(!InventoryOrdering.Order(Array.Empty<string>(), r => r, _ => null, true, descending).Any());
            var rows = new[] { "beta", "Alpha" };
            Check(InventoryOrdering.Order(rows, r => r, _ => null, true, descending).SequenceEqual(rows));
            Check(InventoryOrdering.Order(rows, r => r, _ => null, false, descending).First() == (descending ? "beta" : "Alpha"));
        }
    }),
    ("Window minimum scales physical pixels and rounds up", () =>
    {
        Check(WindowSizing.MinimumPhysicalSize(96, 2000, 2000) == (900, 560));
        Check(WindowSizing.MinimumPhysicalSize(120, 2000, 2000) == (1125, 700));
        Check(WindowSizing.MinimumPhysicalSize(144, 2000, 2000) == (1350, 840));
        Check(WindowSizing.MinimumPhysicalSize(97, 2000, 2000) == (910, 566));
    }),
    ("Window minimum clamps each dimension to the work area", () =>
    {
        Check(WindowSizing.MinimumPhysicalSize(120, 1000, 600) == (1000, 600));
        Check(WindowSizing.MinimumPhysicalSize(120, 1000, 1000) == (1000, 700));
        Check(WindowSizing.MinimumPhysicalSize(120, 2000, 600) == (1125, 600));
        Check(WindowSizing.MinimumPhysicalSize(96, 0, 0) == (0, 0));
    }),
    ("Window minimum rejects invalid DPI and work area", () =>
    {
        foreach (var (dpi, width, height) in new[] { (0u, 1000, 800), (96u, -1, 800), (96u, 1000, -1) })
        {
            try { WindowSizing.MinimumPhysicalSize(dpi, width, height); }
            catch (ArgumentOutOfRangeException) { continue; }
            throw new InvalidOperationException("Invalid window sizing input accepted");
        }
    }),
    ("GPU page preserves software adapter measurements and honest unavailable states", () =>
    {
        var missing = CounterValue.Unavailable("No counter reading");
        var adapter = new GpuAdapterRow(1, "Microsoft Basic Render Driver (software adapter)", 0, 100,
            missing, missing, [new("3D", CounterValue.Measured(23)), new("Copy", CounterValue.Measured(12))], CounterValue.Measured(23), "3D")
        { HasCounterInstances = true };
        IReadOnlyList<DiagnosticItem> Rows(params GpuAdapterRow[] adapters) => DiagnosticData.Create("GPU",
            Snapshot(0) with { GpuAdapters = adapters, TotalCpuPercent = 99, DiskReadBytesPerSecond = 1000 }, null, [], new(), _ => null);
        var measured = Rows(adapter).Single();
        Check(measured.Name.Contains("software adapter") && measured.First == "23.0%" && measured.Detail.Contains("GPU utilization: 23.0%") && measured.Detail.Contains("not a sum"));
        Check(Rows().Single().Name == "GPU utilization unavailable");
        Check(Rows(adapter with { Description = "NVIDIA RTX 4090" }).Single().Name == "NVIDIA RTX 4090");
        Check(Rows(adapter with { Description = "NVIDIA RTX 4090", Engines = [], HeadlineUtilizationPercent = missing }).Single().Name == "NVIDIA RTX 4090");
        Check(Rows(adapter with { Description = "Inventory adapter", HasCounterInstances = false, Engines = [], HeadlineUtilizationPercent = missing }).Single().Name == "Inventory adapter (adapter type unavailable)");
        Check(Rows(adapter with { Description = null }).Single().Name == "Adapter 0x1 (adapter type unavailable)");
        Check(Rows(adapter with { Description = null, HasCounterInstances = false }).Single().Name == "Adapter 0x1 (adapter type unavailable)");
        Check(Rows(adapter with { HasCounterInstances = false }).Single().Name == adapter.Description);
        Check(!measured.Name.Contains("adapter type unavailable"));
        foreach (var value in new[] { missing, CounterValue.Warmup("First sample"), CounterValue.Measured(double.NaN), CounterValue.Measured(101) })
            Check(Rows(adapter with { HeadlineUtilizationPercent = value }).Single().First == "Utilization unavailable");
        Check(Rows(adapter with { HeadlineUtilizationPercent = CounterValue.Measured(0) }).Single().First == "0.0%");
    }),
    ("Lifecycle timeline separates baseline, starts, disappearance and PID reuse", () =>
    {
        var timeline = new ProcessLifecycleTimeline();
        timeline.Observe(Snapshot(0, P(10, 100)));
        timeline.Observe(Snapshot(2, P(10, 100) with { PrivateBytes = 900 }, P(11, 300)));
        timeline.Observe(Snapshot(4, P(10, 200), P(11, 300)));
        timeline.Observe(Snapshot(4, P(12, 400)));
        var e = timeline.Events;
        Check(e.Count == 4);
        Check(e[0] is { Kind: ProcessLifecycleKind.RunningAtFirstObservation, Identity.Pid: 10, AfterGap: false });
        Check(e[1] is { Kind: ProcessLifecycleKind.Started, Identity.Pid: 11 });
        Check(e[2] is { Kind: ProcessLifecycleKind.Started, Identity.CreationTimeFileTime: 200 });
        Check(e[3] is { Kind: ProcessLifecycleKind.NoLongerObserved, Identity.CreationTimeFileTime: 100, Observations: 2, PeakPrivateBytes: 900 });
        Check(e[3].LastObserved == DateTimeOffset.UnixEpoch.AddSeconds(2) && e[3].At == DateTimeOffset.UnixEpoch.AddSeconds(4));
    }),
    ("Lifecycle timeline excludes unstable identities, marks gaps and bounds events", () =>
    {
        var timeline = new ProcessLifecycleTimeline();
        timeline.Observe(Snapshot(0, P(10, 100), P(20, 0)));
        Check(timeline.Events.Count == 1 && timeline.UnstableIdentitiesExcluded == 1);
        timeline.BreakContinuity();
        timeline.Observe(Snapshot(2, P(11, 300)));
        Check(timeline.Events.Skip(1).All(x => x.AfterGap));
        timeline.Observe(Snapshot(30, P(12, 400)));
        Check(timeline.Events.Last().AfterGap);
        for (var i = 0; i < ProcessLifecycleTimeline.MaximumEvents; i++) timeline.Observe(Snapshot(32 + i, P(1000 + i, 1000 + i)));
        Check(timeline.Events.Count == ProcessLifecycleTimeline.MaximumEvents);
    }),
    ("Lifecycle tracking prioritizes existing identities and admits replacements after exit", () =>
    {
        var timeline = new ProcessLifecycleTimeline();
        var originals = Enumerable.Range(1, ProcessLifecycleTimeline.MaximumTracked)
            .Select(pid => P(pid, 100_000 + pid)).ToArray();
        var newcomers = Enumerable.Range(1, 8).Select(i => P(10_000 + i, 200_000 + i)).ToArray();
        timeline.Observe(Snapshot(0, originals));

        var second = originals.Select(p => p.Identity.Pid == 1 ? p with { PrivateBytes = 900 } : p).ToArray();
        timeline.Observe(Snapshot(2, newcomers.Concat(second).ToArray()));
        Check(timeline.TrackingOverflow == newcomers.Length && timeline.Events.All(e => e.Kind == ProcessLifecycleKind.RunningAtFirstObservation));

        var third = originals.Select(p => p.Identity.Pid == 1 ? p with { PrivateBytes = 400 } : p).ToArray();
        timeline.Observe(Snapshot(4, newcomers.Concat(third).ToArray()));
        Check(timeline.TrackingOverflow == newcomers.Length * 2 && timeline.Events.All(e => e.Kind == ProcessLifecycleKind.RunningAtFirstObservation));

        var beforeExitOverflow = timeline.TrackingOverflow;
        var afterExit = originals.Where(p => p.Identity.Pid != 1).ToArray();
        timeline.Observe(Snapshot(6, newcomers.Concat(afterExit).ToArray()));
        var exited = timeline.Events.Single(e => e.Kind == ProcessLifecycleKind.NoLongerObserved && e.Identity.Pid == 1);
        var admitted = timeline.Events.Single(e => e.Kind == ProcessLifecycleKind.Started);
        Check(exited.Observations == 3 && exited.LastObserved == DateTimeOffset.UnixEpoch.AddSeconds(4) &&
            exited.At == DateTimeOffset.UnixEpoch.AddSeconds(6) && exited.PeakPrivateBytes == 900);
        Check(admitted.Identity.Pid == newcomers[0].Identity.Pid && timeline.TrackingOverflow == beforeExitOverflow + newcomers.Length - 1);
    }),
    ("Selected history keeps lifetimes separate and absence explicit", () =>
    {
        var history = new SelectedProcessHistory();
        var process = P(10, 100);
        history.Track(process.Identity, Snapshot(0, process));
        history.Observe(Snapshot(2, P(10, 200)));
        history.Observe(Snapshot(4, P(10, 200)));
        var old = history.Snapshots.Single();
        Check(old.Points.Count == 2 && !old.Points[1].Observed && old.Points[1].PrivateBytes is null);
        history.Track(new(10, 200), Snapshot(4, P(10, 200)));
        Check(history.Snapshots.Count == 2 && history.Snapshots[0].Identity.CreationTimeFileTime == 200);
    }),
    ("Selected history preserves nulls and marks pause and missing-sample gaps", () =>
    {
        var history = new SelectedProcessHistory();
        var process = P(10, 100) with { PrivateBytes = null, CpuPercent = null };
        history.Track(process.Identity, Snapshot(0, process));
        history.BreakContinuity();
        history.Observe(Snapshot(2, process));
        history.Observe(Snapshot(2, process));
        history.Observe(Snapshot(1, process));
        Check(history.Snapshots.Single().Points.Count == 2);
        Check(history.Snapshots.Single().Points[1] is { BreakBefore: true, PrivateBytes: null, CpuPercent: null });
        history.Observe(Snapshot(4));
        history.Observe(Snapshot(6, process));
        Check(history.Snapshots.Single().Points.Last().BreakBefore);
        history.Observe(Snapshot(30, process));
        Check(history.Snapshots.Single().Points.Last().BreakBefore);
    }),
    ("Selected history retains nullable IO rates across breaks and absence", () =>
    {
        var history = new SelectedProcessHistory();
        var process = P(10, 100) with { ReadBytesPerSecond = 123.5, WriteBytesPerSecond = 0 };
        history.Track(process.Identity, Snapshot(0, process));
        Check(history.Snapshots.Single().Points.Single() is { ReadBytesPerSecond: 123.5, WriteBytesPerSecond: 0 });
        history.Observe(Snapshot(2, process with { ReadBytesPerSecond = null, WriteBytesPerSecond = null }));
        Check(history.Snapshots.Single().Points.Last() is { Observed: true, ReadBytesPerSecond: null, WriteBytesPerSecond: null });
        history.BreakContinuity();
        history.Observe(Snapshot(4, process));
        Check(history.Snapshots.Single().Points.Last() is { BreakBefore: true, ReadBytesPerSecond: 123.5, WriteBytesPerSecond: 0 });
        history.Observe(Snapshot(6));
        Check(history.Snapshots.Single().Points.Last() is { Observed: false, ReadBytesPerSecond: null, WriteBytesPerSecond: null });
        history.Observe(Snapshot(30, process));
        Check(history.Snapshots.Single().Points.Last() is { BreakBefore: true, ReadBytesPerSecond: 123.5, WriteBytesPerSecond: 0 });
    }),
    ("Selected history bounds points and least-recently-selected lifetimes", () =>
    {
        var history = new SelectedProcessHistory();
        var process = P(10, 100);
        history.Track(process.Identity, Snapshot(0, process));
        for (int i = 1; i < 200; i++) history.Observe(Snapshot(i * 2, process));
        Check(history.Snapshots.Single().Points.Count == SelectedProcessHistory.MaximumPoints);
        for (int i = 1; i <= 9; i++) history.Track(new(i, i), Snapshot(400, P(i, i)));
        Check(history.Snapshots.Count == SelectedProcessHistory.MaximumLifetimes && history.Snapshots.All(s => s.Identity.Pid != 10));
        history.Track(new(99, null), Snapshot(400, P(99, 0) with { Identity = new(99, null) }));
        Check(history.Snapshots.Count == SelectedProcessHistory.MaximumLifetimes);
    }),
    ("Diagnostic byte units retain small region spans", () =>
    {
        Check(DiagnosticData.Bytes(null) == "—" && DiagnosticData.Bytes(0) == "0 B");
        Check(DiagnosticData.Bytes(512) == "512 B" && DiagnosticData.Bytes(4096) == "4.0 KiB");
        Check(DiagnosticData.Bytes(64 * 1024) == "64.0 KiB" && DiagnosticData.Bytes(1024 * 1024) == "1.0 MiB");
        Check(DiagnosticData.Bytes(1024UL * 1024 * 1024 * 1024) == "1.00 TiB");
    }),
    ("Diagnostic live rows retain objects and order without reset", () =>
    {
        var rows = new DiagnosticRows();
        var first = new DiagnosticItem("a", new(1, 100), "A", "", 1, "1", "", "", 1);
        var second = first with { Key = "b", Identity = new(2, 200), Pid = 2, Name = "B", Number1 = 2 };
        rows.Apply([first, second], true);
        var anchor = rows.Rows[1];
        var reset = false;
        rows.Rows.CollectionChanged += (_, e) => reset |= e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Reset;
        rows.Apply([second with { First = "20", Number1 = 20 }, first], false);
        Check(ReferenceEquals(rows.Rows[1], anchor) && anchor.First == "20" && !reset);
        rows.Apply([second, first], true);
        Check(ReferenceEquals(rows.Rows[0], anchor));
        rows.Apply([first with { Key = "new lifetime", Identity = new(1, 300) }], false);
        Check(rows.Rows.Count == 1 && rows.Rows[0].Key == "new lifetime" && !reset);
    }),
    ("Diagnostic search includes all rows and sorts unavailable values last", () =>
    {
        var processes = Enumerable.Range(1, 75).Select(i => P(i, i) with { Name = $"app{i}", PrivateBytes = (ulong)(1000 - i) }).ToArray();
        var items = DiagnosticData.Create("Memory", Snapshot(0, processes), null, [], new(), _ => null);
        Check(DiagnosticData.FilterAndSort(items, "app75", 2, true, true).Single().Pid == 75);
        var unknown = items[0] with { Key = "unknown", First = "—", Number1 = null };
        foreach (var descending in new[] { false, true })
            Check(DiagnosticData.FilterAndSort([unknown, items[1]], "", 2, descending, true)[^1].Key == "unknown");
    }),
    ("Diagnostic ownership rejects stale and reused lifetimes", () =>
    {
        var alpha = P(42, 100) with { Name = "alpha-owner.exe", ExecutablePath = @"C:\Apps\alpha-owner.exe", UserSid = "S-1-5-21-test", SessionId = 1 };
        var beta = P(43, 200) with { Name = "beta-owner.exe", ExecutablePath = @"C:\Apps\beta-owner.exe", UserSid = "S-1-5-21-test", SessionId = 1 };
        var relationships = new RelationshipSnapshot
        {
            CapturedAt = DateTimeOffset.UnixEpoch,
            Connections =
            [
                new() { Protocol = "TCP", LocalAddress = "::1", LocalPort = 80, RemoteAddress = "::2", RemotePort = 443, OwningPid = 42, OwnerIdentity = alpha.Identity },
                new() { Protocol = "TCP", LocalAddress = "::1", LocalPort = 81, RemoteAddress = "::3", RemotePort = 443, OwningPid = 43, OwnerIdentity = beta.Identity }
            ],
            Services =
            [
                new() { Name = "alpha-service", DisplayName = "Alpha service", State = "Running", ProcessId = 42, OwnerIdentity = alpha.Identity, StartType = "Auto" },
                new() { Name = "beta-service", DisplayName = "Beta service", State = "Running", ProcessId = 43, OwnerIdentity = beta.Identity, StartType = "Manual" }
            ]
        };
        var snapshot = Snapshot(2, alpha, beta);
        var network = DiagnosticData.Create("Network", snapshot, relationships, [], new(), _ => null);
        var alphaConnection = network.Single(row => row.Pid == 42);
        Check(alphaConnection.Identity == alpha.Identity && alphaConnection.Name == "[::1]:80");
        Check(alphaConnection.Third == alpha.Name && alphaConnection.Detail.Contains("Owner verified", StringComparison.Ordinal));
        Check(DiagnosticData.FilterAndSort(network, "", 4, false, false).Select(row => row.Third).SequenceEqual([alpha.Name, beta.Name]));
        var services = DiagnosticData.Create("Services", snapshot, relationships, [], new(), _ => null);
        Check(services.Single(row => row.Pid == 42).Third == alpha.Name);
        Check(services.All(row => row.Detail.Contains("Owner verified", StringComparison.Ordinal)));
        Check(DiagnosticData.FilterAndSort(services, "", 4, false, false).Select(row => row.Third).SequenceEqual([alpha.Name, beta.Name]));

        var stale = DiagnosticData.Create("Network", Snapshot(12, alpha, beta), relationships, [], new(), _ => null).Single(row => row.Pid == 42);
        Check(stale.Identity is null && stale.Pid == 42 && stale.Third == "Owner unavailable" &&
            stale.Detail.Contains("unverified", StringComparison.OrdinalIgnoreCase) && !new DiagnosticRow(stale).CanInspect);
        var reusedAlpha = alpha with { Identity = new(42, 300) };
        var reused = DiagnosticData.Create("Services", Snapshot(2, reusedAlpha, beta), relationships, [], new(), _ => null).Single(row => row.Pid == 42);
        Check(reused.Identity is null && reused.Pid == 42 && reused.Third == "Owner unavailable" &&
            reused.Detail.Contains("unverified", StringComparison.OrdinalIgnoreCase) && !new DiagnosticRow(reused).CanInspect);
    }),
    ("GPU diagnostics show only valid provider readings", () =>
    {
        DiagnosticItem Gpu(double? value) => DiagnosticData.Create("GPU",
            Snapshot(0) with { GpuPercent = value }, null, [], new(), _ => null).Single();
        var unavailable = Gpu(null);
        var zero = Gpu(0);
        var measured = Gpu(42.5);
        Check(unavailable.Name.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
        Check(zero.Name.Contains("0.0%", StringComparison.Ordinal) && zero.Detail.Contains("provider", StringComparison.OrdinalIgnoreCase));
        Check(measured.Name.Contains("42.5%", StringComparison.Ordinal));
        Check(Gpu(double.NaN).Name.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
        Check(Gpu(100.1).Name.Contains("unavailable", StringComparison.OrdinalIgnoreCase));
    }),
    ("Distinct replacement insights retain stable row identities", () =>
    {
        var history = new DiagnosticsHistory();
        var alpha = P(10, 100) with { Name = "alpha.exe", ExecutablePath = @"C:\Alpha\alpha.exe", UserSid = "S-1-5-21-test", SessionId = 1 };
        var beta = P(20, 200) with { Name = "beta.exe", ExecutablePath = @"C:\Beta\beta.exe", UserSid = "S-1-5-21-test", SessionId = 1 };
        var sample = Snapshot(0, alpha, beta);
        history.Add(sample);
        for (var i = 1; i <= 3; i++)
        {
            alpha = alpha with { Identity = new(10, 100 + i) };
            beta = beta with { Identity = new(20, 200 + i) };
            sample = Snapshot(i * 2, alpha, beta);
            history.Add(sample);
        }
        var items = DiagnosticData.Create("Insights", sample, null, [], history, _ => null)
            .Where(item => item.Name == "Repeated process-lifetime replacements").ToArray();
        Check(items.Length == 2 && items.Select(item => item.Key).Distinct().Count() == 2);
        var rows = new DiagnosticRows();
        rows.Apply(items, true);
        var original = rows.Rows.ToDictionary(row => row.Key, StringComparer.Ordinal);
        alpha = alpha with { Identity = new(10, 104) };
        beta = beta with { Identity = new(20, 204) };
        sample = Snapshot(8, alpha, beta);
        history.Add(sample);
        rows.Apply(DiagnosticData.Create("Insights", sample, null, [], history, _ => null)
            .Where(item => item.Name == "Repeated process-lifetime replacements"), false);
        Check(rows.Rows.Count == 2 && rows.Rows.All(row => ReferenceEquals(original[row.Key], row)));
        Check(rows.Rows.All(row => row.Detail.Contains("4 single-instance", StringComparison.Ordinal)));
    }),
    ("Commit chart uses elapsed time and breaks continuity", () =>
    {
        var history = new DiagnosticsHistory();
        history.Add(Snapshot(0) with { Memory = new() { CommitTotalBytes = 10 } });
        history.Add(Snapshot(2) with { Memory = new() { CommitTotalBytes = 20 } });
        history.BreakContinuity();
        history.Add(Snapshot(6) with { Memory = new() { CommitTotalBytes = 30 } });
        var trend = DiagnosticData.BuildTrend(history.System, p => p.CommitBytes, 100);
        Check(trend.Segments.Count == 2 && trend.SampleCount == 3);
        Equal(1d / 3, trend.Segments[0][1].X);
        Equal(.3, trend.Segments[1][0].Y);
        Check(history.System[^1].BreakBefore);
    }),
    ("Application description cache honors expiry", () =>
    {
        var descriptions = new ApplicationDescriptions();
        var cache = (Dictionary<string, (string? Text, DateTimeOffset Expires)>)typeof(ApplicationDescriptions)
            .GetField("cache", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(descriptions)!;
        var process = P(10, 100) with { Name = "example.exe" };
        var path = process.ExecutablePath!;
        cache[path] = ("Fresh description", DateTimeOffset.MaxValue);
        Check(descriptions.GetCached(process) == "Fresh description");
        cache[path] = ("Expired description", DateTimeOffset.MinValue);
        Check(descriptions.GetCached(process) is null);
    }),
    ("Process descriptions resolve without rows and support Overview search", () =>
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var process = P(10, 100) with { Name = "example.exe" };
        var reads = 0;
        var index = new ProcessDescriptionIndex((_, _) =>
        {
            reads++;
            return Task.FromResult<string?>("Friendly application");
        }, clock);
        index.UpdateProcesses([process]);
        Check(index.GetCached(process) is null);
        Check(index.RefreshAsync(default).GetAwaiter().GetResult());
        Check(reads == 1 && index.GetCached(process) == "Friendly application");
        var data = new OverviewData();
        data.Observe(Snapshot(0, process));
        Check(data.Create(null, new(), search: "Friendly application", descriptionLookup: index.GetCached)!
            .Consumers.Single().Members.Single().Identity == process.Identity);
    }),
    ("Process descriptions prune retired keys and reject delayed identity/path results", () =>
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var staleLifetime = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stalePath = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var index = new ProcessDescriptionIndex((_, _) =>
        {
            reads++;
            return reads switch
            {
                1 => Task.FromResult<string?>("Old lifetime"),
                2 => staleLifetime.Task,
                3 => Task.FromResult<string?>("Reused lifetime"),
                4 => stalePath.Task,
                _ => Task.FromResult<string?>("Current path")
            };
        }, clock);
        var original = P(10, 100) with { Name = "old.exe", ExecutablePath = @"C:\Apps\old.exe" };
        var reused = P(10, 200) with { Name = "reused.exe", ExecutablePath = @"C:\Apps\reused.exe" };
        var renamed = reused with { Name = "renamed.exe", ExecutablePath = @"C:\Apps\renamed.exe" };
        var changedPath = renamed with { Name = "current.exe", ExecutablePath = @"C:\Apps\current.exe" };
        index.UpdateProcesses([original]);
        Check(index.RefreshAsync(default).GetAwaiter().GetResult());
        Check(index.GetCached(original) == "Old lifetime");
        clock.Advance(TimeSpan.FromMinutes(5));
        var staleLifetimePass = index.RefreshAsync(default);
        Check(ReferenceEquals(staleLifetimePass, index.RefreshAsync(default)));
        index.UpdateProcesses([reused]);
        Check(index.GetCached(original) is null);
        staleLifetime.SetResult("Late old lifetime");
        Check(!staleLifetimePass.GetAwaiter().GetResult());
        Check(index.GetCached(reused) is null);
        Check(index.RefreshAsync(default).GetAwaiter().GetResult());
        Check(index.GetCached(reused) == "Reused lifetime");
        index.UpdateProcesses([renamed]);
        Check(index.GetCached(reused) is null && index.GetCached(renamed) is null);
        var stalePathPass = index.RefreshAsync(default);
        Check(ReferenceEquals(stalePathPass, index.RefreshAsync(default)));
        index.UpdateProcesses([changedPath]);
        stalePath.SetResult("Late old path");
        Check(!stalePathPass.GetAwaiter().GetResult());
        Check(index.GetCached(changedPath) is null);
        Check(index.RefreshAsync(default).GetAwaiter().GetResult());
        Check(index.GetCached(changedPath) == "Current path" && reads == 5);
    }),
    ("Process description positive and negative cache entries expire", () =>
    {
        var clock = new ManualTimeProvider(DateTimeOffset.UnixEpoch);
        var process = P(10, 100) with { Name = "example.exe" };
        var positiveReads = 0;
        var positive = new ProcessDescriptionIndex((_, _) =>
        {
            positiveReads++;
            return Task.FromResult<string?>(positiveReads == 1 ? "First" : "Second");
        }, clock);
        positive.UpdateProcesses([process]);
        Check(positive.RefreshAsync(default).GetAwaiter().GetResult());
        Check(positive.GetCached(process) == "First");
        Check(!positive.RefreshAsync(default).GetAwaiter().GetResult() && positiveReads == 1);
        clock.Advance(TimeSpan.FromMinutes(5));
        Check(positive.GetCached(process) is null);
        Check(positive.RefreshAsync(default).GetAwaiter().GetResult());
        Check(positive.GetCached(process) == "Second" && positiveReads == 2);

        var negativeReads = 0;
        var negative = new ProcessDescriptionIndex((_, _) =>
        {
            negativeReads++;
            return Task.FromResult<string?>(null);
        }, clock);
        var other = P(11, 200) with { Name = "other.exe", ExecutablePath = @"C:\Apps\other.exe" };
        negative.UpdateProcesses([other]);
        Check(!negative.RefreshAsync(default).GetAwaiter().GetResult() && negativeReads == 1);
        Check(!negative.RefreshAsync(default).GetAwaiter().GetResult() && negativeReads == 1);
        clock.Advance(TimeSpan.FromMinutes(1));
        Check(negative.GetCached(other) is null);
        Check(!negative.RefreshAsync(default).GetAwaiter().GetResult() && negativeReads == 2);
    }),
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
    ("Prompt input requires non-blank text and trims it", () =>
    {
        Check(!PromptInput.CanSubmit(null) && !PromptInput.CanSubmit("") && !PromptInput.CanSubmit(" \t\r\n "));
        Check(PromptInput.Normalize("") is null && PromptInput.Normalize("   ") is null);
        Check(PromptInput.CanSubmit("user32.dll") && PromptInput.Normalize("user32.dll") == "user32.dll");
        Check(PromptInput.CanSubmit("  user32.dll\t") && PromptInput.Normalize("  user32.dll\t") == "user32.dll");
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
        var insight = history.Explain(sample).Single(i => i.Title.StartsWith("Sustained"));
        Check(insight.Key == $"growth:{sample.Processes.Single().Identity}");
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
    }),
    ("Module mappings collapse only exact base, size and case-insensitive path duplicates", () =>
    {
        var seen = new HashSet<ModuleObservation>(ModuleMappingComparer.Instance);
        Check(seen.Count == 0);
        var module = new ModuleObservation("notepad.exe", @"C:\Windows\SysWOW64\notepad.exe", 0x8B0000, 192512);
        Check(seen.Add(module));
        Check(!seen.Add(module with { }));
        Check(!seen.Add(module with { Name = "other name", Path = module.Path.ToUpperInvariant() }));
        Check(seen.Add(module with { BaseAddress = 0x9B0000 }));
        Check(seen.Add(module with { Path = @"C:\Windows\System32\notepad.exe" }));
        Check(seen.Add(module with { SizeBytes = 233472 }));
        Check(seen.Count == 4);
        Check(!ModuleMappingComparer.Instance.Equals(module, null));
        Check(ModuleMappingComparer.Instance.Equals(null, null));
    }),
    // Area D: on-demand inventories and inspection dialogs.
    ("Inventory metric columns adapt to width and always leave the name column readable", () =>
    {
        Check(InventoryLayout.MetricWidth(0, 3) == 0 && InventoryLayout.MetricWidth(500, 0) == 0 && InventoryLayout.MetricWidth(double.NaN, 3) == 0);
        Check(InventoryLayout.MetricWidth(4000, 3) == InventoryLayout.MaximumMetricWidth);
        Check(InventoryLayout.MetricWidth(700, 3) == 119);
        foreach (var available in new[] { 120d, 280, 304, 420, 640, 780, 1200 })
            for (var visible = 1; visible <= 3; visible++)
            {
                var width = InventoryLayout.MetricWidth(available, visible);
                Check(width > 0 && width <= InventoryLayout.MaximumMetricWidth && available - width * visible >= available * 0.4 - 0.001);
            }
        Check(InventoryLayout.MetricWidth(300, 3) < InventoryLayout.MetricWidth(800, 3));
    }),
    ("Inventory rows reconcile only with complete unique keys, such as adapter plus PID", () =>
    {
        Check(KeyedRows.HasUniqueKeys(new[] { "process:1A:42", "process:2B:42", "adapter:1A" }, key => key));
        Check(!KeyedRows.HasUniqueKeys(new[] { "process:1A:42", "process:1A:42" }, key => key));
        Check(!KeyedRows.HasUniqueKeys(new[] { "a", null }, key => key));
        Check(KeyedRows.HasUniqueKeys(Array.Empty<string>(), key => key));
    }),
    ("On-demand counter inventories resample warmup or stale long-average intervals", () =>
    {
        Check(CounterSampling.NeedsFreshInterval(true, TimeSpan.FromSeconds(1)));
        Check(CounterSampling.NeedsFreshInterval(false, null) && CounterSampling.NeedsFreshInterval(false, TimeSpan.Zero));
        Check(CounterSampling.NeedsFreshInterval(false, TimeSpan.FromSeconds(75.1)));
        Check(!CounterSampling.NeedsFreshInterval(false, TimeSpan.FromSeconds(1)) && !CounterSampling.NeedsFreshInterval(false, CounterSampling.MaximumRateInterval));
    }),
    // Overview hardening (Area B)
    ("Stable ranking obeys greedy bounds and plain sort without history", () =>
    {
        var random = new Random(90421);
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var items = Enumerable.Range(0, random.Next(1, 90))
                .Select(id => (Id: id, Value: random.Next(6) == 0 ? (double?)null : random.NextDouble() * 20)).ToArray();
            var previous = items.OrderBy(_ => random.Next()).Take(random.Next(items.Length + 1))
                .Select((item, rank) => (item.Id, Rank: rank)).ToDictionary(item => item.Id, item => item.Rank);
            var tie = Comparer<(int Id, double? Value)>.Create((a, b) => a.Id.CompareTo(b.Id));
            double Tolerance(double value) => Math.Max(0.5, value * 0.1);
            var ordered = StableRanking.Take(items, item => item.Id, item => item.Value, previous, Tolerance, tie, items.Length);
            Check(ordered.Select(item => item.Id).Distinct().Count() == items.Length);
            for (var p = 0; p < ordered.Count; p++)
            {
                var maximum = ordered.Skip(p).Max(item => item.Value);
                Check(maximum is null || ordered[p].Value >= maximum - Tolerance(maximum.Value));
            }
            var prefix = StableRanking.Take(items, item => item.Id, item => item.Value, previous, Tolerance, tie, 12);
            Check(prefix.SequenceEqual(ordered.Take(12)));
            var plain = StableRanking.Take(items, item => item.Id, item => item.Value, new Dictionary<int, int>(), Tolerance, tie, items.Length);
            Check(plain.SequenceEqual(items.OrderByDescending(item => item.Value).ThenBy(item => item.Id)));
        }
    }),
    ("Stable ranking holds near equals, moves large changes, and places newcomers and nulls", () =>
    {
        var previous = new Dictionary<int, int> { [1] = 0, [2] = 1, [3] = 2 };
        var tie = Comparer<(int Id, double? Value)>.Create((a, b) => a.Id.CompareTo(b.Id));
        int[] Order(params (int Id, double? Value)[] items) => StableRanking.Take(items, item => item.Id,
            item => item.Value, previous, _ => 0.5, tie, 50).Select(item => item.Id).ToArray();
        Check(Order((1, 10), (2, 10.1)).SequenceEqual(new[] { 1, 2 }));
        Check(Order((1, 10), (2, 15)).SequenceEqual(new[] { 2, 1 }));
        Check(Order((1, 10), (2, null), (3, null), (4, 20), (5, 15), (6, null))
            .SequenceEqual(new[] { 4, 5, 1, 2, 3, 6 }));
        Check(Order((1, 10), (4, 10.2)).SequenceEqual(new[] { 1, 4 }));
        Check(Order().Length == 0);
        Check(StableRanking.Take(new[] { (Id: 1, Value: (double?)1) }, item => item.Id, item => item.Value,
            previous, _ => 0.5, tie, 0).Count == 0);
        var rejected = false;
        try { Order((1, double.NaN)); } catch (ArgumentException) { rejected = true; }
        Check(rejected);
        rejected = false;
        try { StableRanking.Take(new[] { (Id: 1, Value: (double?)1) }, item => item.Id, item => item.Value,
            previous, _ => -1, tie, 1); } catch (ArgumentException) { rejected = true; }
        Check(rejected);
    }),
    ("Overview CPU hysteresis holds small swaps, allows large swaps, and resets on mode change", () =>
    {
        var data = new OverviewData();
        var a = P(10, 100) with { CpuPercent = 10.1, PrivateBytes = 200UL * 1024 * 1024 };
        var b = P(20, 200) with { CpuPercent = 10, PrivateBytes = 201UL * 1024 * 1024 };
        int[] Order(ConsumerRanking ranking) => data.Create(null, new(), ranking)!.Consumers.Select(group => group.Key.Pid).ToArray();
        data.Observe(Snapshot(0, a, b));
        Check(Order(ConsumerRanking.Cpu).SequenceEqual(new[] { 10, 20 }));
        data.Observe(Snapshot(1, a with { CpuPercent = 10 }, b with { CpuPercent = 10.1 }));
        Check(Order(ConsumerRanking.Cpu).SequenceEqual(new[] { 10, 20 }));
        Check(Order(ConsumerRanking.PrivateCommit).SequenceEqual(new[] { 20, 10 }));
        Check(Order(ConsumerRanking.Cpu).SequenceEqual(new[] { 20, 10 }));
        data.Observe(Snapshot(2, a with { CpuPercent = 15.1 }, b));
        Check(Order(ConsumerRanking.Cpu).SequenceEqual(new[] { 10, 20 }));
    }),
    ("Overview private commit holds near equals and reorders beyond tolerance", () =>
    {
        var data = new OverviewData();
        var a = P(10, 100) with { PrivateBytes = 212UL * 1024 * 1024 };
        var b = P(20, 200) with { PrivateBytes = 211UL * 1024 * 1024 };
        data.Observe(Snapshot(0, a, b));
        Check(data.Create(null, new())!.Consumers[0].Key.Pid == 10);
        data.Observe(Snapshot(1, a with { PrivateBytes = b.PrivateBytes }, b with { PrivateBytes = a.PrivateBytes }));
        Check(data.Create(null, new())!.Consumers[0].Key.Pid == 10);
        data.Observe(Snapshot(2, a, b with { PrivateBytes = 230UL * 1024 * 1024 }));
        Check(data.Create(null, new())!.Consumers[0].Key.Pid == 20);
    }),
    ("Live history follows only the newest time edge and anchors other views", () =>
    {
        Check(InventoryViewport.FollowLatest(true, true, true, 0, 500) == InventoryEdge.Top);
        Check(InventoryViewport.FollowLatest(true, true, true, 0.5, 500) == InventoryEdge.Top);
        Check(InventoryViewport.FollowLatest(true, true, false, 500, 500) == InventoryEdge.Bottom);
        Check(InventoryViewport.FollowLatest(true, true, false, 499.5, 500) == InventoryEdge.Bottom);
        foreach (var descending in new[] { false, true })
        {
            Check(InventoryViewport.FollowLatest(true, true, descending, 250, 500) == InventoryEdge.Anchor);
            Check(InventoryViewport.FollowLatest(false, true, descending, 0, 0) == InventoryEdge.Anchor);
            Check(InventoryViewport.FollowLatest(true, false, descending, 0, 0) == InventoryEdge.Anchor);
            Check(InventoryViewport.FollowLatest(true, true, descending, 0, 0) == (descending ? InventoryEdge.Top : InventoryEdge.Bottom));
            Check(InventoryViewport.FollowLatest(true, true, descending, double.NaN, 500) == InventoryEdge.Anchor);
            Check(InventoryViewport.FollowLatest(true, true, descending, 0, double.PositiveInfinity) == InventoryEdge.Anchor);
            Check(InventoryViewport.FollowLatest(true, true, descending, -1, 500) == InventoryEdge.Anchor);
        }
        Check(InventoryViewport.FollowLatest(true, true, false, 0, 500) == InventoryEdge.Anchor);
        Check(InventoryViewport.FollowLatest(true, true, true, 500, 500) == InventoryEdge.Anchor);
    }),
    ("Overview system cache keeps provider bytes and identifies its distinct definition", () =>
    {
        var data = new OverviewData();
        Check(data.Create(null, new()) is null);
        data.Observe(Snapshot(0) with { Memory = new() { SystemCacheBytes = 5UL * 1024 * 1024 * 1024 } });
        Check(data.Create(null, new())!.SystemCacheBytes == 5UL * 1024 * 1024 * 1024);
        data.Observe(Snapshot(1));
        Check(data.Create(null, new())!.SystemCacheBytes is null);
        Check(OverviewData.SystemCacheDefinition.Contains("standby + system working set") && OverviewData.SystemCacheDefinition.Contains("GetPerformanceInfo"));
    }),
    ("Insights use shared byte units for large, small, zero and unavailable readings", () =>
    {
        var history = new DiagnosticsHistory();
        var sample = Snapshot(0, P(1, 1) with { PrivateBytes = 5UL * 1024 * 1024 * 1024 }) with
        {
            Memory = new() { PhysicalAvailableBytes = 191, PhysicalTotalBytes = 1024, CommitTotalBytes = 0, CommitLimitBytes = 32UL * 1024 * 1024 * 1024 }
        };
        var insights = history.Explain(sample);
        Check(insights.Single(i => i.Title.StartsWith("Largest measured")).Evidence.Contains(MetricFormatting.Bytes(sample.Processes[0].PrivateBytes)));
        Check(insights.Single(i => i.Title == "Physical memory availability").Evidence.StartsWith("191 B"));
        Check(insights.Single(i => i.Title == "Commit headroom").Evidence.StartsWith($"0 B / {MetricFormatting.Bytes(sample.Memory.CommitLimitBytes)}"));
        Check(history.Explain(Snapshot(1)).Count == 0);
    }),
    ("Diagnostics budget reserves expanded plot and legend outside summary scrolling", () =>
    {
        foreach (var height in new[] { 280d, 360, 520, 900 })
        {
            foreach (var expanded in new[] { false, true })
            {
                var chart = expanded ? DiagnosticsLayout.ExpandedChartHeight : DiagnosticsLayout.CollapsedChartHeight;
                var summary = DiagnosticsLayout.MaximumSummaryHeight(height, 48, 6, true, expanded);
                Check(summary >= 0);
                if (height >= 48 + 18 + chart + DiagnosticsLayout.MinimumTableHeight)
                    Check(height - 48 - 18 - summary >= chart + DiagnosticsLayout.MinimumTableHeight);
            }
        }
        var compactSummary = DiagnosticsLayout.MaximumSummaryHeight(390, 48, 6, true, true);
        Check(compactSummary == 24);
        Check(390 - 48 - 18 - compactSummary - DiagnosticsLayout.MinimumTableHeight == DiagnosticsLayout.ExpandedChartHeight);
        Check(DiagnosticsLayout.ExpandedChartHeight >= DiagnosticsLayout.PlotHeight + 32 + 32 + 16 + 8);
        Check(DiagnosticsLayout.MaximumSummaryHeight(390, 48, 6, true, true, 180) == 8);
        Check(DiagnosticsLayout.MaximumSummaryHeight(390, 48, 6, false, true) == 194);
        Check(DiagnosticsLayout.MaximumSummaryHeight(0, 60, 8, true, true) == 0);
        Check(DiagnosticsLayout.MaximumSummaryHeight(double.NaN, 60, 8, true, true) == 0);
        Check(DiagnosticsLayout.MaximumSummaryHeight(500, double.PositiveInfinity, 8, true, true) == 0);
        Check(DiagnosticsLayout.MaximumSummaryHeight(500, 60, -1, true, true) == 0);
        Check(DiagnosticsLayout.MaximumSummaryHeight(500, 60, 8, true, true, double.NaN) == 0);
    }),
    // Area A: shell and process table
    ("Process viewport anchor compensates insertions, removals and distant sort moves without containers", () =>
    {
        var rows = new System.Collections.ObjectModel.ObservableCollection<int>(Enumerable.Range(0, 100));
        var anchor = RowViewportAnchoring.Capture(rows, id => id.ToString(), _ => 44, "20", 890, -10)!.Value;
        var desired = Enumerable.Range(0, 100).Where(id => id != 2).Prepend(101).Prepend(100).ToArray();
        MinimalKeyedRows.Reconcile(rows, desired, id => id, id => id, id => id, (_, _) => { });
        var offset = RowViewportAnchoring.RestoreOffset(anchor, rows, id => id.ToString(), _ => 44, 5000);
        Check(rows.SequenceEqual(desired) && offset == 934);
        Check(rows.IndexOf(20) * 44 - offset == -10);
        desired = rows.Where(id => id != 20).Append(20).ToArray();
        MinimalKeyedRows.Reconcile(rows, desired, id => id, id => id, id => id, (_, _) => { });
        offset = RowViewportAnchoring.RestoreOffset(anchor, rows, id => id.ToString(), _ => 44, 5000);
        Check(rows.SequenceEqual(desired) && offset == rows.IndexOf(20) * 44 + 10);
        Check(rows.IndexOf(20) * 44 - offset == anchor.Y);
        var selected = RowViewportAnchoring.Capture(rows, id => id.ToString(), _ => 44, "30", 800, 44)!.Value;
        var selectedIndex = rows.IndexOf(30);
        rows.Insert(0, 200);
        Check(RowViewportAnchoring.RestoreOffset(selected, rows, id => id.ToString(), _ => 44, 5000) == 844);
        Check(rows.IndexOf(30) == selectedIndex + 1);
    }),
    ("Process viewport anchors handle mixed row heights, bounds, expired keys and invalid geometry", () =>
    {
        var rows = new[] { (Key: "group", Height: 44d), (Key: "child", Height: 32d), (Key: "process", Height: 44d) };
        var anchor = RowViewportAnchoring.Capture(rows, r => r.Key, r => r.Height, "process", 80, -4)!.Value;
        Check(anchor.ContentTop == 76);
        Check(RowViewportAnchoring.RestoreOffset(anchor, rows.Skip(1), r => r.Key, r => r.Height, 100) == 36);
        Check(RowViewportAnchoring.RestoreOffset(anchor, rows, r => r.Key, r => r.Height, 50) == 50);
        Check(RowViewportAnchoring.RestoreOffset(anchor, rows.Reverse(), r => r.Key, r => r.Height, 100) == 4);
        Check(RowViewportAnchoring.RestoreOffset(anchor, rows.Take(2), r => r.Key, r => r.Height, 100) is null);
        Check(RowViewportAnchoring.Capture(Array.Empty<int>(), i => i.ToString(), _ => 44, "1", 0, 0) is null);
        Check(RowViewportAnchoring.Capture(rows, r => r.Key, r => r.Height, "process", double.NaN, 0) is null);
        Check(RowViewportAnchoring.Capture(rows, r => r.Key, r => double.NaN, "process", 0, 0) is null);
        Check(RowViewportAnchoring.RestoreOffset(anchor, rows, r => r.Key, r => r.Height, -1) is null);
    }),
    ("Minimal reconciliation preserves unchanged rows without notifications", () =>
    {
        var rows = new System.Collections.ObjectModel.ObservableCollection<int>(Enumerable.Range(0, 20));
        var notifications = 0;
        var updates = new List<int>();
        rows.CollectionChanged += (_, _) => notifications++;
        var result = MinimalKeyedRows.Reconcile(rows, rows.ToArray(), x => x, x => x, x => x, (row, _) => updates.Add(row));
        Check(result == new ReconciliationCounts(0, 0, 0) && notifications == 0);
        Check(updates.Order().SequenceEqual(rows));
    }),
    ("Minimal reconciliation moves top to bottom exactly once", () =>
    {
        var rows = new System.Collections.ObjectModel.ObservableCollection<int>(Enumerable.Range(0, 20));
        var desired = Enumerable.Range(1, 19).Append(0).ToArray();
        var events = new List<System.Collections.Specialized.NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => events.Add(e.Action);
        var result = MinimalKeyedRows.Reconcile(rows, desired, x => x, x => x, x => x, (_, _) => { });
        Check(rows.SequenceEqual(desired) && result == new ReconciliationCounts(0, 1, 0));
        Check(events.SequenceEqual([System.Collections.Specialized.NotifyCollectionChangedAction.Move]));
    }),
    ("Minimal reconciliation randomized orders use retained count minus LIS moves", () =>
    {
        var random = new Random(1729);
        var rows = new System.Collections.ObjectModel.ObservableCollection<int>();
        for (var iteration = 0; iteration < 1000; iteration++)
        {
            var before = rows.ToArray();
            var desired = Enumerable.Range(0, 50).Where(_ => random.Next(3) != 0).OrderBy(_ => random.Next()).ToArray();
            if (iteration % 20 == 0) desired = [];
            var retained = desired.Where(before.Contains).ToArray();
            var lengths = new int[retained.Length];
            for (var i = 0; i < retained.Length; i++)
            {
                lengths[i] = 1;
                for (var j = 0; j < i; j++)
                    if (Array.IndexOf(before, retained[j]) < Array.IndexOf(before, retained[i]))
                        lengths[i] = Math.Max(lengths[i], lengths[j] + 1);
            }
            var updates = new List<int>();
            var moves = 0;
            System.Collections.Specialized.NotifyCollectionChangedEventHandler handler = (_, e) =>
            {
                if (e.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Move) moves++;
            };
            rows.CollectionChanged += handler;
            var result = MinimalKeyedRows.Reconcile(rows, desired, x => x, x => x, x => x, (row, item) =>
            {
                Check(row == item);
                updates.Add(row);
            });
            rows.CollectionChanged -= handler;
            Check(rows.SequenceEqual(desired));
            Check(result.Moved == retained.Length - lengths.DefaultIfEmpty(0).Max() && moves == result.Moved);
            Check(result.Removed == before.Length - retained.Length && result.Inserted == desired.Length - retained.Length);
            Check(updates.Order().SequenceEqual(retained.Order()));
        }
    }),
    ("Minimal reconciliation rejects duplicate desired keys before mutation", () =>
    {
        var rows = new System.Collections.ObjectModel.ObservableCollection<int>([1, 2]);
        var threw = false;
        try { MinimalKeyedRows.Reconcile(rows, new[] { 1, 1 }, x => x, x => x, x => x, (_, _) => { }); }
        catch (ArgumentException) { threw = true; }
        Check(threw && rows.SequenceEqual([1, 2]));
    }),
    ("Process columns match legacy viewport sweep and fit when minimums permit", () =>
    {
        var actual = new ProcessColumnWidths();
        var oracle = new LegacyProcessColumns();
        for (var width = 100; width <= 2000; width += 7)
        {
            actual.FitViewport(width);
            oracle.FitViewport(width);
            Check(Enumerable.Range(0, 8).All(i => actual.GetWidth(i) == oracle.Widths[i]));
            var minimum = 180 + 126 + 68 + 64 + (width >= 740 ? 88 : 0) + (width >= 840 ? 72 : 0);
            Check(width - 32 < minimum || Math.Abs(actual.ContentWidth - (width - 32)) < 0.0001);
            Equal(actual.ContentWidth + 32, actual.TableWidth);
        }
    }),
    ("Process columns match legacy resize sequences including name redistribution", () =>
    {
        var actual = new ProcessColumnWidths();
        var oracle = new LegacyProcessColumns();
        foreach (var viewport in new[] { 572, 900, 1200, 700, 839, 840, 2000, 400 })
        {
            actual.FitViewport(viewport);
            oracle.FitViewport(viewport);
            foreach (var (index, width) in new (int, double)[]
                { (0, 350), (1, 200), (0, 180), (3, 150), (4, 90), (2, 130), (7, 140),
                  (0, 600), (0, 190), (5, 150), (6, 100), (1, -20), (2, 1000), (-1, 100), (8, 100), (0, double.NaN) })
            {
                actual.Resize(index, width);
                oracle.Resize(index, width);
                Check(Enumerable.Range(0, 8).All(i => actual.GetWidth(i) == oracle.Widths[i]));
            }
        }
    }),
    ("Process columns hide at exact thresholds and ignore unchanged or invalid viewports", () =>
    {
        var columns = new ProcessColumnWidths();
        Check(!columns.FitViewport(572));
        columns.FitViewport(739);
        Check(!columns.IoVisible && !columns.ThreadsVisible);
        columns.FitViewport(740);
        Check(columns.IoVisible && !columns.ThreadsVisible);
        columns.FitViewport(839);
        Check(columns.IoVisible && !columns.ThreadsVisible);
        columns.FitViewport(840);
        Check(columns.IoVisible && columns.ThreadsVisible);
        var before = Enumerable.Range(0, 8).Select(columns.GetWidth).ToArray();
        foreach (var width in new[] { 840, 840.49, 839.51, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 32, 0, -1 })
            Check(!columns.FitViewport(width));
        Check(Enumerable.Range(0, 8).Select(columns.GetWidth).SequenceEqual(before));
        Check(columns.FitViewport(840.5));
        Check(!columns.Resize(0, columns.GetWidth(0)));
        Check(!columns.Resize(0, double.PositiveInfinity));
        Check(!columns.Resize(8, 100));
        columns.FitViewport(100);
        Check(!columns.FitViewport(101));
    }),
    ("Sticky CPU sorting preserves equal displayed values and moves real changes", () =>
    {
        var first = new ProcessSortKey("same", 0.021, 100, 30, 0);
        var second = new ProcessSortKey("same", 0.049, 100, 10, 1);
        Check(StickyProcessSort.Compare(first, second, "CPU", true) < 0);
        Check(StickyProcessSort.Compare(first, second, "CPU", false) < 0);
        Check(StickyProcessSort.Compare(first, second with { Cpu = 0.11 }, "CPU", true) > 0);
        Check(StickyProcessSort.Compare(first, second with { Cpu = 0.11 }, "CPU", false) < 0);
        foreach (var value in new[] { 0d, 0.049, 0.05, 0.15, 1.25, 1.35, 99.999 })
            Check(StickyProcessSort.DisplayedCpu(value).ToString("F1") == value.ToString("F1"));
    }),
    ("Sticky process sorting uses current order then existing before new then PID", () =>
    {
        foreach (var sort in new[] { "Name", "CPU", "Private memory" })
        foreach (var descending in new[] { true, false })
        {
            var earlier = new ProcessSortKey("Example", 1, 100, 40, 0);
            var later = new ProcessSortKey("example", 1, 100, 10, 1);
            var added = new ProcessSortKey("EXAMPLE", 1, 100, 1, int.MaxValue);
            Check(StickyProcessSort.Compare(earlier, later, sort, descending) < 0);
            Check(StickyProcessSort.Compare(later, added, sort, descending) < 0);
            Check(StickyProcessSort.Compare(added, added with { Pid = 2 }, sort, descending) < 0);
        }
        var key = new ProcessSortKey("a", 0, 0, 10, 0);
        Check(StickyProcessSort.Compare(key, key with { Pid = 20 }, "PID", true) > 0);
        Check(StickyProcessSort.Compare(key, key with { Pid = 20 }, "PID", false) < 0);
        Check(StickyProcessSort.Compare(key, key, "CPU", true) == 0);
        Check(!Array.Empty<ProcessSortKey>().Order(Comparer<ProcessSortKey>.Create((a, b) => StickyProcessSort.Compare(a, b, "CPU", true))).Any());
    }),
    ("Sticky process sorting preserves unavailable placement in both directions", () =>
    {
        var known = new ProcessSortKey("same", 0, 100, 10, 1);
        var missing = new ProcessSortKey("same", null, null, 20, 0);
        foreach (var descending in new[] { true, false })
        {
            Check(StickyProcessSort.Compare(known, missing, "CPU", descending) < 0);
            Check(StickyProcessSort.Compare(known, missing, "Private memory", descending) < 0);
        }
        Check(StickyProcessSort.Compare(known with { Memory = 0 }, missing, "Private memory", true) > 0);
        Check(StickyProcessSort.Compare(known with { Memory = ulong.MaxValue }, missing, "Private memory", false) > 0);
    }),
    ("Sticky sort keys cache CPU display precision with F1 parity", () =>
    {
        var culture = System.Globalization.CultureInfo.InvariantCulture;
        var values = Enumerable.Range(0, 200001).Select(i => i * 0.0005).Concat(
            new[] { -0d, -0.05, 0.05, 0.15, 0.25, 1.25, 1.35, 99.95, 100d,
                double.Epsilon, double.MaxValue, double.MinValue, double.NaN, double.PositiveInfinity, double.NegativeInfinity });
        foreach (var value in values)
        {
            var expected = double.Parse(value.ToString("F1", culture), culture);
            var key = new ProcessSortKey("test", value, 0, 1, 0);
            Check(key.Cpu!.Value.Equals(expected));
            Check((key with { Cpu = value }).Cpu!.Value.Equals(expected));
        }
        var unknown = new ProcessSortKey("test", null, 0, 1, 0);
        Check(unknown.Cpu is null);
        Check((unknown with { Cpu = 1.25 }).Cpu == 1.2);
        Check((unknown with { Cpu = null }).Cpu is null);
        var left = new ProcessSortKey("same", 0.021, 0, 1, 0);
        var right = new ProcessSortKey("same", 0.049, 0, 2, 1);
        var comparer = Comparer<ProcessSortKey>.Create((a, b) => StickyProcessSort.Compare(a, b, "CPU", true));
        comparer.Compare(left, right);
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 10000; i++) comparer.Compare(left, right);
        Check(GC.GetAllocatedBytesForCurrentThread() == allocated);
    })
    ,("Storage bounded aggregation, partial results and metadata", () =>
    {
        var now = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        StorageEntry File(string name, long logical = 100, long allocated = 10, long id = 0, int days = 0) => new(name, false, logical, allocated, 0, 0, id, now.UtcDateTime.AddDays(-days), now.UtcDateTime);
        StorageEntry Dir(string name, uint tag = 0, FileAttributes attrs = 0) => new(name, true, 0, 0, attrs, tag, 0, default, default);
        var a = new StorageScanAggregator("C:/", new(DirectoryNodes: 2, HardLinkIds: 1, LargestFiles: 2, Extensions: 1, Problems: 1), true, now);
        var sub = a.AddListing(0, "C:/", [Dir("kept"), Dir("rolled"), Dir("junction", 0xA0000003, FileAttributes.ReparsePoint), Dir("link", 0xA000000C, FileAttributes.ReparsePoint), Dir("cloud", 0x9000001A, FileAttributes.ReparsePoint), Dir("offline", 0, FileAttributes.ReparsePoint | FileAttributes.Offline), Dir("."), File("one.txt", id: 1), File("duplicate.txt", id: 1), File("two.bin", id: 2, days: 50), File("three.zz", days: 100), File("four.zz", days: 500), File("five.zz", days: 900), new("unknown", false, 100, 0, 0, 0, 0, default, default)]);
        Check(sub.Count == 4 && sub[0].Node == 1 && sub[1].Node == 0);
        foreach (var (node, path) in sub) a.AddListing(node, path, [File("sparse", 1000, 0)]);
        a.AddDenied(0, "C:/private", "denied"); a.AddDenied(1, "C:/private2", "denied");
        Check(a.Progress.Files == 10 && a.Progress.Denied == 2);
        var r = a.Complete(StorageScanState.Cancelled, TimeSpan.FromSeconds(1), "synthetic", []);
        Check(r.IsPartial && r.DirectoryCapReached && r.HardLinkCapReached && r.HardLinkIdsTracked == 1);
        Check(r.HardLinkDuplicates == 1 && r.HardLinkLogicalBytes == 100 && r.HardLinkAllocatedBytes == 10);
        Check(r.ReparseTotal == 2 && r.ReparsePoints.Count == 1 && r.ReparsePoints[0].Reason.Contains("junction"));
        Check(r.DeniedTotal == 2 && r.DeniedPaths.Count == 1 && r.Nodes.Sum(n => n.Denied) == 2);
        Check(r.Totals.Files == 10 && r.Totals.Directories == 4 && r.Totals.LogicalBytes == 4600 && r.Totals.AllocatedBytes == 50);
        Check(r.Nodes[1].LogicalBytes == 1000 && r.FullPath(1) == @"C:\kept");
        Check(r.LargestFiles.Count == 2 && r.Extensions.Count == 2 && r.Extensions.Any(e => e.Name == "(other)"));
        Check(r.AgeBuckets.Select(b => b.Count).SequenceEqual(new long[] { 5, 1, 1, 1, 1, 1 }));
        var threw = false; try { a.AddListing(0, "C:/", []); } catch (InvalidOperationException) { threw = true; } Check(threw);
        Check(StorageBusTypes.Name(17) == "NVMe" && StorageBusTypes.Name(100) == "Unknown (100)");
        Check(StorageBusTypes.Media(null, 17).Value == "SSD" && StorageBusTypes.Media(true, 11).Value == "HDD" && StorageBusTypes.Media(null, null).Display.Contains("Unavailable"));
        Check(StorageBusTypes.Media(false, 14).Value!.Contains("Virtual disk"));
        var empty = new StorageScanAggregator("C:/", new(), false, now).Complete(StorageScanState.Completed, TimeSpan.Zero, "empty", []);
        Check(empty.Totals.Files == 0 && !empty.IsPartial);
        Check(StorageFileSystems.HasStableFileIds("ntfs") && StorageFileSystems.HasStableFileIds("ReFS"));
        Check(new string?[] { "FAT", "FAT32", "exFAT", "other", null }.All(fs => !StorageFileSystems.HasStableFileIds(fs)));
        var reading = new StorageReading(null, "not reported");
        var volume = new StorageVolume("C:/", "disk", "NTFS", "Fixed", 100, 25, 20, 4096, reading, reading, reading, reading, reading, null);
        Check(volume.UsedBytes == 75 && volume.UsedFraction == 0.75);
        Check((volume with { TotalBytes = null }).UsedBytes == null && (volume with { FreeBytes = 200 }).UsedFraction == null);
        Check((volume with { TotalBytes = 0, FreeBytes = 0 }).UsedFraction == null);
    }),
    ("Storage developer artifacts, projects, catalogs and AI leads", () =>
    {
        var now = new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var a = new StorageScanAggregator("C:/", new(), false, now);
        StorageEntry F(string name, int days = 0) => new(name, false, 100, 10, 0, 0, 0, now.UtcDateTime.AddDays(-days), now.UtcDateTime.AddMinutes(-2));
        StorageEntry D(string name) => new(name, true, 0, 0, 0, 0, 0, default, default);
        int Add(int parent, string path, string name, params StorageEntry[] files)
        {
            var child = a.AddListing(parent, path, [D(name)]).Single();
            a.AddListing(child.Node, child.Path, files); return child.Node;
        }
        var repo = Add(0, "C:/", "repo", D(".git"), F("package.json"), F("app.csproj"), F("Cargo.toml"), F("AGENTS.md"));
        var pkg = Add(repo, "C:/repo", "pkg", F("package.json"));
        var nm = Add(pkg, "C:/repo/pkg", "node_modules", F("data"));
        Add(nm, "C:/repo/pkg/node_modules", "node_modules", F("nested"));
        Add(repo, "C:/repo", "bin", F("assembly")); Add(repo, "C:/repo", "obj", F("obj")); Add(repo, "C:/repo", "target", F("target"));
        Add(repo, "C:/repo", ".venv", F("pyvenv.cfg")); Add(repo, "C:/repo", "venv", F("notvenv"));
        Add(0, "C:/", "bin", F("unrelated")); Add(0, "C:/", "target", F("unrelated"));
        Add(0, "C:/", "old", F("go.mod", 500));
        var bulk = Add(repo, "C:/repo", "bulk"); a.AddListing(bulk, "C:/repo/bulk", Enumerable.Range(0, 200).Select(i => F(i + ".cs")).ToArray());
        Add(repo, "C:/repo", "worktrees", F(".git"));
        var local = Add(0, "C:/", "local"); var packages = Add(local, "C:/local", "Packages"); var ubuntu = Add(packages, "C:/local/Packages", "FooCanonicalUbuntu"); Add(ubuntu, "C:/local/Packages/FooCanonicalUbuntu", "LocalState", F("disk.vhdx"));
        var otherUbuntu = Add(packages, "C:/local/Packages", "OtherCanonicalUbuntu"); Add(otherUbuntu, "C:/local/Packages/OtherCanonicalUbuntu", "LocalState", F("disk.vhdx"));
        var home = Add(0, "C:/", "home"); var codex = Add(home, "C:/home", ".codex", F("config")); Add(codex, "C:/home/.codex", "sessions", F("chat"));
        Add(0, "C:/", "custom-nuget", F("package"));
        var nuget = Add(local, "C:/local", "NuGet"); Add(nuget, "C:/local/NuGet", "v3-cache", F("http-cache"));
        Add(0, "C:/", "temp", F("in-use"));
        var scan = a.Complete(StorageScanState.Completed, TimeSpan.Zero, "names only", []);
        var ctx = new DeveloperLocationContext("C:/home", "C:/local", "D:/roaming", "D:/programdata", "C:/temp", new Dictionary<string, string> { ["NUGET_PACKAGES"] = "C:/custom-nuget" });
        var report = DeveloperBloatAnalyzer.Analyze(scan, ctx, now, TimeSpan.FromDays(90));
        var project = report.Projects.Single(p => p.Path == @"C:\repo");
        Check(!report.Projects.Any(p => p.Path == @"C:\repo\pkg") && project.RegenerableBytes == 60 && project.Artifacts.Count == 5);
        Check(!report.Items.Any(i => i.Path == @"C:\bin" || i.Path == @"C:\target" || i.Path == @"C:\repo\venv"));
        Check(report.Projects.Single(p => p.Path == @"C:\old").IsStale && !project.IsStale);
        Check(report.Items.Where(i => i.Tool.StartsWith("WSL — ") && i.Path.EndsWith("LocalState") && i.ReclaimableBytes == null).Select(i => i.Tool).Distinct().Count() == 2);
        Check(report.Items.Any(i => i.Tool == "NuGet packages" && i.Path == @"C:\custom-nuget"));
        Check(report.Items.Any(i => i.Tool == "NuGet HTTP cache" && i.Path == @"C:\local\NuGet\v3-cache"));
        Check(report.Items.Single(i => i.Tool == "User temp") is { Confidence: BloatConfidence.Medium, ReclaimableBytes: 10, Reason: "files may be in use by running programs; delete only items not in use" });
        Check(report.NotScanned.Any(i => i.Reason == "Not scanned (outside scanned folder)") && report.AbsentCatalogEntries > 0);
        Check(report.Items.Any(i => i.IsLead && i.Tool == "Bulk creation") && report.Items.Any(i => i.IsLead && i.Tool == "Git worktree" && i.Confidence == BloatConfidence.Medium));
        Check(report.Items.Where(i => i.IsLead).All(i => i.Reason.StartsWith("Lead:")));
        Check(report.CategoryTotals.Single(c => c.Category == "AI tools and models").AllocatedBytes == 20);
        Check(report.Items.Where(i => i.Kind == "AI tool data").All(i => i.ReclaimableBytes == 0));
        Check(DeveloperBloatAnalyzer.Analyze(new StorageScanAggregator("Z:/", new(), false).Complete(StorageScanState.Failed, TimeSpan.Zero, "failed", []), ctx, now, TimeSpan.Zero).Projects.Count == 0);
    }),
    ("Storage category known sums and unknown counts share global path de-duplication", () =>
    {
        BloatItem Item(string category, string path, long allocated, long? reclaim, bool lead = false) =>
            new(category, "fixture", path, allocated, reclaim, null, BloatConfidence.High, "metadata", null, null, "Cache", lead);
        BloatItem[] items = [
            Item("Global caches", @"C:\cache", 100, 100),
            Item("Global caches", @"C:\global-unknown", 40, null),
            Item("Global caches", @"C:\cache\unknown", 10, null),
            Item("Artifacts", "c:/CACHE", 100, 100),
            Item("Artifacts", @"C:\cache\nested", 25, 25),
            Item("Artifacts", @"C:\project\bin", 60, 60),
            Item("Artifacts", @"C:\artifact-unknown", 20, null),
            Item("AI", @"C:\disk", 500, null),
            Item("AI", @"C:\disk\model", 80, 80),
            Item("Settings", @"C:\settings", 10, 0),
            Item("Leads", @"C:\lead", 200, null, true)
        ];
        var totals = DeveloperBloatAnalyzer.Summarize(items);
        var global = totals.Single(x => x.Category == "Global caches");
        var artifacts = totals.Single(x => x.Category == "Artifacts");
        var ai = totals.Single(x => x.Category == "AI");
        var settings = totals.Single(x => x.Category == "Settings");
        Check(global is { AllocatedBytes: 140, ReclaimableBytes: 100, UnknownItems: 1 });
        Check(artifacts is { AllocatedBytes: 80, ReclaimableBytes: 60, UnknownItems: 1 });
        Check(ai is { AllocatedBytes: 500, ReclaimableBytes: null, UnknownItems: 1 });
        Check(settings is { ReclaimableBytes: 0, UnknownItems: 0 } && totals.Count == 4);
        var report = new DeveloperBloatReport(items, [], totals, [], 0);
        Check(report.KnownReclaimableBytes == 160 && report.KnownReclaimableBytes == totals.Sum(x => x.ReclaimableBytes ?? 0) && report.UnknownItems == 3);
        var empty = new DeveloperBloatReport([], [], DeveloperBloatAnalyzer.Summarize([]), [], 0);
        Check(empty.CategoryTotals.Count == 0 && empty.KnownReclaimableBytes == 0 && empty.UnknownItems == 0);
        var scan = new StorageScanAggregator(@"C:\", new(), false).Complete(StorageScanState.Completed, TimeSpan.Zero, "metadata", []);
        using var json = System.Text.Json.JsonDocument.Parse(StorageExport.ToJson(scan, report));
        var exported = json.RootElement.GetProperty("Bloat");
        Check(exported.GetProperty("KnownReclaimableBytes").GetInt64() == 160 && exported.GetProperty("UnknownItems").GetInt32() == 3);
        var exportedGlobal = exported.GetProperty("CategoryTotals").EnumerateArray().Single(x => x.GetProperty("Category").GetString() == "Global caches");
        Check(exportedGlobal.GetProperty("ReclaimableBytes").GetInt64() == 100 && exportedGlobal.GetProperty("UnknownItems").GetInt32() == 1);
    }),
    ("Storage exports escape CSV cells and JSON metadata", () =>
    {
        var a = new StorageScanAggregator("=root,\"quoted\"\nline", new(), false);
        foreach (var text in new[] { "=formula", "+plus", "-minus", "@function", "\ttab", "\rreturn" }) a.AddDenied(0, text, "quote\"comma,\nline");
        var scan = a.Complete(StorageScanState.Cancelled, TimeSpan.Zero, "test", []);
        var csv = StorageExport.ToCsv(scan);
        Check(csv.Contains("\"'=root,\"\"quoted\"\"\nline\"") && csv.Contains("\"Partial\"") && csv.Contains("quote\"\"comma,\nline"));
        foreach (var text in new[] { "=formula", "+plus", "-minus", "@function", "\ttab", "\rreturn" }) Check(csv.Contains("\"'" + text + "\""));
        using var json = System.Text.Json.JsonDocument.Parse(StorageExport.ToJson(scan));
        Check(json.RootElement.GetProperty("Scan").GetProperty("IsPartial").GetBoolean());
        Check(json.RootElement.GetProperty("Scan").GetProperty("Root").GetString() == scan.Root);
        var native = new StorageScanAggregator("C:/fixture", new(), false);
        var child = native.AddListing(0, "C:/fixture", [new("folder", true, 0, 0, 0, 0, 0, default, default)]).Single();
        native.AddListing(child.Node, child.Path, [new("package.json", false, 1, 1, 0, 0, 0, default, default)]);
        native.AddDenied(child.Node, "C:/fixture/folder/denied", "Access denied");
        var nativeScan = native.Complete(StorageScanState.Completed, TimeSpan.Zero, "metadata", []);
        var nativeReport = DeveloperBloatAnalyzer.Analyze(nativeScan, new("C:/fixture", "D:/local", "D:/roaming", "D:/data", "D:/temp"), DateTimeOffset.UtcNow, TimeSpan.FromDays(90));
        Check(child.Path == @"C:\fixture\folder" && nativeScan.Root == @"C:\fixture" && nativeScan.FullPath(child.Node) == child.Path);
        Check(nativeScan.LargestFiles.Single().Path == @"C:\fixture\folder\package.json" && nativeScan.DeniedPaths.Single().Path == @"C:\fixture\folder\denied");
        Check(nativeReport.Projects.Single().Path == child.Path && nativeReport.NotScanned.All(p => !p.Path.Contains('/')));
        Check(StoragePaths.Join("//server/share", "folder/file") == @"\\server\share\folder\file");
        Check(StoragePaths.Native("") == "" && StoragePaths.Native(@"C:\") == @"C:\");
        var nativeCsv = StorageExport.ToCsv(nativeScan, nativeReport);
        Check(nativeCsv.Contains(@"C:\fixture\folder\package.json") && !nativeCsv.Contains("C:/"));
        using var nativeJson = System.Text.Json.JsonDocument.Parse(StorageExport.ToJson(nativeScan, nativeReport));
        Check(nativeJson.RootElement.GetProperty("Bloat").GetProperty("Projects")[0].GetProperty("Path").GetString() == child.Path);
    }),
    ("Storage million-file bounded-memory performance sanity", () =>
    {
        var timer = System.Diagnostics.Stopwatch.StartNew();
        var a = new StorageScanAggregator("C:/", new(LargestFiles: 10, Extensions: 10), false);
        var entries = Enumerable.Range(0, 20).Select(i => new StorageEntry(i + ".bin", false, 100, 10, 0, 0, 0, DateTime.UnixEpoch, DateTime.UnixEpoch)).ToArray();
        Parallel.For(0, 50_000, new ParallelOptions { MaxDegreeOfParallelism = 4 }, i =>
        {
            var d = a.AddListing(0, "C:/", [new(i.ToString(), true, 0, 0, 0, 0, 0, default, default)]).Single();
            a.AddListing(d.Node, d.Path, entries);
        });
        var r = a.Complete(StorageScanState.Completed, timer.Elapsed, "synthetic", []);
        Console.WriteLine($"Storage perf: 1,000,000 files / 50,000 dirs in {timer.Elapsed}; retained {r.Nodes.Count} nodes, {r.LargestFiles.Count} files");
        Check(r.Totals.Files == 1_000_000 && r.Nodes.Count == 50_001 && r.LargestFiles.Count == 10 && r.Totals.AllocatedBytes == 10_000_000);
        Check(timer.Elapsed < TimeSpan.FromMinutes(2));
    })
};
tests = tests.Append(("Hidden item classification and conservative lead rules", (Action)HiddenItemsTests.Run)).ToArray();
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

sealed record KeyedTestRow(int Key, string Text);

sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset current;

    public ManualTimeProvider(DateTimeOffset current) => this.current = current;
    public override DateTimeOffset GetUtcNow() => current;
    public void Advance(TimeSpan duration) => current += duration;
}

// Area C: diagnostics labels and history status
static class AreaCDiagnosticsTests
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Run()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("Area C rows retain instances, reorder, append and remove", Rows),
            ("Area C service ownership distinguishes no process from unverified", Services),
            ("Area C GPU notes include unavailable reasons", Gpu),
            ("Area C history status distinguishes missing and observed samples", History),
            ("Area C missing rates remain unavailable", () => Require(DiagnosticData.Rate(null) == "—" && DiagnosticData.Rate(0) == "0 B/s"))
        };
        foreach (var (name, run) in tests)
        {
            try { run(); Console.WriteLine($"PASS {name}"); }
            catch (Exception ex) { throw new InvalidOperationException($"FAIL {name}: {ex.Message}", ex); }
        }
        Console.WriteLine($"{tests.Length}/{tests.Length} Area C passed");
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed");
    }

    private static DiagnosticItem Item(string key, string detail = "") => new(key, null, key, detail, null, "", "", "");

    private static void Rows()
    {
        var rows = new DiagnosticRows();
        rows.Apply([], true);
        Require(rows.Rows.Count == 0);
        rows.Apply([Item("a"), Item("b"), Item("c")], true);
        var a = rows.Rows[0];
        var b = rows.Rows[1];
        var c = rows.Rows[2];
        var moves = new List<int>();
        rows.Rows.CollectionChanged += (_, e) =>
        {
            if (e.Action is System.Collections.Specialized.NotifyCollectionChangedAction.Move) moves.Add(e.NewStartingIndex);
        };
        rows.Apply([Item("c", "updated"), Item("d"), Item("a"), Item("b")], true);
        Require(rows.Rows.Select(r => r.Key).SequenceEqual(["c", "d", "a", "b"]));
        Require(ReferenceEquals(c, rows.Rows[0]) && ReferenceEquals(a, rows.Rows[2]) && ReferenceEquals(b, rows.Rows[3]));
        Require(c.Detail == "updated" && moves.SequenceEqual([0]));
        rows.Apply([Item("b", "new"), Item("e"), Item("a")], false);
        Require(rows.Rows.Select(r => r.Key).SequenceEqual(["a", "b", "e"]));
        Require(ReferenceEquals(a, rows.Rows[0]) && ReferenceEquals(b, rows.Rows[1]) && b.Detail == "new");
        var random = new Random(23);
        for (var pass = 0; pass < 50; pass++)
        {
            var desired = Enumerable.Range(0, 25).OrderBy(_ => random.Next()).Take(random.Next(26)).Select(n => Item(n.ToString())).ToArray();
            var retained = rows.Rows.ToDictionary(r => r.Key);
            rows.Apply(desired, true);
            Require(rows.Rows.Select(r => r.Key).SequenceEqual(desired.Select(r => r.Key)));
            foreach (var row in rows.Rows)
                if (retained.TryGetValue(row.Key, out var old)) Require(ReferenceEquals(row, old));
        }
        rows.Apply([], false);
        Require(rows.Rows.Count == 0);
    }

    private static void Services()
    {
        var at = DateTimeOffset.UnixEpoch;
        var identity = new ProcessIdentity(42, 123);
        var snapshot = new MonitorSnapshot { CapturedAt = at, Processes = [new ProcessSnapshot { Identity = identity, Name = "owner" }] };
        var service = new ServiceSnapshot { Name = "svc", DisplayName = "Service", State = "Stopped" };
        DiagnosticItem[] Create(params ServiceSnapshot[] services) => DiagnosticData.Create("Services", snapshot,
            new RelationshipSnapshot { CapturedAt = at, Services = services }, [], new(), _ => null).ToArray();
        Require(Create().Length == 0);
        foreach (var pid in new int?[] { null, 0 })
        {
            var row = Create(service with { ProcessId = pid, OwnerIdentity = identity }).Single();
            Require(row.Third == "No process" && row.Detail == "svc · Not running; no owner process" && row.Identity is null);
        }
        var running = service with { State = "Running", ProcessId = 42 };
        var unknown = Create(running).Single();
        Require(unknown.Third == "Owner unavailable" && unknown.Detail.Contains("Owner unverified or stale"));
        var verified = Create(running with { OwnerIdentity = identity }).Single();
        Require(verified.Third == "owner" && verified.Identity == identity);
        var stale = Create(running with { OwnerIdentity = new(42, 456) }).Single();
        Require(stale.Third == "Owner unavailable");
        var duplicate = Create(service, running);
        Require(duplicate.Length == 1 && duplicate[0].Third == "No process");
    }

    private static void Gpu()
    {
        var missing = CounterValue.Unavailable("Provider denied access");
        var adapter = new GpuAdapterRow(1, "Adapter", 0, 0, missing, missing, [], missing, null);
        DiagnosticItem Create(CounterValue value) => DiagnosticData.Create("GPU",
            new MonitorSnapshot { CapturedAt = DateTimeOffset.UnixEpoch, Processes = [], GpuAdapters = [adapter with { HeadlineUtilizationPercent = value }] },
            null, [], new(), _ => null).Single();
        Require(Create(missing).Detail.Contains("GPU utilization: unavailable (Provider denied access)"));
        var warmup = CounterValue.Warmup("First sample");
        Require(Create(warmup).Detail.Contains("unavailable (First sample)"));
        var invalid = CounterValue.Measured(double.NaN);
        Require(Create(invalid).Detail.Contains($"unavailable ({invalid.State})"));
        Require(Create(CounterValue.Measured(0)).Detail.Contains("GPU utilization: 0.0%"));
    }

    private static void History()
    {
        var at = DateTimeOffset.UnixEpoch;
        var history = new ProcessHistorySnapshot(new(42, 123), "owner", []);
        var baseline = ProcessHistoryStatus.Build(null);
        Require(baseline.Contains("updates every second · 8 lifetimes / 180 samples"));
        Require(ProcessHistoryStatus.Build(history) == baseline);
        var observed = new ProcessHistoryPoint(at, true, false, 1, 2, 3, 0, 0, 0);
        Require(ProcessHistoryStatus.Build(history with { Points = [observed] }) == baseline);
        var missing = observed with { At = at.AddSeconds(1), Observed = false };
        var status = ProcessHistoryStatus.Build(history with { Points = [observed, missing] });
        Require(status.Contains($"not observed since {missing.At.LocalDateTime:HH:mm:ss}"));
        Require(status.Contains("retained samples stay visible (missing is not confirmed exit)"));
        Require(ProcessHistoryStatus.Build(history with { Points = [missing, observed with { At = at.AddSeconds(2) }] }) == baseline);
    }
}

static class RamAttributionTests
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Run()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("RAM working-set attributes and proportional bound", WorkingSet),
            ("RAM reconciliation exact, overlap, missing and compression", Reconcile),
            ("RAM pool tag string leads, protected bit and cap", Tags),
            ("RAM PFN and memory-list x64 decoding", Physical)
        };
        foreach (var (name, run) in tests)
        {
            try { run(); Console.WriteLine($"PASS {name}"); }
            catch (Exception ex) { throw new InvalidOperationException($"FAIL {name}: {ex.Message}", ex); }
        }
        Console.WriteLine($"{tests.Length}/{tests.Length} RAM attribution passed");
    }

    private static void Require(bool value) { if (!value) throw new InvalidOperationException("Assertion failed"); }
    private static void Throws<T>(Action action) where T : Exception
    { try { action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }

    private static void WorkingSet()
    {
        var result = RamAttribution.AggregateWorkingSet(new(1, 2), "test", [0x12345000, 256, 256 | 32, 256 | 64, 256 | 224], 4096, true);
        Require(result.PagesScanned == 5 && result.PrivateResidentBytes == 4096 && result.ShareableResidentBytes == 16384);
        Require(result.ActuallySharedBytes == 8192 && result.ShareableSoleBytes == 8192);
        Require(Math.Abs(result.ProportionalEstimateBytes - (4096 * 3 + 2048 + 4096d / 7)) < .001);
        Require(result.Truncated && result.Status == RamReadingStatus.Truncated && result.Detail.Contains("lower bounds") && result.Detail.Contains("saturates"));
        var empty = RamAttribution.AggregateWorkingSet(default, "empty", [], 4096);
        Require(empty.PagesScanned == 0 && empty.PrivateResidentBytes == 0 && empty.ProportionalEstimateBytes == 0);
        Throws<ArgumentOutOfRangeException>(() => RamAttribution.AggregateWorkingSet(default, "", [], 0));
        Throws<OperationCanceledException>(() => RamAttribution.AggregateWorkingSet(default, "", [0], 4096, false, new CancellationToken(true)));
    }

    private static void Reconcile()
    {
        ProcessSnapshot P(string name, ulong bytes) => new() { Identity = new(1, 2), Name = name, PrivateWorkingSetBytes = bytes, WorkingSetBytes = bytes };
        var input = new RamMachineInputs(120, 100, 10, [5, 5, 0, 0, 0, 0, 0, 0], 10, 5, 5, 5, 5, 5, [P("app", 20), P("Memory Compression", 10)]);
        var result = RamAttribution.Reconcile(input);
        Require(result.HardwareReservedBytes == 20 && result.Categories.Sum(r => r.Bytes) == 100);
        Require(result.Categories[0].Bytes == 20 && result.Categories[1].Bytes == 10 && result.Categories.Last().Bytes == 15);
        var overlap = RamAttribution.Reconcile(input with { UsableBytes = 50 });
        Require(overlap.Categories.Last().Bytes == -35 && overlap.Categories.Last().Name.StartsWith("Overlap"));
        var missing = RamAttribution.Reconcile(input with { NonPagedPoolBytes = null });
        Require(missing.Categories.Last().Bytes is null && missing.Categories.Last().Status == RamReadingStatus.Unavailable);
        var empty = RamAttribution.Reconcile(input with { Processes = [] });
        Require(empty.Categories[0].Bytes == 0 && empty.Categories[1].Bytes == 0 && empty.Categories[1].Detail.Contains("Off") && empty.Categories.Last().Bytes == 45);
        Require(RamAttribution.Reconcile(input with { InstalledBytes = null }).Categories.Sum(c => c.Bytes) == 100);
    }

    private static void Tags()
    {
        const uint tag = 0x64636261;
        Require(PoolTagDriverMatcher.Match([0, 97, 98, 99, 100, 0], new HashSet<uint> { tag }).Contains(tag));
        Require(PoolTagDriverMatcher.Match([97, 98, 99, 228], new HashSet<uint> { tag }).Contains(tag));
        Require(PoolTagDriverMatcher.Match([97, 98, 99, 100], new HashSet<uint> { tag | 0x80000000 }).Contains(tag | 0x80000000));
        Require(PoolTagDriverMatcher.Match([], new HashSet<uint> { tag }).Count == 0);
        Require(PoolTagDriverMatcher.Lead(tag, []).Detail.Contains("Unknown"));
        var capped = PoolTagDriverMatcher.Lead(tag, Enumerable.Range(0, 12).Select(i => $"{i}.sys"));
        Require(capped.TotalMatches == 12 && capped.CandidateFiles.Count == 8 && capped.Detail.Contains("not proof"));
        Throws<ArgumentOutOfRangeException>(() => PoolTagDriverMatcher.Lead(tag, [], -1));
    }

    private static void Physical()
    {
        var bytes = new byte[24];
        void Write(int offset, ulong value) => System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
        Write(0, 8 | (2UL << 4) | 128 | 256 | (5UL << 57) | (1UL << 60)); Write(8, 123); Write(16, 456);
        var p = PhysicalMemoryParser.ParseIdentity(bytes);
        Require(p.Use == PhysicalPageUse.Metafile && p.List == PhysicalPageList.Standby && p.Priority == 5 && p.Cold && p.Pinned && p.NonTradeable && p.PageFrameIndex == 123 && p.FileObject == 456);
        var accumulator = new PhysicalMemoryAccumulator(); accumulator.Add(bytes);
        var result = accumulator.Build(RamReadingStatus.Available, "test", null);
        Require(result.PagesScanned == 1 && result.Counts.Single().Pages == 1 && result.FileObjects.Single().PagesByList[2] == 1 && result.Detail.Contains("unavailable"));
        for (var i = 0; i < 260; i++) { Write(16, (ulong)i); accumulator.Add(bytes); }
        result = accumulator.Build(RamReadingStatus.Truncated, "", null);
        Require(result.FileObjects.Count == 257 && result.FileObjects.Sum(r => (long)r.PagesByList.Sum(v => (decimal)v)) == 261);
        var lists = new byte[176];
        for (var i = 0; i < 22; i++) System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(lists.AsSpan(i * 8), (ulong)i);
        var parsed = PhysicalMemoryParser.ParseMemoryLists(lists);
        Require(parsed.Zero == 0 && parsed.Free == 1 && parsed.Modified == 2 && parsed.ModifiedNoWrite == 3 && parsed.Bad == 4 && parsed.StandbyByPriority[0] == 5 && parsed.StandbyByPriority[7] == 12 && parsed.RepurposedByPriority[7] == 20 && parsed.ModifiedPageFile == 21);
        Require(PhysicalMemoryParser.NotAdministrator().Status == RamReadingStatus.RequiresAdministrator && PhysicalMemoryParser.NotAdministrator().PagesScanned == 0);
        Require(new PhysicalMemoryAccumulator().Build(RamReadingStatus.Available, "", null).Counts.Count == 0);
        Throws<ArgumentException>(() => PhysicalMemoryParser.ParseIdentity(new byte[23]));
        Throws<ArgumentException>(() => PhysicalMemoryParser.ParseMemoryLists(new byte[175]));
        Throws<ArgumentException>(() => accumulator.Add(new byte[25]));
        foreach (var list in new[] { PhysicalPageList.Zero, PhysicalPageList.Free, PhysicalPageList.Bad })
        {
            var unused = new PhysicalMemoryAccumulator(); Write(0, 8 | ((ulong)list << 4)); unused.Add(bytes);
            var row = unused.Build(RamReadingStatus.Available, "", null);
            Require(row.Counts.Single().Use == PhysicalPageUse.Unused && row.Counts.Single().List == list && row.FileObjects.Count == 0);
        }
    }
}

static class RamPresentationTests
{
    [System.Runtime.CompilerServices.ModuleInitializer]
    internal static void Run()
    {
        static void Require(bool value) { if (!value) throw new InvalidOperationException("RAM presentation assertion failed"); }
        Require(RamAttributionPresentation.PoolTag(0x6c66744e) == "Ntfl" && RamAttributionPresentation.PoolTag(0x7f002041) == "A \\x00\\x7F");
        Require(RamAttributionPresentation.UseName(PhysicalPageUse.Unknown12) == "Use 12 (undocumented)");
        Require(RamAttributionPresentation.StatusName(RamReadingStatus.IdentityMismatch) == "Exited / PID reused");
        Require(RamAttributionPresentation.StatusName(RamReadingStatus.RequiresAdministrator) == "Requires administrator");
        Require(RamAttributionPresentation.StatusName(RamReadingStatus.TimedOut) == "Timed out");
        Require(RamAttributionPresentation.StatusName(RamReadingStatus.Available) == "Available");
        var paletteNames = new[] { "Process private working sets (excluding compression)", "Compression store", "System cache resident", "Paged pool resident", "Nonpaged pool", "Driver images resident", "Kernel code resident", "Modified (including no-write)", "Free / zero", "Other in-use" }
            .Concat(Enumerable.Range(0, 8).Select(i => $"Standby priority {i}"));
        Require(paletteNames.Select(RamAttributionPresentation.CategoryColor).Distinct().Count() == 18);
        Require(RamAttributionPresentation.CategoryColor("") == RamAttributionPresentation.CategoryColor("Other in-use"));
        var category = new RamCategory("a", 25, "source", RamReadingStatus.Available, "", RamCategoryKind.Measured);
        var breakdown = new RamBreakdown(null, 100, null, [category, category with { Name = "b", Bytes = 75 }]);
        Require(RamAttributionPresentation.BarSegments(breakdown).Select(s => s.Fraction).SequenceEqual(new[] { .25, .75 }));
        Require(RamAttributionPresentation.BarSegments(breakdown with { Categories = [category with { Bytes = -1 }] }).Count == 0);
        Require(RamAttributionPresentation.BarSegments(breakdown with { Categories = [category with { Bytes = null }] }).Count == 0);
        Require(RamAttributionPresentation.BarSegments(breakdown with { UsableBytes = 0, Categories = [] }).Count == 0);
        var document = new RamExportDocument(DateTimeOffset.UnixEpoch, "Overview", [new("machine", "Available", "a,\"b\"\r\nc", new Dictionary<string, string> { ["Size"] = "25" })]);
        var csv = RamAttributionPresentation.Csv(document);
        Require(csv.Contains("\"a,\"\"b\"\"\r\nc\"") && csv.Contains("1970-01-01") && csv.Contains("\"Size\""));
        using var json = System.Text.Json.JsonDocument.Parse(RamAttributionPresentation.Json(document));
        Require(json.RootElement.GetProperty("Tab").GetString() == "Overview" && json.RootElement.GetProperty("Rows")[0].GetProperty("Values").GetProperty("Size").GetString() == "25");
        Require(RamAttributionPresentation.Csv(document with { Rows = [] }).Split("\r\n").Length == 2);
        Console.WriteLine("PASS RAM presentation: unused/unknown labels, pool text, bar proportions, CSV escaping and JSON shape");
    }
}

static class HiddenItemsTests
{
    internal static void Run()
    {
        void Require(bool condition) { if (!condition) throw new InvalidOperationException("Hidden items assertion failed"); }
        var at = DateTimeOffset.UnixEpoch;
        var id = new ProcessIdentity(42, 100);
        BoundedItems<T> B<T>(params T[] rows) => new(rows, 100);
        ProcessSighting S(ProcessSightingSource source, bool exited = false, InspectionStatus status = InspectionStatus.Available, long time = 100) => new(new(42, time), source, Exited: exited, Status: status);
        ProcessCrossViewSnapshot Snap(params ProcessSighting[] rows) => new(at, InspectionStatus.Available, "", false,
            B(new ProcessSourceStatus(ProcessSightingSource.Toolhelp, InspectionStatus.Available, ""), new(ProcessSightingSource.NtSystemInformation, InspectionStatus.Available, "")), B(rows));
        ProcessDiscrepancyKind Classify(ProcessCrossViewSnapshot a, ProcessCrossViewSnapshot b) => ProcessCrossView.Compare(a, b).Items.Single().Classification;
        var tool = S(ProcessSightingSource.Toolhelp);
        var nt = S(ProcessSightingSource.NtSystemInformation);
        var probe = S(ProcessSightingSource.PidProbe);
        var owner = S(ProcessSightingSource.TcpOwner);
        Require(Classify(Snap(tool, nt), Snap(tool, nt)) == ProcessDiscrepancyKind.Consistent);
        Require(Classify(Snap(probe), Snap(tool, nt, probe)) == ProcessDiscrepancyKind.StartedDuringScan);
        Require(Classify(Snap(probe), Snap(tool, probe)) == ProcessDiscrepancyKind.StartedDuringScan);
        Require(Classify(Snap(nt), Snap(S(ProcessSightingSource.PidProbe, status: InspectionStatus.Exited))) == ProcessDiscrepancyKind.ExitedDuringScan);
        Require(Classify(Snap(owner), Snap(S(ProcessSightingSource.PidProbe, status: InspectionStatus.Exited))) == ProcessDiscrepancyKind.StaleOwner);
        Require(Classify(Snap(probe), Snap(S(ProcessSightingSource.NtGetNextProcess, exited: true))) == ProcessDiscrepancyKind.Zombie);
        Require(Classify(Snap(probe), Snap(S(ProcessSightingSource.PidProbe, status: InspectionStatus.AccessDenied))) == ProcessDiscrepancyKind.ProtectedDenied);
        Require(Classify(Snap(probe), Snap(probe)) == ProcessDiscrepancyKind.Unknown);
        Require(Classify(Snap(probe), Snap()) == ProcessDiscrepancyKind.Unavailable);
        Require(Classify(Snap(tool, nt), Snap(S(ProcessSightingSource.Toolhelp, time: 200))) == ProcessDiscrepancyKind.PidReused);
        Require(Classify(Snap(tool, S(ProcessSightingSource.NtSystemInformation, time: 200)), Snap()) == ProcessDiscrepancyKind.PidReused);
        Require(ProcessCrossView.Compare(Snap(), Snap()).Items.Count == 0);
        Require(ProcessCrossView.ToLead(ProcessCrossView.Compare(Snap(probe), Snap(probe)).Items.Single())?.Severity == LeadSeverity.High);
        Require(ProcessCrossView.ToLead(ProcessCrossView.Compare(Snap(tool, nt), Snap(tool, nt)).Items.Single()) is null);
        Require(ProcessCrossView.Compare(Snap(probe), Snap(probe), 0).Truncated);
        var denied = Snap(probe) with { Sources = B(new ProcessSourceStatus(ProcessSightingSource.Toolhelp, InspectionStatus.AccessDenied, "denied")) };
        Require(Classify(denied, Snap(probe)) == ProcessDiscrepancyKind.Unavailable);
        var services = new ServiceCrossViewSnapshot(at, InspectionStatus.Available, InspectionStatus.Available, "", 1,
            B(new ServiceRegistryEntry("deleted", 16, 2, true, null), new("ignored", null, null, false, null)),
            B(new ServiceSnapshot { Name = "user_ab123", DisplayName = "", State = "Stopped" }));
        var serviceDiff = ConfigurationCrossView.CompareServices(services);
        Require(serviceDiff.Items.Count == 2 && serviceDiff.Items.Any(d => d.Reason.Contains("DeleteFlag")));
        Require(ConfigurationCrossView.CompareServices(services with { RegistryStatus = InspectionStatus.AccessDenied, ScmStatus = InspectionStatus.AccessDenied }).Items.Count == 0);
        Require(ConfigurationCrossView.CompareServices(services, 1).Truncated);
        Require(ConfigurationCrossView.CompareServices(services with { Registry = B<ServiceRegistryEntry>(), Scm = B<ServiceSnapshot>() }).Items.Count == 0);
        var tasks = new TaskCrossViewSnapshot(at, InspectionStatus.Available, InspectionStatus.Available, InspectionStatus.Available,
            InspectionStatus.Available, "", true, B(new ScheduledTaskObservation(@"\com", "", "", null, "", "", InspectionStatus.Available, "")),
            B(new TaskTreeEntry(@"\tree", "id", false)), B<TaskCacheEntry>(), B(@"\file"));
        var taskDiff = ConfigurationCrossView.CompareTasks(tasks);
        foreach (var kind in new[] { ConfigurationDiscrepancyKind.MissingSecurityDescriptor, ConfigurationDiscrepancyKind.TreeIdNotInTasks,
            ConfigurationDiscrepancyKind.RegistryNotCom, ConfigurationDiscrepancyKind.ComNotRegistry, ConfigurationDiscrepancyKind.FileNotRegistry, ConfigurationDiscrepancyKind.RegistryNotFile })
            Require(taskDiff.Items.Any(d => d.Kind == kind));
        var matched = tasks with { Com = B(new ScheduledTaskObservation(@"\TREE", "", "", null, "", "", InspectionStatus.Available, "")),
            Tree = B(new TaskTreeEntry(@"\tree", "id", true)), Cache = B(new TaskCacheEntry("ID", @"\tree")), Files = B("tree") };
        Require(ConfigurationCrossView.CompareTasks(matched).Items.Count == 0);
        Require(ConfigurationCrossView.CompareTasks(tasks with { FilesStatus = InspectionStatus.AccessDenied }).Items.All(d => d.Kind != ConfigurationDiscrepancyKind.RegistryNotFile));
        Require(ConfigurationCrossView.CompareTasks(tasks, 0).Truncated);
        Require(ConfigurationCrossView.CompareTasks(tasks with { Com = B<ScheduledTaskObservation>(), Tree = B<TaskTreeEntry>(), Cache = B<TaskCacheEntry>(), Files = B<string>() }).Items.Count == 0);
        var paths = new[] { (@"C:\Users\a\AppData\Local\Temp\x.exe", UnusualLocation.Temp), (@"C:\Users\a\AppData\Roaming\x.exe", UnusualLocation.AppData),
            (@"C:\Users\a\Downloads\x.exe", UnusualLocation.Downloads), (@"C:\ProgramData\x.exe", UnusualLocation.ProgramDataRoot),
            (@"C:\$Recycle.Bin\x.exe", UnusualLocation.RecycleBin), (@"C:\x.exe:payload", UnusualLocation.AlternateDataStream), (@"C:\Users\Public\x.exe", UnusualLocation.Public) };
        foreach (var (path, expected) in paths) Require(SuspiciousLeadRules.ClassifyLocation(path) == expected);
        foreach (var path in new string?[] { null, "", @"C:\ProgramData\Vendor\x.exe", @"C:\Windows\System32\svchost.exe", @"C:\Users\Publicity\x.exe", @"C:\Users\Public\..\x.exe" }) Require(SuspiciousLeadRules.ClassifyLocation(path) == UnusualLocation.None);
        Require(SuspiciousLeadRules.ClassifyLocation(@"D:\Scratch\x.exe", @"D:\Scratch") == UnusualLocation.Temp);
        foreach (var raw in new[] { "", "relative.exe", @"%windir%\x.exe", @"C:\Program Files\x.exe -arg", "\"C:\\x.exe", "\"C:\\x.exe\"junk", @"C:\x.exe:ads", @"C:\x.exe,C:\y.exe", @"C:\Windows\System32\rundll32.exe C:\x.dll,Entry", @"C:\a\..\x.exe", "C:\\x.exe\nsecret" })
            Require(SuspiciousLeadRules.ParsePersistenceTarget(raw).Path is null);
        Require(SuspiciousLeadRules.ParsePersistenceTarget("\"C:\\Program Files\\x.exe\" -secret").Path == @"C:\Program Files\x.exe");
        Require(SuspiciousLeadRules.ParsePersistenceTarget(@"C:\x.exe -args").Path == @"C:\x.exe");
        Require(SuspiciousLeadRules.ParsePersistenceTarget(@"\\server\share\x.dll").Path == @"\\server\share\x.dll");
        ImageProvenance Image(string path, SignatureState signature) => new(path, InspectionStatus.Available, "", null, null, null, null, null, null, null, signature, null, null, null, at);
        foreach (var state in Enum.GetValues<SignatureState>()) Require((SuspiciousLeadRules.SignatureLead(Image(@"C:\x.exe", state)) is not null) == (state is SignatureState.Unsigned or SignatureState.NotTrusted));
        Require(SuspiciousLeadRules.SignatureLead(Image(@"C:\x.exe", SignatureState.Unsigned) with { Status = InspectionStatus.AccessDenied }) is null);
        var process = new ProcessSnapshot { Identity = id, Name = "svchost.exe", ExecutablePath = @"C:\Windows\System32\svchost.exe", ParentPid = 7 };
        var parent = new ProcessSnapshot { Identity = new(7, 50), Name = "wrong.exe" };
        var input = new LeadSweepInput(B(new ProcessLeadInput(process, parent, false, false, null)), B<ConnectionSnapshot>(), B<PersistenceTarget>(), true);
        Require(SuspiciousLeadRules.Evaluate(input).Items.Count == 0);
        Require(SuspiciousLeadRules.Evaluate(input with { Processes = B<ProcessLeadInput>() }).Items.Count == 0);
        var verified = input with { Processes = B(new ProcessLeadInput(process, parent, true, null, null)) };
        Require(SuspiciousLeadRules.Evaluate(verified).Items.Single().Category == LeadCategory.ParentMismatch);
        foreach (var p in new[] { parent with { Identity = new(7, 150) }, parent with { Identity = new(7, null) }, parent with { Name = "services.exe" } })
            Require(SuspiciousLeadRules.Evaluate(input with { Processes = B(new ProcessLeadInput(process, p, true, null, null)) }).Items.Count == 0);
        var unusual = process with { ExecutablePath = @"C:\Users\a\Downloads\svchost.exe" };
        var leads = SuspiciousLeadRules.Evaluate(input with { Processes = B(new ProcessLeadInput(unusual, null, false, false, Image(unusual.ExecutablePath, SignatureState.Unsigned))) });
        foreach (var category in new[] { LeadCategory.UnusualLocation, LeadCategory.NoVisibleWindow, LeadCategory.Masquerading, LeadCategory.Signature }) Require(leads.Items.Any(l => l.Category == category));
        Require(leads.Items.All(l => !string.IsNullOrWhiteSpace(l.BenignNote) && l.ProcessIdentity == id));
        Require(SuspiciousLeadRules.Evaluate(input with { Processes = B(new ProcessLeadInput(process with { ExecutablePath = @"C:\Apps\scvhost.exe" }, null, false, null, null)) }).Items.Single().Severity == LeadSeverity.High);
        SuspiciousLead[] Masquerades(string path, SignatureState state, string? signer = null, string? dev = null, bool verifiedParent = false) =>
            SuspiciousLeadRules.Evaluate(input with { Processes = B(new ProcessLeadInput(process with { ExecutablePath = path }, parent, verifiedParent, null,
                Image(path, state) with { SignerSubject = signer }, dev)) }, tempDirectory: @"C:\Temp").Items
                .Where(l => l.Category is LeadCategory.Masquerading or LeadCategory.ParentMismatch).ToArray();
        var exactTemp = Masquerades(@"C:\Temp\svchost.exe", SignatureState.Unsigned).Single();
        Require(exactTemp.Severity == LeadSeverity.High && exactTemp.Evidence.Contains("Name match: exact of svchost.exe") && exactTemp.Evidence.Contains("user-writable") && exactTemp.Evidence.Contains("Signer: unsigned") && exactTemp.Evidence.Contains("Parent: unverified"));
        foreach (var name in new[] { "svch0st", "lsasss", "scvhost", "svchоst", "ѕνсһοѕт", "svchst", "servicex", "ｗｉｎｌｏｇｏｎ" })
        {
            var near = Masquerades(@"C:\Temp\" + name + ".exe", SignatureState.Unsigned).Single();
            Require(near.Severity == LeadSeverity.High && near.Evidence.Contains("Name match: lookalike"));
        }
        foreach (var name in new[] { "lsass", "csrss", "smss", "wininit", "services" })
        {
            var badParent = Masquerades(@"C:\Windows\System32\" + name + ".exe", SignatureState.CatalogTrusted, "Microsoft Windows", verifiedParent: true).Single();
            Require(badParent.Category == LeadCategory.ParentMismatch && badParent.Severity == LeadSeverity.High && badParent.Evidence.Contains("Parent: verified unexpected"));
        }
        var copied = Masquerades(@"C:\Temp\svchost.exe", SignatureState.CatalogTrusted, "Microsoft Windows Publisher").Single();
        Require(copied.Severity == LeadSeverity.Medium && copied.Evidence.Contains("Signer: Microsoft"));
        var devLead = Masquerades(@"C:\src\project\bin\svchost.exe", SignatureState.Unsigned, dev: @"Build output directory: bin; Project marker .git in C:\src\project").Single();
        Require(devLead.Severity == LeadSeverity.Medium && devLead.Evidence.Contains("Dev context:") && devLead.Evidence.Contains(".git"));
        Require(Masquerades(@"C:\src\bin\svchost.exe", SignatureState.Unsigned).Single().Severity == LeadSeverity.Medium);
        foreach (var folder in new[] { "WinSxS", "servicing", "Temp" })
        {
            var installed = Masquerades(@"C:\Windows\" + folder + @"\component\svchost.exe", SignatureState.EmbeddedTrusted, "Microsoft Corporation").Single();
            Require(installed.Severity == LeadSeverity.Low && installed.Evidence.Contains("expected-but-unusual Microsoft location"));
        }
        Require(Masquerades(@"C:\Temp\svchost.exe", SignatureState.EmbeddedTrusted, "Other Publisher").Single().Severity == LeadSeverity.High);
        foreach (var name in new[] { "svchost", "svch0st" })
        {
            foreach (var state in new[] { SignatureState.EmbeddedTrusted, SignatureState.CatalogTrusted })
            {
                var developer = Masquerades(@"C:\src\project\bin\" + name + ".exe", state, "Other Publisher").Single();
                Require(developer.Severity == LeadSeverity.Medium && developer.Evidence.Contains("Signer: Other Publisher") && developer.Evidence.Contains("Dev context: Build output directory: bin") && developer.Evidence.Contains("locally trusted"));
                var outside = Masquerades(@"C:\Temp\" + name + ".exe", state, "Other Publisher").Single();
                Require(outside.Severity == LeadSeverity.High && outside.Evidence.Contains("Signer: Other Publisher"));
            }
            foreach (var (state, label) in new[] { (SignatureState.Unsigned, "Unsigned"), (SignatureState.NotTrusted, "Not trusted"), (SignatureState.NotChecked, "Not checked"), (SignatureState.Unreadable, "Unreadable") })
            {
                var developer = Masquerades(@"C:\src\project\bin\" + name + ".exe", state, "Other Publisher").Single();
                Require(developer.Severity == LeadSeverity.Medium && developer.Evidence.Contains("Dev context:") && developer.Evidence.Contains($"Signature state: {label}; treated as unsigned for tiering"));
                var outside = Masquerades(@"C:\Temp\" + name + ".exe", state, "Other Publisher").Single();
                Require(outside.Severity == LeadSeverity.High && outside.Evidence.Contains($"Signature state: {label}; treated as unsigned for tiering"));
            }
        }
        var unknownSigner = Masquerades(@"C:\Temp\svchost.exe", SignatureState.EmbeddedTrusted).Single();
        Require(unknownSigner.Severity == LeadSeverity.High && unknownSigner.Evidence.Contains("signer unknown; treated as not Microsoft-signed"));
        Require(Masquerades(@"C:\Temp\svchost.exe", SignatureState.NotTrusted, "Microsoft Corporation").Single().Severity == LeadSeverity.High);
        Require(Masquerades(@"C:\Windows\System32\svchost.exe", SignatureState.CatalogTrusted, "Microsoft Windows").Length == 0);
        Require(Masquerades(@"C:\Windows\explorer.exe", SignatureState.EmbeddedTrusted, "Microsoft Windows").Length == 0);
        Require(Masquerades(@"C:\Apps\ordinary.exe", SignatureState.Unsigned).Length == 0);
        Require(Masquerades(@"C:\Apps\lsm1.exe", SignatureState.Unsigned).Length == 0);
        Require(Masquerades(@"C:\Windows\SysWOW64\svchost.exe", SignatureState.Unsigned).Length == 0);
        Require(Masquerades(@"C:\Windows\System32\lsass.exe", SignatureState.CatalogTrusted, "Microsoft Windows").Length == 0);
        var expectedParentLead = SuspiciousLeadRules.Evaluate(input with { Processes = B(new ProcessLeadInput(process with { ExecutablePath = @"C:\Temp\svchost.exe" }, parent with { Name = "services.exe" }, true, null, null)) }).Items.Single(l => l.Category == LeadCategory.Masquerading);
        Require(expectedParentLead.Evidence.Contains("Parent: verified expected"));
        Require(SuspiciousLeadRules.DeveloperPathContext(@"C:\tools\Docker\svchost.exe") is not null);
        Require(SuspiciousLeadRules.DeveloperPathContext(@"C:\binary\svchost.exe") is null);
        var endpoint = new ConnectionSnapshot { Protocol = "TCP", LocalAddress = "0.0.0.0", LocalPort = 12, OwningPid = 999, State = "Listen" };
        Require(SuspiciousLeadRules.Evaluate(input with { Endpoints = B(endpoint) }).Items.Single().Category == LeadCategory.EndpointOwner);
        Require(SuspiciousLeadRules.Evaluate(input with { Endpoints = B(endpoint with { OwningPid = 42, OwnerIdentity = id }) }).Items.Count == 0);
        Require(SuspiciousLeadRules.Evaluate(input with { Endpoints = B(endpoint with { State = "Established" }) }).Items.Count == 0);
        Require(SuspiciousLeadRules.Evaluate(input with { Endpoints = B(endpoint with { Protocol = "UDP", State = null }) }).Items.Count == 1);
        var missing = new PersistenceTarget("entry", @"C:\missing.exe", InspectionStatus.Available, false, null);
        var persistence = input with { Persistence = B(missing, missing, missing with { Name = "unsigned", Exists = true, Provenance = Image(missing.ParsedPath!, SignatureState.Unsigned) }, missing with { Name = "unparseable", ParsedPath = null }) };
        Require(SuspiciousLeadRules.Evaluate(persistence).Items.Count == 3);
        Require(SuspiciousLeadRules.Evaluate(input with { Persistence = B(missing with { Status = InspectionStatus.AccessDenied }, missing with { IsReference = true }, missing with { Exists = null }) }).Items.Count == 0);
        Require(SuspiciousLeadRules.Evaluate(persistence, cap: 1).Truncated);
        Require(ProcessSourceErrors.AdministratorRequirement(ProcessSightingSource.EtwOwner, 4200, false) == "Requires administrator: ETW provider-owner enumeration is denied without elevation (Win32 4200)");
        Require(ProcessSourceErrors.AdministratorRequirement(ProcessSightingSource.EtwOwner, 4200, true) is null);
        Require(ProcessSourceErrors.AdministratorRequirement(ProcessSightingSource.EtwOwner, 4200, null) is null);
        Require(ProcessSourceErrors.AdministratorRequirement(ProcessSightingSource.Toolhelp, 4200, false) is null);
        Require(ProcessSourceErrors.AdministratorRequirement(ProcessSightingSource.EtwOwner, 5, false) is null);
        var unsigned = SuspiciousLeadRules.SignatureLead(Image(@"C:\apps\chrome.exe", SignatureState.Unsigned), id)!;
        Require(unsigned.Title == "Unsigned image");
        Require(SuspiciousLeadRules.SignatureLead(Image(@"C:\x.exe", SignatureState.NotTrusted))!.Title == "Signature not trusted");
        var otherIdentity = new ProcessIdentity(81, 200);
        var grouped = SuspiciousLeadRules.Aggregate(B(unsigned, unsigned with { ProcessIdentity = otherIdentity, Path = @"c:\APPS\chrome.exe" }), new HashSet<ProcessIdentity> { otherIdentity }, 1);
        Require(grouped.Items.Count == 1 && grouped.Items[0].ProcessIdentity == otherIdentity);
        Require(grouped.Items[0].Evidence.Contains("2 running instances") && grouped.Items[0].Evidence.Contains("remaining PIDs omitted"));
        Require(SuspiciousLeadRules.Aggregate(B(unsigned), new HashSet<ProcessIdentity>()).Items[0].ProcessIdentity is null);
        Require(SuspiciousLeadRules.Aggregate(B<SuspiciousLead>(), new HashSet<ProcessIdentity>()).Items.Count == 0);
        Require(SuspiciousLeadRules.Aggregate(new BoundedItems<SuspiciousLead>([unsigned], 1, true), new HashSet<ProcessIdentity>()).Truncated);
        var coverage = new SuspiciousLead(LeadCategory.Persistence, LeadSeverity.Info, "Persistence target unavailable", "Cannot parse", "entry A", "Indirect launcher");
        var coverageRows = SuspiciousLeadRules.Aggregate(B(coverage, coverage with { Evidence = "entry B" }), new HashSet<ProcessIdentity>());
        Require(coverageRows.Items.Count == 1 && coverageRows.Items[0].Evidence.Contains("2 configuration observations"));
        bool invalidDetailCap = false;
        try { SuspiciousLeadRules.Aggregate(B(unsigned), new HashSet<ProcessIdentity>(), 0); } catch (ArgumentOutOfRangeException) { invalidDetailCap = true; }
        Require(invalidDetailCap);
        Require(new BoundedItems<int>([1, 2], 1).Items.SequenceEqual([1]) && new BoundedItems<int>([], 0).Items.Count == 0);
        Require(new BoundedItems<int>([], 2, true).Truncated);
        Require(new AdsObservation("x", ":Zone.Identifier:$DATA", 10).Severity == LeadSeverity.Info);
        Require(new AdsObservation("x", ":payload:$DATA", 10).Severity == LeadSeverity.Low);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        bool cancelled = false;
        try { SuspiciousLeadRules.Evaluate(input, cancellationToken: cancel.Token); } catch (OperationCanceledException) { cancelled = true; }
        Require(cancelled);
    }
}

static class ActivityTraceTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UtcNow;
    private static void Require(bool condition) { if (!condition) throw new InvalidOperationException("Activity trace assertion failed"); }
    internal static void Run()
    {
        var tests = new (string Name, Action Run)[]
        {
            ("clamping, ring overflow and bounded intern pool", Bounds),
            ("PID reuse, seeded parents, tree and descendants", Processes),
            ("category, text, time and process filtering; 500k under 1s", Filtering),
            ("bounded summaries and Partial diagnostics", Summaries),
            ("RFC4180 CSV formula guard and streaming JSON metadata", Exports),
            ("live tail, exclusion, immutable final and empty/error cases", Lifecycle)
        };
        foreach (var (name, run) in tests) { run(); Console.WriteLine($"PASS Activity trace {name}"); }
    }
    private static TraceRecorderState State(int processCap = 16384, int stringCap = 100000) => new(new(ExcludeSelf: false), At, processCap: processCap, stringCap: stringCap);
    private static void Bounds()
    {
        Require(new TraceOptions().Duration == TimeSpan.FromMinutes(2) && new TraceOptions().EventCap == 200000);
        var low = new TraceOptions(TraceCategories.All, TimeSpan.MinValue, -1);
        var high = new TraceOptions((TraceCategories)127, TimeSpan.MaxValue, int.MaxValue);
        Require(low.Duration.TotalSeconds == 5 && low.EventCap == 10000 && low.ExcludeSelf);
        Require(high.Duration.TotalMinutes == 10 && high.EventCap == 500000 && high.Categories == TraceCategories.All);
        var ring = new TraceRingBuffer(2);
        for (int i = 1; i <= 4; i++) ring.Add(new(i, At.UtcTicks, 1, 0, -1, TraceOperation.FileRead));
        Require(ring.Dropped == 2 && ring.Snapshot().Select(e => e.Sequence).SequenceEqual(new long[] { 3, 4 }));
        Require(ring.Snapshot(1)[0].Sequence == 4 && ring.Snapshot(0).Length == 0);
        var pool = new TraceStringPool(1); var name = new string('x', 3);
        Require(ReferenceEquals(pool.Intern(name), pool.Intern(new string('x', 3))));
        var other = new string('y', 3); Require(ReferenceEquals(other, pool.Intern(other)) && pool.Count == 1 && pool.Capped && pool.Intern(null) is null);
    }
    private static void Processes()
    {
        var state = State();
        state.Seed([new(2, 1, 200, "child"), new(1, 0, 100, "parent"), new(3, 4, 300, "orphan"), new(4, 0, 400, "too new")]);
        state.Record(At, 2, 0, TraceOperation.FileRead);
        state.Record(At, 2, 0, TraceOperation.ProcessStop, exitCode: 7);
        state.Record(At, 2, 0, TraceOperation.FileRead);
        state.Record(At, 2, 0, TraceOperation.ProcessStart, processStart: new(2, 1, 500, "replacement"));
        state.RecordThread(2, true); state.RecordThread(2, false);
        state.Record(At, 2, 0, TraceOperation.FileWrite);
        var r = state.Complete(At);
        Require(r.Processes[0].ParentIndex == 1 && r.Processes[0].Seeded && r.Processes[0].StartUtc is null);
        Require(r.Processes[2].ParentIndex == -1 && r.Processes[0].ExitCode == 7);
        Require(r.Events[0].ProcessIndex == 0 && r.Events[2].ProcessIndex == 0 && r.Events[^1].ProcessIndex == 4);
        Require(r.Processes[4].ThreadsStarted == 1 && r.Processes[4].ThreadsStopped == 1);
        Require(TraceTree.Descendants(r.Processes, 1).SetEquals(new[] { 0, 1, 4 }));
        Require(TraceTree.Build(r.Processes).Single(row => row.Process.Index == 0).Depth == 1);
        var capped = State(processCap: 1); capped.Seed([new(1, 0, 1, "one"), new(1, 0, 2, "two")]);
        capped.Record(At, 1, 0, TraceOperation.FileRead);
        var c = capped.Complete(At); Require(c.ProcessTableTruncated && c.Status == TraceStatus.Partial && c.Events[0].ProcessIndex == -1);
    }
    private static void Filtering()
    {
        var s = State(); s.Seed([new(1, 0, 1, "Alpha"), new(2, 1, 2, "Beta")]);
        s.Record(At, 1, 1, TraceOperation.FileRead, "C:\\target", detail: "detail");
        s.Record(At.AddSeconds(1), 2, 1, TraceOperation.NetSend, "endpoint");
        var r = s.Complete(At);
        Require(new TraceFilter(Categories: TraceCategories.File, Text: "TARGET").Match(r.Events, r.Processes).SequenceEqual(new[] { 0 }));
        Require(new TraceFilter(Text: "ALPHA").Match(r.Events, r.Processes).Length == 1);
        Require(new TraceFilter(Text: "detail").Match(r.Events, r.Processes).Length == 1);
        Require(new TraceFilter(Text: "2", FromUtc: At.AddSeconds(1), ToUtc: At.AddSeconds(1)).Match(r.Events, r.Processes).SequenceEqual(new[] { 1 }));
        Require(new TraceFilter(0).Match(r.Events, r.Processes).Length == 2 && new TraceFilter(0, false).Match(r.Events, r.Processes).Length == 1);
        Require(new TraceFilter(Text: "absent").Match(r.Events, r.Processes).Length == 0);
        var events = Enumerable.Range(0, 500000).Select(i => new TraceEvent(i, At.UtcTicks, 1, 1, 0, TraceOperation.FileRead, "C:\\target")).ToArray();
        var filter = new TraceFilter(0, true, TraceCategories.File, "target", At, At);
        filter.Match(r.Events, r.Processes);
        var watch = System.Diagnostics.Stopwatch.StartNew(); var found = filter.Match(events, r.Processes); watch.Stop();
        Require(found.Length == 500000 && watch.Elapsed < TimeSpan.FromSeconds(1));
        Console.WriteLine($"Activity trace 500k filter: {watch.Elapsed.TotalMilliseconds:F1} ms");
    }
    private static void Summaries()
    {
        var s = State(); s.Seed([new(1, 0, 1, "one")]);
        for (int i = 0; i < 1025; i++) s.Record(At, 1, 1, TraceOperation.FileRead, "file" + i, 2);
        s.Record(At, 1, 1, TraceOperation.FileWrite, "file0", 3);
        s.Record(At, 1, 1, TraceOperation.NetSend, "endpoint", 4);
        s.Record(At, 1, 1, TraceOperation.NetReceive, "endpoint", 5);
        s.Record(At, 1, 1, TraceOperation.RegistryOpen, "key"); s.Record(At, 1, 1, TraceOperation.ImageLoad);
        var r = s.Complete(At); var summary = TraceProcessSummary.Build(r, 0);
        Require(summary.Files.DistinctRetained == 1024 && summary.Files.UntrackedObservations == 1 && summary.Capped && summary.Files.Top[0].Count == 2);
        Require(summary.BytesRead == 2050 && summary.BytesWritten == 3 && summary.BytesSent == 4 && summary.BytesReceived == 5 && summary.ImagesLoaded == 1);
        Require(summary.RegistryKeys.DistinctRetained == 1 && summary.Endpoints.DistinctRetained == 1 && r.CapsHit && r.Status == TraceStatus.Partial);
        Require(TraceProcessSummary.Build(r, 999).Files.DistinctRetained == 0);
        Require(State().Complete(At, eventsLost: 1).Status == TraceStatus.Partial);
        var unresolved = State(); unresolved.ReportDiagnostics(unresolvedNames: 5);
        var unresolvedResult = unresolved.Complete(At);
        Require(unresolvedResult.Status == TraceStatus.Completed && unresolvedResult.UnresolvedNames == 5 && unresolvedResult.Detail.Contains("5 events with unresolved names"));
        var strings = State(stringCap: 0); strings.Record(At, 1, 0, TraceOperation.FileRead, "x"); Require(strings.Complete(At).Status == TraceStatus.Partial);
        var overflow = State(); for (int i = 0; i < 200001; i++) overflow.Record(At, 1, 0, TraceOperation.FileRead);
        var o = overflow.Complete(At); Require(o.BufferDropped == 1 && o.Status == TraceStatus.Partial && o.Events[0].Sequence == 2);
    }
    private static void Exports()
    {
        var s = State();
        foreach (var text in new[] { "=formula", "+formula", "-formula", "@formula", "\tformula", "\rformula", "comma,quote\"\r\nline" }) s.Record(At, 1, 1, TraceOperation.FileRead, text, detail: "detail");
        var r = s.Complete(At, eventsLost: 2); using var csv = new StringWriter(); TraceExport.WriteCsv(csv, r);
        var value = csv.ToString();
        Require(value.Contains("'=formula") && value.Contains("'+formula") && value.Contains("'-formula") && value.Contains("'@formula") && value.Contains("'\tformula") && value.Contains("'\rformula"));
        Require(value.Contains("\"comma,quote\"\"\r\nline\"") && value.Contains("Sequence,TimeUtc,RelativeMs,Category,Operation,PID,TID,Process,ParentPID,Path,Bytes,Detail\r\n"));
        using var stream = new MemoryStream(); TraceExport.WriteJson(stream, r, new(Text: "=formula")); stream.Position = 0;
        using var json = System.Text.Json.JsonDocument.Parse(stream); var root = json.RootElement;
        Require(root.GetProperty("status").GetString() == "Partial" && root.GetProperty("eventsLost").GetInt64() == 2 && root.GetProperty("bufferDropped").GetInt64() == 0);
        Require(root.GetProperty("filter").GetProperty("text").GetString() == "=formula" && root.GetProperty("events").GetArrayLength() == 1 && root.TryGetProperty("limitations", out _));
        using var empty = new MemoryStream(); TraceExport.WriteJson(empty, State().Complete(At)); empty.Position = 0; using var parsed = System.Text.Json.JsonDocument.Parse(empty); Require(parsed.RootElement.GetProperty("events").GetArrayLength() == 0);
    }
    private static void Lifecycle()
    {
        var s = new TraceRecorderState(new(), At, 99);
        s.Record(At, 99, 0, TraceOperation.FileRead);
        for (int i = 0; i < 200; i++) s.Record(At, 1, 0, TraceOperation.FileRead);
        var live = s.Live(At.AddSeconds(1), 1000); Require(live.Tail.Count == 100 && live.Stored == 200 && live.ExcludedSelf == 1 && live.EventsSeen == 201);
        s.Record(At, 1, 0, (TraceOperation)999); s.ReportDiagnostics(2, 3, true);
        var r = s.Complete(At); Require(r.Malformed == 3 && r.UnresolvedNames == 3 && r.Status == TraceStatus.Partial);
        s.Record(At, 1, 0, TraceOperation.FileRead); Require(ReferenceEquals(r, s.Complete(At)) && r.Events.Count == 200);
        Require(State().Complete(At).Status == TraceStatus.Completed && State().Complete(At, TraceStatus.Cancelled, eventsLost: 1).Status == TraceStatus.Cancelled);
        Require(State().Complete(At, TraceStatus.Failed).Status == TraceStatus.Failed);
        bool threw = false; try { _ = new TraceRingBuffer(0); } catch (ArgumentOutOfRangeException) { threw = true; } Require(threw);
    }
}
