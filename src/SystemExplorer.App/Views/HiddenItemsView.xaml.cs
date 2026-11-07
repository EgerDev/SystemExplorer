using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SystemExplorer.Core;

namespace SystemExplorer.App.Views;

public sealed record HiddenScanResult(DateTimeOffset CapturedAt, string Summary, string Sources,
    IReadOnlyList<SuspiciousLead> Leads, bool Truncated);

public sealed partial class HiddenItemsView : UserControl
{
    private readonly Dictionary<int, HiddenScanResult> results = new();
    private bool ready, busy;
    public event Action<int>? ScanRequested;
    public event Action? CancelRequested;
    public event Action<ProcessIdentity>? InspectRequested;
    public HiddenItemsView()
    {
        InitializeComponent();
        ready = true;
        Inventory.ActionRequested += row => { if (row.Value is ProcessIdentity identity) InspectRequested?.Invoke(identity); };
        ShowSelected();
    }
    public void SetElevation(string text) => Elevation.Text = text;
    private void HiddenSizeChanged(object sender, SizeChangedEventArgs args) => LayoutRoot.Height = Math.Max(640, args.NewSize.Height);
    public void SetBusy(bool value)
    {
        busy = value; Selector.IsEnabled = !value; RunButton.Content = value ? "Cancel" : "Run";
        if (!value) ShowSelected();
    }
    public void Progress(string message) => CaptureStatus.Text = message;
    public void Complete(int scan, HiddenScanResult result) { results[scan] = result; }
    private void RunClicked(object sender, RoutedEventArgs args) { if (busy) CancelRequested?.Invoke(); else ScanRequested?.Invoke(Selector.SelectedIndex); }
    private void ScanChanged(object sender, SelectionChangedEventArgs args) { if (ready) ShowSelected(); }
    private void ShowSelected()
    {
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
        Sources.Text = result.Sources;
        var rows = result.Leads.Select((lead, index) => new InventoryRow(lead.Title, Selector.SelectedIndex == 4 ? lead.Reason : lead.Path ?? lead.Reason, lead.Severity.ToString(),
            lead.Category.ToString(), lead.Evidence.Replace('\n', ' '), Number1: (int)lead.Severity,
            FullText: $"{lead.Title}\nSeverity: {lead.Severity} · {lead.Category}\nReason: {lead.Reason}\nEvidence: {lead.Evidence}\nWhy this may be benign: {lead.BenignNote}" +
                (lead.ProcessIdentity is { } identity ? $"\nProcess identity: {identity}" : "") + (lead.Path is { } path ? $"\nPath: {path}" : ""),
            Value: lead.ProcessIdentity, Key: index.ToString()));
        Inventory.ShowRows("Frozen local observation; leads are not proof of compromise.", rows, "Name", "Severity", "Category / kind", "Evidence", "Inspect process", initialSort: 1, reverse: true);
    }
}
