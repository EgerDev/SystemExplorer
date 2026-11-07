using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text.RegularExpressions;
using SystemExplorer.Core;

namespace SystemExplorer.App.Views;

public sealed record HiddenScanResult(DateTimeOffset CapturedAt, string Summary, string Sources,
    IReadOnlyList<SuspiciousLead> Leads, bool Truncated, string Elevation = "");

public sealed partial class HiddenItemsView : UserControl
{
    private readonly Dictionary<int, HiddenScanResult> results = new();
    private bool ready, busy;
    public event Action<int>? ScanRequested;
    public event Action? CancelRequested;
    public event Action<ProcessIdentity>? InspectRequested;
    public event Action<HiddenExportDocument, string>? ExportRequested;
    public HiddenItemsView()
    {
        InitializeComponent();
        ready = true;
        Inventory.ActionRequested += row => { if (row.Value is ProcessIdentity identity) InspectRequested?.Invoke(identity); };
        ShowSelected();
    }
    public void SetElevation(string text) => Elevation.Text = text;
    private void HiddenSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (InventoryCard is null || PageScroll is null || LayoutRoot.ActualHeight == 0) return;
        double headerHeight = LayoutRoot.ActualHeight - InventoryCard.ActualHeight;
        double height = Math.Max(360, PageScroll.ActualHeight - headerHeight);
        if (Math.Abs(InventoryCard.Height - height) > 0.5) InventoryCard.Height = height;
        if (Inventory.FindName("DetailScroll") is ScrollViewer detail) detail.MaxHeight = Math.Clamp(height - 400, 70, 200);
    }
    public void SetBusy(bool value)
    {
        busy = value; Selector.IsEnabled = !value; RunButton.Content = value ? "Cancel" : "Run";
        ExportButton.IsEnabled = !value && results.ContainsKey(Selector.SelectedIndex);
        if (!value) ShowSelected();
    }
    public void Progress(string message) => CaptureStatus.Text = message;
    public void Complete(int scan, HiddenScanResult result) { results[scan] = result with { Elevation = Elevation.Text }; }
    private void ExportClicked(object sender, RoutedEventArgs args)
    {
        if (busy || !results.TryGetValue(Selector.SelectedIndex, out var result)) return;
        var document = HiddenItemsExport.Build((HiddenScanType)Selector.SelectedIndex, result.CapturedAt, result.Elevation, result.Summary, result.Sources, result.Leads, result.Truncated);
        ExportRequested?.Invoke(document, (string)((MenuFlyoutItem)sender).Tag);
    }
    private void RunClicked(object sender, RoutedEventArgs args) { if (busy) CancelRequested?.Invoke(); else ScanRequested?.Invoke(Selector.SelectedIndex); }
    private void ScanChanged(object sender, SelectionChangedEventArgs args) { if (ready) ShowSelected(); }
    private static string Readable(string text) => Regex.Replace(text, @"(?<![\\/])\b(?:AccessDenied|TimedOut|IdentityMismatch|StartedDuringScan|ExitedDuringScan|PidReused|ProtectedDenied|StaleOwner|NtSystemInformation|NtGetNextProcess|PidProbe|EtwOwner|TcpOwner|UdpOwner|ServiceOwner|NotTrusted|NotChecked|NoVisibleWindow|UnusualLocation|ParentMismatch|EndpointOwner|AlternateDataStream|Temp|AppData|ProgramDataRoot|RecycleBin|Public)\b", match => match.Value switch
    {
        "AccessDenied" => "Denied",
        "TimedOut" => "Timed out",
        "IdentityMismatch" => "Process lifetime changed",
        "StartedDuringScan" => "Started during scan",
        "ExitedDuringScan" => "Exited during scan",
        "PidReused" => "PID reused",
        "ProtectedDenied" => "Protected / denied",
        "StaleOwner" => "Stale owner",
        "NtSystemInformation" => "System process list",
        "NtGetNextProcess" => "Limited-query enumeration",
        "PidProbe" => "PID probes",
        "EtwOwner" => "ETW owners",
        "TcpOwner" => "TCP owners",
        "UdpOwner" => "UDP owners",
        "ServiceOwner" => "Service owners",
        "NotTrusted" => "Not trusted",
        "NotChecked" => "Not checked",
        "Temp" => "Temporary folder",
        "AppData" => "Application data folder",
        "Public" => "Public user folder",
        "ProgramDataRoot" => "Program data root",
        "RecycleBin" => "Recycle bin",
        _ => Regex.Replace(match.Value, "([a-z])([A-Z])", "$1 $2").ToLowerInvariant()
    });
    private string CategoryLabel(LeadCategory category) => category switch
    {
        LeadCategory.CrossView => Selector.SelectedIndex == 1 ? "Service" : Selector.SelectedIndex == 2 ? "Scheduled task" : "Cross-view process",
        LeadCategory.Signature => "Signature",
        LeadCategory.UnusualLocation => "Unusual location",
        LeadCategory.Masquerading => "Masquerading",
        LeadCategory.ParentMismatch => "Parent relationship",
        LeadCategory.EndpointOwner => "Listening port",
        LeadCategory.Persistence => "Persistence",
        LeadCategory.NoVisibleWindow => "No visible window",
        LeadCategory.AlternateDataStream => "Alternate data stream",
        _ => "Observation"
    };
    private void ShowSelected()
    {
        ExportButton.IsEnabled = !busy && results.ContainsKey(Selector.SelectedIndex);
        if (!results.TryGetValue(Selector.SelectedIndex, out var result))
        {
            High.Value = Medium.Value = Low.Value = Info.Value = "0";
            CaptureStatus.Text = "Not run yet — click Run";
            Sources.Text = "Read-only, local observations. Leads are not proof of compromise. Use the filter below after a scan; global search does not change these frozen results.";
            Inventory.ShowMessage("Not run yet — click Run");
            return;
        }
        High.Value = result.Leads.Count(l => l.Severity == LeadSeverity.High).ToString();
        Medium.Value = result.Leads.Count(l => l.Severity == LeadSeverity.Medium).ToString();
        Low.Value = result.Leads.Count(l => l.Severity == LeadSeverity.Low).ToString();
        Info.Value = result.Leads.Count(l => l.Severity == LeadSeverity.Info).ToString();
        CaptureStatus.Text = $"Captured at {result.CapturedAt.ToLocalTime():HH:mm:ss} · {result.Summary}" + (result.Truncated ? " · Truncated to bounds" : "");
        Sources.Text = Readable(result.Sources);
        var rows = result.Leads.Select((lead, index) => new InventoryRow(Selector.SelectedIndex == 0 ? Readable(lead.Title) : lead.Title, Selector.SelectedIndex == 2 ? lead.BenignNote : Selector.SelectedIndex == 4 ? lead.Reason : lead.Path ?? Readable(lead.Reason), lead.Severity.ToString(),
            CategoryLabel(lead.Category), (Selector.SelectedIndex == 0 ? Readable(lead.Evidence) : lead.Evidence).Replace('\n', ' '), Number1: (int)lead.Severity,
            FullText: $"{(Selector.SelectedIndex == 0 ? Readable(lead.Title) : lead.Title)}\nSeverity: {lead.Severity} · {CategoryLabel(lead.Category)}\nReason: {Readable(lead.Reason)}\nEvidence: {(Selector.SelectedIndex == 0 ? Readable(lead.Evidence) : lead.Evidence)}\nWhy this may be benign: {lead.BenignNote}" +
                (lead.ProcessIdentity is { } identity ? $"\nProcess identity: {identity}" : "") + (lead.Path is { } path ? $"\nPath: {path}" : ""),
            Value: lead.ProcessIdentity, Key: index.ToString()));
        Inventory.ShowRows("Frozen local observation; leads are not proof of compromise.", rows, "Name", "Severity", "Category / kind", "Evidence", "Inspect process", initialSort: 1, reverse: true);
    }
}
