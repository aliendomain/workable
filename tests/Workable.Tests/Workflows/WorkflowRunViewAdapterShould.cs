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

        var missingRun = await new WorkflowRunViewAdapter().StepChildren(
            system,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            WorkflowRunId.New(),
            "notify");
        Assert.Null(missingRun);

        var handle = await system.WorkflowRuntime.Start(
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
                return detail?.Steps.Single(step => step.Name == "notify").Status == WorkflowOperatorNodeStatus.WaitingOnChildren &&
                    detail.Steps.Single(step => step.Name == "settle").Status == WorkflowOperatorNodeStatus.WaitingOnChildren;
            },
            "Expected the parallel and join nodes to wait on running child workers before inspection.");

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

        var handle = await system.WorkflowRuntime.Start(
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

        var handle = await system.WorkflowRuntime.Start(
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

        var handle = await system.WorkflowRuntime.Start(
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
        var joinPage = await new WorkflowRunViewAdapter().StepChildren(
            system,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            handle.RunId!.Value,
            "settle",
            skip: 0,
            take: 10);
        var missingStep = await new WorkflowRunViewAdapter().StepChildren(
            system,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            handle.RunId!.Value,
            "missing-step",
            skip: 0,
            take: 10);
        var runState = await system.WorkflowRuntime.GetVisibleState(
            handle.RunId!.Value,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        Assert.NotNull(runState);
        var missingStatePage = runState!.GetStepWorkerIdsPage(
            "missing-step",
            new HashSet<string>(StringComparer.Ordinal),
            skip: 0,
            take: 1);

        release.TrySetResult();
        await handle.WaitForCompletion();
        runState.MarkStepCompleted("notify", []);
        var fallbackCompositePage = runState.GetStepWorkerIdsPage(
            "notify",
            new HashSet<string>(["first", "second"], StringComparer.Ordinal),
            skip: 0,
            take: 10);
        var completedJoinPage = runState.GetStepWorkerIdsPage(
            "settle",
            new HashSet<string>(["first", "second"], StringComparer.Ordinal),
            skip: 0,
            take: 10);

        Assert.NotNull(page);
        Assert.Equal(2, page!.TotalCount);
        Assert.Equal(1, page.Skip);
        Assert.Equal(1, page.Take);
        var worker = Assert.Single(page.Workers);
        Assert.Equal("workflow.operator.page.second", worker.DefinitionName);
        Assert.Equal(WorkerState.Running, worker.State);
        Assert.NotNull(joinPage);
        Assert.Equal(2, joinPage!.TotalCount);
        Assert.Equal(2, joinPage.Workers.Count);
        Assert.Equal(2, fallbackCompositePage.TotalCount);
        Assert.Equal(2, fallbackCompositePage.WorkerIds.Count);
        Assert.Equal(0, completedJoinPage.TotalCount);
        Assert.Empty(completedJoinPage.WorkerIds);
        Assert.Null(missingStep);
        Assert.Equal(0, missingStatePage.TotalCount);
        Assert.Empty(missingStatePage.WorkerIds);
    }

    [Fact]
    public async Task PageWorkflowRunsBeforeBuildingOperatorPayloads()
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("workflow.operator.run-page.child"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.operator.run-page"),
                workflow => workflow.DispatchWork(
                    "dispatch",
                    WorkDefinition.Create("workflow.operator.run-page.child")));
        });

        await using var provider = services.BuildServiceProvider();
        var system = Assert.IsType<InMemoryWorkSystem>(provider.GetRequiredService<IWorkSystemRegistry>().Default);
        await system.Start();
        for (var index = 0; index < 4; index++)
        {
            var handle = await system.WorkflowRuntime.Start(
                "workflow.operator.run-page",
                WorkRequestContext.Create(WorkInvocationChannel.InProcess));
            await handle.WaitForCompletion();
        }

        var page = await new WorkflowRunViewAdapter().RunsPage(
            system,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            includeFinal: true,
            skip: 1,
            take: 2);
        var clamped = await new WorkflowRunViewAdapter().RunsPage(
            system,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            includeFinal: true,
            skip: -1,
            take: int.MaxValue);
        var skipClamped = await new WorkflowRunViewAdapter().RunsPage(
            system,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            includeFinal: true,
            skip: int.MaxValue,
            take: 1);

        Assert.Equal(4, page.TotalCount);
        Assert.Equal(1, page.Skip);
        Assert.Equal(2, page.Take);
        Assert.Equal(2, page.Runs.Count);
        Assert.Equal(0, clamped.Skip);
        Assert.Equal(WorkflowRunViewAdapter.MaximumRunPageSize, clamped.Take);
        Assert.Equal(4, clamped.Runs.Count);
        Assert.Equal(WorkflowRunViewAdapter.MaximumRunPageSkip, skipClamped.Skip);
        Assert.Empty(skipClamped.Runs);
    }

    [Fact]
    public async Task ResetJoinFallbackAtEarlierCompletedJoin()
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("workflow.operator.join-reset.first"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
            builder.AddWork(
                WorkDefinition.Create("workflow.operator.join-reset.second"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.operator.join-reset"),
                workflow => workflow
                    .DispatchWork("first", WorkDefinition.Create("workflow.operator.join-reset.first"))
                    .Join("first-join")
                    .DispatchWork("second", WorkDefinition.Create("workflow.operator.join-reset.second"))
                    .Join("second-join"));
        });

        await using var provider = services.BuildServiceProvider();
        var system = Assert.IsType<InMemoryWorkSystem>(provider.GetRequiredService<IWorkSystemRegistry>().Default);
        await system.Start();
        var handle = await system.WorkflowRuntime.Start(
            "workflow.operator.join-reset",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        await handle.WaitForCompletion();
        var runState = await system.WorkflowRuntime.GetVisibleState(
            handle.RunId!.Value,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        Assert.NotNull(runState);
        var page = runState!.GetStepWorkerIdsPage(
            "second-join",
            new HashSet<string>(["first", "second"], StringComparer.Ordinal),
            skip: 0,
            take: 10);
        Assert.Equal(0, page.TotalCount);
        Assert.Empty(page.WorkerIds);
    }

    [Fact]
    public async Task BoundAuthoritativeWorkerReadsForOneOperatorView()
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("workflow.operator.read-bound"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
        });

        await using var provider = services.BuildServiceProvider();
        var system = Assert.IsType<InMemoryWorkSystem>(provider.GetRequiredService<IWorkSystemRegistry>().Default);
        var session = await system.CreateSession(WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var method = typeof(WorkflowRunViewAdapter).GetMethod(
            "LoadWorkers",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var task = Assert.IsAssignableFrom<Task<IReadOnlyDictionary<WorkerId, WorkerSnapshot?>>>(method.Invoke(
            null,
            [
                system,
                session.Catalog,
                Enumerable.Range(0, 300).Select(_ => WorkerId.New()),
                CancellationToken.None,
            ]));

        var workers = await task;

        Assert.Equal(256, workers.Count);
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
        var handle = await system.WorkflowRuntime.Start("workflow.operator.secured", startContext);
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

        var handle = await system.WorkflowRuntime.Start(
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

        var runId = (await system.WorkflowRuntime.Start(
            "workflow.operator.actions",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess))).RunId!.Value;
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

    [Fact]
    public async Task ReportOnlyLifecycleActionsTheViewingCallerMayExecute()
    {
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        var reader = new WorkActor("workflow-actions-reader");
        var pauser = new WorkActor("workflow-actions-pauser");
        var seed = new WorkActor("workflow-actions-seed");
        services.AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(
            new Dictionary<string, IReadOnlySet<string>>
            {
                [reader.Id!] = Groups("workflow.actions.read"),
                [pauser.Id!] = Groups("workflow.actions.read", "workflow.actions.pause"),
                [seed.Id!] = Groups("workflow.actions.seed"),
            }));
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization();
            builder.AddWork(
                WorkDefinition.Create("workflow.operator.authorized-actions.child"),
                async (_, _, cancellationToken) =>
                {
                    childStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.operator.authorized-actions"),
                workflow => workflow.DispatchWork(
                    "dispatch",
                    WorkDefinition.Create("workflow.operator.authorized-actions.child")),
                authorization => authorization
                    .AllowReadToGroups("workflow.actions.read")
                    .AllowOperationsToGroups(
                        ["workflow.actions.pause"],
                        WorkOperationPermissions.Pause)
                    .AllowOperationsToGroups(
                        ["workflow.actions.seed"],
                        WorkOperationPermissions.Operate));
        });

        await using var provider = services.BuildServiceProvider();
        var system = Assert.IsType<InMemoryWorkSystem>(provider.GetRequiredService<IWorkSystemRegistry>().Default);
        await system.Start();
        var runId = (await system.WorkflowRuntime.Start(
            "workflow.operator.authorized-actions",
            CreateContext(seed, "workflow.actions.seed"))).RunId!.Value;
        await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var views = new WorkflowRunViewAdapter();

        var readerView = await views.Run(
            system,
            CreateContext(reader, "workflow.actions.read"),
            runId);
        var pauserView = await views.Run(
            system,
            CreateContext(pauser, "workflow.actions.read", "workflow.actions.pause"),
            runId);
        var readerList = await views.Runs(
            system,
            CreateContext(reader, "workflow.actions.read"));
        var readerChildren = await views.StepChildren(
            system,
            CreateContext(reader, "workflow.actions.read"),
            runId,
            "dispatch");

        Assert.NotNull(readerView);
        Assert.False(readerView!.AvailableActions.Start);
        Assert.False(readerView.AvailableActions.Pause);
        Assert.False(readerView.AvailableActions.Cancel);
        var hiddenChildStep = Assert.Single(readerView.Steps);
        Assert.Equal(0, hiddenChildStep.Children.Total);
        Assert.Empty(hiddenChildStep.ChildWorkerIds);
        Assert.Empty(hiddenChildStep.ChildSample);
        Assert.Equal(0, readerView.OutstandingChildren.Total);
        Assert.Equal(0, Assert.Single(readerList.Runs).OutstandingChildren.Total);
        Assert.NotNull(readerChildren);
        Assert.Equal(0, readerChildren!.TotalCount);
        Assert.Empty(readerChildren.Workers);
        Assert.NotNull(pauserView);
        Assert.False(pauserView!.AvailableActions.Start);
        Assert.True(pauserView.AvailableActions.Pause);
        Assert.False(pauserView.AvailableActions.Cancel);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => views.Run(
            system,
            CreateContext(reader, "workflow.actions.read"),
            runId,
            childSampleSize: -1));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => views.Runs(
            system,
            CreateContext(reader, "workflow.actions.read"),
            childSampleSize: WorkflowRunViewAdapter.MaximumChildSampleSize + 1));

        Assert.True((await system.WorkflowRuntime.Execute(
            runId,
            WorkflowAction.Cancel,
            CreateContext(seed, "workflow.actions.seed"))).IsAccepted);
        await TestEventually.Until(
            () => system.WorkflowRuntime.Get(runId)?.Status == WorkflowRunStatus.Canceled,
            "Expected the authorized workflow cancellation to reach a final state.");
        var finalReaderView = await views.Run(
            system,
            CreateContext(reader, "workflow.actions.read"),
            runId);
        Assert.NotNull(finalReaderView);
        Assert.All(finalReaderView!.Steps, step =>
        {
            Assert.Empty(step.ChildWorkerIds);
            Assert.Empty(step.ChildSample);
            Assert.Equal(0, step.Children.Total);
        });
    }

    [Fact]
    public async Task SanitizeUnhandledChildExceptionsInWorkflowOperatorMessages()
    {
        var services = new ServiceCollection();
        var reader = new WorkActor("workflow-failure-reader");
        var operatorActor = new WorkActor("workflow-failure-operator");
        services.AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(
            new Dictionary<string, IReadOnlySet<string>>
            {
                [reader.Id!] = Groups("workflow.failure.read"),
                [operatorActor.Id!] = Groups("workflow.failure.operate"),
            }));
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization();
            builder.AddWork(
                WorkDefinition.Create("workflow.operator.failure.child"),
                (_, _, _) => throw new InvalidOperationException("secret child failure"));
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.operator.failure"),
                workflow => workflow.DispatchWork(
                    "dispatch",
                    WorkDefinition.Create("workflow.operator.failure.child")),
                authorization => authorization
                    .AllowReadToGroups("workflow.failure.read")
                    .AllowOperateToGroups("workflow.failure.operate"));
        });

        await using var provider = services.BuildServiceProvider();
        var system = Assert.IsType<InMemoryWorkSystem>(provider.GetRequiredService<IWorkSystemRegistry>().Default);
        await system.Start();
        var handle = await system.WorkflowRuntime.Start(
            "workflow.operator.failure",
            CreateContext(operatorActor, "workflow.failure.operate"));
        var runId = handle.RunId!.Value;
        await TestEventually.Until(
            () => system.WorkflowRuntime.Get(runId)?.Status == WorkflowRunStatus.Blocked,
            "Expected the failed child to block the workflow run.");
        var views = new WorkflowRunViewAdapter();

        var list = await views.Runs(
            system,
            CreateContext(reader, "workflow.failure.read"),
            includeFinal: true);
        var detail = await views.Run(
            system,
            CreateContext(reader, "workflow.failure.read"),
            runId);
        var listMessage = Assert.Single(Assert.Single(list.Runs).Messages);
        var detailMessage = Assert.Single(detail!.Messages);

        Assert.Equal(WorkflowRunStatus.Blocked, detail.Status);
        Assert.Equal("workable.workflow.child_completion_unsuccessful", listMessage.Code);
        Assert.Equal("A workflow child completed unsuccessfully with status 'Failed'.", listMessage.Text);
        Assert.Null(listMessage.Metadata);
        Assert.Equal(listMessage, detailMessage);
        Assert.DoesNotContain("secret child failure", listMessage.Text, StringComparison.Ordinal);
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
        Assert.Empty(FindStepWorkerIds(steps, snapshotWithoutJoinState, "parallel")!);
        Assert.Empty(FindStepWorkerIds(steps, snapshotWithoutJoinState, "branch")!);
        Assert.Equal([priorId], FindStepWorkerIds(steps, snapshotWithoutJoinState, "join"));
    }

    [Fact]
    public void ResolveNestedStepIdsWorkflowProvenanceAndEveryChildResolutionSource()
    {
        var definition = WorkDefinition.Create("workflow.operator.lookup");
        var childId = WorkerId.New();
        IReadOnlyList<WorkflowStepDefinition> steps =
        [
            new BranchWorkflowStepDefinition(
                "branch",
                [
                    new DispatchWorkflowStepDefinition("dispatch", definition),
                    new DispatchEachWorkflowStepDefinition(
                        "each",
                        new WorkflowStepReference<object[]>("dispatch"),
                        definition,
                        new WorkflowOutputSelector(null)),
                ]),
            new ParallelWorkflowStepDefinition("parallel", []),
            new JoinWorkflowStepDefinition("join"),
            new UnknownWorkflowStepDefinition(),
        ];
        var snapshot = new WorkflowRunSnapshot(
            WorkflowRunId.New(),
            "workflow.operator.lookup",
            WorkflowRunStatus.Running,
            null,
            [
                StepSnapshot(WorkflowStepRunStatus.Completed, "dispatch", WorkflowStepKind.DispatchWork, childId),
                StepSnapshot(WorkflowStepRunStatus.Running, "branch", WorkflowStepKind.Branch),
                StepSnapshot(WorkflowStepRunStatus.Running, "parallel", WorkflowStepKind.Parallel),
                StepSnapshot(WorkflowStepRunStatus.Running, "join", WorkflowStepKind.Join),
            ],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            [],
            []);

        Assert.Equal([childId], FindStepWorkerIds(steps, snapshot, "dispatch"));
        Assert.Empty(FindStepWorkerIds(steps, snapshot, "each")!);
        Assert.Empty(FindStepWorkerIds(steps, snapshot, "parallel")!);
        Assert.Equal([childId], FindStepWorkerIds(steps, snapshot, "join"));
        Assert.Empty(FindStepWorkerIds(steps, snapshot, "unknown")!);

        var worker = CreateWorkerSnapshot(childId, WorkerState.Completed);
        Assert.Null(InvokePrivate<string?>("GetWorkflowStepName", worker));
        var workflowWorker = worker with
        {
            WorkflowProvenance = new WorkflowProvenance(
                WorkflowRunId.New(),
                "workflow.operator.lookup",
                "dispatch"),
        };
        Assert.Equal("dispatch", InvokePrivate<string?>("GetWorkflowStepName", workflowWorker));

        var workers = new Dictionary<WorkerId, WorkerSnapshot?> { [childId] = worker };
        var nullWorkers = new Dictionary<WorkerId, WorkerSnapshot?> { [childId] = null };
        var completedReceipt = new WorkflowChildReceipt(
            childId,
            "dispatch",
            definition.Name,
            WorkerState.Completed,
            DateTimeOffset.UtcNow,
            [],
            null);
        var failedReceipt = completedReceipt with { State = WorkerState.Failed };
        Assert.True(InvokePrivate<bool>(
            "IsResolvedChild",
            childId,
            workers,
            new Dictionary<WorkerId, WorkflowChildReceipt>()));
        Assert.True(InvokePrivate<bool>(
            "IsResolvedChild",
            childId,
            nullWorkers,
            new Dictionary<WorkerId, WorkflowChildReceipt> { [childId] = completedReceipt }));
        Assert.False(InvokePrivate<bool>(
            "IsResolvedChild",
            childId,
            nullWorkers,
            new Dictionary<WorkerId, WorkflowChildReceipt> { [childId] = failedReceipt }));
        Assert.False(InvokePrivate<bool>(
            "IsResolvedChild",
            childId,
            new Dictionary<WorkerId, WorkerSnapshot?>(),
            new Dictionary<WorkerId, WorkflowChildReceipt>()));
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

    [Fact]
    public void CreatePendingViewsForEveryUnstartedStepShapeWithoutRetainedSnapshots()
    {
        var definition = WorkDefinition.Create("workflow.operator.unstarted.child");
        var presentWorker = CreateWorkerSnapshot(WorkerId.New(), WorkerState.Queued);
        var run = new WorkflowRunSnapshot(
            WorkflowRunId.New(),
            "workflow.operator.unstarted",
            WorkflowRunStatus.Running,
            null,
            [],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            [],
            []);
        var snapshots = new Dictionary<string, WorkflowStepRunSnapshot>();
        var workers = new Dictionary<WorkerId, WorkerSnapshot?>
        {
            [presentWorker.Id] = presentWorker,
        };
        var receipts = new Dictionary<WorkerId, WorkflowChildReceipt>();
        var workersByStep = new Dictionary<string, WorkerSnapshot[]>(StringComparer.Ordinal)
        {
            ["dispatch-present"] = [presentWorker],
            ["nested"] = [presentWorker],
        };
        WorkflowStepDefinition[] steps =
        [
            new DispatchWorkflowStepDefinition("dispatch-missing", definition),
            new DispatchWorkflowStepDefinition("dispatch-present", definition),
            new DispatchEachWorkflowStepDefinition(
                "each",
                new WorkflowStepReference<object[]>("source"),
                definition,
                new WorkflowOutputSelector(null)),
            new ParallelWorkflowStepDefinition(
                "parallel",
                [new DispatchWorkflowStepDefinition("nested", definition)]),
            new BranchWorkflowStepDefinition(
                "branch",
                [new DispatchWorkflowStepDefinition("branch-child", definition)]),
            new JoinWorkflowStepDefinition("join"),
        ];

        var views = steps
            .Select(step => CreateStepView(step, run, snapshots, workers, receipts, workersByStep))
            .ToArray();

        Assert.Equal(
            [
                WorkflowOperatorNodeStatus.Pending,
                WorkflowOperatorNodeStatus.WaitingOnChildren,
                WorkflowOperatorNodeStatus.Pending,
                WorkflowOperatorNodeStatus.Pending,
                WorkflowOperatorNodeStatus.Pending,
                WorkflowOperatorNodeStatus.Pending,
            ],
            views.Select(view => view.Status));
        Assert.Empty(views[0].ChildSample);
        Assert.Single(views[1].ChildSample);
        Assert.Single(views[3].Steps);
        Assert.Single(views[4].Steps);
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

    private static WorkflowStepOperatorView CreateStepView(
        WorkflowStepDefinition step,
        WorkflowRunSnapshot run,
        IReadOnlyDictionary<string, WorkflowStepRunSnapshot> snapshots,
        IReadOnlyDictionary<WorkerId, WorkerSnapshot?> workers,
        IReadOnlyDictionary<WorkerId, WorkflowChildReceipt> receipts,
        IReadOnlyDictionary<string, WorkerSnapshot[]> workersByStep)
    {
        var method = typeof(WorkflowRunViewAdapter).GetMethod(
            "CreateStepView",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        return Assert.IsType<WorkflowStepOperatorView>(method.Invoke(
            null,
            [step, run, snapshots, workers, receipts, workersByStep, 3]));
    }

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

    private static T InvokePrivate<T>(string methodName, params object?[] arguments)
    {
        var method = typeof(WorkflowRunViewAdapter)
            .GetMethods(System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
            .Single(candidate =>
                candidate.Name == methodName &&
                candidate.GetParameters().Length == arguments.Length);
        return (T)method.Invoke(null, arguments)!;
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
            Authorization = WorkAuthorizationSnapshot.CreateForSystem(
                systemName: null,
                actor,
                Groups(groups),
                readableDefinitionIds: null),
        };

    private static IReadOnlySet<string> Groups(params string[] groups)
        => new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);

    private sealed class TestGroupProvider(IReadOnlyDictionary<string, IReadOnlySet<string>> groupsByActor)
        : IWorkAuthorizationGroupProvider
    {
        public ValueTask<IReadOnlySet<string>> GetGroups(
            WorkActor actor,
            string? systemName,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlySet<string>>(groupsByActor.TryGetValue(actor.Id ?? string.Empty, out var groups)
                ? groups
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private sealed record UnknownWorkflowStepDefinition()
        : WorkflowStepDefinition("unknown", (WorkflowStepKind)int.MaxValue);
}
