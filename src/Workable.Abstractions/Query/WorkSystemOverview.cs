namespace Workable;

public sealed record WorkSystemOverview(
    string? SystemName,
    WorkSystemState SystemState,
    int DefinitionCount,
    IReadOnlyList<WorkOverviewCatalogCategoryItem> CatalogCategories,
    IReadOnlyList<WorkOverviewDefinitionItem> CatalogDefinitions,
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
    WorkSystemThroughput? Throughput,
    IReadOnlyList<WorkerOverviewItem> FailedWorkers,
    IReadOnlyList<WorkerIterationOverviewItem> FailedIterations,
    IReadOnlyList<WorkerIterationOverviewItem> CompletedIterations);

public sealed record WorkOverviewCatalogCategoryItem(
    string Label,
    string Path,
    int Count);

public sealed record WorkOverviewDefinitionItem(
    WorkDefinitionId Id,
    string Name,
    string Category);
