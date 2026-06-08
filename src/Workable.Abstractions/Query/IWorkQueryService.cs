namespace Workable;

/// <summary>
/// Provides read-only access to registered definitions, workers, iterations, and system-level operational summaries.
/// </summary>
/// <remarks>
/// <see cref="Worker(WorkerId, CancellationToken)"/> and <see cref="WorkerIteration(WorkerIterationReference, CancellationToken)"/>
/// return authoritative retained detail. List, search, summary, and system-level methods read from Workable's
/// projected in-memory read model and are therefore eventually consistent.
/// </remarks>
public interface IWorkQueryService
{
    /// <summary>
    /// Retrieves one worker snapshot by id from the authoritative retained worker store.
    /// </summary>
    /// <param name="workerId">The id of the worker to retrieve.</param>
    /// <param name="cancellationToken">A token that cancels the query before it completes.</param>
    /// <returns>A task that returns the worker snapshot, or <see langword="null"/> when no matching worker exists.</returns>
    Task<WorkerSnapshot?> Worker(
        WorkerId workerId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves one iteration snapshot by worker id and sequence from the authoritative retained worker store.
    /// </summary>
    /// <param name="iteration">The worker iteration reference to retrieve.</param>
    /// <param name="cancellationToken">A token that cancels the query before it completes.</param>
    /// <returns>A task that returns the iteration snapshot, or <see langword="null"/> when no matching iteration exists.</returns>
    Task<WorkerIterationSnapshot?> WorkerIteration(
        WorkerIterationReference iteration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves worker overview rows that match the supplied criteria.
    /// </summary>
    /// <param name="criteria">Optional filters, paging, and sort settings for the worker query.</param>
    /// <param name="cancellationToken">A token that cancels the query before it completes.</param>
    /// <returns>A task that returns the matching worker overview rows plus paging metadata.</returns>
    Task<WorkerQueryResult> Workers(
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves iteration overview rows that match the supplied criteria.
    /// </summary>
    /// <param name="criteria">Optional filters, paging, and sort settings for the iteration query.</param>
    /// <param name="cancellationToken">A token that cancels the query before it completes.</param>
    /// <returns>A task that returns the matching iteration overview rows plus paging metadata.</returns>
    Task<WorkerIterationQueryResult> WorkerIterations(
        WorkerIterationCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves one definition plus its current worker rollup.
    /// </summary>
    /// <param name="name">The definition name to retrieve.</param>
    /// <param name="cancellationToken">A token that cancels the query before it completes.</param>
    /// <returns>A task that returns the definition information, or <see langword="null"/> when no matching definition exists.</returns>
    Task<WorkInfo?> WorkInfo(
        string name,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves registered definitions that match the supplied criteria.
    /// </summary>
    /// <param name="criteria">Optional filters for the definition query.</param>
    /// <param name="cancellationToken">A token that cancels the query before it completes.</param>
    /// <returns>A task that returns the matching definitions.</returns>
    Task<WorkDefinitionQueryResult> WorkDefinitions(
        WorkDefinitionCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches worker relationship keys and returns the matching key values plus attached worker overview rows.
    /// </summary>
    /// <param name="criteria">Optional filters, search text, paging, and sort settings for the key query.</param>
    /// <param name="cancellationToken">A token that cancels the query before it completes.</param>
    /// <returns>A task that returns the matching worker keys.</returns>
    Task<WorkerKeyQueryResult> WorkerKeys(
        WorkerKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches worker relationship-key types and returns matching types plus attached worker overview rows.
    /// </summary>
    /// <param name="criteria">Optional filters, search text, paging, and sort settings for the key-type query.</param>
    /// <param name="cancellationToken">A token that cancels the query before it completes.</param>
    /// <returns>A task that returns the matching worker key types.</returns>
    Task<WorkerKeyTypeQueryResult> WorkerKeyTypes(
        WorkerKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches iteration relationship keys and returns the matching key values plus attached iteration overview rows.
    /// </summary>
    /// <param name="criteria">Optional filters, search text, paging, and sort settings for the key query.</param>
    /// <param name="cancellationToken">A token that cancels the query before it completes.</param>
    /// <returns>A task that returns the matching iteration keys.</returns>
    Task<WorkIterationKeyQueryResult> WorkIterationKeys(
        WorkIterationKeyCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Searches iteration relationship-key types and returns matching types plus attached iteration overview rows.
    /// </summary>
    /// <param name="criteria">Optional filters, search text, paging, and sort settings for the key-type query.</param>
    /// <param name="cancellationToken">A token that cancels the query before it completes.</param>
    /// <returns>A task that returns the matching iteration key types.</returns>
    Task<WorkIterationKeyTypeQueryResult> WorkIterationKeyTypes(
        WorkIterationKeyTypeCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Summarizes worker counts by state for workers that match the supplied criteria.
    /// </summary>
    /// <param name="criteria">Optional filters that restrict which workers contribute to the summary.</param>
    /// <param name="cancellationToken">A token that cancels the query before it completes.</param>
    /// <returns>A task that returns the worker status summary.</returns>
    Task<WorkerStatusSummary> WorkerStatusSummary(
        WorkerCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a compact whole-system operational snapshot.
    /// </summary>
    /// <param name="criteria">Optional filters that scope the system snapshot to definitions or categories.</param>
    /// <param name="cancellationToken">A token that cancels the query before it completes.</param>
    /// <returns>A task that returns the system details snapshot.</returns>
    Task<WorkSystemDetails> SystemDetails(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves whole-system throughput buckets and related live execution summaries.
    /// </summary>
    /// <param name="criteria">Optional filters that scope the system snapshot to definitions or categories.</param>
    /// <param name="throughput">Optional throughput window and bucket sizing settings.</param>
    /// <param name="cancellationToken">A token that cancels the query before it completes.</param>
    /// <returns>A task that returns the system throughput snapshot.</returns>
    Task<WorkSystemThroughput> SystemThroughput(
        WorkSystemCriteria? criteria = null,
        WorkThroughputCriteria? throughput = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves a compact whole-system throughput summary without the full bucket series.
    /// </summary>
    /// <param name="criteria">Optional filters that scope the system snapshot to definitions or categories.</param>
    /// <param name="throughput">Optional throughput window and bucket sizing settings.</param>
    /// <param name="cancellationToken">A token that cancels the query before it completes.</param>
    /// <returns>A task that returns the system throughput summary.</returns>
    Task<WorkSystemThroughputSummary> SystemThroughputSummary(
        WorkSystemCriteria? criteria = null,
        WorkThroughputCriteria? throughput = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves whole-system worker counts for the supplied system criteria.
    /// </summary>
    /// <param name="criteria">Optional filters that scope the count snapshot to definitions or categories.</param>
    /// <param name="cancellationToken">A token that cancels the query before it completes.</param>
    /// <returns>A task that returns whole-system worker counts.</returns>
    Task<WorkSystemWorkerCounts> SystemWorkerCounts(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves whole-system iteration counts for the supplied system criteria.
    /// </summary>
    /// <param name="criteria">Optional filters that scope the count snapshot to definitions or categories.</param>
    /// <param name="cancellationToken">A token that cancels the query before it completes.</param>
    /// <returns>A task that returns whole-system iteration counts.</returns>
    Task<WorkSystemIterationCounts> SystemIterationCounts(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the most common relationship-key types present in the scoped system slice.
    /// </summary>
    /// <param name="criteria">Optional filters that scope the facet query to definitions or categories.</param>
    /// <param name="cancellationToken">A token that cancels the query before it completes.</param>
    /// <returns>A task that returns the common key-type facets.</returns>
    Task<WorkIterationKeyTypeFacetQueryResult> SystemCommonKeyTypes(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves the current failed workers in the scoped system slice.
    /// </summary>
    /// <param name="criteria">Optional filters that scope the system snapshot to definitions or categories.</param>
    /// <param name="cancellationToken">A token that cancels the query before it completes.</param>
    /// <returns>A task that returns the failed-worker snapshot.</returns>
    Task<WorkSystemFailedWorkers> SystemFailedWorkers(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves failed iteration overview rows in the scoped system slice.
    /// </summary>
    /// <param name="criteria">Optional filters that scope the query to definitions or categories.</param>
    /// <param name="cancellationToken">A token that cancels the query before it completes.</param>
    /// <returns>A task that returns failed iteration overview rows.</returns>
    Task<WorkerIterationOverviewQueryResult> SystemFailedIterations(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves recently completed iteration overview rows in the scoped system slice.
    /// </summary>
    /// <param name="criteria">Optional filters that scope the query to definitions or categories.</param>
    /// <param name="cancellationToken">A token that cancels the query before it completes.</param>
    /// <returns>A task that returns completed iteration overview rows.</returns>
    Task<WorkerIterationOverviewQueryResult> SystemCompletedIterations(
        WorkSystemCriteria? criteria = null,
        CancellationToken cancellationToken = default);
}
