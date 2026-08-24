using System.Reflection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Workflows")]
public sealed class WorkflowChildReceiptShould
{
    [Fact]
    public void MapEveryRetainedWorkerStateToItsCompletionStatus()
    {
        foreach (var state in Enum.GetValues<WorkerState>())
        {
            var receipt = new WorkflowChildReceipt(
                WorkerId.New(),
                "step",
                "work",
                state,
                DateTimeOffset.UtcNow,
                [],
                null);
            var expected = state switch
            {
                WorkerState.Completed => WorkCompletionStatus.Completed,
                WorkerState.Failed => WorkCompletionStatus.Failed,
                WorkerState.Paused => WorkCompletionStatus.Paused,
                WorkerState.Interrupted => WorkCompletionStatus.Interrupted,
                WorkerState.Canceled => WorkCompletionStatus.Canceled,
                _ => WorkCompletionStatus.Invalid,
            };

            Assert.Equal(expected, receipt.CompletionStatus);
        }

        var unknown = new WorkflowChildReceipt(
            WorkerId.New(), "step", "work", (WorkerState)int.MaxValue, DateTimeOffset.UtcNow, [], null);
        Assert.Equal(WorkCompletionStatus.Invalid, unknown.CompletionStatus);
    }

    [Fact]
    public void ValidateAndSnapshotEveryWorkflowRunIdentifierShape()
    {
        var runId = WorkflowRunId.New();
        Assert.Null(WorkflowProvenanceRules.SnapshotInput(null));
        Assert.False(WorkflowProvenanceRules.ContainsMalformedIdentifier(null));
        Assert.False(WorkflowProvenanceRules.ContainsRunIdentifier(null));
        Assert.False(WorkflowProvenanceRules.HasExactRunIdentifier(null, runId));

        var empty = WorkInput.Empty;
        Assert.Equal(empty, WorkflowProvenanceRules.SnapshotInput(empty));
        Assert.False(WorkflowProvenanceRules.ContainsMalformedIdentifier(empty));
        Assert.False(WorkflowProvenanceRules.ContainsRunIdentifier(empty));
        Assert.False(WorkflowProvenanceRules.HasExactRunIdentifier(empty, runId));

        var exact = empty.WithIdentifier(new WorkIdentifier("WORKFLOW-RUN", runId.ToString()));
        var snapshot = WorkflowProvenanceRules.SnapshotInput(exact);
        Assert.NotSame(exact, snapshot);
        Assert.True(WorkflowProvenanceRules.ContainsRunIdentifier(snapshot));
        Assert.True(WorkflowProvenanceRules.HasExactRunIdentifier(snapshot, runId));

        var wrong = empty.WithIdentifier(new WorkIdentifier("workflow-run", WorkflowRunId.New().ToString()));
        var duplicate = exact.WithIdentifier(new WorkIdentifier("workflow-run", runId.ToString()));
        Assert.False(WorkflowProvenanceRules.HasExactRunIdentifier(wrong, runId));
        Assert.False(WorkflowProvenanceRules.HasExactRunIdentifier(duplicate, runId));
        Assert.True(WorkflowProvenanceRules.ContainsMalformedIdentifier(
            empty.WithIdentifier(new WorkIdentifier(" ", "value"))));
        Assert.True(WorkflowProvenanceRules.ContainsMalformedIdentifier(
            empty.WithIdentifier(new WorkIdentifier("type", " "))));
    }

    [Fact]
    public void ProjectWorkflowOriginActorsFromEveryKnownIdentityShape()
    {
        var actorPayload = typeof(WorkflowEventPayloads).GetNestedType(
            "WorkflowEventOriginActorPayload",
            BindingFlags.NonPublic)!;
        var from = actorPayload.GetMethod("From", BindingFlags.Public | BindingFlags.Static)!;

        Assert.Null(from.Invoke(null, [WorkActor.Unknown]));
        Assert.NotNull(from.Invoke(null, [new WorkActor("actor-id")]));
        Assert.NotNull(from.Invoke(null, [new WorkActor(null, "Actor Name")]));
        Assert.NotNull(from.Invoke(null, [new WorkActor(null, null, "actor@example.test")]));
    }
}
