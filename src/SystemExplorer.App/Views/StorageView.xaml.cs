using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SystemExplorer.Core;
using SystemExplorer.Windows;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;
using VirtualKey = Windows.System.VirtualKey;
using Windows.UI.ViewManagement;

namespace SystemExplorer.App.Views;

public sealed record StorageRow(string Name, string Detail, string Size, double Percent = 0, string Path = "", int Node = -1, bool IsFile = false, BloatItem? Item = null)
{
    public string Glyph => Name == "(files in this folder)" ? "\uE8FD" : IsFile ? "\uE8A5" : Path.Length > 0 ? "\uE8B7" : "\uE9D9";
    public Visibility FolderVisibility => Path.Length > 0 && !IsFile && Name != "(files in this folder)" ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GlyphVisibility => FolderVisibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    public string Badge => Item is { IsLead: true } ? "Lead" : Item?.Confidence.ToString() ?? "";
    public Visibility BadgeVisibility => Item is null ? Visibility.Collapsed : Visibility.Visible;
}
public sealed record StorageDriveProperty(string Label, string Value)
{
    public Visibility ValueVisibility => Value.StartsWith("Unavailable") ? Visibility.Collapsed : Visibility.Visible;
    public Visibility UnavailableVisibility => Value.StartsWith("Unavailable") ? Visibility.Visible : Visibility.Collapsed;
}
public sealed record StorageRootChoice(string Root, string Label);
public sealed record StorageDriveCard(string Root, string Title, string Capacity, double UsedPercent, Brush CapacityBrush, IReadOnlyList<StorageDriveProperty> Properties);

public sealed partial class StorageView : UserControl, IDisposable
{
    private readonly WindowsStorageProvider provider = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim analysisGate = new(1, 1);
    private readonly DispatcherQueueTimer timer;
    private CancellationTokenSource? scanCancellation;
    private WindowsStorageScan? operation;
    private StorageScanResult? result;
    private DeveloperBloatReport? report;
    private IReadOnlyList<StorageVolume> volumes = [];
    private readonly Stopwatch elapsed = new();
    private string root = @"C:\";
    private string detailPath = "";
    private int folder, analysisGeneration;
    private bool ready, loaded, disposed;
    public nint OwnerHandle { get; set; }
    private bool Logical => SizeMode.SelectedIndex == 1;

    public StorageView()
    {
        InitializeComponent();
        timer = DispatcherQueue.CreateTimer(); timer.Interval = TimeSpan.FromMilliseconds(250);
        timer.Tick += (_, _) => PollProgress();
        ready = true;
    }
    public async void OnNavigated()
    {
        if (loaded || disposed) return;
        loaded = true; await RefreshAsync();
    }
    private void Notify(string text) { Message.Text = text; Message.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible; }
    private static string Bytes(long? value) => value is { } n ? MetricFormatting.Bytes((ulong)Math.Max(0, n)) : "Unavailable";
    private static string Reading(StorageReading reading) => reading.Value ?? "Unavailable — " + (reading.UnavailableReason ?? "not reported");
    private static string Relative(DateTime? at)
    {
        if (at is null || at == default(DateTime)) return "Unavailable — no timestamp";
        var days = (DateTime.UtcNow - at.Value).TotalDays;
        return days < 1 ? "Today" : days < 30 ? Count((long)days, "day") + " ago" : days < 365 ? Count((long)(days / 30), "month") + " ago" : Count((long)(days / 365), "year") + " ago";
    }
    private static string Count(long count, string noun) => $"{count:N0} {noun}{(count == 1 ? "" : "s")}";
    private void SelectRoot()
    {
        var choices = volumes.Select(v => new StorageRootChoice(v.Root, v.Root)).ToList();
        var selected = choices.FirstOrDefault(v => StoragePaths.Normalize(v.Root).Equals(StoragePaths.Normalize(root), StringComparison.OrdinalIgnoreCase));
        if (selected == null) { selected = new(root, "Folder: " + root); choices.Add(selected); }
        RootPicker.ItemsSource = choices; RootPicker.SelectedItem = selected;
    }
    private long Size(StorageNode n) => Logical ? n.LogicalBytes : n.AllocatedBytes;
    private static double Percent(long size, long total) => total <= 0 ? 0 : Math.Clamp(100d * size / total, 0, 100);
    private async Task RefreshAsync()
    {
        RefreshButton.IsEnabled = false;
        try
        {
            volumes = await provider.GetVolumesAsync(lifetime.Token);
            if (disposed) return;
            var accent = new SolidColorBrush(new UISettings().GetColorValue(UIColorType.Accent));
            StorageDriveProperty Property(string label, string value) => new(label, value);
            DriveCards.ItemsSource = volumes.Select(v => new StorageDriveCard(v.Root, $"{v.Root}  {v.Label}",
                v.UsedBytes is { } used ? $"{Bytes(used)} used of {Bytes(v.TotalBytes)} · {Bytes(v.FreeBytes)} free" : "Unavailable — " + (v.Error ?? "capacity not reported"),
                100 * (v.UsedFraction ?? 0), v.FreeBytes is { } free && v.TotalBytes is > 0 && (double)free / v.TotalBytes < .1
                    ? (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"] : accent,
                [Property("File system", string.IsNullOrEmpty(v.FileSystem) ? "Unavailable — " + v.Error : v.FileSystem), Property("Media type", Reading(v.Media)), Property("Bus", Reading(v.Bus)), Property("Device", Reading(v.Product)), Property("BitLocker", Reading(v.BitLocker)), Property("Compression", Reading(v.Compression)), Property("Cluster size", v.ClusterBytes is null ? "Unavailable — " + (v.Error ?? "not reported") : Bytes(v.ClusterBytes))])).ToArray();
            SelectRoot();
            Notify(volumes.Count == 0 ? "No fixed or removable drive letters were returned." : "");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Notify("Volume inventory unavailable: " + ex.Message); }
        finally { if (!disposed) RefreshButton.IsEnabled = true; }
    }
    private async void RefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void RootChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RootPicker.SelectedItem is StorageRootChoice v) { root = v.Root; RootCaption.Text = root; }
    }
    private async void ChooseFolderClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, OwnerHandle);
            var chosen = await picker.PickSingleFolderAsync();
            if (chosen is null || disposed) return;
            root = chosen.Path; SelectRoot(); RootCaption.Text = root;
        }
        catch (Exception ex) { Notify("Folder picker unavailable: " + ex.Message); }
    }
    private async void AnalyzeDriveClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string path }) { root = path; SelectRoot(); RootCaption.Text = path; await ScanAsync(false); }
    }
    private async void ScanClicked(object sender, RoutedEventArgs e) => await ScanAsync(false);
    private void CancelClicked(object sender, RoutedEventArgs e) => scanCancellation?.Cancel();
    private async void ProfileClicked(object sender, RoutedEventArgs e)
    {
        root = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile); SelectRoot(); RootCaption.Text = root;
        await ScanAsync(true);
    }
    private async Task ScanAsync(bool developer)
    {
        if (scanCancellation != null || disposed) { Notify("A scan is already running. Cancel it before starting another."); return; }
        analysisGeneration++; result = null; report = null; folder = 0;
        ClearLists(); ExportButton.IsEnabled = UseScanButton.IsEnabled = false;
        ScanButton.IsEnabled = ProfileButton.IsEnabled = false; CancelButton.IsEnabled = true;
        ProgressPanel.Visibility = Visibility.Visible; ResultTitle.Text = "Scanning metadata…"; ResultDetail.Text = root;
        ResultNotes.Text = ""; Notify(""); Tabs.SelectedIndex = 1;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); scanCancellation = cancellation;
        elapsed.Restart(); timer.Start();
        try
        {
            operation = provider.StartScan(root, cancellation.Token);
            result = await operation.Completion;
            if (disposed) return;
            RenderScan();
            UseScanButton.IsEnabled = ExportButton.IsEnabled = true;
            ProfileButton.Visibility = StoragePaths.Contains(result.Root, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ? Visibility.Collapsed : Visibility.Visible;
            if (developer) { Tabs.SelectedIndex = 2; await AnalyzeAsync(); }
        }
        catch (Exception ex) { if (!disposed) { ResultTitle.Text = "Failed — " + ex.Message; Notify("Scan unavailable: " + ex.Message); } }
        finally
        {
            timer.Stop(); elapsed.Stop(); operation = null; scanCancellation = null; cancellation.Dispose();
            if (!disposed) { ProgressPanel.Visibility = Visibility.Collapsed; ScanButton.IsEnabled = ProfileButton.IsEnabled = true; CancelButton.IsEnabled = false; }
        }
    }
    private void PollProgress()
    {
        if (operation == null || disposed) return;
        var p = operation.Progress;
        ProgressText.Text = $"{Count(p.Files, "file")} · {Count(p.Directories, "folder")} · {Bytes(p.AllocatedBytes)} allocated · {p.Denied:N0} denied · {elapsed.Elapsed:mm\\:ss} · {p.Files / Math.Max(1, elapsed.Elapsed.TotalSeconds):N0} files/s";
        CurrentPath.Text = p.CurrentPath;
        DeveloperStatus.Text = ProgressText.Text;
    }
    private void ClearLists()
    {
        foreach (var list in new[] { ChildrenList, LargestList, TypesList, AgeList, SkippedList, CategoriesList, BloatList, ProjectsList, AIList, LeadsList }) list.ItemsSource = null;
        Breadcrumb.ItemsSource = null; DetailPath.Text = detailPath = ""; CleanupPanel.Visibility = Visibility.Collapsed;
        OpenDetail.IsEnabled = CopyDetail.IsEnabled = false;
        UnknownItems.Visibility = Visibility.Collapsed;
        FootprintCard.Value = ReclaimValue.Text = ProjectsCard.Value = AICard.Value = "Not scanned";
    }
    private void RenderScan()
    {
        if (result is not { } scan) return;
        ResultTitle.Text = scan.State switch { StorageScanState.Completed => "Complete", StorageScanState.Cancelled => "Partial – cancelled", _ => "Failed — " + (scan.DeniedPaths.FirstOrDefault()?.Reason ?? "scan did not complete") };
        ResultDetail.Text = $"{scan.Method}\n{Count(scan.Totals.Files, "file")} · {Count(scan.Totals.Directories, "folder")} · {Bytes(scan.Totals.LogicalBytes)} logical / {Bytes(scan.Totals.AllocatedBytes)} allocated · {Count(scan.HardLinkDuplicates, "hard-link duplicate")} skipped ({Bytes(scan.HardLinkAllocatedBytes)} allocated)";
        ResultNotes.Text = string.Join("\n", scan.Notes); ResultNotes.Visibility = scan.Notes.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RenderFolder();
        var total = Logical ? scan.Totals.LogicalBytes : scan.Totals.AllocatedBytes;
        LargestList.ItemsSource = scan.LargestFiles.OrderByDescending(f => Logical ? f.LogicalBytes : f.AllocatedBytes).Take(100).Select(f => new StorageRow(System.IO.Path.GetFileName(f.Path), $"{f.Path} · {Relative(f.LastWriteUtc)}", Bytes(Logical ? f.LogicalBytes : f.AllocatedBytes), Percent(Logical ? f.LogicalBytes : f.AllocatedBytes, total), f.Path, IsFile: true)).ToArray();
        StorageRow Stat(StorageStatistic s) => new(s.Name, $"{Count(s.Count, "file")} · {Percent(Logical ? s.LogicalBytes : s.AllocatedBytes, total):F1}%", Bytes(Logical ? s.LogicalBytes : s.AllocatedBytes), Percent(Logical ? s.LogicalBytes : s.AllocatedBytes, total));
        TypesList.ItemsSource = scan.Extensions.OrderByDescending(x => Logical ? x.LogicalBytes : x.AllocatedBytes).Take(500).Select(Stat).ToArray();
        AgeList.ItemsSource = scan.AgeBuckets.Select(Stat).ToArray();
        SkippedSummary.Text = $"{Count(scan.DeniedTotal, "access denial")} · {Count(scan.ReparseTotal, "reparse point")} not followed. Counted and listed, not hidden. Bounded lists below; cloud placeholders are followed.";
        SkippedList.ItemsSource = scan.DeniedPaths.Select(p => new StorageRow(p.Path, "Access denied / enumeration error: " + p.Reason, "", Path: p.Path)).Concat(scan.ReparsePoints.Select(p => new StorageRow(p.Path, "Not followed: " + p.Reason, "", Path: p.Path))).Take(500).ToArray();
    }
    private void RenderFolder()
    {
        if (result is not { } scan) return;
        var n = scan.Nodes[folder]; var total = Size(n);
        var crumbs = new List<StorageRow>();
        for (var i = folder; i >= 0; i = scan.Nodes[i].Parent) crumbs.Add(new(i == 0 ? scan.Root : scan.Nodes[i].Name, "", "", Node: i));
        crumbs.Reverse(); Breadcrumb.ItemsSource = crumbs.Select(x => new StorageCrumb(x.Name, x.Node)).ToArray();
        var rows = n.Children.OrderByDescending(i => Size(scan.Nodes[i])).Take(500).Select(i =>
        {
            var child = scan.Nodes[i]; var size = Size(child);
            return new StorageRow("▸  " + child.Name, $"{Percent(size, total):F1}% of parent · {Count(child.Files, "file")} · {Relative(child.LatestWrite)}", Bytes(size), Percent(size, total), scan.FullPath(i), i);
        }).ToList();
        var direct = Logical ? n.DirectLogicalBytes : n.DirectAllocatedBytes;
        rows.Insert(0, new("(files in this folder)", $"{Percent(direct, total):F1}% of parent · {Count(n.DirectFiles, "file")} · {Relative(n.DirectLatestWrite)}", Bytes(direct), Percent(direct, total), scan.FullPath(folder)));
        ChildrenList.ItemsSource = rows;
        ChildrenNote.Text = n.Children.Count > 500 ? $"+{Count(n.Children.Count - 500, "more folder")}. Showing the largest 500." : $"{Count(n.Children.Count, "child folder")} · right-click a row for local path actions";
    }
    private void BreadcrumbClicked(BreadcrumbBar sender, BreadcrumbBarItemClickedEventArgs e)
    {
        if (e.Item is StorageCrumb crumb) { folder = crumb.Node; RenderFolder(); }
    }
    private void Drill() { if (ChildrenList.SelectedItem is StorageRow { Node: >= 0 } row) { folder = row.Node; RenderFolder(); } }
    private void FolderDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => Drill();
    private void FolderKeyDown(object sender, KeyRoutedEventArgs e) { if (e.Key == VirtualKey.Enter) { Drill(); e.Handled = true; } }
    private void SizeModeChanged(object sender, SelectionChangedEventArgs e) { if (ready) RenderScan(); }
    private async void UseScanClicked(object sender, RoutedEventArgs e) => await AnalyzeAsync();
    private async void TabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ready && Tabs.SelectedIndex == 2 && report == null && result != null && scanCancellation == null) await AnalyzeAsync();
    }
    private async void StaleChanged(object sender, SelectionChangedEventArgs e) { if (ready && report != null) await AnalyzeAsync(); }
    private void StaleFilterChanged(object sender, RoutedEventArgs e) { if (ready) RenderDeveloper(); }
    private async Task AnalyzeAsync()
    {
        if (result is not { } scan || disposed) return;
        var generation = ++analysisGeneration; var months = StaleAfter.SelectedIndex switch { 0 => 3, 2 => 12, _ => 6 };
        DeveloperStatus.Text = "Analyzing metadata…";
        var entered = false;
        try
        {
            await analysisGate.WaitAsync(lifetime.Token); entered = true;
            if (generation != analysisGeneration || disposed) return;
            var ctx = new DeveloperLocationContext(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), System.IO.Path.GetTempPath(), new[] { "NUGET_PACKAGES", "CARGO_HOME", "GOPATH", "GOMODCACHE", "HF_HOME", "OLLAMA_MODELS", "GRADLE_USER_HOME" }.Where(k => Environment.GetEnvironmentVariable(k) is { Length: > 0 }).ToDictionary(k => k, k => Environment.GetEnvironmentVariable(k)!));
            var next = await Task.Run(() => DeveloperBloatAnalyzer.Analyze(scan, ctx, DateTimeOffset.UtcNow, TimeSpan.FromDays(months * 30)), lifetime.Token);
            if (generation != analysisGeneration || disposed) return;
            report = next; RenderDeveloper();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!disposed) Notify("Developer analysis unavailable: " + ex.Message); }
        finally { if (entered) analysisGate.Release(); }
    }
    private static long UnionBytes(IEnumerable<BloatItem> source) => DeveloperBloatAnalyzer.Summarize(source).Sum(x => x.AllocatedBytes);
    private void RenderDeveloper()
    {
        if (report is not { } r || result is not { } scan) return;
        var footprint = UnionBytes(r.Items.Concat(r.Projects.Select(p => new BloatItem("Projects", "Project", p.Path, p.AllocatedBytes, 0, p.LastActivity, BloatConfidence.High, "", null, null, "Project"))));
        FootprintCard.Value = Bytes(footprint); ReclaimValue.Text = Bytes(r.KnownReclaimableBytes);
        UnknownItems.Text = $"+ {Count(r.UnknownItems, "item")} unknown";
        UnknownItems.Visibility = r.UnknownItems > 0 ? Visibility.Visible : Visibility.Collapsed;
        ProjectsCard.Value = $"{r.Projects.Count:N0} · {r.Projects.Count(p => p.IsStale):N0} stale";
        AICard.Value = Bytes(UnionBytes(r.Items.Where(i => i.Category == "AI tools and models")));
        DeveloperStatus.Text = $"{scan.Root} · {(scan.IsPartial ? "Partial scan" : "Scan finished")} · {Count(r.Items.Count, "item")} · {Count(r.AbsentCatalogEntries, "catalog location")} absent. Lists show at most 500 rows.";
        CategoriesList.ItemsSource = r.CategoryTotals.Select(x => new StorageRow(x.Category,
            (x.ReclaimableBytes is { } reclaim ? $"{Bytes(reclaim)} estimated reclaimable" : "Reclaimable: Unknown") + (x.UnknownItems > 0 ? $" + {Count(x.UnknownItems, "item")} unknown" : ""),
            Bytes(x.AllocatedBytes), Percent(x.AllocatedBytes, footprint))).ToArray();
        StorageRow Item(BloatItem x) => new((x.IsLead ? "Lead · " : "") + x.Tool, $"{x.Category} · reclaimable { (x.ReclaimableBytes is { } reclaim ? Bytes(reclaim) : "Unknown")} · {Relative(x.LatestWrite)} · {x.Confidence} confidence" + (x.IsLead ? " · " + x.Reason : ""), Bytes(x.AllocatedBytes), Percent(x.AllocatedBytes, footprint), x.Path, IsFile: x.Kind == "Model file", Item: x);
        BloatList.ItemsSource = r.Items.Take(500).Select(Item).ToArray();
        AIList.ItemsSource = r.Items.Where(i => i.Category == "AI tools and models").Take(500).Select(Item).ToArray();
        LeadsList.ItemsSource = r.Items.Where(i => i.IsLead).Take(500).Select(Item).ToArray();
        ProjectsList.ItemsSource = r.Projects.Where(p => OnlyStale.IsChecked != true || p.IsStale).OrderByDescending(p => p.AllocatedBytes).Take(500).Select(p => new StorageRow(p.Path,
            $"{(p.IsStale ? "Stale · " : "")}{Bytes(p.RegenerableBytes)} regenerable · {Relative(p.LastActivity)} · {p.Kinds} · AI markers: {(p.HasAIMarkers ? "present (lead only)" : "none observed")}", Bytes(p.AllocatedBytes), Percent(p.AllocatedBytes, footprint), p.Path)).ToArray();
        NotScannedText.Text = r.NotScanned.Count == 0 ? "All catalog paths were within this scan's root (absent paths are not listed)." : string.Join("\n", r.NotScanned.Select(x => x.Path + " — " + x.Reason));
    }
    private void RowSelected(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListView { SelectedItem: StorageRow row }) return;
        DetailTitle.Text = row.Name; DetailPath.Text = detailPath = row.Path;
        DetailReason.Text = row.Item?.Reason ?? row.Detail;
        DetailConfidence.Text = row.Item is { } item ? $"{item.Confidence} confidence — name/location heuristic, not a guarantee that cleanup is safe." : "Project discovery uses file names, not file contents.";
        CleanupPanel.Visibility = row.Item?.CleanupCommand is null ? Visibility.Collapsed : Visibility.Visible;
        CleanupText.Text = row.Item?.CleanupCommand ?? ""; CleanupWarning.Text = row.Item?.CleanupWarning ?? "";
        OpenDetail.IsEnabled = CopyDetail.IsEnabled = !string.IsNullOrEmpty(row.Path);
    }
    private void Open(string path, bool file)
    {
        try
        {
            path = path.Replace('/', '\\');
            if (path.Length < 3 || !char.IsLetter(path[0]) || path[1] != ':' || path[2] != '\\' || path.Contains('"')) { Notify("Only local drive paths can be opened."); return; }
            file |= System.IO.File.Exists(path);
            Process.Start(new ProcessStartInfo("explorer.exe", file ? $"/select,\"{path}\"" : $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex) { Notify("Explorer unavailable: " + ex.Message); }
    }
    private void Copy(string path)
    {
        try { var data = new DataPackage(); data.SetText(path.Replace('/', '\\')); Clipboard.SetContent(data); }
        catch (Exception ex) { Notify("Copy unavailable: " + ex.Message); }
    }
    private void OpenRowClicked(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: StorageRow row } && row.Path.Length > 0) Open(row.Path, row.IsFile); }
    private void CopyRowClicked(object sender, RoutedEventArgs e) { if (sender is FrameworkElement { Tag: StorageRow row } && row.Path.Length > 0) Copy(row.Path); }
    private void OpenDetailClicked(object sender, RoutedEventArgs e) => Open(detailPath, System.IO.File.Exists(detailPath.Replace('/', '\\')));
    private void CopyDetailClicked(object sender, RoutedEventArgs e) => Copy(detailPath);
    private async void ExportClicked(object sender, RoutedEventArgs e)
    {
        if (result is not { } scan || sender is not FrameworkElement { Tag: string format }) return;
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "SystemExplorer-storage" };
            picker.FileTypeChoices.Add(format.ToUpperInvariant(), ["." + format]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, OwnerHandle);
            var file = await picker.PickSaveFileAsync();
            if (file == null || disposed) return;
            var bloat = report;
            var text = await Task.Run(() => format == "csv" ? StorageExport.ToCsv(scan, bloat) : StorageExport.ToJson(scan, bloat));
            await System.IO.File.WriteAllTextAsync(file.Path, text);
            Notify("Export saved to " + file.Path);
        }
        catch (Exception ex) { if (!disposed) Notify("Export unavailable: " + ex.Message); }
    }
    private void LayoutChanged(object sender, SizeChangedEventArgs e)
    {
        if (!ready) return;
        var narrow = e.NewSize.Width < 1000;
        DriveLayout.MaximumRowsOrColumns = narrow ? 1 : e.NewSize.Width < 1300 ? 2 : 3;
        foreach (var card in new[] { FoldersCard, BloatCard }) Grid.SetColumnSpan(card, narrow ? 2 : 1);
        foreach (var card in new[] { InsightsCard, DetailCard }) { Grid.SetColumn(card, narrow ? 0 : 1); Grid.SetRow(card, narrow ? 1 : 0); Grid.SetColumnSpan(card, narrow ? 2 : 1); }
        var cards = new FrameworkElement[] { FootprintCard, ReclaimContainer, ProjectsCard, AICard };
        for (var i = 0; i < cards.Length; i++) { Grid.SetRow(cards[i], narrow ? i / 2 : 0); Grid.SetColumn(cards[i], narrow ? i % 2 * 2 : i); Grid.SetColumnSpan(cards[i], narrow ? 2 : 1); }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true; analysisGeneration++; timer.Stop(); scanCancellation?.Cancel(); lifetime.Cancel(); lifetime.Dispose();
        result = null; report = null; ClearLists();
    }
}

public sealed record StorageCrumb(string Name, int Node)
{
    public override string ToString() => Name;
}
