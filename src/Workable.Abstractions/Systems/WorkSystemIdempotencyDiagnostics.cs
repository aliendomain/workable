namespace Workable;

public sealed record WorkSystemIdempotencyDiagnostics(
    long DuplicateRejectionCount,
    WorkIdempotencyStorage? LastDuplicateRejectedStorage);
