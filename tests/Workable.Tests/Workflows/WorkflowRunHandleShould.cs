using Workable;

namespace Workable.Tests;

[Trait("Category", "Workflows")]
public sealed class WorkflowRunHandleShould
{
    [Fact]
    public async Task MapRejectedNotFoundStartToNotFoundCompletion()
    {
        var handle = WorkflowRunHandle.Rejected(WorkflowStartOutcome.NotFound("workflow.missing"));

        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkflowRunStatus.NotFound, completion.Status);
    }

    [Fact]
    public async Task MapRejectedUnauthorizedStartToUnauthorizedCompletion()
    {
        var handle = WorkflowRunHandle.Rejected(WorkflowStartOutcome.Unauthorized("workflow.secured"));

        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkflowRunStatus.Unauthorized, completion.Status);
    }

    [Fact]
    public async Task MapRejectedInvalidStartToInvalidCompletion()
    {
        var handle = WorkflowRunHandle.Rejected(WorkflowStartOutcome.Invalid(
            [WorkMessage.Error("workflow.invalid", "Invalid workflow.")] ));

        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkflowRunStatus.Invalid, completion.Status);
        Assert.Contains(completion.Messages, message => message.Code == "workflow.invalid");
    }
}
