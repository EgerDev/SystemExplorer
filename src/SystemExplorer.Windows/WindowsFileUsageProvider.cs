using System.Diagnostics;
using SystemExplorer.Core;

namespace SystemExplorer.Windows;

public sealed class WindowsFileUsageProvider : IFileUsageProvider
{
    internal const int MaximumProcesses = 2048, MaximumMatches = 4096, MaximumFileUsers = 1024;
    private readonly IFileUsageNative restartManager;
    private readonly IInventoryNative system;
    private readonly IProcessInspectionProvider inspections;
    private readonly TimeSpan budget;
    private readonly Func<TimeSpan>? elapsedForTesting;
    private readonly int processCap, matchCap;

    public WindowsFileUsageProvider() : this(new WindowsProcessInspectionProvider()) { }
    public WindowsFileUsageProvider(IProcessInspectionProvider inspections)
        : this(new NativeSystemInformation(), new NativeSystemInformation(), inspections) { }
    internal WindowsFileUsageProvider(IFileUsageNative restartManager, IInventoryNative system, IProcessInspectionProvider inspections,
        TimeSpan? budget = null, Func<TimeSpan>? elapsedForTesting = null, int processCap = MaximumProcesses, int matchCap = MaximumMatches)
    {
        this.restartManager = restartManager;
        this.system = system;
        this.inspections = inspections;
        this.budget = budget ?? TimeSpan.FromSeconds(10);
        this.elapsedForTesting = elapsedForTesting;
        this.processCap = processCap;
        this.matchCap = matchCap;
    }

    internal static bool IsLocalDriveSyntax(string path) => path.Length >= 3 &&
        ((path[0] >= 'A' && path[0] <= 'Z') || (path[0] >= 'a' && path[0] <= 'z')) && path[1] == ':' && path[2] == '\\' &&
        path.IndexOf('\0') < 0;

    public async Task<FileUsersInspection> FindFileUsersAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var captured = DateTimeOffset.UtcNow;
        FileUsersInspection Empty(InspectionStatus status, string detail) => new(path, captured, status, detail, []);
        if (string.IsNullOrWhiteSpace(path) || !IsLocalDriveSyntax(path))
            return Empty(InspectionStatus.Unsupported, "Only fully qualified local drive paths (X:\\...) are accepted; UNC, device and relative paths are not queried.");
        if (!system.IsSupported) return Empty(InspectionStatus.Unsupported, "Requires Windows x64.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var workerToken = stop.Token;
        var work = Task.Factory.StartNew(() => FileUsers(path, captured, workerToken), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try { return await work.WaitAsync(budget, cancellationToken).ConfigureAwait(false); }
        catch (TimeoutException)
        {
            stop.Cancel();
            return Empty(InspectionStatus.TimedOut, "Restart Manager observation exceeded its timeout. The dedicated worker will end its session when the blocking native call returns; no users are inferred.");
        }
        catch (OperationCanceledException) { stop.Cancel(); throw; }
    }

    private FileUsersInspection FileUsers(string path, DateTimeOffset captured, CancellationToken token)
    {
        FileUsersInspection Result(InspectionStatus status, string detail, IReadOnlyList<FileUserObservation>? rows = null) => new(path, captured, status, detail, rows ?? []);
        FileUsersInspection Error(uint code, string operation) => Result(Win32Status(code), $"{operation} failed (Windows error {code}: {new System.ComponentModel.Win32Exception((int)code).Message}); no shutdown, restart, elevation or fallback attempted.");
        try
        {
            token.ThrowIfCancellationRequested();
            var drive = restartManager.DriveType(path[..3]);
            if (drive is not (2 or 3 or 5 or 6)) return Result(InspectionStatus.Unsupported, $"Drive type {drive} is remote, unknown, missing or unsupported; no Restart Manager session opened.");
            token.ThrowIfCancellationRequested();
            var error = restartManager.StartSession(out var session);
            if (error != 0) return Error(error, "RmStartSession");
            var result = Result(InspectionStatus.Failed, "Restart Manager did not produce a result.");
            uint endError = 0;
            try
            {
                token.ThrowIfCancellationRequested();
                error = restartManager.RegisterFile(session, path);
                if (error != 0) result = Error(error, "RmRegisterResources");
                else
                {
                    NativeSystemInformation.RestartManagerProcess[] users = [];
                    result = Result(InspectionStatus.Truncated, "RmGetList exceeded its four-attempt retry cap.");
                    for (var attempt = 0; attempt < 4; attempt++)
                    {
                        token.ThrowIfCancellationRequested();
                        uint count = (uint)users.Length;
                        error = restartManager.GetList(session, out var needed, ref count, users);
                        token.ThrowIfCancellationRequested();
                        if (error == 234)
                        {
                            if (needed > MaximumFileUsers) { result = Result(InspectionStatus.Truncated, $"RmGetList exceeded {MaximumFileUsers} entries; no incomplete buffer decoded."); break; }
                            users = new NativeSystemInformation.RestartManagerProcess[Math.Max(needed, (uint)Math.Min(MaximumFileUsers, Math.Max(1, users.Length * 2)))];
                            continue;
                        }
                        if (error != 0) { result = Error(error, "RmGetList"); break; }
                        if (count > users.Length) { result = Result(InspectionStatus.Failed, "RmGetList returned an invalid entry count."); break; }
                        var rows = new List<FileUserObservation>();
                        for (var i = 0; i < count; i++) rows.Add(Decode(users[i]));
                        result = Result(rows.Any(r => !r.Identity.IsStable) ? InspectionStatus.Partial : InspectionStatus.Available,
                            "Restart Manager registered-file users, not an exhaustive handle/open-file inventory. Start times are RM observations, not re-validated against a live process. Zero start times remain uncertain (null).", rows.ToArray());
                        break;
                    }
                }
            }
            finally { endError = restartManager.EndSession(session); }
            return endError == 0 ? result : result with { Status = result.Status == InspectionStatus.Available ? InspectionStatus.Partial : result.Status,
                Detail = result.Detail + $" RmEndSession failed (Windows error {endError})." };
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return Result(InspectionStatus.Unsupported, "Restart Manager native API unavailable."); }
        catch (Exception error) when (error is FormatException or ArgumentException or OverflowException)
        { return Result(InspectionStatus.Failed, "Invalid Restart Manager data: " + error.Message); }
    }

    internal static InspectionStatus Win32Status(uint error) => error switch
    { 5 => InspectionStatus.AccessDenied, 50 or 120 or 127 => InspectionStatus.Unsupported, _ => InspectionStatus.Failed };
    internal static FileUserObservation Decode(NativeSystemInformation.RestartManagerProcess user)
    {
        var creation = ((ulong)user.Process.CreationHigh << 32) | user.Process.CreationLow;
        if (user.Process.ProcessId is 0 or > int.MaxValue || creation > long.MaxValue) throw new FormatException("Unrepresentable RM identity.");
        var type = user.AppType switch
        {
            1 => FileUserAppType.MainWindow, 2 => FileUserAppType.OtherWindow, 3 => FileUserAppType.Service,
            4 => FileUserAppType.Explorer, 5 => FileUserAppType.Console, 1000 => FileUserAppType.Critical, _ => FileUserAppType.Unknown
        };
        return new(new((int)user.Process.ProcessId, creation == 0 ? null : (long)creation),
            string.IsNullOrEmpty(user.AppName) ? null : user.AppName, string.IsNullOrEmpty(user.ServiceName) ? null : user.ServiceName,
            type, user.AppType, user.Restartable);
    }

    public Task<ModuleUsersInspection> FindModuleUsersAsync(string query, CancellationToken cancellationToken) =>
        Task.Run(() => ModuleUsers(query, cancellationToken), cancellationToken);

    private async Task<ModuleUsersInspection> ModuleUsers(string query, CancellationToken token)
    {
        var captured = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        TimeSpan Elapsed() => elapsedForTesting?.Invoke() ?? clock.Elapsed;
        TimeSpan Remaining()
        {
            var remaining = budget - Elapsed();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
        var considered = 0; var searched = 0; var denied = 0; var exited = 0; var failed = 0; var skipped = 0; var matched = 0;
        var matches = new List<ModuleUserObservation>();
        var partial = false; var capped = false; var timedOut = false; var uncertainSkipped = 0; var legitimateSkipped = 0;
        ModuleUsersInspection Result(InspectionStatus status, string detail) => new(query, captured, status,
            detail + $" Considered={considered}, matched={matched}, searched={searched}, denied={denied}, exited={exited}, failed={failed}, not searched={skipped} (System/Idle={legitimateSkipped}, uncertain identity={uncertainSkipped}).",
            considered, matched, searched, denied, exited, failed, skipped, matches.ToArray());
        token.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query)) return Result(InspectionStatus.Failed, "A non-empty module name/path substring is required; no search performed.");
        if (!system.IsSupported) return Result(InspectionStatus.Unsupported, "Requires Windows x64.");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        var workerToken = stop.Token;
        try
        {
            if (Remaining() <= TimeSpan.Zero) return Result(InspectionStatus.TimedOut, "Budget expired before process enumeration; considered count unavailable.");
            var queryWork = Task.Run(() => system.Query(5, false, workerToken, () => Elapsed() >= budget), CancellationToken.None);
            NativeInformation raw;
            try { raw = await queryWork.WaitAsync(Remaining(), token).ConfigureAwait(false); }
            catch (TimeoutException) { stop.Cancel(); return Result(InspectionStatus.TimedOut, "Process enumeration exceeded wall-time bound; considered count unavailable, not zero processes established."); }
            if (raw.Status != InspectionStatus.Available) return Result(raw.Status, raw.Detail + " Process enumeration unavailable.");
            var inventory = NativeSystemInformation.ParseProcesses(raw.Bytes, token, () => Elapsed() >= budget);
            if (inventory.Status != InspectionStatus.Available) return Result(inventory.Status, inventory.Detail);
            considered = inventory.Processes.Count;
            var attempted = 0;
            foreach (var identity in inventory.Processes)
            {
                token.ThrowIfCancellationRequested();
                if (identity.Pid is 0 or 4) { skipped++; legitimateSkipped++; continue; }
                if (!identity.IsStable) { skipped++; uncertainSkipped++; partial = true; continue; }
                if (timedOut || Remaining() <= TimeSpan.Zero) { timedOut = true; skipped++; continue; }
                if (attempted >= processCap) { capped = true; skipped++; continue; }
                attempted++;
                ProcessInspection inspection;
                try
                {
                    inspection = await inspections.CaptureAsync(identity, ProcessInspectionKind.Modules, stop.Token)
                        .WaitAsync(Remaining(), token).ConfigureAwait(false);
                }
                catch (TimeoutException) { stop.Cancel(); timedOut = true; failed++; continue; }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { failed++; partial = true; continue; }
                catch (Exception error) when (error is not OperationCanceledException) { failed++; partial = true; continue; }
                if (inspection.Identity != identity) { exited++; partial = true; continue; }
                if (inspection.Status is InspectionStatus.Available or InspectionStatus.Partial or InspectionStatus.Truncated)
                {
                    searched++;
                    partial |= inspection.Status != InspectionStatus.Available;
                    var processMatched = false;
                    var seen = new HashSet<ModuleObservation>(ModuleMappingComparer.Instance);
                    foreach (var module in inspection.Modules)
                    {
                        token.ThrowIfCancellationRequested();
                        if (Remaining() <= TimeSpan.Zero) { timedOut = true; break; }
                        if (!module.Name.Contains(query, StringComparison.OrdinalIgnoreCase) && !module.Path.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                        if (!seen.Add(module)) continue;
                        if (matches.Count < matchCap)
                        {
                            matches.Add(new(identity, module.Name, module.Path, module.BaseAddress));
                            processMatched = true;
                        }
                        else capped = true;
                    }
                    if (processMatched) matched++;
                }
                else
                {
                    partial = true;
                    if (inspection.Status == InspectionStatus.AccessDenied) denied++;
                    else if (inspection.Status is InspectionStatus.Exited or InspectionStatus.IdentityMismatch) exited++;
                    else failed++;
                }
            }
            return Result(timedOut ? InspectionStatus.TimedOut : capped ? InspectionStatus.Truncated : partial ? InspectionStatus.Partial : InspectionStatus.Available,
                $"Sequential local module substring search using the process inspection provider; process cap={processCap}, match cap={matchCap}, wall-time budget={budget.TotalSeconds:g}s. Missing/denied processes are not evidence of absence.");
        }
        catch (OperationCanceledException) { stop.Cancel(); throw; }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        { return Result(InspectionStatus.Unsupported, "Required native API unavailable."); }
    }
}
