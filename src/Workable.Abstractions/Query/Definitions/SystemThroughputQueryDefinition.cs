namespace Workable;

internal sealed record SystemThroughputQueryDefinition(WorkOverviewCriteria? Criteria, WorkThroughputCriteria? Throughput) :
    WorkQueryDefinition<WorkSystemThroughput>("systemThroughput");
