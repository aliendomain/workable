namespace Workable;

internal static class WorkQueries
{
    public static WorkerQueryDefinition Worker(WorkerId workerId)
        => new(workerId);

    public static WorkerIterationQueryDefinition WorkerIteration(WorkerIterationReference iteration)
        => new(iteration);

    public static WorkersQueryDefinition Workers(WorkerCriteria? criteria = null)
        => new(criteria ?? new WorkerCriteria());

    public static WorkerIterationsQueryDefinition WorkerIterations(WorkerIterationCriteria? criteria = null)
        => new(criteria ?? new WorkerIterationCriteria());

    public static WorkInfoByDefinitionIdQueryDefinition WorkInfo(WorkDefinitionId definitionId)
        => new(definitionId);

    public static WorkInfoByNameQueryDefinition WorkInfo(string name)
        => new(name);

    public static WorkDefinitionsQueryDefinition WorkDefinitions(WorkDefinitionCriteria? criteria = null)
        => new(criteria ?? new WorkDefinitionCriteria());

    public static WorkerKeysQueryDefinition WorkerKeys(WorkerKeyCriteria? criteria = null)
        => new(criteria ?? new WorkerKeyCriteria());

    public static WorkerKeyTypesQueryDefinition WorkerKeyTypes(WorkerKeyTypeCriteria? criteria = null)
        => new(criteria ?? new WorkerKeyTypeCriteria());

    public static WorkIterationKeysQueryDefinition WorkIterationKeys(WorkIterationKeyCriteria? criteria = null)
        => new(criteria ?? new WorkIterationKeyCriteria());

    public static WorkIterationKeyTypesQueryDefinition WorkIterationKeyTypes(WorkIterationKeyTypeCriteria? criteria = null)
        => new(criteria ?? new WorkIterationKeyTypeCriteria());

    public static WorkerStatusSummaryQueryDefinition WorkerStatusSummary(WorkerCriteria? criteria = null)
        => new(criteria);

    public static ComponentsQueryDefinition Components(WorkComponentCriteria? criteria = null)
        => new(criteria ?? new WorkComponentCriteria());

    public static ViewQueryDefinition View(string name, WorkViewCriteria? criteria = null)
        => new(name, criteria ?? new WorkViewCriteria());

    public static SystemOverviewQueryDefinition SystemOverview(WorkOverviewCriteria? criteria = null)
        => new(criteria);

    public static SystemThroughputQueryDefinition SystemThroughput(
        WorkOverviewCriteria? criteria = null,
        WorkThroughputCriteria? throughput = null)
        => new(criteria, throughput);

    public static SystemOverviewCountsQueryDefinition SystemOverviewCounts(WorkOverviewCriteria? criteria = null)
        => new(criteria);

    public static SystemWorkerCountsQueryDefinition SystemWorkerCounts(WorkOverviewCriteria? criteria = null)
        => new(criteria);

    public static SystemIterationCountsQueryDefinition SystemIterationCounts(WorkOverviewCriteria? criteria = null)
        => new(criteria);

    public static SystemCommonKeyTypesQueryDefinition SystemCommonKeyTypes(WorkOverviewCriteria? criteria = null)
        => new(criteria);

    public static SystemFailedWorkersQueryDefinition SystemFailedWorkers(WorkOverviewCriteria? criteria = null)
        => new(criteria);

    public static SystemFailedIterationsQueryDefinition SystemFailedIterations(WorkOverviewCriteria? criteria = null)
        => new(criteria);

    public static SystemCompletedIterationsQueryDefinition SystemCompletedIterations(WorkOverviewCriteria? criteria = null)
        => new(criteria);
}
