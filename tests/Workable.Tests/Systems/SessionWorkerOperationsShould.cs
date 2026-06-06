using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Systems")]
public sealed class SessionWorkerOperationsShould
{
    [Fact]
    public async Task UseSessionRequestContextForActionsAndReconfiguration()
    {
        var definition = WorkDefinition.Create(
            "session.worker.operations",
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        await using var system = CreateSystem(definition);
        await system.Start();
        var handle = await system.Queue.Enqueue(definition.Name);
        var worker = await RequiredWorker(system, handle);
        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.HttpApi,
            new WorkActor("session-worker-user", "Session Worker User"),
            "Operate worker through a session.",
            "https://workable.test/workers");
        var session = system.CreateSession(requestContext);
        var changes = new WorkerReconfiguration(ProfilingEnabled: true);

        var reconfigure = await session.Workers.Reconfigure(worker.Version, changes);
        var reconfiguredWorker = await RequiredWorker(system, handle);
        var cancel = await session.Workers.Execute(reconfiguredWorker.Version, WorkAction.Cancel);
        var canceledWorker = await RequiredWorker(system, handle);

        Assert.True(reconfigure.IsAccepted);
        Assert.True(cancel.IsAccepted);
        Assert.Collection(
            canceledWorker.ActionHistory,
            history =>
            {
                Assert.Equal(WorkerActionHistoryKind.Reconfiguration, history.Kind);
                Assert.Null(history.Action);
                Assert.Equal(changes, history.Reconfiguration);
                AssertSessionRequestContext(history.RequestContext);
            },
            history =>
            {
                Assert.Equal(WorkerActionHistoryKind.WorkerAction, history.Kind);
                Assert.Equal(WorkAction.Cancel, history.Action);
                AssertSessionRequestContext(history.RequestContext);
            });
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

    private static async Task<WorkerSnapshot> RequiredWorker(
        IWorkSystem system,
        IWorkerHandle handle)
    {
        var workerId = handle.WorkerId ?? throw new InvalidOperationException("Expected accepted worker.");
        return await system.Query.Worker(workerId)
            ?? throw new InvalidOperationException("Expected worker.");
    }

    private static void AssertSessionRequestContext(WorkRequestContext requestContext)
    {
        Assert.Equal(WorkInvocationChannel.HttpApi, requestContext.Channel);
        Assert.Equal("session-worker-user", requestContext.Actor.Id);
        Assert.Equal("Session Worker User", requestContext.Actor.Name);
        Assert.Equal("Operate worker through a session.", requestContext.Description);
        Assert.Equal("https://workable.test/workers", requestContext.Url);
    }
}
