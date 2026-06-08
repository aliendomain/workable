using Microsoft.Extensions.Logging;

namespace Workable;

/// <summary>
/// Represents one retained worker log entry.
/// </summary>
/// <param name="OccurredAt">The time the log entry was written.</param>
/// <param name="WorkerId">The identifier of the related worker.</param>
/// <param name="DefinitionId">The identifier of the related definition.</param>
/// <param name="Category">The logger category that produced the entry.</param>
/// <param name="Level">The log level of the entry.</param>
/// <param name="EventId">The structured event identifier associated with the entry.</param>
/// <param name="Message">The rendered log message.</param>
/// <param name="ExceptionType">The exception type, when one was captured.</param>
/// <param name="ExceptionMessage">The exception message, when one was captured.</param>
public sealed record WorkerLogEntry(
    DateTimeOffset OccurredAt,
    WorkerId WorkerId,
    WorkDefinitionId DefinitionId,
    string Category,
    LogLevel Level,
    EventId EventId,
    string Message,
    string? ExceptionType = null,
    string? ExceptionMessage = null)
{
    /// <summary>
    /// Gets the stable identifier of the retained log entry.
    /// </summary>
    public Guid Id { get; init; } = Guid.NewGuid();

    /// <summary>
    /// Gets the monotonic ordinal assigned to the retained log entry.
    /// </summary>
    public long Ordinal { get; init; }
}
