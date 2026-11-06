using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SystemExplorer.Core;

namespace SystemExplorer.App.Views;

public sealed record InspectionRow(string Name, string Detail, ulong Address, ulong Size)
{
    public string AddressText => $"0x{Address:X16}";
    public string SizeText => DiagnosticData.Bytes(Size);
    public string Description => $"{Name}\n{Detail}\nBase {AddressText} · Span {Size:N0} bytes (virtual, not resident RAM)";
}

public sealed partial class ProcessInspectionView : UserControl
{
    private IReadOnlyList<InspectionRow> source = [];
    private string status = "Collecting a bounded read-only snapshot… Close to cancel.";
    private string sort = "Name";
    private bool descending;
    private bool ready;
    public ProcessInspectionView() { InitializeComponent(); ready = true; }
    public void ShowResult(ProcessInspection result)
    {
        source = result.Kind == ProcessInspectionKind.Modules
            ? result.Modules.Select(m => new InspectionRow(m.Name, m.Path, m.BaseAddress, m.SizeBytes)).ToArray()
            : result.Regions.Select(r => new InspectionRow($"{State(r.State)} · {Type(r.Type)}",
                $"Protection: {Protection(r.Protection)} · Allocation base: {(r.AllocationBase is { } address ? $"0x{address:X16}" : "—")}", r.BaseAddress, r.SizeBytes)).ToArray();
        sort = result.Kind == ProcessInspectionKind.Modules ? "Name" : "Address";
        status = $"{result.Status} · PID {result.Identity.Pid} · creation {result.Identity.CreationTimeFileTime} · {result.CapturedAt.LocalDateTime:T}\n{result.Detail} Frozen snapshot; no live refresh.";
        Rebuild();
    }
    public void ShowError(string message) { status = message; source = []; Rebuild(); }
    private void FilterChanged(object sender, TextChangedEventArgs args) { if (ready) Rebuild(); }
    private void SortClicked(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string column }) return;
        descending = column == sort && !descending;
        sort = column;
        Rebuild();
    }
    private void Rebuild()
    {
        var filtered = source.Where(row => row.Description.Contains(Search.Text.Trim(), StringComparison.OrdinalIgnoreCase));
        var rows = sort switch
        {
            "Address" => descending ? filtered.OrderByDescending(r => r.Address) : filtered.OrderBy(r => r.Address),
            "Size" => descending ? filtered.OrderByDescending(r => r.Size) : filtered.OrderBy(r => r.Size),
            _ => descending ? filtered.OrderByDescending(r => r.Name, StringComparer.OrdinalIgnoreCase) : filtered.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
        };
        var displayed = rows.ToArray();
        Rows.ItemsSource = displayed;
        Status.Text = $"{status}\n{displayed.Length} of {source.Count} records{(displayed.Length == 0 ? " · No matching observations" : "")}";
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        Detail.Text = Rows.SelectedItem is InspectionRow row ? row.Description : "Select a row for complete, copyable details. Spans are virtual address ranges, not resident RAM.";
    }
    private static string State(uint state) => state switch { 0x1000 => "Committed", 0x2000 => "Reserved", 0x10000 => "Free", _ => $"State 0x{state:X}" };
    private static string Type(uint? type) => type switch { 0x1000000 => "Image", 0x40000 => "Mapped", 0x20000 => "Private", _ => type is { } value ? $"Type 0x{value:X}" : "—" };
    private static string Protection(uint? protect)
    {
        if (protect is not { } value) return "— (undefined for free/reserved pages)";
        var name = (value & 0xff) switch { 1 => "No access", 2 => "Read", 4 => "Read/write", 8 => "Write copy", 0x10 => "Execute", 0x20 => "Execute/read", 0x40 => "Execute/read/write", 0x80 => "Execute/write copy", _ => "Unknown" };
        return $"{name}{((value & 0x100) != 0 ? " + guard" : "")} (0x{value:X})";
    }
}
