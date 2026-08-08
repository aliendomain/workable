namespace Workable;
/// <summary>
/// Queues work definitions and returns handles for the resulting workers.
/// </summary>
public interface IWorkQueueService
{
    /// <summary>
    /// Notifies the local durable queue reader that caller-owned transaction work may now be committed and ready.
    /// </summary>
    /// <remarks>
    /// Call this after successfully committing a caller-owned durability transaction. Calls are coalesced while a wake
    /// remains pending and affect only the local Workable runtime; fallback polling remains responsible for
    /// cross-process discovery and missed notifications. This is a trusted in-process hint and should not be exposed
    /// directly to untrusted callers.
    /// </remarks>
    void NotifyDurableWorkAvailable();

    /// <summary>
    /// Queues work by definition name using raw <see cref="WorkInput"/>.
    /// </summary>
    /// <param name="name">The registered work definition name to queue.</param>
    /// <param name="input">The raw serialized input payload to supply to the worker, if any.</param>
    /// <param name="options">Optional queue-time worker options and configuration overrides.</param>
    /// <param name="cancellationToken">A token that cancels the queue request before it completes.</param>
    /// <returns>
    /// A task that returns a worker handle containing the immediate queue outcome and, when accepted, the created worker id.
    /// </returns>
    Task<IWorkerHandle> Enqueue(
        string name,
        WorkInput? input = null,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues work by definition name using typed input that Workable serializes into <see cref="WorkInput"/>.
    /// </summary>
    /// <typeparam name="TInput">The logical input type to serialize for the queued worker.</typeparam>
    /// <param name="name">The registered work definition name to queue.</param>
    /// <param name="input">The typed input value to serialize for the worker.</param>
    /// <param name="options">Optional queue-time worker options and configuration overrides.</param>
    /// <param name="cancellationToken">A token that cancels the queue request before it completes.</param>
    /// <returns>
    /// A task that returns a worker handle containing the immediate queue outcome and, when accepted, the created worker id.
    /// </returns>
    Task<IWorkerHandle> Enqueue<TInput>(
        string name,
        TInput input,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default);
}
