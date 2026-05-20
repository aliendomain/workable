namespace Workable;

internal sealed class EmptyWorkEventSubscription : IWorkEventSubscription
{
    public static EmptyWorkEventSubscription Instance { get; } = new();

    private EmptyWorkEventSubscription()
    {
    }

    public IAsyncEnumerable<WorkEvent> Read(CancellationToken cancellationToken = default)
        => Empty(cancellationToken);

    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;

    private static async IAsyncEnumerable<WorkEvent> Empty(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }
}
