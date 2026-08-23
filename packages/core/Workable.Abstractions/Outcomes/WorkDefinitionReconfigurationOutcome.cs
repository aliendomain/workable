namespace Workable;

/// <summary>
/// Represents the immediate result of a definition reconfiguration request.
/// </summary>
/// <param name="Status">The high-level reconfiguration status returned by the request.</param>
/// <param name="Definition">The authoritative definition snapshot returned only when the caller may read it.</param>
/// <param name="Messages">Structured messages that describe validation, authorization, or conflict details.</param>
public sealed record WorkDefinitionReconfigurationOutcome(
    WorkDefinitionReconfigurationStatus Status,
    WorkDefinition? Definition,
    IReadOnlyList<WorkMessage> Messages)
{
    /// <summary>
    /// Gets the authoritative definition revision returned by the operation, even when the caller cannot read the
    /// complete definition snapshot.
    /// </summary>
    public long? Revision { get; init; } = Definition?.Revision;

    /// <summary>
    /// Gets a value indicating whether the reconfiguration request was accepted and applied.
    /// </summary>
    public bool IsAccepted => this.Status == WorkDefinitionReconfigurationStatus.Accepted;

    /// <summary>
    /// Creates an accepted reconfiguration outcome.
    /// </summary>
    /// <param name="definition">The authoritative definition snapshot after the reconfiguration was applied.</param>
    /// <param name="messages">Optional informational messages to retain alongside the accepted outcome.</param>
    /// <returns>An accepted reconfiguration outcome.</returns>
    public static WorkDefinitionReconfigurationOutcome Accepted(WorkDefinition definition, IEnumerable<WorkMessage>? messages = null)
        => new(WorkDefinitionReconfigurationStatus.Accepted, definition, [.. messages ?? []])
        {
            Revision = definition.Revision,
        };

    /// <summary>
    /// Creates an unauthorized reconfiguration outcome for a definition the caller cannot operate.
    /// </summary>
    /// <param name="target">The definition name or target text the caller attempted to reconfigure.</param>
    /// <returns>An unauthorized reconfiguration outcome.</returns>
    public static WorkDefinitionReconfigurationOutcome Unauthorized(string target)
        => new(
            WorkDefinitionReconfigurationStatus.Unauthorized,
            null,
            [WorkMessage.Error(
                "workable.definition.unauthorized",
                $"You are not authorized to operate work definition '{target}'.",
                "definition.authorization")]);

    /// <summary>
    /// Creates a not-found reconfiguration outcome for a missing definition.
    /// </summary>
    /// <param name="target">The definition name or target text that could not be resolved.</param>
    /// <returns>A not-found reconfiguration outcome.</returns>
    public static WorkDefinitionReconfigurationOutcome NotFound(string target)
        => new(
            WorkDefinitionReconfigurationStatus.NotFound,
            null,
            [WorkMessage.Error("workable.definition.not_found", $"No work definition was found for '{target}'.", "definition")]);

    /// <summary>
    /// Creates an invalid reconfiguration outcome using the supplied validation messages.
    /// </summary>
    /// <param name="definition">The authoritative definition snapshot associated with the invalid request.</param>
    /// <param name="messages">The messages that explain why the request was invalid.</param>
    /// <returns>An invalid reconfiguration outcome.</returns>
    public static WorkDefinitionReconfigurationOutcome Invalid(WorkDefinition definition, IEnumerable<WorkMessage> messages)
        => new(WorkDefinitionReconfigurationStatus.Invalid, definition, [.. messages])
        {
            Revision = definition.Revision,
        };

    /// <summary>
    /// Creates a conflict reconfiguration outcome for a definition revision mismatch.
    /// </summary>
    /// <param name="definition">The authoritative definition snapshot that conflicted with the request.</param>
    /// <param name="expectedRevision">The definition revision supplied by the caller.</param>
    /// <returns>A conflict reconfiguration outcome.</returns>
    public static WorkDefinitionReconfigurationOutcome Conflict(WorkDefinition definition, long expectedRevision)
        => new(
            WorkDefinitionReconfigurationStatus.Conflict,
            definition,
            [WorkMessage.Error(
                "workable.definition.revision_conflict",
                $"Work definition '{definition.Name}' is at revision {definition.Revision}, but revision {expectedRevision} was supplied.",
                "definition.revision")])
        {
            Revision = definition.Revision,
        };
}
