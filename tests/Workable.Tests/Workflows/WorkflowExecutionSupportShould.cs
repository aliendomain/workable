using Workable;

namespace Workable.Tests;

[Trait("Category", "Workflows")]
public sealed class WorkflowExecutionSupportShould
{
    [Fact]
    public async Task ReturnCompletedWhenNoOutstandingWorkersExist()
    {
        var completion = await WorkflowExecutionSupport.WaitForOutstanding([], CancellationToken.None);

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(WorkflowRunStatus.Completed, completion.Status);
    }

    [Fact]
    public async Task ReturnCompletedWithoutCreatingHandlesWhenNoOutstandingWorkersExist()
    {
        var createdHandles = 0;

        var completion = await WorkflowExecutionSupport.WaitForOutstanding(
            [],
            _ =>
            {
                Interlocked.Increment(ref createdHandles);
                throw new InvalidOperationException("No handles should be created.");
            },
            CancellationToken.None);

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(0, Volatile.Read(ref createdHandles));
    }

    [Fact]
    public async Task DistinctWorkerIdsBeforeWaitingForCompletion()
    {
        var workerId = WorkerId.New();
        var createdHandles = 0;

        var completion = await WorkflowExecutionSupport.WaitForOutstanding(
            [workerId, workerId],
            _ =>
            {
                Interlocked.Increment(ref createdHandles);
                return new TestWorkerHandle(
                    WorkQueueOutcome.Accepted(workerId),
                    workerId,
                    Task.FromResult(new WorkCompletion(WorkCompletionStatus.Completed, null, null, [])));
            },
            CancellationToken.None);

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(1, Volatile.Read(ref createdHandles));
    }

    [Theory]
    [InlineData(WorkCompletionStatus.Completed, WorkflowRunStatus.Completed)]
    [InlineData(WorkCompletionStatus.Canceled, WorkflowRunStatus.Canceled)]
    [InlineData(WorkCompletionStatus.Failed, WorkflowRunStatus.Failed)]
    [InlineData(WorkCompletionStatus.Interrupted, WorkflowRunStatus.Failed)]
    [InlineData(WorkCompletionStatus.NotFound, WorkflowRunStatus.NotFound)]
    [InlineData(WorkCompletionStatus.Invalid, WorkflowRunStatus.Invalid)]
    [InlineData(WorkCompletionStatus.Executing, WorkflowRunStatus.Invalid)]
    [InlineData(WorkCompletionStatus.Paused, WorkflowRunStatus.Invalid)]
    public void MapWorkerCompletionStatusesToWorkflowStatuses(
        WorkCompletionStatus status,
        WorkflowRunStatus expected)
    {
        Assert.Equal(expected, WorkflowExecutionSupport.ToWorkflowStatus(status));
    }

    [Fact]
    public void AddWorkflowIdentifiersPreservesExistingInputMetadata()
    {
        var input = WorkInput.Empty
            .WithSubject(new WorkSubjectId("order", "42"))
            .WithConcurrencyKey(new WorkConcurrencyKey("tenant", "acme"))
            .WithIdentifier(new WorkIdentifier("existing", "value"));

        var updated = WorkflowExecutionSupport.AddWorkflowIdentifiers(
            input,
            new WorkflowRunId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")),
            "workflow.demo",
            "dispatch");

        Assert.Equal(input.SubjectId, updated.SubjectId);
        Assert.Equal(input.ConcurrencyKey, updated.ConcurrencyKey);
        Assert.Contains(new WorkIdentifier("existing", "value"), updated.Identifiers!);
        Assert.Contains(new WorkIdentifier("workflow-definition", "workflow.demo"), updated.Identifiers!);
        Assert.Contains(new WorkIdentifier("workflow-step", "dispatch"), updated.Identifiers!);
        Assert.Contains(
            updated.Identifiers!,
            identifier => identifier.Type == "workflow-run" &&
                identifier.Value == "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    }

    private sealed class TestWorkerHandle(
        WorkQueueOutcome queueOutcome,
        WorkerId? workerId,
        Task<WorkCompletion> completion) : IWorkerHandle
    {
        public WorkQueueOutcome QueueOutcome { get; } = queueOutcome;

        public WorkerId? WorkerId { get; } = workerId;

        public Task<WorkCompletion> WaitForCompletion(CancellationToken cancellationToken = default)
            => completion;

        public async Task<WorkCompletion<TOutput>> WaitForCompletion<TOutput>(CancellationToken cancellationToken = default)
            => (await this.WaitForCompletion(cancellationToken)).ToTyped<TOutput>();
    }
}
