using System.ComponentModel;
using Microsoft.UI.Xaml;

namespace SystemExplorer.App.Controls;

public sealed class ProcessColumns : INotifyPropertyChanged
{
    private readonly double[] widths = [260, 120, 76, 96, 76, 126, 112, 72];
    private static readonly double[] Minimums = [180, 112, 68, 88, 72, 90, 106, 64];
    private static readonly double[] Maximums = [600, 220, 160, 200, 160, 320, 220, 160];
    private static readonly string[] Names = [nameof(Name), nameof(Memory), nameof(Cpu), nameof(Io), nameof(Threads), nameof(User), nameof(Status), nameof(Pid)];
    public GridLength Name => new(widths[0]);
    public GridLength Memory => new(widths[1]);
    public GridLength Cpu => new(widths[2]);
    public GridLength Io => new(widths[3]);
    public GridLength Threads => new(widths[4]);
    public GridLength User => new(widths[5]);
    public GridLength Status => new(widths[6]);
    public GridLength Pid => new(widths[7]);
    public double ContentWidth => widths.Sum();
    public double TableWidth => ContentWidth + 32;
    public double GetWidth(int index) => widths[index];
    public void Resize(int index, double width)
    {
        if (index < 0 || index >= widths.Length || !double.IsFinite(width)) return;
        var next = Math.Clamp(width, Minimums[index], Maximums[index]);
        if (next == widths[index]) return;
        widths[index] = next;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(Names[index]));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ContentWidth)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TableWidth)));
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
