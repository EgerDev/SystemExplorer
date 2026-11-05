using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using SystemExplorer.Core;

namespace SystemExplorer.App;

public sealed class NativeTray : IDisposable
{
    private const uint CallbackMessage = 0x8000 + 0x531;
    private const nuint SubclassId = 0x535845;
    private readonly Window window;
    private readonly SubclassProcedure procedure;
    private readonly uint taskbarCreated;
    private NotifyIconData data;
    private bool disposed;
    private bool registered;

    public NativeTray(Window window)
    {
        this.window = window;
        VerifyThread();
        procedure = WindowMessage;
        taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        data = new NotifyIconData
        {
            Size = (uint)Marshal.SizeOf<NotifyIconData>(),
            Window = WinRT.Interop.WindowNative.GetWindowHandle(window),
            Id = 1,
            Flags = 1 | 2 | 4 | 0x80,
            Callback = CallbackMessage,
            Icon = LoadIcon(IntPtr.Zero, new IntPtr(32512)),
            Tip = "SystemExplorer — waiting for measurements",
            Info = "",
            InfoTitle = ""
        };
        if (!SetWindowSubclass(data.Window, procedure, SubclassId, 0))
            throw new InvalidOperationException("Could not attach the tray notification callback.");
        window.Closed += WindowClosed;
        registered = AddIcon();
    }

    public bool IsAvailable => !disposed && registered;

    public bool Update(MonitorSnapshot snapshot, bool paused = false)
    {
        VerifyThread();
        if (disposed) return false;
        var cpu = snapshot.TotalCpuPercent is { } value && double.IsFinite(value) ? $"{value:F1}%" : "unavailable";
        var memory = snapshot.Memory.PhysicalUsedBytes is { } used ? $"{used / (1024d * 1024 * 1024):F1} GiB" : "unavailable";
        data.Tip = $"SystemExplorer{(paused ? " (paused)" : "")}\nCPU {cpu} · Physical used {memory}\nSample {snapshot.CapturedAt.LocalDateTime:T}";
        if (data.Tip.Length > 127) data.Tip = data.Tip[..127];
        if (!registered) registered = AddIcon();
        return registered && ShellNotifyIcon(1, ref data);
    }

    private bool AddIcon()
    {
        if (!ShellNotifyIcon(0, ref data)) return false;
        data.Version = 4;
        if (ShellNotifyIcon(4, ref data)) return true;
        ShellNotifyIcon(2, ref data);
        return false;
    }

    private nint WindowMessage(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint reference)
    {
        if (!disposed && message == taskbarCreated && taskbarCreated != 0)
            registered = AddIcon();
        if (!disposed && message == CallbackMessage)
        {
            var notification = (uint)((long)lParam & 0xffff);
            if (notification is 0x400 or 0x401)
                window.DispatcherQueue.TryEnqueue(() =>
                {
                    if (disposed) return;
                    ShowWindow(data.Window, 9);
                    window.Activate();
                    SetForegroundWindow(data.Window);
                });
        }
        if (message == 0x82) Dispose();
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void WindowClosed(object sender, WindowEventArgs args) => Dispose();

    public void Dispose()
    {
        VerifyThread();
        if (disposed) return;
        disposed = true;
        ShellNotifyIcon(2, ref data);
        RemoveWindowSubclass(data.Window, procedure, SubclassId);
        window.Closed -= WindowClosed;
        registered = false;
        GC.KeepAlive(procedure);
    }

    private void VerifyThread()
    {
        if (!window.DispatcherQueue.HasThreadAccess)
            throw new InvalidOperationException("Use NativeTray on the owning window UI thread.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NotifyIconData
    {
        public uint Size;
        public nint Window;
        public uint Id;
        public uint Flags;
        public uint Callback;
        public nint Icon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Tip;
        public uint State;
        public uint StateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string InfoTitle;
        public uint InfoFlags;
        public Guid Guid;
        public nint BalloonIcon;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint SubclassProcedure(nint hwnd, uint message, nuint wParam, nint lParam, nuint id, nuint reference);
    [DllImport("shell32.dll", EntryPoint = "Shell_NotifyIconW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShellNotifyIcon(uint message, ref NotifyIconData data);
    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern nint LoadIcon(nint instance, nint name);
    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessage(string name);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowSubclass(nint hwnd, SubclassProcedure procedure, nuint id, nuint reference);
    [DllImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveWindowSubclass(nint hwnd, SubclassProcedure procedure, nuint id);
    [DllImport("comctl32.dll")]
    private static extern nint DefSubclassProc(nint hwnd, uint message, nuint wParam, nint lParam);
}
