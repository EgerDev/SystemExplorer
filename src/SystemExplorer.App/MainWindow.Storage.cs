namespace SystemExplorer.App;

public sealed partial class MainWindow
{
    private void OnStorageNavigated()
    {
        if (section != "Storage") return;
        StoragePanel.OwnerHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        StoragePanel.OnNavigated();
    }

    private void CloseStorage() => StoragePanel.Dispose();
}
