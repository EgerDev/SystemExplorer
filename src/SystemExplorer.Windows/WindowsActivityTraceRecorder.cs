using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using SystemExplorer.Core;
using TraceOptions = SystemExplorer.Core.TraceOptions;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SystemExplorer.ActivityTrace.Smoke")]

namespace SystemExplorer.Windows;

public sealed partial class WindowsActivityTraceRecorder : IDisposable, IAsyncDisposable
{
    public const string SessionPrefix = "SystemExplorer-Trace-";
    private static readonly Guid ProcessProvider = new("22fb2cd6-0e7b-422b-a0c7-2fad1fd0e716");
    private static readonly Guid FileProvider = new("edd08927-9cc4-4e65-b970-c2560fb5c289");
    private static readonly Guid RegistryProvider = new("70eb4f03-c1de-4f73-a051-33d13d5413bd");
    private static readonly Guid NetworkProvider = new("7dd42a49-5329-4832-8dfd-43d979153a88");
    private const string Limits = "Read-only private manifest ETW session. File bytes are requested I/O, not completed or physical disk bytes. Names opened before trace may be unavailable. No command lines or registry value names/data are read or retained.";
    private readonly TraceRecorderState state;
    private readonly TaskCompletionSource<TraceStatus> stop = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource timer = new();
    private readonly Dictionary<ulong, string> fileObjects = [], fileKeys = [], registryKeys = [];
    private readonly int nameMapCap;
    private TraceEventSession? session;
    private Task? processing;
    private CancellationTokenRegistration cancellation;
    private long cutoff = long.MaxValue;
    private bool exitHooked;
    public string? SessionName { get; private set; }
    public Task<TraceResult> Completion { get; private set; } = null!;
    public TraceStatus Status => Completion.IsCompletedSuccessfully ? Completion.Result.Status : TraceStatus.Running;

    private WindowsActivityTraceRecorder(TraceOptions options, int nameMapCap)
    { state = new(options, DateTimeOffset.UtcNow, Environment.ProcessId); this.nameMapCap = nameMapCap; }

    public static Task<WindowsActivityTraceRecorder> StartAsync(TraceOptions options, IReadOnlyList<TraceProcessSeed> seeds, CancellationToken token = default)
        => StartCoreAsync(options, seeds, token, 65_536);

    internal static Task<WindowsActivityTraceRecorder> StartCoreAsync(TraceOptions options, IReadOnlyList<TraceProcessSeed> seeds, CancellationToken token, int nameMapCap)
    {
        ArgumentNullException.ThrowIfNull(options); ArgumentNullException.ThrowIfNull(seeds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nameMapCap);
        var recorder = new WindowsActivityTraceRecorder(options, nameMapCap);
        recorder.Initialize(seeds, token);
        return Task.FromResult(recorder);
    }

    private void Initialize(IReadOnlyList<TraceProcessSeed> seeds, CancellationToken token)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
        { Completion = Task.FromResult(state.Complete(DateTimeOffset.UtcNow, TraceStatus.RequiresAdministrator, "Administrator token required. No session attempted.")); return; }
        if (token.IsCancellationRequested)
        { Completion = Task.FromResult(state.Complete(DateTimeOffset.UtcNow, TraceStatus.Cancelled, "Cancelled before session creation.")); return; }
        try
        {
            CleanupStaleSessions();
            state.Seed(seeds);
            SessionName = $"{SessionPrefix}{Environment.ProcessId}-{Guid.NewGuid():N}";
            session = new TraceEventSession(SessionName) { StopOnDispose = true, BufferSizeMB = 32 };
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit; exitHooked = true;
            session.Source.Dynamic.All += OnEvent;
            var categories = state.Options.Categories;
            ulong processKeywords = 0;
            if ((categories & TraceCategories.Process) != 0) processKeywords |= 0x10;
            if ((categories & TraceCategories.Thread) != 0) processKeywords |= 0x20;
            if ((categories & TraceCategories.Image) != 0) processKeywords |= 0x40;
            if (processKeywords != 0) session.EnableProvider(ProcessProvider, TraceEventLevel.Informational, processKeywords);
            if ((categories & TraceCategories.File) != 0) session.EnableProvider(FileProvider, TraceEventLevel.Informational, 0xfb0);
            if ((categories & TraceCategories.Registry) != 0) session.EnableProvider(RegistryProvider, TraceEventLevel.Informational, 0x7301);
            if ((categories & TraceCategories.Network) != 0) session.EnableProvider(NetworkProvider, TraceEventLevel.Informational, 0x30);
            // Materialize the source even for None, so StopAsync has a session to drain.
            var source = session.Source;
            processing = Task.Factory.StartNew(() => source.Process(), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            cancellation = token.Register(() => stop.TrySetResult(TraceStatus.Cancelled));
            Completion = FinishAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            if (exitHooked) { AppDomain.CurrentDomain.ProcessExit -= OnProcessExit; exitHooked = false; }
            string cleanupDetail = "";
            try { session?.Dispose(); }
            catch (Exception cleanup) when (cleanup is not OutOfMemoryException) { cleanupDetail = $" Session cleanup failed ({cleanup.GetType().Name})."; }
            session = null;
            Completion = Task.FromResult(state.Complete(DateTimeOffset.UtcNow, cleanupDetail.Length == 0 ? TraceStatus.SessionUnavailable : TraceStatus.Failed,
                $"Private ETW session unavailable ({ex.GetType().Name}). " + Limits + cleanupDetail));
        }
    }

    public TraceLiveSnapshot Live(int tail = 100) => state.Live(DateTimeOffset.UtcNow, tail);
    public Task<TraceResult> StopAsync() { stop.TrySetResult(TraceStatus.Completed); return Completion; }
    public Task<TraceResult> CancelAsync() { stop.TrySetResult(TraceStatus.Cancelled); return Completion; }

    private async Task<TraceResult> FinishAsync()
    {
        var delay = Task.Delay(state.Options.Duration, timer.Token);
        var first = await Task.WhenAny(stop.Task, delay, processing!).ConfigureAwait(false);
        TraceStatus status = first == processing ? TraceStatus.Failed : stop.Task.IsCompleted ? await stop.Task.ConfigureAwait(false) : TraceStatus.Completed;
        timer.Cancel();
        Interlocked.Exchange(ref cutoff, DateTimeOffset.UtcNow.UtcTicks);
        long lost = 0; string detail = Limits;
        try
        {
            try { lost = StopOwnedSession(SessionName!); session!.StopOnDispose = false; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                status = TraceStatus.Failed; detail += " Final EventsLost unavailable; native STOP failed (" + ex.GetType().Name + ").";
                session!.Stop();
            }
            try { await processing!.ConfigureAwait(false); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { status = TraceStatus.Failed; detail += " ETW processing failed (" + ex.GetType().Name + ")."; }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException) { status = TraceStatus.Failed; detail += " ETW cleanup failed (" + ex.GetType().Name + ")."; }
        finally
        {
            if (exitHooked) { AppDomain.CurrentDomain.ProcessExit -= OnProcessExit; exitHooked = false; }
            cancellation.Dispose();
            try { session?.Dispose(); } catch (Exception ex) when (ex is not OutOfMemoryException) { status = TraceStatus.Failed; detail += " Session disposal failed."; }
            session = null;
        }
        return state.Complete(DateTimeOffset.UtcNow, status, detail, lost);
    }

    private void OnProcessExit(object? sender, EventArgs args) => StopAsync().GetAwaiter().GetResult();
    public void Dispose() { StopAsync().GetAwaiter().GetResult(); timer.Dispose(); }
    public async ValueTask DisposeAsync() { await StopAsync().ConfigureAwait(false); timer.Dispose(); }

    private static bool TryOwner(string name, out int pid)
    {
        pid = 0;
        if (!name.StartsWith(SessionPrefix, StringComparison.Ordinal)) return false;
        var suffix = name.AsSpan(SessionPrefix.Length); int separator = suffix.IndexOf('-');
        return separator > 0 && int.TryParse(suffix[..separator], NumberStyles.None, CultureInfo.InvariantCulture, out pid) && pid > 0
            && Guid.TryParseExact(suffix[(separator + 1)..], "N", out _);
    }

    public static int CleanupStaleSessions()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator)) return 0;
        int stopped = 0;
        foreach (var name in TraceEventSession.GetActiveSessionNames())
        {
            if (!TryOwner(name, out int pid) || pid == Environment.ProcessId) continue;
            bool dead;
            try { using var process = Process.GetProcessById(pid); dead = process.HasExited; }
            catch (ArgumentException) { dead = true; }
            catch (Win32Exception) { continue; }
            catch (InvalidOperationException) { continue; }
            if (!dead) continue;
            try { StopOwnedSession(name); stopped++; }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 4201) { }
        }
        return stopped;
    }

    [DllImport("advapi32.dll", EntryPoint = "ControlTraceW", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern uint ControlTrace(ulong handle, string name, IntPtr properties, uint code);

    private static long StopOwnedSession(string name)
    {
        if (!TryOwner(name, out _) || name.Length >= 1024) throw new ArgumentException("Not an owned activity session.", nameof(name));
        int structureSize = Marshal.SizeOf<WindowsIoAttributionCapture.EventTraceProperties>();
        int size = structureSize + 4096 + 256;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.StructureToPtr(new WindowsIoAttributionCapture.EventTraceProperties
            {
                Wnode = new WindowsIoAttributionCapture.WnodeHeader { BufferSize = (uint)size, Flags = 0x20000 },
                LoggerNameOffset = (uint)structureSize, LogFileNameOffset = (uint)(structureSize + 2048)
            }, buffer, false);
            var encoded = Encoding.Unicode.GetBytes(name + '\0'); Marshal.Copy(encoded, 0, buffer + structureSize, encoded.Length);
            uint error = ControlTrace(0, name, buffer, 1);
            if (error != 0) throw new Win32Exception((int)error);
            return Marshal.PtrToStructure<WindowsIoAttributionCapture.EventTraceProperties>(buffer).EventsLost;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
}
