using SystemExplorer.Core;
using SystemExplorer.App;

internal static class OperationGateTests
{
    internal static void Run()
    {
        static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Operation gate assertion failed."); }
        var gate = new OperationGate();
        Check(gate.Phase == OperationPhase.Idle && !gate.IsBusy);
        gate.RequestCancel();
        Check(gate.Phase == OperationPhase.Idle);
        Check(gate.TryBegin(out var first) && first.Generation == 1 && gate.IsCurrent(first));
        Check(!gate.TryBegin(out _) && gate.Phase == OperationPhase.Running);
        var foreign = new OperationGate();
        Check(foreign.TryBegin(out var other));
        Check(!gate.Complete(other) && gate.IsCurrent(first));
        gate.RequestCancel(); gate.RequestCancel();
        Check(first.CancellationToken.IsCancellationRequested && gate.Phase == OperationPhase.Cancelling);
        Check(!gate.TryBegin(out _));
        Check(gate.Complete(first) && gate.Phase == OperationPhase.Idle);
        Check(!gate.Complete(first) && !gate.IsCurrent(first));
        using var lifetime = new CancellationTokenSource();
        Check(gate.TryBegin(out var second, lifetime.Token) && second.Generation == first.Generation + 1);
        Check(!gate.Complete(first) && gate.IsCurrent(second) && gate.IsBusy);
        lifetime.Cancel();
        Check(second.CancellationToken.IsCancellationRequested && gate.IsBusy);
        Check(gate.Complete(second));
        Check(gate.TryBegin(out var empty) && gate.Complete(empty));
        Check(gate.TryBegin(out var failed));
        try { throw new IOException("Simulated work failure"); }
        catch (IOException) { Check(gate.Complete(failed)); }
        Check(!gate.IsBusy && foreign.Complete(other));
    }

    internal static void Presentation()
    {
        static void Check(bool condition) { if (!condition) throw new InvalidOperationException("Operation presentation assertion failed."); }
        var gate = new OperationGate();
        Check(gate.TryBegin(out var export));
        Check(InvestigationOperationStatus.CanPublish(gate, export, 1, 1));
        Check(!InvestigationOperationStatus.CanPublish(gate, export, 1, 2));
        Check(gate.Complete(export));
        Check(gate.TryBegin(out var scan));
        Check(!InvestigationOperationStatus.CanPublish(gate, export, 1, 1));
        Check(!gate.Complete(export) && gate.IsCurrent(scan));
        gate.RequestCancel();
        Check(!InvestigationOperationStatus.CanPublish(gate, scan, 2, 2));
        Check(gate.Complete(scan));
        Check(InvestigationOperationStatus.TerminalScanStatus(true) == "Cancelled");
        Check(InvestigationOperationStatus.TerminalScanStatus(false) == "Failed");
        Check(!InvestigationOperationStatus.CancellationNotice.Contains("No new observation"));
        Check(InvestigationOperationStatus.CancellationNotice.Contains("Completed observations"));
    }
}
