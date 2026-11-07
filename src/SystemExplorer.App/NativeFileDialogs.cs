using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SystemExplorer.App;

internal static class NativeFileDialogs
{
    private static int active;

    internal static IDisposable? TryBegin()
    {
        return Interlocked.CompareExchange(ref active, 1, 0) == 0 ? new DialogLease() : null;
    }

    private sealed class DialogLease : IDisposable
    {
        private int disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Exchange(ref active, 0);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class BrowseInfo
    {
        public nint Owner, Root, DisplayName;
        public string Title = "Select a folder";
        public uint Flags = 0x1 | 0x10 | 0x40 | 0x200;
        public nint Callback, Parameter;
        public int Image;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHBrowseForFolderW([In, Out] BrowseInfo info);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SHGetPathFromIDListEx(nint item, StringBuilder path, uint length, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string path, StringBuilder expanded, uint length);

    internal static string NormalizeExistingPath(string path)
    {
        var full = Path.GetFullPath(path);
        var expanded = new StringBuilder(32768);
        uint length = GetLongPathNameW(full, expanded, (uint)expanded.Capacity);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot resolve selected path: " + full);
        if (length >= expanded.Capacity) throw new PathTooLongException("Selected path exceeds the dialog path limit.");
        return expanded.ToString();
    }

    internal static string? ChooseFolder(nint owner, string title)
    {
        using var lease = TryBegin();
        if (lease is null) return null;
        var info = new BrowseInfo { Owner = owner, Title = title, DisplayName = Marshal.AllocHGlobal(32768 * sizeof(char)) };
        nint item = 0;
        try
        {
            item = SHBrowseForFolderW(info);
            if (item == 0) return null;
            var path = new StringBuilder(32768);
            if (!SHGetPathFromIDListEx(item, path, (uint)path.Capacity, 0))
                throw new IOException("Folder selection did not return a filesystem path.");
            return NormalizeExistingPath(path.ToString());
        }
        finally
        {
            if (item != 0) Marshal.FreeCoTaskMem(item);
            Marshal.FreeHGlobal(info.DisplayName);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private sealed class OpenFileName
    {
        public int Size = Marshal.SizeOf<OpenFileName>();
        public nint Owner, Instance;
        public string? Filter;
        public nint CustomFilter;
        public int MaxCustomFilter, FilterIndex = 1;
        public nint File;
        public int MaxFile = 32768;
        public nint FileTitle;
        public int MaxFileTitle;
        public string? InitialDirectory, Title;
        public int Flags = 0x80000 | 0x800 | 0x8 | 0x2;
        public short FileOffset, FileExtension;
        public string? DefaultExtension;
        public nint CustomData, Hook;
        public string? TemplateName;
        public nint Reserved;
        public int ReservedSize, FlagsEx;
    }
    [DllImport("comdlg32.dll", EntryPoint = "GetSaveFileNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileName([In, Out] OpenFileName options);
    [DllImport("comdlg32.dll")]
    private static extern uint CommDlgExtendedError();
    internal static string? ChooseSave(nint owner, string format, string title, string suggestedName)
    {
        if (format is not ("csv" or "json")) throw new ArgumentOutOfRangeException(nameof(format));
        using var lease = TryBegin();
        if (lease is null) return null;
        var options = new OpenFileName
        {
            Owner = owner, Title = title, DefaultExtension = format,
            Filter = format == "csv" ? "CSV files (*.csv)\0*.csv\0\0" : "JSON files (*.json)\0*.json\0\0"
        };
        options.File = Marshal.AllocHGlobal(options.MaxFile * sizeof(char));
        try
        {
            var name = ($"{suggestedName}.{format}\0").ToCharArray();
            if (name.Length > options.MaxFile) throw new PathTooLongException("Suggested filename exceeds the dialog path limit.");
            Marshal.Copy(name, 0, options.File, name.Length);
            if (GetSaveFileName(options))
            {
                string path = Marshal.PtrToStringUni(options.File) ?? throw new IOException("Save dialog returned no path.");
                return Path.Combine(NormalizeExistingPath(Path.GetDirectoryName(path)!), Path.GetFileName(path));
            }
            uint error = CommDlgExtendedError();
            if (error != 0) throw new Win32Exception((int)error);
            return null;
        }
        finally { Marshal.FreeHGlobal(options.File); }
    }
}
