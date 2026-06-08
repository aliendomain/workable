namespace Workable;
/// <summary>
/// Represents the immediate result of one worker action or worker reconfiguration request.
/// </summary>
/// <param name="Status">The high-level action status returned by the request.</param>
/// <param name="Action">The action that was requested.</param>
/// <param name="WorkerId">The target worker id when known.</param>
/// <param name="Worker">The authoritative worker snapshot returned with the outcome, when available.</param>
/// <param name="Messages">Structured messages that describe validation, authorization, or conflict details.</param>
public sealed record WorkActionOutcome(
    WorkActionStatus Status,
    WorkAction Action,
    WorkerId? WorkerId,
    WorkerSnapshot? Worker,
    IReadOnlyList<WorkMessage> Messages)
{
    /// <summary>
    /// Gets a value indicating whether the action request was accepted and applied.
    /// </summary>
    public bool IsAccepted => this.Status == WorkActionStatus.Accepted;

    /// <summary>
    /// Creates an accepted action outcome.
    /// </summary>
    /// <param name="action">The action that was applied.</param>
    /// <param name="worker">The authoritative worker snapshot after the action was applied.</param>
    /// <param name="messages">Optional informational messages to retain alongside the accepted outcome.</param>
    /// <returns>An accepted action outcome.</returns>
    public static WorkActionOutcome Accepted(WorkAction action, WorkerSnapshot worker, IEnumerable<WorkMessage>? messages = null)
        => new(WorkActionStatus.Accepted, action, worker.Id, worker, [.. messages ?? []]);

    /// <summary>
    /// Creates a not-found action outcome for a missing worker.
    /// </summary>
    /// <param name="action">The action that was requested.</param>
    /// <param name="workerId">The worker id that could not be found.</param>
    /// <returns>A not-found action outcome.</returns>
    public static WorkActionOutcome NotFound(WorkAction action, WorkerId workerId)
        => new(WorkActionStatus.NotFound, action, workerId, null, [WorkMessage.Error("workable.worker.not_found", $"No worker was found for '{workerId}'.", "worker")]);

    /// <summary>
    /// Creates an unauthorized action outcome for a worker the caller cannot operate.
    /// </summary>
    /// <param name="action">The action that was requested.</param>
    /// <param name="workerId">The worker id the caller attempted to operate.</param>
    /// <returns>An unauthorized action outcome.</returns>
    public static WorkActionOutcome Unauthorized(WorkAction action, WorkerId workerId)
        => new(
            WorkActionStatus.Unauthorized,
            action,
            workerId,
            null,
            [WorkMessage.Error("workable.worker.unauthorized", $"You are not authorized to operate worker '{workerId}'.", "worker.authorization")]);

    /// <summary>
    /// Creates an invalid action outcome using the supplied validation or state messages.
    /// </summary>
    /// <param name="action">The action that was requested.</param>
    /// <param name="worker">The target worker snapshot, when one was available.</param>
    /// <param name="messages">The messages that explain why the request was invalid.</param>
    /// <returns>An invalid action outcome.</returns>
    public static WorkActionOutcome Invalid(WorkAction action, WorkerSnapshot? worker, IEnumerable<WorkMessage> messages)
        => new(WorkActionStatus.Invalid, action, worker?.Id, worker, [.. messages]);

    /// <summary>
    /// Creates a conflict action outcome using the supplied concurrency or revision messages.
    /// </summary>
    /// <param name="action">The action that was requested.</param>
    /// <param name="worker">The authoritative worker snapshot that conflicted with the request.</param>
    /// <param name="messages">The messages that explain the conflict.</param>
    /// <returns>A conflict action outcome.</returns>
    public static WorkActionOutcome Conflict(WorkAction action, WorkerSnapshot worker, IEnumerable<WorkMessage> messages)
        => new(WorkActionStatus.Conflict, action, worker.Id, worker, [.. messages]);
}
