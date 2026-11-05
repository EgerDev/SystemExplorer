namespace SystemExplorer.Core;

public static class Sampling
{
    public static double? CpuPercent(ProcessIdentity previousIdentity, ProcessIdentity currentIdentity,
        ulong previousCpuTicks, ulong currentCpuTicks, double elapsedSeconds, int logicalProcessors)
    {
        if (!currentIdentity.IsStable || previousIdentity != currentIdentity || elapsedSeconds <= 0 ||
            !double.IsFinite(elapsedSeconds) || logicalProcessors <= 0 || currentCpuTicks < previousCpuTicks)
            return null;
        var value = (currentCpuTicks - previousCpuTicks) / 10_000_000d / elapsedSeconds / logicalProcessors * 100;
        return value is >= 0 and <= 100.01 ? Math.Min(100, value) : null;
    }

    public static double? Rate(ulong previous, ulong current, double elapsedSeconds) =>
        current >= previous && double.IsFinite(elapsedSeconds) && elapsedSeconds > 0 ? (current - previous) / elapsedSeconds : null;
}
