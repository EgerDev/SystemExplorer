using SystemExplorer.Core;

namespace SystemExplorer.App;

public sealed partial class MainWindow
{
    private async void ExportHiddenAsync(HiddenExportDocument document, string format)
    {
        var gate = HiddenPanel.Operation;
        if (!gate.TryBegin(out var ticket, shutdown.Token)) return;
        var version = HiddenPanel.SelectionVersion;
        HiddenPanel.OperationChanged(HiddenOperationKind.Export);
        string? notice = null;
        try
        {
            var path = NativeFileDialogs.ChooseSave(WinRT.Interop.WindowNative.GetWindowHandle(this), format,
                "Export hidden items", "SystemExplorer-Hidden-" + document.Metadata.ScanType.Replace(' ', '-'));
            if (path is null) return;
            var text = format == "csv" ? HiddenItemsExport.Csv(document) : HiddenItemsExport.Json(document);
            await File.WriteAllTextAsync(path, text, ticket.CancellationToken);
            notice = "Export saved. Frozen scan unchanged.";
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            notice = $"Export unavailable: {error.GetType().Name} (HRESULT 0x{error.HResult:X8}): {error.Message} Source: {error.Source ?? "unknown"}. Frozen scan unchanged.";
        }
        finally
        {
            var publish = InvestigationOperationStatus.CanPublish(gate, ticket, version, HiddenPanel.SelectionVersion);
            if (gate.Complete(ticket) && !shutdown.IsCancellationRequested)
            {
                HiddenPanel.OperationChanged(HiddenOperationKind.Export);
                if (publish && notice is not null) HiddenPanel.Progress(notice);
            }
        }
    }
}
