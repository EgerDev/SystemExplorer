namespace SystemExplorer.Core;

public sealed class ProcessColumnWidths
{
    private readonly double[] preferred = [260, 132, 76, 100, 80, 0, 0, 72];
    private readonly double[] widths = [260, 132, 76, 0, 0, 0, 0, 72];
    private static readonly double[] Minimums = [180, 126, 68, 88, 72, 0, 0, 64];
    private static readonly double[] Maximums = [600, 220, 160, 200, 160, 320, 220, 160];
    private static readonly int[] ReductionOrder = [0, 3, 4, 1, 2, 7];
    private static readonly int[] RedistributionOrder = [1, 3, 4, 2, 7];
    private double viewport = 572;
    public double GetWidth(int index) => widths[index];
    public double ContentWidth => widths.Sum();
    public double TableWidth => ContentWidth + 32;
    public bool IoVisible => widths[3] > 0;
    public bool ThreadsVisible => widths[4] > 0;
    public bool FitViewport(double width)
    {
        if (!double.IsFinite(width) || width <= 32 || Math.Abs(width - viewport) < 0.5) return false;
        viewport = width;
        return Fit();
    }
    private bool Fit()
    {
        Span<double> before = stackalloc double[8];
        widths.CopyTo(before);
        preferred.CopyTo(widths, 0);
        if (viewport < 740) widths[3] = 0;
        if (viewport < 840) widths[4] = 0;
        var available = Math.Max(0, viewport - 32);
        var excess = widths.Sum() - available;
        foreach (var i in ReductionOrder)
        {
            if (excess <= 0) break;
            var reduction = Math.Min(excess, Math.Max(0, widths[i] - Minimums[i]));
            widths[i] -= reduction;
            excess -= reduction;
        }
        if (excess < 0) widths[0] -= excess;
        return !before.SequenceEqual(widths);
    }
    public bool Resize(int index, double width)
    {
        if (index < 0 || index >= widths.Length || !double.IsFinite(width)) return false;
        var next = Math.Clamp(width, Minimums[index], Maximums[index]);
        if (next == widths[index]) return false;
        if (index == 0)
        {
            var remaining = next - widths[0];
            foreach (var other in RedistributionOrder)
            {
                if (widths[other] == 0) continue;
                var changed = Math.Clamp(widths[other] - remaining, Minimums[other], Maximums[other]);
                remaining -= widths[other] - changed;
                preferred[other] = changed;
                if (Math.Abs(remaining) < 0.1) break;
            }
        }
        preferred[index] = next;
        return Fit();
    }
}
