using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

// Read-only interop feasibility probe, not the proposed production provider.
internal static class Enumerate
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size, Usage, Pid;
        public UIntPtr DefaultHeap;
        public uint Module, Threads, ParentPid;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string Name;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileTime
    {
        public uint Low, High;
        public ulong Value { get { return ((ulong)High << 32) | Low; } }
    }

    private sealed class NativeHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public NativeHandle() : base(true) { }
        protected override bool ReleaseHandle() { return CloseHandle(handle); }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern NativeHandle CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32FirstW(NativeHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32NextW(NativeHandle snapshot, ref ProcessEntry entry);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern NativeHandle OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(NativeHandle process, out FileTime created,
        out FileTime exited, out FileTime kernel, out FileTime user);
    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessId();

    private static int Main()
    {
        try
        {
            if (IntPtr.Size != 8 || Marshal.SizeOf(typeof(ProcessEntry)) != 568)
                throw new InvalidOperationException("Expected Windows x64 PROCESSENTRY32W layout.");
            int count = 0, known = 0, denied = 0;
            bool foundSelf = false;
            HashSet<uint> seen = new HashSet<uint>();
            uint self = GetCurrentProcessId();
            Console.WriteLine("pid\tcreation_filetime_100ns\tidentity_error_win32");
            using (NativeHandle snapshot = CreateToolhelp32Snapshot(2, 0))
            {
                if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
                ProcessEntry entry = new ProcessEntry();
                entry.Size = (uint)Marshal.SizeOf(typeof(ProcessEntry));
                if (!Process32FirstW(snapshot, ref entry))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                do
                {
                    if (!seen.Add(entry.Pid)) throw new InvalidOperationException("Duplicate PID in snapshot.");
                    count++;
                    ulong? creation = null;
                    int error = 0;
                    using (NativeHandle process = OpenProcess(0x1000, false, entry.Pid))
                    {
                        FileTime c, e, k, u;
                        if (process.IsInvalid) error = Marshal.GetLastWin32Error();
                        else if (GetProcessTimes(process, out c, out e, out k, out u)) creation = c.Value;
                        else error = Marshal.GetLastWin32Error();
                    }
                    if (creation.HasValue) known++;
                    if (error == 5) denied++;
                    if (entry.Pid == self && creation.HasValue && creation.Value != 0) foundSelf = true;
                    Console.WriteLine("{0}\t{1}\t{2}", entry.Pid,
                        creation.HasValue ? creation.Value.ToString() : "null", error);
                } while (Process32NextW(snapshot, ref entry));
                int finalError = Marshal.GetLastWin32Error();
                if (finalError != 18) throw new Win32Exception(finalError);
            }
            if (!foundSelf || count == 0) throw new InvalidOperationException("Live self-identity check failed.");
            Console.Error.WriteLine("PASS: x64 layout; unique snapshot PIDs; live self creation time; enumeration complete. rows={0}, known_identity={1}, access_denied={2}", count, known, denied);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
