using System.Diagnostics.CodeAnalysis;

namespace Workable;
/// <summary>
/// Represents the immediate and eventual outcome of a queue request.
/// </summary>
public interface IWorkerHandle
{
    /// <summary>
    /// Gets the immediate queue outcome returned by the enqueue operation.
    /// </summary>
    WorkQueueOutcome QueueOutcome { get; }

    /// <summary>
    /// Gets the identifier of the created worker when the queue request was accepted; otherwise <see langword="null"/>.
    /// </summary>
    WorkerId? WorkerId { get; }

    /// <summary>
    /// Waits for the queued worker to reach a completion state and returns its untyped completion result.
    /// </summary>
    /// <param name="cancellationToken">A token that cancels the wait before the worker reaches a completion state.</param>
    /// <returns>A task that completes with the worker's completion result.</returns>
    Task<WorkCompletion> WaitForCompletion(CancellationToken cancellationToken = default);

    /// <summary>
    /// Waits for the queued worker to reach a completion state and deserializes the retained output to the requested type.
    /// </summary>
    /// <typeparam name="TOutput">The logical output type to deserialize from the retained worker output.</typeparam>
    /// <param name="cancellationToken">A token that cancels the wait before the worker reaches a completion state.</param>
    /// <returns>A task that completes with the worker's typed completion result.</returns>
    Task<WorkCompletion<TOutput>> WaitForCompletion<TOutput>(CancellationToken cancellationToken = default);
}
