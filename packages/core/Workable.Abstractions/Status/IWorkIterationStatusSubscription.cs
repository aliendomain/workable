namespace Workable;

/// <summary>
/// Represents one replayable, live subscription to an iteration status stream.
/// </summary>
public interface IWorkIterationStatusSubscription : IAsyncDisposable
{
    /// <summary>
    /// Reads retained and live items in sequence order until the iteration stream completes or reading is canceled.
    /// </summary>
    IAsyncEnumerable<WorkIterationStatusItem> Read(CancellationToken cancellationToken = default);
}
