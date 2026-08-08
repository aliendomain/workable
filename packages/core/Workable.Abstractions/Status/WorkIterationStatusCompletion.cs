namespace Workable;

/// <summary>
/// Represents the authoritative retained terminal state attached to a completed iteration status stream.
/// </summary>
/// <param name="WorkerId">The worker that owns the iteration.</param>
/// <param name="WorkerRevision">The worker revision recorded with the terminal iteration update.</param>
/// <param name="WorkerState">The worker state recorded with the terminal iteration update.</param>
/// <param name="Iteration">The retained iteration status, output, messages, timing, and attempt count.</param>
/// <param name="CancellationOrigin">The accepted cancellation request origin, when one stopped the iteration.</param>
public sealed record WorkIterationStatusCompletion(
    WorkerId WorkerId,
    long WorkerRevision,
    WorkerState WorkerState,
    WorkerIterationSnapshot Iteration,
    WorkOrigin? CancellationOrigin);
