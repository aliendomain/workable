namespace Workable;

public sealed record WorkerKeyDescriptor(
    WorkKeyKind Kind,
    string Type,
    string Value,
    IReadOnlyList<WorkerOverviewItem> Workers);
