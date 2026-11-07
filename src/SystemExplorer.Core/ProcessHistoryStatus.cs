namespace SystemExplorer.Core;

public static class ProcessHistoryStatus
{
    public static string Build(ProcessHistorySnapshot? history)
    {
        const string status = "Live session history · updates every second · 8 lifetimes / 180 samples. Missing is not confirmed exit; gaps are not zero.";
        return history?.Points.LastOrDefault() is { Observed: false } latest
            ? $"{status} Selected lifetime not observed since {latest.At.LocalDateTime:HH:mm:ss}; retained samples stay visible (missing is not confirmed exit)."
            : status;
    }
}
