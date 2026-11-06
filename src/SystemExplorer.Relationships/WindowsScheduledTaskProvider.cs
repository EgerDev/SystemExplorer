using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.CSharp.RuntimeBinder;
using SystemExplorer.Core;

[assembly: InternalsVisibleTo("SystemExplorer.ScheduledTasks.Smoke")]

namespace SystemExplorer.Relationships;

public sealed class WindowsScheduledTaskProvider : IScheduledTaskProvider
{
    internal const int MaximumTasks = 1000, MaximumFolders = 256, MaximumDepth = 16;
    internal const int MaximumMembers = 32, MaximumCharacters = 4096;
    private readonly IScheduledTaskBackend backend;
    private readonly SemaphoreSlim gate = new(1, 1);

    public WindowsScheduledTaskProvider() : this(new WindowsScheduledTaskBackend()) { }
    internal WindowsScheduledTaskProvider(IScheduledTaskBackend backend) =>
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));

    public async Task<ScheduledTaskInspection> CaptureAsync(CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var result = await Task.Run(() => new Capture(backend, cancellationToken).Run(), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        finally { gate.Release(); }
    }

    private sealed class Capture(IScheduledTaskBackend backend, CancellationToken token)
    {
        private readonly DateTimeOffset capturedAt = DateTimeOffset.UtcNow;
        private readonly long started = backend.GetTimestamp();
        private readonly List<ScheduledTaskObservation> tasks = [];
        private int folders, denied, failures;
        private bool truncated, rootDenied;
        private const string Limitations = "Local current-token inventory; hidden tasks requested, but inaccessible tasks may be omitted by Windows. Not all scheduled tasks are logon/startup items. Values are unexpanded observations, not target execution or ownership evidence. No arguments, dates, XML, security descriptors or credentials collected. The three-second budget is cooperative; blocking COM calls cannot be interrupted.";

        internal ScheduledTaskInspection Run()
        {
            token.ThrowIfCancellationRequested();
            if (!OperatingSystem.IsWindows()) return Result(InspectionStatus.Unsupported);
            try
            {
                Check();
                using var service = backend.CreateService();
                Check();
                service.Connect(); // No server, user, domain or password: local current token only.
                Check();
                using var root = Open(service, SchedulerChild.Root);
                Check();
                folders = 1;
                Visit(root, 0);
                Check();
                return Result(rootDenied && tasks.Count == 0 ? InspectionStatus.AccessDenied :
                    truncated ? InspectionStatus.Truncated : denied + failures > 0 ? InspectionStatus.Partial : InspectionStatus.Available);
            }
            catch (BudgetExpiredException) { return Result(InspectionStatus.TimedOut); }
            catch (LimitReachedException) { truncated = true; return Result(InspectionStatus.Truncated); }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                token.ThrowIfCancellationRequested();
                Record(ex);
                return Result(backend.GetElapsedTime(started) >= TimeSpan.FromSeconds(3) ? InspectionStatus.TimedOut : ErrorStatus(ex), FailureDetail(ex));
            }
        }

        private ScheduledTaskInspection Result(InspectionStatus status, string failure = "") => new(capturedAt, status,
            $"{Limitations} Attempted folders={folders}; observed task rows={tasks.Count}; denied reads={denied}; failed reads={failures}; bounded/truncated={truncated}. {failure}".TrimEnd(), tasks.ToArray());

        private void Check()
        {
            token.ThrowIfCancellationRequested();
            if (backend.GetElapsedTime(started) >= TimeSpan.FromSeconds(3)) throw new BudgetExpiredException();
        }

        private ISchedulerNode Open(ISchedulerNode node, SchedulerChild child, int index = 0)
        {
            Check();
            // The caller must own the result with using before checking cancellation after the COM call.
            return node.Open(child, index);
        }

        private object Read(ISchedulerNode node, SchedulerValue value)
        {
            Check();
            object result = node.Read(value);
            Check();
            return result;
        }

        private int Count(ISchedulerNode node)
        {
            int count = (int)Read(node, SchedulerValue.Count);
            if (count < 0) throw new InvalidDataException("Negative collection count.");
            return count;
        }

        private void Visit(ISchedulerNode folder, int depth)
        {
            Check();
            try
            {
                using var entries = Open(folder, SchedulerChild.Tasks, 1); // TASK_ENUM_HIDDEN
                Check();
                int count = Count(entries);
                for (int index = 1; index <= count; index++)
                {
                    Check();
                    if (tasks.Count >= MaximumTasks) throw new LimitReachedException();
                    try
                    {
                        using var task = Open(entries, SchedulerChild.Item, index);
                        Check();
                        tasks.Add(ReadTask(task));
                    }
                    catch (Exception ex) when (IsReadFailure(ex))
                    {
                        Check();
                        Record(ex);
                        tasks.Add(new("", "", "Unavailable", null, "Unavailable", "Unavailable", ErrorStatus(ex), FailureDetail(ex)));
                    }
                }
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                Check();
                Record(ex);
                if (depth == 0 && ErrorStatus(ex) == InspectionStatus.AccessDenied) rootDenied = true;
            }
            try
            {
                using var children = Open(folder, SchedulerChild.Folders, 0);
                Check();
                int count = Count(children);
                if (depth >= MaximumDepth && count > 0) { truncated = true; return; }
                for (int index = 1; index <= count; index++)
                {
                    Check();
                    if (folders >= MaximumFolders || tasks.Count >= MaximumTasks) throw new LimitReachedException();
                    folders++;
                    try
                    {
                        using var child = Open(children, SchedulerChild.Item, index);
                        Check();
                        Visit(child, depth + 1);
                    }
                    catch (Exception ex) when (IsReadFailure(ex)) { Check(); Record(ex); }
                }
            }
            catch (Exception ex) when (IsReadFailure(ex))
            {
                Check();
                Record(ex);
                if (depth == 0 && ErrorStatus(ex) == InspectionStatus.AccessDenied) rootDenied = true;
            }
        }

        private ScheduledTaskObservation ReadTask(ISchedulerNode task)
        {
            var issues = new List<string>();
            bool limited = false, accessDenied = false;
            void Failed(string field, Exception ex)
            {
                Check();
                Record(ex);
                accessDenied |= ErrorStatus(ex) == InspectionStatus.AccessDenied;
                issues.Add($"{field}: {FailureDetail(ex)}");
            }
            T Field<T>(string label, Func<T> read, T fallback)
            {
                try { return read(); }
                catch (Exception ex) when (IsReadFailure(ex)) { Failed(label, ex); return fallback; }
            }
            string Bounded(string value)
            {
                if (value.Length <= MaximumCharacters) return value;
                limited = true;
                // Avoid splitting a UTF-16 surrogate pair in a displayed field.
                int length = char.IsHighSurrogate(value[MaximumCharacters - 1]) ? MaximumCharacters - 1 : MaximumCharacters;
                return value[..length];
            }
            string Text(ISchedulerNode node, SchedulerValue value) =>
                Bounded(Read(node, value) as string ?? throw new InvalidDataException("Missing string field."));
            string Members(ISchedulerNode definition, bool actions)
            {
                using var collection = Open(definition, actions ? SchedulerChild.Actions : SchedulerChild.Triggers);
                Check();
                int count = Count(collection);
                if (count > MaximumMembers) limited = true;
                var labels = new List<string>();
                for (int index = 1; index <= Math.Min(count, MaximumMembers); index++)
                {
                    Check();
                    try
                    {
                        using var member = Open(collection, SchedulerChild.Item, index);
                        Check();
                        int type = (int)Read(member, SchedulerValue.Type);
                        labels.Add(actions
                            ? type == 0 ? $"Execute: {Text(member, SchedulerValue.Path)}" : ActionLabel(type)
                            : TriggerLabel(type));
                    }
                    catch (Exception ex) when (IsReadFailure(ex))
                    {
                        Failed(actions ? "action" : "trigger", ex);
                        labels.Add("Unavailable");
                    }
                }
                return Bounded(string.Join("; ", labels));
            }

            string path = Field("path", () => Text(task, SchedulerValue.Path), "");
            string name = Field("name", () => Text(task, SchedulerValue.Name), "");
            string state = Field("state", () => StateLabel((int)Read(task, SchedulerValue.State)), "Unavailable");
            bool? enabled = Field<bool?>("enabled", () => (bool)Read(task, SchedulerValue.Enabled), null);
            string actions = "Unavailable", triggers = "Unavailable";
            try
            {
                using var definition = Open(task, SchedulerChild.Definition);
                Check();
                actions = Field("actions", () => Members(definition, true), "Unavailable");
                triggers = Field("triggers", () => Members(definition, false), "Unavailable");
            }
            catch (Exception ex) when (IsReadFailure(ex)) { Failed("definition", ex); }
            Check();
            string detail = issues.Count == 0 ? "Read-only observed task metadata; executable paths may be sensitive local metadata." : string.Join(" ", issues);
            if (limited) detail += " Fields or action/trigger collections truncated to bounds.";
            detail = Bounded(detail);
            truncated |= limited;
            var status = accessDenied ? InspectionStatus.AccessDenied : issues.Count > 0 ? InspectionStatus.Partial :
                limited ? InspectionStatus.Truncated : InspectionStatus.Available;
            return new(path, name, state, enabled, actions, triggers, status, detail);
        }

        private void Record(Exception ex)
        {
            if (ErrorStatus(ex) == InspectionStatus.AccessDenied) denied++;
            else failures++;
        }
    }

    private sealed class BudgetExpiredException : Exception;
    private sealed class LimitReachedException : Exception;

    private static bool IsReadFailure(Exception ex) => ex is COMException or UnauthorizedAccessException or
        NotSupportedException or InvalidCastException or InvalidDataException or RuntimeBinderException;
    private static InspectionStatus ErrorStatus(Exception ex) => ex switch
    {
        UnauthorizedAccessException => InspectionStatus.AccessDenied,
        COMException com when com.HResult == unchecked((int)0x80070005) => InspectionStatus.AccessDenied,
        NotSupportedException or RuntimeBinderException => InspectionStatus.Unsupported,
        COMException com when com.HResult is unchecked((int)0x80040154) or unchecked((int)0x80004001) => InspectionStatus.Unsupported,
        _ => InspectionStatus.Failed
    };
    private static string FailureDetail(Exception ex) => $"{ErrorStatus(ex)} (HRESULT 0x{ex.HResult:X8}); observation unavailable.";
    private static string StateLabel(int state) => state switch
    {
        0 => "Unknown", 1 => "Disabled", 2 => "Queued", 3 => "Ready", 4 => "Running", _ => $"Unknown ({state})"
    };
    private static string ActionLabel(int type) => type switch
    {
        5 => "COM handler (not activated)", 6 => "Send email (type only)", 7 => "Show message (type only)", _ => $"Unknown action ({type})"
    };
    private static string TriggerLabel(int type) => type switch
    {
        0 => "Event", 1 => "Time", 2 => "Daily", 3 => "Weekly", 4 => "Monthly", 5 => "Monthly day of week",
        6 => "Idle", 7 => "Registration", 8 => "Boot", 9 => "Logon", 11 => "Session state change", 12 => "Custom (12)",
        _ => $"Unknown trigger ({type})"
    };
}

internal enum SchedulerChild { Root, Tasks, Folders, Item, Definition, Actions, Triggers }
internal enum SchedulerValue { Count, Path, Name, State, Enabled, Type }
internal interface ISchedulerNode : IDisposable
{
    void Connect();
    ISchedulerNode Open(SchedulerChild child, int index);
    object Read(SchedulerValue value);
}
internal interface IScheduledTaskBackend
{
    ISchedulerNode CreateService();
    long GetTimestamp();
    TimeSpan GetElapsedTime(long started);
}

internal sealed class WindowsScheduledTaskBackend : IScheduledTaskBackend
{
    public long GetTimestamp() => Stopwatch.GetTimestamp();
    public TimeSpan GetElapsedTime(long started) => Stopwatch.GetElapsedTime(started);
    public ISchedulerNode CreateService()
    {
        var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new PlatformNotSupportedException("Task Scheduler COM is unavailable.");
        return new ComNode(Activator.CreateInstance(type) ?? throw new PlatformNotSupportedException("Task Scheduler COM activation failed."));
    }

    private sealed class ComNode(object instance) : ISchedulerNode
    {
        private object? instance = instance;
        private dynamic Value => instance ?? throw new ObjectDisposedException(nameof(ComNode));
        public void Connect() => Value.Connect();
        public ISchedulerNode Open(SchedulerChild child, int index)
        {
            object result = child switch
            {
                SchedulerChild.Root => Value.GetFolder("\\"),
                SchedulerChild.Tasks => Value.GetTasks(index),
                SchedulerChild.Folders => Value.GetFolders(index),
                SchedulerChild.Item => Value[index],
                SchedulerChild.Definition => Value.Definition,
                SchedulerChild.Actions => Value.Actions,
                SchedulerChild.Triggers => Value.Triggers,
                _ => throw new ArgumentOutOfRangeException(nameof(child))
            };
            return new ComNode(result);
        }
        public object Read(SchedulerValue value) => value switch
        {
            SchedulerValue.Count => (int)Value.Count,
            SchedulerValue.Path => (string)Value.Path,
            SchedulerValue.Name => (string)Value.Name,
            SchedulerValue.State => (int)Value.State,
            SchedulerValue.Enabled => (bool)Value.Enabled,
            SchedulerValue.Type => (int)Value.Type,
            _ => throw new ArgumentOutOfRangeException(nameof(value))
        };
        public void Dispose()
        {
            object? owned = Interlocked.Exchange(ref instance, null);
            if (owned is not null && Marshal.IsComObject(owned)) Marshal.ReleaseComObject(owned);
        }
    }
}
