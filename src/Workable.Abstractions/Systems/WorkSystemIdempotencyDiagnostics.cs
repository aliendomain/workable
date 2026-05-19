namespace Workable;

public sealed record WorkSystemIdempotencyDiagnostics(
    long DuplicateRejectionCount,
    WorkCoordinationStorage? LastDuplicateRejectedStorage);
