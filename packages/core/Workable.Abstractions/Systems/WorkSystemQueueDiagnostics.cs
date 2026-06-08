namespace Workable;

/// <summary>
/// Describes rejected queue requests for a system.
/// </summary>
/// <param name="RejectedWorkCount">The total number of queue requests that were not accepted.</param>
/// <param name="LastRejectedAt">The time the most recent rejected queue request occurred.</param>
/// <param name="LastRejectedStatus">The status of the most recent rejected queue request.</param>
/// <param name="LastRejectedCode">The most recent rejection message code.</param>
/// <param name="LastRejectedMessage">The most recent rejection message text.</param>
/// <param name="AlertableRejectedWorkCount">
/// The number of rejected queue requests whose codes are treated as operator-alertable infrastructure or capacity signals.
/// </param>
/// <param name="LastAlertableRejectedCode">The most recent alertable rejection message code.</param>
/// <param name="LastAlertableRejectedMessage">The most recent alertable rejection message text.</param>
public sealed record WorkSystemQueueDiagnostics(
    long RejectedWorkCount,
    DateTimeOffset? LastRejectedAt,
    WorkQueueStatus? LastRejectedStatus,
    string? LastRejectedCode,
    string? LastRejectedMessage,
    long AlertableRejectedWorkCount,
    string? LastAlertableRejectedCode,
    string? LastAlertableRejectedMessage);
