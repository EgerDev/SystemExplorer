using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SystemExplorer.Core;

namespace SystemExplorer.App.Views;

public sealed partial class IoAttributionView : UserControl
{
    private readonly IoAttributionCapture capture;
    private int sort;
    private bool descending;

    public IoAttributionView(IoAttributionCapture capture)
    {
        InitializeComponent();
        this.capture = capture;
        Rebuild();
    }

    private void SortClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string tag } || !int.TryParse(tag, out var column)) return;
        descending = column == sort ? !descending : column > 0;
        sort = column;
        Rebuild();
    }

    private void Rebuild()
    {
        decimal Number(IoAttributionProcess row) => sort switch
        {
            1 => row.Pid, 2 => row.NetworkSendBytes, 3 => row.NetworkReceiveBytes,
            4 => row.DiskReadBytes, 5 => row.DiskWriteBytes, _ => row.EventCount
        };
        var rows = sort == 0
            ? descending ? capture.Processes.OrderBy(r => r.Name is null).ThenByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase) : capture.Processes.OrderBy(r => r.Name is null).ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            : descending ? capture.Processes.OrderByDescending(Number) : capture.Processes.OrderBy(Number);
        var seconds = capture.ActualDuration.TotalSeconds;
        string Rate(ulong bytes) => seconds > 0 ? Display.Rate(bytes / seconds) : "Unavailable";
        Rows.ItemsSource = rows.Select(r => new
        {
            Name = r.Name ?? "Name unavailable", r.Pid,
            Identity = r.Identity is { IsStable: true } identity ? $"Creation: {identity.CreationTimeFileTime}" : "Unstable identity",
            Send = Rate(r.NetworkSendBytes), Receive = Rate(r.NetworkReceiveBytes), Read = Rate(r.DiskReadBytes), Write = Rate(r.DiskWriteBytes), Events = r.EventCount
        }).ToArray();
        var label = capture.Status == IoAttributionStatus.Completed ? "Measured" : "Partial / unavailable";
        var headers = new[] { "Process", "PID", "Net send/s", "Net recv/s", "Disk read/s", "Disk write/s", "Events" };
        Status.Text = $"{label}: {capture.Status} · actual duration {seconds:F3} s · events lost {capture.EventsLost}\n{capture.Detail}\n{capture.Processes.Count} processes ({capture.Processes.Count(r => r.Identity is { IsStable: true })} stable identities) · {headers[sort]} {(descending ? "descending" : "ascending")}. Rates = observed bytes / actual duration; no observations is not proof of no I/O.";
    }
}
