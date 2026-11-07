using System.ComponentModel;
using System.Runtime.InteropServices;

namespace SystemExplorer.App;

internal static class TraceSaveDialog
{
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
    internal static string? Choose(nint owner, string format)
    {
        var options = new OpenFileName
        {
            Owner = owner, Title = "Export filtered activity trace", DefaultExtension = format,
            Filter = format == "csv" ? "CSV files (*.csv)\0*.csv\0\0" : "JSON files (*.json)\0*.json\0\0"
        };
        options.File = Marshal.AllocHGlobal(options.MaxFile * sizeof(char));
        try
        {
            var name = ($"SystemExplorer-trace-{DateTime.Now:yyyyMMdd-HHmmss}.{format}\0").ToCharArray();
            Marshal.Copy(name, 0, options.File, name.Length);
            if (GetSaveFileName(options)) return Marshal.PtrToStringUni(options.File);
            uint error = CommDlgExtendedError();
            if (error != 0) throw new Win32Exception((int)error);
            return null;
        }
        finally { Marshal.FreeHGlobal(options.File); }
    }
}
