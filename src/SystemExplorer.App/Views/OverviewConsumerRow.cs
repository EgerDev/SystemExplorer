using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SystemExplorer.Core;

namespace SystemExplorer.App.Views;

public sealed class OverviewConsumerRow : INotifyPropertyChanged, IDisposable
{
    private ProcessSnapshot process;
    private ProcessGroup? group;
    private CancellationTokenSource? metadata;
    private string? description;
    private ImageSource? icon;
    private bool expanded;

    public OverviewConsumerRow(ProcessSnapshot process, ProcessGroup? group, bool child, bool expanded)
    {
        this.process = process;
        this.group = group;
        IsChild = child;
        this.expanded = expanded;
    }

    public ProcessIdentity Identity => process.Identity;
    public bool IsChild { get; }
    public string Key => $"{(IsChild ? "member" : "group")}:{Identity}";
    public bool HasChildren => group is { Members.Count: > 1 };
    public bool Expanded => expanded;
    public string Name => description ?? process.Name;
    public string Secondary => HasChildren ? $"{group!.Members.Count} processes · {process.Name}" : $"{process.Name} · PID {Identity.Pid}";
    public string Memory => Display.Bytes(group is null ? process.PrivateBytes : group.PrivateBytes);
    public string Cpu => Display.Percent(group is null ? process.CpuPercent : group.CpuPercent);
    public ImageSource? Icon => icon;
    public Visibility FallbackVisibility => icon is null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ExpandVisibility => HasChildren ? Visibility.Visible : Visibility.Collapsed;
    public string Chevron => expanded ? "\uE70D" : "\uE76C";
    public string ExpandName => $"{(expanded ? "Collapse" : "Expand")} {Name}, {group?.Members.Count ?? 1} observed processes";
    public string InspectName => $"Inspect {process.Name}, PID {Identity.Pid}, read-only";
    public string AccessibleName => $"{Name}, {Secondary}, private commit {Memory}, CPU {Cpu}";
    public string IdentityExplanation => $"{Name}\nExecutable: {process.Name}\nPID: {Identity.Pid}\n{process.ExecutablePath ?? "Path unavailable"}\nNames and icons come from local files, not verified publisher or application identity. Groups use matching executable, owner and observed ancestry; no process roles or running state are inferred.";
    public Thickness Indent => new(IsChild ? 18 : 0, 0, 0, 0);
    public global::Windows.UI.Text.FontWeight NameWeight => IsChild ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;

    public void Update(ProcessSnapshot next, ProcessGroup? nextGroup, bool isExpanded, ProcessIcons icons, ApplicationDescriptions descriptions)
    {
        var pathChanged = !string.Equals(process.ExecutablePath, next.ExecutablePath, StringComparison.OrdinalIgnoreCase);
        process = next;
        group = nextGroup;
        expanded = isExpanded;
        if (pathChanged)
        {
            Dispose();
            description = null;
            icon = null;
        }
        Changed();
        if (metadata is null && next.Identity.IsStable && next.ExecutablePath is not null)
        {
            metadata = new CancellationTokenSource();
            _ = LoadMetadataAsync(next, icons, descriptions, metadata.Token);
        }
    }

    private async Task LoadMetadataAsync(ProcessSnapshot captured, ProcessIcons icons, ApplicationDescriptions descriptions, CancellationToken token)
    {
        try
        {
            var text = await descriptions.LoadAsync(captured, token);
            var image = await icons.LoadAsync(captured.ExecutablePath!, token);
            if (token.IsCancellationRequested || captured.Identity != process.Identity ||
                !string.Equals(captured.ExecutablePath, process.ExecutablePath, StringComparison.OrdinalIgnoreCase)) return;
            description = text;
            icon = image;
            Changed();
        }
        catch (OperationCanceledException) { }
    }

    private void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    public event PropertyChangedEventHandler? PropertyChanged;

    public void Dispose()
    {
        metadata?.Cancel();
        metadata?.Dispose();
        metadata = null;
    }
}
