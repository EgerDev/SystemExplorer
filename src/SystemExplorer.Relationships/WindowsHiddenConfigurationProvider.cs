using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;
using SystemExplorer.Core;

namespace SystemExplorer.Relationships;

public sealed class WindowsHiddenConfigurationProvider : IServiceCrossViewProvider, ITaskCrossViewProvider
{
    private const int Cap = 4096, DepthCap = 32;
    private const string TaskRoot = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\TaskCache";
    Task<ServiceCrossViewSnapshot> IServiceCrossViewProvider.CaptureAsync(CancellationToken cancellationToken) => CaptureServicesAsync(cancellationToken);
    Task<TaskCrossViewSnapshot> ITaskCrossViewProvider.CaptureAsync(CancellationToken cancellationToken) => CaptureTasksAsync(cancellationToken);
    public Task<ServiceCrossViewSnapshot> CaptureServicesAsync(CancellationToken cancellationToken) => Task.Run(() => Services(cancellationToken), cancellationToken);
    public Task<TaskCrossViewSnapshot> CaptureTasksAsync(CancellationToken cancellationToken) => Task.Run(() => Tasks(cancellationToken), cancellationToken);

    private sealed class View(CancellationToken token)
    {
        private readonly Stopwatch watch = Stopwatch.StartNew();
        internal InspectionStatus Status = InspectionStatus.Available;
        internal bool Truncated;
        internal int Visits;
        internal string Detail = "";
        internal double Milliseconds => watch.Elapsed.TotalMilliseconds;
        internal void Check()
        {
            token.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromSeconds(10)) throw new ViewFailure(InspectionStatus.TimedOut, "Cooperative ten-second view budget reached; native calls cannot be interrupted.");
        }
        internal void Limit() { Truncated = true; throw new ViewFailure(InspectionStatus.Truncated, "Collection/depth cap reached."); }
        internal void Run(Action read)
        {
            try { Check(); read(); Check(); }
            catch (ViewFailure ex) { Status = ex.Status; Detail = ex.Message; Truncated |= Status == InspectionStatus.Truncated; }
            catch (Win32Exception ex) { Status = ex.NativeErrorCode == 5 ? InspectionStatus.AccessDenied : InspectionStatus.Failed; Detail = $"Windows error {ex.NativeErrorCode}: {ex.Message}"; }
            catch (UnauthorizedAccessException) { Status = InspectionStatus.AccessDenied; Detail = "Access denied; full coverage requires administrator."; }
            catch (COMException ex) { Status = ex.HResult == unchecked((int)0x80070005) ? InspectionStatus.AccessDenied : InspectionStatus.Failed; Detail = $"COM HRESULT 0x{ex.HResult:X8}; task enumeration unavailable."; }
            catch (IOException ex) { Status = InspectionStatus.Partial; Detail = $"Filesystem observation unavailable (HRESULT 0x{ex.HResult:X8})."; }
            catch (InvalidDataException ex) { Status = InspectionStatus.Failed; Detail = ex.Message; }
            finally { watch.Stop(); }
        }
        internal void Failure(int error)
        {
            if (error == 5) Status = InspectionStatus.AccessDenied;
            else if (Status == InspectionStatus.Available) Status = InspectionStatus.Partial;
            Detail = $"Some reads unavailable (Windows error {error}); concurrent deletion or access restrictions may explain omissions.";
        }
        internal string Summary(string name, int count) => $"{name}: {count} rows, {Status}, {Milliseconds:F1} ms. {Detail}";
    }
    private sealed class ViewFailure(InspectionStatus status, string detail) : Exception(detail) { internal InspectionStatus Status { get; } = status; }

    private static SafeRegistryHandle Open(string path)
    {
        int error = NativeServiceMethods.RegOpenKeyExW(new IntPtr(unchecked((int)0x80000002)), path, 0, 0x20119, out var raw);
        if (error != 0) throw new Win32Exception(error);
        return new SafeRegistryHandle(raw, true);
    }
    private static IEnumerable<string> Names(SafeRegistryHandle key, View view)
    {
        for (uint index = 0; ; index++)
        {
            view.Check();
            var name = new StringBuilder(256);
            uint length = 256;
            int error = NativeServiceMethods.RegEnumKeyExW(key.DangerousGetHandle(), index, name, ref length, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
            if (error == 259) yield break;
            if (error != 0) throw new Win32Exception(error);
            if (++view.Visits > Cap) view.Limit();
            yield return name.ToString();
        }
    }
    private static (uint Type, byte[] Data)? Value(SafeRegistryHandle key, string name)
    {
        uint size = 0;
        int error = HiddenConfigurationNative.RegQueryValueExW(key, name, IntPtr.Zero, out uint type, null, ref size);
        if (error == 2) return null;
        if (error != 0) throw new Win32Exception(error);
        if (size > 16384) throw new ViewFailure(InspectionStatus.Truncated, "Registry value exceeds 16384-byte cap.");
        var data = new byte[size];
        error = HiddenConfigurationNative.RegQueryValueExW(key, name, IntPtr.Zero, out type, data, ref size);
        if (error != 0) throw new Win32Exception(error);
        return (type, data);
    }
    private static uint? Dword(SafeRegistryHandle key, string name) => Value(key, name) is { Type: 4, Data.Length: 4 } v ? BitConverter.ToUInt32(v.Data) : null;
    private static string? Text(SafeRegistryHandle key, string name) => Value(key, name) is { } v && v.Type is 1 or 2 && v.Data.Length % 2 == 0 ? Encoding.Unicode.GetString(v.Data).TrimEnd('\0') : null;
    private static bool HasValue(SafeRegistryHandle key, string name)
    {
        uint size = 0;
        int error = HiddenConfigurationNative.RegQueryValueExW(key, name, IntPtr.Zero, out _, null, ref size);
        if (error == 2) return false;
        if (error != 0) throw new Win32Exception(error);
        return true;
    }
    private static ServiceCrossViewSnapshot Services(CancellationToken token)
    {
        var at = DateTimeOffset.UtcNow;
        var registry = new List<ServiceRegistryEntry>();
        var scm = new List<ServiceSnapshot>();
        int ignored = 0;
        var regView = new View(token);
        regView.Run(() =>
        {
            const string root = @"SYSTEM\CurrentControlSet\Services";
            using var key = Open(root);
            foreach (var name in Names(key, regView))
            {
                try
                {
                    using var child = Open(root + "\\" + name);
                    uint? type = Dword(child, "Type");
                    if (type is null) { ignored++; continue; }
                    registry.Add(new(name, type, Dword(child, "Start"), Dword(child, "DeleteFlag") == 1, Text(child, "ImagePath")));
                }
                catch (Win32Exception ex) { regView.Failure(ex.NativeErrorCode); }
            }
        });
        var scmView = new View(token);
        scmView.Run(() =>
        {
            using var manager = NativeServiceMethods.OpenSCManagerW(null, null, 4);
            if (manager.IsInvalid) throw new Win32Exception();
            const uint capacity = 256 * 1024;
            IntPtr buffer = Marshal.AllocHGlobal((int)capacity);
            uint resume = 0;
            try
            {
                for (int page = 0; page < 32; page++)
                {
                    scmView.Check();
                    bool ok = NativeServiceMethods.EnumServicesStatusExW(manager, 0, 0x3ff, 3, buffer, capacity, out _, out uint count, ref resume, null);
                    int error = ok ? 0 : Marshal.GetLastWin32Error();
                    if (!ok && error != 234) throw new Win32Exception(error);
                    int stride = Marshal.SizeOf<NativeMethods.ServiceEntry>();
                    if (count > capacity / stride) throw new InvalidDataException("Invalid SCM count.");
                    for (int i = 0; i < count; i++)
                    {
                        scmView.Check();
                        if (scm.Count >= Cap) scmView.Limit();
                        var entry = Marshal.PtrToStructure<NativeMethods.ServiceEntry>(IntPtr.Add(buffer, i * stride));
                        scm.Add(new() { Name = Marshal.PtrToStringUni(entry.Name) ?? "", DisplayName = Marshal.PtrToStringUni(entry.DisplayName) ?? "",
                            State = entry.State.ToString(), ProcessId = entry.Pid > 0 ? (int)entry.Pid : null });
                    }
                    if (ok) return;
                }
                scmView.Limit();
            }
            finally { Marshal.FreeHGlobal(buffer); }
        });
        return new(at, regView.Status, scmView.Status, regView.Summary("Registry", registry.Count) + " " + scmView.Summary("SCM", scm.Count), ignored,
            new(registry, Cap, regView.Truncated), new(scm, Cap, scmView.Truncated));
    }

    private static TaskCrossViewSnapshot Tasks(CancellationToken token)
    {
        var at = DateTimeOffset.UtcNow;
        var com = new List<ScheduledTaskObservation>();
        var tree = new List<TaskTreeEntry>();
        var cache = new List<TaskCacheEntry>();
        var files = new List<string>();
        var comView = new View(token);
        comView.Run(() =>
        {
            var backend = new WindowsScheduledTaskBackend();
            using var service = backend.CreateService();
            comView.Check(); service.Connect(); comView.Check();
            using var root = service.Open(SchedulerChild.Root, 0);
            void Visit(ISchedulerNode folder, int depth)
            {
                comView.Check();
                using (var tasks = folder.Open(SchedulerChild.Tasks, 1))
                {
                    int count = (int)tasks.Read(SchedulerValue.Count);
                    if (count < 0) throw new InvalidDataException("Invalid task count.");
                    for (int i = 1; i <= count; i++)
                    {
                        comView.Check();
                        if (com.Count >= Cap) comView.Limit();
                        using var task = tasks.Open(SchedulerChild.Item, i);
                        string path = (string)task.Read(SchedulerValue.Path);
                        if (path.Length > 4096) comView.Limit();
                        com.Add(new(path, path.Split('\\')[^1], "Observed", null, "Not collected", "Not collected", InspectionStatus.Available, "TASK_ENUM_HIDDEN; metadata path only."));
                    }
                }
                using var folders = folder.Open(SchedulerChild.Folders, 0);
                int children = (int)folders.Read(SchedulerValue.Count);
                if (children < 0) throw new InvalidDataException("Invalid folder count.");
                for (int i = 1; i <= children; i++)
                {
                    comView.Check();
                    if (depth >= DepthCap || ++comView.Visits > Cap) comView.Limit();
                    using var child = folders.Open(SchedulerChild.Item, i);
                    Visit(child, depth + 1);
                }
            }
            Visit(root, 0);
        });
        var treeView = new View(token);
        treeView.Run(() =>
        {
            void Visit(string subpath, int depth)
            {
                treeView.Check();
                using var key = Open(TaskRoot + "\\Tree" + subpath);
                if (Text(key, "Id") is { } id) tree.Add(new(subpath, id, HasValue(key, "SD")));
                foreach (var name in Names(key, treeView))
                {
                    if (depth >= DepthCap) treeView.Limit();
                    try { Visit(subpath + "\\" + name, depth + 1); }
                    catch (Win32Exception ex) { treeView.Failure(ex.NativeErrorCode); }
                }
            }
            Visit("", 0);
        });
        var cacheView = new View(token);
        cacheView.Run(() =>
        {
            using var key = Open(TaskRoot + "\\Tasks");
            foreach (var id in Names(key, cacheView))
            {
                try
                {
                    using var child = Open(TaskRoot + "\\Tasks\\" + id);
                    if (Text(child, "Path") is { } path) cache.Add(new(id, path));
                    else cacheView.Failure(13);
                }
                catch (Win32Exception ex) { cacheView.Failure(ex.NativeErrorCode); }
            }
        });
        var filesView = new View(token);
        filesView.Run(() =>
        {
            string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "Tasks");
            void Visit(string directory, int depth)
            {
                filesView.Check();
                if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) { filesView.Status = InspectionStatus.Partial; filesView.Detail = "Reparse points skipped; no links followed."; return; }
                foreach (var path in Directory.EnumerateFileSystemEntries(directory))
                {
                    filesView.Check();
                    if (++filesView.Visits > Cap) filesView.Limit();
                    try
                    {
                        var attributes = File.GetAttributes(path);
                        if ((attributes & FileAttributes.ReparsePoint) != 0) { filesView.Status = InspectionStatus.Partial; filesView.Detail = "Reparse points skipped; no links followed."; continue; }
                        if ((attributes & FileAttributes.Directory) != 0)
                        {
                            if (depth >= DepthCap) filesView.Limit();
                            Visit(path, depth + 1);
                        }
                        else files.Add("\\" + Path.GetRelativePath(root, path));
                    }
                    catch (UnauthorizedAccessException) { filesView.Failure(5); }
                    catch (IOException) { filesView.Failure(2); }
                }
            }
            Visit(root, 0);
        });
        using var identity = WindowsIdentity.GetCurrent();
        bool elevated = HiddenConfigurationNative.GetTokenInformation(identity.AccessToken, 20, out int elevation, 4, out _) && elevation != 0;
        string detail = string.Join(" ", comView.Summary("COM", com.Count), treeView.Summary("Tree", tree.Count), cacheView.Summary("Tasks cache", cache.Count), filesView.Summary("Files", files.Count));
        if (!elevated) detail += " Full task coverage requires administrator; no elevation attempted.";
        return new(at, comView.Status, treeView.Status, cacheView.Status, filesView.Status, detail, elevated,
            new(com, Cap, comView.Truncated), new(tree, Cap, treeView.Truncated), new(cache, Cap, cacheView.Truncated), new(files, Cap, filesView.Truncated));
    }
}

internal static class HiddenConfigurationNative
{
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int RegQueryValueExW(SafeRegistryHandle key, string name, IntPtr reserved, out uint type, byte[]? data, ref uint size);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out int value, uint size, out uint required);
}
