using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Systems")]
public sealed class SessionWorkQueueServiceShould
{
    [Fact]
    public async Task UseSessionRequestContextForEveryEnqueueOverload()
    {
        await using var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork<SessionQueueWork>(
                WorkDefinition.Create("session.queue.work")))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();
        var definition = RequireDefinition(system, "session.queue.work");
        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.HttpApi,
            new WorkActor("session-queue-user", "Session Queue User"),
            "Queue through a session.",
            "https://workable.test/session");
        var session = system.CreateSession(requestContext);

        var byIdUntyped = await session.Queue.Enqueue(
            definition.Id,
            WorkInput.FromValue(new SessionQueueInput("id-untyped")));
        var byIdTyped = await session.Queue.Enqueue(
            definition.Id,
            new SessionQueueInput("id-typed"));
        var byNameUntyped = await session.Queue.Enqueue(
            definition.Name,
            WorkInput.FromValue(new SessionQueueInput("name-untyped")));
        var byNameTyped = await session.Queue.Enqueue(
            definition.Name,
            new SessionQueueInput("name-typed"));

        AssertQueueResult("id-untyped", await byIdUntyped.WaitForCompletion<SessionQueueResult>());
        AssertQueueResult("id-typed", await byIdTyped.WaitForCompletion<SessionQueueResult>());
        AssertQueueResult("name-untyped", await byNameUntyped.WaitForCompletion<SessionQueueResult>());
        AssertQueueResult("name-typed", await byNameTyped.WaitForCompletion<SessionQueueResult>());
    }

    private static WorkDefinition RequireDefinition(IWorkSystem system, string name)
        => system.Catalog.TryGet(name, out var definition)
            ? definition
            : throw new InvalidOperationException($"Expected definition '{name}'.");

    private static void AssertQueueResult(
        string expectedInput,
        WorkCompletion<SessionQueueResult> completion)
    {
        Assert.Equal(WorkCompletionStatus.Completed, completion.Status);
        var output = completion.Output ?? throw new InvalidOperationException("Expected typed output.");
        Assert.Equal(expectedInput, output.Input);
        Assert.Equal(WorkInvocationChannel.HttpApi, output.Channel);
        Assert.Equal("session-queue-user", output.ActorId);
        Assert.Equal("Session Queue User", output.ActorName);
        Assert.Equal("Queue through a session.", output.Description);
        Assert.Equal("https://workable.test/session", output.Url);
    }

    private sealed record SessionQueueInput(string Value);

    private sealed record SessionQueueResult(
        string Input,
        WorkInvocationChannel Channel,
        string? ActorId,
        string? ActorName,
        string? Description,
        string? Url);

    private sealed class SessionQueueWork : IWorkExecutor<SessionQueueInput, SessionQueueResult>
    {
        public Task<WorkExecutionResult<SessionQueueResult>> Execute(
            IWorkExecutionContext context,
            SessionQueueInput input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult<SessionQueueResult>.Success(new SessionQueueResult(
                input.Value,
                context.Origin.Channel,
                context.Origin.Actor.Id,
                context.Origin.Actor.Name,
                context.Origin.Description,
                context.Origin.Url)));
    }
}
