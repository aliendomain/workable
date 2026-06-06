using Workable;

namespace Workable.Tests;

[Trait("Category", "WorkOutcomes")]
public sealed class WorkOutcomeTests
{
    [Fact]
    public void WorkQueueOutcomeAcceptedIncludesIdentityAndMessages()
    {
        var workerId = WorkerId.New();
        var messages = new[] { WorkMessage.Info("sample.accepted", "Accepted.") };

        var outcome = WorkQueueOutcome.Accepted(workerId, messages);

        Assert.Equal(WorkQueueStatus.Accepted, outcome.Status);
        Assert.True(outcome.IsAccepted);
        Assert.Equal(workerId, outcome.WorkerId);
        Assert.Equal(messages, outcome.Messages);
    }

    [Fact]
    public void WorkQueueOutcomeNotFoundCreatesStructuredMessage()
    {
        var outcome = WorkQueueOutcome.NotFound("missing-work");

        Assert.Equal(WorkQueueStatus.NotFound, outcome.Status);
        Assert.False(outcome.IsAccepted);
        Assert.Null(outcome.WorkerId);
        var message = Assert.Single(outcome.Messages);
        Assert.Equal("workable.definition.not_found", message.Code);
        Assert.Equal(WorkMessageSeverity.Error, message.Severity);
        Assert.Equal("definition", message.Target);
        Assert.Contains("missing-work", message.Text);
    }

    [Fact]
    public void WorkQueueOutcomeInvalidIncludesMessages()
    {
        var messages = new[] { WorkMessage.Error("sample.invalid", "Invalid.") };

        var outcome = WorkQueueOutcome.Invalid(messages);

        Assert.Equal(WorkQueueStatus.Invalid, outcome.Status);
        Assert.False(outcome.IsAccepted);
        Assert.Null(outcome.WorkerId);
        Assert.Equal(messages, outcome.Messages);
    }

    [Fact]
    public void WorkQueueOutcomeUnauthorizedIncludesMessages()
    {
        var outcome = WorkQueueOutcome.Unauthorized("secured-work");

        Assert.Equal(WorkQueueStatus.Unauthorized, outcome.Status);
        Assert.False(outcome.IsAccepted);
        Assert.Null(outcome.WorkerId);
        var message = Assert.Single(outcome.Messages);
        Assert.Equal("workable.definition.unauthorized", message.Code);
        Assert.Equal("definition.authorization", message.Target);
        Assert.Contains("secured-work", message.Text);
    }

    [Fact]
    public void WorkActionOutcomeAcceptedIncludesSnapshotAndMessages()
    {
        var worker = CreateWorkerSnapshot(WorkerState.Running);
        var messages = new[] { WorkMessage.Info("sample.started", "Started.") };

        var outcome = WorkActionOutcome.Accepted(WorkAction.Start, worker, messages);

        Assert.Equal(WorkActionStatus.Accepted, outcome.Status);
        Assert.True(outcome.IsAccepted);
        Assert.Equal(WorkAction.Start, outcome.Action);
        Assert.Equal(worker.Id, outcome.WorkerId);
        Assert.Same(worker, outcome.Worker);
        Assert.Equal(messages, outcome.Messages);
    }

    [Fact]
    public void WorkActionOutcomeNotFoundCreatesStructuredMessage()
    {
        var workerId = WorkerId.New();

        var outcome = WorkActionOutcome.NotFound(WorkAction.Cancel, workerId);

        Assert.Equal(WorkActionStatus.NotFound, outcome.Status);
        Assert.False(outcome.IsAccepted);
        Assert.Equal(WorkAction.Cancel, outcome.Action);
        Assert.Equal(workerId, outcome.WorkerId);
        Assert.Null(outcome.Worker);
        var message = Assert.Single(outcome.Messages);
        Assert.Equal("workable.worker.not_found", message.Code);
        Assert.Equal(WorkMessageSeverity.Error, message.Severity);
        Assert.Equal("worker", message.Target);
        Assert.Contains(workerId.ToString(), message.Text);
    }

    [Fact]
    public void WorkActionOutcomeInvalidIncludesWorkerAndMessages()
    {
        var worker = CreateWorkerSnapshot(WorkerState.Completed);
        var messages = new[] { WorkMessage.Error("sample.invalid", "Invalid.") };

        var outcome = WorkActionOutcome.Invalid(WorkAction.Pause, worker, messages);

        Assert.Equal(WorkActionStatus.Invalid, outcome.Status);
        Assert.False(outcome.IsAccepted);
        Assert.Equal(WorkAction.Pause, outcome.Action);
        Assert.Equal(worker.Id, outcome.WorkerId);
        Assert.Same(worker, outcome.Worker);
        Assert.Equal(messages, outcome.Messages);
    }

    [Fact]
    public void WorkActionOutcomeUnauthorizedCreatesStructuredMessage()
    {
        var workerId = WorkerId.New();

        var outcome = WorkActionOutcome.Unauthorized(WorkAction.Cancel, workerId);

        Assert.Equal(WorkActionStatus.Unauthorized, outcome.Status);
        Assert.False(outcome.IsAccepted);
        Assert.Equal(WorkAction.Cancel, outcome.Action);
        Assert.Equal(workerId, outcome.WorkerId);
        Assert.Null(outcome.Worker);
        var message = Assert.Single(outcome.Messages);
        Assert.Equal("workable.worker.unauthorized", message.Code);
        Assert.Equal("worker.authorization", message.Target);
        Assert.Contains(workerId.ToString(), message.Text);
    }

    [Fact]
    public void WorkActionOutcomeConflictIncludesWorkerAndMessages()
    {
        var worker = CreateWorkerSnapshot(WorkerState.Pausing);
        var messages = new[] { WorkMessage.Error("sample.conflict", "Conflict.") };

        var outcome = WorkActionOutcome.Conflict(WorkAction.Pause, worker, messages);

        Assert.Equal(WorkActionStatus.Conflict, outcome.Status);
        Assert.False(outcome.IsAccepted);
        Assert.Equal(WorkAction.Pause, outcome.Action);
        Assert.Equal(worker.Id, outcome.WorkerId);
        Assert.Same(worker, outcome.Worker);
        Assert.Equal(messages, outcome.Messages);
    }

    [Fact]
    public void WorkCompletionReportsSuccessfulCompletionOnlyForCompletedStatus()
    {
        var worker = CreateWorkerSnapshot(WorkerState.Completed);
        var completed = new WorkCompletion(WorkCompletionStatus.Completed, worker, WorkOutput.Empty, []);
        var failed = new WorkCompletion(WorkCompletionStatus.Failed, worker, null, []);

        Assert.True(completed.IsCompletedSuccessfully);
        Assert.False(failed.IsCompletedSuccessfully);
    }

    private static WorkerSnapshot CreateWorkerSnapshot(WorkerState state)
        => new(
            WorkerId.New(),
            3,
            5,
            "sample.work",
            WorkDefinitionMetadataDefaults.Category,
            null,
            null,
            new HashSet<WorkIdentifier>(),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            state,
            WorkInput.Empty,
            null,
            WorkerOptions.Default,
            WorkConfiguration.Default,
            [],
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
}
