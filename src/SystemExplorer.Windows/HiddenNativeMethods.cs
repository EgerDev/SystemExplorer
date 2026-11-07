using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace SystemExplorer.Windows;

internal static class HiddenNativeMethods
{
    [DllImport("ntdll.dll")]
    internal static extern int NtGetNextProcess(IntPtr previous, uint access, uint attributes, uint flags, out SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint GetProcessId(SafeProcessHandle process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetExitCodeProcess(SafeProcessHandle process, out uint code);
    [DllImport("advapi32.dll")]
    internal static extern uint EnumerateTraceGuidsEx(int query, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint required);
    [DllImport("iphlpapi.dll")]
    internal static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int kind, uint reserved);
    [DllImport("iphlpapi.dll")]
    internal static extern uint GetExtendedUdpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order, uint family, int kind, uint reserved);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern ServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumServicesStatusExW(ServiceHandle manager, int level, uint type, uint state, IntPtr buffer, uint size, out uint needed, out uint count, ref uint resume, string? group);
    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
    internal sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle);
    }
    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceEntry
    {
        internal IntPtr Name, DisplayName;
        internal uint Type, State, Controls, ExitCode, SpecificExitCode, CheckPoint, WaitHint, Pid, Flags;
    }
    internal delegate bool WindowCallback(IntPtr window, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(WindowCallback callback, IntPtr parameter);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")]
    internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, out int value, uint size, out uint required);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct StreamData
    {
        internal long Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] internal string Name;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern StreamHandle FindFirstStreamW(string path, int level, out StreamData data, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FindNextStreamW(StreamHandle handle, out StreamData data);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(IntPtr handle);
    internal sealed class StreamHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public StreamHandle() : base(true) { }
        protected override bool ReleaseHandle() => FindClose(handle);
    }
}
