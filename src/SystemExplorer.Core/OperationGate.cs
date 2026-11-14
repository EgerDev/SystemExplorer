namespace SystemExplorer.Core;

public enum OperationPhase { Idle, Running, Cancelling }

public sealed class OperationTicket
{
    internal OperationTicket(long generation, CancellationToken cancellationToken)
    { Generation = generation; CancellationToken = cancellationToken; }

    public long Generation { get; }
    public CancellationToken CancellationToken { get; }
}

// Owned by the UI thread; cancellation does not release ownership until Complete.
public sealed class OperationGate
{
    private long generation;
    private OperationTicket? current;
    private CancellationTokenSource? cancellation;
    public OperationPhase Phase { get; private set; }
    public bool IsBusy => Phase != OperationPhase.Idle;

    public bool TryBegin(out OperationTicket ticket, CancellationToken lifetime = default)
    {
        ticket = null!;
        if (IsBusy) return false;
        var next = checked(generation + 1);
        cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        generation = next;
        ticket = current = new(next, cancellation.Token);
        Phase = OperationPhase.Running;
        return true;
    }

    public bool IsCurrent(OperationTicket ticket) => current is not null && ReferenceEquals(current, ticket);

    public void RequestCancel()
    {
        if (Phase != OperationPhase.Running) return;
        Phase = OperationPhase.Cancelling;
        cancellation!.Cancel();
    }

    public bool Complete(OperationTicket ticket)
    {
        if (!IsCurrent(ticket)) return false;
        current = null;
        Phase = OperationPhase.Idle;
        cancellation!.Dispose();
        cancellation = null;
        return true;
    }
}
