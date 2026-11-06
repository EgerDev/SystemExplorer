using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using SystemExplorer.Core;

namespace SystemExplorer.Relationships;

internal sealed class ServiceConfigReader
{
    private const uint ErrorAccessDenied = 5;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint ErrorServiceDoesNotExist = 1060;
    private const uint ErrorServiceMarkedForDelete = 1072;
    private const uint ServiceQueryConfig = 0x1;
    private const int MaximumServicesPerPass = 256;
    private const double MaximumElapsedMilliseconds = 100;
    private const uint MaximumConfigBytes = 8192;
    private const int MaximumQueryCalls = 3;
    private const int MaximumExamples = 3;
    private const int MaximumExampleNameLength = 80;
    private static readonly uint ConfigHeaderSize = (uint)Marshal.SizeOf<NativeMethods.QueryServiceConfig>();

    private readonly IServiceConfigBackend backend;
    private string? nextServiceName;

    internal ServiceConfigReader() : this(new WindowsServiceConfigBackend()) { }

    internal ServiceConfigReader(IServiceConfigBackend backend) =>
        this.backend = backend ?? throw new ArgumentNullException(nameof(backend));

    internal void Enrich(
        SafeServiceHandle manager,
        List<ServiceSnapshot> services,
        List<string> limitations,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(manager);
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(limitations);

        long startedAt = backend.GetTimestamp();
        int attempted = 0, available = 0, denied = 0, missing = 0, errors = 0, budgetSkipped = 0, canceled = 0;
        var examples = new List<string>(MaximumExamples);
        try
        {
            for (int i = 0; i < services.Count; i++)
                services[i] = services[i] with { StartType = null };

            token.ThrowIfCancellationRequested();
            var grouped = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < services.Count; i++)
            {
                string name = services[i].Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    errors++;
                    AddExample(examples, "error", "(invalid service name)");
                    continue;
                }
                if (!grouped.TryGetValue(name, out var indexes))
                {
                    indexes = new List<int>();
                    grouped.Add(name, indexes);
                }
                indexes.Add(i);
            }

            var ordered = grouped.Select(pair => new ServiceCandidate(pair.Key, pair.Value.ToArray()))
                .OrderBy(candidate => candidate.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
                .ToArray();
            if (ordered.Length == 0)
            {
                nextServiceName = null;
                return;
            }

            int start = FindStart(ordered, nextServiceName);
            for (int offset = 0; offset < ordered.Length; offset++)
            {
                int index = (start + offset) % ordered.Length;
                var candidate = ordered[index];
                if (attempted >= MaximumServicesPerPass || IsOverTimeLimit(startedAt))
                {
                    budgetSkipped += ordered.Length - offset;
                    break;
                }

                nextServiceName = ordered[(index + 1) % ordered.Length].Name;
                attempted++;

                ReadResult result;
                try
                {
                    result = ReadStartType(manager, candidate.Name, startedAt, token);
                }
                catch (Win32Exception ex)
                {
                    result = ClassifyError(ex.NativeErrorCode);
                }
                catch (InvalidDataException)
                {
                    result = new ReadResult(ResultKind.Error);
                }

                switch (result.Kind)
                {
                    case ResultKind.Available:
                        available++;
                        foreach (int rowIndex in candidate.RowIndexes)
                            services[rowIndex] = services[rowIndex] with { StartType = result.StartType };
                        break;
                    case ResultKind.Denied:
                        denied++;
                        AddExample(examples, "denied", candidate.Name);
                        break;
                    case ResultKind.Missing:
                        missing++;
                        AddExample(examples, "missing/deleted", candidate.Name);
                        break;
                    case ResultKind.Error:
                        errors++;
                        AddExample(examples, "error", candidate.Name);
                        break;
                    case ResultKind.BudgetSkipped:
                        budgetSkipped += ordered.Length - offset;
                        offset = ordered.Length;
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            canceled++;
            throw;
        }
        finally
        {
            var summary = new StringBuilder()
                .Append("Service start types (configured base only): observed=").Append(services.Count)
                .Append(", attempted=").Append(attempted)
                .Append(", available=").Append(available)
                .Append(", denied=").Append(denied)
                .Append(", missing/deleted=").Append(missing)
                .Append(", errors=").Append(errors)
                .Append(", budget-skipped=").Append(budgetSkipped)
                .Append(", canceled=").Append(canceled);
            if (examples.Count > 0) summary.Append("; examples=").Append(string.Join(" | ", examples));
            limitations.Add(summary.Append('.').ToString());
        }
    }

    private ReadResult ReadStartType(
        SafeServiceHandle manager,
        string serviceName,
        long startedAt,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        using IServiceConfigQueryHandle? service = backend.Open(manager, serviceName, ServiceQueryConfig, out int openError);
        token.ThrowIfCancellationRequested();
        if (service is null) return ClassifyError(openError);
        if (openError != 0) return ClassifyError(openError);

        IntPtr buffer = IntPtr.Zero;
        uint bufferSize = 0;
        try
        {
            for (int call = 0; call < MaximumQueryCalls; call++)
            {
                token.ThrowIfCancellationRequested();
                if (IsOverTimeLimit(startedAt)) return new ReadResult(ResultKind.BudgetSkipped);

                int error = service.Query(buffer, bufferSize, out uint requiredSize);
                token.ThrowIfCancellationRequested();
                if (error == 0)
                {
                    if (buffer == IntPtr.Zero || bufferSize < ConfigHeaderSize)
                        return new ReadResult(ResultKind.Error);
                    var config = Marshal.PtrToStructure<NativeMethods.QueryServiceConfig>(buffer);
                    return new ReadResult(ResultKind.Available, StartType(config.StartType));
                }
                if ((uint)error != ErrorInsufficientBuffer) return ClassifyError(error);
                if (requiredSize < ConfigHeaderSize || requiredSize > MaximumConfigBytes)
                    return new ReadResult(ResultKind.Error);
                if (IsOverTimeLimit(startedAt)) return new ReadResult(ResultKind.BudgetSkipped);

                if (call == 0)
                {
                    buffer = backend.AllocateBuffer((int)requiredSize);
                    if (buffer == IntPtr.Zero) return new ReadResult(ResultKind.Error);
                    bufferSize = requiredSize;
                    continue;
                }

                if (requiredSize <= bufferSize || call == MaximumQueryCalls - 1)
                    return new ReadResult(ResultKind.Error);

                IntPtr replacement = backend.AllocateBuffer((int)requiredSize);
                if (replacement == IntPtr.Zero) return new ReadResult(ResultKind.Error);
                backend.FreeBuffer(buffer);
                buffer = replacement;
                bufferSize = requiredSize;
            }
            return new ReadResult(ResultKind.Error);
        }
        finally
        {
            if (buffer != IntPtr.Zero) backend.FreeBuffer(buffer);
        }
    }

    private bool IsOverTimeLimit(long startedAt) =>
        backend.GetElapsedMilliseconds(startedAt) >= MaximumElapsedMilliseconds;

    private static int FindStart(ServiceCandidate[] candidates, string? nextName)
    {
        if (string.IsNullOrEmpty(nextName)) return 0;
        for (int i = 0; i < candidates.Length; i++)
            if (StringComparer.OrdinalIgnoreCase.Compare(candidates[i].Name, nextName) >= 0) return i;
        return 0;
    }

    private static ReadResult ClassifyError(int error) => error switch
    {
        (int)ErrorAccessDenied => new ReadResult(ResultKind.Denied),
        (int)ErrorServiceDoesNotExist or (int)ErrorServiceMarkedForDelete => new ReadResult(ResultKind.Missing),
        _ => new ReadResult(ResultKind.Error)
    };

    private static string StartType(uint value) => value switch
    {
        0 => "Boot",
        1 => "System",
        2 => "Automatic",
        3 => "Manual",
        4 => "Disabled",
        _ => $"Unknown({value})"
    };

    private static void AddExample(List<string> examples, string category, string name)
    {
        if (examples.Count >= MaximumExamples) return;
        string safeName = name.Replace('\r', ' ').Replace('\n', ' ');
        if (safeName.Length > MaximumExampleNameLength)
            safeName = safeName[..(MaximumExampleNameLength - 3)] + "...";
        examples.Add($"{category}:{safeName}");
    }

    private sealed record ServiceCandidate(string Name, int[] RowIndexes);

    private enum ResultKind { Available, Denied, Missing, Error, BudgetSkipped }

    private readonly record struct ReadResult(ResultKind Kind, string? StartType = null);
}

internal interface IServiceConfigBackend
{
    IServiceConfigQueryHandle? Open(SafeServiceHandle manager, string serviceName, uint desiredAccess, out int error);
    IntPtr AllocateBuffer(int size);
    void FreeBuffer(IntPtr buffer);
    long GetTimestamp();
    double GetElapsedMilliseconds(long startedAt);
}

internal interface IServiceConfigQueryHandle : IDisposable
{
    int Query(IntPtr buffer, uint bufferSize, out uint requiredSize);
}

internal sealed class WindowsServiceConfigBackend : IServiceConfigBackend
{
    public IServiceConfigQueryHandle? Open(SafeServiceHandle manager, string serviceName, uint desiredAccess, out int error)
    {
        var service = NativeMethods.OpenServiceW(manager, serviceName, desiredAccess);
        if (service.IsInvalid)
        {
            error = Marshal.GetLastWin32Error();
            service.Dispose();
            return null;
        }
        error = 0;
        return new WindowsServiceConfigQueryHandle(service);
    }

    public IntPtr AllocateBuffer(int size) => Marshal.AllocHGlobal(size);

    public void FreeBuffer(IntPtr buffer) => Marshal.FreeHGlobal(buffer);

    public long GetTimestamp() => Stopwatch.GetTimestamp();

    public double GetElapsedMilliseconds(long startedAt) => Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds;

    private sealed class WindowsServiceConfigQueryHandle(SafeServiceHandle handle) : IServiceConfigQueryHandle
    {
        public int Query(IntPtr buffer, uint bufferSize, out uint requiredSize)
        {
            bool success = NativeMethods.QueryServiceConfigW(handle, buffer, bufferSize, out requiredSize);
            return success ? 0 : Marshal.GetLastWin32Error();
        }

        public void Dispose() => handle.Dispose();
    }
}
