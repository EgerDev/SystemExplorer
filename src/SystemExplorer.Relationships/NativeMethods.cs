using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

namespace SystemExplorer.Relationships;

internal static class NativeMethods
{
    [DllImport("iphlpapi.dll")]
    internal static extern uint GetExtendedTcpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
        uint family, int tableClass, uint reserved);

    [DllImport("iphlpapi.dll")]
    internal static extern uint GetExtendedUdpTable(IntPtr table, ref uint size, [MarshalAs(UnmanagedType.Bool)] bool order,
        uint family, int tableClass, uint reserved);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeServiceHandle OpenSCManagerW(string? machine, string? database, uint access);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeServiceHandle OpenServiceW(SafeServiceHandle manager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumServicesStatusExW(SafeServiceHandle manager, int level, uint type, uint state,
        IntPtr services, uint bufferSize, out uint bytesNeeded, out uint returned, ref uint resume, string? group);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryServiceConfigW(SafeServiceHandle service, IntPtr buffer, uint bufferSize, out uint bytesNeeded);

    [DllImport("advapi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseServiceHandle(IntPtr handle);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    internal static extern int WinVerifyTrust(IntPtr window, ref Guid action, ref WinTrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    internal static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    internal static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr provider, uint signer,
        [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);

    [DllImport("crypt32.dll", CharSet = CharSet.Unicode)]
    internal static extern uint CertGetNameStringW(IntPtr certificate, uint type, uint flags, IntPtr parameter,
        StringBuilder? name, uint size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ServiceEntry
    {
        internal IntPtr Name, DisplayName;
        internal uint Type, State, ControlsAccepted, Win32ExitCode, ServiceExitCode, CheckPoint, WaitHint, Pid, Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct QueryServiceConfig
    {
        internal uint ServiceType, StartType, ErrorControl;
        internal IntPtr BinaryPathName, LoadOrderGroup;
        internal uint TagId;
        internal IntPtr Dependencies, ServiceStartName, DisplayName;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinTrustFileInfo
    {
        internal uint Size;
        internal IntPtr Path, File, KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WinTrustData
    {
        internal uint Size;
        internal IntPtr PolicyCallback, SipClient;
        internal uint UiChoice, RevocationChecks, UnionChoice;
        internal IntPtr FileInfo;
        internal uint StateAction;
        internal IntPtr State, UrlReference;
        internal uint ProviderFlags, UiContext;
        internal IntPtr SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProviderSignerPrefix
    {
        internal uint Size, VerifyTimeLow, VerifyTimeHigh, CertificateCount;
        internal IntPtr Certificates;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProviderCertificatePrefix
    {
        internal uint Size;
        internal IntPtr Certificate;
    }
}

internal sealed class SafeServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeServiceHandle() : base(true) { }
    protected override bool ReleaseHandle() => NativeMethods.CloseServiceHandle(handle);
}
