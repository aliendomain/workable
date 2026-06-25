using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Workflows")]
public sealed class WorkflowRuntimeShould
{
    [Fact]
    public async Task RejectUnknownWorkflow()
    {
        var services = new ServiceCollection();

        services.AddWorkableSystem(builder => builder.RequireAuthorization(false));

        using var provider = services.BuildServiceProvider();
        var system = (InMemoryWorkSystem)provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var handle = system.WorkflowRuntime.Start(
            "workflow.missing",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var completion = await handle.WaitForCompletion();

        Assert.False(handle.StartOutcome.IsAccepted);
        Assert.Equal(WorkflowRunStatus.NotFound, completion.Status);
        Assert.Contains(
            handle.StartOutcome.Messages,
            message => message.Code == "workable.workflow.definition.not_found");
    }

    [Fact]
    public async Task RejectUnauthorizedWorkflowStart()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkAuthorizationGroupProvider>(
            new TestWorkflowGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["workflow-user"] = Groups("workflow.read"),
            }));
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(true);
            builder.AddWork(
                WorkDefinition.Create("sample.dispatch"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.secured"),
                workflow => workflow.DispatchWork("dispatch", "sample.dispatch"),
                authorize: auth => auth.AllowOperateToGroups("workflow.ops"));
        });

        using var provider = services.BuildServiceProvider();
        var system = (InMemoryWorkSystem)provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var handle = system.WorkflowRuntime.Start(
            "workflow.secured",
            WorkRequestContext.Create(
                WorkInvocationChannel.InProcess,
                new WorkActor("workflow-user")));
        var completion = await handle.WaitForCompletion();

        Assert.False(handle.StartOutcome.IsAccepted);
        Assert.Equal(WorkflowRunStatus.Unauthorized, completion.Status);
        Assert.Contains(
            handle.StartOutcome.Messages,
            message => message.Code == "workable.workflow.definition.unauthorized");
    }

    [Fact]
    public async Task AllowWorkflowStartForSystemOperateAllAuthorizationFromRequestContext()
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(true);
            builder.ConfigureAuthorization(auth => auth.AllowOperateAllWorkToGroups("workflow.operate-all"));
            builder.AddWork(
                WorkDefinition.Create("sample.dispatch"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.secured.system-admin"),
                workflow => workflow.DispatchWork("dispatch", "sample.dispatch"),
                authorize: auth => auth.AllowOperateToGroups("workflow.ops"));
        });

        using var provider = services.BuildServiceProvider();
        var system = (InMemoryWorkSystem)provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("workflow-user", "Workflow User"),
            isAuthenticated: true) with
        {
            Authorization = WorkAuthorizationSnapshot.Create(
                new WorkActor("workflow-user", "Workflow User"),
                Groups("workflow.operate-all"),
                readableDefinitionIds: null),
        };

        var handle = system.WorkflowRuntime.Start("workflow.secured.system-admin", requestContext);
        var completion = await handle.WaitForCompletion();

        Assert.True(handle.StartOutcome.IsAccepted);
        Assert.NotEqual(WorkflowRunStatus.Unauthorized, completion.Status);
    }

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
    public async Task ForwardWorkflowIdentifiersAndStripAuthorizationWhenDispatchingChildWork()
    {
        WorkRequestContext? childRequestContext = null;
        WorkInput? childInput = null;
        var services = new ServiceCollection();

        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("sample.dispatch"),
                (context, input, _) =>
                {
                    childRequestContext = context.RequestContext;
                    childInput = input;
                    return Task.FromResult(WorkExecutionResult.Success());
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.dispatch.context"),
                workflow => workflow.DispatchWork(
                    "dispatch",
                    "sample.dispatch",
                    WorkInput.Empty.WithIdentifier(new WorkIdentifier("existing", "value"))));
        });

        using var provider = services.BuildServiceProvider();
        var system = (InMemoryWorkSystem)provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("workflow-user", "Workflow User"),
            description: "Start workflow with auth context.",
            isAuthenticated: true) with
        {
            Authorization = WorkAuthorizationSnapshot.Create(
                new WorkActor("workflow-user", "Workflow User"),
                Groups("workflow.ops"),
                readableDefinitionIds: null),
        };

        var handle = system.WorkflowRuntime.Start(
            "workflow.dispatch.context",
            requestContext);
        var completion = await handle.WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.NotNull(childRequestContext);
        Assert.Equal("workflow-user", childRequestContext!.Actor.Id);
        Assert.True(childRequestContext.IsAuthenticated);
        Assert.Null(childRequestContext.Authorization);
        Assert.NotNull(childInput);
        Assert.Contains(new WorkIdentifier("existing", "value"), childInput!.Identifiers!);
        Assert.Contains(
            childInput.Identifiers!,
            identifier => identifier.Type == "workflow-run" && identifier.Value == handle.RunId!.Value.ToString());
        Assert.Contains(
            childInput.Identifiers!,
            identifier => identifier.Type == "workflow-definition" && identifier.Value == "workflow.dispatch.context");
        Assert.Contains(
            childInput.Identifiers!,
            identifier => identifier.Type == "workflow-step" && identifier.Value == "dispatch");
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
    public async Task FailWorkflowWhenChildDispatchIsRejected()
    {
        var services = new ServiceCollection();

        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.dispatch.missing-child"),
                workflow => workflow.DispatchWork("dispatch", "sample.missing"));
        });

        using var provider = services.BuildServiceProvider();
        var system = (InMemoryWorkSystem)provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var handle = system.WorkflowRuntime.Start(
            "workflow.dispatch.missing-child",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var completion = await handle.WaitForCompletion();

        Assert.False(completion.IsCompletedSuccessfully);
        Assert.NotNull(completion.Run);
        var run = completion.Run!;
        Assert.Equal(WorkflowRunStatus.Failed, run.Status);
        Assert.Equal(WorkflowStepRunStatus.Failed, run.Steps.Single(step => step.Name == "dispatch").Status);
        Assert.Contains(
            completion.Messages,
            message => message.Code == "workable.definition.not_found");
    }

    [Fact]
    public async Task FailWorkflowWhenTrailingChildCompletesUnsuccessfullyWithoutJoin()
    {
        var services = new ServiceCollection();

        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("sample.fail"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("sample.fail", "Child failed.")])));
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.trailing.failure"),
                workflow => workflow.DispatchWork("dispatch", "sample.fail"));
        });

        using var provider = services.BuildServiceProvider();
        var system = (InMemoryWorkSystem)provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var handle = system.WorkflowRuntime.Start(
            "workflow.trailing.failure",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var completion = await handle.WaitForCompletion();

        Assert.False(completion.IsCompletedSuccessfully);
        Assert.NotNull(completion.Run);
        Assert.Equal(WorkflowRunStatus.Failed, completion.Run!.Status);
        Assert.Equal(WorkflowStepRunStatus.Completed, completion.Run.Steps.Single(step => step.Name == "dispatch").Status);
        Assert.Contains(
            completion.Messages,
            message => message.Code == "sample.fail");
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
    public async Task RejectDurableWorkflowWhenNoPersistenceStoreIsRegistered()
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
            message => message.Code == "workable.workflow.coordination.persistence_store_required");
    }

    [Fact]
    public async Task ExecuteDurableWorkflowAndAutoUpgradeChildDispatchToDurableQueueing()
    {
        var ran = 0;
        var store = new TestWorkflowPersistenceStore();
        var services = new ServiceCollection();

        services.AddSingleton<IWorkPersistenceStore>(store);
        services.AddWorkableSystem("workflow-tests", builder =>
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
                WorkflowDefinition.Create(
                    "workflow.durable.dispatch",
                    coordination: WorkflowCoordinationConfiguration.Durable),
                workflow => workflow.DispatchWork("dispatch", "sample.dispatch"));
        });

        using var provider = services.BuildServiceProvider();
        var system = GetNamedSystem(provider, "workflow-tests");
        await system.Start();

        var handle = system.WorkflowRuntime.Start(
            "workflow.durable.dispatch",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var completion = await handle.WaitForCompletion();

        Assert.True(handle.StartOutcome.IsAccepted);
        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(1, Volatile.Read(ref ran));
        Assert.Single(store.Enqueued);
        Assert.True(store.Enqueued[0].Configuration.Coordination.IsDurabilityEnabled);
        Assert.Contains(handle.RunId!.Value, store.DeletedWorkflowRuns);
    }

    [Fact]
    public async Task DeleteDurableRunWhenTrailingChildCompletesUnsuccessfullyWithoutJoin()
    {
        var store = new TestWorkflowPersistenceStore();
        var services = new ServiceCollection();

        services.AddSingleton<IWorkPersistenceStore>(store);
        services.AddWorkableSystem("workflow-tests", builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("sample.fail"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("sample.fail", "Child failed.")])));
            builder.AddWorkflow(
                WorkflowDefinition.Create(
                    "workflow.durable.trailing.failure",
                    coordination: WorkflowCoordinationConfiguration.Durable),
                workflow => workflow.DispatchWork("dispatch", "sample.fail"));
        });

        using var provider = services.BuildServiceProvider();
        var system = GetNamedSystem(provider, "workflow-tests");
        await system.Start();

        var handle = system.WorkflowRuntime.Start(
            "workflow.durable.trailing.failure",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var completion = await handle.WaitForCompletion();

        Assert.False(completion.IsCompletedSuccessfully);
        Assert.Equal(WorkflowRunStatus.Failed, completion.Status);
        Assert.Contains(handle.RunId!.Value, store.DeletedWorkflowRuns);
    }

    [Fact]
    public async Task DeleteDurableRunWhenWorkflowTargetsMissingChildWork()
    {
        var store = new TestWorkflowPersistenceStore();
        var services = new ServiceCollection();

        services.AddSingleton<IWorkPersistenceStore>(store);
        services.AddWorkableSystem("workflow-tests", builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWorkflow(
                WorkflowDefinition.Create(
                    "workflow.durable.missing-child",
                    coordination: WorkflowCoordinationConfiguration.Durable),
                workflow => workflow.DispatchWork("dispatch", "sample.missing"));
        });

        using var provider = services.BuildServiceProvider();
        var system = GetNamedSystem(provider, "workflow-tests");
        await system.Start();

        var handle = system.WorkflowRuntime.Start(
            "workflow.durable.missing-child",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var completion = await handle.WaitForCompletion();

        Assert.False(completion.IsCompletedSuccessfully);
        Assert.Equal(WorkflowRunStatus.Failed, completion.Status);
        Assert.Contains(
            completion.Messages,
            message => message.Code == "workable.workflow.execution_exception");
        Assert.Contains(handle.RunId!.Value, store.DeletedWorkflowRuns);
    }

    [Fact]
    public async Task RejectDurableWorkflowWhenSystemIsUnnamed()
    {
        var store = new TestWorkflowPersistenceStore();
        var services = new ServiceCollection();

        services.AddSingleton<IWorkPersistenceStore>(store);
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("sample.dispatch"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
            builder.AddWorkflow(
                WorkflowDefinition.Create(
                    "workflow.durable.dispatch",
                    coordination: WorkflowCoordinationConfiguration.Durable),
                workflow => workflow.DispatchWork("dispatch", "sample.dispatch"));
        });

        using var provider = services.BuildServiceProvider();
        var system = (InMemoryWorkSystem)provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var handle = system.WorkflowRuntime.Start(
            "workflow.durable.dispatch",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var completion = await handle.WaitForCompletion();

        Assert.False(handle.StartOutcome.IsAccepted);
        Assert.Equal(WorkflowRunStatus.Invalid, completion.Status);
        Assert.Contains(
            handle.StartOutcome.Messages,
            message => message.Code == "workable.workflow.coordination.named_system_required");
    }

    [Fact]
    public async Task RecoverDurableWorkflowRunAndReconnectReplayedChildWorkers()
    {
        var store = new TestWorkflowPersistenceStore();
        var holdChildren = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var firstProvider = CreateDurableParallelWorkflowProvider(
            store,
            cancellationToken => holdChildren.Task.WaitAsync(cancellationToken),
            cancellationToken => holdChildren.Task.WaitAsync(cancellationToken));
        var firstSystem = GetNamedSystem(firstProvider, "workflow-tests");
        await firstSystem.Start();

        var handle = firstSystem.WorkflowRuntime.Start(
            "workflow.durable.parallel",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        WorkflowRunSnapshot? interruptedRun = null;
        await TestEventually.Until(
            () =>
            {
                interruptedRun = firstSystem.WorkflowRuntime.Get(handle.RunId!.Value);
                return interruptedRun is not null &&
                    interruptedRun.Steps.Single(step => step.Name == "dispatch").WorkerIds.Count == 2 &&
                    interruptedRun.Steps.Single(step => step.Name == "join").Status == WorkflowStepRunStatus.Running;
            },
            "Expected the durable workflow to persist both child workers and begin waiting at the join step.");

        var workerIds = interruptedRun!.Steps.Single(step => step.Name == "dispatch").WorkerIds;
        await StopWithTimeout(firstSystem, TimeSpan.FromSeconds(2));
        var canceled = await handle.WaitForCompletion();
        Assert.Equal(WorkflowRunStatus.Canceled, canceled.Status);
        foreach (var workerId in workerIds)
        {
            store.Requeue(workerId);
        }

        var resumedChildren = 0;
        using var secondProvider = CreateDurableParallelWorkflowProvider(
            store,
            _ =>
            {
                Interlocked.Increment(ref resumedChildren);
                return Task.CompletedTask;
            },
            _ =>
            {
                Interlocked.Increment(ref resumedChildren);
                return Task.CompletedTask;
            });
        var secondSystem = GetNamedSystem(secondProvider, "workflow-tests");
        await secondSystem.Start();

        await TestEventually.Until(
            () =>
            {
                var recovered = secondSystem.WorkflowRuntime.Get(handle.RunId!.Value);
                return recovered is not null && recovered.Status == WorkflowRunStatus.Completed;
            },
            "Expected the recovered durable workflow to resume after replay and complete.",
            timeout: TimeSpan.FromSeconds(15));

        var completion = secondSystem.WorkflowRuntime.Get(handle.RunId!.Value)
            ?? throw new InvalidOperationException("Expected recovered workflow run.");

        Assert.Equal(WorkflowStepRunStatus.Completed, completion.Steps.Single(step => step.Name == "dispatch").Status);
        Assert.Equal(WorkflowStepRunStatus.Completed, completion.Steps.Single(step => step.Name == "join").Status);
        Assert.Equal(2, Volatile.Read(ref resumedChildren));
        Assert.All(store.Enqueued, request => Assert.True(request.Configuration.Coordination.IsDurabilityEnabled));
        Assert.Contains(handle.RunId!.Value, store.DeletedWorkflowRuns);
        Assert.Equal(2, store.WorkflowInitializations.Count);
        Assert.Equal(
            2,
            store.WorkflowInitializations
                .Select(initialization => initialization.WorkSystemId)
                .Distinct()
                .Count());
        Assert.All(
            store.WorkflowInitializations,
            initialization => Assert.Equal("workflow-tests", initialization.WorkSystemName));
    }

    [Fact]
    public async Task RecoverDurableWorkflowRunAndOnlyReplayOutstandingParallelChildren()
    {
        var store = new TestWorkflowPersistenceStore();
        using var firstProvider = CreateDurableParallelWorkflowProvider(
            store,
            _ => Task.CompletedTask,
            cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        var firstSystem = GetNamedSystem(firstProvider, "workflow-tests");
        await firstSystem.Start();

        var handle = firstSystem.WorkflowRuntime.Start(
            "workflow.durable.parallel",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        WorkflowRunSnapshot? interruptedRun = null;
        await TestEventually.Until(
            () =>
            {
                interruptedRun = firstSystem.WorkflowRuntime.Get(handle.RunId!.Value);
                return interruptedRun is not null &&
                    interruptedRun.Steps.Single(step => step.Name == "join").Status == WorkflowStepRunStatus.Running &&
                    interruptedRun.Steps.Single(step => step.Name == "join").WorkerIds.Count == 1;
            },
            "Expected the durable workflow join step to retain only the unfinished child worker before shutdown.",
            timeout: TimeSpan.FromSeconds(15));

        var remainingWorkerId = interruptedRun!.Steps.Single(step => step.Name == "join").WorkerIds.Single();
        var remainingRequest = store.Enqueued.Single(request => request.WorkerId == remainingWorkerId);

        await StopWithTimeout(firstSystem, TimeSpan.FromSeconds(2));
        var canceled = await handle.WaitForCompletion();
        Assert.Equal(WorkflowRunStatus.Canceled, canceled.Status);
        store.Requeue(remainingWorkerId);

        var resumedAlpha = 0;
        var resumedBeta = 0;
        using var secondProvider = CreateDurableParallelWorkflowProvider(
            store,
            _ =>
            {
                Interlocked.Increment(ref resumedAlpha);
                return Task.CompletedTask;
            },
            _ =>
            {
                Interlocked.Increment(ref resumedBeta);
                return Task.CompletedTask;
            });
        var secondSystem = GetNamedSystem(secondProvider, "workflow-tests");
        await secondSystem.Start();

        await TestEventually.Until(
            () =>
            {
                var recovered = secondSystem.WorkflowRuntime.Get(handle.RunId!.Value);
                return recovered is not null && recovered.Status == WorkflowRunStatus.Completed;
            },
            "Expected the recovered durable workflow to resume only the unfinished child and complete.",
            timeout: TimeSpan.FromSeconds(15));

        Assert.Equal("sample.beta", remainingRequest.Definition.Name);
        Assert.Equal(0, Volatile.Read(ref resumedAlpha));
        Assert.Equal(1, Volatile.Read(ref resumedBeta));
    }

    [Fact]
    public async Task RecoverDurableWorkflowRunAndFailWhenAReplayedChildFailsAfterRestart()
    {
        var store = new TestWorkflowPersistenceStore();
        using var firstProvider = CreateDurableParallelWorkflowProvider(
            store,
            _ => Task.CompletedTask,
            cancellationToken => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken));
        var firstSystem = GetNamedSystem(firstProvider, "workflow-tests");
        await firstSystem.Start();

        var handle = firstSystem.WorkflowRuntime.Start(
            "workflow.durable.parallel",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        WorkflowRunSnapshot? interruptedRun = null;
        await TestEventually.Until(
            () =>
            {
                interruptedRun = firstSystem.WorkflowRuntime.Get(handle.RunId!.Value);
                return interruptedRun is not null &&
                    interruptedRun.Steps.Single(step => step.Name == "join").Status == WorkflowStepRunStatus.Running &&
                    interruptedRun.Steps.Single(step => step.Name == "join").WorkerIds.Count == 1;
            },
            "Expected the durable workflow join step to retain only the unfinished child worker before restart.",
            timeout: TimeSpan.FromSeconds(15));

        var remainingWorkerId = interruptedRun!.Steps.Single(step => step.Name == "join").WorkerIds.Single();
        var remainingRequest = store.Enqueued.Single(request => request.WorkerId == remainingWorkerId);

        await StopWithTimeout(firstSystem, TimeSpan.FromSeconds(2));
        var canceled = await handle.WaitForCompletion();
        Assert.Equal(WorkflowRunStatus.Canceled, canceled.Status);
        store.Requeue(remainingWorkerId);

        using var secondProvider = CreateDurableParallelWorkflowProvider(
            store,
            _ => Task.CompletedTask,
            _ => Task.FromException(new InvalidOperationException("replayed child failed")));
        var secondSystem = GetNamedSystem(secondProvider, "workflow-tests");
        await secondSystem.Start();

        await TestEventually.Until(
            () =>
            {
                var recovered = secondSystem.WorkflowRuntime.Get(handle.RunId!.Value);
                return recovered is not null && recovered.Status == WorkflowRunStatus.Failed;
            },
            "Expected the recovered durable workflow to fail when the remaining replayed child fails.",
            timeout: TimeSpan.FromSeconds(15));

        var completion = secondSystem.WorkflowRuntime.Get(handle.RunId!.Value)
            ?? throw new InvalidOperationException("Expected recovered workflow run.");
        Assert.Equal("sample.beta", remainingRequest.Definition.Name);
        Assert.Contains(
            completion.Messages,
            message => message.Code == "workable.execution.exception" &&
                message.Text.Contains("replayed child failed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelExecutionLifetimeAndStartANewWorkflowAfterResettingIt()
    {
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastRuns = 0;
        var services = new ServiceCollection();

        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("sample.slow"),
                async (_, _, _) =>
                {
                    slowStarted.TrySetResult();
                    await slowRelease.Task;
                    return WorkExecutionResult.Success();
                });
            builder.AddWork(
                WorkDefinition.Create("sample.fast"),
                (_, _, _) =>
                {
                    Interlocked.Increment(ref fastRuns);
                    return Task.FromResult(WorkExecutionResult.Success());
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.slow"),
                workflow => workflow.DispatchWork("dispatch", "sample.slow"));
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.fast"),
                workflow => workflow.DispatchWork("dispatch", "sample.fast"));
        });

        using var provider = services.BuildServiceProvider();
        var system = (InMemoryWorkSystem)provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        system.WorkflowRuntime.StartExecutionLifetime();
        var slowHandle = system.WorkflowRuntime.Start(
            "workflow.slow",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        await slowStarted.Task.WaitAsync(CancellationToken.None);

        system.WorkflowRuntime.CancelExecutionLifetime();
        var canceled = await slowHandle.WaitForCompletion();

        Assert.Equal(WorkflowRunStatus.Canceled, canceled.Status);

        system.WorkflowRuntime.StartExecutionLifetime();
        slowRelease.TrySetResult();
        var fastHandle = system.WorkflowRuntime.Start(
            "workflow.fast",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var completed = await fastHandle.WaitForCompletion();

        Assert.True(completed.IsCompletedSuccessfully);
        Assert.Equal(1, Volatile.Read(ref fastRuns));
    }

    [Fact]
    public async Task StopIgnoresLifecycleObserverFailuresAndStillCancelsWorkflowExecutions()
    {
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new ThrowingLifecycleObserver();
        var services = new ServiceCollection();

        services.AddSingleton<IWorkSystemLifecycleObserver>(observer);
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("sample.slow"),
                async (_, _, cancellationToken) =>
                {
                    slowStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.slow"),
                workflow => workflow.DispatchWork("dispatch", "sample.slow"));
        });

        using var provider = services.BuildServiceProvider();
        var system = (InMemoryWorkSystem)provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var handle = system.WorkflowRuntime.Start(
            "workflow.slow",
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        await slowStarted.Task.WaitAsync(CancellationToken.None);

        await system.Stop();
        var completion = await handle.WaitForCompletion();

        Assert.Equal(WorkSystemState.Stopped, system.State);
        Assert.Equal(WorkflowRunStatus.Canceled, completion.Status);
        Assert.True(observer.StoppingCalled);
        Assert.True(observer.StoppedCalled);
    }

    private static async Task StopWithTimeout(IWorkSystem system, TimeSpan timeoutAfter)
    {
        using var timeout = new CancellationTokenSource(timeoutAfter);
        await system.Stop(timeout.Token);
    }

    private static InMemoryWorkSystem GetNamedSystem(ServiceProvider provider, string name)
    {
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(registry.TryGet(name, out var system));
        return Assert.IsType<InMemoryWorkSystem>(system);
    }

    private static ServiceProvider CreateDurableParallelWorkflowProvider(
        TestWorkflowPersistenceStore store,
        Func<CancellationToken, Task> alpha,
        Func<CancellationToken, Task> beta)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IWorkPersistenceStore>(store);
        services.AddWorkableSystem("workflow-tests", builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("sample.alpha"),
                async (_, _, cancellationToken) =>
                {
                    await alpha(cancellationToken);
                    return WorkExecutionResult.Success();
                },
                configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
            builder.AddWork(
                WorkDefinition.Create("sample.beta"),
                async (_, _, cancellationToken) =>
                {
                    await beta(cancellationToken);
                    return WorkExecutionResult.Success();
                },
                configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
            builder.AddWorkflow(
                WorkflowDefinition.Create(
                    "workflow.durable.parallel",
                    coordination: WorkflowCoordinationConfiguration.Durable),
                workflow => workflow
                    .RunParallel("dispatch", parallel => parallel
                        .DispatchWork("alpha", "sample.alpha")
                        .DispatchWork("beta", "sample.beta"))
                    .Join("join"));
        });

        return services.BuildServiceProvider();
    }

    private static IReadOnlySet<string> Groups(params string[] groups)
        => new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);

    private sealed class TestWorkflowGroupProvider(IReadOnlyDictionary<string, IReadOnlySet<string>> groupsByActor)
        : IWorkAuthorizationGroupProvider
    {
        public IReadOnlySet<string> GetGroups(WorkActor actor, string? systemName)
            => actor.Id is not null && groupsByActor.TryGetValue(actor.Id, out var groups)
                ? groups
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ThrowingLifecycleObserver : IWorkSystemLifecycleObserver
    {
        public bool StoppingCalled { get; private set; }

        public bool StoppedCalled { get; private set; }

        public Task SystemStopping(
            IWorkSystem system,
            WorkOrigin origin,
            CancellationToken cancellationToken = default)
        {
            this.StoppingCalled = true;
            throw new InvalidOperationException("observer stopping failed");
        }

        public Task SystemStopped(
            IWorkSystem system,
            CancellationToken cancellationToken = default)
        {
            this.StoppedCalled = true;
            throw new InvalidOperationException("observer stopped failed");
        }
    }

    private sealed class TestWorkflowPersistenceStore : IWorkPersistenceStore
    {
        private readonly Lock sync = new();
        private readonly Queue<WorkQueueDurabilityEnqueueRequest> pending = [];
        private readonly Dictionary<WorkflowRunId, WorkflowRunPersistenceRecord> workflowRuns = [];

        public List<WorkflowPersistenceInitializationContext> WorkflowInitializations { get; } = [];

        public List<WorkflowPersistenceReadRequest> WorkflowReadRequests { get; } = [];

        public List<WorkflowPersistenceTransactionRequest> WorkflowTransactionRequests { get; } = [];

        public List<WorkQueueDurabilityEnqueueRequest> Enqueued { get; } = [];

        public List<WorkerId> DeletedFinalWorkers { get; } = [];

        public List<WorkflowRunId> DeletedWorkflowRuns { get; } = [];

        public Task Initialize(WorkQueueDurabilityInitializationContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task InitializeWorkflows(
            WorkflowPersistenceInitializationContext context,
            CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                this.WorkflowInitializations.Add(context);
            }

            return Task.CompletedTask;
        }

        public Task Enqueue(WorkQueueDurabilityEnqueueRequest request, CancellationToken cancellationToken = default)
        {
            if (request.Transaction is TestWorkflowPersistenceTransaction transaction)
            {
                transaction.Enqueued.Add(request);
                return Task.CompletedTask;
            }

            lock (this.sync)
            {
                this.EnqueueLocked(request);
            }

            return Task.CompletedTask;
        }

        public Task ReserveIdempotency(WorkIdempotencyPersistenceRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IWorkflowPersistenceTransaction> BeginWorkflowTransaction(
            WorkflowPersistenceTransactionRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                this.WorkflowTransactionRequests.Add(request);
            }

            return Task.FromResult<IWorkflowPersistenceTransaction>(new TestWorkflowPersistenceTransaction(this));
        }

        public async IAsyncEnumerable<WorkQueueDurabilityEntry> ClaimReady(
            WorkQueueDurabilityClaimRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            List<WorkQueueDurabilityEnqueueRequest> claimed = [];
            lock (this.sync)
            {
                while (this.pending.TryDequeue(out var entry))
                {
                    claimed.Add(entry);
                }
            }

            foreach (var entry in claimed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return new WorkQueueDurabilityEntry(
                    new WorkQueueDurabilityLease(entry.WorkerId, request.OwnerId, Guid.NewGuid().ToString("N")),
                    entry.Definition.Name,
                    entry.Input,
                    entry.Options,
                    entry.Configuration,
                    entry.RequestContext,
                    entry.CreatedAt);
            }

            await Task.CompletedTask;
        }

        public Task RenewLeases(
            IReadOnlyList<WorkQueueDurabilityLease> leases,
            TimeSpan leaseDuration,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RetainFailed(
            IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task DeleteFinal(
            IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
            CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                this.DeletedFinalWorkers.AddRange(workers.Select(worker => worker.WorkerId));
            }

            return Task.CompletedTask;
        }

        public Task DeleteFinal(
            IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
            IWorkQueueDurabilityTransaction transaction,
            CancellationToken cancellationToken = default)
            => this.DeleteFinal(workers, cancellationToken);

        public Task<bool> DurableWorkerExists(
            WorkerId workerId,
            CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                return Task.FromResult(
                    this.Enqueued.Any(entry => entry.WorkerId == workerId) &&
                    !this.DeletedFinalWorkers.Contains(workerId));
            }
        }

        public async IAsyncEnumerable<WorkflowRunPersistenceRecord> ListIncompleteWorkflowRuns(
            WorkflowPersistenceReadRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            List<WorkflowRunPersistenceRecord> runs;
            lock (this.sync)
            {
                this.WorkflowReadRequests.Add(request);
                runs = [.. this.workflowRuns.Values
                    .Where(run => string.Equals(run.PersistenceScope, request.PersistenceScope, StringComparison.Ordinal))
                    .OrderBy(run => run.CreatedAt)];
            }

            foreach (var run in runs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return run;
            }

            await Task.CompletedTask;
        }

        public Task UpsertWorkflowRun(
            WorkflowRunPersistenceRecord run,
            CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                this.workflowRuns[run.RunId] = run;
            }

            return Task.CompletedTask;
        }

        public Task UpsertWorkflowRun(
            WorkflowRunPersistenceRecord run,
            IWorkflowPersistenceTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            ((TestWorkflowPersistenceTransaction)transaction).WorkflowUpserts[run.RunId] = run;
            return Task.CompletedTask;
        }

        public Task DeleteWorkflowRun(
            WorkflowPersistenceDeleteRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                this.workflowRuns.Remove(request.RunId);
                this.DeletedWorkflowRuns.Add(request.RunId);
            }

            return Task.CompletedTask;
        }

        public Task DeleteWorkflowRun(
            WorkflowPersistenceDeleteRequest request,
            IWorkflowPersistenceTransaction transaction,
            CancellationToken cancellationToken = default)
        {
            ((TestWorkflowPersistenceTransaction)transaction).WorkflowDeletes.Add(request.RunId);
            return Task.CompletedTask;
        }

        public void Requeue(WorkerId workerId)
        {
            lock (this.sync)
            {
                var request = this.Enqueued.LastOrDefault(entry => entry.WorkerId == workerId)
                    ?? throw new InvalidOperationException($"Expected durable worker '{workerId.Value:D}'.");
                this.pending.Enqueue(request);
            }
        }

        private void EnqueueLocked(WorkQueueDurabilityEnqueueRequest request)
        {
            this.Enqueued.Add(request);
            this.pending.Enqueue(request);
        }

        private sealed class TestWorkflowPersistenceTransaction(TestWorkflowPersistenceStore store)
            : IWorkflowPersistenceTransaction
        {
            public List<WorkQueueDurabilityEnqueueRequest> Enqueued { get; } = [];

            public Dictionary<WorkflowRunId, WorkflowRunPersistenceRecord> WorkflowUpserts { get; } = [];

            public List<WorkflowRunId> WorkflowDeletes { get; } = [];

            public ValueTask DisposeAsync()
                => ValueTask.CompletedTask;

            public Task Commit(CancellationToken cancellationToken = default)
            {
                lock (store.sync)
                {
                    foreach (var request in this.Enqueued)
                    {
                        store.EnqueueLocked(request);
                    }

                    foreach (var upsert in this.WorkflowUpserts.Values)
                    {
                        store.workflowRuns[upsert.RunId] = upsert;
                    }

                    foreach (var runId in this.WorkflowDeletes)
                    {
                        store.workflowRuns.Remove(runId);
                        store.DeletedWorkflowRuns.Add(runId);
                    }
                }

                return Task.CompletedTask;
            }
        }
    }
}
