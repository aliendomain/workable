namespace Workable;

internal sealed class EmptyWorkIterationStatusSubscription : IWorkIterationStatusSubscription
{
    public static EmptyWorkIterationStatusSubscription Instance { get; } = new();

    private EmptyWorkIterationStatusSubscription()
    {
    }

    public WorkIterationStatusCompletion? Completion => null;

    public IAsyncEnumerable<WorkIterationStatusItem> Read(CancellationToken cancellationToken = default)
        => Empty(cancellationToken);

    public ValueTask DisposeAsync()
        => ValueTask.CompletedTask;

    private static async IAsyncEnumerable<WorkIterationStatusItem> Empty(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        yield break;
    }
}
