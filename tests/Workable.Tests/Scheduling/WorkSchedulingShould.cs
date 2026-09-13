using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Scheduling")]
public sealed class WorkSchedulingShould
{
    [Fact]
    public async Task DispatchOneTimeWorkByNameUsingTheLatestDefinition()
    {
        var store = new InMemoryScheduleStore();
        var executed = new TaskCompletionSource<(WorkConfiguration Configuration, WorkRequestContext RequestContext)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .EnableScheduling(TimeSpan.FromMinutes(5))
                .AddWork(
                    WorkDefinition.Create(
                        "scheduled.latest",
                        scheduleSecurityVersion: "latest-v1"),
                    (context, _, _) =>
                    {
                        executed.TrySetResult((context.Configuration, context.RequestContext));
                        return Task.FromResult(WorkExecutionResult.Success());
                    }))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        Assert.False(system.RequiresAuthorization);
        await system.Start();
        var actor = new WorkActor("scheduler-user", "Scheduler User");
        var session = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            actor,
            "Run later"));
        var outcome = await session.Schedules.Create(new WorkScheduleRequest(
            "scheduled.latest",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(250)),
            WorkInput.FromValue("payload")));
        var original = system.Catalog.Definitions.Single();
        var updatedConfiguration = original.Configuration with
        {
            Logging = original.Configuration.Logging with { MaximumBufferedEntries = 7 },
        };

        var changed = await system.Catalog.Reconfigure(
            original.Version,
            new WorkDefinitionReconfiguration(Configuration: updatedConfiguration));
        var execution = await executed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(outcome.IsAccepted);
        Assert.Equal(
            "latest-v1",
            (await store.Get(new(null, outcome.Schedule!.Id)))!.ExecutionGrant.DefinitionSecurityVersion);
        Assert.True(changed.IsAccepted);
        Assert.Equal(7, execution.Configuration.Logging.MaximumBufferedEntries);
        Assert.Equal(actor, execution.RequestContext.Actor);
        Assert.Null(execution.RequestContext.Authorization);
        var schedule = await session.Schedules.Get(outcome.Schedule!.Id);
        Assert.Equal(WorkScheduleStatus.Completed, schedule!.Status);
        Assert.Null(schedule.NextRunAt);
        Assert.NotNull(schedule.LastRunAt);
        var occurrence = Assert.Single((await session.Schedules.ListOccurrences(schedule.Id)).Occurrences);
        Assert.Equal(WorkScheduleOccurrenceStatus.Accepted, occurrence.Status);
        Assert.NotNull(occurrence.WorkerId);
        Assert.Equal(WorkQueueStatus.Accepted, occurrence.QueueStatus);
        Assert.InRange(occurrence.ExpiresAt - occurrence.AttemptedAt, TimeSpan.FromMinutes(4.9), TimeSpan.FromMinutes(5.1));
    }

    [Fact]
    public async Task NormalizeScheduledWorkToReturnAfterAcceptanceWithoutWaitingForCompletion()
    {
        var store = new InMemoryScheduleStore();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.background-start"),
                    async (_, _, cancellationToken) =>
                    {
                        started.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                        return WorkExecutionResult.Success();
                    },
                    configuration => configuration.ReturnAfterCompleted()))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var created = await system.Schedules.Create(new WorkScheduleRequest(
            "scheduled.background-start",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(150))));
        var occurrence = await WaitForOccurrence(store, created.Schedule!.Id);
        var worker = await system.Query.Worker(occurrence.WorkerId!.Value);

        Assert.True(created.IsAccepted);
        Assert.True(started.Task.IsCompleted);
        Assert.Equal(WorkScheduleOccurrenceStatus.Accepted, occurrence.Status);
        Assert.Equal(WorkStartPolicy.StartAndReturnAfterAccepted, worker!.Configuration.Start.Policy);
        Assert.False(release.Task.IsCompleted);
        release.TrySetResult();
    }

    [Fact]
    public async Task DispatchClaimedBatchesConcurrentlyAndDrainSubsequentBatchesImmediately()
    {
        var inner = new InMemoryScheduleStore();
        var store = new ConcurrentDispatchStartScheduleStore(inner, expectedDispatches: 2);
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .UseScheduling(WorkSystemSchedulingConfiguration.Default with
                {
                    IsEnabled = true,
                    MaximumDispatchesPerBatch = 2,
                })
                .AddWork(
                    WorkDefinition.Create("scheduled.concurrent-batch"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success())))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var runAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(150);

        var first = await system.Schedules.Create(new WorkScheduleRequest(
            "scheduled.concurrent-batch",
            WorkScheduleTiming.Once(runAt)));
        var second = await system.Schedules.Create(new WorkScheduleRequest(
            "scheduled.concurrent-batch",
            WorkScheduleTiming.Once(runAt)));
        var third = await system.Schedules.Create(new WorkScheduleRequest(
            "scheduled.concurrent-batch",
            WorkScheduleTiming.Once(runAt)));

        await store.ExpectedDispatchesStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, store.MaximumConcurrentDispatches);
        store.ContinueDispatches.TrySetResult();
        await store.SubsequentBatchStarted.Task.WaitAsync(TimeSpan.FromMilliseconds(750));
        await WaitForOccurrence(inner, first.Schedule!.Id);
        await WaitForOccurrence(inner, second.Schedule!.Id);
        await WaitForOccurrence(inner, third.Schedule!.Id);
    }

    [Fact]
    public async Task SuppressDispatchWhenTheClaimCannotBeCommitted()
    {
        var inner = new InMemoryScheduleStore();
        var store = new BlockingClaimScheduleStore(inner);
        var executions = 0;
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.stale-claim"),
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref executions);
                        return Task.FromResult(WorkExecutionResult.Success());
                    }))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var created = await system.Schedules.Create(new WorkScheduleRequest(
            "scheduled.stale-claim",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(150))));
        await store.Claimed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        inner.AllowDispatchStart = false;
        store.ContinueClaim.TrySetResult();
        await Task.Delay(TimeSpan.FromMilliseconds(250));

        Assert.True(created.IsAccepted);
        Assert.Equal(0, Volatile.Read(ref executions));
        Assert.Empty((await inner.ListOccurrences(new(null, created.Schedule!.Id))).ToArray());
    }

    [Fact]
    public async Task ContinueUnderTheAuthorizationGrantedWhenTheScheduleWasCreated()
    {
        var store = new InMemoryScheduleStore();
        var groups = new MutableGroupProvider(new HashSet<string> { "runners" });
        var executed = new TaskCompletionSource<WorkRequestContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddSingleton<IWorkAuthorizationGroupProvider>(groups)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization()
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.secure"),
                    (context, _, _) =>
                    {
                        executed.TrySetResult(context.RequestContext);
                        return Task.FromResult(WorkExecutionResult.Success());
                    },
                    configure: null,
                    authorize: authorization => authorization.AllowQueueToGroups("runners")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        Assert.True(system.RequiresAuthorization);
        await system.Start();
        var actor = new WorkActor("revoked-user");
        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            actor,
            isAuthenticated: true);
        var session = await system.CreateSession(requestContext);
        Assert.IsType<AuthorizedWorkScheduler>(session.Schedules);
        var created = await session.Schedules.Create(new WorkScheduleRequest(
            "scheduled.secure",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(250))));
        Assert.False((await store.Get(new(null, created.Schedule!.Id)))!.ExecutionGrant.AllowsFullProfileCapture);
        groups.Groups = new HashSet<string>();

        var occurrence = await WaitForOccurrence(store, created.Schedule.Id);
        var executionContext = await executed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(created.IsAccepted);
        Assert.Equal(
            WorkDefinition.DefaultScheduleSecurityVersion,
            (await store.Get(new(null, created.Schedule!.Id)))!.ExecutionGrant.DefinitionSecurityVersion);
        Assert.Equal(WorkScheduleOccurrenceStatus.Accepted, occurrence.Status);
        Assert.Equal(WorkQueueStatus.Accepted, occurrence.QueueStatus);
        Assert.Equal(actor, executionContext.Actor);
        Assert.Null(executionContext.Authorization);
        Assert.Equal(1, groups.Calls);
    }

    [Fact]
    public async Task DoNotGainFullProfileCaptureWhenTheLatestDefinitionChangesAfterCreation()
    {
        var store = new InMemoryScheduleStore();
        var executions = 0;
        var record = ScheduleRecord(
            "scheduled.profile-grant",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow),
            new WorkActor("scheduler-without-diagnostics"),
            new WorkScheduleExecutionGrant(AllowsFullProfileCapture: false));
        Assert.Equal(WorkScheduleStoreCreationStatus.Accepted, await store.Create(StoreRequest(record)));
        var configuration = WorkConfiguration.Default with
        {
            ExecutionDiagnostics = WorkExecutionDiagnosticsPersistenceConfiguration.Default with
            {
                IsEnabled = true,
                Retention = TimeSpan.FromHours(1),
                ProfileCaptureMode = WorkProfileCaptureMode.Full,
            },
        };
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddSingleton<IWorkExecutionDiagnosticsRepository>(new TestExecutionDiagnosticsRepository())
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .UseScheduling(WorkSystemSchedulingConfiguration.Default with
                {
                    IsEnabled = true,
                    MaximumOccurrencePayloadBytes = 2,
                    MaximumRetainedOccurrencePayloadBytes = 2,
                })
                .AddWork(
                    WorkDefinition.Create(
                        "scheduled.profile-grant",
                        configuration: configuration),
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref executions);
                        return Task.FromResult(WorkExecutionResult.Success());
                    }))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var occurrence = await WaitForOccurrence(store, record.Schedule.Id);

        Assert.Equal(0, Volatile.Read(ref executions));
        Assert.Equal(WorkScheduleOccurrenceStatus.Rejected, occurrence.Status);
        Assert.Equal(WorkQueueStatus.Unauthorized, occurrence.QueueStatus);
        Assert.Empty(occurrence.Messages);
    }

    [Fact]
    public async Task RejectScheduleCreationWhenDefinitionScopedFullCaptureIsHiddenByRuntimeOverrides()
    {
        var store = new InMemoryScheduleStore();
        var groups = new MutableGroupProvider(new HashSet<string> { "runners" });
        var configuration = WorkConfiguration.Default with
        {
            ExecutionDiagnostics = WorkExecutionDiagnosticsPersistenceConfiguration.Default with
            {
                IsEnabled = true,
                ProfileCaptureMode = WorkProfileCaptureMode.Full,
            },
        };
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddSingleton<IWorkExecutionDiagnosticsRepository>(new TestExecutionDiagnosticsRepository())
            .AddSingleton<IWorkAuthorizationGroupProvider>(groups)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization()
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.definition-full", configuration: configuration),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configure: null,
                    authorize: authorization => authorization.AllowQueueToGroups("runners")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var session = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("runner"),
            isAuthenticated: true));

        var outcome = await session.Schedules.Create(new WorkScheduleRequest(
            "scheduled.definition-full",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow + TimeSpan.FromHours(1)),
            WorkerOptions: new WorkerOptions(WorkConfiguration.Default)));

        Assert.Equal(WorkScheduleCreationStatus.Unauthorized, outcome.Status);
        Assert.Empty((await store.List(new(null))).ToArray());
    }

    [Fact]
    public async Task RejectDispatchWhenTheDefinitionSecurityContractChanges()
    {
        var store = new InMemoryScheduleStore();
        var executions = 0;
        var record = ScheduleRecord(
            "scheduled.security-contract",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow),
            new WorkActor("scheduler"),
            new WorkScheduleExecutionGrant(true, "contract-v1"));
        Assert.Equal(WorkScheduleStoreCreationStatus.Accepted, await store.Create(StoreRequest(record)));
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create(
                        "scheduled.security-contract",
                        scheduleSecurityVersion: "contract-v2"),
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref executions);
                        return Task.FromResult(WorkExecutionResult.Success());
                    }))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var occurrence = await WaitForOccurrence(store, record.Schedule.Id);

        Assert.Equal(0, Volatile.Read(ref executions));
        Assert.Equal(WorkScheduleOccurrenceStatus.Rejected, occurrence.Status);
        Assert.Equal(WorkQueueStatus.Invalid, occurrence.QueueStatus);
        Assert.Contains(occurrence.Messages, message =>
            message.Code == "workable.schedule.definition_security_version_changed");
    }

    [Fact]
    public async Task AllowAnAuthorizedCancelerToCancelAnotherActorsSchedule()
    {
        var store = new InMemoryScheduleStore();
        var groups = new MutableGroupProvider(new HashSet<string> { "runners" });
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddSingleton<IWorkAuthorizationGroupProvider>(groups)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization()
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.cancel"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configure: null,
                    authorize: authorization => authorization
                        .AllowQueueToGroups("runners")
                        .AllowOperationsToGroups(
                            ["cancelers"],
                            WorkOperationPermissions.Cancel,
                            requirements => requirements.WhenScheduleActionsRequire(context =>
                                context.ScheduleId is not null &&
                                context.Action == WorkOperateAction.Cancel &&
                                context.RequestContext.Actor.Id == "canceler"))))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        Assert.True(system.RequiresAuthorization);
        await system.Start();
        var creator = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("creator"),
            isAuthenticated: true));
        var created = await creator.Schedules.Create(new WorkScheduleRequest(
            "scheduled.cancel",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow + TimeSpan.FromHours(1))));

        var rejected = await creator.Schedules.Cancel(created.Schedule!.Id);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => creator.Schedules.List(
            new WorkScheduleCriteria(Take: WorkScheduleCriteria.MaximumTake + 1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => creator.Schedules.ListOccurrences(
            created.Schedule.Id,
            WorkScheduleOccurrenceReadRequest.MaximumTake + 1));
        groups.Groups = new HashSet<string> { "cancelers" };
        var constrained = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("not-the-canceler"),
            isAuthenticated: true));
        var constraintRejected = await constrained.Schedules.Cancel(created.Schedule.Id);
        var canceler = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("canceler"),
            isAuthenticated: true));
        var accepted = await canceler.Schedules.Cancel(created.Schedule.Id);

        Assert.Equal(WorkScheduleCancellationStatus.Unauthorized, rejected.Status);
        Assert.Equal(WorkScheduleCancellationStatus.Unauthorized, constraintRejected.Status);
        Assert.True(accepted.IsAccepted);
        Assert.Null(accepted.Schedule);
        var persisted = await store.Get(new(null, created.Schedule.Id));
        Assert.Equal(WorkScheduleStatus.Canceled, persisted!.Schedule.Status);
        Assert.Equal("canceler", persisted.Schedule.CanceledBy!.Id);
    }

    [Fact]
    public async Task DoNotBroadenWorkerConstrainedCancelGrantsToSchedules()
    {
        var store = new InMemoryScheduleStore();
        var groups = new MutableGroupProvider(new HashSet<string> { "runners" });
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddSingleton<IWorkAuthorizationGroupProvider>(groups)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization()
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.worker-constrained-cancel"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configure: null,
                    authorize: authorization => authorization
                        .AllowQueueToGroups("runners")
                        .AllowOperationsToGroups(
                            ["cancelers"],
                            WorkOperationPermissions.Cancel,
                            requirements => requirements.WhenWorkerActionsRequire(_ => true))))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var creator = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("creator"),
            isAuthenticated: true));
        var created = await creator.Schedules.Create(new WorkScheduleRequest(
            "scheduled.worker-constrained-cancel",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow + TimeSpan.FromHours(1))));
        groups.Groups = new HashSet<string> { "cancelers" };
        var canceler = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("canceler"),
            isAuthenticated: true));

        var outcome = await canceler.Schedules.Cancel(created.Schedule!.Id);

        Assert.Equal(WorkScheduleCancellationStatus.Unauthorized, outcome.Status);
        Assert.Equal(
            WorkScheduleStatus.Active,
            (await store.Get(new(null, created.Schedule.Id)))!.Schedule.Status);
    }

    [Fact]
    public async Task ReturnInvalidWhenTypedScheduleCancelInputCannotBeDeserialized()
    {
        var store = new InMemoryScheduleStore();
        var groups = new MutableGroupProvider(new HashSet<string> { "runners" });
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddSingleton<IWorkAuthorizationGroupProvider>(groups)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization()
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.invalid-cancel-input"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configure: null,
                    authorize: authorization => authorization
                        .AllowQueueToGroups("runners")
                        .AllowOperationsToGroups(
                            ["cancelers"],
                            WorkOperationPermissions.Cancel,
                            requirements => requirements.WhenScheduleActionsRequire<ScheduleAuthorizationInput>(
                                context => context.Input?.Value == "allowed"))))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var creator = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("creator"),
            isAuthenticated: true));
        var created = await creator.Schedules.Create(new WorkScheduleRequest(
            "scheduled.invalid-cancel-input",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow + TimeSpan.FromHours(1)),
            WorkInput.FromValue("not-an-object")));
        groups.Groups = new HashSet<string> { "cancelers" };
        var canceler = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("canceler"),
            isAuthenticated: true));

        var outcome = await canceler.Schedules.Cancel(created.Schedule!.Id);

        Assert.Equal(WorkScheduleCancellationStatus.Invalid, outcome.Status);
        Assert.Null(outcome.Schedule);
        Assert.Contains(outcome.Messages, message =>
            message.Code == "workable.authorization.operate_requirement_input_invalid" &&
            message.Target == "schedule.input");
        Assert.Equal(
            WorkScheduleStatus.Active,
            (await store.Get(new(null, created.Schedule.Id)))!.Schedule.Status);
    }

    [Fact]
    public async Task RequireExplicitSystemReadPermissionForOrphanedScheduleData()
    {
        var store = new InMemoryScheduleStore();
        var actor = new WorkActor("original-scheduler");
        var record = ScheduleRecord(
            "removed.definition",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow + TimeSpan.FromHours(1)),
            actor,
            WorkScheduleExecutionGrant.Unrestricted);
        Assert.Equal(WorkScheduleStoreCreationStatus.Accepted, await store.Create(StoreRequest(record)));
        var groups = new MutableGroupProvider(new HashSet<string> { "system-operators" });
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddSingleton<IWorkAuthorizationGroupProvider>(groups)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization()
                .ConfigureAuthorization(authorization => authorization
                    .AllowOperateAllWorkToGroups("system-operators")
                    .AllowReadAllWorkToGroups("system-readers"))
                .EnableScheduling())
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var session = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("system-operator"),
            isAuthenticated: true));

        var get = await session.Schedules.Get(record.Schedule.Id);
        var list = await session.Schedules.List();
        var canceled = await session.Schedules.Cancel(record.Schedule.Id);
        groups.Groups = new HashSet<string> { "system-readers" };
        var reader = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("system-reader"),
            isAuthenticated: true));
        var readableAfterCancel = await reader.Schedules.Get(record.Schedule.Id);

        Assert.Null(get);
        Assert.Empty(list.Schedules);
        Assert.True(canceled.IsAccepted);
        Assert.Null(canceled.Schedule);
        Assert.Equal(actor, readableAfterCancel!.CreatedBy);
        Assert.Equal(
            WorkScheduleStatus.Canceled,
            (await store.Get(new(null, record.Schedule.Id)))!.Schedule.Status);
    }

    [Fact]
    public async Task DisableWorkerRecurrenceForRecurringSchedulesEvenAfterDefinitionReconfiguration()
    {
        var store = new InMemoryScheduleStore();
        var executions = new List<(WorkerId WorkerId, bool RecurrenceEnabled)>();
        var executionSignal = new SemaphoreSlim(0);
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .UseScheduling(new WorkSystemSchedulingConfiguration
                {
                    IsEnabled = true,
                    MinimumInterval = TimeSpan.FromMilliseconds(50),
                })
                .AddWork(
                    WorkDefinition.Create("scheduled.recurring"),
                    (context, _, _) =>
                    {
                        lock (executions)
                        {
                            executions.Add((context.WorkerId, context.Configuration.Recurrence.IsEnabled));
                        }

                        executionSignal.Release();
                        return Task.FromResult(WorkExecutionResult.Success());
                    }))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var created = await system.Schedules.Create(new WorkScheduleRequest(
            "scheduled.recurring",
            WorkScheduleTiming.Every(
                TimeSpan.FromMilliseconds(150),
                DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(200))));
        var definition = system.Catalog.Definitions.Single();
        var reconfigured = await system.Catalog.Reconfigure(
            definition.Version,
            new WorkDefinitionReconfiguration(
                Configuration: definition.Configuration with
                {
                    Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMilliseconds(10)),
                }));

        Assert.True(created.IsAccepted);
        Assert.True(reconfigured.IsAccepted);
        Assert.True(await executionSignal.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(await executionSignal.WaitAsync(TimeSpan.FromSeconds(5)));
        await system.Schedules.Cancel(created.Schedule!.Id);
        lock (executions)
        {
            Assert.True(executions.Count >= 2);
            Assert.NotEqual(executions[0].WorkerId, executions[1].WorkerId);
            Assert.All(executions, execution => Assert.False(execution.RecurrenceEnabled));
        }
    }

    [Fact]
    public async Task RespectMissedExecutionPolicyAcrossSystemDowntime()
    {
        var store = new InMemoryScheduleStore();
        var executions = 0;
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.missed"),
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref executions);
                        return Task.FromResult(WorkExecutionResult.Success());
                    }))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dueAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(300);
        var retry = await system.Schedules.Create(new WorkScheduleRequest(
            "scheduled.missed",
            WorkScheduleTiming.Once(dueAt, runMissedExecution: true)));
        var skip = await system.Schedules.Create(new WorkScheduleRequest(
            "scheduled.missed",
            WorkScheduleTiming.Once(dueAt, runMissedExecution: false)));
        await system.Stop();
        await Task.Delay(TimeSpan.FromMilliseconds(450));
        await system.Start();

        var retriedOccurrence = await WaitForOccurrence(store, retry.Schedule!.Id);
        var skippedOccurrence = await WaitForOccurrence(store, skip.Schedule!.Id);

        Assert.Equal(WorkScheduleOccurrenceStatus.Accepted, retriedOccurrence.Status);
        Assert.Equal(WorkScheduleOccurrenceStatus.Skipped, skippedOccurrence.Status);
        Assert.Equal(1, Volatile.Read(ref executions));
    }

    [Fact]
    public async Task BoundBestEffortHostPresenceCleanupDuringShutdown()
    {
        var store = new InMemoryScheduleStore { BlockEndHost = true };
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.shutdown"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success())))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        await system.Stop().WaitAsync(TimeSpan.FromSeconds(3));

        await store.EndHostStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await store.EndHostCancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ContinueScheduleListsWithoutDuplicatesAcrossEqualCreationTimes()
    {
        var store = new InMemoryScheduleStore();
        var createdAt = DateTimeOffset.UtcNow;
        var ids = new[]
        {
            new WorkScheduleId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            new WorkScheduleId(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            new WorkScheduleId(Guid.Parse("33333333-3333-3333-3333-333333333333")),
        };
        foreach (var id in ids)
        {
            var record = ScheduleRecord(
                "scheduled.pagination",
                WorkScheduleTiming.Once(createdAt + TimeSpan.FromHours(1)),
                new WorkActor("scheduler"),
                WorkScheduleExecutionGrant.Unrestricted);
            record = record with
            {
                Schedule = record.Schedule with
                {
                    Id = id,
                    CreatedAt = createdAt,
                },
            };
            Assert.Equal(WorkScheduleStoreCreationStatus.Accepted, await store.Create(StoreRequest(record)));
        }

        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.pagination"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success())))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var first = await system.Schedules.List(new WorkScheduleCriteria(Take: 2));
        var second = await system.Schedules.List(new WorkScheduleCriteria(Take: 2, Cursor: first.Cursor));

        Assert.Equal(2, first.Schedules.Count);
        Assert.NotNull(first.Cursor);
        Assert.Single(second.Schedules);
        Assert.Null(second.Cursor);
        Assert.Equal(ids.OrderBy(id => id.Value), first.Schedules.Concat(second.Schedules).Select(schedule => schedule.Id).OrderBy(id => id.Value));

        Assert.NotNull(await store.Cancel(new(null, ids[2], DateTimeOffset.UtcNow, new WorkActor("canceler"))));
        var overview = await system.Schedules.GetOverview(new(ids[2], RecentScheduleTake: 1, OccurrenceTake: 10));
        Assert.Single(overview.Recent.Schedules);
        Assert.Equal(2, overview.Upcoming.Schedules.Count);
        Assert.Equal(ids[2], overview.SelectedSchedule!.Id);
        Assert.Equal(WorkScheduleStatus.Canceled, overview.SelectedSchedule.Status);
        Assert.Empty(overview.Occurrences);
    }

    [Fact]
    public async Task PageTheUpcomingScheduleOverview()
    {
        var store = new InMemoryScheduleStore();
        var createdAt = DateTimeOffset.UtcNow;
        for (var index = 0; index <= 100; index++)
        {
            var record = ScheduleRecord(
                "scheduled.overview-limit",
                WorkScheduleTiming.Once(createdAt + TimeSpan.FromHours(1)),
                new WorkActor("scheduler"),
                WorkScheduleExecutionGrant.Unrestricted);
            record = record with
            {
                Schedule = record.Schedule with
                {
                    CreatedAt = createdAt - TimeSpan.FromTicks(index),
                },
            };
            Assert.Equal(
                WorkScheduleStoreCreationStatus.Accepted,
                await store.Create(LargeStoreRequest(record)));
        }

        var overview = await ((IWorkScheduleStore)store).GetOverview(new(
            WorkSystemName: null,
            RecentScheduleTake: 1,
            UpcomingScheduleTake: 100,
            OccurrenceTake: 1));

        Assert.Equal(100, overview.Upcoming.Schedules.Count);
        Assert.Equal(101, overview.Upcoming.ActiveScheduleCount);
        Assert.Equal(101, overview.Upcoming.UpcomingScheduleCount);
        Assert.NotNull(overview.Upcoming.Cursor);
        Assert.NotNull(overview.Recent.Cursor);
        var anchoredOverview = await ((IWorkScheduleStore)store).GetOverview(new(
            WorkSystemName: null,
            RecentScheduleTake: 100,
            UpcomingScheduleTake: 100,
            OccurrenceTake: 1,
            RecentCursor: overview.Recent.Cursor,
            UpcomingCursor: overview.Upcoming.Cursor));
        Assert.Equal(100, anchoredOverview.Recent.Schedules.Count);
        Assert.Single(anchoredOverview.Upcoming.Schedules);
        Assert.DoesNotContain(
            anchoredOverview.Recent.Schedules,
            schedule => overview.Recent.Schedules.Any(first => first.Id == schedule.Id));
        Assert.DoesNotContain(
            anchoredOverview.Upcoming.Schedules,
            schedule => overview.Upcoming.Schedules.Any(first => first.Id == schedule.Id));
        var next = await ((IWorkScheduleStore)store).ListUpcoming(new(
            WorkSystemName: null,
            Take: 100,
            Cursor: overview.Upcoming.Cursor));
        Assert.Single(next.Schedules);
        Assert.Equal(101, next.ActiveScheduleCount);
    }

    [Fact]
    public async Task BoundTheDefaultUpcomingProviderFallback()
    {
        var store = new InMemoryScheduleStore();
        var firstRunAt = DateTimeOffset.UtcNow + TimeSpan.FromDays(1);
        for (var index = 0; index < WorkScheduleStoreUpcomingRequest.MaximumDefaultScanCount; index++)
        {
            var record = ScheduleRecord(
                "scheduled.default-management-bound",
                WorkScheduleTiming.Once(firstRunAt),
                new WorkActor($"scheduler-{index}"),
                WorkScheduleExecutionGrant.Unrestricted);
            Assert.Equal(
                WorkScheduleStoreCreationStatus.Accepted,
                await store.Create(LargeStoreRequest(record)));
        }

        var maximumResult = await ((IWorkScheduleStore)store).ListUpcoming(new(
            WorkSystemName: null,
            Take: WorkScheduleStoreUpcomingRequest.MaximumDefaultScanCount));
        Assert.Equal(WorkScheduleStoreUpcomingRequest.MaximumDefaultScanCount, maximumResult.Schedules.Count);
        Assert.Equal(WorkScheduleStoreUpcomingRequest.MaximumDefaultScanCount, maximumResult.ActiveScheduleCount);

        var overflowRecord = ScheduleRecord(
            "scheduled.default-management-bound",
            WorkScheduleTiming.Once(firstRunAt),
            new WorkActor("overflow-scheduler"),
            WorkScheduleExecutionGrant.Unrestricted);
        Assert.Equal(
            WorkScheduleStoreCreationStatus.Accepted,
            await store.Create(LargeStoreRequest(overflowRecord)));

        var exception = await Assert.ThrowsAsync<NotSupportedException>(() =>
            ((IWorkScheduleStore)store).ListUpcoming(new(WorkSystemName: null)));

        Assert.Contains(
            $"at most {WorkScheduleStoreUpcomingRequest.MaximumDefaultScanCount}",
            exception.Message,
            StringComparison.Ordinal);
        Assert.Contains("Override IWorkScheduleStore.ListUpcoming", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DoNotTreatAHealthyClusterAsDownWhenANewHostClaimsOverdueWork()
    {
        var store = new InMemoryScheduleStore();
        var executions = 0;
        static void Configure(IWorkSystemBuilder builder, Action execute)
            => builder
                .RequireAuthorization(false)
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.rolling-host"),
                    (_, _, _) =>
                    {
                        execute();
                        return Task.FromResult(WorkExecutionResult.Success());
                    });

        await using var existingProvider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(new NonClaimingScheduleStore(store))
            .AddWorkableSystem(builder => Configure(builder, () => Interlocked.Increment(ref executions)))
            .BuildServiceProvider();
        await using var newProvider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => Configure(builder, () => Interlocked.Increment(ref executions)))
            .BuildServiceProvider();
        var existingSystem = existingProvider.GetRequiredService<IWorkSystemRegistry>().Default;
        var newSystem = newProvider.GetRequiredService<IWorkSystemRegistry>().Default;
        await existingSystem.Start();
        var dueAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(250);
        var created = await existingSystem.Schedules.Create(new(
            "scheduled.rolling-host",
            WorkScheduleTiming.Once(dueAt, runMissedExecution: false)));
        await Task.Delay(TimeSpan.FromMilliseconds(350));

        await newSystem.Start();

        var occurrence = await WaitForOccurrence(store, created.Schedule!.Id);
        Assert.Equal(WorkScheduleOccurrenceStatus.Accepted, occurrence.Status);
        Assert.Equal(1, Volatile.Read(ref executions));
    }

    [Fact]
    public async Task TreatWorkThatBecomesDueDuringStartupAsMissed()
    {
        var scheduleStore = new InMemoryScheduleStore();
        var persistenceStore = new BlockingInitializationPersistenceStore();
        var executions = 0;
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(scheduleStore)
            .AddSingleton<IWorkPersistenceStore>(persistenceStore)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.startup-missed"),
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref executions);
                        return Task.FromResult(WorkExecutionResult.Success());
                    }))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var dueAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(300);
        var created = await system.Schedules.Create(new(
            "scheduled.startup-missed",
            WorkScheduleTiming.Once(dueAt, runMissedExecution: false)));
        await system.Stop();
        persistenceStore.BlockNextInitialization();

        var restarting = system.Start();
        await persistenceStore.InitializationBlocked.WaitAsync(TimeSpan.FromSeconds(5));
        var remaining = dueAt - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(100);
        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining);
        }

        persistenceStore.ReleaseInitialization();
        await restarting;

        var occurrence = await WaitForOccurrence(scheduleStore, created.Schedule!.Id);
        Assert.Equal(WorkScheduleOccurrenceStatus.Skipped, occurrence.Status);
        Assert.Equal(0, Volatile.Read(ref executions));
    }

    [Fact]
    public async Task DispatchCronWorkAndAdvanceToOneFutureOccurrence()
    {
        var store = new InMemoryScheduleStore();
        var executions = 0;
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.cron"),
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref executions);
                        return Task.FromResult(WorkExecutionResult.Success());
                    }))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var created = await system.Schedules.Create(new WorkScheduleRequest(
            "scheduled.cron",
            WorkScheduleTiming.Cron(
                "* * * * *",
                startsAt: DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2))));

        var occurrence = await WaitForOccurrence(store, created.Schedule!.Id);
        var current = await system.Schedules.Get(created.Schedule.Id);

        Assert.True(created.IsAccepted);
        Assert.True(created.Schedule.Timing.IsCron);
        Assert.True(created.Schedule.Timing.IsRecurring);
        Assert.Equal(WorkScheduleOccurrenceStatus.Accepted, occurrence.Status);
        Assert.Equal(1, Volatile.Read(ref executions));
        Assert.Equal(WorkScheduleStatus.Active, current!.Status);
        Assert.True(current.NextRunAt > DateTimeOffset.UtcNow);
        await system.Schedules.Cancel(created.Schedule.Id);
    }

    [Fact]
    public async Task RejectInvalidAndUnavailableSchedulesAndStaticRecurrenceConflicts()
    {
        var unavailableProvider = new ServiceCollection()
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .AddWork(
                    WorkDefinition.Create("plain"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success())))
            .BuildServiceProvider();
        await using (unavailableProvider)
        {
            var unavailableSystem = unavailableProvider.GetRequiredService<IWorkSystemRegistry>().Default;
            await unavailableSystem.Start();
            var unavailable = await unavailableSystem.Schedules.Create(new WorkScheduleRequest(
                "plain",
                WorkScheduleTiming.Once(DateTimeOffset.UtcNow)));
            Assert.Equal(WorkScheduleCreationStatus.Unavailable, unavailable.Status);
            var overview = await unavailableSystem.Schedules.GetOverview();
            Assert.Empty(overview.Recent.Schedules);
            Assert.Empty(overview.Upcoming.Schedules);
            Assert.Null(overview.SelectedSchedule);
            Assert.Empty(overview.Occurrences);
        }

        var store = new InMemoryScheduleStore();
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create(
                        "static.recurring",
                        configuration: WorkConfiguration.Default with
                        {
                            Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(1)),
                        }),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success())))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => system.Schedules.GetOverview(
            new(RecentScheduleTake: 0)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => system.Schedules.GetOverview(
            new(UpcomingScheduleTake: 0)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => system.Schedules.ListUpcoming(0));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => system.Schedules.GetOverview(
            new(OccurrenceTake: WorkScheduleOccurrenceReadRequest.MaximumTake + 1)));

        var blank = await system.Schedules.Create(new WorkScheduleRequest(
            " ",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow)));
        var missing = await system.Schedules.Create(new WorkScheduleRequest(
            "missing",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow)));
        var interval = await system.Schedules.Create(new WorkScheduleRequest(
            "static.recurring",
            new WorkScheduleTiming(DateTimeOffset.UtcNow, TimeSpan.Zero)));
        var conflict = await system.Schedules.Create(new WorkScheduleRequest(
            "static.recurring",
            WorkScheduleTiming.Every(TimeSpan.FromMinutes(1))));
        var cronConflict = await system.Schedules.Create(new WorkScheduleRequest(
            "static.recurring",
            WorkScheduleTiming.Cron("0 9 * * 1-5")));
        var ambiguous = await system.Schedules.Create(new WorkScheduleRequest(
            "static.recurring",
            new WorkScheduleTiming(
                DateTimeOffset.UtcNow,
                TimeSpan.FromMinutes(1),
                CronExpression: "0 9 * * *",
                TimeZoneId: "UTC")));
        var invalidCron = await system.Schedules.Create(new WorkScheduleRequest(
            "static.recurring",
            WorkScheduleTiming.Cron("not cron")));
        var oversizedCron = await system.Schedules.Create(new WorkScheduleRequest(
            "static.recurring",
            WorkScheduleTiming.Cron(new string('*', 257))));
        var oversizedTimeZone = await system.Schedules.Create(new WorkScheduleRequest(
            "static.recurring",
            WorkScheduleTiming.Cron("0 9 * * *", new string('z', 257))));
        var missingTimeZone = await system.Schedules.Create(new WorkScheduleRequest(
            "static.recurring",
            WorkScheduleTiming.Cron("0 9 * * *", string.Empty)));
        var unknownTimeZone = await system.Schedules.Create(new WorkScheduleRequest(
            "static.recurring",
            WorkScheduleTiming.Cron("0 9 * * *", "Mars/Olympus_Mons")));
        var timeZoneWithoutCron = await system.Schedules.Create(new WorkScheduleRequest(
            "static.recurring",
            new WorkScheduleTiming(DateTimeOffset.UtcNow, TimeZoneId: "UTC")));
        var impossibleCron = await system.Schedules.Create(new WorkScheduleRequest(
            "static.recurring",
            WorkScheduleTiming.Cron("0 0 31 2 *")));
        var once = await system.Schedules.Create(new WorkScheduleRequest(
            "static.recurring",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow + TimeSpan.FromHours(1))));

        Assert.Equal(WorkScheduleCreationStatus.Invalid, blank.Status);
        Assert.Equal(WorkScheduleCreationStatus.Invalid, missing.Status);
        Assert.Equal(WorkScheduleCreationStatus.Invalid, interval.Status);
        Assert.Equal(WorkScheduleCreationStatus.Invalid, conflict.Status);
        Assert.Equal(WorkScheduleCreationStatus.Invalid, cronConflict.Status);
        Assert.Contains(cronConflict.Messages, message => message.Code == "workable.schedule.static_recurrence_conflict");
        Assert.Equal(WorkScheduleCreationStatus.Invalid, ambiguous.Status);
        Assert.Contains(ambiguous.Messages, message => message.Code == "workable.schedule.recurrence_ambiguous");
        Assert.Equal(WorkScheduleCreationStatus.Invalid, invalidCron.Status);
        Assert.Contains(invalidCron.Messages, message => message.Code == "workable.schedule.cron_invalid");
        Assert.Contains(oversizedCron.Messages, message => message.Code == "workable.schedule.cron_too_long");
        Assert.Contains(oversizedTimeZone.Messages, message => message.Code == "workable.schedule.time_zone_too_long");
        Assert.Equal(WorkScheduleCreationStatus.Invalid, missingTimeZone.Status);
        Assert.Contains(missingTimeZone.Messages, message => message.Target == "timing.timeZoneId");
        Assert.Equal(WorkScheduleCreationStatus.Invalid, unknownTimeZone.Status);
        Assert.Contains(unknownTimeZone.Messages, message => message.Text.Contains("Mars/Olympus_Mons", StringComparison.Ordinal));
        Assert.Equal(WorkScheduleCreationStatus.Invalid, timeZoneWithoutCron.Status);
        Assert.Contains(timeZoneWithoutCron.Messages, message => message.Code == "workable.schedule.time_zone_without_cron");
        Assert.Equal(WorkScheduleCreationStatus.Invalid, impossibleCron.Status);
        Assert.Contains(impossibleCron.Messages, message => message.Code == "workable.schedule.cron_no_occurrence");
        Assert.True(once.IsAccepted);
        await system.Stop();
        var stopped = await system.Schedules.Create(new WorkScheduleRequest(
            "static.recurring",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow)));
        Assert.Equal(WorkScheduleCreationStatus.Invalid, stopped.Status);
    }

    [Fact]
    public async Task ApplyNormalFailedWorkerPoliciesWithoutStoppingARecurringSchedule()
    {
        var store = new InMemoryScheduleStore();
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .UseScheduling(new WorkSystemSchedulingConfiguration
                {
                    IsEnabled = true,
                    MinimumInterval = TimeSpan.FromMilliseconds(50),
                })
                .AddWork(
                    WorkDefinition.Create("scheduled.failure.manual"),
                    (_, _, _) => Task.FromResult(ScheduledFailure()))
                .AddWork(
                    WorkDefinition.Create("scheduled.failure.auto-cancel"),
                    (_, _, _) => Task.FromResult(ScheduledFailure()),
                    configuration => configuration.AutoCancelFailedWorkersAfter(TimeSpan.FromMilliseconds(25)))
                .AddWork(
                    WorkDefinition.Create("scheduled.failure.recurring"),
                    (_, _, _) => Task.FromResult(ScheduledFailure())))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var firstRunAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(150);
        var manual = await system.Schedules.Create(new(
            "scheduled.failure.manual",
            WorkScheduleTiming.Once(firstRunAt)));
        var autoCancel = await system.Schedules.Create(new(
            "scheduled.failure.auto-cancel",
            WorkScheduleTiming.Once(firstRunAt)));
        var recurring = await system.Schedules.Create(new(
            "scheduled.failure.recurring",
            WorkScheduleTiming.Every(TimeSpan.FromMilliseconds(150), firstRunAt)));

        var manualOccurrence = await WaitForOccurrence(store, manual.Schedule!.Id);
        var autoCancelOccurrence = await WaitForOccurrence(store, autoCancel.Schedule!.Id);
        var recurringOccurrence = await WaitForOccurrence(store, recurring.Schedule!.Id);
        await WaitForWorkerState(system, manualOccurrence.WorkerId!.Value, WorkerState.Failed);
        await WaitForWorkerState(system, autoCancelOccurrence.WorkerId!.Value, WorkerState.Canceled);
        await WaitForWorkerState(system, recurringOccurrence.WorkerId!.Value, WorkerState.Failed);
        await TestEventually.Until(
            async () => (await store.ListOccurrences(new(null, recurring.Schedule.Id))).Count >= 2,
            "Expected the recurring schedule to dispatch again after a failed worker.");

        var active = await system.Schedules.Get(recurring.Schedule.Id);

        Assert.Equal(WorkScheduleStatus.Active, active!.Status);
        Assert.Equal(WorkerState.Failed, (await system.Query.Worker(manualOccurrence.WorkerId.Value))!.State);
        Assert.Equal(WorkerState.Canceled, (await system.Query.Worker(autoCancelOccurrence.WorkerId.Value))!.State);
        await system.Schedules.Cancel(recurring.Schedule.Id);
    }

    [Fact]
    public async Task EvaluateInputAndOptionsQueueRequirementsOnlyWhenCreatingTheSchedule()
    {
        var store = new InMemoryScheduleStore();
        var allowDispatch = 1;
        var authorizationChecks = 0;
        var executions = 0;
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddSingleton<IWorkAuthorizationGroupProvider>(
                new MutableGroupProvider(new HashSet<string> { "runners" }))
            .AddWorkableSystem(builder => builder
                .RequireAuthorization()
                .EnableScheduling()
                .AddWork<ScheduleAuthorizationInput>(
                    WorkDefinition.Create("scheduled.requirements"),
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref executions);
                        return Task.FromResult(WorkExecutionResult.Success());
                    },
                    configure: null,
                    authorize: authorization => authorization.AllowQueueToGroups(
                        ["runners"],
                        requirements => requirements.WhenQueueingRequire<ScheduleAuthorizationInput>(context =>
                        {
                            Interlocked.Increment(ref authorizationChecks);
                            return Volatile.Read(ref allowDispatch) == 1 &&
                                context.Input?.Value == "allowed" &&
                                context.Options is { HasExplicitProfilingEnabled: true, ProfilingEnabled: true } &&
                                context.Options.Configuration?.Start.Policy ==
                                    WorkStartPolicy.StartAndReturnAfterAccepted;
                        }))))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var session = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("requirements-user"),
            isAuthenticated: true));
        var rejectedAtCreation = await session.Schedules.Create(
            "scheduled.requirements",
            new ScheduleAuthorizationInput("denied"),
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow + TimeSpan.FromHours(1)),
            new WorkerOptions(ProfilingEnabled: true));
        var accepted = await session.Schedules.Create(
            "scheduled.requirements",
            new ScheduleAuthorizationInput("allowed"),
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(150)),
            new WorkerOptions(ProfilingEnabled: true));
        Volatile.Write(ref allowDispatch, 0);

        var occurrence = await WaitForOccurrence(store, accepted.Schedule!.Id);
        await TestEventually.Until(() => Volatile.Read(ref executions) == 1);

        Assert.Equal(WorkScheduleCreationStatus.Unauthorized, rejectedAtCreation.Status);
        Assert.True(accepted.IsAccepted);
        Assert.Equal(WorkScheduleOccurrenceStatus.Accepted, occurrence.Status);
        Assert.Equal(WorkQueueStatus.Accepted, occurrence.QueueStatus);
        Assert.Equal(1, Volatile.Read(ref executions));
        Assert.Equal(2, Volatile.Read(ref authorizationChecks));
    }

    [Fact]
    public async Task FilterScheduleAndOccurrenceReadsByDefinitionAuthorization()
    {
        var store = new InMemoryScheduleStore();
        var groups = new MutableGroupProvider(new HashSet<string> { "creators" });
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddSingleton<IWorkAuthorizationGroupProvider>(groups)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization()
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.visible"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configure: null,
                    authorize: authorization => authorization
                        .AllowQueueToGroups("creators")
                        .AllowReadToGroups("readers"))
                .AddWork(
                    WorkDefinition.Create("scheduled.hidden"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configure: null,
                    authorize: authorization => authorization
                        .AllowQueueToGroups("creators")
                        .AllowReadToGroups("hidden-readers")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var creator = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("creator"),
            isAuthenticated: true));
        var firstRunAt = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(150);
        var visible = await creator.Schedules.Create(new(
            "scheduled.visible",
            WorkScheduleTiming.Once(firstRunAt)));
        var hidden = await creator.Schedules.Create(new(
            "scheduled.hidden",
            WorkScheduleTiming.Once(firstRunAt)));
        await WaitForOccurrence(store, visible.Schedule!.Id);
        await WaitForOccurrence(store, hidden.Schedule!.Id);

        groups.Groups = new HashSet<string> { "readers" };
        var reader = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("reader"),
            isAuthenticated: true));
        var schedules = await reader.Schedules.List();
        var hiddenByName = await reader.Schedules.List(new(DefinitionName: "scheduled.hidden"));
        var overview = await reader.Schedules.GetOverview(new(
            hidden.Schedule.Id,
            RecentScheduleTake: 1,
            OccurrenceTake: 10));

        Assert.Equal("scheduled.visible", Assert.Single(schedules.Schedules).DefinitionName);
        Assert.Empty(hiddenByName.Schedules);
        Assert.NotNull(await reader.Schedules.Get(visible.Schedule.Id));
        Assert.Null(await reader.Schedules.Get(hidden.Schedule.Id));
        Assert.Single((await reader.Schedules.ListOccurrences(visible.Schedule.Id)).Occurrences);
        Assert.Empty((await reader.Schedules.ListOccurrences(hidden.Schedule.Id)).Occurrences);
        Assert.Equal("scheduled.visible", Assert.Single(overview.Recent.Schedules).DefinitionName);
        Assert.Empty(overview.Upcoming.Schedules);
        Assert.Equal(visible.Schedule.Id, overview.SelectedSchedule!.Id);
        Assert.Single(overview.Occurrences);

        groups.Groups = new HashSet<string>();
        var noAccessReader = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("no-access-reader"),
            isAuthenticated: true));
        var hiddenOverview = await noAccessReader.Schedules.GetOverview();
        var hiddenUpcoming = await noAccessReader.Schedules.ListUpcoming();
        Assert.Empty(hiddenOverview.Recent.Schedules);
        Assert.Empty(hiddenOverview.Upcoming.Schedules);
        Assert.Null(hiddenOverview.SelectedSchedule);
        Assert.Empty(hiddenOverview.Occurrences);
        Assert.Empty(hiddenUpcoming.Schedules);
        Assert.Equal(0, hiddenUpcoming.ActiveScheduleCount);
    }

    [Fact]
    public async Task ApplyDefinitionReadScopeBeforeTheStoreTruncatesScheduleResults()
    {
        var store = new InMemoryScheduleStore();
        var groups = new MutableGroupProvider(new HashSet<string> { "visible-readers" });
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddSingleton<IWorkAuthorizationGroupProvider>(groups)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization()
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.visible-window"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configure: null,
                    authorize: authorization => authorization.AllowReadToGroups("visible-readers"))
                .AddWork(
                    WorkDefinition.Create("scheduled.hidden-window"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configure: null,
                    authorize: authorization => authorization.AllowReadToGroups("hidden-readers")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var future = DateTimeOffset.UtcNow + TimeSpan.FromDays(1);
        var visible = ScheduleRecord(
            "scheduled.visible-window",
            WorkScheduleTiming.Once(future),
            new WorkActor("visible-creator"),
            WorkScheduleExecutionGrant.Unrestricted);
        Assert.Equal(WorkScheduleStoreCreationStatus.Accepted, await store.Create(LargeStoreRequest(visible)));
        var secondVisible = visible with
        {
            Schedule = visible.Schedule with
            {
                Id = WorkScheduleId.New(),
                CreatedAt = visible.Schedule.CreatedAt + TimeSpan.FromTicks(1),
            },
        };
        Assert.Equal(WorkScheduleStoreCreationStatus.Accepted, await store.Create(LargeStoreRequest(secondVisible)));
        for (var index = 0; index < WorkScheduleCriteria.MaximumTake; index++)
        {
            var hidden = ScheduleRecord(
                "scheduled.hidden-window",
                WorkScheduleTiming.Once(future),
                new WorkActor($"hidden-creator-{index}"),
                WorkScheduleExecutionGrant.Unrestricted);
            Assert.Equal(WorkScheduleStoreCreationStatus.Accepted, await store.Create(LargeStoreRequest(hidden)));
        }

        var reader = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("visible-reader"),
            isAuthenticated: true));

        var result = await reader.Schedules.List(new(Take: 1));
        var continued = await reader.Schedules.List(new(Take: 1, Cursor: result.Cursor));
        var upcoming = await reader.Schedules.ListUpcoming(take: 1);
        var continuedUpcoming = await reader.Schedules.ListUpcoming(1, upcoming.Cursor);

        Assert.Equal("scheduled.visible-window", Assert.Single(result.Schedules).DefinitionName);
        Assert.NotNull(result.Cursor);
        Assert.Equal("scheduled.visible-window", Assert.Single(continued.Schedules).DefinitionName);
        Assert.Null(continued.Cursor);
        Assert.Equal("scheduled.visible-window", Assert.Single(upcoming.Schedules).DefinitionName);
        Assert.Equal(2, upcoming.ActiveScheduleCount);
        Assert.NotNull(upcoming.Cursor);
        Assert.Equal("scheduled.visible-window", Assert.Single(continuedUpcoming.Schedules).DefinitionName);
        Assert.Null(continuedUpcoming.Cursor);
    }

    [Fact]
    public async Task BindOverviewOccurrencesToTheAuthorizedSelectedSchedule()
    {
        var inner = new InMemoryScheduleStore();
        var store = new FaultInjectingScheduleStore(inner);
        var groups = new MutableGroupProvider(new HashSet<string> { "visible-readers" });
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddSingleton<IWorkAuthorizationGroupProvider>(groups)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization()
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.visible-overview"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configure: null,
                    authorize: authorization => authorization.AllowReadToGroups("visible-readers"))
                .AddWork(
                    WorkDefinition.Create("scheduled.hidden-overview"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configure: null,
                    authorize: authorization => authorization.AllowReadToGroups("hidden-readers")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var firstRunAt = DateTimeOffset.UtcNow + TimeSpan.FromDays(1);
        var visible = ScheduleRecord(
            "scheduled.visible-overview",
            WorkScheduleTiming.Once(firstRunAt),
            new WorkActor("visible-creator"),
            WorkScheduleExecutionGrant.Unrestricted);
        var hidden = ScheduleRecord(
            "scheduled.hidden-overview",
            WorkScheduleTiming.Once(firstRunAt),
            new WorkActor("hidden-creator"),
            WorkScheduleExecutionGrant.Unrestricted);
        Assert.Equal(WorkScheduleStoreCreationStatus.Accepted, await inner.Create(LargeStoreRequest(visible)));
        Assert.Equal(WorkScheduleStoreCreationStatus.Accepted, await inner.Create(LargeStoreRequest(hidden)));
        var summaries = await inner.List(new(WorkSystemName: null, Take: 10));
        var visibleSummary = Assert.Single(summaries, schedule => schedule.Id == visible.Schedule.Id);
        var attemptedAt = DateTimeOffset.UtcNow;
        var hiddenOccurrence = new WorkScheduleOccurrence(
            Guid.NewGuid(),
            hidden.Schedule.Id,
            firstRunAt,
            attemptedAt,
            WorkScheduleOccurrenceStatus.Accepted,
            QueueStatus: null,
            WorkerId: null,
            Messages: [],
            ExpiresAt: attemptedAt + TimeSpan.FromDays(1));
        var visibleOccurrence = hiddenOccurrence with
        {
            OccurrenceId = Guid.NewGuid(),
            ScheduleId = visible.Schedule.Id,
        };
        store.OverviewResultOverride = new(
            new([visibleSummary]),
            new([visibleSummary], null, 1, 1, 0),
            visibleSummary,
            [hiddenOccurrence, visibleOccurrence]);
        var reader = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("visible-reader"),
            isAuthenticated: true));

        var overview = await reader.Schedules.GetOverview(new(
            visible.Schedule.Id,
            OccurrenceTake: 1));

        Assert.Equal(visible.Schedule.Id, overview.SelectedSchedule!.Id);
        Assert.Equal(visibleOccurrence, Assert.Single(overview.Occurrences));
    }

    [Fact]
    public async Task BoundOverviewCollectionsReturnedByCustomProviders()
    {
        var inner = new InMemoryScheduleStore();
        var store = new FaultInjectingScheduleStore(inner);
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.overview-provider-bound"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success())))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var firstRunAt = DateTimeOffset.UtcNow + TimeSpan.FromDays(1);
        var first = ScheduleRecord(
            "scheduled.overview-provider-bound",
            WorkScheduleTiming.Once(firstRunAt),
            new WorkActor("first-creator"),
            WorkScheduleExecutionGrant.Unrestricted);
        var second = ScheduleRecord(
            "scheduled.overview-provider-bound",
            WorkScheduleTiming.Once(firstRunAt),
            new WorkActor("second-creator"),
            WorkScheduleExecutionGrant.Unrestricted);
        Assert.Equal(WorkScheduleStoreCreationStatus.Accepted, await inner.Create(LargeStoreRequest(first)));
        Assert.Equal(WorkScheduleStoreCreationStatus.Accepted, await inner.Create(LargeStoreRequest(second)));
        var summaries = await inner.List(new(WorkSystemName: null, Take: 10));
        Assert.Equal(2, summaries.Count);
        var selected = summaries[0];
        var attemptedAt = DateTimeOffset.UtcNow;
        var firstOccurrence = new WorkScheduleOccurrence(
            Guid.NewGuid(),
            selected.Id,
            firstRunAt,
            attemptedAt,
            WorkScheduleOccurrenceStatus.Accepted,
            QueueStatus: null,
            WorkerId: null,
            Messages: [],
            ExpiresAt: attemptedAt + TimeSpan.FromDays(1));
        var secondOccurrence = firstOccurrence with { OccurrenceId = Guid.NewGuid() };
        var unrelatedOccurrence = firstOccurrence with
        {
            OccurrenceId = Guid.NewGuid(),
            ScheduleId = summaries[1].Id,
        };
        store.OverviewResultOverride = new(
            new(summaries),
            new(summaries, null, 2, 2, 0),
            selected,
            [unrelatedOccurrence, firstOccurrence, secondOccurrence]);

        var overview = await system.Schedules.GetOverview(new(
            selected.Id,
            RecentScheduleTake: 1,
            UpcomingScheduleTake: 1,
            OccurrenceTake: 1));

        Assert.Equal(summaries[0], Assert.Single(overview.Recent.Schedules));
        Assert.Equal(summaries[0], Assert.Single(overview.Upcoming.Schedules));
        Assert.Equal(firstOccurrence, Assert.Single(overview.Occurrences));
    }

    [Fact]
    public async Task DoNotDispatchAClaimCanceledBeforeQueueingStarts()
    {
        var inner = new InMemoryScheduleStore();
        var store = new BlockingClaimScheduleStore(inner);
        var executions = 0;
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.cancel-race"),
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref executions);
                        return Task.FromResult(WorkExecutionResult.Success());
                    }))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var created = await system.Schedules.Create(new(
            "scheduled.cancel-race",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow)));
        await store.Claimed.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var canceled = await system.Schedules.Cancel(created.Schedule!.Id);
        store.ContinueClaim.TrySetResult();
        await Task.Delay(250);

        Assert.True(canceled.IsAccepted);
        Assert.Equal(0, Volatile.Read(ref executions));
        Assert.Equal(WorkScheduleStatus.Canceled, (await system.Schedules.Get(created.Schedule.Id))!.Status);
        Assert.Empty((await inner.ListOccurrences(new(null, created.Schedule.Id))).ToArray());
    }

    [Fact]
    public async Task RejectCancellationAfterDispatchHasBeenCommitted()
    {
        var inner = new InMemoryScheduleStore();
        var store = new BlockingDispatchStartScheduleStore(inner);
        var executed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.dispatch-commit"),
                    (_, _, _) =>
                    {
                        executed.TrySetResult();
                        return Task.FromResult(WorkExecutionResult.Success());
                    }))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var created = await system.Schedules.Create(new(
            "scheduled.dispatch-commit",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow)));
        await store.DispatchStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var canceled = await system.Schedules.Cancel(created.Schedule!.Id);
        store.ContinueDispatch.TrySetResult();
        await executed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var occurrence = await WaitForOccurrence(inner, created.Schedule.Id);

        Assert.Equal(WorkScheduleCancellationStatus.Conflict, canceled.Status);
        Assert.Contains(canceled.Messages, message => message.Code == "workable.schedule.dispatch_in_progress");
        Assert.Equal(WorkScheduleOccurrenceStatus.Accepted, occurrence.Status);
        Assert.Equal(WorkScheduleStatus.Completed, (await system.Schedules.Get(created.Schedule.Id))!.Status);
    }

    [Fact]
    public async Task SurfaceInitializationAndCreationStoreFailuresWithoutPoisoningASecondAttempt()
    {
        var store = new FaultInjectingScheduleStore(new InMemoryScheduleStore())
        {
            InitializeFailures = 1,
        };
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.store-foreground-failure"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success())))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        await Assert.ThrowsAsync<InvalidOperationException>(() => system.Start());
        await system.Start();
        store.CreateFailures = 1;
        await Assert.ThrowsAsync<InvalidOperationException>(() => system.Schedules.Create(new(
            "scheduled.store-foreground-failure",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow + TimeSpan.FromHours(1)))));
        var accepted = await system.Schedules.Create(new(
            "scheduled.store-foreground-failure",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow + TimeSpan.FromHours(1))));

        Assert.True(accepted.IsAccepted);
        Assert.Equal(2, store.InitializeCalls);
        Assert.Equal(2, store.CreateCalls);
    }

    [Fact]
    public async Task ContinueSchedulingAfterClaimCompletionAndCleanupStoreFailures()
    {
        var store = new FaultInjectingScheduleStore(new InMemoryScheduleStore())
        {
            ClaimFailures = 1,
            CompleteFailures = 1,
            CleanupFailures = 1,
        };
        var executionSignal = new SemaphoreSlim(0);
        var executions = 0;
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.store-background-failure"),
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref executions);
                        executionSignal.Release();
                        return Task.FromResult(WorkExecutionResult.Success());
                    }))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var first = await system.Schedules.Create(new(
            "scheduled.store-background-failure",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow)));
        Assert.True(await executionSignal.WaitAsync(TimeSpan.FromSeconds(5)));
        await TestEventually.Until(() => store.CompleteCalls >= 1);
        var second = await system.Schedules.Create(new(
            "scheduled.store-background-failure",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow)));
        Assert.True(await executionSignal.WaitAsync(TimeSpan.FromSeconds(5)));
        var secondOccurrence = await WaitForOccurrence(store.Inner, second.Schedule!.Id);
        await TestEventually.Until(
            () => store.CleanupCalls >= 2,
            "Expected cleanup to retry after its injected failure.",
            timeout: TimeSpan.FromSeconds(5));

        Assert.Equal(WorkScheduleOccurrenceStatus.Accepted, secondOccurrence.Status);
        Assert.Empty((await store.Inner.ListOccurrences(new(null, first.Schedule!.Id))).ToArray());
        Assert.Equal(2, Volatile.Read(ref executions));
        Assert.True(store.ClaimCalls >= 3);
        Assert.True(store.CompleteCalls >= 2);
        Assert.Equal(WorkSystemState.Started, system.State);
    }

    [Fact]
    public async Task RejectSchedulesThatWouldFailNormalQueueInputValidation()
    {
        var store = new InMemoryScheduleStore();
        var concurrency = WorkConcurrencyConfiguration.Default with
        {
            IsEnabled = true,
            MaximumCapacity = 1,
            Scope = WorkConcurrencyScope.PerSubject,
        };
        var configuration = WorkConfiguration.Default with
        {
            Coordination = WorkCoordinationConfiguration.Default with
            {
                IsEnabled = true,
                Concurrency = concurrency,
            },
        };
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.queue-validation", configuration: configuration),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success())))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var future = DateTimeOffset.UtcNow + TimeSpan.FromHours(1);

        var malformed = await system.Schedules.Create(new(
            "scheduled.queue-validation",
            WorkScheduleTiming.Every(TimeSpan.FromMinutes(1), future),
            WorkInput.Empty.WithIdentifier(new WorkIdentifier(" ", "value"))));
        var reserved = await system.Schedules.Create(new(
            "scheduled.queue-validation",
            WorkScheduleTiming.Every(TimeSpan.FromMinutes(1), future),
            WorkInput.Empty.WithIdentifier(new WorkIdentifier("workflow-run", WorkflowRunId.New().ToString()))));
        var missingSubject = await system.Schedules.Create(new(
            "scheduled.queue-validation",
            WorkScheduleTiming.Every(TimeSpan.FromMinutes(1), future)));

        Assert.Equal(WorkScheduleCreationStatus.Invalid, malformed.Status);
        Assert.Contains(malformed.Messages, message => message.Code == "workable.identifier.invalid");
        Assert.Equal(WorkScheduleCreationStatus.Invalid, reserved.Status);
        Assert.Contains(reserved.Messages, message => message.Code == "workable.workflow.identifier.reserved");
        Assert.Equal(WorkScheduleCreationStatus.Invalid, missingSubject.Status);
        Assert.Contains(missingSubject.Messages, message => message.Code == "workable.concurrency.subject_required");
        Assert.Empty((await store.List(new(null))).ToArray());
    }

    [Fact]
    public async Task BoundSchedulePayloadAndRetainedHistoryPerSystemAndActor()
    {
        static async Task<WorkScheduleCreationOutcome> Reject(
            WorkSystemSchedulingConfiguration configuration,
            bool retainCanceledSeed)
        {
            var store = new InMemoryScheduleStore();
            await using var provider = new ServiceCollection()
                .AddSingleton<IWorkScheduleStore>(store)
                .AddWorkableSystem(builder => builder
                    .RequireAuthorization(false)
                    .UseScheduling(configuration with { IsEnabled = true })
                    .AddWork(
                        WorkDefinition.Create("scheduled.retained-limit"),
                        (_, _, _) => Task.FromResult(WorkExecutionResult.Success())))
                .BuildServiceProvider();
            var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
            await system.Start();
            var future = DateTimeOffset.UtcNow + TimeSpan.FromHours(1);
            if (retainCanceledSeed)
            {
                var seed = await system.Schedules.Create(new(
                    "scheduled.retained-limit",
                    WorkScheduleTiming.Once(future)));
                Assert.True(seed.IsAccepted);
                Assert.True((await system.Schedules.Cancel(seed.Schedule!.Id)).IsAccepted);
            }

            return await system.Schedules.Create(new(
                "scheduled.retained-limit",
                WorkScheduleTiming.Once(future),
                WorkInput.FromValue(new { payload = "bounded" })));
        }

        var payload = await Reject(WorkSystemSchedulingConfiguration.Default with
        {
            MaximumSchedulePayloadBytes = 1,
        }, retainCanceledSeed: false);
        var retainedSystemCount = await Reject(WorkSystemSchedulingConfiguration.Default with
        {
            MaximumRetainedSchedules = 1,
            MaximumRetainedSchedulesPerActor = 10,
        }, retainCanceledSeed: true);
        var retainedActorCount = await Reject(WorkSystemSchedulingConfiguration.Default with
        {
            MaximumRetainedSchedules = 10,
            MaximumRetainedSchedulesPerActor = 1,
        }, retainCanceledSeed: true);
        var retainedSystemBytes = await Reject(WorkSystemSchedulingConfiguration.Default with
        {
            MaximumRetainedPayloadBytes = 1,
            MaximumRetainedPayloadBytesPerActor = long.MaxValue,
        }, retainCanceledSeed: false);
        var retainedActorBytes = await Reject(WorkSystemSchedulingConfiguration.Default with
        {
            MaximumRetainedPayloadBytes = long.MaxValue,
            MaximumRetainedPayloadBytesPerActor = 1,
        }, retainCanceledSeed: false);

        Assert.Equal(WorkScheduleCreationStatus.Invalid, payload.Status);
        Assert.Contains(payload.Messages, message => message.Code == "workable.schedule.payload_too_large");
        Assert.Equal(WorkScheduleCreationStatus.LimitReached, retainedSystemCount.Status);
        Assert.Contains(retainedSystemCount.Messages, message => message.Code == "workable.schedule.retained_system_limit_reached");
        Assert.Equal(WorkScheduleCreationStatus.LimitReached, retainedActorCount.Status);
        Assert.Contains(retainedActorCount.Messages, message => message.Code == "workable.schedule.retained_actor_limit_reached");
        Assert.Equal(WorkScheduleCreationStatus.LimitReached, retainedSystemBytes.Status);
        Assert.Contains(retainedSystemBytes.Messages, message => message.Code == "workable.schedule.retained_system_limit_reached");
        Assert.Equal(WorkScheduleCreationStatus.LimitReached, retainedActorBytes.Status);
        Assert.Contains(retainedActorBytes.Messages, message => message.Code == "workable.schedule.retained_actor_limit_reached");
    }

    [Fact]
    public async Task ReportDefinitionNamesRejectedByTheScheduleStoreAsInvalid()
    {
        var store = new InMemoryScheduleStore
        {
            CreationStatusOverride = WorkScheduleStoreCreationStatus.InvalidDefinitionName,
        };
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.unsupported-name"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success())))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var outcome = await system.Schedules.Create(new(
            "scheduled.unsupported-name",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow + TimeSpan.FromHours(1))));

        Assert.Equal(WorkScheduleCreationStatus.Invalid, outcome.Status);
        Assert.Contains(outcome.Messages, message =>
            message.Code == "workable.schedule.definition_name_too_long" && message.Target == "definition");
    }

    [Fact]
    public async Task EnforceMinimumIntervalsAndActiveScheduleLimits()
    {
        var store = new InMemoryScheduleStore();
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .UseScheduling(new WorkSystemSchedulingConfiguration
                {
                    IsEnabled = true,
                    MaximumActiveSchedules = 2,
                    MaximumActiveSchedulesPerDefinition = 1,
                })
                .AddWork(WorkDefinition.Create("scheduled.limit.alpha"), (_, _, _) => Task.FromResult(WorkExecutionResult.Success()))
                .AddWork(WorkDefinition.Create("scheduled.limit.beta"), (_, _, _) => Task.FromResult(WorkExecutionResult.Success()))
                .AddWork(WorkDefinition.Create("scheduled.limit.gamma"), (_, _, _) => Task.FromResult(WorkExecutionResult.Success())))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var future = DateTimeOffset.UtcNow + TimeSpan.FromHours(1);

        var tooFrequent = await system.Schedules.Create(new(
            "scheduled.limit.alpha",
            WorkScheduleTiming.Every(TimeSpan.FromSeconds(59), future)));
        var alpha = await system.Schedules.Create(new(
            "scheduled.limit.alpha",
            WorkScheduleTiming.Every(TimeSpan.FromMinutes(1), future)));
        var duplicateAlpha = await system.Schedules.Create(new(
            "SCHEDULED.LIMIT.ALPHA",
            WorkScheduleTiming.Once(future)));
        var beta = await system.Schedules.Create(new(
            "scheduled.limit.beta",
            WorkScheduleTiming.Once(future)));
        var systemFull = await system.Schedules.Create(new(
            "scheduled.limit.gamma",
            WorkScheduleTiming.Once(future)));
        await system.Schedules.Cancel(alpha.Schedule!.Id);
        var afterCancel = await system.Schedules.Create(new(
            "scheduled.limit.gamma",
            WorkScheduleTiming.Once(future)));

        Assert.Equal(WorkScheduleCreationStatus.Invalid, tooFrequent.Status);
        Assert.Contains(tooFrequent.Messages, message => message.Code == "workable.schedule.interval_invalid");
        Assert.True(alpha.IsAccepted);
        Assert.Equal(WorkScheduleCreationStatus.LimitReached, duplicateAlpha.Status);
        Assert.Contains(duplicateAlpha.Messages, message => message.Code == "workable.schedule.definition_limit_reached");
        Assert.True(beta.IsAccepted);
        Assert.Equal(WorkScheduleCreationStatus.LimitReached, systemFull.Status);
        Assert.Contains(systemFull.Messages, message => message.Code == "workable.schedule.system_limit_reached");
        Assert.True(afterCancel.IsAccepted);
    }

    [Fact]
    public async Task EnforceActiveScheduleCapacityPerCreatorWithoutBlockingOtherCreators()
    {
        var store = new InMemoryScheduleStore();
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .UseScheduling(WorkSystemSchedulingConfiguration.Default with
                {
                    IsEnabled = true,
                    MaximumActiveSchedulesPerActor = 1,
                })
                .AddWork(
                    WorkDefinition.Create("scheduled.actor-capacity"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success())))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var firstCreator = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("first-creator")));
        var secondCreator = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("second-creator")));
        var future = DateTimeOffset.UtcNow + TimeSpan.FromHours(1);

        var first = await firstCreator.Schedules.Create(new(
            "scheduled.actor-capacity",
            WorkScheduleTiming.Once(future)));
        var sameCreator = await firstCreator.Schedules.Create(new(
            "scheduled.actor-capacity",
            WorkScheduleTiming.Once(future)));
        var otherCreator = await secondCreator.Schedules.Create(new(
            "scheduled.actor-capacity",
            WorkScheduleTiming.Once(future)));

        Assert.True(first.IsAccepted);
        Assert.Equal(WorkScheduleCreationStatus.LimitReached, sameCreator.Status);
        Assert.Contains(sameCreator.Messages, message => message.Code == "workable.schedule.active_actor_limit_reached");
        Assert.True(otherCreator.IsAccepted);
    }

    [Fact]
    public async Task RejectOversizedCreatorAndCancelerIdentityWithoutChangingItsSecurityMeaning()
    {
        var store = new InMemoryScheduleStore();
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.actor-bounds"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success())))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var creator = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor(new string('i', 600), new string('n', 600), new string('e', 600))));
        var rejectedCreation = await creator.Schedules.Create(new(
            "scheduled.actor-bounds",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow + TimeSpan.FromHours(1))));
        var validCreator = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("exact-creator")));
        var created = await validCreator.Schedules.Create(new(
            "scheduled.actor-bounds",
            WorkScheduleTiming.Once(DateTimeOffset.UtcNow + TimeSpan.FromHours(1))));
        var beforeCancellation = await store.Get(new(null, created.Schedule!.Id));
        var canceler = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor(new string('c', 600), new string('a', 600), new string('m', 600))));

        var canceled = await canceler.Schedules.Cancel(created.Schedule.Id);
        var afterCancellation = await store.Get(new(null, created.Schedule.Id));
        var maximallyEscapedField = new string('\u0001', WorkScheduler.MaximumPersistedActorFieldLength);
        var maximumCancellationActorPayloadBytes = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new WorkActor(maximallyEscapedField, maximallyEscapedField, maximallyEscapedField),
            WorkData.DefaultJsonOptions).LongLength;
        var boundedCanceler = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor(maximallyEscapedField, maximallyEscapedField, maximallyEscapedField)));
        var acceptedCancellation = await boundedCanceler.Schedules.Cancel(created.Schedule.Id);
        var afterAcceptedCancellation = await store.Get(new(null, created.Schedule.Id));

        Assert.Equal(WorkScheduleCreationStatus.Invalid, rejectedCreation.Status);
        Assert.Null(rejectedCreation.Schedule);
        Assert.Equal(3, rejectedCreation.Messages.Count(message =>
            message.Code == "workable.schedule.actor_field_too_long"));
        Assert.True(created.IsAccepted);
        Assert.Equal("exact-creator", created.Schedule.CreatedBy.Id);
        Assert.Equal(WorkScheduleCancellationStatus.Invalid, canceled.Status);
        Assert.Equal(3, canceled.Messages.Count(message =>
            message.Code == "workable.schedule.actor_field_too_long"));
        Assert.Equal(WorkScheduleStatus.Active, afterCancellation!.Schedule.Status);
        Assert.Equal(beforeCancellation!.SerializedPayloadBytes, afterCancellation.SerializedPayloadBytes);
        Assert.True(maximumCancellationActorPayloadBytes <=
            WorkScheduleStoreCreateRequest.CancellationActorPayloadReserveBytes);
        Assert.True(acceptedCancellation.IsAccepted);
        Assert.True(afterAcceptedCancellation!.SerializedPayloadBytes <= beforeCancellation.SerializedPayloadBytes);
    }

    [Fact]
    public async Task DrainSeveralExpiredOccurrenceBatchesDuringOneCleanupInterval()
    {
        var store = new FaultInjectingScheduleStore(new InMemoryScheduleStore())
        {
            FullOccurrenceCleanupBatches = 3,
        };
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .EnableScheduling()
                .AddWork(
                    WorkDefinition.Create("scheduled.cleanup-backlog"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success())))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();
        await TestEventually.Until(
            () => store.CleanupCalls >= 5,
            "Expected cleanup to drain multiple occurrence batches and then inspect terminal schedules.");

        Assert.Equal(0, store.FullOccurrenceCleanupBatches);
    }

    [Fact]
    public async Task PurgeTerminalSchedulesAfterTheHistoryRetentionPeriod()
    {
        var store = new InMemoryScheduleStore();
        var finalizedAt = DateTimeOffset.UtcNow - TimeSpan.FromMinutes(2);
        var completedId = WorkScheduleId.New();
        var activeId = WorkScheduleId.New();
        var actor = new WorkActor("retention-test");
        var context = WorkRequestContext.Create(WorkInvocationChannel.InProcess, actor);
        WorkSchedulePersistenceRecord Record(WorkScheduleId id, WorkScheduleStatus status) => new(
            new WorkScheduleSnapshot(
                id,
                null,
                "scheduled.retention",
                WorkScheduleTiming.Once(DateTimeOffset.UtcNow + TimeSpan.FromHours(1)),
                WorkInput.Empty,
                null,
                status,
                finalizedAt,
                actor,
                status == WorkScheduleStatus.Active ? DateTimeOffset.UtcNow + TimeSpan.FromHours(1) : null,
                status == WorkScheduleStatus.Completed ? finalizedAt : null,
                null,
                null),
            context,
            WorkScheduleExecutionGrant.Unrestricted);
        Assert.Equal(WorkScheduleStoreCreationStatus.Accepted, await store.Create(StoreRequest(Record(completedId, WorkScheduleStatus.Completed))));
        Assert.Equal(WorkScheduleStoreCreationStatus.Accepted, await store.Create(StoreRequest(Record(activeId, WorkScheduleStatus.Active))));
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkScheduleStore>(store)
            .AddWorkableSystem(builder => builder
                .RequireAuthorization(false)
                .EnableScheduling(TimeSpan.FromMinutes(1))
                .AddWork(WorkDefinition.Create("scheduled.retention"), (_, _, _) => Task.FromResult(WorkExecutionResult.Success())))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        await TestEventually.Until(
            async () => await store.Get(new(null, completedId)) is null,
            "Expected the terminal schedule to be deleted by retention cleanup.");

        Assert.NotNull(await store.Get(new(null, activeId)));
    }

    [Fact]
    public void EnforceSchedulingHistoryRetentionBoundsAndAdvanceWithoutCatchUpBursts()
    {
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkableSystem(
            builder => builder.EnableScheduling(TimeSpan.FromSeconds(30))));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkableSystem(
            builder => builder.EnableScheduling(TimeSpan.FromDays(8))));
        Assert.Throws<ArgumentNullException>(() => new ServiceCollection().AddWorkableSystem(
            builder => builder.UseScheduling(null!)));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkableSystem(
            builder => builder.UseScheduling(new WorkSystemSchedulingConfiguration
            {
                MaximumActiveSchedules = 0,
            })));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkableSystem(
            builder => builder.UseScheduling(new WorkSystemSchedulingConfiguration
            {
                MaximumActiveSchedulesPerDefinition = 0,
            })));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkableSystem(
            builder => builder.UseScheduling(new WorkSystemSchedulingConfiguration
            {
                MaximumActiveSchedulesPerActor = 0,
            })));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkableSystem(
            builder => builder.UseScheduling(new WorkSystemSchedulingConfiguration
            {
                MaximumRetainedSchedules = 0,
            })));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkableSystem(
            builder => builder.UseScheduling(new WorkSystemSchedulingConfiguration
            {
                MaximumRetainedSchedulesPerActor = 0,
            })));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkableSystem(
            builder => builder.UseScheduling(new WorkSystemSchedulingConfiguration
            {
                MaximumSchedulePayloadBytes = 0,
            })));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkableSystem(
            builder => builder.UseScheduling(new WorkSystemSchedulingConfiguration
            {
                MaximumRetainedPayloadBytes = 0,
            })));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkableSystem(
            builder => builder.UseScheduling(new WorkSystemSchedulingConfiguration
            {
                MaximumRetainedPayloadBytesPerActor = 0,
            })));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkableSystem(
            builder => builder.UseScheduling(new WorkSystemSchedulingConfiguration
            {
                MaximumRetainedOccurrences = 0,
            })));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkableSystem(
            builder => builder.UseScheduling(new WorkSystemSchedulingConfiguration
            {
                MaximumOccurrencePayloadBytes = 0,
            })));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkableSystem(
            builder => builder.UseScheduling(new WorkSystemSchedulingConfiguration
            {
                MaximumOccurrencePayloadBytes = 1,
            })));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkableSystem(
            builder => builder.UseScheduling(new WorkSystemSchedulingConfiguration
            {
                MaximumOccurrencePayloadBytes = 10,
                MaximumRetainedOccurrencePayloadBytes = 9,
            })));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkableSystem(
            builder => builder.UseScheduling(new WorkSystemSchedulingConfiguration
            {
                MaximumOccurrencePayloadBytes = 10,
                MaximumOccurrenceQueryPayloadBytes = 9,
            })));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkableSystem(
            builder => builder.UseScheduling(new WorkSystemSchedulingConfiguration
            {
                MaximumDispatchesPerBatch = 0,
            })));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkableSystem(
            builder => builder.UseScheduling(new WorkSystemSchedulingConfiguration
            {
                MaximumSchedulePayloadBytes = 10,
                MaximumClaimedPayloadBytesPerBatch = 9,
            })));
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddWorkableSystem(
            builder => builder.UseScheduling(new WorkSystemSchedulingConfiguration
            {
                MinimumInterval = TimeSpan.Zero,
            })));

        var scheduledAt = new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        Assert.Null(WorkScheduler.GetNextRunAt(null, scheduledAt, scheduledAt));
        Assert.Equal(
            scheduledAt + TimeSpan.FromMinutes(4),
            WorkScheduler.GetNextRunAt(TimeSpan.FromMinutes(1), scheduledAt, scheduledAt + TimeSpan.FromMinutes(3)));
        Assert.Null(WorkScheduler.GetNextRunAt(
            TimeSpan.FromDays(1),
            DateTimeOffset.MaxValue - TimeSpan.FromHours(1),
            DateTimeOffset.MaxValue - TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentException>(() => WorkDefinition.Create(
            "scheduled.security-version.blank",
            scheduleSecurityVersion: " "));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkDefinition.Create(
            "scheduled.security-version.long",
            scheduleSecurityVersion: new string('v', WorkDefinition.MaximumScheduleSecurityVersionLength + 1)));
        Assert.Equal(
            "contract-v2",
            WorkDefinition.Create(
                "scheduled.security-version.valid",
                scheduleSecurityVersion: "contract-v2").ScheduleSecurityVersion);
    }

    [Fact]
    public void CalculateCronOccurrencesInTheirTimeZoneAcrossDowntimeAndDaylightSavingTime()
    {
        var weekday = WorkScheduleTiming.Cron(
            "0 9 * * 1-5",
            "America/Los_Angeles",
            new DateTimeOffset(2026, 9, 10, 0, 0, 0, TimeSpan.Zero));
        var first = WorkScheduleTimingCalculator.GetInitialRunAt(weekday);
        var afterDowntime = WorkScheduleTimingCalculator.GetNextRunAt(
            weekday,
            first!.Value,
            new DateTimeOffset(2026, 9, 11, 17, 0, 0, TimeSpan.Zero));

        Assert.Equal(new DateTimeOffset(2026, 9, 10, 9, 0, 0, TimeSpan.FromHours(-7)), first);
        Assert.Equal(new DateTimeOffset(2026, 9, 14, 9, 0, 0, TimeSpan.FromHours(-7)), afterDowntime);

        var springForward = WorkScheduleTiming.Cron(
            "30 2 * * *",
            "America/Los_Angeles",
            new DateTimeOffset(2026, 3, 8, 0, 0, 0, TimeSpan.FromHours(-8)));
        Assert.Equal(
            new DateTimeOffset(2026, 3, 8, 3, 0, 0, TimeSpan.FromHours(-7)),
            WorkScheduleTimingCalculator.GetInitialRunAt(springForward));

        var fallBack = WorkScheduleTiming.Cron(
            "30 1 * * *",
            "America/Los_Angeles",
            new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.FromHours(-7)));
        var fallOccurrences = WorkScheduleTimingCalculator.GetUpcomingCronOccurrences(fallBack, 2);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-7)), fallOccurrences[0]);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 1, 30, 0, TimeSpan.FromHours(-8)), fallOccurrences[1]);

        var bothDayFields = WorkScheduleTiming.Cron(
            "0 9 15 * 1",
            "UTC",
            new DateTimeOffset(2027, 2, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal(
            new DateTimeOffset(2027, 2, 15, 9, 0, 0, TimeSpan.Zero),
            WorkScheduleTimingCalculator.GetInitialRunAt(bothDayFields));

        Assert.Empty(WorkScheduleTimingCalculator.GetUpcomingCronOccurrences(fallBack, 0));
        Assert.Null(WorkScheduleTimingCalculator.GetInitialRunAt(
            WorkScheduleTiming.Cron("not cron", startsAt: DateTimeOffset.UtcNow)));
    }

    private static async Task<WorkScheduleOccurrence> WaitForOccurrence(
        InMemoryScheduleStore store,
        WorkScheduleId scheduleId)
    {
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var occurrences = await store.ListOccurrences(new(null, scheduleId));
            if (occurrences.Count > 0)
            {
                return occurrences[0];
            }

            await Task.Delay(20);
        }

        throw new TimeoutException($"Schedule '{scheduleId}' did not produce an occurrence.");
    }

    private static Task WaitForWorkerState(IWorkSystem system, WorkerId workerId, WorkerState state)
        => TestEventually.Until(
            async () => (await system.Query.Worker(workerId))?.State == state,
            $"Expected worker '{workerId}' to reach '{state}'.");

    private static WorkExecutionResult ScheduledFailure()
        => WorkExecutionResult.Failure([WorkMessage.Error("scheduled.failure", "Scheduled test failure.")]);

    private static WorkSchedulePersistenceRecord ScheduleRecord(
        string definitionName,
        WorkScheduleTiming timing,
        WorkActor actor,
        WorkScheduleExecutionGrant executionGrant,
        WorkInput? input = null,
        WorkerOptions? workerOptions = null)
        => new(
            new WorkScheduleSnapshot(
                WorkScheduleId.New(),
                null,
                definitionName,
                timing,
                input,
                workerOptions,
                WorkScheduleStatus.Active,
                DateTimeOffset.UtcNow,
                actor,
                timing.FirstRunAt,
                LastRunAt: null,
                CanceledAt: null,
                CanceledBy: null),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess, actor),
            executionGrant);

    private static WorkScheduleStoreCreateRequest StoreRequest(WorkSchedulePersistenceRecord record)
        => new(record, 100, 1_000, 10, 10, 10, 100, 100, 10_000, 10_000);

    private static WorkScheduleStoreCreateRequest LargeStoreRequest(WorkSchedulePersistenceRecord record)
        => new(
            record,
            100,
            1_000,
            2_000,
            2_000,
            2_000,
            2_000,
            2_000,
            1_000_000,
            1_000_000);

    private sealed record ScheduleAuthorizationInput(string Value);

    private sealed class BlockingInitializationPersistenceStore : IWorkPersistenceStore
    {
        private int blockNextInitialization;
        private readonly TaskCompletionSource initializationBlocked = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource initializationReleased = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task InitializationBlocked => this.initializationBlocked.Task;

        public void BlockNextInitialization()
            => Volatile.Write(ref this.blockNextInitialization, 1);

        public void ReleaseInitialization()
            => this.initializationReleased.TrySetResult();

        public async Task Initialize(
            WorkQueueDurabilityInitializationContext context,
            CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref this.blockNextInitialization, 0) == 0)
            {
                return;
            }

            this.initializationBlocked.TrySetResult();
            await this.initializationReleased.Task.WaitAsync(cancellationToken);
        }

        public Task Enqueue(
            WorkQueueDurabilityEnqueueRequest request,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task ReserveIdempotency(
            WorkIdempotencyPersistenceRequest request,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<WorkQueueDurabilityEntry> ClaimReady(
            WorkQueueDurabilityClaimRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
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
            => Task.CompletedTask;

        public Task DeleteFinal(
            IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
            IWorkQueueDurabilityTransaction transaction,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public async IAsyncEnumerable<WorkflowRunPersistenceRecord> ListWorkflowRuns(
            WorkflowPersistenceReadRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task DeleteWorkflowRun(
            WorkflowPersistenceDeleteRequest request,
            CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class MutableGroupProvider(IReadOnlySet<string> groups) : IWorkAuthorizationGroupProvider
    {
        private int calls;

        public IReadOnlySet<string> Groups { get; set; } = groups;

        public int Calls => Volatile.Read(ref this.calls);

        public ValueTask<IReadOnlySet<string>> GetGroups(
            WorkActor actor,
            string? systemName,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.calls);
            return ValueTask.FromResult(this.Groups);
        }
    }

    internal sealed class InMemoryScheduleStore : IWorkScheduleStore
    {
        private readonly Lock sync = new();
        private readonly Dictionary<WorkScheduleId, WorkSchedulePersistenceRecord> schedules = [];
        private readonly Dictionary<WorkScheduleId, Lease> leases = [];
        private readonly Dictionary<WorkScheduleId, List<WorkScheduleOccurrence>> occurrences = [];
        private readonly Dictionary<WorkScheduleId, long> cancellationPayloadReserves = [];
        private readonly Dictionary<Guid, WorkScheduleHostObservation> hostObservations = [];

        public bool AllowDispatchStart { get; set; } = true;

        public bool BlockEndHost { get; set; }

        public TaskCompletionSource EndHostStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource EndHostCancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public WorkScheduleStoreCreationStatus? CreationStatusOverride { get; set; }

        public Task Initialize(WorkScheduleStoreInitializationContext context, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task<IReadOnlyList<WorkScheduleClaim>> ClaimDueAndObserveHost(
            WorkScheduleClaimRequest request,
            WorkScheduleHostObservation observation,
            CancellationToken cancellationToken = default)
        {
            this.ObserveHost(observation);
            return this.ClaimDue(request, cancellationToken);
        }

        public void ObserveHost(WorkScheduleHostObservation observation)
        {
            lock (this.sync)
            {
                foreach (var hostRunId in this.hostObservations
                    .Where(pair => string.Equals(
                        pair.Value.WorkSystemName,
                        observation.WorkSystemName,
                        StringComparison.OrdinalIgnoreCase) &&
                        pair.Value.AvailableThrough < observation.DeleteEndedBefore)
                    .Select(static pair => pair.Key)
                    .Take(100)
                    .ToArray())
                {
                    this.hostObservations.Remove(hostRunId);
                }

                this.hostObservations[observation.HostRunId] = observation;
            }
        }

        public async Task EndHost(
            WorkScheduleHostEnd hostEnd,
            CancellationToken cancellationToken = default)
        {
            if (this.BlockEndHost)
            {
                this.EndHostStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    this.EndHostCancellationObserved.TrySetResult();
                    throw;
                }
            }

            lock (this.sync)
            {
                if (this.hostObservations.TryGetValue(hostEnd.HostRunId, out var observation) &&
                    string.Equals(observation.WorkSystemName, hostEnd.WorkSystemName, StringComparison.OrdinalIgnoreCase))
                {
                    this.hostObservations[hostEnd.HostRunId] = observation with
                    {
                        ObservedAt = observation.ObservedAt < hostEnd.EndedAt
                            ? hostEnd.EndedAt
                            : observation.ObservedAt,
                        AvailableThrough = observation.AvailableThrough > hostEnd.EndedAt
                            ? hostEnd.EndedAt
                            : observation.AvailableThrough,
                    };
                }
            }

        }

        public Task<IReadOnlySet<DateTimeOffset>> FindAvailableTimes(
            WorkScheduleHostAvailabilityRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                IReadOnlySet<DateTimeOffset> available = request.ScheduledTimes
                    .Where(scheduledAt => this.hostObservations.Values.Any(observation =>
                        string.Equals(observation.WorkSystemName, request.WorkSystemName, StringComparison.OrdinalIgnoreCase) &&
                        observation.StartedAt <= scheduledAt &&
                        observation.AvailableThrough >= scheduledAt))
                    .ToHashSet();
                return Task.FromResult(available);
            }
        }

        public Task<WorkScheduleStoreCreationStatus> Create(
            WorkScheduleStoreCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            if (this.CreationStatusOverride is { } creationStatus)
            {
                return Task.FromResult(creationStatus);
            }

            lock (this.sync)
            {
                var schedule = request.Record.Schedule;
                var active = this.schedules.Values
                    .Where(record => record.Schedule.Status == WorkScheduleStatus.Active)
                    .Where(record => string.Equals(
                        record.Schedule.WorkSystemName,
                        schedule.WorkSystemName,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                var retained = this.schedules.Values
                    .Where(record => string.Equals(
                        record.Schedule.WorkSystemName,
                        schedule.WorkSystemName,
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                if (request.PayloadSizeBytes > request.MaximumSchedulePayloadBytes)
                {
                    return Task.FromResult(WorkScheduleStoreCreationStatus.PayloadTooLarge);
                }

                if (retained.Length >= request.MaximumRetainedSchedules)
                {
                    return Task.FromResult(WorkScheduleStoreCreationStatus.RetainedSystemLimitReached);
                }

                var actorRetained = retained.Where(record => string.Equals(
                    record.Schedule.CreatedBy.Id,
                    schedule.CreatedBy.Id,
                    StringComparison.Ordinal)).ToArray();
                if (actorRetained.Length >= request.MaximumRetainedSchedulesPerActor)
                {
                    return Task.FromResult(WorkScheduleStoreCreationStatus.RetainedActorLimitReached);
                }

                if (retained.Sum(record => record.SerializedPayloadBytes) + request.PayloadSizeBytes >
                    request.MaximumRetainedPayloadBytes)
                {
                    return Task.FromResult(WorkScheduleStoreCreationStatus.RetainedSystemBytesLimitReached);
                }

                if (actorRetained.Sum(record => record.SerializedPayloadBytes) + request.PayloadSizeBytes >
                    request.MaximumRetainedPayloadBytesPerActor)
                {
                    return Task.FromResult(WorkScheduleStoreCreationStatus.RetainedActorBytesLimitReached);
                }
                if (active.Length >= request.MaximumActiveSchedules)
                {
                    return Task.FromResult(WorkScheduleStoreCreationStatus.SystemLimitReached);
                }

                if (active.Count(record => string.Equals(
                        record.Schedule.CreatedBy.Id,
                        schedule.CreatedBy.Id,
                        StringComparison.Ordinal)) >= request.MaximumActiveSchedulesPerActor)
                {
                    return Task.FromResult(WorkScheduleStoreCreationStatus.ActiveActorLimitReached);
                }

                if (active.Count(record => string.Equals(
                        record.Schedule.DefinitionName,
                        schedule.DefinitionName,
                        StringComparison.OrdinalIgnoreCase)) >= request.MaximumActiveSchedulesPerDefinition)
                {
                    return Task.FromResult(WorkScheduleStoreCreationStatus.DefinitionLimitReached);
                }

                this.schedules.Add(
                    schedule.Id,
                    request.Record with { SerializedPayloadBytes = request.PayloadSizeBytes });
                this.cancellationPayloadReserves.Add(schedule.Id, request.CancellationPayloadReserveBytes);
            }

            return Task.FromResult(WorkScheduleStoreCreationStatus.Accepted);
        }

        public Task<WorkSchedulePersistenceRecord?> Get(
            WorkScheduleStoreReadRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                return Task.FromResult(this.schedules.GetValueOrDefault(request.ScheduleId));
            }
        }

        public Task<IReadOnlyList<WorkScheduleSummary>> List(
            WorkScheduleStoreListRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                IReadOnlyList<WorkScheduleSummary> result = [.. this.schedules.Values
                    .Where(record => string.Equals(record.Schedule.WorkSystemName, request.WorkSystemName, StringComparison.OrdinalIgnoreCase))
                    .Where(record => request.DefinitionName is null || string.Equals(record.Schedule.DefinitionName, request.DefinitionName, StringComparison.OrdinalIgnoreCase))
                    .Where(record => request.DefinitionNames is null || request.DefinitionNames.Contains(record.Schedule.DefinitionName))
                    .Where(record => request.Status is null || record.Schedule.Status == request.Status)
                    .Where(record => request.Cursor is null ||
                        record.Schedule.CreatedAt < request.Cursor.CreatedAt ||
                        (record.Schedule.CreatedAt == request.Cursor.CreatedAt &&
                            record.Schedule.Id.Value.CompareTo(request.Cursor.ScheduleId.Value) > 0))
                    .OrderByDescending(record => record.Schedule.CreatedAt)
                    .ThenBy(record => record.Schedule.Id.Value)
                    .Take(request.Take)
                    .Select(static record => ToSummary(record.Schedule))];
                return Task.FromResult(result);
            }
        }

        public Task<WorkSchedulePersistenceRecord?> Cancel(
            WorkScheduleStoreCancelRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                if (!this.schedules.TryGetValue(request.ScheduleId, out var record) ||
                    record.Schedule.Status != WorkScheduleStatus.Active ||
                    this.leases.GetValueOrDefault(request.ScheduleId)?.DispatchStarted == true)
                {
                    return Task.FromResult<WorkSchedulePersistenceRecord?>(null);
                }

                var canceled = record with
                {
                    SerializedPayloadBytes = record.SerializedPayloadBytes -
                        this.cancellationPayloadReserves.GetValueOrDefault(request.ScheduleId) +
                        System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                            request.CanceledBy,
                            WorkData.DefaultJsonOptions).LongLength,
                    Schedule = record.Schedule with
                    {
                        Status = WorkScheduleStatus.Canceled,
                        NextRunAt = null,
                        CanceledAt = request.CanceledAt,
                        CanceledBy = request.CanceledBy,
                    },
                };
                this.schedules[request.ScheduleId] = canceled;
                this.cancellationPayloadReserves.Remove(request.ScheduleId);
                this.leases.Remove(request.ScheduleId);
                return Task.FromResult<WorkSchedulePersistenceRecord?>(canceled);
            }
        }

        public Task<IReadOnlyList<WorkScheduleClaim>> ClaimDue(
            WorkScheduleClaimRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                var claims = new List<WorkScheduleClaim>();
                var payloadBytes = 0L;
                foreach (var record in this.schedules.Values
                    .Where(record => record.Schedule.Status == WorkScheduleStatus.Active && record.Schedule.NextRunAt <= request.DueAt)
                    .Where(record => !this.leases.TryGetValue(record.Schedule.Id, out var lease) || lease.ExpiresAt <= request.DueAt)
                    .OrderBy(record => record.Schedule.NextRunAt)
                    .Take(request.MaximumCount))
                {
                    if (claims.Count > 0 && payloadBytes + record.SerializedPayloadBytes > request.MaximumPayloadBytes)
                    {
                        break;
                    }

                    var leaseId = Guid.NewGuid();
                    this.leases[record.Schedule.Id] = new(leaseId, request.DueAt + request.LeaseDuration, false);
                    claims.Add(new(
                        leaseId,
                        request.DueAt + request.LeaseDuration,
                        record.Schedule.NextRunAt!.Value,
                        record));
                    payloadBytes += record.SerializedPayloadBytes;
                }

                return Task.FromResult<IReadOnlyList<WorkScheduleClaim>>(claims);
            }
        }

        public Task<bool> BeginDispatch(
            WorkScheduleDispatchStart dispatch,
            CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                if (!this.AllowDispatchStart ||
                    !this.schedules.TryGetValue(dispatch.ScheduleId, out var record) ||
                    record.Schedule.Status != WorkScheduleStatus.Active ||
                    !this.leases.TryGetValue(dispatch.ScheduleId, out var lease) ||
                    lease.Id != dispatch.LeaseId ||
                    lease.ExpiresAt <= dispatch.StartedAt ||
                    lease.DispatchStarted)
                {
                    return Task.FromResult(false);
                }

                this.leases[dispatch.ScheduleId] = new(dispatch.LeaseId, dispatch.LeaseExpiresAt, true);
                return Task.FromResult(true);
            }
        }

        public Task CompleteClaim(
            WorkScheduleClaimCompletion completion,
            CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                if (!this.leases.TryGetValue(completion.ScheduleId, out var lease) || lease.Id != completion.LeaseId ||
                    !lease.DispatchStarted ||
                    !this.schedules.TryGetValue(completion.ScheduleId, out var record) ||
                    record.Schedule.Status != WorkScheduleStatus.Active)
                {
                    return Task.CompletedTask;
                }

                this.leases.Remove(completion.ScheduleId);
                var cancellationPayloadReserve = completion.ScheduleStatus == WorkScheduleStatus.Active
                    ? this.cancellationPayloadReserves.GetValueOrDefault(completion.ScheduleId)
                    : 0;
                this.schedules[completion.ScheduleId] = record with
                {
                    SerializedPayloadBytes = record.SerializedPayloadBytes -
                        this.cancellationPayloadReserves.GetValueOrDefault(completion.ScheduleId) +
                        cancellationPayloadReserve,
                    Schedule = record.Schedule with
                    {
                        Status = completion.ScheduleStatus,
                        NextRunAt = completion.NextRunAt,
                        LastRunAt = completion.Occurrence.AttemptedAt,
                    },
                };
                if (completion.ScheduleStatus != WorkScheduleStatus.Active)
                {
                    this.cancellationPayloadReserves.Remove(completion.ScheduleId);
                }
                if (!this.occurrences.TryGetValue(completion.ScheduleId, out var values))
                {
                    values = [];
                    this.occurrences.Add(completion.ScheduleId, values);
                }

                values.Insert(0, completion.Occurrence);
                while (this.occurrences.Values.Sum(static entries => entries.Count) > completion.MaximumRetainedOccurrences ||
                    this.occurrences.Values.SelectMany(static entries => entries).Sum(OccurrencePayloadSize) >
                        completion.MaximumRetainedOccurrencePayloadBytes)
                {
                    var oldest = this.occurrences
                        .SelectMany(static pair => pair.Value.Select(occurrence => (pair.Key, Occurrence: occurrence)))
                        .OrderBy(static candidate => candidate.Occurrence.AttemptedAt)
                        .ThenBy(static candidate => candidate.Occurrence.OccurrenceId)
                        .First();
                    this.occurrences[oldest.Key].Remove(oldest.Occurrence);
                }
                return Task.CompletedTask;
            }
        }

        private static long OccurrencePayloadSize(WorkScheduleOccurrence occurrence)
            => System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                occurrence.Messages,
                WorkData.DefaultJsonOptions).LongLength;

        private static WorkScheduleSummary ToSummary(WorkScheduleSnapshot schedule)
            => new(
                schedule.Id,
                schedule.WorkSystemName,
                schedule.DefinitionName,
                schedule.Timing,
                schedule.Status,
                schedule.CreatedAt,
                schedule.CreatedBy,
                schedule.NextRunAt,
                schedule.LastRunAt,
                schedule.CanceledAt,
                schedule.CanceledBy);

        public Task ReleaseClaim(WorkScheduleClaimRelease release, CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                if (this.leases.GetValueOrDefault(release.ScheduleId)?.Id == release.LeaseId)
                {
                    this.leases.Remove(release.ScheduleId);
                }
            }

            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<WorkScheduleOccurrence>> ListOccurrences(
            WorkScheduleOccurrenceReadRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                var result = new List<WorkScheduleOccurrence>();
                if (this.occurrences.TryGetValue(request.ScheduleId, out var values))
                {
                    var payloadBytes = 0L;
                    foreach (var occurrence in values
                        .Where(value => value.ExpiresAt > DateTimeOffset.UtcNow)
                        .Take(request.Take))
                    {
                        var occurrenceBytes = OccurrencePayloadSize(occurrence);
                        if (result.Count > 0 && payloadBytes + occurrenceBytes > request.MaximumPayloadBytes)
                        {
                            break;
                        }

                        result.Add(occurrence);
                        payloadBytes += occurrenceBytes;
                    }
                }

                return Task.FromResult<IReadOnlyList<WorkScheduleOccurrence>>(result);
            }
        }

        public Task<int> DeleteExpiredOccurrences(
            WorkScheduleOccurrenceExpirationRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                var deleted = 0;
                foreach (var values in this.occurrences.Values)
                {
                    deleted += values.RemoveAll(value => deleted < request.MaximumCount && value.ExpiresAt <= request.ExpiresBefore);
                }

                return Task.FromResult(deleted);
            }
        }

        public Task<int> DeleteExpiredSchedules(
            WorkScheduleExpirationRequest request,
            CancellationToken cancellationToken = default)
        {
            lock (this.sync)
            {
                var expired = this.schedules.Values
                    .Where(record => string.Equals(record.Schedule.WorkSystemName, request.WorkSystemName, StringComparison.OrdinalIgnoreCase))
                    .Where(record => record.Schedule.Status switch
                    {
                        WorkScheduleStatus.Completed => record.Schedule.LastRunAt <= request.FinalizedBefore,
                        WorkScheduleStatus.Canceled => record.Schedule.CanceledAt <= request.FinalizedBefore,
                        _ => false,
                    })
                    .OrderBy(record => record.Schedule.CanceledAt ?? record.Schedule.LastRunAt)
                    .Take(request.MaximumCount)
                    .Select(record => record.Schedule.Id)
                    .ToArray();
                foreach (var scheduleId in expired)
                {
                    this.schedules.Remove(scheduleId);
                    this.leases.Remove(scheduleId);
                    this.occurrences.Remove(scheduleId);
                    this.cancellationPayloadReserves.Remove(scheduleId);
                }

                return Task.FromResult(expired.Length);
            }
        }

        private sealed record Lease(Guid Id, DateTimeOffset ExpiresAt, bool DispatchStarted);
    }

    private sealed class NonClaimingScheduleStore(InMemoryScheduleStore inner) : IWorkScheduleStore
    {
        public Task Initialize(WorkScheduleStoreInitializationContext context, CancellationToken cancellationToken = default)
            => inner.Initialize(context, cancellationToken);

        public Task<WorkScheduleStoreCreationStatus> Create(WorkScheduleStoreCreateRequest request, CancellationToken cancellationToken = default)
            => inner.Create(request, cancellationToken);

        public Task<WorkSchedulePersistenceRecord?> Get(WorkScheduleStoreReadRequest request, CancellationToken cancellationToken = default)
            => inner.Get(request, cancellationToken);

        public Task<IReadOnlyList<WorkScheduleSummary>> List(WorkScheduleStoreListRequest request, CancellationToken cancellationToken = default)
            => inner.List(request, cancellationToken);

        public Task<WorkSchedulePersistenceRecord?> Cancel(WorkScheduleStoreCancelRequest request, CancellationToken cancellationToken = default)
            => inner.Cancel(request, cancellationToken);

        public Task<IReadOnlyList<WorkScheduleClaim>> ClaimDue(WorkScheduleClaimRequest request, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<WorkScheduleClaim>>([]);

        public Task<bool> BeginDispatch(WorkScheduleDispatchStart dispatch, CancellationToken cancellationToken = default)
            => inner.BeginDispatch(dispatch, cancellationToken);

        public Task CompleteClaim(WorkScheduleClaimCompletion completion, CancellationToken cancellationToken = default)
            => inner.CompleteClaim(completion, cancellationToken);

        public Task ReleaseClaim(WorkScheduleClaimRelease release, CancellationToken cancellationToken = default)
            => inner.ReleaseClaim(release, cancellationToken);

        public Task<IReadOnlyList<WorkScheduleOccurrence>> ListOccurrences(WorkScheduleOccurrenceReadRequest request, CancellationToken cancellationToken = default)
            => inner.ListOccurrences(request, cancellationToken);

        public Task<int> DeleteExpiredOccurrences(WorkScheduleOccurrenceExpirationRequest request, CancellationToken cancellationToken = default)
            => inner.DeleteExpiredOccurrences(request, cancellationToken);

        public Task<int> DeleteExpiredSchedules(WorkScheduleExpirationRequest request, CancellationToken cancellationToken = default)
            => inner.DeleteExpiredSchedules(request, cancellationToken);

        public Task<IReadOnlyList<WorkScheduleClaim>> ClaimDueAndObserveHost(
            WorkScheduleClaimRequest request,
            WorkScheduleHostObservation observation,
            CancellationToken cancellationToken = default)
        {
            inner.ObserveHost(observation);
            return Task.FromResult<IReadOnlyList<WorkScheduleClaim>>([]);
        }

        public Task EndHost(WorkScheduleHostEnd hostEnd, CancellationToken cancellationToken = default)
            => inner.EndHost(hostEnd, cancellationToken);

        public Task<IReadOnlySet<DateTimeOffset>> FindAvailableTimes(WorkScheduleHostAvailabilityRequest request, CancellationToken cancellationToken = default)
            => inner.FindAvailableTimes(request, cancellationToken);
    }

    private sealed class BlockingClaimScheduleStore(InMemoryScheduleStore inner) : IWorkScheduleStore
    {
        private int blocked;

        public TaskCompletionSource Claimed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ContinueClaim { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Initialize(WorkScheduleStoreInitializationContext context, CancellationToken cancellationToken = default)
            => inner.Initialize(context, cancellationToken);

        public Task<WorkScheduleStoreCreationStatus> Create(
            WorkScheduleStoreCreateRequest request,
            CancellationToken cancellationToken = default)
            => inner.Create(request, cancellationToken);

        public Task<WorkSchedulePersistenceRecord?> Get(
            WorkScheduleStoreReadRequest request,
            CancellationToken cancellationToken = default)
            => inner.Get(request, cancellationToken);

        public Task<IReadOnlyList<WorkScheduleSummary>> List(
            WorkScheduleStoreListRequest request,
            CancellationToken cancellationToken = default)
            => inner.List(request, cancellationToken);

        public Task<WorkSchedulePersistenceRecord?> Cancel(
            WorkScheduleStoreCancelRequest request,
            CancellationToken cancellationToken = default)
            => inner.Cancel(request, cancellationToken);

        public async Task<IReadOnlyList<WorkScheduleClaim>> ClaimDue(
            WorkScheduleClaimRequest request,
            CancellationToken cancellationToken = default)
        {
            var claims = await inner.ClaimDue(request, cancellationToken);
            if (claims.Count > 0 && Interlocked.CompareExchange(ref this.blocked, 1, 0) == 0)
            {
                this.Claimed.TrySetResult();
                await this.ContinueClaim.Task.WaitAsync(cancellationToken);
            }

            return claims;
        }

        public Task<IReadOnlyList<WorkScheduleClaim>> ClaimDueAndObserveHost(
            WorkScheduleClaimRequest request,
            WorkScheduleHostObservation observation,
            CancellationToken cancellationToken = default)
        {
            inner.ObserveHost(observation);
            return this.ClaimDue(request, cancellationToken);
        }

        public Task EndHost(WorkScheduleHostEnd hostEnd, CancellationToken cancellationToken = default)
            => inner.EndHost(hostEnd, cancellationToken);

        public Task<IReadOnlySet<DateTimeOffset>> FindAvailableTimes(
            WorkScheduleHostAvailabilityRequest request,
            CancellationToken cancellationToken = default)
            => inner.FindAvailableTimes(request, cancellationToken);

        public Task CompleteClaim(WorkScheduleClaimCompletion completion, CancellationToken cancellationToken = default)
            => inner.CompleteClaim(completion, cancellationToken);

        public Task<bool> BeginDispatch(WorkScheduleDispatchStart dispatch, CancellationToken cancellationToken = default)
            => inner.BeginDispatch(dispatch, cancellationToken);

        public Task ReleaseClaim(WorkScheduleClaimRelease release, CancellationToken cancellationToken = default)
            => inner.ReleaseClaim(release, cancellationToken);

        public Task<IReadOnlyList<WorkScheduleOccurrence>> ListOccurrences(
            WorkScheduleOccurrenceReadRequest request,
            CancellationToken cancellationToken = default)
            => inner.ListOccurrences(request, cancellationToken);

        public Task<int> DeleteExpiredOccurrences(
            WorkScheduleOccurrenceExpirationRequest request,
            CancellationToken cancellationToken = default)
            => inner.DeleteExpiredOccurrences(request, cancellationToken);

        public Task<int> DeleteExpiredSchedules(
            WorkScheduleExpirationRequest request,
            CancellationToken cancellationToken = default)
            => inner.DeleteExpiredSchedules(request, cancellationToken);
    }

    private sealed class BlockingDispatchStartScheduleStore(InMemoryScheduleStore inner) : IWorkScheduleStore
    {
        private int blocked;

        public TaskCompletionSource DispatchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ContinueDispatch { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Initialize(WorkScheduleStoreInitializationContext context, CancellationToken cancellationToken = default)
            => inner.Initialize(context, cancellationToken);

        public Task<WorkScheduleStoreCreationStatus> Create(
            WorkScheduleStoreCreateRequest request,
            CancellationToken cancellationToken = default)
            => inner.Create(request, cancellationToken);

        public Task<WorkSchedulePersistenceRecord?> Get(
            WorkScheduleStoreReadRequest request,
            CancellationToken cancellationToken = default)
            => inner.Get(request, cancellationToken);

        public Task<IReadOnlyList<WorkScheduleSummary>> List(
            WorkScheduleStoreListRequest request,
            CancellationToken cancellationToken = default)
            => inner.List(request, cancellationToken);

        public Task<WorkSchedulePersistenceRecord?> Cancel(
            WorkScheduleStoreCancelRequest request,
            CancellationToken cancellationToken = default)
            => inner.Cancel(request, cancellationToken);

        public Task<IReadOnlyList<WorkScheduleClaim>> ClaimDue(
            WorkScheduleClaimRequest request,
            CancellationToken cancellationToken = default)
            => inner.ClaimDue(request, cancellationToken);

        public Task<IReadOnlyList<WorkScheduleClaim>> ClaimDueAndObserveHost(
            WorkScheduleClaimRequest request,
            WorkScheduleHostObservation observation,
            CancellationToken cancellationToken = default)
            => inner.ClaimDueAndObserveHost(request, observation, cancellationToken);

        public Task EndHost(WorkScheduleHostEnd hostEnd, CancellationToken cancellationToken = default)
            => inner.EndHost(hostEnd, cancellationToken);

        public Task<IReadOnlySet<DateTimeOffset>> FindAvailableTimes(
            WorkScheduleHostAvailabilityRequest request,
            CancellationToken cancellationToken = default)
            => inner.FindAvailableTimes(request, cancellationToken);

        public async Task<bool> BeginDispatch(
            WorkScheduleDispatchStart dispatch,
            CancellationToken cancellationToken = default)
        {
            var began = await inner.BeginDispatch(dispatch, cancellationToken);
            if (began && Interlocked.CompareExchange(ref this.blocked, 1, 0) == 0)
            {
                this.DispatchStarted.TrySetResult();
                await this.ContinueDispatch.Task.WaitAsync(cancellationToken);
            }

            return began;
        }

        public Task CompleteClaim(WorkScheduleClaimCompletion completion, CancellationToken cancellationToken = default)
            => inner.CompleteClaim(completion, cancellationToken);

        public Task ReleaseClaim(WorkScheduleClaimRelease release, CancellationToken cancellationToken = default)
            => inner.ReleaseClaim(release, cancellationToken);

        public Task<IReadOnlyList<WorkScheduleOccurrence>> ListOccurrences(
            WorkScheduleOccurrenceReadRequest request,
            CancellationToken cancellationToken = default)
            => inner.ListOccurrences(request, cancellationToken);

        public Task<int> DeleteExpiredOccurrences(
            WorkScheduleOccurrenceExpirationRequest request,
            CancellationToken cancellationToken = default)
            => inner.DeleteExpiredOccurrences(request, cancellationToken);

        public Task<int> DeleteExpiredSchedules(
            WorkScheduleExpirationRequest request,
            CancellationToken cancellationToken = default)
            => inner.DeleteExpiredSchedules(request, cancellationToken);
    }

    private sealed class ConcurrentDispatchStartScheduleStore(
        InMemoryScheduleStore inner,
        int expectedDispatches) : IWorkScheduleStore
    {
        private int activeDispatches;
        private int maximumConcurrentDispatches;
        private int startedDispatches;

        public TaskCompletionSource ExpectedDispatchesStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ContinueDispatches { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SubsequentBatchStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int MaximumConcurrentDispatches => Volatile.Read(ref this.maximumConcurrentDispatches);

        public Task Initialize(WorkScheduleStoreInitializationContext context, CancellationToken cancellationToken = default)
            => inner.Initialize(context, cancellationToken);

        public Task<WorkScheduleStoreCreationStatus> Create(
            WorkScheduleStoreCreateRequest request,
            CancellationToken cancellationToken = default)
            => inner.Create(request, cancellationToken);

        public Task<WorkSchedulePersistenceRecord?> Get(
            WorkScheduleStoreReadRequest request,
            CancellationToken cancellationToken = default)
            => inner.Get(request, cancellationToken);

        public Task<IReadOnlyList<WorkScheduleSummary>> List(
            WorkScheduleStoreListRequest request,
            CancellationToken cancellationToken = default)
            => inner.List(request, cancellationToken);

        public Task<WorkSchedulePersistenceRecord?> Cancel(
            WorkScheduleStoreCancelRequest request,
            CancellationToken cancellationToken = default)
            => inner.Cancel(request, cancellationToken);

        public Task<IReadOnlyList<WorkScheduleClaim>> ClaimDue(
            WorkScheduleClaimRequest request,
            CancellationToken cancellationToken = default)
            => inner.ClaimDue(request, cancellationToken);

        public Task<IReadOnlyList<WorkScheduleClaim>> ClaimDueAndObserveHost(
            WorkScheduleClaimRequest request,
            WorkScheduleHostObservation observation,
            CancellationToken cancellationToken = default)
            => inner.ClaimDueAndObserveHost(request, observation, cancellationToken);

        public Task EndHost(WorkScheduleHostEnd hostEnd, CancellationToken cancellationToken = default)
            => inner.EndHost(hostEnd, cancellationToken);

        public Task<IReadOnlySet<DateTimeOffset>> FindAvailableTimes(
            WorkScheduleHostAvailabilityRequest request,
            CancellationToken cancellationToken = default)
            => inner.FindAvailableTimes(request, cancellationToken);

        public async Task<bool> BeginDispatch(
            WorkScheduleDispatchStart dispatch,
            CancellationToken cancellationToken = default)
        {
            var began = await inner.BeginDispatch(dispatch, cancellationToken);
            if (!began)
            {
                return false;
            }

            var active = Interlocked.Increment(ref this.activeDispatches);
            UpdateMaximum(ref this.maximumConcurrentDispatches, active);
            var started = Interlocked.Increment(ref this.startedDispatches);
            if (started == expectedDispatches)
            {
                this.ExpectedDispatchesStarted.TrySetResult();
            }
            else if (started > expectedDispatches)
            {
                this.SubsequentBatchStarted.TrySetResult();
            }

            try
            {
                await this.ContinueDispatches.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref this.activeDispatches);
            }

            return true;
        }

        public Task CompleteClaim(WorkScheduleClaimCompletion completion, CancellationToken cancellationToken = default)
            => inner.CompleteClaim(completion, cancellationToken);

        public Task ReleaseClaim(WorkScheduleClaimRelease release, CancellationToken cancellationToken = default)
            => inner.ReleaseClaim(release, cancellationToken);

        public Task<IReadOnlyList<WorkScheduleOccurrence>> ListOccurrences(
            WorkScheduleOccurrenceReadRequest request,
            CancellationToken cancellationToken = default)
            => inner.ListOccurrences(request, cancellationToken);

        public Task<int> DeleteExpiredOccurrences(
            WorkScheduleOccurrenceExpirationRequest request,
            CancellationToken cancellationToken = default)
            => inner.DeleteExpiredOccurrences(request, cancellationToken);

        public Task<int> DeleteExpiredSchedules(
            WorkScheduleExpirationRequest request,
            CancellationToken cancellationToken = default)
            => inner.DeleteExpiredSchedules(request, cancellationToken);

        private static void UpdateMaximum(ref int maximum, int candidate)
        {
            while (true)
            {
                var current = Volatile.Read(ref maximum);
                if (candidate <= current || Interlocked.CompareExchange(ref maximum, candidate, current) == current)
                {
                    return;
                }
            }
        }
    }

    private sealed class FaultInjectingScheduleStore(InMemoryScheduleStore inner) : IWorkScheduleStore
    {
        private int initializeFailures;
        private int createFailures;
        private int claimFailures;
        private int completeFailures;
        private int cleanupFailures;
        private int fullOccurrenceCleanupBatches;
        private int initializeCalls;
        private int createCalls;
        private int claimCalls;
        private int completeCalls;
        private int cleanupCalls;

        public InMemoryScheduleStore Inner { get; } = inner;

        public int InitializeFailures { get => Volatile.Read(ref this.initializeFailures); set => Volatile.Write(ref this.initializeFailures, value); }

        public int CreateFailures { get => Volatile.Read(ref this.createFailures); set => Volatile.Write(ref this.createFailures, value); }

        public int ClaimFailures { get => Volatile.Read(ref this.claimFailures); set => Volatile.Write(ref this.claimFailures, value); }

        public int CompleteFailures { get => Volatile.Read(ref this.completeFailures); set => Volatile.Write(ref this.completeFailures, value); }

        public int CleanupFailures { get => Volatile.Read(ref this.cleanupFailures); set => Volatile.Write(ref this.cleanupFailures, value); }

        public int FullOccurrenceCleanupBatches
        {
            get => Volatile.Read(ref this.fullOccurrenceCleanupBatches);
            set => Volatile.Write(ref this.fullOccurrenceCleanupBatches, value);
        }

        public int InitializeCalls => Volatile.Read(ref this.initializeCalls);

        public int CreateCalls => Volatile.Read(ref this.createCalls);

        public int ClaimCalls => Volatile.Read(ref this.claimCalls);

        public int CompleteCalls => Volatile.Read(ref this.completeCalls);

        public int CleanupCalls => Volatile.Read(ref this.cleanupCalls);

        public WorkScheduleStoreOverviewResult? OverviewResultOverride { get; set; }

        public Task Initialize(WorkScheduleStoreInitializationContext context, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.initializeCalls);
            ThrowIfConsumed(ref this.initializeFailures, "initialize");
            return this.Inner.Initialize(context, cancellationToken);
        }

        public Task<WorkScheduleStoreCreationStatus> Create(
            WorkScheduleStoreCreateRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.createCalls);
            ThrowIfConsumed(ref this.createFailures, "create");
            return this.Inner.Create(request, cancellationToken);
        }

        public Task<WorkSchedulePersistenceRecord?> Get(
            WorkScheduleStoreReadRequest request,
            CancellationToken cancellationToken = default)
            => this.Inner.Get(request, cancellationToken);

        public Task<IReadOnlyList<WorkScheduleSummary>> List(
            WorkScheduleStoreListRequest request,
            CancellationToken cancellationToken = default)
            => this.Inner.List(request, cancellationToken);

        public Task<WorkScheduleStoreOverviewResult> GetOverview(
            WorkScheduleStoreOverviewRequest request,
            CancellationToken cancellationToken = default)
            => this.OverviewResultOverride is { } result
                ? Task.FromResult(result)
                : ((IWorkScheduleStore)this.Inner).GetOverview(request, cancellationToken);

        public Task<WorkSchedulePersistenceRecord?> Cancel(
            WorkScheduleStoreCancelRequest request,
            CancellationToken cancellationToken = default)
            => this.Inner.Cancel(request, cancellationToken);

        public Task<IReadOnlyList<WorkScheduleClaim>> ClaimDue(
            WorkScheduleClaimRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.claimCalls);
            ThrowIfConsumed(ref this.claimFailures, "claim");
            return this.Inner.ClaimDue(request, cancellationToken);
        }

        public Task<IReadOnlyList<WorkScheduleClaim>> ClaimDueAndObserveHost(
            WorkScheduleClaimRequest request,
            WorkScheduleHostObservation observation,
            CancellationToken cancellationToken = default)
        {
            this.Inner.ObserveHost(observation);
            return this.ClaimDue(request, cancellationToken);
        }

        public Task EndHost(WorkScheduleHostEnd hostEnd, CancellationToken cancellationToken = default)
            => this.Inner.EndHost(hostEnd, cancellationToken);

        public Task<IReadOnlySet<DateTimeOffset>> FindAvailableTimes(
            WorkScheduleHostAvailabilityRequest request,
            CancellationToken cancellationToken = default)
            => this.Inner.FindAvailableTimes(request, cancellationToken);

        public Task CompleteClaim(WorkScheduleClaimCompletion completion, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.completeCalls);
            ThrowIfConsumed(ref this.completeFailures, "complete");
            return this.Inner.CompleteClaim(completion, cancellationToken);
        }

        public Task<bool> BeginDispatch(WorkScheduleDispatchStart dispatch, CancellationToken cancellationToken = default)
            => this.Inner.BeginDispatch(dispatch, cancellationToken);

        public Task ReleaseClaim(WorkScheduleClaimRelease release, CancellationToken cancellationToken = default)
            => this.Inner.ReleaseClaim(release, cancellationToken);

        public Task<IReadOnlyList<WorkScheduleOccurrence>> ListOccurrences(
            WorkScheduleOccurrenceReadRequest request,
            CancellationToken cancellationToken = default)
            => this.Inner.ListOccurrences(request, cancellationToken);

        public Task<int> DeleteExpiredOccurrences(
            WorkScheduleOccurrenceExpirationRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.cleanupCalls);
            ThrowIfConsumed(ref this.cleanupFailures, "cleanup");
            if (TryConsume(ref this.fullOccurrenceCleanupBatches))
            {
                return Task.FromResult(request.MaximumCount);
            }

            return this.Inner.DeleteExpiredOccurrences(request, cancellationToken);
        }

        public Task<int> DeleteExpiredSchedules(
            WorkScheduleExpirationRequest request,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref this.cleanupCalls);
            ThrowIfConsumed(ref this.cleanupFailures, "cleanup");
            return this.Inner.DeleteExpiredSchedules(request, cancellationToken);
        }

        private static void ThrowIfConsumed(ref int remaining, string operation)
        {
            while (true)
            {
                var current = Volatile.Read(ref remaining);
                if (current == 0)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref remaining, current - 1, current) == current)
                {
                    throw new InvalidOperationException($"Injected schedule-store {operation} failure.");
                }
            }
        }

        private static bool TryConsume(ref int remaining)
        {
            while (true)
            {
                var current = Volatile.Read(ref remaining);
                if (current == 0)
                {
                    return false;
                }

                if (Interlocked.CompareExchange(ref remaining, current - 1, current) == current)
                {
                    return true;
                }
            }
        }
    }
}
