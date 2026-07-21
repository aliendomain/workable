using Workable;

namespace Workable.Tests;

[Trait("Category", "HttpApi")]
public sealed class WorkableHttpCriteriaShould
{
    [Fact]
    public void PreserveEveryWorkerQueryFilterWhenMappingToTheCoreContract()
    {
        var subject = new WorkSubjectId("invoice", "inv-100");
        var concurrencyKey = new WorkConcurrencyKey("tenant", "tenant-7");
        var identifier = new WorkIdentifier("claim", "claim-42");
        var createdFrom = new DateTimeOffset(2026, 7, 1, 1, 2, 3, TimeSpan.Zero);
        var createdTo = createdFrom.AddHours(1);
        var updatedFrom = createdFrom.AddMinutes(10);
        var updatedTo = createdFrom.AddMinutes(50);
        var configuration = new WorkerConfigurationCriteria(
            RecurrenceEnabled: true,
            ConcurrencyEnabled: false,
            ProfilingEnabled: true);
        var source = new WorkableHttpWorkerCriteria(
            DefinitionName: "billing.close",
            SubjectId: subject,
            ConcurrencyKey: concurrencyKey,
            Identifier: identifier,
            States: [WorkerState.Running, WorkerState.Failed],
            Configuration: configuration,
            CreatedFrom: createdFrom,
            CreatedTo: createdTo,
            UpdatedFrom: updatedFrom,
            UpdatedTo: updatedTo,
            Sort: WorkerCriteriaSort.UpdatedAt,
            Direction: WorkCriteriaSortDirection.Ascending,
            Skip: 12,
            Take: 34,
            Category: "Billing:Close",
            IncludeSubcategories: false);

        var actual = source.ToWorkerCriteria();

        Assert.Equal(source.DefinitionName, actual.DefinitionName);
        Assert.Equal(subject, actual.SubjectId);
        Assert.Equal(concurrencyKey, actual.ConcurrencyKey);
        Assert.Equal(identifier, actual.Identifier);
        Assert.Equal([WorkerState.Running, WorkerState.Failed], actual.States!.Order());
        Assert.Same(configuration, actual.Configuration);
        Assert.Equal(createdFrom, actual.CreatedFrom);
        Assert.Equal(createdTo, actual.CreatedTo);
        Assert.Equal(updatedFrom, actual.UpdatedFrom);
        Assert.Equal(updatedTo, actual.UpdatedTo);
        Assert.Equal(WorkerCriteriaSort.UpdatedAt, actual.Sort);
        Assert.Equal(WorkCriteriaSortDirection.Ascending, actual.Direction);
        Assert.Equal(12, actual.Skip);
        Assert.Equal(34, actual.Take);
        Assert.Equal("Billing:Close", actual.Category);
        Assert.False(actual.IncludeSubcategories);
    }

    [Fact]
    public void PreserveEveryIterationQueryFilterWhenMappingToTheCoreContract()
    {
        var workerId = WorkerId.New();
        var subject = new WorkSubjectId("invoice", "inv-100");
        var concurrencyKey = new WorkConcurrencyKey("tenant", "tenant-7");
        var identifier = new WorkIdentifier("claim", "claim-42");
        var startedFrom = new DateTimeOffset(2026, 7, 1, 1, 2, 3, TimeSpan.Zero);
        var startedTo = startedFrom.AddHours(1);
        var completedFrom = startedFrom.AddMinutes(10);
        var completedTo = startedFrom.AddMinutes(50);
        var source = new WorkableHttpWorkerIterationCriteria(
            WorkerId: workerId,
            DefinitionName: "billing.close",
            Category: "Billing:Close",
            SubjectId: subject,
            ConcurrencyKey: concurrencyKey,
            Identifier: identifier,
            Statuses: [WorkCompletionStatus.Completed, WorkCompletionStatus.Failed],
            StartedFrom: startedFrom,
            StartedTo: startedTo,
            CompletedFrom: completedFrom,
            CompletedTo: completedTo,
            Sort: WorkerIterationCriteriaSort.StartedAt,
            Direction: WorkCriteriaSortDirection.Ascending,
            Skip: 23,
            Take: 45);

        var actual = source.ToWorkerIterationCriteria();

        Assert.Equal(workerId, actual.WorkerId);
        Assert.Equal(source.DefinitionName, actual.DefinitionName);
        Assert.Equal(source.Category, actual.Category);
        Assert.Equal(subject, actual.SubjectId);
        Assert.Equal(concurrencyKey, actual.ConcurrencyKey);
        Assert.Equal(identifier, actual.Identifier);
        Assert.Equal([WorkCompletionStatus.Completed, WorkCompletionStatus.Failed], actual.Statuses!.Order());
        Assert.Equal(startedFrom, actual.StartedFrom);
        Assert.Equal(startedTo, actual.StartedTo);
        Assert.Equal(completedFrom, actual.CompletedFrom);
        Assert.Equal(completedTo, actual.CompletedTo);
        Assert.Equal(WorkerIterationCriteriaSort.StartedAt, actual.Sort);
        Assert.Equal(WorkCriteriaSortDirection.Ascending, actual.Direction);
        Assert.Equal(23, actual.Skip);
        Assert.Equal(45, actual.Take);
    }
}
