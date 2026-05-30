namespace Workable;

internal sealed class EmptyWorkEventSubscription : IWorkEventSubscription, IWorkEventSubscriptionDiagnostics
{
    public static EmptyWorkEventSubscription Instance { get; } = new();

    private EmptyWorkEventSubscription()
    {
    }

    public IAsyncEnumerable<WorkEvent> Read(CancellationToken cancellationToken = default)
        => Empty(cancellationToken);

    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;

    public WorkEventSubscriptionDiagnosticsSnapshot GetDiagnosticsSnapshot()
        => new(
            Capacity: 0,
            OverflowBehavior: WorkEventOverflowBehavior.DropOldest,
            QueuedCount: 0,
            PeakQueuedCount: 0,
            AcceptedEventCount: 0,
            DeliveredEventCount: 0,
            DroppedEventCount: 0);

    private static async IAsyncEnumerable<WorkEvent> Empty(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }
}
