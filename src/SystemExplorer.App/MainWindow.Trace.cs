using SystemExplorer.Core;

namespace SystemExplorer.App;

public sealed partial class MainWindow
{
    private void InitializeTrace()
    {
        TracePanel.Initialize(WinRT.Interop.WindowNative.GetWindowHandle(this),
            () => Model.Snapshot?.Processes.Select(p => new TraceProcessSeed(p.Identity.Pid, p.ParentPid ?? -1,
                p.Identity.CreationTimeFileTime, p.Name, p.ExecutablePath)).ToArray() ?? [],
            identity => identity.IsStable && Model.Snapshot is { } snapshot && DateTimeOffset.UtcNow - snapshot.CapturedAt < TimeSpan.FromSeconds(6)
                && snapshot.Processes.Any(p => p.Identity == identity),
            InspectOverviewProcess);
    }
}
