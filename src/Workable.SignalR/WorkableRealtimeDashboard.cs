namespace Workable;
public sealed record WorkableRealtimeDashboard(
    WorkSystemId SystemId,
    string? SystemName,
    WorkSystemState SystemState,
    int DefinitionCount,
    int ActiveWorkerCount,
    int FinalWorkerCount,
    int FailedWorkerCount,
    IReadOnlyDictionary<WorkerState, int> WorkerCountByState,
    int CurrentIterationCount,
    int CompletedIterationCount,
    int FailedIterationCount,
    int CanceledIterationCount,
    IReadOnlyDictionary<WorkCompletionStatus, int> IterationCountByStatus,
    IReadOnlyList<WorkIterationKeyTypeFacet> CommonKeyTypes,
    IReadOnlyList<WorkerOverviewItem> FailedWorkers,
    IReadOnlyList<WorkerIterationOverviewItem> FailedIterations,
    IReadOnlyList<WorkerIterationOverviewItem> CompletedIterations,
    DateTimeOffset UpdatedAt)
{
    public static WorkableRealtimeDashboard From(IWorkSystem system, WorkSystemOverview overview)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(overview);

        return new WorkableRealtimeDashboard(
            system.Id,
            overview.SystemName,
            overview.SystemState,
            overview.DefinitionCount,
            overview.ActiveWorkerCount,
            overview.FinalWorkerCount,
            overview.FailedWorkerCount,
            overview.WorkerCountByState,
            overview.CurrentIterationCount,
            overview.CompletedIterationCount,
            overview.FailedIterationCount,
            overview.CanceledIterationCount,
            overview.IterationCountByStatus,
            overview.CommonKeyTypes,
            overview.FailedWorkers,
            overview.FailedIterations,
            overview.CompletedIterations,
            DateTimeOffset.UtcNow);
    }
}
