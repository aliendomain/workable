namespace Workable;

public sealed record WorkSystemShutdownWorker(
    WorkerId Id,
    string DefinitionName,
    string DefinitionCategory,
    WorkerState State,
    WorkSubjectId? SubjectId)
{
    public string Name => this.DefinitionName;

    public static WorkSystemShutdownWorker From(WorkerOverviewItem worker)
        => new(
            worker.Id,
            worker.DefinitionName,
            worker.Category,
            worker.State,
            worker.SubjectId);

    public static WorkSystemShutdownWorker From(WorkerSnapshot worker)
        => new(
            worker.Id,
            worker.DefinitionName,
            worker.DefinitionCategory,
            worker.State,
            worker.SubjectId);
}
