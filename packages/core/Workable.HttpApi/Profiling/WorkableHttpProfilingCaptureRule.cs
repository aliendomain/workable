namespace Workable;

/// <summary>
/// Describes one temporary full-profile capture rule.
/// </summary>
public sealed record WorkableHttpProfilingCaptureRule(
    Guid Id,
    string? DefinitionName,
    string? ActorId,
    int MaximumMatches,
    int RemainingMatches,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    WorkActor CreatedBy);

/// <summary>
/// Describes the current automatic-instrumentation capture configuration and temporary rules.
/// </summary>
public sealed record WorkableHttpProfilingCaptureState(
    int MaximumAutomaticInstrumentationNodes,
    IReadOnlyList<WorkableHttpProfilingCaptureRule> Rules);

/// <summary>
/// Requests temporary full-profile capture for matching future workers.
/// </summary>
public sealed record WorkableHttpCreateProfilingCaptureRuleRequest(
    string? DefinitionName = null,
    string? ActorId = null,
    int MaximumMatches = 1,
    int ExpiresAfterMinutes = 30,
    string? Description = null);
