using SystemExplorer.Core;
using Windows.Storage.Pickers;

namespace SystemExplorer.App;

public sealed partial class MainWindow
{
    private async void ExportHiddenAsync(HiddenExportDocument document, string format)
    {
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "SystemExplorer-Hidden-" + document.Metadata.ScanType.Replace(' ', '-') };
            picker.FileTypeChoices.Add(format == "csv" ? "CSV" : "JSON", new List<string> { "." + format });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            var text = format == "csv" ? HiddenItemsExport.Csv(document) : HiddenItemsExport.Json(document);
            await File.WriteAllTextAsync(file.Path, text, shutdown.Token);
            HiddenPanel.Progress("Export saved. Frozen scan unchanged.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            HiddenPanel.Progress($"Export unavailable: {error.GetType().Name} (HRESULT 0x{error.HResult:X8}). Frozen scan unchanged.");
        }
    }
}
