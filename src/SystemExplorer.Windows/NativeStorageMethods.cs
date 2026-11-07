using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SystemExplorer.Windows;

internal static class NativeStorageMethods
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern uint GetLogicalDriveStringsW(uint size, [Out] char[] buffer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] internal static extern uint GetDriveTypeW(string root);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GetVolumeInformationW(string root, StringBuilder label, uint labelSize, out uint serial, out uint maxComponent, out uint flags, StringBuilder fs, uint fsSize);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GetDiskFreeSpaceExW(string root, out long available, out long total, out long free);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool GetDiskFreeSpaceW(string root, out uint sectors, out uint bytes, out uint free, out uint total);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern SafeFileHandle CreateFileW(string path, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize, byte[] output, int outputSize, out int returned, IntPtr overlapped);
    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, IntPtr buffer, uint size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern IntPtr FindFirstFileExW(string pattern, int level, out FindData data, int search, IntPtr filter, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    internal static extern bool FindNextFileW(IntPtr handle, out FindData data);
    [DllImport("kernel32.dll")] internal static extern bool FindClose(IntPtr handle);
    [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentThread();
    [DllImport("kernel32.dll", SetLastError = true)] internal static extern bool SetThreadPriority(IntPtr thread, int priority);
    [DllImport("kernel32.dll")] internal static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool OpenProcessToken(IntPtr process, uint access, out SafeFileHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] internal static extern bool GetTokenInformation(SafeFileHandle token, int info, out int value, int size, out int returned);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct FindData
    {
        internal uint Attributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        internal uint SizeHigh, SizeLow, Reserved0, Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] internal string Alternate;
    }
}
