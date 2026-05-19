namespace Workable;

public sealed record WorkSystemQueueDiagnostics(
    long RejectedWorkCount,
    DateTimeOffset? LastRejectedAt,
    WorkQueueStatus? LastRejectedStatus,
    WorkDefinitionId? LastRejectedDefinitionId,
    string? LastRejectedCode,
    string? LastRejectedMessage,
    long AlertableRejectedWorkCount,
    string? LastAlertableRejectedCode,
    string? LastAlertableRejectedMessage);
