using Microsoft.Extensions.DependencyInjection;
using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Views")]
public sealed class WorkableViewQueryAdapterTests
{
    [Fact]
    public void RequiresIntervalPublishReturnsTrueForThroughputComponents()
    {
        var adapter = new WorkableViewQueryAdapter();

        var requiresIntervalPublish = adapter.RequiresIntervalPublish(
            "overview",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest("system", "system"),
                new WorkComponentRequest("throughput", "throughput"),
            ]));

        Assert.True(requiresIntervalPublish);
    }

    [Fact]
    public void RequiresIntervalPublishReturnsFalseForStateBasedOverviewComponents()
    {
        var adapter = new WorkableViewQueryAdapter();

        var requiresIntervalPublish = adapter.RequiresIntervalPublish(
            "overview",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest("system", "system"),
                new WorkComponentRequest("workers", "workers"),
            ]));

        Assert.False(requiresIntervalPublish);
    }

    [Fact]
    public void NormalizeViewCriteriaReturnsDefaultWorkerComponents()
    {
        var adapter = new WorkableViewQueryAdapter();

        var criteria = adapter.NormalizeViewCriteria("worker");
        var components = Assert.IsAssignableFrom<IReadOnlyList<WorkComponentRequest>>(criteria.Components);

        Assert.Equal(["worker", "currentIteration"], components.Select(component => component.Id).ToArray());
        Assert.Equal(["workerDetail", "workerCurrentIteration"], components.Select(component => component.Type).ToArray());
        Assert.All(components, component => Assert.Equal(WorkComponentShapes.Detailed, component.Shape));
    }

    [Fact]
    public void ShouldPublishForChangesMatchesWorkerDetailByWorkerId()
    {
        var adapter = new WorkableViewQueryAdapter();
        var workerId = WorkerId.New();
        var criteria = new WorkViewCriteria(Components:
        [
            new WorkComponentRequest(
                "worker",
                "workerDetail",
                JsonSerializer.SerializeToElement(new
                {
                    workerId = workerId.Value,
                })),
        ]);

        Assert.True(adapter.ShouldPublishForChanges(
            "worker",
            criteria,
            [WorkChangeKey.Worker(workerId)]));
        Assert.False(adapter.ShouldPublishForChanges(
            "worker",
            criteria,
            [WorkChangeKey.Worker(WorkerId.New())]));
    }

    [Fact]
    public void ShouldPublishForChangesMatchesWorkerGridByStructuredKey()
    {
        var adapter = new WorkableViewQueryAdapter();
        var subject = new WorkSubjectId("invoice", "inv-100");
        var criteria = new WorkViewCriteria(Components:
        [
            new WorkComponentRequest(
                "workers",
                "workerGrid",
                JsonSerializer.SerializeToElement(new
                {
                    keyKind = WorkKeyKind.Subject,
                    keyType = subject.Type,
                    keyValue = subject.Value,
                })),
        ]);

        Assert.True(adapter.ShouldPublishForChanges(
            "workers",
            criteria,
            [WorkChangeKey.Subject(subject)]));
        Assert.False(adapter.ShouldPublishForChanges(
            "workers",
            criteria,
            [WorkChangeKey.Subject(new WorkSubjectId(subject.Type, "inv-200"))]));
    }

    [Fact]
    public void ShouldPublishForChangesMatchesWorkerGridByOriginActor()
    {
        var adapter = new WorkableViewQueryAdapter();
        const string actorId = "user-123";
        var criteria = new WorkViewCriteria(Components:
        [
            new WorkComponentRequest(
                "workers",
                "workerGrid",
                JsonSerializer.SerializeToElement(new { actorId })),
        ]);

        Assert.True(adapter.ShouldPublishForChanges(
            "workers",
            criteria,
            [WorkChangeKey.Actor(actorId)]));
        Assert.False(adapter.ShouldPublishForChanges(
            "workers",
            criteria,
            [WorkChangeKey.Actor("user-456")]));
        Assert.True(adapter.ShouldPublishForChanges(
            "workers",
            criteria,
            [WorkChangeKey.System()]));
    }

    [Fact]
    public void ShouldPublishForChangesMatchesDefinitionScopedViewsByDefinition()
    {
        var adapter = new WorkableViewQueryAdapter();
        var criteria = new WorkViewCriteria(
            new WorkSystemCriteria(DefinitionName: "billing.close"),
            [
                new WorkComponentRequest("workers", "workers", Shape: WorkComponentShapes.Compact),
            ]);

        Assert.True(adapter.ShouldPublishForChanges(
            "overview",
            criteria,
            [WorkChangeKey.Definition("billing.close")]));
        Assert.False(adapter.ShouldPublishForChanges(
            "overview",
            criteria,
            [WorkChangeKey.Definition("shipping.close")]));
    }

    [Fact]
    public void ShouldPublishForChangesKeepsGlobalOverviewSubscribedToWorkerState()
    {
        var adapter = new WorkableViewQueryAdapter();
        var criteria = new WorkViewCriteria(Components:
        [
            new WorkComponentRequest("workers", "workers", Shape: WorkComponentShapes.Compact),
        ]);

        Assert.True(adapter.ShouldPublishForChanges(
            "overview",
            criteria,
            [WorkChangeKey.Worker(WorkerId.New())]));
    }

    [Fact]
    public void ShouldPublishForChangesKeepsDiagnosticsComponentsConservativeInCustomViews()
    {
        var adapter = new WorkableViewQueryAdapter();
        var criteria = new WorkViewCriteria(Components:
        [
            new WorkComponentRequest("queueDiagnostics", "queueDiagnostics", Shape: WorkComponentShapes.Compact),
        ]);

        Assert.True(adapter.ShouldPublishForChanges(
            "overview",
            criteria,
            [WorkChangeKey.Worker(WorkerId.New())]));
    }

    [Fact]
    public void ShouldPublishForChangesFallsBackToPublishForMalformedComponentOptions()
    {
        var adapter = new WorkableViewQueryAdapter();
        using var document = JsonDocument.Parse("""{"keyKind": "not-a-kind"}""");
        var criteria = new WorkViewCriteria(Components:
        [
            new WorkComponentRequest(
                "workers",
                "workerGrid",
                document.RootElement.Clone()),
        ]);

        Assert.True(adapter.ShouldPublishForChanges(
            "workers",
            criteria,
            [WorkChangeKey.Worker(WorkerId.New())]));
    }

    [Fact]
    public void ShouldPublishForChangesSkipsEmptyAndIntervalOnlyChanges()
    {
        var adapter = new WorkableViewQueryAdapter();
        var throughput = new WorkViewCriteria(Components:
        [
            new WorkComponentRequest("throughput", "throughput"),
        ]);

        Assert.False(adapter.ShouldPublishForChanges("overview", null, []));
        Assert.False(adapter.ShouldPublishForChanges(
            "overview",
            throughput,
            [WorkChangeKey.Worker(WorkerId.New())]));
    }

    [Fact]
    public void ShouldPublishForChangesUsesConservativeFallbacksForUnknownViewsAndSystemChanges()
    {
        var adapter = new WorkableViewQueryAdapter();
        var unknownComponent = new WorkViewCriteria(Components:
        [
            new WorkComponentRequest("future", "futureComponent"),
        ]);
        var systemComponent = new WorkViewCriteria(Components:
        [
            new WorkComponentRequest("system", "system"),
        ]);

        Assert.True(adapter.ShouldPublishForChanges(
            "future-view",
            null,
            [WorkChangeKey.Worker(WorkerId.New())]));
        Assert.False(adapter.ShouldPublishForChanges(
            "overview",
            unknownComponent,
            [WorkChangeKey.Worker(WorkerId.New())]));
        Assert.True(adapter.ShouldPublishForChanges(
            "overview",
            unknownComponent,
            [WorkChangeKey.System()]));
        Assert.False(adapter.ShouldPublishForChanges(
            "overview",
            systemComponent,
            [WorkChangeKey.Diagnostics("queue")]));
        Assert.True(adapter.ShouldPublishForChanges(
            "overview",
            systemComponent,
            [WorkChangeKey.System()]));
    }

    [Fact]
    public void ShouldPublishForChangesScopesCatalogAndWorkerComponentsToDefinitionSets()
    {
        var adapter = new WorkableViewQueryAdapter();
        var scope = new WorkSystemCriteria(DefinitionNames: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "billing.close",
            "billing.refund",
        });
        var catalog = new WorkViewCriteria(scope,
        [
            new WorkComponentRequest("catalog", "catalog"),
        ]);
        var workers = new WorkViewCriteria(scope,
        [
            new WorkComponentRequest("workers", "workers"),
        ]);

        Assert.True(adapter.ShouldPublishForChanges(
            "overview",
            catalog,
            [WorkChangeKey.Definition("billing.refund")]));
        Assert.False(adapter.ShouldPublishForChanges(
            "overview",
            catalog,
            [WorkChangeKey.Definition("shipping.close")]));
        Assert.True(adapter.ShouldPublishForChanges(
            "overview",
            catalog,
            [WorkChangeKey.System()]));
        Assert.True(adapter.ShouldPublishForChanges(
            "overview",
            workers,
            [WorkChangeKey.Definition("billing.close")]));
        Assert.False(adapter.ShouldPublishForChanges(
            "overview",
            workers,
            [WorkChangeKey.Definition("shipping.close")]));
        Assert.True(adapter.ShouldPublishForChanges(
            "overview",
            workers,
            [WorkChangeKey.System()]));
    }

    [Theory]
    [InlineData("workerGrid", WorkKeyKind.Subject)]
    [InlineData("workerGrid", WorkKeyKind.ConcurrencyKey)]
    [InlineData("workerGrid", WorkKeyKind.Identifier)]
    [InlineData("iterationGrid", WorkKeyKind.Subject)]
    [InlineData("iterationGrid", WorkKeyKind.ConcurrencyKey)]
    [InlineData("iterationGrid", WorkKeyKind.Identifier)]
    public void ShouldPublishForChangesMatchesEveryStructuredGridKey(
        string componentType,
        WorkKeyKind keyKind)
    {
        var adapter = new WorkableViewQueryAdapter();
        const string keyType = "invoice";
        const string keyValue = "inv-100";
        var criteria = new WorkViewCriteria(Components:
        [
            new WorkComponentRequest(
                "grid",
                componentType,
                JsonSerializer.SerializeToElement(new { keyKind, keyType, keyValue })),
        ]);
        var matching = CreateStructuredChange(keyKind, keyType, keyValue);
        var unrelated = CreateStructuredChange(keyKind, keyType, "inv-200");

        Assert.True(adapter.ShouldPublishForChanges("overview", criteria, [matching]));
        Assert.False(adapter.ShouldPublishForChanges("overview", criteria, [unrelated]));
        Assert.True(adapter.ShouldPublishForChanges("overview", criteria, [WorkChangeKey.System()]));
    }

    [Fact]
    public void ShouldPublishWorkerComponentsConservativelyForUnknownSystemChanges()
    {
        var adapter = new WorkableViewQueryAdapter();
        var workerId = WorkerId.New();
        var criteria = new WorkViewCriteria(Components:
        [
            new WorkComponentRequest(
                "worker",
                "workerCurrentIteration",
                JsonSerializer.SerializeToElement(new { workerId = workerId.Value })),
        ]);

        Assert.True(adapter.ShouldPublishForChanges("worker", criteria, [WorkChangeKey.System()]));
        Assert.False(adapter.ShouldPublishForChanges(
            "worker",
            criteria,
            [WorkChangeKey.Worker(WorkerId.New())]));
    }

    [Fact]
    public async Task ReturnNullForMissingWorkerAndIterationDetailSections()
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.RequireAuthorization(false));
        await using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var session = await system.CreateSession(WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var adapter = new WorkableViewQueryAdapter();
        var workerId = WorkerId.New();
        var iteration = new WorkerIterationReference(workerId, 1);

        Assert.Null(await adapter.WorkerIterationMessages(session, iteration));
        Assert.Null(await adapter.WorkerIterationLogs(session, iteration));
        Assert.Null(await adapter.WorkerOverview(session, workerId));
        Assert.Null(await adapter.WorkerOverviewLogs(session, workerId));
        Assert.Null(await adapter.WorkerOverviewTimeline(session, workerId));
    }

    [Fact]
    public async Task WorkerOverviewReturnsLogsPageForLogsInitialPanel()
    {
        var definition = WorkDefinition.Create("views.worker.landing.logs", "Returns worker overview logs.");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork<LandingLoggedExecutor>(
            definition,
            configuration => configuration.ConfigureLogging(level: LogLevel.Information)));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var session = await CreateTransportSession(
            system,
            WorkInvocationChannel.HttpApi,
            new WorkActor("view-user", "View Tester"));
        var handle = await session.Queue.Enqueue(
            definition.Name,
            options: new WorkerOptions(ProfilingEnabled: true));
        await handle.WaitForCompletion();
        await WaitForReadModel(system);

        var landing = await new WorkableViewQueryAdapter().WorkerOverview(
            session,
            RequiredWorkerId(handle),
            new WorkWorkerOverviewCriteria(
                Activity: WorkWorkerOverviewActivity.Logs,
                ActivityTake: 10,
                RecentIterationTake: 5));

        Assert.NotNull(landing);
        Assert.Equal(WorkWorkerOverviewActivity.Logs, landing.Activity);
        Assert.Equal(WorkInvocationChannel.HttpApi, landing.Worker.CreatedOrigin.Channel);
        Assert.Equal(WorkOriginSurface.HostApplication, landing.Worker.CreatedOrigin.Surface);
        Assert.Equal("view-user", landing.Worker.CreatedOrigin.ActorId);
        Assert.Equal("View Tester", landing.Worker.CreatedOrigin.ActorName);
        Assert.Equal(1, landing.Worker.ConfigDifferenceCount);
        Assert.True(landing.Worker.IsFinal);
        Assert.NotNull(landing.Logs.Page);
        Assert.Null(landing.Timeline.Page);
        Assert.Equal(3, landing.Logs.Summary.Total);
        Assert.Equal(0, landing.Logs.Summary.Critical);
        Assert.Equal(1, landing.Logs.Summary.Error);
        Assert.Equal(1, landing.Logs.Summary.Errors);
        Assert.Equal(1, landing.Logs.Summary.Warning);
        Assert.Equal(1, landing.Logs.Summary.Warnings);
        Assert.Equal(1, landing.Logs.Summary.Information);
        Assert.Equal(0, landing.Logs.Summary.Debug);
        Assert.Equal(0, landing.Logs.Summary.Trace);
        Assert.Equal(WorkCompletionStatus.Completed, Assert.IsType<WorkWorkerOverviewLatestIteration>(landing.LatestIteration).Status);
        Assert.Single(landing.RecentIterations);
    }

    [Fact]
    public async Task WorkerOverviewReturnsTimelinePageAndFailureDetailsForTimelineInitialPanel()
    {
        var definition = WorkDefinition.Create("views.worker.landing.timeline", "Returns worker overview timeline.");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork<LandingFailingExecutor>(
            definition,
            configuration => configuration.ConfigureLogging(level: LogLevel.Information)));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var session = await CreateTransportSession(system);
        var handle = await session.Queue.Enqueue(
            definition.Name,
            options: new WorkerOptions(ProfilingEnabled: true));
        await handle.WaitForCompletion();
        await WaitForReadModel(system);

        var landing = await new WorkableViewQueryAdapter().WorkerOverview(
            session,
            RequiredWorkerId(handle),
            new WorkWorkerOverviewCriteria(
                Activity: WorkWorkerOverviewActivity.Timeline,
                ActivityTake: 10,
                RecentIterationTake: 5));

        var latestIteration = Assert.IsType<WorkWorkerOverviewLatestIteration>(landing?.LatestIteration);

        Assert.NotNull(landing);
        var nonNullLanding = landing!;
        Assert.Equal(WorkWorkerOverviewActivity.Timeline, nonNullLanding.Activity);
        Assert.Null(nonNullLanding.Logs.Page);
        Assert.NotNull(nonNullLanding.Timeline.Page);
        Assert.Single(nonNullLanding.Timeline.Page.Items);
        Assert.Equal(1, nonNullLanding.Timeline.Summary.Total);
        Assert.Equal(1, nonNullLanding.Timeline.Summary.FailureCount);
        Assert.Equal(0, nonNullLanding.Timeline.Summary.UserActionCount);
        Assert.Equal(WorkCompletionStatus.Failed, latestIteration.Status);
        Assert.NotNull(latestIteration.Failure);
        Assert.Equal(WorkWorkerOverviewFailureKind.Failure, latestIteration.Failure.Kind);
        Assert.Equal("view.failure", latestIteration.Failure.Code);
        Assert.Equal("The work failed.", latestIteration.Failure.Message);
        Assert.Equal(WorkCompletionStatus.Failed, Assert.Single(nonNullLanding.RecentIterations).Status);
    }

    [Fact]
    public async Task WorkerOverviewReturnsExecutingLatestIterationForActiveWorker()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var definition = WorkDefinition.Create("views.worker.landing.active", "Returns worker overview for active workers.");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork(
            definition,
            async (_, _, cancellationToken) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
                return WorkExecutionResult.Success();
            }));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var session = await CreateTransportSession(system);
        var handle = await session.Queue.Enqueue(definition.Name);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(1));

        var landing = await new WorkableViewQueryAdapter().WorkerOverview(
            session,
            RequiredWorkerId(handle),
            new WorkWorkerOverviewCriteria(
                Activity: WorkWorkerOverviewActivity.Timeline,
                ActivityTake: 10,
                RecentIterationTake: 5));

        release.TrySetResult();
        await handle.WaitForCompletion();

        var latestIteration = Assert.IsType<WorkWorkerOverviewLatestIteration>(landing?.LatestIteration);

        Assert.NotNull(landing);
        var nonNullLanding = landing!;
        Assert.Equal(WorkCompletionStatus.Executing, latestIteration.Status);
        Assert.Null(latestIteration.CompletedAt);
        Assert.Null(latestIteration.ExecutionDuration);
        Assert.NotNull(nonNullLanding.Timeline.Page);
        Assert.Contains(nonNullLanding.Timeline.Page.Items, item =>
            item.Kind == WorkWorkerOverviewTimelineItemKind.Iteration &&
            item.Sequence == 1 &&
            item.IterationStatus == WorkCompletionStatus.Executing);
        Assert.Equal(WorkCompletionStatus.Executing, Assert.Single(nonNullLanding.RecentIterations).Status);
    }

    [Fact]
    public async Task WorkerOverviewSynthesizesWaitingStateFromWorkerSnapshot()
    {
        var definition = WorkDefinition.Create("views.worker.timeline.waiting", "Synthesizes waiting timeline state.");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork(
            definition,
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration.UseRecurrence(WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(5)) with
            {
                ContinueAfterFailure = false,
                RetainedIterations = 10,
            })));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var session = await CreateTransportSession(system);
        var handle = await session.Queue.Enqueue(definition.Name);
        var workerId = RequiredWorkerId(handle);
        try
        {
            await TestEventually.Until(async () => (await system.Query.Worker(workerId))?.State == WorkerState.Waiting);

            var overview = await new WorkableViewQueryAdapter().WorkerOverview(
                session,
                workerId,
                new WorkWorkerOverviewCriteria(
                    Activity: WorkWorkerOverviewActivity.Timeline,
                    ActivityTake: 10));

            var waitingItem = Assert.Single(
                Assert.IsType<WorkWorkerOverviewPage<WorkWorkerOverviewTimelineItem>>(overview?.Timeline.Page).Items,
                item => item.Kind == WorkWorkerOverviewTimelineItemKind.StateChange &&
                    item.State == WorkerState.Waiting);

            Assert.Equal("live-state:waiting", waitingItem.Id);
            Assert.NotNull(waitingItem.PendingState);
            Assert.Equal(WorkWorkerOverviewPendingStateMode.Recurrence, waitingItem.PendingState.Mode);
        }
        finally
        {
            var worker = await system.Query.Worker(workerId);
            if (worker is not null && worker.State is not (WorkerState.Canceled or WorkerState.Completed))
            {
                await system.Workers.Execute(worker.Version, WorkAction.Cancel);
            }
        }

        await handle.WaitForCompletion();
    }

    [Fact]
    public async Task WorkerOverviewAttachesRetryPendingToFailedIterationInsteadOfRetryingRow()
    {
        var attempts = 0;
        var definition = WorkDefinition.Create("views.worker.timeline.retrying", "Attaches retry pending state.");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork(
            definition,
            (_, _, _) =>
            {
                attempts++;
                throw new TimeoutException("Retry this.");
            },
            configuration => configuration
                .RetryTransientFailures(
                    count: 1,
                    initialDelay: TimeSpan.FromMinutes(5),
                    maximumDelay: TimeSpan.FromMinutes(5),
                    jitter: TimeSpan.Zero)
                .ClassifyExceptions(exception => exception is TimeoutException
                    ? WorkExceptionClassification.Transient
                    : WorkExceptionClassification.Unknown)));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var session = await CreateTransportSession(system);
        var handle = await session.Queue.Enqueue(definition.Name);
        var workerId = RequiredWorkerId(handle);
        try
        {
            await TestEventually.Until(async () => (await system.Query.Worker(workerId))?.State == WorkerState.Retrying);

            var overview = await new WorkableViewQueryAdapter().WorkerOverview(
                session,
                workerId,
                new WorkWorkerOverviewCriteria(
                    Activity: WorkWorkerOverviewActivity.Timeline,
                    ActivityTake: 20));

            Assert.Equal(1, attempts);
            var latestIteration = Assert.IsType<WorkWorkerOverviewLatestIteration>(overview?.LatestIteration);
            Assert.NotNull(latestIteration.Failure);
            Assert.NotNull(latestIteration.Failure.PendingState);
            Assert.Equal(WorkWorkerOverviewPendingStateMode.Retry, latestIteration.Failure.PendingState.Mode);

            var timelinePage = Assert.IsType<WorkWorkerOverviewPage<WorkWorkerOverviewTimelineItem>>(overview?.Timeline.Page);
            var failedIteration = Assert.Single(timelinePage.Items, item =>
                item.Kind == WorkWorkerOverviewTimelineItemKind.Iteration &&
                item.Sequence == latestIteration.Sequence);
            var failedPendingState = failedIteration.Failure?.PendingState;
            Assert.NotNull(failedPendingState);
            Assert.Equal(WorkWorkerOverviewPendingStateMode.Retry, failedPendingState!.Mode);
            Assert.DoesNotContain(timelinePage.Items, item =>
                item.Kind == WorkWorkerOverviewTimelineItemKind.StateChange &&
                item.State == WorkerState.Retrying);
        }
        finally
        {
            var worker = await system.Query.Worker(workerId);
            if (worker is not null && worker.State is not (WorkerState.Canceled or WorkerState.Completed))
            {
                await system.Workers.Execute(worker.Version, WorkAction.Cancel);
            }
        }

        var completion = await handle.WaitForCompletion();
        Assert.Equal(WorkCompletionStatus.Canceled, completion.Status);
    }

    [Fact]
    public async Task WorkerOverviewRealtimeStateRespectsCompactAndExpandedPanelModes()
    {
        var definition = WorkDefinition.Create("views.worker.realtime.state", "Returns worker overview realtime state.");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork<LandingLoggedExecutor>(
            definition,
            configuration => configuration.ConfigureLogging(level: LogLevel.Information)));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var session = await CreateTransportSession(system);
        var handle = await session.Queue.Enqueue(definition.Name);
        await handle.WaitForCompletion();

        var adapter = new WorkableViewQueryAdapter();
        var compact = Require(await adapter.WorkerOverviewRealtimeState(
            session,
            RequiredWorkerId(handle),
            new WorkWorkerOverviewRealtimeCriteria(
                WorkerControls: WorkComponentShapes.Compact,
                WorkerLogs: WorkComponentShapes.Compact,
                WorkerDuration: WorkComponentShapes.Compact,
                WorkerTimeline: WorkComponentShapes.Compact)));
        var expanded = Require(await adapter.WorkerOverviewRealtimeState(
            session,
            RequiredWorkerId(handle),
            new WorkWorkerOverviewRealtimeCriteria(
                WorkerControls: WorkComponentShapes.Standard,
                WorkerLogs: WorkComponentShapes.Standard,
                WorkerDuration: WorkComponentShapes.Standard,
                WorkerTimeline: WorkComponentShapes.Standard)));

        var compactSummary = Require(compact.LogSummary);
        Assert.Equal(3, compactSummary.Total);
        Assert.Equal(1, compactSummary.Error);
        Assert.Equal(1, compactSummary.Warning);
        Assert.Equal(1, compactSummary.Information);
        Assert.Empty(compact.LogEntries);
        Assert.Empty(compact.RecentIterations);
        Assert.Null(compact.TimelineSummary);
        Assert.Empty(compact.TimelineItems);
        var compactIteration = Require(compact.LatestIteration);
        Assert.Equal(WorkCompletionStatus.Completed, compactIteration.Status);
        Assert.Null(compactIteration.Output);

        var expandedSummary = Require(expanded.LogSummary);
        Assert.Equal(3, expandedSummary.Total);
        Assert.Equal(3, expanded.LogEntries.Count);
        Assert.Contains(expanded.LogEntries, entry => entry.Message == "landing info");
        Assert.Contains(expanded.LogEntries, entry => entry.Message == "landing warning");
        Assert.Contains(expanded.LogEntries, entry => entry.Message == "landing error");
        Assert.Single(expanded.RecentIterations);
        Assert.Equal(1, Require(expanded.TimelineSummary).Total);
        var timelineItem = Assert.Single(expanded.TimelineItems);
        Assert.Equal(WorkWorkerOverviewTimelineItemKind.Iteration, timelineItem.Kind);
        Assert.Equal(WorkCompletionStatus.Completed, timelineItem.IterationStatus);
    }

    [Fact]
    public async Task WorkerOverviewRealtimeStateCapsExpandedInitialActivityPayloads()
    {
        var definition = WorkDefinition.Create("views.worker.realtime.cap", "Caps expanded worker overview realtime payloads.");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork<FloodLoggedExecutor>(
            definition,
            configuration => configuration.ConfigureLogging(level: LogLevel.Information)));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var session = await CreateTransportSession(system);
        var handle = await session.Queue.Enqueue(definition.Name);
        await handle.WaitForCompletion();

        var adapter = new WorkableViewQueryAdapter();
        var expanded = Require(await adapter.WorkerOverviewRealtimeState(
            session,
            RequiredWorkerId(handle),
            new WorkWorkerOverviewRealtimeCriteria(
                WorkerControls: WorkComponentShapes.Standard,
                WorkerLogs: WorkComponentShapes.Standard,
                WorkerDuration: WorkComponentShapes.Standard,
                WorkerTimeline: WorkComponentShapes.Standard)));

        Assert.Equal(50, expanded.LogEntries.Count);
    }

    [Fact]
    public async Task WorkerOverviewAggregatesLogsAcrossRetainedIterations()
    {
        var attemptsByWorker = new ConcurrentDictionary<WorkerId, int>();
        var definition = WorkDefinition.Create("views.worker.logs.aggregate", "Aggregates retained iteration logs.");
        var services = new ServiceCollection();
        services.AddSingleton(attemptsByWorker);
        services.AddWorkableSystem(builder => builder.AddWork<AggregateRecurringLogExecutor>(
            definition,
            configuration => configuration
                .ConfigureLogging(level: LogLevel.Information)
                .UseRecurrence(WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(5)) with
                {
                    ContinueAfterFailure = false,
                    RetainedIterations = 10,
                })));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var session = await CreateTransportSession(system);
        var handle = await session.Queue.Enqueue(definition.Name);
        var workerId = RequiredWorkerId(handle);
        await PushRecurringWorkerAfterFirstAttempt(system, workerId, attemptsByWorker);
        await handle.WaitForCompletion();
        await WaitForReadModel(system);

        var overview = await new WorkableViewQueryAdapter().WorkerOverview(
            session,
            workerId,
            new WorkWorkerOverviewCriteria(
                Activity: WorkWorkerOverviewActivity.Logs,
                ActivityTake: 10));

        Assert.NotNull(overview);
        Assert.NotNull(overview.Logs.Page);
        Assert.Equal(2, overview.Logs.Summary.Total);
        Assert.Equal(2, overview.Logs.Summary.Information);
        Assert.Contains(overview.Logs.Page.Items, item => item.Message.Contains("attempt 1", StringComparison.Ordinal));
        Assert.Contains(overview.Logs.Page.Items, item => item.Message.Contains("attempt 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkerOverviewCanScopeLogsToASpecificIterationSequence()
    {
        var attemptsByWorker = new ConcurrentDictionary<WorkerId, int>();
        var definition = WorkDefinition.Create("views.worker.logs.sequence", "Scopes retained logs to one iteration.");
        var services = new ServiceCollection();
        services.AddSingleton(attemptsByWorker);
        services.AddWorkableSystem(builder => builder.AddWork<AggregateRecurringLogExecutor>(
            definition,
            configuration => configuration
                .ConfigureLogging(level: LogLevel.Information)
                .UseRecurrence(WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(5)) with
                {
                    ContinueAfterFailure = false,
                    RetainedIterations = 10,
                })));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var session = await CreateTransportSession(system);
        var handle = await session.Queue.Enqueue(definition.Name);
        var workerId = RequiredWorkerId(handle);
        await PushRecurringWorkerAfterFirstAttempt(system, workerId, attemptsByWorker);
        await handle.WaitForCompletion();
        await WaitForReadModel(system);

        var overview = await new WorkableViewQueryAdapter().WorkerOverview(
            session,
            workerId,
            new WorkWorkerOverviewCriteria(
                Activity: WorkWorkerOverviewActivity.Logs,
                ActivityTake: 10,
                LogIterationSequence: 2));

        Assert.NotNull(overview);
        Assert.NotNull(overview.Logs.Page);
        Assert.Equal(1, overview.Logs.Summary.Total);
        Assert.Equal(1, overview.Logs.Summary.Information);
        Assert.Collection(
            overview.Logs.Page.Items,
            item => Assert.Contains("attempt 2", item.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WorkerOverviewFiltersAndPagesLogsUsingServerCriteria()
    {
        var definition = WorkDefinition.Create("views.worker.logs.criteria", "Filters and pages worker logs.");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork<LandingLoggedExecutor>(
            definition,
            configuration => configuration.ConfigureLogging(level: LogLevel.Information)));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var session = await CreateTransportSession(system);
        var handle = await session.Queue.Enqueue(definition.Name);
        await handle.WaitForCompletion();
        await WaitForReadModel(system);
        var adapter = new WorkableViewQueryAdapter();

        var filtered = await adapter.WorkerOverview(
            session,
            RequiredWorkerId(handle),
            new WorkWorkerOverviewCriteria(
                Activity: WorkWorkerOverviewActivity.Logs,
                ActivityTake: 10,
                LogLevels: [LogLevel.Warning]));

        var firstPage = await adapter.WorkerOverview(
            session,
            RequiredWorkerId(handle),
            new WorkWorkerOverviewCriteria(
                Activity: WorkWorkerOverviewActivity.Logs,
                ActivityTake: 1,
                LogSortDirection: WorkWorkerOverviewSortDirection.Asc));

        var secondPage = await adapter.WorkerOverview(
            session,
            RequiredWorkerId(handle),
            new WorkWorkerOverviewCriteria(
                Activity: WorkWorkerOverviewActivity.Logs,
                ActivityTake: 1,
                ActivityCursor: firstPage?.Logs.Page?.Cursor,
                LogSortDirection: WorkWorkerOverviewSortDirection.Asc));

        Assert.NotNull(filtered);
        Assert.Equal(3, filtered.Logs.Summary.Total);
        Assert.Equal(0, filtered.Logs.Summary.Critical);
        Assert.Equal(1, filtered.Logs.Summary.Error);
        Assert.Equal(1, filtered.Logs.Summary.Errors);
        Assert.Equal(1, filtered.Logs.Summary.Warning);
        Assert.Equal(1, filtered.Logs.Summary.Warnings);
        Assert.Equal(1, filtered.Logs.Summary.Information);
        Assert.Equal(0, filtered.Logs.Summary.Debug);
        Assert.Equal(0, filtered.Logs.Summary.Trace);
        Assert.Collection(
            Assert.IsType<WorkWorkerOverviewPage<WorkWorkerOverviewLogEntry>>(filtered.Logs.Page).Items,
            item => Assert.Equal(LogLevel.Warning, item.Level));

        var firstLogsPage = firstPage?.Logs.Page ?? throw new Xunit.Sdk.XunitException("Expected firstPage.Logs.Page to be non-null.");
        Assert.Single(firstLogsPage.Items);
        Assert.True(firstLogsPage.HasMore);
        Assert.Equal("landing info", firstLogsPage.Items[0].Message);

        var secondLogsPage = Assert.IsType<WorkWorkerOverviewPage<WorkWorkerOverviewLogEntry>>(secondPage?.Logs.Page);
        Assert.Single(secondLogsPage.Items);
        Assert.Equal("landing warning", secondLogsPage.Items[0].Message);
    }

    [Fact]
    public async Task WorkerOverviewReturnsDistinctIdsForDuplicateLogEntries()
    {
        var definition = WorkDefinition.Create("views.worker.logs.duplicate-ids", "Returns unique ids for duplicate log entries.");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork<DuplicateLoggedExecutor>(
            definition,
            configuration => configuration.ConfigureLogging(level: LogLevel.Information)));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var session = await CreateTransportSession(system);
        var handle = await session.Queue.Enqueue(definition.Name);
        await handle.WaitForCompletion();
        await WaitForReadModel(system);
        var adapter = new WorkableViewQueryAdapter();

        var overview = await adapter.WorkerOverview(
            session,
            RequiredWorkerId(handle),
            new WorkWorkerOverviewCriteria(
                Activity: WorkWorkerOverviewActivity.Logs,
                ActivityTake: 10));

        var page = Assert.IsType<WorkWorkerOverviewPage<WorkWorkerOverviewLogEntry>>(overview?.Logs.Page);
        Assert.Equal(2, page.Items.Count);
        Assert.All(page.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.Id)));
        Assert.Equal(2, page.Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.All(page.Items, item => Assert.Equal("duplicate log", item.Message));
    }

    [Fact]
    public async Task WorkerIterationMessagesCanFilterSortAndPage()
    {
        var definition = WorkDefinition.Create("views.iteration.messages.criteria", "Filters and pages iteration messages.");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork(
            definition,
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success(messages:
            [
                new WorkMessage(
                    "iteration.warning",
                    WorkMessageSeverity.Warning,
                    "Warning message.",
                    "messages.warning",
                    new Dictionary<string, object?> { ["slot"] = 2 })
                {
                    OccurredAt = DateTimeOffset.Parse("2026-05-29T10:00:02Z"),
                },
                new WorkMessage(
                    "iteration.information",
                    WorkMessageSeverity.Information,
                    "Information message.",
                    "messages.information",
                    new Dictionary<string, object?> { ["slot"] = 1 })
                {
                    OccurredAt = DateTimeOffset.Parse("2026-05-29T10:00:01Z"),
                },
                new WorkMessage(
                    "iteration.debug",
                    WorkMessageSeverity.Debug,
                    "Debug message.",
                    "messages.debug",
                    new Dictionary<string, object?> { ["slot"] = 3 })
                {
                    OccurredAt = DateTimeOffset.Parse("2026-05-29T10:00:03Z"),
                },
            ]))));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var session = await CreateTransportSession(system);
        var handle = await session.Queue.Enqueue(definition.Name);
        await handle.WaitForCompletion();
        await WaitForReadModel(system);
        var adapter = new WorkableViewQueryAdapter();

        var firstPage = await adapter.WorkerIterationMessages(
            session,
            new WorkerIterationReference(RequiredWorkerId(handle), 1),
            new WorkIterationMessageCriteria(
                Take: 1,
                SortDirection: WorkWorkerOverviewSortDirection.Asc,
                Severities: [WorkMessageSeverity.Information, WorkMessageSeverity.Warning]));

        var secondPage = await adapter.WorkerIterationMessages(
            session,
            new WorkerIterationReference(RequiredWorkerId(handle), 1),
            new WorkIterationMessageCriteria(
                Take: 1,
                Cursor: firstPage?.Page.Cursor,
                SortDirection: WorkWorkerOverviewSortDirection.Asc,
                Severities: [WorkMessageSeverity.Information, WorkMessageSeverity.Warning]));

        Assert.NotNull(firstPage);
        Assert.Equal(3, firstPage.Summary.Total);
        Assert.Equal(1, firstPage.Summary.Warning);
        Assert.Equal(1, firstPage.Summary.Information);
        Assert.Equal(1, firstPage.Summary.Debug);
        Assert.Single(firstPage.Page.Items);
        Assert.True(firstPage.Page.HasMore);
        Assert.Equal("iteration.information", firstPage.Page.Items[0].Code);
        Assert.Equal("messages.information", firstPage.Page.Items[0].Target);
        Assert.NotNull(firstPage.Page.Items[0].Metadata);

        Assert.NotNull(secondPage);
        Assert.Single(secondPage.Page.Items);
        Assert.Equal("iteration.warning", secondPage.Page.Items[0].Code);
        Assert.False(secondPage.Page.HasMore);
    }

    [Fact]
    public async Task WorkerOverviewFiltersAndPagesTimelineUsingServerCriteria()
    {
        var definition = WorkDefinition.Create("views.worker.timeline.criteria", "Filters and pages worker timeline.");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork(
            definition,
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success())));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var session = await CreateTransportSession(
            system,
            WorkInvocationChannel.HttpApi,
            new WorkActor("timeline-user", "Timeline Tester"));
        var handle = await session.Queue.Enqueue(definition.Name);
        await handle.WaitForCompletion();
        await WaitForReadModel(system);
        var adapter = new WorkableViewQueryAdapter();

        var filtered = await adapter.WorkerOverview(
            session,
            RequiredWorkerId(handle),
            new WorkWorkerOverviewCriteria(
                Activity: WorkWorkerOverviewActivity.Timeline,
                ActivityTake: 10,
                TimelineCategories: [WorkWorkerOverviewTimelineCategory.SystemEvent]));

        var firstPage = await adapter.WorkerOverview(
            session,
            RequiredWorkerId(handle),
            new WorkWorkerOverviewCriteria(
                Activity: WorkWorkerOverviewActivity.Timeline,
                ActivityTake: 1,
                TimelineSortDirection: WorkWorkerOverviewSortDirection.Desc));

        Assert.NotNull(filtered);
        Assert.True(filtered.Timeline.Summary.Total >= 1);
        Assert.All(
            Assert.IsType<WorkWorkerOverviewPage<WorkWorkerOverviewTimelineItem>>(filtered.Timeline.Page).Items,
            item => Assert.Equal(WorkWorkerOverviewTimelineCategory.SystemEvent, item.Category));

        Assert.NotNull(firstPage?.Timeline.Page);
        Assert.Single(firstPage.Timeline.Page.Items);
    }

    [Fact]
    public async Task WorkerOverviewResolvesInitialPanelAutomaticallyFromWorkerShape()
    {
        var definition = WorkDefinition.Create("views.worker.landing.auto", "Returns worker overview with automatic panel selection.");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork(
            definition,
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success())));
        await using var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var session = await CreateTransportSession(system);
        var handle = await session.Queue.Enqueue(definition.Name);
        await handle.WaitForCompletion();
        await WaitForReadModel(system);

        var landing = await new WorkableViewQueryAdapter().WorkerOverview(
            session,
            RequiredWorkerId(handle));

        Assert.NotNull(landing);
        Assert.Equal(WorkWorkerOverviewActivity.Timeline, landing.Activity);
        Assert.NotNull(landing.Timeline.Page);
        Assert.Null(landing.Logs.Page);
        Assert.True(landing.Worker.IsFinal);
    }

    private static WorkChangeKey CreateStructuredChange(
        WorkKeyKind kind,
        string type,
        string value)
        => kind switch
        {
            WorkKeyKind.Subject => WorkChangeKey.Subject(new WorkSubjectId(type, value)),
            WorkKeyKind.ConcurrencyKey => WorkChangeKey.Concurrency(new WorkConcurrencyKey(type, value)),
            WorkKeyKind.Identifier => WorkChangeKey.Identifier(new WorkIdentifier(type, value)),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Expected a structured work key kind."),
        };

    private static ValueTask<IWorkSystemSession> CreateTransportSession(
        IWorkSystem system,
        WorkInvocationChannel channel = WorkInvocationChannel.InProcess,
        WorkActor? actor = null)
        => TransportAuthorizationTestSupport.CreateTransportSession(
            system,
            channel,
            actor,
            description: "Use view adapter test session.");

    private static async Task WaitForReadModel(IWorkSystem system)
        => await TestEventually.ReadModelDrained(system);

    private static async Task PushRecurringWorkerAfterFirstAttempt(
        IWorkSystem system,
        WorkerId workerId,
        ConcurrentDictionary<WorkerId, int> attemptsByWorker)
    {
        var waiting = await TestEventually.UntilNotNull(async () =>
        {
            attemptsByWorker.TryGetValue(workerId, out var attempts);
            var worker = await system.Query.Worker(workerId);
            return attempts == 1 && worker?.State == WorkerState.Waiting ? worker : null;
        });

        var push = await system.Workers.Execute(waiting.Version, WorkAction.Push);

        Assert.True(push.IsAccepted);
    }

    private static WorkerId RequiredWorkerId(IWorkerHandle handle)
        => handle.WorkerId ?? throw new InvalidOperationException("Expected accepted worker.");

    private static T Require<T>(T? value)
        where T : class
    {
        Assert.NotNull(value);
        return value;
    }

    private sealed class LandingLoggedExecutor(ILogger<LandingLoggedExecutor> logger) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        {
            logger.LogInformation("landing info");
            logger.LogWarning("landing warning");
            logger.LogError("landing error");
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class DuplicateLoggedExecutor(ILogger<DuplicateLoggedExecutor> logger) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        {
            logger.LogInformation("duplicate log");
            logger.LogInformation("duplicate log");
            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class FloodLoggedExecutor(ILogger<FloodLoggedExecutor> logger) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        {
            for (var index = 0; index < 200; index++)
            {
                logger.LogInformation("flood log {Index}", index);
            }

            return Task.FromResult(WorkExecutionResult.Success());
        }
    }

    private sealed class LandingFailingExecutor : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("view.failure", "The work failed.")]));
    }

    private sealed class AggregateRecurringLogExecutor(
        ConcurrentDictionary<WorkerId, int> attemptsByWorker,
        ILogger<AggregateRecurringLogExecutor> logger) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        {
            var attempt = attemptsByWorker.AddOrUpdate(context.WorkerId, 1, (_, current) => current + 1);
            logger.LogInformation("aggregate log attempt {Attempt}", attempt);
            return Task.FromResult(
                attempt >= 2
                    ? WorkExecutionResult.Failure([WorkMessage.Error("aggregate.stop", "Stop recurrence.")])
                    : WorkExecutionResult.Success());
        }
    }
}
