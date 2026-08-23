using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Queueing")]
public sealed class WorkQueueServiceShould
{
    [Fact]
    public async Task ValidateDelegatedAndWorkflowChildInputsBeforeQueueing()
    {
        var definition = WorkDefinition.Create("queue.internal.validation");
        await using var system = CreateSystem(definition);
        var queue = Assert.IsType<WorkQueueService>(system.Queue);
        var context = WorkRequestContext.Create(WorkInvocationChannel.InProcess);
        var malformed = WorkInput.Empty.WithIdentifier(new WorkIdentifier(" ", "value"));
        var provenance = new WorkflowProvenance(
            WorkflowRunId.New(),
            "queue.internal.workflow",
            "dispatch");

        var malformedDelegated = await queue.EnqueueDelegated(
            definition.Name, malformed, null, context, CancellationToken.None);
        var missingDelegated = await queue.EnqueueDelegated(
            "queue.internal.missing", null, null, context, CancellationToken.None);
        var malformedWorkflowChild = await queue.EnqueueWorkflowChild(
            definition.Name, malformed, null, context, provenance, CancellationToken.None);
        var mismatchedWorkflowChild = await queue.EnqueueWorkflowChild(
            definition.Name, WorkInput.Empty, null, context, provenance, CancellationToken.None);

        Assert.Equal("workable.identifier.invalid", Assert.Single(malformedDelegated.QueueOutcome.Messages).Code);
        Assert.Equal(WorkQueueStatus.NotFound, missingDelegated.QueueOutcome.Status);
        Assert.Equal("workable.identifier.invalid", Assert.Single(malformedWorkflowChild.QueueOutcome.Messages).Code);
        Assert.Equal(
            "workable.workflow.identifier.invalid",
            Assert.Single(mismatchedWorkflowChild.QueueOutcome.Messages).Code);
    }

    [Fact]
    public async Task RejectBlankNamedQueueRequestsBeforeLookup()
    {
        await using var system = CreateSystem(WorkDefinition.Create("queue.blank.name"));

        await Assert.ThrowsAsync<ArgumentException>(() => system.Queue.Enqueue(" "));
        await Assert.ThrowsAsync<ArgumentException>(() => system.Queue.Enqueue(" ", new QueueInput("typed")));
    }

    [Fact]
    public async Task ReturnInvalidOutcomeWhenSessionChannelIsNotAllowed()
    {
        var definition = WorkDefinition.Create(
            "queue.dotnet.only",
            configuration: WorkConfiguration.Default with
            {
                Invocation = WorkInvocationConfiguration.Allow(WorkInvocationChannel.InProcess),
            });
        await using var system = CreateSystem(definition);
        await system.Start();
        var session = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.HttpApi,
            description: "Queue through HTTP."));

        var handle = await session.Queue.Enqueue(definition.Name);
        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkQueueStatus.Invalid, handle.QueueOutcome.Status);
        Assert.Null(handle.QueueOutcome.WorkerId);
        var message = Assert.Single(handle.QueueOutcome.Messages);
        Assert.Equal("workable.invocation.channel_not_allowed", message.Code);
        Assert.Equal("invocation.channel", message.Target);
        Assert.Contains("HTTP API", message.Text);
        Assert.Equal(WorkCompletionStatus.Invalid, completion.Status);
        Assert.Equal(handle.QueueOutcome.Messages, completion.Messages);
    }

    [Fact]
    public async Task ApplyTheSameValidationWhenQueueingByDefinitionId()
    {
        var definition = WorkDefinition.Create(
            "queue.by.definition.id",
            configuration: WorkConfiguration.Default with
            {
                Invocation = WorkInvocationConfiguration.Allow(WorkInvocationChannel.InProcess),
            });
        await using var system = CreateSystem(definition);
        await system.Start();
        var queue = Assert.IsType<WorkQueueService>(system.Queue);
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess);

        var malformed = await queue.Enqueue(
            definition.Id,
            WorkInput.Empty.WithIdentifier(new WorkIdentifier(" ", "value")),
            options: null,
            requestContext,
            CancellationToken.None);
        var reserved = await queue.Enqueue(
            definition.Id,
            WorkInput.Empty.WithIdentifier(new WorkIdentifier("workflow-run", WorkflowRunId.New().ToString())),
            options: null,
            requestContext,
            CancellationToken.None);
        var missing = await queue.Enqueue(
            WorkDefinitionId.New(),
            input: null,
            options: null,
            requestContext,
            CancellationToken.None);
        var wrongChannel = await queue.Enqueue(
            definition.Id,
            input: null,
            options: null,
            WorkRequestContext.Create(WorkInvocationChannel.HttpApi),
            CancellationToken.None);
        var accepted = await queue.Enqueue(definition.Id);

        Assert.Equal("workable.identifier.invalid", Assert.Single(malformed.QueueOutcome.Messages).Code);
        Assert.Equal("workable.workflow.identifier.reserved", Assert.Single(reserved.QueueOutcome.Messages).Code);
        Assert.Equal(WorkQueueStatus.NotFound, missing.QueueOutcome.Status);
        Assert.Equal("workable.invocation.channel_not_allowed", Assert.Single(wrongChannel.QueueOutcome.Messages).Code);
        Assert.Equal(WorkCompletionStatus.Completed, (await accepted.WaitForCompletion()).Status);
    }

    [Theory]
    [InlineData(WorkInvocationChannel.Mcp, "MCP")]
    [InlineData(WorkInvocationChannel.SignalR, "SignalR")]
    [InlineData((WorkInvocationChannel)999, "999")]
    public async Task DescribeEveryRejectedInvocationChannel(
        WorkInvocationChannel channel,
        string expectedDescription)
    {
        var definition = WorkDefinition.Create(
            "queue.channel.description",
            configuration: WorkConfiguration.Default with
            {
                Invocation = WorkInvocationConfiguration.Allow(WorkInvocationChannel.InProcess),
            });
        await using var system = CreateSystem(definition);
        await system.Start();
        var session = await system.CreateSession(WorkRequestContext.Create(channel));

        var handle = await session.Queue.Enqueue(definition.Name);

        Assert.Contains(expectedDescription, Assert.Single(handle.QueueOutcome.Messages).Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreserveNullAndExistingWorkInputInTypedQueueOverloads()
    {
        WorkInput? firstInput = null;
        WorkInput? secondInput = null;
        var calls = 0;
        var definition = WorkDefinition.Create("queue.typed.input");
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, (_, input, _) =>
            {
                if (calls++ == 0)
                {
                    firstInput = input;
                }
                else
                {
                    secondInput = input;
                }

                return Task.FromResult(WorkExecutionResult.Success());
            }))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();
        var existing = WorkInput.FromJson("{\"value\":1}");
        var queue = Assert.IsType<WorkQueueService>(system.Queue);

        await (await queue.Enqueue<QueueInput?>(definition.Id, null)).WaitForCompletion();
        await (await system.Queue.Enqueue<WorkInput>(definition.Name, existing)).WaitForCompletion();

        Assert.Null(firstInput);
        Assert.Equal(existing, secondInput);
    }

    private static IWorkSystem CreateSystem(WorkDefinition definition)
        => new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, SuccessfulWork))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private sealed record QueueInput(string Value);
}
