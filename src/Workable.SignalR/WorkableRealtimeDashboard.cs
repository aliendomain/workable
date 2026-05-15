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
    DateTimeOffset? OldestQueuedAt,
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
    public static WorkableRealtimeDashboard From(IWorkSystem system, WorkSystemDetails details)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(details);

        return new WorkableRealtimeDashboard(
            system.Id,
            details.SystemName,
            details.SystemState,
            details.DefinitionCount,
            details.ActiveWorkerCount,
            details.FinalWorkerCount,
            details.FailedWorkerCount,
            details.WorkerCountByState,
            details.OldestQueuedAt,
            details.CurrentIterationCount,
            details.CompletedIterationCount,
            details.FailedIterationCount,
            details.CanceledIterationCount,
            details.IterationCountByStatus,
            details.CommonKeyTypes,
            details.FailedWorkers,
            details.FailedIterations,
            details.CompletedIterations,
            DateTimeOffset.UtcNow);
    }
}
