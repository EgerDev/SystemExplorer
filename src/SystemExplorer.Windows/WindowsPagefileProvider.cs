using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SystemExplorer.Core;

[assembly: InternalsVisibleTo("SystemExplorer.Pagefiles.Smoke")]

namespace SystemExplorer.Windows;

public sealed class WindowsPagefileProvider : IPagefileProvider
{
    internal const int MaximumRecords = 128;
    internal const int MaximumNameCharacters = 32768;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);
    private readonly IPagefileNative native;
    private readonly Func<TimeSpan>? elapsedForTesting;

    public WindowsPagefileProvider() : this(new PagefileNative()) { }

    internal WindowsPagefileProvider(IPagefileNative native, Func<TimeSpan>? elapsedForTesting = null)
    {
        this.native = native;
        this.elapsedForTesting = elapsedForTesting;
    }

    public Task<PagefileInspection> CaptureAsync(CancellationToken cancellationToken) =>
        Task.Run(() => Capture(cancellationToken), cancellationToken);

    private PagefileInspection Capture(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var captured = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var rows = new List<PagefileObservation>();
        uint pageSize = 0;
        InspectionStatus? stopped = null;
        string reason = "";
        TimeSpan Elapsed() => elapsedForTesting?.Invoke() ?? clock.Elapsed;
        PagefileInspection Result(InspectionStatus status, string detail) => new(captured, status, detail, rows.ToArray());
        PagefileInspection Incomplete(InspectionStatus status, string detail) =>
            Result(status, $"{detail} Retained {rows.Count} record(s) form an incomplete prefix; absence is not established.");

        bool CheckBudget()
        {
            if (token.IsCancellationRequested) return false;
            if (Elapsed() < Budget) return true;
            stopped = InspectionStatus.TimedOut;
            reason = "Pagefile observation reached its three-second cooperative budget; blocking native calls cannot be interrupted.";
            return false;
        }

        bool Observe(IntPtr context, IntPtr information, IntPtr filename)
        {
            try
            {
                if (stopped.HasValue || !CheckBudget()) return false;
                if (rows.Count >= MaximumRecords)
                {
                    stopped = InspectionStatus.Truncated;
                    reason = $"Pagefile inventory truncated at {MaximumRecords} records.";
                    return false;
                }
                if (information == IntPtr.Zero || filename == IntPtr.Zero ||
                    (uint)Marshal.ReadInt32(information) != Marshal.SizeOf<PagefileInformation>())
                    throw new InvalidOperationException("Invalid pagefile callback layout.");

                var info = Marshal.PtrToStructure<PagefileInformation>(information);
                var name = new StringBuilder();
                for (var index = 0; index < MaximumNameCharacters; index++)
                {
                    if (index % 256 == 0 && !CheckBudget()) return false;
                    var character = (char)Marshal.ReadInt16(filename, checked(index * sizeof(char)));
                    if (character == '\0')
                    {
                        if (name.Length == 0) throw new InvalidOperationException("Missing pagefile name.");
                        var row = ConvertPages(name.ToString(), info, pageSize);
                        if (!CheckBudget()) return false;
                        rows.Add(row);
                        return true;
                    }
                    name.Append(character);
                }
                stopped = InspectionStatus.Truncated;
                reason = $"Pagefile name exceeded the {MaximumNameCharacters - 1}-character decoding bound; the incomplete record was omitted.";
                return false;
            }
            catch (Exception)
            {
                stopped = rows.Count == 0 ? InspectionStatus.Failed : InspectionStatus.Partial;
                reason = "Pagefile callback data could not be decoded or converted safely; the incomplete record was omitted.";
                return false;
            }
        }

        if (!OperatingSystem.IsWindows())
            return Result(InspectionStatus.Unsupported, "Pagefile enumeration requires Windows; no values were inferred.");
        NativeEnumeration enumeration;
        PagefileCallback callback = Observe;
        try
        {
            pageSize = native.GetPageSize();
            token.ThrowIfCancellationRequested();
            if (pageSize == 0) return Result(InspectionStatus.Failed, "GetSystemInfo returned no usable page size; no values were inferred.");
            if (!CheckBudget())
            {
                token.ThrowIfCancellationRequested();
                return Incomplete(InspectionStatus.TimedOut, reason);
            }
            enumeration = native.Enumerate(callback);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            token.ThrowIfCancellationRequested();
            return Incomplete(rows.Count == 0 ? InspectionStatus.Unsupported : InspectionStatus.Partial,
                "The documented Windows pagefile API is unavailable; no fallback was attempted.");
        }
        finally
        {
            GC.KeepAlive(callback);
        }
        token.ThrowIfCancellationRequested();
        if (stopped is { } status) return Incomplete(status, reason);
        if (!enumeration.Succeeded)
        {
            var failureStatus = rows.Count > 0 ? InspectionStatus.Partial : enumeration.Error switch
            {
                5 => InspectionStatus.AccessDenied,
                50 or 120 or 127 => InspectionStatus.Unsupported,
                _ => InspectionStatus.Failed
            };
            return Incomplete(failureStatus, $"EnumPageFilesW failed (Windows error {enumeration.Error}); no missing values were inferred.");
        }
        if (!CheckBudget())
        {
            token.ThrowIfCancellationRequested();
            return Incomplete(InspectionStatus.TimedOut, reason);
        }
        token.ThrowIfCancellationRequested();
        return Result(InspectionStatus.Available, rows.Count == 0
            ? "EnumPageFilesW succeeded; no pagefiles were enumerated. This is not system commit or physical RAM usage."
            : "EnumPageFilesW observed pagefile sizes, current usage and historical peaks, converted from pages using GetSystemInfo. Values may change during enumeration; peaks are not clamped to current sizes. This is not system commit or physical RAM usage.");
    }

    internal static PagefileObservation ConvertPages(string name, PagefileInformation info, uint pageSize) =>
        new(name, checked(info.TotalSize.ToUInt64() * pageSize), checked(info.TotalInUse.ToUInt64() * pageSize),
            checked(info.PeakUsage.ToUInt64() * pageSize));

    [StructLayout(LayoutKind.Sequential)]
    internal struct PagefileInformation
    {
        internal uint Size, Reserved;
        internal UIntPtr TotalSize, TotalInUse, PeakUsage;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal delegate bool PagefileCallback(IntPtr context, IntPtr information, IntPtr filename);

    internal readonly record struct NativeEnumeration(bool Succeeded, int Error);

    internal interface IPagefileNative
    {
        uint GetPageSize();
        NativeEnumeration Enumerate(PagefileCallback callback);
    }

    private sealed class PagefileNative : IPagefileNative
    {
        public uint GetPageSize()
        {
            GetSystemInfo(out var information);
            return information.PageSize;
        }

        public NativeEnumeration Enumerate(PagefileCallback callback)
        {
            var succeeded = EnumPageFilesW(callback, IntPtr.Zero);
            var error = succeeded ? 0 : Marshal.GetLastWin32Error();
            return new(succeeded, error);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemInformation
    {
        internal uint Architecture, PageSize;
        internal UIntPtr MinimumAddress, MaximumAddress, ActiveProcessorMask;
        internal uint ProcessorCount, ProcessorType, AllocationGranularity;
        internal ushort ProcessorLevel, ProcessorRevision;
    }

    [DllImport("psapi.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumPageFilesW(PagefileCallback callback, IntPtr context);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    private static extern void GetSystemInfo(out SystemInformation information);
}
