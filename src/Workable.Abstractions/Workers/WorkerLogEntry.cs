using Microsoft.Extensions.Logging;

namespace Workable;
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
    public Guid Id { get; init; } = Guid.NewGuid();
}
