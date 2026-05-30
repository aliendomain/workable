namespace Workable;

internal static class WorkableRealtimeWorkerOverviewUpdateFactory
{
    private const string LiveWaitingStateTimelineItemId = "live-state:waiting";

    public static WorkWorkerOverviewRealtimeUpdate CreateInitial(WorkWorkerOverviewRealtimeState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        return new WorkWorkerOverviewRealtimeUpdate(
            DateTimeOffset.UtcNow,
            state.Worker,
            state.LatestIteration,
            state.LogSummary,
            state.LogEntries,
            state.RecentIterations,
            state.TimelineSummary,
            state.TimelineItems);
    }

    public static WorkWorkerOverviewRealtimeUpdate? Create(
        WorkEvent workEvent,
        WorkWorkerOverviewRealtimeState current,
        WorkWorkerOverviewRealtimeCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(workEvent);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(criteria);

        var payload = workEvent.DeserializeData<WorkerOverviewRealtimeEventPayload>();
        return workEvent.EventType switch
        {
            "worker.log" => CreateLogUpdate(workEvent, current, criteria, payload),
            "worker.start" or "worker.push" => CreateActionUpdate(workEvent, current, criteria, payload, null),
            "worker.iteration.started" or "worker.iteration.completed" or "worker.iteration.failed" or
            "worker.completed" or "worker.failed" or "worker.canceled" or "worker.interrupted" =>
                CreateIterationUpdate(current, criteria, payload),
            "worker.waiting" => CreateStateUpdate(current, criteria, payload, WorkerState.Waiting),
            "worker.retrying" => CreateRetryingUpdate(current, criteria, payload),
            "worker.pause" => CreateActionUpdate(workEvent, current, criteria, payload, WorkerState.Paused),
            "worker.cancel" => CreateActionUpdate(workEvent, current, criteria, payload, WorkerState.Canceled),
            _ => CreateWorkerOnlyUpdate(current, payload),
        };
    }

    public static WorkWorkerOverviewRealtimeState Apply(
        WorkWorkerOverviewRealtimeState current,
        WorkWorkerOverviewRealtimeUpdate update,
        WorkWorkerOverviewRealtimeCriteria criteria)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(update);
        ArgumentNullException.ThrowIfNull(criteria);

        var nextWorker = update.Worker ?? current.Worker;
        var nextLatestIteration = update.LatestIteration ?? current.LatestIteration;
        var nextLogEntries = update.LogEntries is null
            ? current.LogEntries
            : MergeLogEntries(current.LogEntries, update.LogEntries, criteria.LogSortDirection);
        var nextRecentIterations = update.RecentIterations is null
            ? current.RecentIterations
            : MergeRecentIterations(current.RecentIterations, update.RecentIterations);
        var nextTimelineItems = update.TimelineItems is null
            ? current.TimelineItems
            : MergeTimelineItems(current.TimelineItems, update.TimelineItems, criteria.TimelineSortDirection);

        return new WorkWorkerOverviewRealtimeState(
            nextWorker,
            nextLatestIteration,
            update.LogSummary ?? current.LogSummary,
            nextLogEntries,
            nextRecentIterations,
            update.TimelineSummary ?? current.TimelineSummary,
            NormalizeLiveTimelineItems(nextTimelineItems, nextWorker.State, nextLatestIteration, criteria.TimelineSortDirection));
    }

    public static WorkWorkerOverviewRealtimeUpdate? Coalesce(
        WorkWorkerOverviewRealtimeState current,
        IReadOnlyList<WorkWorkerOverviewRealtimeUpdate> updates,
        WorkWorkerOverviewRealtimeCriteria criteria,
        out WorkWorkerOverviewRealtimeState nextState)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(updates);
        ArgumentNullException.ThrowIfNull(criteria);

        nextState = current;
        if (updates.Count == 0)
        {
            return null;
        }

        if (updates.Count == 1)
        {
            nextState = Apply(current, updates[0], criteria);
            return updates[0];
        }

        var includesWorker = false;
        var includesLatestIteration = false;
        var includesLogSummary = false;
        var includesRecentIterations = false;
        var includesTimelineSummary = false;
        var logEntries = new List<WorkWorkerOverviewLogEntry>();
        var timelineItems = new List<WorkWorkerOverviewTimelineItem>();
        var recentIterations = new List<WorkWorkerOverviewRecentIteration>();

        foreach (var update in updates)
        {
            includesWorker |= update.Worker is not null;
            includesLatestIteration |= update.LatestIteration is not null;
            includesLogSummary |= update.LogSummary is not null;
            includesRecentIterations |= update.RecentIterations is { Count: > 0 };
            includesTimelineSummary |= update.TimelineSummary is not null;

            if (update.LogEntries is { Count: > 0 })
            {
                logEntries.AddRange(update.LogEntries);
            }

            if (update.RecentIterations is { Count: > 0 })
            {
                recentIterations.AddRange(update.RecentIterations);
            }

            if (update.TimelineItems is { Count: > 0 })
            {
                timelineItems.AddRange(update.TimelineItems);
            }

            nextState = Apply(nextState, update, criteria);
        }

        return CreateUpdate(
            worker: includesWorker ? nextState.Worker : null,
            latestIteration: includesLatestIteration ? nextState.LatestIteration : null,
            logSummary: includesLogSummary ? nextState.LogSummary : null,
            logEntries: logEntries.Count == 0
                ? null
                : MergeLogEntries([], logEntries, criteria.LogSortDirection),
            recentIterations: recentIterations.Count == 0
                ? null
                : MergeRecentIterations([], recentIterations),
            timelineSummary: includesTimelineSummary ? nextState.TimelineSummary : null,
            timelineItems: timelineItems.Count == 0
                ? null
                : NormalizeLiveTimelineItems(
                    MergeTimelineItems([], timelineItems, criteria.TimelineSortDirection),
                    nextState.Worker.State,
                    nextState.LatestIteration,
                    criteria.TimelineSortDirection));
    }

    private static WorkWorkerOverviewRealtimeUpdate? CreateLogUpdate(
        WorkEvent workEvent,
        WorkWorkerOverviewRealtimeState current,
        WorkWorkerOverviewRealtimeCriteria criteria,
        WorkerOverviewRealtimeEventPayload? payload)
    {
        var logSummary = IncludesLogSummary(criteria.WorkerLogs)
            ? CreateLogSummaryFromPayload(payload) ?? current.LogSummary
            : null;
        var logEntries = IsExpandedRealtimeShape(criteria.WorkerLogs)
            ? CreateLogEntries(workEvent, criteria, payload)
            : null;

        return CreateUpdate(
            logSummary: logSummary,
            logEntries: logEntries);
    }

    private static WorkWorkerOverviewRealtimeUpdate? CreateIterationUpdate(
        WorkWorkerOverviewRealtimeState current,
        WorkWorkerOverviewRealtimeCriteria criteria,
        WorkerOverviewRealtimeEventPayload? payload)
    {
        var latestIteration = CreateLatestIterationFromPayload(current, payload);
        var sequence = payload?.Iteration?.Sequence ?? latestIteration?.Sequence;
        var recentIterations = IsExpandedRealtimeShape(criteria.WorkerDuration)
            ? CreateRecentIterations(current, payload)
            : null;
        var timelineSummary = IsExpandedRealtimeShape(criteria.WorkerTimeline)
            ? CreateTimelineSummaryFromPayload(payload) ?? current.TimelineSummary
            : null;
        var timelineItems = IsExpandedRealtimeShape(criteria.WorkerTimeline)
            ? CreateIterationTimelineItems(
                current,
                criteria,
                latestIteration,
                sequence,
                includeRetryPendingClear: true)
            : null;

        return CreateUpdate(
            worker: CreateWorkerFromPayload(current, payload),
            latestIteration: latestIteration,
            recentIterations: recentIterations,
            timelineSummary: timelineSummary,
            timelineItems: timelineItems);
    }

    private static WorkWorkerOverviewRealtimeUpdate? CreateStateUpdate(
        WorkWorkerOverviewRealtimeState current,
        WorkWorkerOverviewRealtimeCriteria criteria,
        WorkerOverviewRealtimeEventPayload? payload,
        WorkerState state)
    {
        var worker = CreateWorkerFromPayload(current, payload);
        var timelineSummary = IsExpandedRealtimeShape(criteria.WorkerTimeline)
            ? CreateTimelineSummaryFromPayload(payload) ?? current.TimelineSummary
            : null;
        var timelineItems = IsExpandedRealtimeShape(criteria.WorkerTimeline)
            ? CreateStateTimelineItems(current, criteria, payload, state)
            : null;

        return CreateUpdate(
            worker: worker,
            latestIteration: current.LatestIteration,
            timelineSummary: timelineSummary,
            timelineItems: timelineItems);
    }

    private static WorkWorkerOverviewRealtimeUpdate? CreateRetryingUpdate(
        WorkWorkerOverviewRealtimeState current,
        WorkWorkerOverviewRealtimeCriteria criteria,
        WorkerOverviewRealtimeEventPayload? payload)
    {
        var worker = CreateWorkerFromPayload(current, payload);
        var latestIteration = ApplyRetryPendingFromPayload(
            CreateLatestIterationFromPayload(current, payload),
            payload?.Worker);
        var sequence = payload?.Iteration?.Sequence ?? latestIteration?.Sequence;
        var timelineSummary = IsExpandedRealtimeShape(criteria.WorkerTimeline)
            ? CreateTimelineSummaryFromPayload(payload) ?? current.TimelineSummary
            : null;
        var timelineItems = IsExpandedRealtimeShape(criteria.WorkerTimeline)
            ? CreateIterationTimelineItems(
                current,
                criteria,
                latestIteration,
                sequence,
                includeRetryPendingClear: false)
            : null;

        return CreateUpdate(
            worker: worker,
            latestIteration: latestIteration,
            timelineSummary: timelineSummary,
            timelineItems: timelineItems);
    }

    private static WorkWorkerOverviewRealtimeUpdate? CreateActionUpdate(
        WorkEvent workEvent,
        WorkWorkerOverviewRealtimeState current,
        WorkWorkerOverviewRealtimeCriteria criteria,
        WorkerOverviewRealtimeEventPayload? payload,
        WorkerState? resultingState)
    {
        var worker = CreateWorkerFromPayload(current, payload);
        var timelineSummary = IsExpandedRealtimeShape(criteria.WorkerTimeline)
            ? CreateTimelineSummaryFromPayload(payload) ?? current.TimelineSummary
            : null;
        var timelineItems = IsExpandedRealtimeShape(criteria.WorkerTimeline)
            ? CreateActionTimelineItems(workEvent, current, criteria, payload, resultingState)
            : null;

        return CreateUpdate(
            worker: worker,
            latestIteration: current.LatestIteration,
            timelineSummary: timelineSummary,
            timelineItems: timelineItems);
    }

    private static WorkWorkerOverviewRealtimeUpdate? CreateWorkerOnlyUpdate(
        WorkWorkerOverviewRealtimeState current,
        WorkerOverviewRealtimeEventPayload? payload)
        => CreateUpdate(
            worker: CreateWorkerFromPayload(current, payload),
            latestIteration: CreateLatestIterationFromPayload(current, payload),
            logSummary: CreateLogSummaryFromPayload(payload),
            timelineSummary: CreateTimelineSummaryFromPayload(payload));

    private static WorkWorkerOverviewRealtimeUpdate? CreateUpdate(
        WorkWorkerOverviewWorker? worker = null,
        WorkWorkerOverviewLatestIteration? latestIteration = null,
        WorkWorkerOverviewLogSummary? logSummary = null,
        IReadOnlyList<WorkWorkerOverviewLogEntry>? logEntries = null,
        IReadOnlyList<WorkWorkerOverviewRecentIteration>? recentIterations = null,
        WorkWorkerOverviewTimelineSummary? timelineSummary = null,
        IReadOnlyList<WorkWorkerOverviewTimelineItem>? timelineItems = null)
    {
        var hasAnyChange = worker is not null ||
            latestIteration is not null ||
            logSummary is not null ||
            (logEntries?.Count > 0) ||
            (recentIterations?.Count > 0) ||
            timelineSummary is not null ||
            (timelineItems?.Count > 0);
        if (!hasAnyChange)
        {
            return null;
        }

        return new WorkWorkerOverviewRealtimeUpdate(
            DateTimeOffset.UtcNow,
            worker,
            latestIteration,
            logSummary,
            logEntries,
            recentIterations,
            timelineSummary,
            timelineItems);
    }

    private static IReadOnlyList<WorkWorkerOverviewLogEntry>? CreateLogEntries(
        WorkEvent workEvent,
        WorkWorkerOverviewRealtimeCriteria criteria,
        WorkerOverviewRealtimeEventPayload? payload)
    {
        if (payload is null)
        {
            return null;
        }

        var log = payload.Log;
        if (log is null)
        {
            return null;
        }

        if (criteria.LogIterationSequence.HasValue &&
            payload.Iteration?.Sequence != criteria.LogIterationSequence.Value)
        {
            return null;
        }

        if (TryParseLogLevel(log.Level, out var level) is false || !MatchesLogCriteria(criteria, level))
        {
            return null;
        }

        return [
            new WorkWorkerOverviewLogEntry(
                log.Id,
                workEvent.OccurredAt,
                level,
                log.Category,
                log.Message,
                log.EventId.Id,
                log.EventId.Name,
                log.ExceptionType,
                log.ExceptionMessage,
                payload.Iteration?.Sequence,
                log.Ordinal),
        ];
    }

    private static IReadOnlyList<WorkWorkerOverviewRecentIteration>? CreateRecentIterations(
        WorkWorkerOverviewRealtimeState current,
        WorkerOverviewRealtimeEventPayload? payload)
    {
        if (payload?.Iteration is not { } iteration)
        {
            return null;
        }

        return [
            new WorkWorkerOverviewRecentIteration(
                current.Worker.WorkerId,
                iteration.Sequence,
                iteration.Status,
                iteration.StartedAt,
                iteration.CompletedAt,
                iteration.ExecutionDuration,
                iteration.AttemptCount),
        ];
    }

    private static IReadOnlyList<WorkWorkerOverviewTimelineItem>? CreateIterationTimelineItems(
        WorkWorkerOverviewRealtimeState current,
        WorkWorkerOverviewRealtimeCriteria criteria,
        WorkWorkerOverviewLatestIteration? latestIteration,
        long? sequence,
        bool includeRetryPendingClear)
    {
        if (!sequence.HasValue)
        {
            return null;
        }

        var sequenceValue = sequence.Value;
        var items = new List<WorkWorkerOverviewTimelineItem>();
        if (includeRetryPendingClear &&
            TryCreateClearedRetryPendingTimelineItem(current, latestIteration) is { } clearedRetryPendingItem &&
            MatchesTimelineCriteria(criteria, clearedRetryPendingItem.Category))
        {
            items.Add(clearedRetryPendingItem);
        }

        if (latestIteration is not null && latestIteration.Sequence == sequenceValue)
        {
            var latestIterationItem = CreateIterationTimelineItem(
                latestIteration,
                current.Worker.State == WorkerState.Retrying
                    ? latestIteration.Failure?.PendingState
                    : null);
            if (MatchesTimelineCriteria(criteria, latestIterationItem.Category))
            {
                items.Add(latestIterationItem);
            }
            return items;
        }

        var existingItems = current.TimelineItems
            .Where(item => item.Kind == WorkWorkerOverviewTimelineItemKind.Iteration &&
                item.Sequence == sequenceValue)
            .ToArray();
        if (existingItems.Length == 0)
        {
            return items.Count == 0 ? null : items;
        }

        items.AddRange(existingItems.Where(item => MatchesTimelineCriteria(criteria, item.Category)));
        return items;
    }

    private static WorkWorkerOverviewTimelineItem? TryCreateClearedRetryPendingTimelineItem(
        WorkWorkerOverviewRealtimeState current,
        WorkWorkerOverviewLatestIteration? nextLatestIteration)
    {
        var currentLatestIteration = current.LatestIteration;
        if (currentLatestIteration?.Failure?.PendingState?.Mode != WorkWorkerOverviewPendingStateMode.Retry)
        {
            return null;
        }

        if (nextLatestIteration is null ||
            nextLatestIteration.Sequence == currentLatestIteration.Sequence ||
            nextLatestIteration.Status != WorkCompletionStatus.Executing)
        {
            return null;
        }

        var failureWithoutPendingState = currentLatestIteration.Failure with
        {
            PendingState = null,
        };

        return new WorkWorkerOverviewTimelineItem(
            $"iteration:{currentLatestIteration.Sequence}",
            currentLatestIteration.CompletedAt ?? currentLatestIteration.StartedAt,
            WorkWorkerOverviewTimelineItemKind.Iteration,
            currentLatestIteration.Status == WorkCompletionStatus.Failed
                ? WorkWorkerOverviewTimelineCategory.Failure
                : WorkWorkerOverviewTimelineCategory.SystemEvent,
            null,
            null,
            null,
            null,
            currentLatestIteration.Sequence,
            currentLatestIteration.Status,
            currentLatestIteration.ExecutionDuration,
            null,
            failureWithoutPendingState,
            AttemptCount: currentLatestIteration.AttemptCount);
    }

    private static WorkWorkerOverviewLogSummary? CreateLogSummaryFromPayload(WorkerOverviewRealtimeEventPayload? payload)
        => payload?.Worker?.LogSummary is not { } summary
            ? null
            : new WorkWorkerOverviewLogSummary(
                summary.Total,
                summary.Critical,
                summary.Error,
                summary.Errors,
                summary.Warning,
                summary.Warnings,
                summary.Information,
                summary.Debug,
                summary.Trace);

    private static WorkWorkerOverviewTimelineSummary? CreateTimelineSummaryFromPayload(WorkerOverviewRealtimeEventPayload? payload)
        => payload?.Worker?.TimelineSummary is not { } summary
            ? null
            : new WorkWorkerOverviewTimelineSummary(
                summary.Total,
                summary.UserActionCount,
                summary.SystemEventCount,
                summary.FailureCount);

    private static IReadOnlyList<WorkWorkerOverviewTimelineItem>? CreateStateTimelineItems(
        WorkWorkerOverviewRealtimeState current,
        WorkWorkerOverviewRealtimeCriteria criteria,
        WorkerOverviewRealtimeEventPayload? payload,
        WorkerState state)
    {
        if (!MatchesTimelineCriteria(criteria, WorkWorkerOverviewTimelineCategory.SystemEvent) ||
            payload?.Worker is not { } worker)
        {
            return null;
        }

        var pendingState = state switch
        {
            WorkerState.Waiting => new WorkWorkerOverviewPendingState(
                WorkWorkerOverviewPendingStateMode.Recurrence,
                worker.NextRunAt,
                worker.StateChangedAt,
                worker.UpdatedAt,
                worker.RetryAttempt),
            _ => null,
        };

        return [
            new WorkWorkerOverviewTimelineItem(
                Id: CreateLiveStateTimelineItemId(state, worker.StateSequence),
                At: worker.StateChangedAt,
                Kind: WorkWorkerOverviewTimelineItemKind.StateChange,
                Category: WorkWorkerOverviewTimelineCategory.SystemEvent,
                ActionHistoryKind: null,
                Action: null,
                ActionStatus: null,
                State: state,
                Sequence: null,
                IterationStatus: null,
                ExecutionDuration: null,
                Origin: null,
                Failure: null,
                PendingState: pendingState),
        ];
    }

    private static IReadOnlyList<WorkWorkerOverviewTimelineItem> NormalizeLiveTimelineItems(
        IReadOnlyList<WorkWorkerOverviewTimelineItem> items,
        WorkerState workerState,
        WorkWorkerOverviewLatestIteration? latestIteration,
        WorkWorkerOverviewSortDirection direction)
    {
        var filtered = items
            .Where(item => ShouldKeepTimelineItem(item, items, workerState))
            .Select(item => NormalizeTimelineItemPendingState(item, workerState, latestIteration))
            .ToArray();

        return direction == WorkWorkerOverviewSortDirection.Asc
            ? filtered
                .OrderBy(item => item.At)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToArray()
            : filtered
                .OrderByDescending(item => item.At)
                .ThenByDescending(item => item.Id, StringComparer.Ordinal)
                .ToArray();
    }

    private static bool ShouldRetainLiveWaitingStateItem(
        IReadOnlyList<WorkWorkerOverviewTimelineItem> items,
        WorkerState workerState)
        => workerState == WorkerState.Waiting &&
            items.All(item => item.Kind != WorkWorkerOverviewTimelineItemKind.Iteration ||
                item.IterationStatus != WorkCompletionStatus.Executing);

    private static bool ShouldKeepTimelineItem(
        WorkWorkerOverviewTimelineItem item,
        IReadOnlyList<WorkWorkerOverviewTimelineItem> items,
        WorkerState workerState)
        => !string.Equals(item.Id, LiveWaitingStateTimelineItemId, StringComparison.Ordinal) ||
            ShouldRetainLiveWaitingStateItem(items, workerState);

    private static WorkWorkerOverviewTimelineItem NormalizeTimelineItemPendingState(
        WorkWorkerOverviewTimelineItem item,
        WorkerState workerState,
        WorkWorkerOverviewLatestIteration? latestIteration)
    {
        if (item.Failure?.PendingState?.Mode != WorkWorkerOverviewPendingStateMode.Retry)
        {
            return item;
        }

        var shouldKeepRetryPending = workerState == WorkerState.Retrying &&
            latestIteration is not null &&
            latestIteration.Sequence == item.Sequence &&
            latestIteration.Status == WorkCompletionStatus.Failed;

        return shouldKeepRetryPending
            ? item
            : item with
            {
                Failure = item.Failure with
                {
                    PendingState = null,
                },
            };
    }

    private static IReadOnlyList<WorkWorkerOverviewLogEntry> MergeLogEntries(
        IReadOnlyList<WorkWorkerOverviewLogEntry> current,
        IReadOnlyList<WorkWorkerOverviewLogEntry> update,
        WorkWorkerOverviewSortDirection direction)
    {
        var byId = new Dictionary<string, WorkWorkerOverviewLogEntry>(StringComparer.Ordinal);
        foreach (var entry in current)
        {
            byId[entry.Id] = entry;
        }

        foreach (var entry in update)
        {
            byId[entry.Id] = entry;
        }

        var merged = byId.Values.ToArray();

        return direction == WorkWorkerOverviewSortDirection.Asc
            ? merged
                .OrderBy(entry => entry.OccurredAt)
                .ThenBy(entry => entry.Id, StringComparer.Ordinal)
                .ToArray()
            : merged
                .OrderByDescending(entry => entry.OccurredAt)
                .ThenByDescending(entry => entry.Id, StringComparer.Ordinal)
                .ToArray();
    }

    private static IReadOnlyList<WorkWorkerOverviewRecentIteration> MergeRecentIterations(
        IReadOnlyList<WorkWorkerOverviewRecentIteration> current,
        IReadOnlyList<WorkWorkerOverviewRecentIteration> update)
    {
        var targetCount = Math.Max(current.Count, 25);
        return current
            .Concat(update)
            .GroupBy(iteration => iteration.Sequence)
            .Select(group => group
                .OrderByDescending(iteration => iteration.CompletedAt ?? iteration.StartedAt)
                .ThenByDescending(iteration => iteration.Sequence)
                .First())
            .OrderByDescending(iteration => iteration.Sequence)
            .Take(targetCount)
            .ToArray();
    }

    private static IReadOnlyList<WorkWorkerOverviewTimelineItem> MergeTimelineItems(
        IReadOnlyList<WorkWorkerOverviewTimelineItem> current,
        IReadOnlyList<WorkWorkerOverviewTimelineItem> update,
        WorkWorkerOverviewSortDirection direction)
    {
        var byId = new Dictionary<string, WorkWorkerOverviewTimelineItem>(StringComparer.Ordinal);
        foreach (var item in current)
        {
            byId[item.Id] = item;
        }

        foreach (var item in update)
        {
            byId[item.Id] = item;
        }

        var merged = byId.Values.ToArray();

        return direction == WorkWorkerOverviewSortDirection.Asc
            ? merged
                .OrderBy(item => item.At)
                .ThenBy(item => item.Id, StringComparer.Ordinal)
                .ToArray()
            : merged
                .OrderByDescending(item => item.At)
                .ThenByDescending(item => item.Id, StringComparer.Ordinal)
                .ToArray();
    }

    private static IReadOnlyList<WorkWorkerOverviewTimelineItem>? CreateActionTimelineItems(
        WorkEvent workEvent,
        WorkWorkerOverviewRealtimeState current,
        WorkWorkerOverviewRealtimeCriteria criteria,
        WorkerOverviewRealtimeEventPayload? payload,
        WorkerState? resultingState)
    {
        var items = new List<WorkWorkerOverviewTimelineItem>();
        var acceptedTransitionAt = resultingState.HasValue &&
            payload?.ActionStatus == WorkActionStatus.Accepted &&
            payload.Worker is { } resultingWorker
            ? resultingWorker.StateChangedAt
            : (DateTimeOffset?)null;
        if (payload?.Action is { } action &&
            MatchesTimelineCriteria(
                criteria,
                IsUserOrigin(payload.Origin)
                    ? WorkWorkerOverviewTimelineCategory.UserAction
                    : WorkWorkerOverviewTimelineCategory.SystemEvent))
        {
            var actionAt = acceptedTransitionAt ?? workEvent.OccurredAt;
            items.Add(new WorkWorkerOverviewTimelineItem(
                Id: $"action:{action}:{actionAt.ToUnixTimeMilliseconds()}:{payload.Worker?.StateSequence ?? current.Worker.StateSequence}",
                At: actionAt,
                Kind: WorkWorkerOverviewTimelineItemKind.ActionRequest,
                Category: IsUserOrigin(payload.Origin)
                    ? WorkWorkerOverviewTimelineCategory.UserAction
                    : WorkWorkerOverviewTimelineCategory.SystemEvent,
                ActionHistoryKind: WorkerActionHistoryKind.WorkerAction,
                Action: action,
                ActionStatus: payload.ActionStatus,
                State: null,
                Sequence: null,
                IterationStatus: null,
                ExecutionDuration: null,
                Origin: payload.Origin is null ? null : CreateOrigin(payload.Origin),
                Failure: null));
        }

        if (resultingState.HasValue &&
            payload?.ActionStatus == WorkActionStatus.Accepted &&
            payload.Worker is { } worker &&
            MatchesTimelineCriteria(criteria, WorkWorkerOverviewTimelineCategory.SystemEvent))
        {
            items.Add(new WorkWorkerOverviewTimelineItem(
                $"action-state:{resultingState.Value.ToString().ToLowerInvariant()}:{worker.StateSequence}",
                worker.StateChangedAt,
                WorkWorkerOverviewTimelineItemKind.StateChange,
                WorkWorkerOverviewTimelineCategory.SystemEvent,
                null,
                null,
                null,
                resultingState,
                null,
                null,
                null,
                null,
                null,
                null));
        }

        return items.Count == 0 ? null : items;
    }

    private static WorkWorkerOverviewWorker? CreateWorkerFromPayload(
        WorkWorkerOverviewRealtimeState current,
        WorkerOverviewRealtimeEventPayload? payload)
    {
        if (payload?.Worker is not { } worker)
        {
            return current.Worker;
        }

        return current.Worker with
        {
            Revision = worker.Revision,
            StateSequence = worker.StateSequence,
            State = worker.State,
            StateChangedAt = worker.StateChangedAt,
            UpdatedAt = worker.UpdatedAt,
            NextRunAt = worker.NextRunAt,
            RetryAttempt = worker.RetryAttempt,
            ConfigDifferenceCount = worker.ConfigDifferenceCount,
        };
    }

    private static WorkWorkerOverviewLatestIteration? CreateLatestIterationFromPayload(
        WorkWorkerOverviewRealtimeState current,
        WorkerOverviewRealtimeEventPayload? payload)
    {
        if (payload?.Iteration is not { } iteration)
        {
            return current.LatestIteration;
        }

        if (current.LatestIteration?.Sequence == iteration.Sequence)
        {
            return current.LatestIteration with
            {
                Status = iteration.Status,
                StartedAt = iteration.StartedAt,
                CompletedAt = iteration.CompletedAt,
                ExecutionDuration = iteration.ExecutionDuration,
                AttemptCount = iteration.AttemptCount,
                Output = iteration.Output ?? current.LatestIteration.Output,
                Failure = CreateFailure(iteration.Failure) ?? current.LatestIteration.Failure,
            };
        }

        return new WorkWorkerOverviewLatestIteration(
            current.Worker.WorkerId,
            iteration.Sequence,
            iteration.Status,
            iteration.StartedAt,
            iteration.CompletedAt,
            iteration.ExecutionDuration,
            iteration.Output,
            CreateFailure(iteration.Failure),
            iteration.AttemptCount);
    }

    private static WorkWorkerOverviewLatestIteration? ApplyRetryPendingFromPayload(
        WorkWorkerOverviewLatestIteration? iteration,
        WorkerOverviewRealtimeEventWorker? worker)
    {
        if (iteration?.Failure is null || worker?.State != WorkerState.Retrying)
        {
            return iteration;
        }

        return iteration with
        {
            Failure = iteration.Failure with
            {
                PendingState = new WorkWorkerOverviewPendingState(
                    WorkWorkerOverviewPendingStateMode.Retry,
                    worker.NextRunAt,
                    worker.StateChangedAt,
                    worker.UpdatedAt,
                    worker.RetryAttempt),
            },
        };
    }

    private static WorkWorkerOverviewTimelineItem CreateIterationTimelineItem(
        WorkWorkerOverviewLatestIteration iteration,
        WorkWorkerOverviewPendingState? pendingState)
        => new(
            $"iteration:{iteration.Sequence}",
            iteration.CompletedAt ?? iteration.StartedAt,
            WorkWorkerOverviewTimelineItemKind.Iteration,
            iteration.Status == WorkCompletionStatus.Failed
                ? WorkWorkerOverviewTimelineCategory.Failure
                : WorkWorkerOverviewTimelineCategory.SystemEvent,
            null,
            null,
            null,
            null,
            iteration.Sequence,
            iteration.Status,
            iteration.ExecutionDuration,
            null,
            iteration.Failure is null
                ? null
                : iteration.Failure with
                {
                    PendingState = pendingState ?? iteration.Failure.PendingState,
                },
            AttemptCount: iteration.AttemptCount);

    private static string CreateLiveStateTimelineItemId(WorkerState state, long stateSequence)
        => state == WorkerState.Waiting
            ? LiveWaitingStateTimelineItemId
            : $"state:{state.ToString().ToLowerInvariant()}:{stateSequence}";

    private static bool MatchesLogCriteria(WorkWorkerOverviewRealtimeCriteria criteria, Microsoft.Extensions.Logging.LogLevel level)
        => criteria.LogLevels is null || criteria.LogLevels.Count == 0 || criteria.LogLevels.Contains(level);

    private static bool MatchesTimelineCriteria(
        WorkWorkerOverviewRealtimeCriteria criteria,
        WorkWorkerOverviewTimelineCategory category)
        => criteria.TimelineCategories is null ||
            criteria.TimelineCategories.Count == 0 ||
            criteria.TimelineCategories.Contains(category);

    private static bool TryParseLogLevel(string value, out Microsoft.Extensions.Logging.LogLevel level)
        => Enum.TryParse(value, ignoreCase: true, out level);

    private static bool IncludesLogSummary(string shape)
        => string.Equals(shape, WorkComponentShapes.Compact, StringComparison.Ordinal) ||
            IsExpandedRealtimeShape(shape);

    private static bool IsExpandedRealtimeShape(string shape)
        => string.Equals(shape, WorkComponentShapes.Standard, StringComparison.Ordinal) ||
            string.Equals(shape, WorkComponentShapes.Detailed, StringComparison.Ordinal);

    private sealed record WorkerOverviewRealtimeEventPayload(
        WorkerOverviewRealtimeEventWorker? Worker = null,
        WorkerOverviewRealtimeEventOrigin? Origin = null,
        WorkAction? Action = null,
        WorkActionStatus? ActionStatus = null,
        WorkerOverviewRealtimeEventIteration? Iteration = null,
        WorkerOverviewRealtimeEventLog? Log = null);

    private sealed record WorkerOverviewRealtimeEventWorker(
        long Revision,
        long StateSequence,
        DateTimeOffset UpdatedAt,
        DateTimeOffset StateChangedAt,
        WorkerState State,
        DateTimeOffset? NextRunAt = null,
        int? RetryAttempt = null,
        int ConfigDifferenceCount = 0,
        WorkerOverviewRealtimeEventLogSummary? LogSummary = null,
        WorkerOverviewRealtimeEventTimelineSummary? TimelineSummary = null);

    private sealed record WorkerOverviewRealtimeEventIteration(
        long Sequence,
        DateTimeOffset StartedAt,
        DateTimeOffset? CompletedAt,
        TimeSpan? ExecutionDuration,
        WorkCompletionStatus Status,
        int AttemptCount,
        WorkOutput? Output = null,
        WorkerOverviewRealtimeEventFailure? Failure = null);

    private sealed record WorkerOverviewRealtimeEventLog(
        string Id,
        long? Ordinal,
        string Category,
        string Level,
        WorkerOverviewRealtimeEventLogEventId EventId,
        string Message,
        string? ExceptionType,
        string? ExceptionMessage);

    private sealed record WorkerOverviewRealtimeEventLogEventId(int Id, string? Name);

    private sealed record WorkerOverviewRealtimeEventLogSummary(
        int Total,
        int Critical,
        int Error,
        int Errors,
        int Warning,
        int Warnings,
        int Information,
        int Debug,
        int Trace);

    private sealed record WorkerOverviewRealtimeEventTimelineSummary(
        int Total,
        int UserActionCount,
        int SystemEventCount,
        int FailureCount);

    private sealed record WorkerOverviewRealtimeEventFailure(
        WorkerIterationFailureKind Kind,
        string Message,
        string? Code = null,
        string? Target = null,
        string? ExceptionType = null,
        string? StackTrace = null,
        bool DeclaredByWork = false);

    private sealed record WorkerOverviewRealtimeEventOrigin(
        string Channel,
        WorkerOverviewRealtimeEventActor? Actor = null,
        string? Description = null,
        string? Url = null);

    private sealed record WorkerOverviewRealtimeEventActor(
        string? Id = null,
        string? Name = null,
        string? Email = null);

    private static bool IsUserOrigin(WorkerOverviewRealtimeEventOrigin? origin)
        => !string.IsNullOrWhiteSpace(origin?.Actor?.Name) ||
            !string.IsNullOrWhiteSpace(origin?.Actor?.Id);

    private static WorkWorkerOverviewOrigin CreateOrigin(WorkerOverviewRealtimeEventOrigin origin)
        => new(
            Enum.TryParse<WorkInvocationChannel>(origin.Channel, ignoreCase: true, out var channel)
                ? channel
                : WorkInvocationChannel.DotNet,
            origin.Actor?.Id,
            origin.Actor?.Name,
            origin.Actor?.Email);

    private static WorkWorkerOverviewFailure? CreateFailure(WorkerOverviewRealtimeEventFailure? failure)
        => failure is null
            ? null
            : new WorkWorkerOverviewFailure(
                failure.Kind == WorkerIterationFailureKind.Exception
                    ? WorkWorkerOverviewFailureKind.Exception
                    : WorkWorkerOverviewFailureKind.Failure,
                failure.Message,
                failure.Code,
                failure.Target,
                failure.ExceptionType,
                failure.StackTrace,
                failure.DeclaredByWork);
}
