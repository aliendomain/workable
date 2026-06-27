using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Workflows")]
public sealed class WorkflowRunViewAdapterShould
{
    [Fact]
    public async Task BuildParallelDetailAndOutstandingSummaryFromAuthoritativeWorkers()
    {
        var emailStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoiceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("workflow.operator.email"),
                async (_, _, cancellationToken) =>
                {
                    emailStarted.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddWork(
                WorkDefinition.Create("workflow.operator.invoice"),
                async (_, _, cancellationToken) =>
                {
                    invoiceStarted.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.operator.parallel"),
                workflow => workflow
                    .RunParallel("notify", parallel => parallel
                        .DispatchWork("email", "workflow.operator.email")
                        .DispatchWork("invoice", "workflow.operator.invoice"))
                    .Join("settle"));
        });

        await using var provider = services.BuildServiceProvider();
        var system = Assert.IsType<InMemoryWorkSystem>(provider.GetRequiredService<IWorkSystemRegistry>().Default);
        await system.Start();

        var handle = system.WorkflowRuntime.Start(
            "workflow.operator.parallel",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        await Task.WhenAll(emailStarted.Task, invoiceStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));

        var detail = await new WorkflowRunViewAdapter().Run(
            system,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            handle.RunId!.Value);

        release.TrySetResult();
        await handle.WaitForCompletion();

        Assert.NotNull(detail);
        Assert.Equal("workflow.operator.parallel", detail!.DefinitionName);
        Assert.Equal("notify", detail.CurrentStepName);
        Assert.Equal(2, detail.OutstandingChildren.Total);
        Assert.Equal(2, detail.OutstandingChildren.ByState[WorkerState.Running]);

        var notify = Assert.Single(detail.Steps, step => step.Name == "notify");
        Assert.Equal(WorkflowOperatorNodeStatus.WaitingOnChildren, notify.Status);
        Assert.Equal(2, notify.Children.Total);
        Assert.Equal(["email", "invoice"], notify.Steps.Select(step => step.Name).ToArray());
        Assert.All(notify.Steps, step => Assert.Equal(WorkflowOperatorNodeStatus.WaitingOnChildren, step.Status));
        Assert.All(
            notify.Steps.SelectMany(step => step.ChildSample),
            worker => Assert.Equal(WorkerState.Running, worker.State));

        var settle = Assert.Single(detail.Steps, step => step.Name == "settle");
        Assert.Equal(WorkflowOperatorNodeStatus.WaitingOnChildren, settle.Status);
        Assert.Equal(2, settle.Children.Total);
    }

    [Fact]
    public async Task HideUnreadableWorkflowRuns()
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(true);
            builder.AddWork(
                WorkDefinition.Create("workflow.operator.secured.child"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.operator.secured"),
                workflow => workflow.DispatchWork("dispatch", "workflow.operator.secured.child"),
                authorize: auth => auth
                    .AllowReadToGroups("workflow.read")
                    .AllowOperateToGroups("workflow.ops"));
        });

        await using var provider = services.BuildServiceProvider();
        var system = Assert.IsType<InMemoryWorkSystem>(provider.GetRequiredService<IWorkSystemRegistry>().Default);
        await system.Start();

        var actor = new WorkActor("workflow-user", "Workflow User");
        var startContext = CreateContext(actor, "workflow.ops", "workflow.read");
        var readContext = CreateContext(actor, "workflow.read");
        var hiddenContext = CreateContext(actor);
        var handle = system.WorkflowRuntime.Start("workflow.operator.secured", startContext);
        await handle.WaitForCompletion();

        var visibleRuns = await new WorkflowRunViewAdapter().Runs(
            system,
            readContext,
            includeFinal: true);
        var hiddenRuns = await new WorkflowRunViewAdapter().Runs(
            system,
            hiddenContext,
            includeFinal: true);
        var hiddenDetail = await new WorkflowRunViewAdapter().Run(
            system,
            hiddenContext,
            handle.RunId!.Value);

        Assert.Single(visibleRuns.Runs);
        Assert.Empty(hiddenRuns.Runs);
        Assert.Null(hiddenDetail);
    }

    [Fact]
    public async Task ReportUnavailableChildrenWhenWorkersHaveBeenPurged()
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("workflow.operator.purge.child"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.operator.purge"),
                workflow => workflow.DispatchWork("dispatch", "workflow.operator.purge.child"));
        });

        await using var provider = services.BuildServiceProvider();
        var system = Assert.IsType<InMemoryWorkSystem>(provider.GetRequiredService<IWorkSystemRegistry>().Default);
        await system.Start();

        var handle = system.WorkflowRuntime.Start(
            "workflow.operator.purge",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        await handle.WaitForCompletion();

        var workerId = handle.RunId is { } runId
            ? system.WorkflowRuntime.Get(runId)!.Steps.Single(step => step.Name == "dispatch").WorkerIds.Single()
            : throw new InvalidOperationException("Expected workflow run id.");
        var worker = await system.Query.Worker(workerId)
            ?? throw new InvalidOperationException("Expected child worker.");
        var purged = await system.Workers.Execute(worker.Version, WorkAction.Purge);
        Assert.Equal(WorkActionStatus.Accepted, purged.Status);

        var detail = await new WorkflowRunViewAdapter().Run(
            system,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            handle.RunId!.Value);

        Assert.NotNull(detail);
        var dispatch = Assert.Single(detail!.Steps, step => step.Name == "dispatch");
        Assert.Equal(WorkflowOperatorNodeStatus.Completed, dispatch.Status);
        Assert.Equal(1, dispatch.Children.Total);
        Assert.Equal(1, dispatch.Children.Unavailable);
        Assert.Empty(dispatch.ChildSample);
    }

    private static WorkRequestContext CreateContext(WorkActor actor, params string[] groups)
        => WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            actor,
            isAuthenticated: true) with
        {
            Authorization = WorkAuthorizationSnapshot.Create(
                actor,
                Groups(groups),
                readableDefinitionIds: null),
        };

    private static IReadOnlySet<string> Groups(params string[] groups)
        => new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);
}
