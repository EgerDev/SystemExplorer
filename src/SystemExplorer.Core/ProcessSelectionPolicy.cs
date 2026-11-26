namespace SystemExplorer.Core;

public static class ProcessSelectionPolicy
{
    public static bool ShouldClearAfterRowRebuild(ProcessIdentity? selectedIdentity, bool rowFound) =>
        selectedIdentity is not null && !rowFound;
}
