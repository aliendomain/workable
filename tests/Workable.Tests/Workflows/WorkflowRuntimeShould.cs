using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Workflows")]
public sealed class WorkflowRuntimeShould
{
    [Fact]
    public async Task ExecuteDispatchWorkAndCompleteWorkflow()
    {
        var ran = 0;
        var services = new ServiceCollection();

        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("sample.dispatch"),
                (_, _, _) =>
                {
                    Interlocked.Increment(ref ran);
                    return Task.FromResult(WorkExecutionResult.Success());
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.dispatch"),
                workflow => workflow.DispatchWork("dispatch", "sample.dispatch"));
        });

        using var provider = services.BuildServiceProvider();
        var system = (InMemoryWorkSystem)provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var handle = system.WorkflowRuntime.Start(
            "workflow.dispatch",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var completion = await handle.WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(1, Volatile.Read(ref ran));
        Assert.NotNull(completion.Run);
        var run = completion.Run!;
        var step = Assert.Single(run.Steps);
        Assert.Equal(WorkflowStepRunStatus.Completed, step.Status);
        Assert.Single(step.WorkerIds);
    }

    [Fact]
    public async Task WaitAtJoinUntilParallelChildrenComplete()
    {
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var quickCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();

        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("sample.quick"),
                async (_, _, _) =>
                {
                    quickCompleted.TrySetResult();
                    await Task.Yield();
                    return WorkExecutionResult.Success();
                });
            builder.AddWork(
                WorkDefinition.Create("sample.slow"),
                async (_, _, cancellationToken) =>
                {
                    slowStarted.TrySetResult();
                    await slowRelease.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.parallel.join"),
                workflow => workflow
                    .RunParallel("dispatch", parallel => parallel
                        .DispatchWork("quick", "sample.quick")
                        .DispatchWork("slow", "sample.slow"))
                    .Join("join"));
        });

        using var provider = services.BuildServiceProvider();
        var system = (InMemoryWorkSystem)provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var handle = system.WorkflowRuntime.Start(
            "workflow.parallel.join",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        await slowStarted.Task.WaitAsync(CancellationToken.None);
        await quickCompleted.Task.WaitAsync(CancellationToken.None);

        await TestEventually.Until(() =>
        {
            var run = system.WorkflowRuntime.Get(handle.RunId!.Value);
            return run is not null &&
                run.Status == WorkflowRunStatus.Running &&
                run.Steps.Single(step => step.Name == "join").Status == WorkflowStepRunStatus.Running;
        }, "Expected the join step to begin and wait while the slow child was still running.");

        var waitTask = handle.WaitForCompletion();
        Assert.False(waitTask.IsCompleted);

        slowRelease.TrySetResult();
        var completion = await waitTask;

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.NotNull(completion.Run);
        var run = completion.Run!;
        Assert.Equal(WorkflowRunStatus.Completed, run.Status);
        Assert.Equal(WorkflowStepRunStatus.Completed, run.Steps.Single(step => step.Name == "dispatch").Status);
        Assert.Equal(WorkflowStepRunStatus.Completed, run.Steps.Single(step => step.Name == "join").Status);
    }

    [Fact]
    public async Task FailWorkflowWhenJoinObservesFailedChild()
    {
        var services = new ServiceCollection();

        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("sample.good"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
            builder.AddWork(
                WorkDefinition.Create("sample.bad"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("sample.child.failed", "Child failed.")])));
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.parallel.failure"),
                workflow => workflow
                    .RunParallel("dispatch", parallel => parallel
                        .DispatchWork("good", "sample.good")
                        .DispatchWork("bad", "sample.bad"))
                    .Join("join"));
        });

        using var provider = services.BuildServiceProvider();
        var system = (InMemoryWorkSystem)provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var handle = system.WorkflowRuntime.Start(
            "workflow.parallel.failure",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var completion = await handle.WaitForCompletion();

        Assert.False(completion.IsCompletedSuccessfully);
        Assert.Equal(WorkflowRunStatus.Failed, completion.Status);
        Assert.NotNull(completion.Run);
        var run = completion.Run!;
        Assert.Equal(WorkflowStepRunStatus.Failed, run.Steps.Single(step => step.Name == "join").Status);
    }

    [Fact]
    public async Task RejectNonDurableWorkflowThatDispatchesDurableWork()
    {
        var services = new ServiceCollection();

        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("sample.durable"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                configuration => configuration
                    .CoordinatePersistently()
                    .QueueDurably());
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.invalid.durable-child"),
                workflow => workflow.DispatchWork("dispatch", "sample.durable"));
        });

        using var provider = services.BuildServiceProvider();
        var system = (InMemoryWorkSystem)provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var handle = system.WorkflowRuntime.Start(
            "workflow.invalid.durable-child",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var completion = await handle.WaitForCompletion();

        Assert.False(handle.StartOutcome.IsAccepted);
        Assert.Equal(WorkflowRunStatus.Invalid, completion.Status);
        Assert.Contains(
            handle.StartOutcome.Messages,
            message => message.Code == "workable.workflow.child_durability_requires_durable_workflow");
    }

    [Fact]
    public async Task RejectDurableWorkflowUntilDurableRuntimeExists()
    {
        var services = new ServiceCollection();

        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("sample.dispatch"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
            builder.AddWorkflow(
                WorkflowDefinition.Create(
                    "workflow.durable",
                    coordination: WorkflowCoordinationConfiguration.Durable),
                workflow => workflow.DispatchWork("dispatch", "sample.dispatch"));
        });

        using var provider = services.BuildServiceProvider();
        var system = (InMemoryWorkSystem)provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var handle = system.WorkflowRuntime.Start(
            "workflow.durable",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var completion = await handle.WaitForCompletion();

        Assert.False(handle.StartOutcome.IsAccepted);
        Assert.Equal(WorkflowRunStatus.Invalid, completion.Status);
        Assert.Contains(
            handle.StartOutcome.Messages,
            message => message.Code == "workable.workflow.durability.not_supported");
    }
}
