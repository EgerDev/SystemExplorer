using System.Reflection;
using SystemExplorer.Core;
using SystemExplorer.App;

var tests = new (string Name, Action Run)[]
{
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

sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset current;

    public ManualTimeProvider(DateTimeOffset current) => this.current = current;
    public override DateTimeOffset GetUtcNow() => current;
    public void Advance(TimeSpan duration) => current += duration;
}
