namespace Workable;

/// <summary>
/// Represents one active coalesced change subscription.
/// </summary>
public interface IWorkChangeSubscription : IAsyncDisposable
{
    /// <summary>
    /// Reads coalesced changes until the subscription is disposed or the read is canceled.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the read operation.</param>
    /// <returns>An async stream of coalesced changes.</returns>
    IAsyncEnumerable<WorkChange> Read(CancellationToken cancellationToken = default);
}
