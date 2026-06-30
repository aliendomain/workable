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
                        .DispatchWork("email", WorkDefinition.Create("workflow.operator.email"))
                        .DispatchWork("invoice", WorkDefinition.Create("workflow.operator.invoice")))
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
    public async Task ShowOnlyUnresolvedWorkersOnJoinNodes()
    {
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseSlow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("workflow.operator.fast"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
            builder.AddWork(
                WorkDefinition.Create("workflow.operator.slow"),
                async (_, _, cancellationToken) =>
                {
                    slowStarted.TrySetResult();
                    await releaseSlow.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.operator.join.waiting"),
                workflow => workflow
                    .RunParallel("fan-out", parallel => parallel
                        .DispatchWork("fast", WorkDefinition.Create("workflow.operator.fast"))
                        .DispatchWork("slow", WorkDefinition.Create("workflow.operator.slow")))
                    .Join("settle"));
        });

        await using var provider = services.BuildServiceProvider();
        var system = Assert.IsType<InMemoryWorkSystem>(provider.GetRequiredService<IWorkSystemRegistry>().Default);
        await system.Start();

        var handle = system.WorkflowRuntime.Start(
            "workflow.operator.join.waiting",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        WorkflowRunDetailView? detail = null;
        await TestEventually.Until(
            async () =>
            {
                detail = await new WorkflowRunViewAdapter().Run(
                    system,
                    WorkRequestContext.Create(WorkInvocationChannel.InProcess),
                    handle.RunId!.Value);
                return detail?.Steps.Single(step => step.Name == "settle").Children.Total == 1;
            },
            "Expected join node to show only the unresolved slow worker.");

        releaseSlow.TrySetResult();
        await handle.WaitForCompletion();

        Assert.NotNull(detail);
        var settle = Assert.Single(detail!.Steps, step => step.Name == "settle");
        Assert.Equal(WorkflowOperatorNodeStatus.WaitingOnChildren, settle.Status);
        Assert.Equal(1, settle.Children.Total);
        var child = Assert.Single(settle.ChildSample);
        Assert.Equal("workflow.operator.slow", child.DefinitionName);
        Assert.Equal(WorkerState.Running, child.State);
    }

    [Fact]
    public async Task PageSelectedStepChildren()
    {
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("workflow.operator.page.first"),
                async (_, _, cancellationToken) =>
                {
                    firstStarted.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddWork(
                WorkDefinition.Create("workflow.operator.page.second"),
                async (_, _, cancellationToken) =>
                {
                    secondStarted.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.operator.page.parallel"),
                workflow => workflow
                    .RunParallel("notify", parallel => parallel
                        .DispatchWork("first", WorkDefinition.Create("workflow.operator.page.first"))
                        .DispatchWork("second", WorkDefinition.Create("workflow.operator.page.second")))
                    .Join("settle"));
        });

        await using var provider = services.BuildServiceProvider();
        var system = Assert.IsType<InMemoryWorkSystem>(provider.GetRequiredService<IWorkSystemRegistry>().Default);
        await system.Start();

        var handle = system.WorkflowRuntime.Start(
            "workflow.operator.page.parallel",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));

        var page = await new WorkflowRunViewAdapter().StepChildren(
            system,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            handle.RunId!.Value,
            "notify",
            skip: 1,
            take: 1);

        release.TrySetResult();
        await handle.WaitForCompletion();

        Assert.NotNull(page);
        Assert.Equal(2, page!.TotalCount);
        Assert.Equal(1, page.Skip);
        Assert.Equal(1, page.Take);
        var worker = Assert.Single(page.Workers);
        Assert.Equal("workflow.operator.page.second", worker.DefinitionName);
        Assert.Equal(WorkerState.Running, worker.State);
    }

    [Fact]
    public async Task HideUnreadableWorkflowRuns()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkAuthorizationGroupProvider>(
            new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["workflow-user"] = Groups("workflow.read", "workflow.ops"),
            }));
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(true);
            builder.ConfigureAuthorization(auth => auth
                .AllowReadAllWorkToGroups("workflow.read")
                .AllowOperateAllWorkToGroups("workflow.ops"));
            builder.AddWork(
                WorkDefinition.Create("workflow.operator.secured.child"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                configuration => configuration.DoNotStart());
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.operator.secured"),
                workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("workflow.operator.secured.child")),
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
        await TestEventually.Until(
            () => system.WorkflowRuntime.Get(handle.RunId!.Value)?.Steps.Single(step => step.Name == "dispatch").WorkerIds.Count == 1,
            "Expected the secured workflow to dispatch its child before querying visibility.",
            timeout: TimeSpan.FromSeconds(5));

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

        var cancel = await system.WorkflowRuntime.Execute(handle.RunId!.Value, WorkflowAction.Cancel, startContext);
        Assert.True(cancel.IsAccepted);
        await handle.WaitForCompletion();
    }

    [Fact]
    public async Task RemoveFinalWorkflowRunWhenItsLastChildWorkerIsPurged()
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
                workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("workflow.operator.purge.child")));
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

        Assert.Null(detail);
    }

    [Fact]
    public async Task ReportAvailableActionsFromWorkflowRunStatus()
    {
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("workflow.operator.actions.child"),
                async (_, _, cancellationToken) =>
                {
                    childStarted.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.operator.actions"),
                workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("workflow.operator.actions.child")));
        });

        await using var provider = services.BuildServiceProvider();
        var system = Assert.IsType<InMemoryWorkSystem>(provider.GetRequiredService<IWorkSystemRegistry>().Default);
        await system.Start();

        var runId = system.WorkflowRuntime.Start(
            "workflow.operator.actions",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess)).RunId!.Value;
        await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var pausedOutcome = await system.WorkflowRuntime.Execute(
            runId,
            WorkflowAction.Pause,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        Assert.True(pausedOutcome.IsAccepted);

        WorkflowRunDetailView? paused = null;
        await TestEventually.Until(
            async () =>
            {
                paused = await new WorkflowRunViewAdapter().Run(
                    system,
                    WorkRequestContext.Create(WorkInvocationChannel.InProcess),
                    runId);
                return paused?.Status == WorkflowRunStatus.Paused;
            },
            "Expected the workflow detail view to report the paused run state.");

        var canceledOutcome = await system.WorkflowRuntime.Execute(
            runId,
            WorkflowAction.Cancel,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        Assert.True(canceledOutcome.IsAccepted);

        var canceled = await new WorkflowRunViewAdapter().Run(
            system,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            runId);

        Assert.NotNull(paused);
        Assert.True(paused!.AvailableActions.Start);
        Assert.False(paused.AvailableActions.Pause);
        Assert.True(paused.AvailableActions.Cancel);
        Assert.NotNull(canceled);
        Assert.Equal(WorkflowRunStatus.Canceled, canceled!.Status);
        Assert.False(canceled.AvailableActions.Start);
        Assert.False(canceled.AvailableActions.Pause);
        Assert.False(canceled.AvailableActions.Cancel);
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

    private sealed class TestGroupProvider(IReadOnlyDictionary<string, IReadOnlySet<string>> groupsByActor)
        : IWorkAuthorizationGroupProvider
    {
        public IReadOnlySet<string> GetGroups(WorkActor actor, string? systemName)
            => groupsByActor.TryGetValue(actor.Id ?? string.Empty, out var groups)
                ? groups
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }
}
