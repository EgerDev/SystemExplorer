using System.ComponentModel;
using Microsoft.UI.Xaml;
using SystemExplorer.Core;

namespace SystemExplorer.App.Controls;

public sealed class ProcessColumns : INotifyPropertyChanged
{
    private readonly ProcessColumnWidths widths = new();
    private static readonly string[] WidthProperties =
        [nameof(Name), nameof(Memory), nameof(Cpu), nameof(Io), nameof(Threads), nameof(User), nameof(Status), nameof(Pid)];
    public GridLength Name => new(widths.GetWidth(0));
    public GridLength Memory => new(widths.GetWidth(1));
    public GridLength Cpu => new(widths.GetWidth(2));
    public GridLength Io => new(widths.GetWidth(3));
    public GridLength Threads => new(widths.GetWidth(4));
    public GridLength User => new(widths.GetWidth(5));
    public GridLength Status => new(widths.GetWidth(6));
    public GridLength Pid => new(widths.GetWidth(7));
    public double ContentWidth => widths.ContentWidth;
    public double TableWidth => widths.TableWidth;
    public Visibility IoVisibility => widths.IoVisible ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ThreadsVisibility => widths.ThreadsVisible ? Visibility.Visible : Visibility.Collapsed;
    public double GetWidth(int index) => widths.GetWidth(index);
    public void FitViewport(double width) => Change(() => widths.FitViewport(width));
    public void Resize(int index, double width) => Change(() => widths.Resize(index, width));
    private void Change(Func<bool> change)
    {
        Span<double> before = stackalloc double[8];
        for (var i = 0; i < before.Length; i++) before[i] = widths.GetWidth(i);
        var contentWidth = ContentWidth;
        if (!change()) return;
        for (var i = 0; i < before.Length; i++)
            if (before[i] != widths.GetWidth(i)) Notify(WidthProperties[i]);
        if (contentWidth != ContentWidth)
        {
            Notify(nameof(ContentWidth));
            Notify(nameof(TableWidth));
        }
        if ((before[3] > 0) != widths.IoVisible) Notify(nameof(IoVisibility));
        if ((before[4] > 0) != widths.ThreadsVisible) Notify(nameof(ThreadsVisibility));
    }
    private void Notify(string property) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    public event PropertyChangedEventHandler? PropertyChanged;
}
