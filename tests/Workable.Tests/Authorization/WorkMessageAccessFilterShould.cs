using Workable;

namespace Workable.Tests;

public sealed class WorkMessageAccessFilterShould
{
    [Theory]
    [InlineData("workable.execution.exception", "Work execution failed with an unhandled exception.")]
    [InlineData("workable.workflow.execution_exception", "Workflow execution failed with an unhandled exception.")]
    [InlineData("workable.queue_durability.duplicate", "The durable queue rejected this request as a duplicate.")]
    [InlineData("workable.queue_durability.store_unreachable", "The persistence store required for durable queueing is currently unavailable.")]
    [InlineData("workable.idempotency.duplicate_subject", "A work request with the same idempotency subject already exists.")]
    [InlineData("workable.idempotency.persistence_store_unreachable", "The persistence store required for idempotency is currently unavailable.")]
    public void RedactSensitiveDetailsWithoutRetainedReadAccess(
        string code,
        string expectedText)
    {
        var original = new WorkMessage(
            code,
            WorkMessageSeverity.Error,
            "secret exception",
            Metadata: new Dictionary<string, object?>
            {
                ["exceptionStackTrace"] = "secret stack",
            });

        var filtered = WorkMessageAccessFilter.Apply([original], canReadRetainedDetails: false);
        var message = Assert.Single(filtered);

        Assert.Equal(code, message.Code);
        Assert.Equal(expectedText, message.Text);
        Assert.Null(message.Metadata);
    }

    [Fact]
    public void PreserveMessagesForCallersWithRetainedReadAccess()
    {
        IReadOnlyList<WorkMessage> messages =
        [
            WorkMessage.Error(
                "workable.workflow.execution_exception",
                "diagnostic detail",
                "workflow.execution"),
        ];

        var filtered = WorkMessageAccessFilter.Apply(messages, canReadRetainedDetails: true);

        Assert.Same(messages, filtered);
    }
}
