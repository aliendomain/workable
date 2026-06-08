namespace Workable;

/// <summary>
/// Represents a compact whole-system operational snapshot.
/// </summary>
/// <param name="SystemName">The system name, or <see langword="null"/> for the default unnamed system.</param>
/// <param name="SystemState">The current lifecycle state of the system.</param>
/// <param name="DefinitionCount">The number of registered definitions in the scoped system slice.</param>
/// <param name="ActiveWorkerCount">The number of active workers in the scoped system slice.</param>
/// <param name="FinalWorkerCount">The number of final-state workers in the scoped system slice.</param>
/// <param name="FailedWorkerCount">The number of failed workers in the scoped system slice.</param>
/// <param name="WorkerCountByState">Worker counts grouped by worker state.</param>
/// <param name="OldestQueuedAt">The oldest queued worker creation time, when one exists.</param>
/// <param name="CurrentIterationCount">The number of currently executing iterations in the scoped system slice.</param>
/// <param name="CompletedIterationCount">The number of completed iterations in the scoped system slice.</param>
/// <param name="FailedIterationCount">The number of failed iterations in the scoped system slice.</param>
/// <param name="CanceledIterationCount">The number of canceled iterations in the scoped system slice.</param>
/// <param name="IterationCountByStatus">Iteration counts grouped by completion status.</param>
/// <param name="CommonKeyTypes">The most common relationship-key types in the scoped system slice.</param>
/// <param name="Throughput">Optional throughput data when the caller requested it.</param>
/// <param name="FailedWorkers">A compact list of currently failed workers in the scoped system slice.</param>
/// <param name="FailedIterations">A compact list of failed iterations in the scoped system slice.</param>
/// <param name="CompletedIterations">A compact list of recently completed iterations in the scoped system slice.</param>
public sealed record WorkSystemDetails(
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
    WorkSystemThroughput? Throughput,
    IReadOnlyList<WorkerOverviewItem> FailedWorkers,
    IReadOnlyList<WorkerIterationOverviewItem> FailedIterations,
    IReadOnlyList<WorkerIterationOverviewItem> CompletedIterations) : IWorkQueryResult;

/// <summary>
/// Represents one category item in a catalog-oriented system summary.
/// </summary>
/// <param name="Label">The display label for the category item.</param>
/// <param name="Path">The full category path.</param>
/// <param name="Count">The number of definitions in the category item.</param>
public sealed record WorkSystemCatalogCategoryItem(
    string Label,
    string Path,
    int Count);

/// <summary>
/// Represents one definition item in a compact system summary.
/// </summary>
/// <param name="Name">The definition name.</param>
/// <param name="Category">The definition category path.</param>
public sealed record WorkSystemDefinitionItem(
    string Name,
    string Category);
