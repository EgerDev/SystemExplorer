using System.Runtime.InteropServices;
using System.Text;

namespace SystemExplorer.Relationships;

internal static class NativeServiceMethods
{
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int RegOpenKeyExW(IntPtr root, string path, uint options, uint access, out IntPtr key);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int RegEnumKeyExW(IntPtr key, uint index, StringBuilder name, ref uint length, IntPtr reserved, IntPtr className, IntPtr classLength, IntPtr lastWrite);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int RegEnumValueW(IntPtr key, uint index, StringBuilder name, ref uint length, IntPtr reserved, IntPtr type, IntPtr data, IntPtr dataSize);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int RegQueryValueExW(IntPtr key, string name, IntPtr reserved, out uint type, byte[] data, ref uint size);
    [DllImport("advapi32.dll", ExactSpelling = true)]
    internal static extern int RegCloseKey(IntPtr key);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern SafeServiceHandle OpenSCManagerW(string? machine, string? database, uint access);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern SafeServiceHandle OpenServiceW(SafeServiceHandle manager, string name, uint access);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceConfigW(SafeServiceHandle service, IntPtr buffer, uint capacity, out uint needed);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumServicesStatusExW(SafeServiceHandle manager, int level, uint type, uint state,
        IntPtr buffer, uint capacity, out uint needed, out uint count, ref uint resume, string? group);
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceConfig2W(SafeServiceHandle service, uint level, IntPtr buffer, uint capacity, out uint needed);

    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumDependentServicesW(SafeServiceHandle service, uint state, IntPtr buffer, uint capacity, out uint needed, out uint count);
}

[StructLayout(LayoutKind.Sequential)]
internal struct ServiceTriggerInfo
{
    internal uint Count;
    internal IntPtr Triggers, Reserved;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ServiceTriggerEntry
{
    internal uint Type, Action;
    internal IntPtr Subtype;
    internal uint DataCount;
    internal IntPtr Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ServiceDependentEntry
{
    internal IntPtr Name, DisplayName;
    internal uint Type, State, Controls, ExitCode, SpecificExitCode, CheckPoint, WaitHint;
}
