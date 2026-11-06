using System.Runtime.InteropServices;
using SystemExplorer.Core;
using SystemExplorer.Relationships;

int checks = 0;
void Require(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
}
void Clean(FakeBackend backend) => Require(backend.Acquired == backend.Disposed && backend.ActiveServices == 0, "A scheduler node leaked or was released twice.");
async Task<ScheduledTaskInspection> Inspect(FakeBackend backend)
{
    var result = await new WindowsScheduledTaskProvider(backend).CaptureAsync(default);
    Clean(backend);
    Require(result.Detail.Length > 0 && result.Detail.Length <= 4096 && result.Detail.Contains("Not all scheduled tasks"), "Inventory limitations missing.");
    return result;
}
async Task Canceled(Func<Task> action)
{
    try { await action(); }
    catch (OperationCanceledException) { checks++; return; }
    throw new InvalidOperationException("Cancellation was ignored.");
}
COMException Denied() => new("Synthetic denied", unchecked((int)0x80070005));
COMException Failure() => new("Synthetic failure", unchecked((int)0x80004005));

var action = Specs.Member("action", 0);
action.Values[SchedulerValue.Path] = "%FICTIONAL%\\工具.exe";
var handler = Specs.Member("handler", 5); // No Path getter: reading one must fail this test.
var unknownAction = Specs.Member("unknown-action", 99);
var task = Specs.Task();
var definition = task.Children[SchedulerChild.Definition](0);
definition.Children[SchedulerChild.Actions] = _ => Specs.Collection("actions", [action, handler, unknownAction]);
definition.Children[SchedulerChild.Triggers] = _ => Specs.Collection("triggers", [Specs.Member("trigger", 9), Specs.Member("trigger", 98)]);
var backend = new FakeBackend(Specs.Folder([task], [Specs.Folder([Specs.Task("nested")])]));
var result = await Inspect(backend);
Require(result.Status == InspectionStatus.Available && result.Tasks.Count == 2, "Recursive inventory failed.");
Require(result.Tasks[0].Path == "\\fictional" && result.Tasks[0].Name == "fictional" && result.Tasks[0].State == "Ready" && result.Tasks[0].Enabled == true, "Base task metadata was changed.");
Require(result.Tasks[0].Actions == "Execute: %FICTIONAL%\\工具.exe; COM handler (not activated); Unknown action (99)", "Action path/type labels changed or expanded.");
Require(result.Tasks[0].Triggers == "Logon; Unknown trigger (98)", "Trigger types were not preserved.");
Require(backend.Operations.Where(x => x.StartsWith("Open:folder.Tasks:")).All(x => x.EndsWith(":1")) &&
    backend.Operations.Where(x => x.StartsWith("Open:folder.Folders:")).All(x => x.EndsWith(":0")), "Hidden-task/folder flags are wrong.");
Require(backend.Operations.Count(x => x == "Connect:service") == 1 && !backend.Operations.Contains("Read:handler.Path"), "Connection/handler scope changed.");

result = await Inspect(new FakeBackend(Specs.Folder()));
Require(result.Status == InspectionStatus.Available && result.Tasks.Count == 0, "Legitimate empty inventory rejected.");
foreach (string stage in new[] { "connect", "root", "tasks" })
{
    var root = Specs.Folder();
    var fake = new FakeBackend(root);
    if (stage == "connect") fake.Service.ConnectError = Denied();
    if (stage == "root") fake.Service.Children[SchedulerChild.Root] = _ => throw Denied();
    if (stage == "tasks") root.Children[SchedulerChild.Tasks] = _ => throw Denied();
    result = await Inspect(fake);
    Require(result.Status == InspectionStatus.AccessDenied && result.Tasks.Count == 0 && result.Detail.Contains("denied reads=1"), "Root denial became empty success.");
}
var rootFailure = new FakeBackend(Specs.Folder());
rootFailure.Service.ConnectError = Failure();
Require((await Inspect(rootFailure)).Status == InspectionStatus.Failed, "Connection failure was hidden.");
var unsupported = new FakeBackend(Specs.Folder()) { CreateError = new COMException("Synthetic missing COM class", unchecked((int)0x80040154)) };
Require((await Inspect(unsupported)).Status == InspectionStatus.Unsupported, "Unavailable COM class was not explicit.");

var inaccessibleChild = Specs.Folder();
inaccessibleChild.Children[SchedulerChild.Tasks] = _ => throw Denied();
result = await Inspect(new FakeBackend(Specs.Folder([Specs.Task()], [inaccessibleChild])));
Require(result.Status == InspectionStatus.Partial && result.Tasks.Count == 1 && result.Detail.Contains("denied reads=1"), "Subfolder denial was reported as complete.");
var inaccessibleTask = Specs.Task();
inaccessibleTask.ReadErrors[SchedulerValue.Enabled] = Denied();
inaccessibleTask.Children[SchedulerChild.Definition] = _ => throw Denied();
result = await Inspect(new FakeBackend(Specs.Folder([inaccessibleTask])));
Require(result.Status == InspectionStatus.Partial && result.Tasks[0].Status == InspectionStatus.AccessDenied &&
    result.Tasks[0].Enabled is null && result.Tasks[0].Actions == "Unavailable" && result.Tasks[0].Name == "fictional", "Per-task denied metadata was lost or fabricated.");
var brokenItemRoot = Specs.Folder();
brokenItemRoot.Children[SchedulerChild.Tasks] = _ => Specs.Collection("tasks", 1, _ => throw Failure());
result = await Inspect(new FakeBackend(brokenItemRoot));
Require(result.Status == InspectionStatus.Partial && result.Tasks.Single().Status == InspectionStatus.Failed && result.Tasks[0].Path == "", "Failed item acquisition was hidden.");
var brokenAction = Specs.Member("action", 0);
brokenAction.ReadErrors[SchedulerValue.Path] = Denied();
task = Specs.Task();
task.Children[SchedulerChild.Definition](0).Children[SchedulerChild.Actions] = _ => Specs.Collection("actions", [brokenAction]);
result = await Inspect(new FakeBackend(Specs.Folder([task])));
Require(result.Status == InspectionStatus.Partial && result.Tasks[0].Status == InspectionStatus.AccessDenied && result.Tasks[0].Actions == "Unavailable", "Denied action was not explicit.");

var manyTasks = Specs.Folder();
manyTasks.Children[SchedulerChild.Tasks] = _ => Specs.Collection("tasks", 1001, _ => Specs.Task());
var manyBackend = new FakeBackend(manyTasks);
result = await Inspect(manyBackend);
Require(result.Status == InspectionStatus.Truncated && result.Tasks.Count == 1000 && manyBackend.Operations.Count(x => x.StartsWith("Open:tasks.Item:")) == 1000, "Task bound was exceeded.");
var manyFolders = Specs.Folder();
manyFolders.Children[SchedulerChild.Folders] = _ => Specs.Collection("folders", 256, _ => Specs.Folder());
manyBackend = new FakeBackend(manyFolders);
result = await Inspect(manyBackend);
Require(result.Status == InspectionStatus.Truncated && manyBackend.Operations.Count(x => x.StartsWith("Open:folders.Item:")) == 255, "Folder bound must include root.");
manyFolders = Specs.Folder();
manyFolders.Children[SchedulerChild.Folders] = _ => Specs.Collection("folders", int.MaxValue, _ => throw Denied());
manyBackend = new FakeBackend(manyFolders);
result = await Inspect(manyBackend);
Require(result.Status == InspectionStatus.Truncated && manyBackend.Operations.Count(x => x.StartsWith("Open:folders.Item:")) == 255, "Denied folder attempts escaped the folder bound.");
Spec DeepFolder(int remaining) => Specs.Folder([], remaining == 0 ? [] : [DeepFolder(remaining - 1)]);
var deepBackend = new FakeBackend(DeepFolder(18));
result = await Inspect(deepBackend);
Require(result.Status == InspectionStatus.Truncated && deepBackend.Operations.Count(x => x.StartsWith("Open:folders.Item:")) == 16, "Depth bound failed.");
task = Specs.Task();
definition = task.Children[SchedulerChild.Definition](0);
definition.Children[SchedulerChild.Actions] = _ => Specs.Collection("actions", 33, _ => Specs.Member("action", 5));
definition.Children[SchedulerChild.Triggers] = _ => Specs.Collection("triggers", 33, _ => Specs.Member("trigger", 9));
manyBackend = new FakeBackend(Specs.Folder([task]));
result = await Inspect(manyBackend);
Require(result.Status == InspectionStatus.Truncated && result.Tasks[0].Status == InspectionStatus.Truncated &&
    manyBackend.Operations.Count(x => x.StartsWith("Open:actions.Item:")) == 32 && manyBackend.Operations.Count(x => x.StartsWith("Open:triggers.Item:")) == 32, "Action/trigger bounds failed.");
task = Specs.Task(new string('n', 5000));
action = Specs.Member("action", 0);
action.Values[SchedulerValue.Path] = new string('a', 4095) + "😀more";
task.Children[SchedulerChild.Definition](0).Children[SchedulerChild.Actions] = _ => Specs.Collection("actions", [action]);
result = await Inspect(new FakeBackend(Specs.Folder([task])));
Require(result.Status == InspectionStatus.Truncated && result.Tasks[0].Name.Length == 4096 && result.Tasks[0].Path.Length == 4096 && result.Tasks[0].Actions.Length <= 4096 &&
    !char.IsHighSurrogate(result.Tasks[0].Actions[^1]), "Character bounds/surrogate preservation failed.");

foreach (string stopAfter in new[] { "Connect:service", "Open:service.Root:0", "Open:folder.Tasks:1", "Read:task.Name", "Open:task.Definition:0", "Open:definition.Actions:0", "Read:action.Type" })
{
    using var source = new CancellationTokenSource();
    var fake = new FakeBackend(Specs.Folder([Specs.Task()]));
    fake.AfterOperation = operation => { if (operation == stopAfter) source.Cancel(); };
    await Canceled(() => new WindowsScheduledTaskProvider(fake).CaptureAsync(source.Token));
    Clean(fake);
    Require(fake.Operations[^1] == stopAfter, "COM reads continued after cancellation.");
}
using (var source = new CancellationTokenSource())
{
    source.Cancel();
    var fake = new FakeBackend(Specs.Folder());
    await Canceled(() => new WindowsScheduledTaskProvider(fake).CaptureAsync(source.Token));
    Require(fake.Acquired == 0, "Pre-cancellation activated COM.");
}
var budget = new FakeBackend(Specs.Folder([Specs.Task()]));
budget.AfterOperation = operation => { if (operation == "Read:task.Name") budget.Elapsed = TimeSpan.FromSeconds(3); };
result = await Inspect(budget);
Require(result.Status == InspectionStatus.TimedOut && budget.Operations[^1] == "Read:task.Name", "Cooperative budget did not stop subsequent getters.");
using (var source = new CancellationTokenSource())
{
    var fake = new FakeBackend(Specs.Folder()) { AfterServiceDispose = source.Cancel };
    await Canceled(() => new WindowsScheduledTaskProvider(fake).CaptureAsync(source.Token));
    Clean(fake);
}
var serialized = new FakeBackend(Specs.Folder([Specs.Task()]));
var serializedProvider = new WindowsScheduledTaskProvider(serialized);
await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => serializedProvider.CaptureAsync(default)));
Clean(serialized);
Require(serialized.MaximumActiveServices == 1, "Captures were not serialized per provider.");

if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native scheduler smoke requires Windows.");
var native = await new WindowsScheduledTaskProvider().CaptureAsync(default);
Require(native.Tasks.Count <= 1000 && native.Detail.Length > 0 && native.Detail.Contains("current-token"), "Native result bounds/limitations missing.");
foreach (var row in native.Tasks)
    Require(new[] { row.Path, row.Name, row.State, row.Actions, row.Triggers, row.Detail }.All(x => x.Length <= 4096), "Native field exceeds character bound.");
Require(native.Status is InspectionStatus.Available or InspectionStatus.Partial or InspectionStatus.Truncated or InspectionStatus.TimedOut or InspectionStatus.AccessDenied ||
    (native.Status == InspectionStatus.Unsupported && (native.Detail.Contains("0x80040154") || native.Detail.Contains("0x80004001"))) ||
    (native.Status == InspectionStatus.Failed && native.Detail.Contains("0x80041315")), "Unexpected native scheduler failure.");
if (native.Status == InspectionStatus.AccessDenied) Require(native.Tasks.Count == 0 && native.Detail.Contains("denied reads="), "Root denial must be explicit.");
Console.WriteLine($"PASS: {checks} assertions; native status={native.Status}, task rows={native.Tasks.Count}. No real task names or paths logged.");

sealed class Spec(string kind)
{
    public string Kind { get; } = kind;
    public Dictionary<SchedulerValue, object> Values { get; } = [];
    public Dictionary<SchedulerValue, Exception> ReadErrors { get; } = [];
    public Dictionary<SchedulerChild, Func<int, Spec>> Children { get; } = [];
    public Exception? ConnectError;
}

static class Specs
{
    public static Spec Collection(string kind, IReadOnlyList<Spec> items) => Collection(kind, items.Count, index => items[index - 1]);
    public static Spec Collection(string kind, int count, Func<int, Spec> item)
    {
        var result = new Spec(kind);
        result.Values[SchedulerValue.Count] = count;
        result.Children[SchedulerChild.Item] = item;
        return result;
    }
    public static Spec Folder(IReadOnlyList<Spec>? tasks = null, IReadOnlyList<Spec>? folders = null)
    {
        var result = new Spec("folder");
        result.Children[SchedulerChild.Tasks] = _ => Collection("tasks", tasks ?? []);
        result.Children[SchedulerChild.Folders] = _ => Collection("folders", folders ?? []);
        return result;
    }
    public static Spec Member(string kind, int type)
    {
        var result = new Spec(kind);
        result.Values[SchedulerValue.Type] = type;
        return result;
    }
    public static Spec Task(string name = "fictional")
    {
        var task = new Spec("task");
        task.Values[SchedulerValue.Path] = "\\" + name;
        task.Values[SchedulerValue.Name] = name;
        task.Values[SchedulerValue.State] = 3;
        task.Values[SchedulerValue.Enabled] = true;
        var definition = new Spec("definition");
        definition.Children[SchedulerChild.Actions] = _ => Collection("actions", [Member("action", 5)]);
        definition.Children[SchedulerChild.Triggers] = _ => Collection("triggers", [Member("trigger", 9)]);
        task.Children[SchedulerChild.Definition] = _ => definition;
        return task;
    }
}

sealed class FakeBackend : IScheduledTaskBackend
{
    public Spec Service { get; } = new("service");
    public int Acquired, Disposed, ActiveServices, MaximumActiveServices;
    public List<string> Operations { get; } = [];
    public TimeSpan Elapsed;
    public Exception? CreateError;
    public Action<string>? AfterOperation;
    public Action? AfterServiceDispose;
    public FakeBackend(Spec root) => Service.Children[SchedulerChild.Root] = _ => root;
    public long GetTimestamp() => 0;
    public TimeSpan GetElapsedTime(long started) => Elapsed;
    public ISchedulerNode CreateService()
    {
        if (CreateError is { } error) throw error;
        return new Lease(this, Service);
    }
    private void Operation(string operation)
    {
        Operations.Add(operation);
        AfterOperation?.Invoke(operation);
    }
    private sealed class Lease : ISchedulerNode
    {
        private readonly FakeBackend owner;
        private readonly Spec spec;
        private bool disposed;
        public Lease(FakeBackend owner, Spec spec)
        {
            this.owner = owner;
            this.spec = spec;
            owner.Acquired++;
            if (spec.Kind == "service") owner.MaximumActiveServices = Math.Max(owner.MaximumActiveServices, ++owner.ActiveServices);
        }
        public void Connect()
        {
            owner.Operation($"Connect:{spec.Kind}");
            if (spec.ConnectError is { } error) throw error;
        }
        public ISchedulerNode Open(SchedulerChild child, int index)
        {
            string operation = $"Open:{spec.Kind}.{child}:{index}";
            Spec target;
            try { target = spec.Children[child](index); }
            catch { owner.Operation(operation); throw; }
            var lease = new Lease(owner, target);
            owner.Operation(operation);
            return lease;
        }
        public object Read(SchedulerValue value)
        {
            owner.Operation($"Read:{spec.Kind}.{value}");
            if (spec.ReadErrors.TryGetValue(value, out var error)) throw error;
            return spec.Values[value];
        }
        public void Dispose()
        {
            if (disposed) throw new InvalidOperationException("Double release.");
            disposed = true;
            owner.Disposed++;
            if (spec.Kind == "service")
            {
                owner.ActiveServices--;
                owner.AfterServiceDispose?.Invoke();
            }
        }
    }
}
