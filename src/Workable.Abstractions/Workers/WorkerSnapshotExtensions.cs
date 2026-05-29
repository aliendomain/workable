namespace Workable;

public static class WorkerSnapshotExtensions
{
    public static IReadOnlyList<WorkerIterationSnapshot> GetMergedIterations(this WorkerSnapshot worker)
    {
        ArgumentNullException.ThrowIfNull(worker);

        var merged = new Dictionary<long, WorkerIterationSnapshot>();
        foreach (var iteration in worker.Iterations)
        {
            merged[iteration.Sequence] = MergeIteration(
                merged.GetValueOrDefault(iteration.Sequence),
                iteration);
        }

        if (worker.CurrentIteration is not null)
        {
            merged[worker.CurrentIteration.Sequence] = MergeIteration(
                merged.GetValueOrDefault(worker.CurrentIteration.Sequence),
                worker.CurrentIteration);
        }

        return [.. merged.Values.OrderByDescending(iteration => iteration.Sequence)];
    }

    public static WorkerIterationSnapshot? GetLatestKnownIteration(this WorkerSnapshot worker)
    {
        ArgumentNullException.ThrowIfNull(worker);
        return worker.GetMergedIterations().FirstOrDefault();
    }

    public static IReadOnlyList<WorkerActivityEvent> GetActivityEvents(
        this WorkerSnapshot worker,
        IReadOnlyList<WorkerIterationSnapshot>? iterations = null)
    {
        ArgumentNullException.ThrowIfNull(worker);

        var items = new List<WorkerActivityEvent>();
        foreach (var entry in worker.ActionHistory.OrderBy(history => history.OccurredAt))
        {
            items.Add(CreateActionEvent(entry));
            var resultingState = CreateActionResultStateEvent(worker, entry);
            if (resultingState is not null)
            {
                items.Add(resultingState);
            }
        }

        items.AddRange((iterations ?? worker.GetMergedIterations()).Select(CreateIterationEvent));

        return [.. items
            .OrderByDescending(item => item.At)
            .ThenByDescending(GetSortOrder)];
    }

    private static WorkerIterationSnapshot MergeIteration(
        WorkerIterationSnapshot? existing,
        WorkerIterationSnapshot candidate)
    {
        if (existing is null)
        {
            return candidate;
        }

        var preferred = PreferIteration(existing, candidate);
        var secondary = ReferenceEquals(preferred, existing) ? candidate : existing;
        return secondary with
        {
            AttemptCount = Math.Max(preferred.AttemptCount, secondary.AttemptCount),
            CompletedAt = preferred.Status == WorkCompletionStatus.Executing
                ? secondary.CompletedAt
                : preferred.CompletedAt,
            ExecutionDuration = preferred.Status == WorkCompletionStatus.Executing &&
                    secondary.Status != WorkCompletionStatus.Executing
                ? secondary.ExecutionDuration
                : preferred.ExecutionDuration,
            Logs = MergeLogEntries(preferred.Logs, secondary.Logs),
            Messages = MergeMessages(preferred.Messages, secondary.Messages),
            Output = preferred.Output ?? secondary.Output,
            StartedAt = preferred.StartedAt == default ? secondary.StartedAt : preferred.StartedAt,
            Status = preferred.Status,
        };
    }

    private static WorkerIterationSnapshot PreferIteration(
        WorkerIterationSnapshot left,
        WorkerIterationSnapshot right)
    {
        var leftTerminal = left.Status != WorkCompletionStatus.Executing;
        var rightTerminal = right.Status != WorkCompletionStatus.Executing;
        if (leftTerminal != rightTerminal)
        {
            return rightTerminal ? right : left;
        }

        if (left.CompletedAt != right.CompletedAt)
        {
            return right.CompletedAt >= left.CompletedAt ? right : left;
        }

        return right.Sequence >= left.Sequence ? right : left;
    }

    private static IReadOnlyList<WorkMessage> MergeMessages(
        IReadOnlyList<WorkMessage>? primary,
        IReadOnlyList<WorkMessage>? secondary)
    {
        var merged = new Dictionary<string, WorkMessage>(StringComparer.Ordinal);
        foreach (var message in (primary ?? []).Concat(secondary ?? []))
        {
            merged[GetMessageKey(message)] = message;
        }

        return [.. merged.Values];
    }

    private static string GetMessageKey(WorkMessage message)
        => string.Join(
            "|",
            message.Code,
            message.Severity,
            message.Text,
            message.Target ?? string.Empty);

    private static IReadOnlyList<WorkerLogEntry> MergeLogEntries(
        IReadOnlyList<WorkerLogEntry>? primary,
        IReadOnlyList<WorkerLogEntry>? secondary)
    {
        var merged = new Dictionary<string, WorkerLogEntry>(StringComparer.Ordinal);
        foreach (var entry in (primary ?? []).Concat(secondary ?? []))
        {
            merged[GetLogEntryId(entry)] = entry;
        }

        return [.. merged.Values.OrderByDescending(entry => entry.OccurredAt).ThenByDescending(GetLogEntryId)];
    }

    private static string GetLogEntryId(WorkerLogEntry entry)
        => entry.Id.ToString("N");

    private static int GetSortOrder(WorkerActivityEvent item)
        => item.Kind switch
        {
            WorkerActivityEventKind.Iteration => 3,
            WorkerActivityEventKind.StateChange => 2,
            _ => 1,
        };

    private static WorkerActivityEvent CreateActionEvent(WorkerActionHistoryEntry entry)
    {
        var hasActor = !string.IsNullOrWhiteSpace(entry.Origin.Actor.Name) ||
            !string.IsNullOrWhiteSpace(entry.Origin.Actor.Id);
        return new WorkerActivityEvent(
            Id: $"action:{entry.Kind}:{entry.Action?.ToString() ?? "none"}:{entry.OccurredAt.ToUnixTimeMilliseconds()}:{entry.StateSequence}",
            At: entry.OccurredAt,
            Kind: WorkerActivityEventKind.ActionRequest,
            Category: hasActor ? WorkerActivityEventCategory.UserAction : WorkerActivityEventCategory.SystemEvent,
            ActionHistoryKind: entry.Kind,
            Action: entry.Action,
            ActionStatus: entry.Status,
            State: null,
            Sequence: entry.IterationSequence,
            IterationStatus: null,
            AttemptCount: null,
            ExecutionDuration: null,
            Origin: entry.Origin,
            Failure: null);
    }

    private static WorkerActivityEvent? CreateActionResultStateEvent(
        WorkerSnapshot worker,
        WorkerActionHistoryEntry entry)
    {
        if (entry.Status != WorkActionStatus.Accepted)
        {
            return null;
        }

        if (worker.StateSequence == entry.StateSequence && worker.State == entry.State)
        {
            return null;
        }

        return entry.State switch
        {
            WorkerState.Paused => new WorkerActivityEvent(
                Id: $"action-state:paused:{entry.OccurredAt.ToUnixTimeMilliseconds()}:{entry.StateSequence}",
                At: entry.OccurredAt,
                Kind: WorkerActivityEventKind.StateChange,
                Category: WorkerActivityEventCategory.SystemEvent,
                ActionHistoryKind: null,
                Action: null,
                ActionStatus: null,
                State: WorkerState.Paused,
                Sequence: null,
                IterationStatus: null,
                AttemptCount: null,
                ExecutionDuration: null,
                Origin: null,
                Failure: null),
            WorkerState.Canceled => new WorkerActivityEvent(
                Id: $"action-state:canceled:{entry.OccurredAt.ToUnixTimeMilliseconds()}:{entry.StateSequence}",
                At: entry.OccurredAt,
                Kind: WorkerActivityEventKind.StateChange,
                Category: WorkerActivityEventCategory.SystemEvent,
                ActionHistoryKind: null,
                Action: null,
                ActionStatus: null,
                State: WorkerState.Canceled,
                Sequence: null,
                IterationStatus: null,
                AttemptCount: null,
                ExecutionDuration: null,
                Origin: null,
                Failure: null),
            _ => null,
        };
    }

    private static WorkerActivityEvent CreateIterationEvent(WorkerIterationSnapshot iteration)
        => new(
            Id: $"iteration:{iteration.Sequence}",
            At: iteration.SettledAt ?? iteration.StartedAt,
            Kind: WorkerActivityEventKind.Iteration,
            Category: iteration.Status == WorkCompletionStatus.Failed
                ? WorkerActivityEventCategory.Failure
                : WorkerActivityEventCategory.SystemEvent,
            ActionHistoryKind: null,
            Action: null,
            ActionStatus: null,
            State: null,
            Sequence: iteration.Sequence,
            IterationStatus: iteration.Status,
            AttemptCount: iteration.AttemptCount,
            ExecutionDuration: iteration.SettledExecutionDuration,
            Origin: null,
            Failure: iteration.Failure);
}
