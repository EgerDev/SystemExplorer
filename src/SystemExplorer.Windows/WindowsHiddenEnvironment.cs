using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using SystemExplorer.Core;

namespace SystemExplorer.Windows;

public sealed record HiddenElevationState(InspectionStatus Status, bool? IsElevated, string Detail);
public sealed record VisibleWindowOwners(InspectionStatus Status, string Detail, BoundedItems<int> Pids);

public static class WindowsHiddenEnvironment
{
    public static HiddenElevationState GetElevation()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (HiddenNativeMethods.GetTokenInformation(identity.AccessToken, 20, out int elevated, 4, out _))
        {
            bool administrator = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            bool fullToken = elevated != 0 && administrator;
            return new(InspectionStatus.Available, fullToken, fullToken ? "Token is elevated with administrator role enabled."
                : "Full coverage requires administrator; the current token is filtered or not elevated. No elevation attempted.");
        }
        int error = Marshal.GetLastWin32Error();
        return new(error == 5 ? InspectionStatus.AccessDenied : InspectionStatus.Failed, null, $"Token elevation unavailable (Windows error {error}); administrator coverage cannot be established.");
    }

    public static VisibleWindowOwners GetVisibleWindowOwners(CancellationToken cancellationToken, int cap = 8192)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(cap);
        cancellationToken.ThrowIfCancellationRequested();
        var pids = new HashSet<int>();
        int visited = 0;
        bool truncated = false;
        bool ok = HiddenNativeMethods.EnumWindows((window, _) =>
        {
            if (cancellationToken.IsCancellationRequested) return false;
            if (++visited > 32768) { truncated = true; return false; }
            if (!HiddenNativeMethods.IsWindowVisible(window)) return true;
            HiddenNativeMethods.GetWindowThreadProcessId(window, out uint pid);
            if (pid > 0 && pid <= int.MaxValue) pids.Add((int)pid);
            if (pids.Count > cap) { truncated = true; return false; }
            return true;
        }, IntPtr.Zero);
        cancellationToken.ThrowIfCancellationRequested();
        int error = ok || truncated ? 0 : Marshal.GetLastWin32Error();
        return new(truncated ? InspectionStatus.Truncated : ok ? InspectionStatus.Available : error == 5 ? InspectionStatus.AccessDenied : InspectionStatus.Failed,
            ok || truncated ? "Visible top-level windows on the current desktop only; other sessions/desktops are not covered." : new Win32Exception(error).Message,
            new(pids.Order(), cap, truncated));
    }
}
