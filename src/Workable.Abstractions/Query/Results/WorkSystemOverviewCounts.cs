namespace Workable;

public sealed record WorkSystemOverviewCounts(
    string? SystemName,
    WorkSystemState SystemState,
    int DefinitionCount,
    int ActiveWorkerCount,
    int FinalWorkerCount,
    int FailedWorkerCount,
    int CurrentIterationCount,
    int CompletedIterationCount,
    int FailedIterationCount,
    int CanceledIterationCount) : IWorkQueryResult;
