namespace Workable;

internal sealed class EmptyWorkChangeSubscription : IWorkChangeSubscription, IWorkChangeSubscriptionDiagnostics
{
    public static EmptyWorkChangeSubscription Instance { get; } = new();

    public static WorkChangeSubscriptionDiagnosticsSnapshot EmptyDiagnostics { get; } = new(
        Capacity: 0,
        QueuedCount: 0,
        PeakQueuedCount: 0,
        AcceptedChangeCount: 0,
        DeliveredChangeCount: 0,
        CoalescedChangeCount: 0,
        DroppedChangeCount: 0);

    private EmptyWorkChangeSubscription()
    {
    }

    public IAsyncEnumerable<WorkChange> Read(CancellationToken cancellationToken = default)
        => Empty(cancellationToken);

    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;

    public WorkChangeSubscriptionDiagnosticsSnapshot GetDiagnosticsSnapshot()
        => EmptyDiagnostics;

    private static async IAsyncEnumerable<WorkChange> Empty(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }
}
