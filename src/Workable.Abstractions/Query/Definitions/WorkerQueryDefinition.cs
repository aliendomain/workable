namespace Workable;

internal sealed record WorkerQueryDefinition(WorkerId WorkerId) :
    WorkQueryDefinition<WorkerSnapshot>("worker");
