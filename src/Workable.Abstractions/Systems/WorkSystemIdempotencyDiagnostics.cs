namespace Workable;

/// <summary>
/// Describes duplicate-subject queue requests rejected by idempotency coordination.
/// </summary>
/// <param name="DuplicateRejectionCount">The total number of queue requests rejected because an idempotency reservation already existed.</param>
/// <param name="LastDuplicateRejectedStorage">The coordination storage mode that rejected the most recent duplicate request.</param>
public sealed record WorkSystemIdempotencyDiagnostics(
    long DuplicateRejectionCount,
    WorkCoordinationStorage? LastDuplicateRejectedStorage);
