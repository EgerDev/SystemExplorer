namespace SystemExplorer.Core;

public static class ProcessSelectionPolicy
{
    public static bool IsSelectedProcessObserved(ProcessIdentity? selectedIdentity, IReadOnlyList<ProcessSnapshot> processes) =>
        selectedIdentity is { } identity && processes.Any(process => process.Identity == identity);
}
