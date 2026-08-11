namespace Workable;

/// <summary>
/// Queues children under the scoped execution authority of the active parent worker.
/// </summary>
/// <remarks>
/// A child queue is valid only while its owning execution context is active. It can queue only definitions declared by
/// the parent configuration. Delegated execution bypasses the child's direct queue authorization without granting the
/// initiating actor general access to the child definition.
/// The parent executor is therefore a security boundary: it must validate caller-controlled business scope, child
/// input, worker options, and fan-out before delegating them. Child authorization requirements that inspect input or
/// options are intentionally not evaluated on this path.
/// </remarks>
public interface IChildWorkQueueService
{
    /// <summary>
    /// Queues declared child work using raw input.
    /// </summary>
    Task<IWorkerHandle> Enqueue(
        string name,
        WorkInput? input = null,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queues declared child work using typed input.
    /// </summary>
    Task<IWorkerHandle> Enqueue<TInput>(
        string name,
        TInput input,
        WorkerOptions? options = null,
        CancellationToken cancellationToken = default);
}
