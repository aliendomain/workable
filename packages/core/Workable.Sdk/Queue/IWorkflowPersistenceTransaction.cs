namespace Workable;

/// <summary>
/// Represents a store-specific workflow persistence transaction that can also participate in durable worker enqueue.
/// </summary>
public interface IWorkflowPersistenceTransaction : IWorkQueueDurabilityTransaction, IAsyncDisposable
{
    /// <summary>
    /// Commits the transaction.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the commit operation.</param>
    Task Commit(CancellationToken cancellationToken = default);
}
