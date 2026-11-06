using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SystemExplorer.Core;
using SystemExplorer.Windows;

internal static class FileUsageSmoke
{
    private static void Assert(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static void W32(byte[] b, int o, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), value);
    private static void W64(byte[] b, int o, ulong value) => BinaryPrimitives.WriteUInt64LittleEndian(b.AsSpan(o), value);
    private static WindowsFileUsageProvider Provider(FakeRm rm, FakeSystem? system = null, FakeInspection? inspections = null,
        TimeSpan? budget = null, Func<TimeSpan>? elapsed = null, int processCap = 2048, int matchCap = 4096) =>
        new(rm, system ?? new FakeSystem([]), inspections ?? new FakeInspection(), budget, elapsed, processCap, matchCap);

    internal static async Task Run(Func<string, Func<Task>, Task> section)
    {
        await section("local path validation and drive refusal before RM session", async () =>
        {
            foreach (var path in new[] { "relative.txt", @"C:relative.txt", @"\rooted", @"\\server\share\file", @"\\?\C:\x", @"\\.\PhysicalDrive0", "", "   ", "C:/x" })
            {
                var rm = new FakeRm();
                var r = await Provider(rm).FindFileUsersAsync(path, default);
                Assert(r.Status == InspectionStatus.Unsupported && rm.Starts == 0 && rm.DriveQueries == 0, $"reject {path}");
            }
            foreach (uint drive in new uint[] { 0, 1, 4 })
            {
                var rm = new FakeRm { Drive = drive };
                Assert((await Provider(rm).FindFileUsersAsync(@"C:\x", default)).Status == InspectionStatus.Unsupported && rm.Starts == 0, "remote/unknown/no-root drive");
            }
            var local = new FakeRm();
            Assert((await Provider(local).FindFileUsersAsync(@"C:\x", default)).Status == InspectionStatus.Available && local.Starts == 1 && local.Ends == 1, "local drive accepted");
        });
        await section("RM fixed-buffer Unicode decoding, 668-byte layout and app types", () =>
        {
            Assert(Marshal.SizeOf<NativeSystemInformation.RestartManagerIdentity>() == 12 && Marshal.SizeOf<NativeSystemInformation.RestartManagerProcess>() == 668, "RM sizes");
            Assert(Marshal.OffsetOf<NativeSystemInformation.RestartManagerProcess>("AppType").ToInt32() == 652 && Marshal.OffsetOf<NativeSystemInformation.RestartManagerProcess>("Restartable").ToInt32() == 664, "RM offsets");
            var bytes = new byte[668]; W32(bytes, 0, 321); W64(bytes, 4, 0x123456789);
            Encoding.Unicode.GetBytes("App Ω").CopyTo(bytes, 12); Encoding.Unicode.GetBytes("svc").CopyTo(bytes, 524);
            W32(bytes, 652, 3); W32(bytes, 656, 123); W32(bytes, 660, 99); W32(bytes, 664, 1);
            var ptr = Marshal.AllocHGlobal(bytes.Length);
            try
            {
                Marshal.Copy(bytes, 0, ptr, bytes.Length);
                var native = Marshal.PtrToStructure<NativeSystemInformation.RestartManagerProcess>(ptr);
                var row = WindowsFileUsageProvider.Decode(native);
                Assert(row.Identity == new ProcessIdentity(321, 0x123456789) && row.AppName == "App Ω" && row.ServiceShortName == "svc" && row.AppType == FileUserAppType.Service && row.Restartable == true, "fixed RM buffer decode");
                foreach (var (raw, type) in new[] { (0, FileUserAppType.Unknown), (1, FileUserAppType.MainWindow), (2, FileUserAppType.OtherWindow), (3, FileUserAppType.Service), (4, FileUserAppType.Explorer), (5, FileUserAppType.Console), (1000, FileUserAppType.Critical), (999, FileUserAppType.Unknown) })
                {
                    native.AppType = raw;
                    var mapped = WindowsFileUsageProvider.Decode(native);
                    Assert(mapped.RawAppType == raw && mapped.AppType == type, "RM app type");
                }
                native.Process.CreationLow = native.Process.CreationHigh = 0; native.AppName = native.ServiceName = "";
                row = WindowsFileUsageProvider.Decode(native);
                Assert(row.Identity.CreationTimeFileTime == null && row.AppName == null && row.ServiceShortName == null, "uncertain RM start / empty strings");
            }
            finally { Marshal.FreeHGlobal(ptr); }
            return Task.CompletedTask;
        });
        await section("RM retry/entry caps, errors and guaranteed EndSession", async () =>
        {
            var rm = new FakeRm { MoreData = true };
            var r = await Provider(rm).FindFileUsersAsync(@"C:\x", default);
            Assert(r.Status == InspectionStatus.Truncated && rm.Lists == 4 && rm.Ends == 1, "retry cap");
            rm = new FakeRm { MoreData = true, Needed = 1025 };
            Assert((await Provider(rm).FindFileUsersAsync(@"C:\x", default)).Status == InspectionStatus.Truncated && rm.Lists == 1 && rm.Ends == 1, "entry cap");
            foreach (var stage in new[] { "register", "list" })
            {
                foreach (uint error in new uint[] { 5, 2, 50 })
                {
                    rm = new FakeRm { RegisterError = stage == "register" ? error : 0, ListError = stage == "list" ? error : 0 };
                    r = await Provider(rm).FindFileUsersAsync(@"C:\x", default);
                    Assert(r.Status == WindowsFileUsageProvider.Win32Status(error) && rm.Ends == 1 && r.Detail.Contains($"error {error}"), "error cleanup / mapping");
                }
            }
            rm = new FakeRm { StartError = 5 };
            Assert((await Provider(rm).FindFileUsersAsync(@"C:\x", default)).Status == InspectionStatus.AccessDenied && rm.Ends == 0, "failed start has no session to end");
            rm = new FakeRm { EndError = 5 };
            Assert((await Provider(rm).FindFileUsersAsync(@"C:\x", default)).Status == InspectionStatus.Partial && rm.Ends == 1, "end error reported");
            rm = new FakeRm { ThrowList = true };
            Assert((await Provider(rm).FindFileUsersAsync(@"C:\x", default)).Status == InspectionStatus.Failed && rm.Ends == 1, "exception cleanup");
            rm = new FakeRm { ReturnUser = true };
            r = await Provider(rm).FindFileUsersAsync(@"C:\x", default);
            Assert(r.Status == InspectionStatus.Partial && r.Users.Single().Identity.CreationTimeFileTime == null && rm.Ends == 1, "zero RM start time remains uncertain");
        });
        await section("RM prompt timeout/cancellation while worker retains cleanup ownership", async () =>
        {
            foreach (var cancel in new[] { false, true })
            {
                var rm = new FakeRm { Block = true };
                using var cancellation = new CancellationTokenSource();
                var clock = Stopwatch.StartNew();
                var task = Provider(rm, budget: TimeSpan.FromMilliseconds(150)).FindFileUsersAsync(@"C:\x", cancellation.Token);
                try
                {
                    await rm.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
                    if (cancel) cancellation.Cancel();
                    if (cancel)
                    {
                        var canceled = false;
                        try { await task; } catch (OperationCanceledException) { canceled = true; }
                        Assert(canceled, "caller cancellation immediate");
                    }
                    else Assert((await task).Status == InspectionStatus.TimedOut, "timeout status");
                    Assert(clock.Elapsed < TimeSpan.FromSeconds(2) && rm.Ends == 0, "prompt return without ending in-flight native session");
                }
                finally { rm.Release.Set(); }
                await rm.Ended.Task.WaitAsync(TimeSpan.FromSeconds(2));
                Assert(rm.Ends == 1, "background EndSession after unblock");
            }
        });
        await section("module identities, counts, substring matching and process/match/time caps", async () =>
        {
            ProcessIdentity[] ids = [new(0, null), new(4, null), new(1, 100), new(2, 200), new(3, 300), new(5, 500), new(6, null), new(7, 700)];
            var system = new FakeSystem(ids);
            var parsed = NativeSystemInformation.ParseProcesses(system.Bytes);
            Assert(parsed.Status == InspectionStatus.Available && parsed.Processes.SequenceEqual(ids), "process identity chain parser");
            var inspections = new FakeInspection();
            var provider = Provider(new(), system, inspections);
            var r = await provider.FindModuleUsersAsync("NtDlL.DlL", default);
            Assert(r.Status == InspectionStatus.Partial && r.ProcessesConsidered == 8 && r.ProcessesMatched == 2 && r.ProcessesSearched == 2 && r.ProcessesDenied == 1 && r.ProcessesExited == 1 && r.ProcessesFailed == 1 && r.ProcessesNotSearched == 3, "module category counts");
            Assert(r.Matches.Count == 2 && r.Matches.Any(m => m.ModuleName == "other.dll") && inspections.Calls == 5, "case-insensitive name or path substring, stable identities only");
            Assert(r.ProcessesSearched + r.ProcessesDenied + r.ProcessesExited + r.ProcessesFailed + r.ProcessesNotSearched == r.ProcessesConsidered, "count partition");
            r = await Provider(new(), system, new(), processCap: 1).FindModuleUsersAsync("ntdll", default);
            Assert(r.Status == InspectionStatus.Truncated && r.ProcessesSearched == 1 && r.ProcessesNotSearched == 7, "process cap");
            r = await Provider(new(), system, new(), matchCap: 1).FindModuleUsersAsync("ntdll", default);
            Assert(r.Status == InspectionStatus.Truncated && r.Matches.Count == 1 && r.ProcessesMatched == 2, "match cap retains process matched count");
            var elapsed = TimeSpan.Zero;
            inspections = new FakeInspection { AfterCapture = () => elapsed = TimeSpan.FromSeconds(11) };
            r = await Provider(new(), system, inspections, elapsed: () => elapsed).FindModuleUsersAsync("ntdll", default);
            Assert(r.Status == InspectionStatus.TimedOut && inspections.Calls == 1 && r.ProcessesSearched == 1 && r.ProcessesNotSearched == 7, "wall-time cap injected clock");
            var pending = new TaskCompletionSource<ProcessInspection>(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                r = await Provider(new(), new FakeSystem([new(1, 100), new(7, 700)]), new FakeInspection { Pending = pending }, budget: TimeSpan.FromMilliseconds(150))
                    .FindModuleUsersAsync("ntdll", default);
                Assert(r.Status == InspectionStatus.TimedOut && r.ProcessesFailed == 1 && r.ProcessesNotSearched == 1 && r.ProcessesConsidered == 2, "in-flight timeout is failed attempt; remainder not searched");
            }
            finally { pending.TrySetCanceled(); }
            system = new FakeSystem(ids);
            Assert((await Provider(new(), system).FindModuleUsersAsync("  ", default)).Status == InspectionStatus.Failed && system.Queries == 0, "empty query no search");
            r = await Provider(new(), new FakeSystem([new(0, null), new(4, null), new(1, 100)])).FindModuleUsersAsync("nomatch", default);
            Assert(r.Status == InspectionStatus.Available && r.Matches.Count == 0, "legitimate System/Idle skips allow available / empty match result");
        });
        await section("live Restart Manager file users, UNC refusal and ntdll module users", async () =>
        {
            using var own = Process.GetCurrentProcess();
            var identity = new ProcessIdentity(own.Id, own.StartTime.ToFileTimeUtc());
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), $"SystemExplorer-object-inventory-{Guid.NewGuid():N}.tmp");
            var provider = new WindowsFileUsageProvider();
            try
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite);
                var r = await provider.FindFileUsersAsync(path, default);
                Console.WriteLine($"  file users: {r.Status}; rows={r.Users.Count}; {r.Detail}");
                Assert(r.Status is InspectionStatus.Available or InspectionStatus.Partial or InspectionStatus.Truncated or InspectionStatus.AccessDenied or InspectionStatus.Unsupported or InspectionStatus.TimedOut, "honest RM live status");
                if (r.Status == InspectionStatus.Available) Assert(r.Users.Any(u => u.Identity == identity), "RM own PID and creation time");
                Assert((await provider.FindFileUsersAsync(@"\\server\share\file", default)).Status == InspectionStatus.Unsupported, "live UNC rejection");
            }
            finally { File.Delete(path); }
            var clock = Stopwatch.StartNew();
            var modules = await provider.FindModuleUsersAsync("ntdll.dll", default);
            Console.WriteLine($"  module users: {modules.Status}; rows={modules.Matches.Count}; elapsed={clock.Elapsed.TotalSeconds:F2}s; {modules.Detail}");
            Assert(modules.Status is InspectionStatus.Available or InspectionStatus.Partial or InspectionStatus.Truncated or InspectionStatus.AccessDenied or InspectionStatus.Unsupported or InspectionStatus.TimedOut, "honest modules live status");
            Assert(modules.ProcessesSearched + modules.ProcessesDenied + modules.ProcessesExited + modules.ProcessesFailed + modules.ProcessesNotSearched == modules.ProcessesConsidered, "live count partition");
            Assert(clock.Elapsed < TimeSpan.FromSeconds(12), "module wall-time bound with scheduling tolerance");
            if (modules.Status is InspectionStatus.Available or InspectionStatus.Partial) Assert(modules.Matches.Any(m => m.Identity == identity), "own identity in ntdll matches");
        });
    }

    private sealed class FakeRm : IFileUsageNative
    {
        internal uint Drive = 3, StartError, RegisterError, ListError, EndError, Needed = 1;
        internal int Starts, Ends, Lists, DriveQueries;
        internal bool MoreData, Block, ThrowList, ReturnUser;
        internal readonly ManualResetEventSlim Release = new();
        internal readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Ended = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public uint DriveType(string root) { DriveQueries++; return Drive; }
        public uint StartSession(out uint session) { Starts++; session = 42; return StartError; }
        public uint RegisterFile(uint session, string path) => RegisterError;
        public uint GetList(uint session, out uint needed, ref uint count, NativeSystemInformation.RestartManagerProcess[] processes)
        {
            Lists++;
            if (Block) { Entered.TrySetResult(); Release.Wait(); }
            if (ThrowList) throw new FormatException("fake malformed RM response");
            needed = Needed;
            if (MoreData || (ReturnUser && processes.Length == 0)) return 234;
            count = ReturnUser ? 1u : 0u;
            if (ReturnUser) processes[0] = new() { Process = new() { ProcessId = 123 }, AppName = "", ServiceName = "" };
            return ListError;
        }
        public uint EndSession(uint session) { Interlocked.Increment(ref Ends); Ended.TrySetResult(); return EndError; }
    }
    private sealed class FakeSystem : IInventoryNative
    {
        internal readonly byte[] Bytes;
        internal int Queries;
        internal FakeSystem(ProcessIdentity[] identities)
        {
            Bytes = new byte[Math.Max(1, identities.Length) * 256];
            for (var i = 0; i < identities.Length; i++)
            {
                var o = i * 256;
                W32(Bytes, o, i == identities.Length - 1 ? 0u : 256u);
                W64(Bytes, o + 32, (ulong)(identities[i].CreationTimeFileTime ?? 0)); W64(Bytes, o + 80, (ulong)identities[i].Pid);
            }
        }
        public bool IsSupported => true;
        public NativeInformation Query(int informationClass, bool objectTypes, CancellationToken token, Func<bool> expired)
        { Queries++; return new(InspectionStatus.Available, "fixed", Bytes); }
        public IInventoryProcessLease Open(ProcessIdentity identity) => throw new InvalidOperationException("Module search must reuse inspection provider, not open processes itself.");
    }
    private sealed class FakeInspection : IProcessInspectionProvider
    {
        internal int Calls;
        internal Action? AfterCapture;
        internal TaskCompletionSource<ProcessInspection>? Pending;
        public Task<ProcessInspection> CaptureAsync(ProcessIdentity identity, ProcessInspectionKind kind, CancellationToken token)
        {
            Assert(identity.IsStable && identity.Pid is not (0 or 4) && kind == ProcessInspectionKind.Modules, "module provider dispatch");
            Calls++;
            var status = identity.Pid switch { 2 => InspectionStatus.AccessDenied, 3 => InspectionStatus.IdentityMismatch, 5 => InspectionStatus.Failed, 7 => InspectionStatus.Partial, _ => InspectionStatus.Available };
            ModuleObservation[] modules = identity.Pid == 7 ? [new("other.dll", @"C:\NTDLL.DLL\other.dll", 0x2222, 4096)] : [new("ntdll.dll", @"C:\Windows\System32\ntdll.dll", 0x1111, 4096)];
            modules = [.. modules, .. modules.Select(m => m with { Path = m.Path.ToUpperInvariant() })];
            AfterCapture?.Invoke();
            if (Pending != null) return Pending.Task;
            return Task.FromResult(new ProcessInspection(identity, kind, DateTimeOffset.UtcNow, status, "fixed", modules, []));
        }
    }
}
