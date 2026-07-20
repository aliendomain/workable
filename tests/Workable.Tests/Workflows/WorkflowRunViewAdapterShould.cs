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

        WorkflowRunDetailView? detail = null;
        var adapter = new WorkflowRunViewAdapter();
        await TestEventually.Until(
            async () =>
            {
                detail = await adapter.Run(
                    system,
                    WorkRequestContext.Create(WorkInvocationChannel.InProcess),
                    handle.RunId!.Value);
                return detail?.Steps.Single(step => step.Name == "notify").Status == WorkflowOperatorNodeStatus.WaitingOnChildren;
            },
            "Expected the parallel workflow node to wait on running child workers before inspection.");

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
    public async Task BuildBranchDetailAndPageBranchChildren()
    {
        var collectStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var renderStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("workflow.operator.branch.collect"),
                async (_, _, cancellationToken) =>
                {
                    collectStarted.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddWork(
                WorkDefinition.Create("workflow.operator.branch.render"),
                async (_, _, cancellationToken) =>
                {
                    renderStarted.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddWork(
                WorkDefinition.Create("workflow.operator.branch.publish"),
                async (_, _, cancellationToken) =>
                {
                    publishStarted.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.operator.branch"),
                workflow => workflow
                    .RunParallel("fan-out", parallel => parallel
                        .Branch("documents", branch => branch
                            .DispatchWork("collect", WorkDefinition.Create("workflow.operator.branch.collect"))
                            .RunParallel("replicate", replicate => replicate
                                .DispatchWork("render", WorkDefinition.Create("workflow.operator.branch.render"))
                                .DispatchWork("publish", WorkDefinition.Create("workflow.operator.branch.publish")))))
                    .Join("settle"));
        });

        await using var provider = services.BuildServiceProvider();
        var system = Assert.IsType<InMemoryWorkSystem>(provider.GetRequiredService<IWorkSystemRegistry>().Default);
        await system.Start();

        var handle = system.WorkflowRuntime.Start(
            "workflow.operator.branch",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        await Task.WhenAll(collectStarted.Task, renderStarted.Task, publishStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));

        WorkflowRunDetailView? detail = null;
        var adapter = new WorkflowRunViewAdapter();
        await TestEventually.Until(
            async () =>
            {
                detail = await adapter.Run(
                    system,
                    WorkRequestContext.Create(WorkInvocationChannel.InProcess),
                    handle.RunId!.Value);
                return detail?.Steps
                    .Single(step => step.Name == "fan-out")
                    .Steps
                    .Single(step => step.Name == "documents")
                    .Status == WorkflowOperatorNodeStatus.WaitingOnChildren;
            },
            "Expected the branch node to wait on its running child workers before inspection.");

        var branchChildren = await adapter.StepChildren(
            system,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            handle.RunId!.Value,
            "documents",
            skip: 0,
            take: 10);
        var runs = await adapter.Runs(
            system,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var nestedChild = await adapter.StepChildren(
            system,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            handle.RunId!.Value,
            "render",
            skip: 0,
            take: 10);

        release.TrySetResult();
        await handle.WaitForCompletion();

        Assert.NotNull(detail);
        var fanOut = Assert.Single(detail!.Steps, step => step.Name == "fan-out");
        var documents = Assert.Single(fanOut.Steps, step => step.Name == "documents");
        Assert.Equal(WorkflowStepKind.Branch, documents.Kind);
        Assert.Equal(WorkflowOperatorNodeStatus.WaitingOnChildren, documents.Status);
        Assert.Equal(3, documents.Children.Total);
        Assert.Equal(["collect", "replicate"], documents.Steps.Select(step => step.Name).ToArray());
        Assert.All(documents.ChildSample, worker => Assert.Equal(WorkerState.Running, worker.State));

        Assert.NotNull(branchChildren);
        Assert.Equal(3, branchChildren!.TotalCount);
        Assert.Equal(
            ["workflow.operator.branch.collect", "workflow.operator.branch.publish", "workflow.operator.branch.render"],
            branchChildren.Workers.Select(worker => worker.DefinitionName).OrderBy(static name => name, StringComparer.Ordinal).ToArray());
        var runSummary = Assert.Single(runs.Runs, item => item.RunId == handle.RunId!.Value.Value);
        Assert.Equal(3, runSummary.OutstandingChildren.Total);
        Assert.NotNull(nestedChild);
        var renderWorker = Assert.Single(nestedChild!.Workers);
        Assert.Equal("workflow.operator.branch.render", renderWorker.DefinitionName);
        Assert.Equal(WorkerState.Running, renderWorker.State);
    }

    [Fact]
    public void ResolveBranchChildWorkersFromNestedStepsWhenBranchHasNoDirectWorkers()
    {
        var collectWorkerId = WorkerId.New();
        var renderWorkerId = WorkerId.New();
        var publishWorkerId = WorkerId.New();
        IReadOnlyList<WorkflowStepDefinition> steps =
        [
            new ParallelWorkflowStepDefinition(
                "fan-out",
                [
                    new BranchWorkflowStepDefinition(
                        "documents",
                        [
                            new DispatchWorkflowStepDefinition(
                                "collect",
                                WorkDefinition.Create("workflow.operator.branch.collect")),
                            new ParallelWorkflowStepDefinition(
                                "replicate",
                                [
                                    new DispatchWorkflowStepDefinition(
                                        "render",
                                        WorkDefinition.Create("workflow.operator.branch.render")),
                                    new DispatchWorkflowStepDefinition(
                                        "publish",
                                        WorkDefinition.Create("workflow.operator.branch.publish")),
                                ]),
                        ]),
                ]),
        ];
        var snapshot = new WorkflowRunSnapshot(
            WorkflowRunId.New(),
            "workflow.operator.branch",
            WorkflowRunStatus.Running,
            null,
            [
                new WorkflowStepRunSnapshot(
                    "fan-out",
                    WorkflowStepKind.Parallel,
                    WorkflowStepRunStatus.Running,
                    [],
                    DateTimeOffset.UtcNow,
                    null,
                    []),
                new WorkflowStepRunSnapshot(
                    "documents",
                    WorkflowStepKind.Branch,
                    WorkflowStepRunStatus.Running,
                    [],
                    DateTimeOffset.UtcNow,
                    null,
                    []),
                new WorkflowStepRunSnapshot(
                    "collect",
                    WorkflowStepKind.DispatchWork,
                    WorkflowStepRunStatus.Completed,
                    [collectWorkerId],
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    []),
                new WorkflowStepRunSnapshot(
                    "replicate",
                    WorkflowStepKind.Parallel,
                    WorkflowStepRunStatus.Running,
                    [],
                    DateTimeOffset.UtcNow,
                    null,
                    []),
                new WorkflowStepRunSnapshot(
                    "render",
                    WorkflowStepKind.DispatchWork,
                    WorkflowStepRunStatus.Completed,
                    [renderWorkerId],
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    []),
                new WorkflowStepRunSnapshot(
                    "publish",
                    WorkflowStepKind.DispatchWork,
                    WorkflowStepRunStatus.Completed,
                    [publishWorkerId],
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    []),
            ],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            [],
            []);

        var method = typeof(WorkflowRunViewAdapter).GetMethod(
            "TryGetStepWorkerIds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        object?[] arguments = [steps, snapshot, "documents", null];

        var found = Assert.IsType<bool>(method.Invoke(null, arguments));

        Assert.True(found);
        var workerIds = Assert.IsAssignableFrom<IReadOnlyList<WorkerId>>(arguments[3]);
        Assert.Equal([collectWorkerId, renderWorkerId, publishWorkerId], workerIds);
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

    [Theory]
    [InlineData(WorkflowRunStatus.Running, WorkflowOperatorNodeStatus.Running)]
    [InlineData(WorkflowRunStatus.Paused, WorkflowOperatorNodeStatus.Paused)]
    [InlineData(WorkflowRunStatus.Blocked, WorkflowOperatorNodeStatus.Blocked)]
    [InlineData(WorkflowRunStatus.Canceled, WorkflowOperatorNodeStatus.Canceled)]
    public void ResolveRunningDispatchAndParallelNodesFromTheRunStatus(
        WorkflowRunStatus runStatus,
        WorkflowOperatorNodeStatus expected)
    {
        var snapshot = StepSnapshot(WorkflowStepRunStatus.Running);
        var noChildren = BuildChildStates();

        Assert.Equal(
            expected,
            InvokeStatus("ResolveDispatchStatus", snapshot, runStatus, noChildren, null));
        Assert.Equal(
            expected,
            InvokeStatus("ResolveParallelStatus", snapshot, runStatus, Array.Empty<WorkflowStepOperatorView>()));
    }

    [Theory]
    [InlineData(WorkflowOperatorNodeStatus.Failed, WorkflowOperatorNodeStatus.Failed)]
    [InlineData(WorkflowOperatorNodeStatus.Canceled, WorkflowOperatorNodeStatus.Canceled)]
    [InlineData(WorkflowOperatorNodeStatus.Paused, WorkflowOperatorNodeStatus.Paused)]
    [InlineData(WorkflowOperatorNodeStatus.Blocked, WorkflowOperatorNodeStatus.Blocked)]
    [InlineData(WorkflowOperatorNodeStatus.Running, WorkflowOperatorNodeStatus.WaitingOnChildren)]
    [InlineData(WorkflowOperatorNodeStatus.WaitingOnChildren, WorkflowOperatorNodeStatus.WaitingOnChildren)]
    [InlineData(WorkflowOperatorNodeStatus.Completed, WorkflowOperatorNodeStatus.Completed)]
    public void ResolveCompletedParallelNodesFromChildOutcomePrecedence(
        WorkflowOperatorNodeStatus childStatus,
        WorkflowOperatorNodeStatus expected)
    {
        var children = new[] { OperatorStep(childStatus) };

        var actual = InvokeStatus(
            "ResolveParallelStatus",
            StepSnapshot(WorkflowStepRunStatus.Completed),
            WorkflowRunStatus.Running,
            children);

        Assert.Equal(expected, actual);
        Assert.Equal(
            WorkflowOperatorNodeStatus.Pending,
            InvokeStatus("ResolveParallelStatus", null, WorkflowRunStatus.Running, children));
        Assert.Equal(
            WorkflowOperatorNodeStatus.Failed,
            InvokeStatus(
                "ResolveParallelStatus",
                StepSnapshot(WorkflowStepRunStatus.Failed),
                WorkflowRunStatus.Running,
                children));
    }

    [Fact]
    public void ResolveDispatchNodesAcrossSnapshotAndChildOutcomeBoundaries()
    {
        var noChildren = BuildChildStates();
        Assert.Equal(
            WorkflowOperatorNodeStatus.Pending,
            InvokeStatus("ResolveDispatchStatus", null, WorkflowRunStatus.Running, noChildren, null));
        Assert.Equal(
            WorkflowOperatorNodeStatus.Pending,
            InvokeStatus("ResolveDispatchStatus", StepSnapshot(WorkflowStepRunStatus.Pending), WorkflowRunStatus.Running, noChildren, null));
        Assert.Equal(
            WorkflowOperatorNodeStatus.Failed,
            InvokeStatus("ResolveDispatchStatus", StepSnapshot(WorkflowStepRunStatus.Failed), WorkflowRunStatus.Running, noChildren, null));

        var canceledChild = BuildChildStates(WorkerState.Canceled);
        Assert.Equal(
            WorkflowOperatorNodeStatus.Canceled,
            InvokeStatus("ResolveDispatchStatus", null, WorkflowRunStatus.Running, canceledChild, null));
        Assert.Equal(
            WorkflowOperatorNodeStatus.Blocked,
            InvokeStatus("ResolveDispatchStatus", null, WorkflowRunStatus.Running, canceledChild, WorkflowCanceledChildBehavior.Block));
        Assert.Equal(
            WorkflowOperatorNodeStatus.Completed,
            InvokeStatus("ResolveDispatchStatus", null, WorkflowRunStatus.Running, canceledChild, WorkflowCanceledChildBehavior.Continue));

        var failedChild = BuildChildStates(WorkerState.Failed);
        Assert.Equal(
            WorkflowOperatorNodeStatus.Blocked,
            InvokeStatus("ResolveDispatchStatus", null, WorkflowRunStatus.Running, failedChild, null));
        Assert.Equal(
            WorkflowOperatorNodeStatus.Paused,
            InvokeStatus("ResolveDispatchStatus", null, WorkflowRunStatus.Paused, failedChild, null));
        Assert.Equal(
            WorkflowOperatorNodeStatus.WaitingOnChildren,
            InvokeStatus("ResolveDispatchStatus", null, WorkflowRunStatus.Running, BuildChildStates(WorkerState.Running), null));
        Assert.Equal(
            WorkflowOperatorNodeStatus.Completed,
            InvokeStatus("ResolveDispatchStatus", null, WorkflowRunStatus.Running, BuildChildStates(WorkerState.Completed), null));
    }

    [Theory]
    [InlineData(WorkflowStepRunStatus.Pending, WorkflowRunStatus.Running, false, WorkflowOperatorNodeStatus.Pending)]
    [InlineData(WorkflowStepRunStatus.Failed, WorkflowRunStatus.Running, false, WorkflowOperatorNodeStatus.Failed)]
    [InlineData(WorkflowStepRunStatus.Completed, WorkflowRunStatus.Running, false, WorkflowOperatorNodeStatus.Completed)]
    [InlineData(WorkflowStepRunStatus.Running, WorkflowRunStatus.Paused, false, WorkflowOperatorNodeStatus.Paused)]
    [InlineData(WorkflowStepRunStatus.Running, WorkflowRunStatus.Blocked, false, WorkflowOperatorNodeStatus.Blocked)]
    [InlineData(WorkflowStepRunStatus.Running, WorkflowRunStatus.Canceled, false, WorkflowOperatorNodeStatus.Canceled)]
    [InlineData(WorkflowStepRunStatus.Running, WorkflowRunStatus.Running, true, WorkflowOperatorNodeStatus.WaitingOnChildren)]
    [InlineData(WorkflowStepRunStatus.Running, WorkflowRunStatus.Running, false, WorkflowOperatorNodeStatus.Running)]
    public void ResolveJoinNodesFromStepRunAndOutstandingChildState(
        WorkflowStepRunStatus stepStatus,
        WorkflowRunStatus runStatus,
        bool hasChild,
        WorkflowOperatorNodeStatus expected)
    {
        var children = hasChild ? BuildChildStates(WorkerState.Running) : BuildChildStates();

        Assert.Equal(
            expected,
            InvokeStatus("ResolveJoinStatus", StepSnapshot(stepStatus), runStatus, children));
    }

    [Fact]
    public void PreferRecordedWorkerIdsForCompositeAndJoinStepQueries()
    {
        var parallelId = WorkerId.New();
        var branchId = WorkerId.New();
        var joinId = WorkerId.New();
        IReadOnlyList<WorkflowStepDefinition> steps =
        [
            new ParallelWorkflowStepDefinition("parallel", []),
            new BranchWorkflowStepDefinition("branch", []),
            new JoinWorkflowStepDefinition("join"),
        ];
        var snapshot = new WorkflowRunSnapshot(
            WorkflowRunId.New(),
            "workflow.operator.recorded-ids",
            WorkflowRunStatus.Running,
            null,
            [
                StepSnapshot(WorkflowStepRunStatus.Completed, "parallel", WorkflowStepKind.Parallel, parallelId),
                StepSnapshot(WorkflowStepRunStatus.Completed, "branch", WorkflowStepKind.Branch, branchId),
                StepSnapshot(WorkflowStepRunStatus.Running, "join", WorkflowStepKind.Join, joinId),
            ],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            [],
            []);

        Assert.Equal([parallelId], FindStepWorkerIds(steps, snapshot, "parallel"));
        Assert.Equal([branchId], FindStepWorkerIds(steps, snapshot, "branch"));
        Assert.Equal([joinId], FindStepWorkerIds(steps, snapshot, "join"));
        Assert.Null(FindStepWorkerIds(steps, snapshot, "missing"));

        var priorId = WorkerId.New();
        var snapshotWithoutJoinState = snapshot with
        {
            Steps = [StepSnapshot(WorkflowStepRunStatus.Completed, "dispatch", WorkflowStepKind.DispatchWork, priorId)],
        };
        Assert.Equal([priorId], FindStepWorkerIds(steps, snapshotWithoutJoinState, "join"));
    }

    [Fact]
    public void RejectWorkflowStepKindsThatTheOperatorViewDoesNotUnderstand()
    {
        var method = typeof(WorkflowRunViewAdapter).GetMethod(
            "CreateStepView",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var run = new WorkflowRunSnapshot(
            WorkflowRunId.New(),
            "workflow.operator.unknown-step",
            WorkflowRunStatus.Running,
            null,
            [],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            [],
            []);

        var exception = Assert.Throws<System.Reflection.TargetInvocationException>(() => method.Invoke(
            null,
            [
                new UnknownWorkflowStepDefinition(),
                run,
                new Dictionary<string, WorkflowStepRunSnapshot>(),
                new Dictionary<WorkerId, WorkerSnapshot?>(),
                new Dictionary<WorkerId, WorkflowChildReceipt>(),
                new Dictionary<string, WorkerSnapshot[]>(),
                3,
            ]));

        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    private static WorkflowStepRunSnapshot StepSnapshot(
        WorkflowStepRunStatus status,
        string name = "step",
        WorkflowStepKind kind = WorkflowStepKind.DispatchWork,
        params WorkerId[] workerIds)
        => new(
            name,
            kind,
            status,
            workerIds,
            DateTimeOffset.UtcNow,
            status is WorkflowStepRunStatus.Completed or WorkflowStepRunStatus.Failed ? DateTimeOffset.UtcNow : null,
            []);

    private static WorkflowStepOperatorView OperatorStep(WorkflowOperatorNodeStatus status)
        => new(
            "child",
            WorkflowStepKind.DispatchWork,
            status,
            null,
            null,
            new WorkflowChildWorkerSummary(0, 0, 0, 0, new Dictionary<WorkerState, int>()),
            [],
            [],
            0,
            [],
            []);

    private static object BuildChildStates(params WorkerState[] states)
    {
        var workers = states
            .Select(state => CreateWorkerSnapshot(WorkerId.New(), state))
            .ToDictionary(worker => worker.Id, worker => (WorkerSnapshot?)worker);
        var method = typeof(WorkflowRunViewAdapter).GetMethod(
            "BuildChildStates",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        return method.Invoke(
            null,
            [workers.Keys.ToArray(), workers, new Dictionary<WorkerId, WorkflowChildReceipt>()])!;
    }

    private static WorkflowOperatorNodeStatus InvokeStatus(string methodName, params object?[] arguments)
    {
        var method = typeof(WorkflowRunViewAdapter).GetMethod(
            methodName,
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        Assert.NotNull(method);
        return Assert.IsType<WorkflowOperatorNodeStatus>(method.Invoke(null, arguments));
    }

    private static IReadOnlyList<WorkerId>? FindStepWorkerIds(
        IReadOnlyList<WorkflowStepDefinition> steps,
        WorkflowRunSnapshot snapshot,
        string stepName)
    {
        var method = typeof(WorkflowRunViewAdapter).GetMethod(
            "TryGetStepWorkerIds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        object?[] arguments = [steps, snapshot, stepName, null];

        var found = Assert.IsType<bool>(method.Invoke(null, arguments));
        return found ? Assert.IsAssignableFrom<IReadOnlyList<WorkerId>>(arguments[3]) : null;
    }

    private static WorkerSnapshot CreateWorkerSnapshot(WorkerId workerId, WorkerState state)
    {
        var now = DateTimeOffset.UtcNow;
        var definition = WorkDefinition.Create("workflow.operator.status-child");
        return new WorkerSnapshot(
            workerId,
            1,
            1,
            definition.Name,
            definition.Category,
            null,
            null,
            new HashSet<WorkIdentifier>(),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            state,
            null,
            null,
            WorkerOptions.Default,
            definition.Configuration,
            [],
            null,
            now,
            now,
            now);
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

    private sealed record UnknownWorkflowStepDefinition()
        : WorkflowStepDefinition("unknown", (WorkflowStepKind)int.MaxValue);
}
