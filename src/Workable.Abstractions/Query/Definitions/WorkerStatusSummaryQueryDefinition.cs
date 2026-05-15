namespace Workable;

internal sealed record WorkerStatusSummaryQueryDefinition(WorkerCriteria? Criteria) :
    WorkQueryDefinition<WorkerStatusSummary>("workerStatusSummary");
