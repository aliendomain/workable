namespace Workable;

/// <summary>
/// Requests upcoming occurrences for a cron schedule without creating it.
/// </summary>
/// <param name="CronExpression">The standard five-field cron expression to evaluate.</param>
/// <param name="TimeZoneId">The time-zone identifier used to interpret the expression.</param>
/// <param name="StartsAt">The earliest eligible instant, or now when omitted.</param>
/// <param name="Count">The number of upcoming occurrences to return, from one through ten.</param>
public sealed record WorkableHttpCronSchedulePreviewRequest(
    string CronExpression,
    string TimeZoneId,
    DateTimeOffset? StartsAt = null,
    int Count = 5);

/// <summary>
/// Returns upcoming UTC-aware cron occurrences.
/// </summary>
/// <param name="Occurrences">The upcoming occurrences in chronological order.</param>
public sealed record WorkableHttpCronSchedulePreviewResponse(
    IReadOnlyList<DateTimeOffset> Occurrences);
