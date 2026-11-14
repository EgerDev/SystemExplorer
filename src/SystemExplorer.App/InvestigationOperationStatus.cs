using SystemExplorer.Core;

namespace SystemExplorer.App;

public enum HiddenOperationKind { Scan, Export }
public enum RamOperationKind { Initialize, Processes, Drivers, Physical, Export }

public static class InvestigationOperationStatus
{
    public const string CancellationNotice = "Cancelled. Completed observations, if any, are retained.";
    public static string TerminalScanStatus(bool cancelled) => cancelled ? "Cancelled" : "Failed";
    public static bool CanPublish(OperationGate gate, OperationTicket ticket, long capturedVersion, long currentVersion) =>
        gate.IsCurrent(ticket) && !ticket.CancellationToken.IsCancellationRequested && capturedVersion == currentVersion;
}
