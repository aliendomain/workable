namespace Workable;

public sealed record WorkSystemShutdownWorker(
    WorkerId Id,
    WorkDefinitionId DefinitionId,
    string DefinitionName,
    string DefinitionCategory,
    WorkerState State,
    WorkSubjectId? SubjectId)
{
    public string Name => this.DefinitionName;

    public static WorkSystemShutdownWorker From(WorkerOverviewItem worker)
        => new(
            worker.Id,
            worker.DefinitionId,
            worker.DefinitionName,
            worker.Category,
            worker.State,
            worker.SubjectId);

    public static WorkSystemShutdownWorker From(WorkerSnapshot worker)
        => new(
            worker.Id,
            worker.DefinitionId,
            worker.DefinitionName,
            worker.DefinitionCategory,
            worker.State,
            worker.SubjectId);
}
