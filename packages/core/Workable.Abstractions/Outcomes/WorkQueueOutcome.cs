namespace Workable;
/// <summary>
/// Represents the immediate result of a queue request.
/// </summary>
/// <param name="Status">The high-level queue status returned by the request.</param>
/// <param name="WorkerId">The created worker id when the queue request was accepted; otherwise <see langword="null"/>.</param>
/// <param name="Messages">Structured messages that describe validation, authorization, or informational details for the request.</param>
public sealed record WorkQueueOutcome(
    WorkQueueStatus Status,
    WorkerId? WorkerId,
    IReadOnlyList<WorkMessage> Messages)
{
    /// <summary>
    /// Gets a value indicating whether the queue request created a worker.
    /// </summary>
    public bool IsAccepted => this.Status == WorkQueueStatus.Accepted;

    /// <summary>
    /// Creates an accepted queue outcome.
    /// </summary>
    /// <param name="workerId">The identifier of the worker created by the queue request.</param>
    /// <param name="messages">Optional informational messages to retain alongside the accepted outcome.</param>
    /// <returns>An accepted queue outcome.</returns>
    public static WorkQueueOutcome Accepted(WorkerId workerId, IEnumerable<WorkMessage>? messages = null)
        => new(WorkQueueStatus.Accepted, workerId, [.. messages ?? []]);

    /// <summary>
    /// Creates a not-found queue outcome for a missing definition target.
    /// </summary>
    /// <param name="target">The definition name or target text that could not be resolved.</param>
    /// <returns>A not-found queue outcome.</returns>
    public static WorkQueueOutcome NotFound(string target)
        => new(WorkQueueStatus.NotFound, null, [WorkMessage.Error("workable.definition.not_found", $"No work definition was found for '{target}'.", "definition")]);

    /// <summary>
    /// Creates an unauthorized queue outcome for a definition the caller cannot operate.
    /// </summary>
    /// <param name="target">The definition name or target text that the caller attempted to queue.</param>
    /// <returns>An unauthorized queue outcome.</returns>
    public static WorkQueueOutcome Unauthorized(string target)
        => new(
            WorkQueueStatus.Unauthorized,
            null,
            [WorkMessage.Error("workable.definition.unauthorized", $"You are not authorized to queue work '{target}'.", "definition.authorization")]);

    /// <summary>
    /// Creates an invalid queue outcome using the supplied validation or state messages.
    /// </summary>
    /// <param name="messages">The messages that explain why the queue request was invalid.</param>
    /// <returns>An invalid queue outcome.</returns>
    public static WorkQueueOutcome Invalid(IEnumerable<WorkMessage> messages)
        => new(WorkQueueStatus.Invalid, null, [.. messages]);
}
