namespace Workable;

/// <summary>
/// Represents an HTTP request to schedule work by definition name.
/// </summary>
/// <param name="Timing">When the work should first run and, optionally, recur.</param>
/// <param name="Work">The input and worker options to apply when each occurrence is queued.</param>
public sealed record WorkableHttpScheduleRequest(
    WorkScheduleTiming Timing,
    WorkableHttpWorkRequest? Work = null);
