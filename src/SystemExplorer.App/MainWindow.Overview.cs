using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SystemExplorer.Core;

namespace SystemExplorer.App;

public sealed partial class MainWindow
{
    private readonly OverviewData overviewData = new();
    private bool overviewStale;
    private int observationGeneration;

    private void InitializeOverview()
    {
        OverviewPanel.RankingChanged += RefreshOverview;
        OverviewPanel.ViewProcessesRequested += () => NavigateTo("Processes");
        OverviewPanel.InsightsRequested += () => NavigateTo("Insights", clearSearch: true);
        OverviewPanel.InspectRequested += InspectOverviewProcess;
    }

    private void RefreshOverview()
    {
        if (!ready || section != "Overview") return;
        var overviewDescription = OverviewPanel.DescriptionLookup;
        var reading = overviewData.Create(Model.Relationships, Model.History,
            OverviewPanel.Ranking, GlobalSearch.Text,
            process => Model.Descriptions.GetCached(process) ?? overviewDescription(process));
        OverviewPanel.Update(reading, paused, overviewStale);
    }

    private void NavigateTo(string target, bool clearSearch = false)
    {
        if (clearSearch) GlobalSearch.Text = "";
        var item = Navigation.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(candidate => candidate.Tag?.ToString() == target);
        if (item is not null) Navigation.SelectedItem = item;
    }

    private async void InspectOverviewProcess(ProcessIdentity identity)
    {
        if (!identity.IsStable)
        {
            await ShowOverviewNoticeAsync("Process identity unavailable",
                "Creation time is unavailable, so this observation cannot be safely matched across views. Open Processes to choose a current observation.");
            return;
        }
        if (Model.Snapshot?.Processes.Any(process => process.Identity == identity) != true)
        {
            await ShowOverviewNoticeAsync("Process no longer observed",
                "This process lifetime is no longer present in the latest observation. A matching PID alone is not a verified match.");
            return;
        }

        NavigateTo("Processes");
        var row = FindOverviewProcess(identity);
        if (row is null)
        {
            GlobalSearch.Text = "";
            ProcessSearch.Text = "";
            Model.Search = "";
            Model.Rebuild();
            row = FindOverviewProcess(identity);
        }
        if (row is null)
        {
            await ShowOverviewNoticeAsync("Process unavailable", "The selected process cannot be located in the current table.");
            return;
        }

        ProcessList.SelectedItem = row;
        ProcessList.ScrollIntoView(row);
        ProcessList.Focus(FocusState.Programmatic);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (ReferenceEquals(ProcessList.SelectedItem, row) &&
                ProcessList.ContainerFromItem(row) is ListViewItem item)
                item.Focus(FocusState.Programmatic);
        });
    }

    private ProcessRow? FindOverviewProcess(ProcessIdentity identity)
    {
        var row = Model.Rows.FirstOrDefault(candidate => candidate.Identity == identity && !candidate.HasChildren)
            ?? Model.Rows.FirstOrDefault(candidate => candidate.Identity == identity);
        if (row is not null || Model.Snapshot is not { } snapshot) return row;

        var group = ProcessGrouping.Create(snapshot.Processes)
            .FirstOrDefault(candidate => candidate.Members.Any(process => process.Identity == identity));
        var root = group is null ? null : Model.Rows.FirstOrDefault(candidate =>
            !candidate.IsChild && candidate.Identity == group.Key && candidate.HasChildren);
        if (root is { IsExpanded: false }) Model.Toggle(root);
        return Model.Rows.FirstOrDefault(candidate => candidate.Identity == identity && !candidate.HasChildren)
            ?? Model.Rows.FirstOrDefault(candidate => candidate.Identity == identity);
    }

    private async Task ShowOverviewNoticeAsync(string title, string message)
    {
        if (detailsOpen || shutdown.IsCancellationRequested || Root.XamlRoot is null) return;
        detailsOpen = true;
        try
        {
            await new ContentDialog
            {
                XamlRoot = Root.XamlRoot,
                RequestedTheme = Root.RequestedTheme,
                Title = title,
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                CloseButtonText = "Close"
            }.ShowAsync();
        }
        finally { detailsOpen = false; }
    }
}
