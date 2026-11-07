using System.Runtime.InteropServices;
using System.Text;
using SystemExplorer.App;

internal static class NativeDialogTests
{
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string path, StringBuilder shortened, uint length);

    internal static void Run()
    {
        static void Require(bool value) { if (!value) throw new InvalidOperationException("Native dialog regression failed."); }
        using (var first = NativeFileDialogs.TryBegin())
        {
            Require(first is not null && NativeFileDialogs.TryBegin() is null);
            Require(NativeFileDialogs.ChooseFolder(0, "must not open") is null);
            Require(NativeFileDialogs.ChooseSave(0, "csv", "must not open", "test") is null);
            first!.Dispose();
            using var second = NativeFileDialogs.TryBegin();
            first.Dispose();
            Require(second is not null && NativeFileDialogs.TryBegin() is null);
        }
        try
        {
            using var lease = NativeFileDialogs.TryBegin();
            Require(lease is not null);
            throw new OperationCanceledException();
        }
        catch (OperationCanceledException) { }
        using (var afterError = NativeFileDialogs.TryBegin()) Require(afterError is not null);
        bool invalidFormat = false;
        try { NativeFileDialogs.ChooseSave(0, "exe", "must not open", "test"); }
        catch (ArgumentOutOfRangeException) { invalidFormat = true; }
        Require(invalidFormat);
        string directory = Path.Combine(Path.GetTempPath(), "SystemExplorer picker regression " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var canonical = NativeFileDialogs.NormalizeExistingPath(directory);
            var shortPath = new StringBuilder(32768);
            uint length = GetShortPathNameW(directory, shortPath, (uint)shortPath.Capacity);
            Require(length > 0 && length < shortPath.Capacity);
            Require(NativeFileDialogs.NormalizeExistingPath(shortPath.ToString()) == canonical);
            bool missing = false;
            try { NativeFileDialogs.NormalizeExistingPath(Path.Combine(directory, "missing")); }
            catch (System.ComponentModel.Win32Exception) { missing = true; }
            Require(missing);
        }
        finally { Directory.Delete(directory); }
        Console.WriteLine("PASS native dialog non-GUI checks: shared reentrancy, cancellation/error release, format validation, long/8.3 Temp paths and missing paths");
    }
}
