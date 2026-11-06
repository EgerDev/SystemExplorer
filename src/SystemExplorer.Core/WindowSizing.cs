namespace SystemExplorer.Core;

public static class WindowSizing
{
    public const int MinimumEffectiveWidth = 900;
    public const int MinimumEffectiveHeight = 560;

    public static (int Width, int Height) MinimumPhysicalSize(uint dpi, int workWidth, int workHeight)
    {
        ArgumentOutOfRangeException.ThrowIfZero(dpi);
        ArgumentOutOfRangeException.ThrowIfNegative(workWidth);
        ArgumentOutOfRangeException.ThrowIfNegative(workHeight);
        return ((int)Math.Min(Math.Ceiling(MinimumEffectiveWidth * (double)dpi / 96), workWidth),
            (int)Math.Min(Math.Ceiling(MinimumEffectiveHeight * (double)dpi / 96), workHeight));
    }
}
