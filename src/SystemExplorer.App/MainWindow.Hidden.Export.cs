using SystemExplorer.Core;

namespace SystemExplorer.App;

public sealed partial class MainWindow
{
    private async void ExportHiddenAsync(HiddenExportDocument document, string format)
    {
        try
        {
            var path = NativeFileDialogs.ChooseSave(WinRT.Interop.WindowNative.GetWindowHandle(this), format,
                "Export hidden items", "SystemExplorer-Hidden-" + document.Metadata.ScanType.Replace(' ', '-'));
            if (path is null) return;
            var text = format == "csv" ? HiddenItemsExport.Csv(document) : HiddenItemsExport.Json(document);
            await File.WriteAllTextAsync(path, text, shutdown.Token);
            HiddenPanel.Progress("Export saved. Frozen scan unchanged.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            HiddenPanel.Progress($"Export unavailable: {error.GetType().Name} (HRESULT 0x{error.HResult:X8}): {error.Message} Source: {error.Source ?? "unknown"}. Frozen scan unchanged.");
        }
    }
}
