using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SystemExplorer.Windows;

internal static class NativeSecurityMethods
{
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern uint WaitForSingleObject(SafeProcessHandle process, uint milliseconds);
    [DllImport("ntdll.dll")]
    internal static extern int NtQueryInformationProcess(SafeProcessHandle process, int kind, IntPtr buffer, uint size, out uint required);
    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)]
    internal static extern bool GetTokenInformation(SafeAccessTokenHandle token, int kind, IntPtr buffer, uint size, out uint required);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern bool ConvertSidToStringSidW(IntPtr sid, out IntPtr text);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern bool LookupAccountSidW(string? system, IntPtr sid, StringBuilder? name, ref uint nameSize,
        StringBuilder? domain, ref uint domainSize, out int use);
    [StructLayout(LayoutKind.Sequential)]
    internal struct Luid { internal uint Low; internal int High; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    internal static extern bool LookupPrivilegeNameW(string? system, in Luid luid, StringBuilder? name, ref uint size);
    [DllImport("kernel32.dll")]
    internal static extern IntPtr LocalFree(IntPtr memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int GetPackageFamilyName(SafeProcessHandle process, ref uint size, StringBuilder? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessInformation(SafeProcessHandle process, int kind, out uint information, uint size);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetProcessMitigationPolicy(SafeProcessHandle process, int kind, [Out] byte[] buffer, UIntPtr size);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool IsProcessInJob(SafeProcessHandle process, IntPtr job, out bool inJob);
    internal static readonly Guid VerifyAction = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TrustFile
    {
        internal uint Size;
        [MarshalAs(UnmanagedType.LPWStr)] internal string Path;
        internal IntPtr File, Subject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct TrustCatalog
    {
        internal uint Size, Version;
        [MarshalAs(UnmanagedType.LPWStr)] internal string CatalogPath;
        [MarshalAs(UnmanagedType.LPWStr)] internal string MemberTag;
        [MarshalAs(UnmanagedType.LPWStr)] internal string MemberPath;
        internal IntPtr File, Hash;
        internal uint HashSize;
        internal IntPtr CatalogContext, Admin;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct TrustData
    {
        internal uint Size;
        internal IntPtr Policy, Sip;
        internal uint Ui, Revocation, Choice;
        internal IntPtr Information;
        internal uint StateAction;
        internal IntPtr State, Url;
        internal uint Flags, UiContext;
        internal IntPtr SignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct CatalogInfo
    {
        internal uint Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Path;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProviderCertificate
    {
        internal uint Size;
        internal IntPtr Certificate;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    internal static extern int WinVerifyTrust(IntPtr window, in Guid action, ref TrustData data);
    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptCATAdminAcquireContext2(out IntPtr admin, IntPtr subsystem, string algorithm, IntPtr policy, uint flags);
    [DllImport("wintrust.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr admin, IntPtr file, ref uint size, [Out] byte[]? hash, uint flags);
    [DllImport("wintrust.dll", SetLastError = true)]
    internal static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr admin, byte[] hash, uint size, uint flags, IntPtr previous);
    [DllImport("wintrust.dll", EntryPoint = "CryptCATCatalogInfoFromContext", ExactSpelling = true, CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptCATAdminCatalogInfoFromContext(IntPtr catalog, ref CatalogInfo info, uint flags);
    [DllImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptCATAdminReleaseCatalogContext(IntPtr admin, IntPtr catalog, uint flags);
    [DllImport("wintrust.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CryptCATAdminReleaseContext(IntPtr admin, uint flags);
    [DllImport("wintrust.dll")]
    internal static extern IntPtr WTHelperProvDataFromStateData(IntPtr state);
    [DllImport("wintrust.dll")]
    internal static extern IntPtr WTHelperGetProvSignerFromChain(IntPtr data, uint signer, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterIndex);
    [DllImport("wintrust.dll")]
    internal static extern IntPtr WTHelperGetProvCertFromChain(IntPtr signer, uint index);
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint CertGetNameStringW(IntPtr certificate, uint type, uint flags, IntPtr parameter, StringBuilder? name, uint size);
}
