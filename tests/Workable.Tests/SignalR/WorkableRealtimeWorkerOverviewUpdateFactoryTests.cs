using System.Text.Json;

namespace Workable.Tests;

[Trait("Category", "SignalR")]
public sealed class WorkableRealtimeWorkerOverviewUpdateFactoryTests
{
    [Fact]
    public void PublicMethodsRejectNullInputs()
    {
        var current = CreateState();
        var criteria = new WorkWorkerOverviewRealtimeCriteria();
        var update = WorkableRealtimeWorkerOverviewUpdateFactory.CreateInitial(current);
        var workEvent = CreateEvent("worker.completed", new { });

        AssertRejectsNull("state", () => WorkableRealtimeWorkerOverviewUpdateFactory.CreateInitial(null!));
        AssertRejectsNull("workEvent", () => WorkableRealtimeWorkerOverviewUpdateFactory.Create(null!, current, criteria));
        AssertRejectsNull("current", () => WorkableRealtimeWorkerOverviewUpdateFactory.Create(workEvent, null!, criteria));
        AssertRejectsNull("criteria", () => WorkableRealtimeWorkerOverviewUpdateFactory.Create(workEvent, current, null!));
        AssertRejectsNull("current", () => WorkableRealtimeWorkerOverviewUpdateFactory.Apply(null!, update, criteria));
        AssertRejectsNull("update", () => WorkableRealtimeWorkerOverviewUpdateFactory.Apply(current, null!, criteria));
        AssertRejectsNull("criteria", () => WorkableRealtimeWorkerOverviewUpdateFactory.Apply(current, update, null!));
        AssertRejectsNull("current", () => WorkableRealtimeWorkerOverviewUpdateFactory.Coalesce(null!, [update], criteria, out _));
        AssertRejectsNull("updates", () => WorkableRealtimeWorkerOverviewUpdateFactory.Coalesce(current, null!, criteria, out _));
        AssertRejectsNull("criteria", () => WorkableRealtimeWorkerOverviewUpdateFactory.Coalesce(current, [update], null!, out _));
    }

    [Fact]
    public void CreateInitialReturnsFullSnapshot()
    {
        var workerId = WorkerId.New();
        var state = new WorkWorkerOverviewRealtimeState(
            CreateWorker(workerId: workerId, state: WorkerState.Waiting, retryAttempt: 2),
            CreateLatestIteration(sequence: 9, WorkCompletionStatus.Failed),
            new WorkWorkerOverviewLogSummary(12, 1, 2, 3, 4, 4, 5, 6, 7),
            [
                new WorkWorkerOverviewLogEntry(
                    "log-1",
                    DateTimeOffset.UtcNow.AddSeconds(-2),
                    Microsoft.Extensions.Logging.LogLevel.Warning,
                    "Tests",
                    "warning",
                    1,
                    "warn",
                    null,
                    null),
            ],
            [
                new WorkWorkerOverviewRecentIteration(
                    workerId,
                    9,
                    WorkCompletionStatus.Failed,
                    DateTimeOffset.UtcNow.AddMinutes(-2),
                    DateTimeOffset.UtcNow.AddMinutes(-1),
                    TimeSpan.FromSeconds(3)),
            ],
            new WorkWorkerOverviewTimelineSummary(4, 1, 2, 1),
            [
                new WorkWorkerOverviewTimelineItem(
                    "timeline-1",
                    DateTimeOffset.UtcNow.AddSeconds(-1),
                    WorkWorkerOverviewTimelineItemKind.Iteration,
                    WorkWorkerOverviewTimelineCategory.Failure,
                    null,
                    null,
                    null,
                    null,
                    9,
                    WorkCompletionStatus.Failed,
                    TimeSpan.FromSeconds(3),
                    null,
                    null),
            ]);

        var update = WorkableRealtimeWorkerOverviewUpdateFactory.CreateInitial(state);

        Assert.Same(state.Worker, update.Worker);
        Assert.Same(state.LatestIteration, update.LatestIteration);
        Assert.Same(state.LogSummary, update.LogSummary);
        Assert.Same(state.LogEntries, update.LogEntries);
        Assert.Same(state.RecentIterations, update.RecentIterations);
        Assert.Same(state.TimelineSummary, update.TimelineSummary);
        Assert.Same(state.TimelineItems, update.TimelineItems);
    }

    [Fact]
    public void CreateForLogEventReturnsSummaryAndMatchingEntryWhenLogsExpanded()
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var workerId = WorkerId.New();
        var current = new WorkWorkerOverviewRealtimeState(
            CreateWorker(workerId: workerId),
            CreateLatestIteration(sequence: 3, WorkCompletionStatus.Executing),
            new WorkWorkerOverviewLogSummary(10, 1, 2, 3, 4, 4, 5, 6, 7),
            [],
            [],
            null,
            []);
        var criteria = new WorkWorkerOverviewRealtimeCriteria(WorkerLogs: WorkComponentShapes.Standard);
        var workEvent = CreateEvent(
            "worker.log",
            new
            {
                Worker = new { UpdatedAt = occurredAt, StateChangedAt = occurredAt, State = WorkerState.Running, NextRunAt = (DateTimeOffset?)null, RetryAttempt = (int?)null },
                Iteration = new { Sequence = 3L, StartedAt = occurredAt.AddSeconds(-2), CompletedAt = (DateTimeOffset?)null, ExecutionDuration = (TimeSpan?)null, Status = WorkCompletionStatus.Executing },
                Log = new
                {
                    Id = "log-b",
                    Category = "Tests",
                    Level = "Warning",
                    EventId = new { Id = 7, Name = "warn" },
                    Message = "B",
                    ExceptionType = (string?)null,
                    ExceptionMessage = (string?)null,
                },
            },
            occurredAt);

        var update = RequireUpdate(WorkableRealtimeWorkerOverviewUpdateFactory.Create(workEvent, current, criteria));

        Assert.Same(current.LogSummary, update.LogSummary);
        Assert.Collection(
            Assert.IsAssignableFrom<IReadOnlyList<WorkWorkerOverviewLogEntry>>(update.LogEntries),
            entry =>
            {
                Assert.Equal("log-b", entry.Id);
                Assert.Equal("Tests", entry.Category);
                Assert.Equal("B", entry.Message);
                Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, entry.Level);
                Assert.Equal(7, entry.EventId);
                Assert.Equal("warn", entry.EventName);
                Assert.Equal(occurredAt, entry.OccurredAt);
            });
        Assert.Null(update.Worker);
        Assert.Null(update.LatestIteration);
    }

    [Fact]
    public void CreateForRetryingEventReturnsCurrentWorkerAndIterationDelta()
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var stateChangedAt = occurredAt.AddSeconds(-1);
        var nextRunAt = occurredAt.AddMinutes(5);
        var workerId = WorkerId.New();
        var latestIteration = new WorkWorkerOverviewLatestIteration(
            workerId,
            5,
            WorkCompletionStatus.Failed,
            occurredAt.AddMinutes(-2),
            occurredAt.AddMinutes(-1),
            TimeSpan.FromSeconds(1),
            null,
            new WorkWorkerOverviewFailure(
                WorkWorkerOverviewFailureKind.Exception,
                "boom",
                PendingState: new WorkWorkerOverviewPendingState(
                    WorkWorkerOverviewPendingStateMode.Retry,
                    nextRunAt,
                    stateChangedAt,
                    occurredAt,
                    2)));
        var current = new WorkWorkerOverviewRealtimeState(
            CreateWorker(workerId: workerId, state: WorkerState.Failed, retryAttempt: 1),
            latestIteration,
            null,
            [],
            [],
            new WorkWorkerOverviewTimelineSummary(4, 1, 2, 1),
            []);
        var criteria = new WorkWorkerOverviewRealtimeCriteria(
            WorkerControls: WorkComponentShapes.Compact,
            WorkerTimeline: WorkComponentShapes.Standard);
        var workEvent = CreateEvent(
            "worker.retrying",
            new
            {
                Worker = new
                {
                    UpdatedAt = occurredAt,
                    StateChangedAt = stateChangedAt,
                    State = WorkerState.Retrying,
                    NextRunAt = nextRunAt,
                    RetryAttempt = 2,
                },
                Iteration = new
                {
                    Sequence = 5L,
                    StartedAt = latestIteration.StartedAt,
                    CompletedAt = latestIteration.CompletedAt,
                    ExecutionDuration = latestIteration.ExecutionDuration,
                    Status = WorkCompletionStatus.Failed,
                },
            });

        var update = RequireUpdate(WorkableRealtimeWorkerOverviewUpdateFactory.Create(workEvent, current, criteria));

        var worker = Require(update.Worker);
        Assert.Equal(WorkerState.Retrying, worker.State);
        Assert.Equal(nextRunAt, worker.NextRunAt);
        Assert.Equal(2, worker.RetryAttempt);
        var iteration = Require(update.LatestIteration);
        Assert.Equal(5, iteration.Sequence);
        var pendingState = Require(iteration.Failure?.PendingState);
        Assert.Equal(WorkWorkerOverviewPendingStateMode.Retry, pendingState.Mode);
        Assert.Same(current.TimelineSummary, update.TimelineSummary);
        Assert.Collection(
            Assert.IsAssignableFrom<IReadOnlyList<WorkWorkerOverviewTimelineItem>>(update.TimelineItems),
            item =>
            {
                Assert.Equal(5, item.Sequence);
                Assert.Equal(WorkWorkerOverviewTimelineItemKind.Iteration, item.Kind);
                Assert.Equal(WorkWorkerOverviewTimelineCategory.Failure, item.Category);
                Assert.Equal(WorkWorkerOverviewPendingStateMode.Retry, Require(item.Failure?.PendingState).Mode);
            });
    }

    [Fact]
    public void CreateForWorkerFailedEventReturnsFailedIterationTimelineItem()
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var workerId = WorkerId.New();
        var current = new WorkWorkerOverviewRealtimeState(
            CreateWorker(workerId: workerId, state: WorkerState.Running),
            CreateLatestIteration(sequence: 5, WorkCompletionStatus.Executing),
            null,
            [],
            [],
            new WorkWorkerOverviewTimelineSummary(1, 0, 1, 0),
            [
                new WorkWorkerOverviewTimelineItem(
                    "iteration:5",
                    occurredAt.AddSeconds(-5),
                    WorkWorkerOverviewTimelineItemKind.Iteration,
                    WorkWorkerOverviewTimelineCategory.SystemEvent,
                    null,
                    null,
                    null,
                    null,
                    5,
                    WorkCompletionStatus.Executing,
                    null,
                    null,
                    null),
            ]);
        var criteria = new WorkWorkerOverviewRealtimeCriteria(
            WorkerDuration: WorkComponentShapes.Standard,
            WorkerTimeline: WorkComponentShapes.Standard);
        var workEvent = CreateEvent(
            "worker.failed",
            new
            {
                Worker = new
                {
                    UpdatedAt = occurredAt,
                    StateChangedAt = occurredAt,
                    State = WorkerState.Failed,
                    NextRunAt = (DateTimeOffset?)null,
                    RetryAttempt = (int?)null,
                },
                Iteration = new
                {
                    Sequence = 5L,
                    StartedAt = occurredAt.AddSeconds(-10),
                    CompletedAt = occurredAt,
                    ExecutionDuration = TimeSpan.FromSeconds(10),
                    Status = WorkCompletionStatus.Failed,
                    Failure = new
                    {
                        Kind = WorkerIterationFailureKind.Failure,
                        Message = "Boom.",
                        Code = "sample.failed",
                        Target = "execution",
                        ExceptionType = (string?)null,
                        StackTrace = (string?)null,
                        DeclaredByWork = false,
                    },
                },
            },
            occurredAt);

        var update = RequireUpdate(WorkableRealtimeWorkerOverviewUpdateFactory.Create(workEvent, current, criteria));

        var worker = Require(update.Worker);
        Assert.Equal(workerId, worker.WorkerId);
        Assert.Equal(WorkerState.Failed, worker.State);
        var iteration = Require(update.LatestIteration);
        Assert.Equal(5, iteration.Sequence);
        Assert.Equal(WorkCompletionStatus.Failed, iteration.Status);
        Assert.Equal(TimeSpan.FromSeconds(10), iteration.ExecutionDuration);
        Assert.Equal("Boom.", Require(iteration.Failure).Message);
        var recent = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<WorkWorkerOverviewRecentIteration>>(update.RecentIterations));
        Assert.Equal(5, recent.Sequence);
        Assert.Equal(WorkCompletionStatus.Failed, recent.Status);
        Assert.Equal(TimeSpan.FromSeconds(10), recent.ExecutionDuration);
        var timelineItem = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<WorkWorkerOverviewTimelineItem>>(update.TimelineItems));
        Assert.Equal(WorkWorkerOverviewTimelineItemKind.Iteration, timelineItem.Kind);
        Assert.Equal(5, timelineItem.Sequence);
        Assert.Equal(WorkCompletionStatus.Failed, timelineItem.IterationStatus);
        Assert.Equal("sample.failed", Require(timelineItem.Failure).Code);
    }

    [Fact]
    public void CreateForIterationStartedEventOmitsTimelineItemWhenTimelineFilterExcludesSystemEvents()
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var workerId = WorkerId.New();
        var current = new WorkWorkerOverviewRealtimeState(
            CreateWorker(workerId: workerId, state: WorkerState.Running),
            CreateLatestIteration(sequence: 5, WorkCompletionStatus.Completed),
            null,
            [],
            [],
            new WorkWorkerOverviewTimelineSummary(1, 0, 1, 0),
            []);
        var criteria = new WorkWorkerOverviewRealtimeCriteria(
            WorkerTimeline: WorkComponentShapes.Standard,
            TimelineCategories: [WorkWorkerOverviewTimelineCategory.Failure]);
        var workEvent = CreateEvent(
            "worker.iteration.started",
            new
            {
                Worker = new
                {
                    UpdatedAt = occurredAt,
                    StateChangedAt = occurredAt,
                    State = WorkerState.Running,
                    NextRunAt = (DateTimeOffset?)null,
                    RetryAttempt = (int?)null,
                },
                Iteration = new
                {
                    Sequence = 6L,
                    StartedAt = occurredAt,
                    CompletedAt = (DateTimeOffset?)null,
                    ExecutionDuration = (TimeSpan?)null,
                    Status = WorkCompletionStatus.Executing,
                    AttemptCount = 1,
                },
            },
            occurredAt);

        var update = RequireUpdate(WorkableRealtimeWorkerOverviewUpdateFactory.Create(workEvent, current, criteria));

        Assert.Equal(WorkerState.Running, Require(update.Worker).State);
        var iteration = Require(update.LatestIteration);
        Assert.Equal(6, iteration.Sequence);
        Assert.Equal(WorkCompletionStatus.Executing, iteration.Status);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<WorkWorkerOverviewTimelineItem>>(update.TimelineItems));
    }

    [Fact]
    public void CreateForWaitingEventReturnsWaitingStateItem()
    {
        var changedAt = DateTimeOffset.UtcNow;
        var nextRunAt = changedAt.AddMinutes(5);
        var workerId = WorkerId.New();
        var current = new WorkWorkerOverviewRealtimeState(
            CreateWorker(workerId: workerId, state: WorkerState.Running),
            CreateLatestIteration(sequence: 2, WorkCompletionStatus.Completed),
            null,
            [],
            [],
            new WorkWorkerOverviewTimelineSummary(3, 0, 3, 0),
            []);
        var criteria = new WorkWorkerOverviewRealtimeCriteria(WorkerTimeline: WorkComponentShapes.Standard);
        var workEvent = CreateEvent(
            "worker.waiting",
            new
            {
                Worker = new
                {
                    UpdatedAt = changedAt,
                    StateChangedAt = changedAt,
                    State = WorkerState.Waiting,
                    NextRunAt = nextRunAt,
                    RetryAttempt = (int?)null,
                },
                Iteration = new
                {
                    Sequence = 2L,
                    StartedAt = changedAt.AddSeconds(-1),
                    CompletedAt = changedAt,
                    ExecutionDuration = TimeSpan.FromSeconds(1),
                    Status = WorkCompletionStatus.Completed,
                },
            });

        var update = RequireUpdate(WorkableRealtimeWorkerOverviewUpdateFactory.Create(workEvent, current, criteria));

        var worker = Require(update.Worker);
        Assert.Equal(WorkerState.Waiting, worker.State);
        Assert.Equal(nextRunAt, worker.NextRunAt);
        Assert.Same(current.TimelineSummary, update.TimelineSummary);
        Assert.Collection(
            Assert.IsAssignableFrom<IReadOnlyList<WorkWorkerOverviewTimelineItem>>(update.TimelineItems),
            item =>
            {
                Assert.Equal("live-state:waiting", item.Id);
                Assert.Equal(WorkWorkerOverviewTimelineItemKind.StateChange, item.Kind);
                Assert.Equal(WorkerState.Waiting, item.State);
                var pendingState = Require(item.PendingState);
                Assert.Equal(WorkWorkerOverviewPendingStateMode.Recurrence, pendingState.Mode);
                Assert.Equal(nextRunAt, pendingState.NextRunAt);
            });
    }

    [Fact]
    public void CreateForStartActionReturnsTimelineActionItem()
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var workerId = WorkerId.New();
        var current = new WorkWorkerOverviewRealtimeState(
            CreateWorker(workerId: workerId, state: WorkerState.Queued),
            null,
            null,
            [],
            [],
            new WorkWorkerOverviewTimelineSummary(0, 0, 0, 0),
            []);
        var criteria = new WorkWorkerOverviewRealtimeCriteria(WorkerTimeline: WorkComponentShapes.Standard);
        var workEvent = CreateEvent(
            "worker.start",
            new
            {
                Worker = new
                {
                    UpdatedAt = occurredAt,
                    StateChangedAt = occurredAt,
                    State = WorkerState.Running,
                    NextRunAt = (DateTimeOffset?)null,
                    RetryAttempt = (int?)null,
                },
                Origin = new
                {
                    Channel = "InProcess",
                },
                Action = WorkAction.Start,
                ActionStatus = WorkActionStatus.Accepted,
            },
            occurredAt);

        var update = RequireUpdate(WorkableRealtimeWorkerOverviewUpdateFactory.Create(workEvent, current, criteria));

        Assert.Equal(WorkerState.Running, Require(update.Worker).State);
        Assert.Collection(
            Assert.IsAssignableFrom<IReadOnlyList<WorkWorkerOverviewTimelineItem>>(update.TimelineItems),
            item =>
            {
                Assert.Equal(WorkWorkerOverviewTimelineItemKind.ActionRequest, item.Kind);
                Assert.Equal(WorkAction.Start, item.Action);
                Assert.Equal(WorkActionStatus.Accepted, item.ActionStatus);
                Assert.Equal(WorkWorkerOverviewTimelineCategory.SystemEvent, item.Category);
            });
    }

    [Fact]
    public void CoalescePreservesActionAndIterationChangesAcrossBatch()
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var workerId = WorkerId.New();
        var current = new WorkWorkerOverviewRealtimeState(
            CreateWorker(workerId: workerId, state: WorkerState.Queued),
            null,
            null,
            [],
            [],
            new WorkWorkerOverviewTimelineSummary(0, 0, 0, 0),
            []);
        var criteria = new WorkWorkerOverviewRealtimeCriteria(
            WorkerTimeline: WorkComponentShapes.Standard,
            WorkerDuration: WorkComponentShapes.Standard);
        var startUpdate = RequireUpdate(WorkableRealtimeWorkerOverviewUpdateFactory.Create(
            CreateEvent(
                "worker.start",
                new
                {
                    Worker = new
                    {
                        UpdatedAt = occurredAt,
                        StateChangedAt = occurredAt,
                        State = WorkerState.Running,
                        NextRunAt = (DateTimeOffset?)null,
                        RetryAttempt = (int?)null,
                    },
                    Origin = new
                    {
                        Channel = "InProcess",
                    },
                    Action = WorkAction.Start,
                    ActionStatus = WorkActionStatus.Accepted,
                },
                occurredAt),
            current,
            criteria));
        var afterStart = WorkableRealtimeWorkerOverviewUpdateFactory.Apply(current, startUpdate, criteria);
        var iterationUpdate = RequireUpdate(WorkableRealtimeWorkerOverviewUpdateFactory.Create(
            CreateEvent(
                "worker.iteration.started",
                new
                {
                    Worker = new
                    {
                        UpdatedAt = occurredAt.AddMilliseconds(10),
                        StateChangedAt = occurredAt,
                        State = WorkerState.Running,
                        NextRunAt = (DateTimeOffset?)null,
                        RetryAttempt = (int?)null,
                    },
                    Iteration = new
                    {
                        Sequence = 1L,
                        StartedAt = occurredAt.AddMilliseconds(10),
                        CompletedAt = (DateTimeOffset?)null,
                        ExecutionDuration = (TimeSpan?)null,
                        Status = WorkCompletionStatus.Executing,
                    },
                },
                occurredAt.AddMilliseconds(10)),
            afterStart,
            criteria));

        var batchedUpdate = WorkableRealtimeWorkerOverviewUpdateFactory.Coalesce(
            current,
            [startUpdate, iterationUpdate],
            criteria,
            out var nextState);

        batchedUpdate = RequireUpdate(batchedUpdate);
        Assert.Equal(WorkerState.Running, nextState.Worker.State);
        Assert.Equal(WorkerState.Running, Require(batchedUpdate.Worker).State);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<WorkWorkerOverviewRecentIteration>>(batchedUpdate.RecentIterations));
        var timelineItems = Assert.IsAssignableFrom<IReadOnlyList<WorkWorkerOverviewTimelineItem>>(batchedUpdate.TimelineItems);
        Assert.Contains(timelineItems, item => item.Kind == WorkWorkerOverviewTimelineItemKind.ActionRequest && item.Action == WorkAction.Start);
        Assert.Contains(timelineItems, item => item.Kind == WorkWorkerOverviewTimelineItemKind.Iteration &&
            item.Sequence == 1 &&
            item.IterationStatus == WorkCompletionStatus.Executing);
    }

    [Fact]
    public void CoalesceReturnsNullAndPreservesStateWhenThereAreNoUpdates()
    {
        var current = CreateState();
        var criteria = new WorkWorkerOverviewRealtimeCriteria();

        var update = WorkableRealtimeWorkerOverviewUpdateFactory.Coalesce(
            current,
            [],
            criteria,
            out var nextState);

        Assert.Null(update);
        Assert.Same(current, nextState);
    }

    [Fact]
    public void CoalesceReturnsSingleUpdateAndAppliesItToState()
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var current = CreateState();
        var waitingWorker = current.Worker with
        {
            State = WorkerState.Waiting,
            StateChangedAt = occurredAt,
            UpdatedAt = occurredAt,
            NextRunAt = occurredAt.AddMinutes(5),
        };
        var update = new WorkWorkerOverviewRealtimeUpdate(
            occurredAt,
            waitingWorker);
        var criteria = new WorkWorkerOverviewRealtimeCriteria();

        var coalesced = WorkableRealtimeWorkerOverviewUpdateFactory.Coalesce(
            current,
            [update],
            criteria,
            out var nextState);

        Assert.Same(update, coalesced);
        Assert.Same(waitingWorker, nextState.Worker);
        Assert.Same(current.LatestIteration, nextState.LatestIteration);
        Assert.Same(current.LogEntries, nextState.LogEntries);
        Assert.Same(current.RecentIterations, nextState.RecentIterations);
        Assert.Same(current.TimelineItems, nextState.TimelineItems);
    }

    [Fact]
    public void ApplyRemovesLiveWaitingStateItemWhenWorkerLeavesWaiting()
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var current = new WorkWorkerOverviewRealtimeState(
            CreateWorker(state: WorkerState.Waiting),
            null,
            null,
            [],
            [],
            new WorkWorkerOverviewTimelineSummary(1, 0, 1, 0),
            [
                new WorkWorkerOverviewTimelineItem(
                    "live-state:waiting",
                    occurredAt,
                    WorkWorkerOverviewTimelineItemKind.StateChange,
                    WorkWorkerOverviewTimelineCategory.SystemEvent,
                    null,
                    null,
                    null,
                    WorkerState.Waiting,
                    null,
                    null,
                    null,
                    null,
                    null,
                    new WorkWorkerOverviewPendingState(
                        WorkWorkerOverviewPendingStateMode.Recurrence,
                        occurredAt.AddMinutes(5),
                        occurredAt,
                        occurredAt,
                        null)),
            ]);
        var update = new WorkWorkerOverviewRealtimeUpdate(
            occurredAt.AddSeconds(1),
            current.Worker with
            {
                State = WorkerState.Running,
                StateChangedAt = occurredAt.AddSeconds(1),
                UpdatedAt = occurredAt.AddSeconds(1),
                NextRunAt = null,
            });

        var next = WorkableRealtimeWorkerOverviewUpdateFactory.Apply(
            current,
            update,
            new WorkWorkerOverviewRealtimeCriteria(WorkerTimeline: WorkComponentShapes.Standard));

        Assert.DoesNotContain(next.TimelineItems, item => item.Id == "live-state:waiting");
    }

    [Fact]
    public void ApplyRemovesLiveWaitingStateItemWhenExecutingIterationIsPresent()
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var current = new WorkWorkerOverviewRealtimeState(
            CreateWorker(state: WorkerState.Waiting),
            CreateLatestIteration(sequence: 7, WorkCompletionStatus.Executing),
            null,
            [],
            [],
            new WorkWorkerOverviewTimelineSummary(2, 0, 2, 0),
            [
                new WorkWorkerOverviewTimelineItem(
                    "live-state:waiting",
                    occurredAt,
                    WorkWorkerOverviewTimelineItemKind.StateChange,
                    WorkWorkerOverviewTimelineCategory.SystemEvent,
                    null,
                    null,
                    null,
                    WorkerState.Waiting,
                    null,
                    null,
                    null,
                    null,
                    null,
                    new WorkWorkerOverviewPendingState(
                        WorkWorkerOverviewPendingStateMode.Recurrence,
                        occurredAt.AddMinutes(5),
                        occurredAt,
                        occurredAt,
                        null)),
                new WorkWorkerOverviewTimelineItem(
                    "iteration:7",
                    occurredAt.AddSeconds(1),
                    WorkWorkerOverviewTimelineItemKind.Iteration,
                    WorkWorkerOverviewTimelineCategory.SystemEvent,
                    null,
                    null,
                    null,
                    null,
                    7,
                    WorkCompletionStatus.Executing,
                    null,
                    null,
                    null),
            ]);

        var next = WorkableRealtimeWorkerOverviewUpdateFactory.Apply(
            current,
            new WorkWorkerOverviewRealtimeUpdate(occurredAt.AddSeconds(2)),
            new WorkWorkerOverviewRealtimeCriteria(WorkerTimeline: WorkComponentShapes.Standard));

        Assert.DoesNotContain(next.TimelineItems, item => item.Id == "live-state:waiting");
        Assert.Contains(next.TimelineItems, item => item.Id == "iteration:7");
    }

    [Fact]
    public void ApplyClearsRetryPendingWhenNextIterationStarts()
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var workerId = WorkerId.New();
        var current = new WorkWorkerOverviewRealtimeState(
            CreateWorker(workerId: workerId, state: WorkerState.Retrying, retryAttempt: 3),
            new WorkWorkerOverviewLatestIteration(
                workerId,
                20,
                WorkCompletionStatus.Failed,
                occurredAt.AddSeconds(-5),
                occurredAt.AddSeconds(-2),
                TimeSpan.FromSeconds(1),
                null,
                new WorkWorkerOverviewFailure(
                    WorkWorkerOverviewFailureKind.Exception,
                    "boom",
                    PendingState: new WorkWorkerOverviewPendingState(
                        WorkWorkerOverviewPendingStateMode.Retry,
                        occurredAt.AddSeconds(10),
                        occurredAt.AddSeconds(-1),
                        occurredAt.AddSeconds(-1),
                        3))),
            null,
            [],
            [],
            new WorkWorkerOverviewTimelineSummary(2, 0, 1, 1),
            [
                new WorkWorkerOverviewTimelineItem(
                    "iteration:20",
                    occurredAt.AddSeconds(-2),
                    WorkWorkerOverviewTimelineItemKind.Iteration,
                    WorkWorkerOverviewTimelineCategory.Failure,
                    null,
                    null,
                    null,
                    null,
                    20,
                    WorkCompletionStatus.Failed,
                    TimeSpan.FromSeconds(1),
                    null,
                    new WorkWorkerOverviewFailure(
                        WorkWorkerOverviewFailureKind.Exception,
                        "boom",
                        PendingState: new WorkWorkerOverviewPendingState(
                            WorkWorkerOverviewPendingStateMode.Retry,
                            occurredAt.AddSeconds(10),
                            occurredAt.AddSeconds(-1),
                            occurredAt.AddSeconds(-1),
                            3))),
            ]);
        var update = new WorkWorkerOverviewRealtimeUpdate(
            occurredAt,
            current.Worker with
            {
                State = WorkerState.Running,
                StateChangedAt = occurredAt,
                UpdatedAt = occurredAt,
                NextRunAt = null,
                RetryAttempt = null,
            },
            new WorkWorkerOverviewLatestIteration(
                workerId,
                21,
                WorkCompletionStatus.Executing,
                occurredAt,
                null,
                null,
                null,
                null),
            RecentIterations:
            [
                new WorkWorkerOverviewRecentIteration(
                    workerId,
                    21,
                    WorkCompletionStatus.Executing,
                    occurredAt,
                    null,
                    null),
            ],
            TimelineItems:
            [
                new WorkWorkerOverviewTimelineItem(
                    "iteration:21",
                    occurredAt,
                    WorkWorkerOverviewTimelineItemKind.Iteration,
                    WorkWorkerOverviewTimelineCategory.SystemEvent,
                    null,
                    null,
                    null,
                    null,
                    21,
                    WorkCompletionStatus.Executing,
                    null,
                    null,
                    null),
            ]);

        var next = WorkableRealtimeWorkerOverviewUpdateFactory.Apply(
            current,
            update,
            new WorkWorkerOverviewRealtimeCriteria(
                WorkerDuration: WorkComponentShapes.Standard,
                WorkerTimeline: WorkComponentShapes.Standard));

        var failedIteration = Assert.Single(next.TimelineItems, item => item.Id == "iteration:20");
        Assert.Null(failedIteration.Failure?.PendingState);
        Assert.Contains(next.TimelineItems, item => item.Id == "iteration:21" && item.IterationStatus == WorkCompletionStatus.Executing);
    }

    [Fact]
    public void CreateForNextIterationStartClearsRetryPendingOnPreviousFailedIteration()
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var workerId = WorkerId.New();
        var current = new WorkWorkerOverviewRealtimeState(
            CreateWorker(workerId: workerId, state: WorkerState.Retrying, retryAttempt: 2),
            new WorkWorkerOverviewLatestIteration(
                workerId,
                20,
                WorkCompletionStatus.Failed,
                occurredAt.AddSeconds(-5),
                occurredAt.AddSeconds(-2),
                TimeSpan.Zero,
                null,
                new WorkWorkerOverviewFailure(
                    WorkWorkerOverviewFailureKind.Exception,
                    "transient failure",
                    PendingState: new WorkWorkerOverviewPendingState(
                        WorkWorkerOverviewPendingStateMode.Retry,
                        occurredAt.AddSeconds(5),
                        occurredAt.AddSeconds(-1),
                        occurredAt.AddSeconds(-1),
                        2))),
            null,
            [],
            [],
            new WorkWorkerOverviewTimelineSummary(1, 0, 0, 1),
            [
                new WorkWorkerOverviewTimelineItem(
                    "iteration:20",
                    occurredAt.AddSeconds(-2),
                    WorkWorkerOverviewTimelineItemKind.Iteration,
                    WorkWorkerOverviewTimelineCategory.Failure,
                    null,
                    null,
                    null,
                    null,
                    20,
                    WorkCompletionStatus.Failed,
                    TimeSpan.Zero,
                    null,
                    new WorkWorkerOverviewFailure(
                        WorkWorkerOverviewFailureKind.Exception,
                        "transient failure",
                        PendingState: new WorkWorkerOverviewPendingState(
                            WorkWorkerOverviewPendingStateMode.Retry,
                            occurredAt.AddSeconds(5),
                            occurredAt.AddSeconds(-1),
                            occurredAt.AddSeconds(-1),
                            2))),
            ]);
        var criteria = new WorkWorkerOverviewRealtimeCriteria(
            WorkerDuration: WorkComponentShapes.Standard,
            WorkerTimeline: WorkComponentShapes.Standard);
        var update = WorkableRealtimeWorkerOverviewUpdateFactory.Create(
            CreateEvent(
                "worker.iteration.started",
                new
                {
                    Worker = new
                    {
                        UpdatedAt = occurredAt,
                        StateChangedAt = occurredAt,
                        State = WorkerState.Running,
                        NextRunAt = (DateTimeOffset?)null,
                        RetryAttempt = (int?)null,
                    },
                    Iteration = new
                    {
                        Sequence = 21L,
                        StartedAt = occurredAt,
                        CompletedAt = (DateTimeOffset?)null,
                        ExecutionDuration = (TimeSpan?)null,
                        Status = WorkCompletionStatus.Executing,
                    },
                },
                occurredAt),
            current,
            criteria);

        Assert.NotNull(update);
        var timelineItems = Assert.IsAssignableFrom<IReadOnlyList<WorkWorkerOverviewTimelineItem>>(update!.TimelineItems!);
        Assert.Contains(timelineItems, item => item.Id == "iteration:21" && item.IterationStatus == WorkCompletionStatus.Executing);

        var clearedFailedIteration = Assert.Single(timelineItems, item => item.Id == "iteration:20");
        Assert.Null(clearedFailedIteration.Failure?.PendingState);
    }

    [Fact]
    public void CreateForLogEventSkipsEntryWhenIterationFilterDoesNotMatch()
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var current = new WorkWorkerOverviewRealtimeState(
            CreateWorker(),
            CreateLatestIteration(sequence: 3, WorkCompletionStatus.Executing),
            new WorkWorkerOverviewLogSummary(10, 1, 2, 3, 4, 4, 5, 6, 7),
            [],
            [],
            null,
            []);
        var criteria = new WorkWorkerOverviewRealtimeCriteria(
            WorkerLogs: WorkComponentShapes.Standard,
            LogIterationSequence: 9);
        var workEvent = CreateEvent(
            "worker.log",
            new
            {
                Worker = new { UpdatedAt = occurredAt, StateChangedAt = occurredAt, State = WorkerState.Running, NextRunAt = (DateTimeOffset?)null, RetryAttempt = (int?)null },
                Iteration = new { Sequence = 3L, StartedAt = occurredAt.AddSeconds(-2), CompletedAt = (DateTimeOffset?)null, ExecutionDuration = (TimeSpan?)null, Status = WorkCompletionStatus.Executing },
                Log = new
                {
                    Id = "log-b",
                    Category = "Tests",
                    Level = "Warning",
                    EventId = new { Id = 7, Name = "warn" },
                    Message = "B",
                    ExceptionType = (string?)null,
                    ExceptionMessage = (string?)null,
                },
            },
            occurredAt);

        var update = WorkableRealtimeWorkerOverviewUpdateFactory.Create(workEvent, current, criteria);

        Assert.NotNull(update);
        Assert.NotNull(update.LogSummary);
        Assert.Null(update.LogEntries);
    }

    [Fact]
    public void CreateForPauseEventBuildsTimelineItemsFromPayload()
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var stateChangedAt = occurredAt.AddSeconds(1);
        var workerId = WorkerId.New();
        var current = new WorkWorkerOverviewRealtimeState(
            CreateWorker(workerId: workerId, state: WorkerState.Running),
            null,
            null,
            [],
            [],
            new WorkWorkerOverviewTimelineSummary(2, 1, 1, 0),
            []);
        var criteria = new WorkWorkerOverviewRealtimeCriteria(WorkerTimeline: WorkComponentShapes.Standard);
        var workEvent = CreateEvent(
            "worker.pause",
            new
            {
                Worker = new
                {
                    Revision = 2L,
                    StateSequence = 5L,
                    UpdatedAt = occurredAt,
                    StateChangedAt = stateChangedAt,
                    State = WorkerState.Paused,
                    NextRunAt = (DateTimeOffset?)null,
                    RetryAttempt = (int?)null,
                    TimelineSummary = new
                    {
                        Total = 2,
                        UserActionCount = 1,
                        SystemEventCount = 1,
                        FailureCount = 0,
                    },
                },
                Origin = new
                {
                    Channel = WorkInvocationChannel.HttpApi,
                    Actor = new { Id = "user-1", Name = "Greya", Email = "greya@example.test" },
                    Description = "Pause worker through HTTP API.",
                    Url = "/workable/workers/1/actions/pause",
                },
                Action = WorkAction.Pause,
                ActionStatus = WorkActionStatus.Accepted,
            },
            occurredAt);

        var update = WorkableRealtimeWorkerOverviewUpdateFactory.Create(workEvent, current, criteria);

        Assert.NotNull(update);
        Assert.NotNull(update.Worker);
        Assert.Equal(WorkerState.Paused, update.Worker!.State);
        Assert.NotNull(update.TimelineSummary);
        Assert.Collection(
            Assert.IsAssignableFrom<IReadOnlyList<WorkWorkerOverviewTimelineItem>>(update.TimelineItems!),
            action =>
            {
                Assert.Equal(WorkWorkerOverviewTimelineItemKind.ActionRequest, action.Kind);
                Assert.Equal(WorkWorkerOverviewTimelineCategory.UserAction, action.Category);
                Assert.Equal(WorkAction.Pause, action.Action);
                Assert.Equal(WorkActionStatus.Accepted, action.ActionStatus);
                Assert.Equal(WorkInvocationChannel.HttpApi, action.Origin!.Channel);
                Assert.Equal(stateChangedAt, action.At);
            },
            state =>
            {
                Assert.Equal(WorkWorkerOverviewTimelineItemKind.StateChange, state.Kind);
                Assert.Equal(WorkWorkerOverviewTimelineCategory.SystemEvent, state.Category);
                Assert.Equal(WorkerState.Paused, state.State);
                Assert.Equal(stateChangedAt, state.At);
            });
    }

    [Fact]
    public void ApplyMergesLogEntriesByIdAndRespectsSortDirection()
    {
        var workerId = WorkerId.New();
        var older = DateTimeOffset.UtcNow.AddMinutes(-3);
        var newer = older.AddMinutes(2);
        var current = new WorkWorkerOverviewRealtimeState(
            CreateWorker(workerId: workerId),
            CreateLatestIteration(sequence: 3, WorkCompletionStatus.Executing),
            new WorkWorkerOverviewLogSummary(2, 0, 1, 1, 1, 1, 0, 0, 0),
            [
                new WorkWorkerOverviewLogEntry(
                    "log-1",
                    older,
                    Microsoft.Extensions.Logging.LogLevel.Information,
                    "Tests",
                    "old",
                    1,
                    "old",
                    null,
                    null),
            ],
            [],
            null,
            []);
        var update = new WorkWorkerOverviewRealtimeUpdate(
            DateTimeOffset.UtcNow,
            LogEntries:
            [
                new WorkWorkerOverviewLogEntry(
                    "log-1",
                    newer,
                    Microsoft.Extensions.Logging.LogLevel.Warning,
                    "Tests",
                    "updated",
                    2,
                    "updated",
                    null,
                    null),
                new WorkWorkerOverviewLogEntry(
                    "log-2",
                    older.AddMinutes(1),
                    Microsoft.Extensions.Logging.LogLevel.Error,
                    "Tests",
                    "second",
                    3,
                    "second",
                    null,
                    null),
            ]);
        var criteria = new WorkWorkerOverviewRealtimeCriteria(
            WorkerLogs: WorkComponentShapes.Standard,
            LogSortDirection: WorkWorkerOverviewSortDirection.Desc);

        var next = WorkableRealtimeWorkerOverviewUpdateFactory.Apply(current, update, criteria);

        Assert.Collection(
            next.LogEntries,
            entry =>
            {
                Assert.Equal("log-1", entry.Id);
                Assert.Equal("updated", entry.Message);
                Assert.Equal(newer, entry.OccurredAt);
            },
            entry =>
            {
                Assert.Equal("log-2", entry.Id);
                Assert.Equal("second", entry.Message);
            });
    }

    [Fact]
    public void ApplyMergesTimelineItemsByIdAndRespectsAscendingSort()
    {
        var workerId = WorkerId.New();
        var first = DateTimeOffset.UtcNow.AddMinutes(-4);
        var second = first.AddMinutes(1);
        var third = second.AddMinutes(1);
        var current = new WorkWorkerOverviewRealtimeState(
            CreateWorker(workerId: workerId),
            null,
            null,
            [],
            [],
            new WorkWorkerOverviewTimelineSummary(2, 1, 1, 0),
            [
                new WorkWorkerOverviewTimelineItem(
                    "timeline-2",
                    second,
                    WorkWorkerOverviewTimelineItemKind.StateChange,
                    WorkWorkerOverviewTimelineCategory.SystemEvent,
                    null,
                    null,
                    null,
                    WorkerState.Waiting,
                    null,
                    null,
                    null,
                    null,
                    null),
            ]);
        var update = new WorkWorkerOverviewRealtimeUpdate(
            DateTimeOffset.UtcNow,
            TimelineItems:
            [
                new WorkWorkerOverviewTimelineItem(
                    "timeline-1",
                    first,
                    WorkWorkerOverviewTimelineItemKind.ActionRequest,
                    WorkWorkerOverviewTimelineCategory.UserAction,
                    WorkerActionHistoryKind.WorkerAction,
                    WorkAction.Pause,
                    WorkActionStatus.Accepted,
                    null,
                    null,
                    null,
                    null,
                    new WorkWorkerOverviewOrigin(WorkInvocationChannel.HttpApi),
                    null),
                new WorkWorkerOverviewTimelineItem(
                    "timeline-2",
                    third,
                    WorkWorkerOverviewTimelineItemKind.StateChange,
                    WorkWorkerOverviewTimelineCategory.SystemEvent,
                    null,
                    null,
                    null,
                    WorkerState.Paused,
                    null,
                    null,
                    null,
                    null,
                    null),
            ]);
        var criteria = new WorkWorkerOverviewRealtimeCriteria(
            WorkerTimeline: WorkComponentShapes.Standard,
            TimelineSortDirection: WorkWorkerOverviewSortDirection.Asc);

        var next = WorkableRealtimeWorkerOverviewUpdateFactory.Apply(current, update, criteria);

        Assert.Collection(
            next.TimelineItems,
            item => Assert.Equal("timeline-1", item.Id),
            item =>
            {
                Assert.Equal("timeline-2", item.Id);
                Assert.Equal(WorkerState.Paused, item.State);
                Assert.Equal(third, item.At);
            });
    }

    [Fact]
    public void CoalescePreservesRetryPendingOnFailedIterationAcrossFailureAndRetryUpdates()
    {
        var occurredAt = DateTimeOffset.UtcNow;
        var retryAt = occurredAt.AddSeconds(30);
        var workerId = WorkerId.New();
        var current = new WorkWorkerOverviewRealtimeState(
            CreateWorker(workerId: workerId, state: WorkerState.Running),
            CreateLatestIteration(sequence: 20, WorkCompletionStatus.Executing),
            null,
            [],
            [],
            new WorkWorkerOverviewTimelineSummary(1, 0, 1, 0),
            [
                new WorkWorkerOverviewTimelineItem(
                    "iteration:20",
                    occurredAt.AddSeconds(-1),
                    WorkWorkerOverviewTimelineItemKind.Iteration,
                    WorkWorkerOverviewTimelineCategory.SystemEvent,
                    null,
                    null,
                    null,
                    null,
                    20,
                    WorkCompletionStatus.Executing,
                    null,
                    null,
                    null),
            ]);
        var criteria = new WorkWorkerOverviewRealtimeCriteria(
            WorkerTimeline: WorkComponentShapes.Standard,
            WorkerDuration: WorkComponentShapes.Standard);

        var failedUpdate = WorkableRealtimeWorkerOverviewUpdateFactory.Create(
            CreateEvent(
                "worker.failed",
                new
                {
                    Worker = new
                    {
                        UpdatedAt = occurredAt,
                        StateChangedAt = occurredAt,
                        State = WorkerState.Failed,
                        NextRunAt = (DateTimeOffset?)null,
                        RetryAttempt = 2,
                    },
                    Iteration = new
                    {
                        Sequence = 20L,
                        StartedAt = occurredAt.AddSeconds(-2),
                        CompletedAt = occurredAt,
                        ExecutionDuration = TimeSpan.Zero,
                        Status = WorkCompletionStatus.Failed,
                        Failure = new
                        {
                            Kind = WorkWorkerOverviewFailureKind.Exception,
                            Message = "Recurring sample hit a transient failure.",
                        },
                    },
                },
                occurredAt),
            current,
            criteria);
        Assert.NotNull(failedUpdate);

        var afterFailed = WorkableRealtimeWorkerOverviewUpdateFactory.Apply(current, failedUpdate!, criteria);
        var retryingUpdate = WorkableRealtimeWorkerOverviewUpdateFactory.Create(
            CreateEvent(
                "worker.retrying",
                new
                {
                    Worker = new
                    {
                        UpdatedAt = occurredAt.AddMilliseconds(10),
                        StateChangedAt = occurredAt.AddMilliseconds(10),
                        State = WorkerState.Retrying,
                        NextRunAt = retryAt,
                        RetryAttempt = 3,
                    },
                    Iteration = new
                    {
                        Sequence = 20L,
                        StartedAt = occurredAt.AddSeconds(-2),
                        CompletedAt = occurredAt,
                        ExecutionDuration = TimeSpan.Zero,
                        Status = WorkCompletionStatus.Failed,
                    },
                },
                occurredAt.AddMilliseconds(10)),
            afterFailed,
            criteria);
        Assert.NotNull(retryingUpdate);

        var batchedUpdate = WorkableRealtimeWorkerOverviewUpdateFactory.Coalesce(
            current,
            [failedUpdate!, retryingUpdate!],
            criteria,
            out var nextState);

        Assert.NotNull(batchedUpdate);
        Assert.Equal(WorkerState.Retrying, nextState.Worker.State);
        Assert.NotNull(batchedUpdate!.LatestIteration?.Failure?.PendingState);
        Assert.Equal(WorkWorkerOverviewPendingStateMode.Retry, batchedUpdate.LatestIteration!.Failure!.PendingState!.Mode);
        Assert.Equal(retryAt, batchedUpdate.LatestIteration.Failure.PendingState.NextRunAt);

        var failedTimelineItem = Assert.Single(
            Assert.IsAssignableFrom<IReadOnlyList<WorkWorkerOverviewTimelineItem>>(batchedUpdate.TimelineItems!),
            item => item.Id == "iteration:20");
        Assert.NotNull(failedTimelineItem.Failure?.PendingState);
        Assert.Equal(WorkWorkerOverviewPendingStateMode.Retry, failedTimelineItem.Failure!.PendingState!.Mode);
        Assert.Equal(retryAt, failedTimelineItem.Failure!.PendingState!.NextRunAt);
    }

    private static WorkEvent CreateEvent(string eventType, object payload, DateTimeOffset? occurredAt = null)
        => new(
            occurredAt ?? DateTimeOffset.UtcNow,
            new WorkSystemId(Guid.NewGuid()),
            "tests",
            WorkerId.New(),
            new WorkDefinitionId(Guid.NewGuid()),
            "signalr.worker-overview",
            null,
            null,
            new HashSet<WorkIdentifier>(),
            eventType,
            JsonSerializer.SerializeToElement(payload, WorkEventJson.Options));

    private static WorkWorkerOverviewRealtimeUpdate RequireUpdate(WorkWorkerOverviewRealtimeUpdate? update)
        => Require(update);

    private static T Require<T>(T? value)
        where T : class
    {
        Assert.NotNull(value);
        return value;
    }

    private static void AssertRejectsNull(string parameterName, Action action)
    {
        var exception = Assert.Throws<ArgumentNullException>(action);

        Assert.Equal(parameterName, exception.ParamName);
    }

    private static WorkWorkerOverviewRealtimeState CreateState()
        => new(
            CreateWorker(),
            null,
            null,
            [],
            [],
            null,
            []);

    private static WorkWorkerOverviewWorker CreateWorker(
        WorkerId? workerId = null,
        WorkerState state = WorkerState.Running,
        int? retryAttempt = null)
        => new(
            workerId ?? WorkerId.New(),
            1,
            4,
            state,
            false,
            DateTimeOffset.UtcNow.AddMinutes(-10),
            DateTimeOffset.UtcNow.AddMinutes(-1),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(5),
            retryAttempt,
            new WorkWorkerOverviewOrigin(WorkInvocationChannel.HttpApi),
            new WorkDefinitionId(Guid.NewGuid()),
            "signalr.worker-overview",
            "SignalR",
            0);

    private static WorkWorkerOverviewLatestIteration CreateLatestIteration(long sequence, WorkCompletionStatus status)
        => new(
            WorkerId.New(),
            sequence,
            status,
            DateTimeOffset.UtcNow.AddMinutes(-2),
            status == WorkCompletionStatus.Executing ? null : DateTimeOffset.UtcNow.AddMinutes(-1),
            status == WorkCompletionStatus.Executing ? null : TimeSpan.FromSeconds(1),
            null,
            null);
}
