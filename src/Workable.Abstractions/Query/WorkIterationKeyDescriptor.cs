namespace Workable;

public sealed record WorkIterationKeyDescriptor(
    WorkKeyKind Kind,
    string Type,
    string Value,
    IReadOnlyList<WorkerIterationOverviewItem> Iterations);
