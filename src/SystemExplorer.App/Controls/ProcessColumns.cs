using System.ComponentModel;
using Microsoft.UI.Xaml;

namespace SystemExplorer.App.Controls;

public sealed class ProcessColumns : INotifyPropertyChanged
{
    private readonly double[] preferred = [260, 132, 76, 100, 80, 0, 0, 72];
    private readonly double[] widths = [260, 132, 76, 0, 0, 0, 0, 72];
    private static readonly double[] Minimums = [180, 126, 68, 88, 72, 0, 0, 64];
    private static readonly double[] Maximums = [600, 220, 160, 200, 160, 320, 220, 160];
    private double viewport = 572;
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
    public Visibility IoVisibility => widths[3] > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ThreadsVisibility => widths[4] > 0 ? Visibility.Visible : Visibility.Collapsed;
    public double GetWidth(int index) => widths[index];
    public void FitViewport(double width)
    {
        if (!double.IsFinite(width) || width <= 32) return;
        viewport = width;
        for (var i = 0; i < widths.Length; i++) widths[i] = preferred[i];
        if (width < 740) widths[3] = 0;
        if (width < 840) widths[4] = 0;
        var available = Math.Max(0, width - 32);
        var excess = widths.Sum() - available;
        foreach (var i in new[] { 0, 3, 4, 1, 2, 7 })
        {
            if (excess <= 0) break;
            var reduction = Math.Min(excess, Math.Max(0, widths[i] - Minimums[i]));
            widths[i] -= reduction;
            excess -= reduction;
        }
        if (excess < 0) widths[0] -= excess;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }
    public void Resize(int index, double width)
    {
        if (index < 0 || index >= widths.Length || !double.IsFinite(width)) return;
        var next = Math.Clamp(width, Minimums[index], Maximums[index]);
        if (next == widths[index]) return;
        if (index == 0)
        {
            var remaining = next - widths[0];
            foreach (var other in new[] { 1, 3, 4, 2, 7 })
            {
                if (widths[other] == 0) continue;
                var changed = Math.Clamp(widths[other] - remaining, Minimums[other], Maximums[other]);
                remaining -= widths[other] - changed;
                preferred[other] = changed;
                if (Math.Abs(remaining) < 0.1) break;
            }
        }
        preferred[index] = next;
        FitViewport(viewport);
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}
