using System.Diagnostics.CodeAnalysis;

namespace Workable;
internal sealed class WorkerRetentionScheduler(
    WorkerIndex index,
    WorkSystemRetentionConfiguration systemRetention,
    Func<IReadOnlyList<WorkerId>, WorkDefinitionId?, int> purge) : IDisposable
{
    private const int PurgeBatchSize = 4096;
    private const int ScheduledPurgeTrimMinimumHighWaterMark = 65_536;
    private static readonly TimeSpan ScheduledPurgeTrimInterval = TimeSpan.FromMinutes(1);
    private static readonly WorkerState[] FinalStates = [WorkerState.Canceled, WorkerState.Completed];
    private readonly PriorityQueue<ScheduledPurge, DateTimeOffset> scheduledPurges = new();
    private readonly SortedSet<FinalWorkerRetentionEntry> finalWorkers = new(FinalWorkerRetentionEntryComparer.Instance);
    private readonly Dictionary<WorkDefinitionId, SortedSet<FinalWorkerRetentionEntry>> finalWorkersByDefinition = [];
    private readonly Dictionary<WorkerId, FinalWorkerRetentionEntry> finalWorkerEntriesById = [];
    private readonly Dictionary<WorkDefinitionId, int> countRetentionTargetsByDefinition = [];
    private readonly SemaphoreSlim signal = new(0);
    private readonly Lock sync = new();
    private CancellationTokenSource? cancellation;
    private Task? schedulerTask;
    private bool systemCountRetentionDirty;
    private bool signalPending;
    private int scheduledPurgeHighWaterMark;
    private DateTimeOffset lastScheduledPurgeTrimAt = DateTimeOffset.MinValue;
    private DateTimeOffset? lastRunAt;
    private TimeSpan lastRunDuration;
    private int lastPurgedCount;
    private long totalPurgedCount;
    private long nextScheduleGeneration;
    private string? schedulerFailureType;
    private string? schedulerFailureMessage;

    public WorkSystemRetentionDiagnostics Diagnostics
    {
        get
        {
            lock (this.sync)
            {
                var now = DateTimeOffset.UtcNow;
                var oldestDueAt = this.scheduledPurges.TryPeek(out _, out var dueAt)
                    ? dueAt
                    : (DateTimeOffset?)null;
                var oldestDuePurgeAge = oldestDueAt is { } scheduledAt && scheduledAt < now
                    ? now - scheduledAt
                    : TimeSpan.Zero;

                return new WorkSystemRetentionDiagnostics(
                    this.finalWorkerEntriesById.Count,
                    this.scheduledPurges.Count,
                    this.scheduledPurgeHighWaterMark,
                    oldestDueAt,
                    oldestDuePurgeAge,
                    this.countRetentionTargetsByDefinition.Count,
                    this.systemCountRetentionDirty,
                    this.lastRunAt,
                    this.lastRunDuration,
                    this.lastPurgedCount,
                    this.totalPurgedCount,
                    this.schedulerFailureType,
                    this.schedulerFailureMessage);
            }
        }
    }

    public void Start()
    {
        lock (this.sync)
        {
            if (this.schedulerTask is { IsCompleted: false })
            {
                return;
            }

            this.cancellation?.Dispose();
            this.cancellation = new CancellationTokenSource();
            using (ExecutionContext.SuppressFlow())
            {
                this.schedulerTask = Task.Run(() => this.Run(this.cancellation.Token), CancellationToken.None);
            }
        }

        this.Signal();
    }

    public async Task Stop(CancellationToken cancellationToken)
    {
        Task? task;
        lock (this.sync)
        {
            this.cancellation?.Cancel();
            task = this.schedulerTask;
        }

        this.Signal();

        if (task is not null)
        {
            await task.WaitAsync(cancellationToken);
        }
    }

    public void Clear()
    {
        lock (this.sync)
        {
            this.scheduledPurges.Clear();
            this.scheduledPurges.TrimExcess();
            this.finalWorkers.Clear();
            this.finalWorkersByDefinition.Clear();
            this.finalWorkerEntriesById.Clear();
            this.countRetentionTargetsByDefinition.Clear();
            this.systemCountRetentionDirty = false;
            this.scheduledPurgeHighWaterMark = 0;
            this.lastScheduledPurgeTrimAt = DateTimeOffset.MinValue;
        }
    }

    public void Schedule(WorkerRecord worker)
        => this.Schedule(worker, includeInCountRetention: true);

    public void ScheduleDeferred(WorkerRecord worker)
        => this.Schedule(worker, includeInCountRetention: false);

    private void Schedule(WorkerRecord worker, bool includeInCountRetention)
    {
        if (!worker.IsFinal)
        {
            return;
        }

        var dueAt = DateTimeOffset.UtcNow + worker.Configuration.Retention.PurgeInterval;
        lock (this.sync)
        {
            var scheduleGeneration = ++this.nextScheduleGeneration;
            this.TrackFinalWorkerLocked(worker, includeInCountRetention, scheduleGeneration);
            this.scheduledPurges.Enqueue(new ScheduledPurge(worker.Id, scheduleGeneration), dueAt);
            this.scheduledPurgeHighWaterMark = Math.Max(
                this.scheduledPurgeHighWaterMark,
                this.scheduledPurges.Count);
            if (includeInCountRetention)
            {
                this.countRetentionTargetsByDefinition[worker.Work.Definition.Id] = worker.Configuration.Retention.MaximumFinalWorkers;
                this.systemCountRetentionDirty = true;
            }
        }

        this.Signal();
    }

    public void Forget(WorkerId workerId)
    {
        lock (this.sync)
        {
            this.RemoveFinalWorkerLocked(workerId);
        }
    }

    public void Dispose()
    {
        lock (this.sync)
        {
            this.cancellation?.Cancel();
            this.cancellation?.Dispose();
        }

        this.Signal();
    }

    private async Task Run(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (this.TryTakeDuePurgeBatch(
                    out var scheduledPurgeWorkerIds,
                    out var scheduledPurgeDefinitionId))
                {
                    this.TryPurge(
                        scheduledPurgeWorkerIds,
                        scheduledPurgeDefinitionId,
                        cancellationToken);
                    continue;
                }

                if (this.TryTakeCountRetentionWork(out var definitionIds, out var enforceSystemCap))
                {
                    this.EnforceCountRetention(definitionIds, enforceSystemCap, cancellationToken);
                    continue;
                }

                this.TrimScheduledPurgeQueueIfNeeded();
                await this.WaitForSignal(this.GetDelayUntilNextPurge(), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
    }

    private bool TryTakeDuePurgeBatch(
        [NotNullWhen(true)] out IReadOnlyList<WorkerId>? workerIds,
        out WorkDefinitionId? definitionId)
    {
        lock (this.sync)
        {
            if (!this.scheduledPurges.TryPeek(out var _, out var dueAt) ||
                dueAt > DateTimeOffset.UtcNow)
            {
                workerIds = null;
                definitionId = null;
                return false;
            }

            var dueWorkerIds = new List<WorkerId>(PurgeBatchSize);
            WorkDefinitionId? commonDefinitionId = null;
            var mixedDefinitions = false;
            while (dueWorkerIds.Count < PurgeBatchSize &&
                this.scheduledPurges.TryPeek(out var scheduledPurge, out dueAt) &&
                dueAt <= DateTimeOffset.UtcNow)
            {
                this.scheduledPurges.Dequeue();
                if (!this.finalWorkerEntriesById.TryGetValue(scheduledPurge.WorkerId, out var entry) ||
                    entry.ScheduleGeneration != scheduledPurge.ScheduleGeneration)
                {
                    continue;
                }

                this.RemoveFinalWorkerLocked(entry);
                dueWorkerIds.Add(entry.WorkerId);
                if (commonDefinitionId is null)
                {
                    commonDefinitionId = entry.DefinitionId;
                }
                else if (commonDefinitionId != entry.DefinitionId)
                {
                    mixedDefinitions = true;
                }
            }

            if (dueWorkerIds.Count == 0)
            {
                workerIds = null;
                definitionId = null;
                return false;
            }

            workerIds = dueWorkerIds;
            definitionId = mixedDefinitions ? null : commonDefinitionId;
            return true;
        }
    }

    private TimeSpan GetDelayUntilNextPurge()
    {
        lock (this.sync)
        {
            if (!this.scheduledPurges.TryPeek(out _, out var dueAt))
            {
                return Timeout.InfiniteTimeSpan;
            }

            var delay = dueAt - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }
    }

    private bool TryTakeCountRetentionWork(
        [NotNullWhen(true)] out IReadOnlyDictionary<WorkDefinitionId, int>? definitionTargets,
        out bool enforceSystemCap)
    {
        lock (this.sync)
        {
            enforceSystemCap = this.systemCountRetentionDirty;
            if (this.countRetentionTargetsByDefinition.Count == 0 && !enforceSystemCap)
            {
                definitionTargets = null;
                return false;
            }

            definitionTargets = new Dictionary<WorkDefinitionId, int>(this.countRetentionTargetsByDefinition);
            this.countRetentionTargetsByDefinition.Clear();
            this.systemCountRetentionDirty = false;
            return true;
        }
    }

    private async Task WaitForSignal(TimeSpan delay, CancellationToken cancellationToken)
    {
        var signaled = delay == Timeout.InfiniteTimeSpan
            ? await this.WaitForSignal(cancellationToken)
            : await this.signal.WaitAsync(delay, cancellationToken);

        if (!signaled)
        {
            return;
        }

        lock (this.sync)
        {
            this.signalPending = false;
        }
    }

    private async Task<bool> WaitForSignal(CancellationToken cancellationToken)
    {
        await this.signal.WaitAsync(cancellationToken);
        return true;
    }

    private void EnforceCountRetention(
        IReadOnlyDictionary<WorkDefinitionId, int> definitionTargets,
        bool enforceSystemCap,
        CancellationToken cancellationToken)
    {
        foreach (var definitionTarget in definitionTargets)
        {
            this.EnforceDefinitionCountRetention(definitionTarget.Key, definitionTarget.Value, cancellationToken);
        }

        if (enforceSystemCap)
        {
            this.EnforceSystemCountRetention(cancellationToken);
        }
    }

    private void EnforceDefinitionCountRetention(
        WorkDefinitionId definitionId,
        int targetFinalWorkers,
        CancellationToken cancellationToken)
    {
        var definitionIds = new HashSet<WorkDefinitionId> { definitionId };
        var finalWorkerCount = this.CountFinalWorkers(definitionIds);
        if (finalWorkerCount == 0)
        {
            return;
        }

        var excessCount = finalWorkerCount - targetFinalWorkers;
        if (excessCount <= 0)
        {
            return;
        }

        this.PurgeExcessFinalWorkers(definitionId, excessCount, cancellationToken);
    }

    private void EnforceSystemCountRetention(CancellationToken cancellationToken)
    {
        var excessCount = this.CountFinalWorkers(definitionIds: null) - systemRetention.MaximumFinalWorkers;
        if (excessCount <= 0)
        {
            return;
        }

        this.PurgeExcessFinalWorkers(definitionId: null, excessCount, cancellationToken);
    }

    private int CountFinalWorkers(IReadOnlySet<WorkDefinitionId>? definitionIds)
    {
        var counts = index.CountByState(definitionIds);
        return FinalStates.Sum(state => counts.GetValueOrDefault(state));
    }

    private void PurgeExcessFinalWorkers(
        WorkDefinitionId? definitionId,
        int excessCount,
        CancellationToken cancellationToken)
    {
        var remaining = excessCount;
        while (remaining > 0)
        {
            var workerIds = this.TakeOldestFinalWorkers(definitionId, Math.Min(PurgeBatchSize, remaining));
            if (workerIds.Count == 0)
            {
                return;
            }

            this.TryPurge(workerIds, definitionId, cancellationToken);
            remaining -= workerIds.Count;
        }
    }

    private void TryPurge(
        IReadOnlyList<WorkerId> workerIds,
        WorkDefinitionId? definitionId,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var purgedCount = purge(workerIds, definitionId);
            stopwatch.Stop();
            this.RecordRun(startedAt, stopwatch.Elapsed, purgedCount, exception: null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            this.RecordRun(startedAt, stopwatch.Elapsed, purgedCount: 0, exception);
        }
    }

    private void RecordRun(
        DateTimeOffset startedAt,
        TimeSpan duration,
        int purgedCount,
        Exception? exception)
    {
        lock (this.sync)
        {
            this.lastRunAt = startedAt;
            this.lastRunDuration = duration;
            this.lastPurgedCount = purgedCount;
            this.totalPurgedCount += purgedCount;
            this.schedulerFailureType = exception?.GetType().FullName;
            this.schedulerFailureMessage = exception?.Message;
        }
    }

    private void Signal()
    {
        var shouldRelease = false;
        lock (this.sync)
        {
            if (!this.signalPending)
            {
                this.signalPending = true;
                shouldRelease = true;
            }
        }

        if (shouldRelease)
        {
            this.signal.Release();
        }
    }

    private void TrimScheduledPurgeQueueIfNeeded()
    {
        lock (this.sync)
        {
            var count = this.scheduledPurges.Count;
            if (this.scheduledPurgeHighWaterMark < ScheduledPurgeTrimMinimumHighWaterMark ||
                count > this.scheduledPurgeHighWaterMark / 4)
            {
                return;
            }

            var now = DateTimeOffset.UtcNow;
            if (now - this.lastScheduledPurgeTrimAt < ScheduledPurgeTrimInterval)
            {
                return;
            }

            this.scheduledPurges.TrimExcess();
            this.scheduledPurgeHighWaterMark = count;
            this.lastScheduledPurgeTrimAt = now;
        }
    }

    private void TrackFinalWorkerLocked(
        WorkerRecord worker,
        bool includeInCountRetention,
        long scheduleGeneration)
    {
        var entry = new FinalWorkerRetentionEntry(
            worker.CreatedAt,
            worker.Id,
            worker.Work.Definition.Id,
            scheduleGeneration);

        this.RemoveFinalWorkerLocked(worker.Id);
        this.finalWorkerEntriesById[worker.Id] = entry;
        if (!includeInCountRetention)
        {
            return;
        }

        this.finalWorkers.Add(entry);

        if (!this.finalWorkersByDefinition.TryGetValue(entry.DefinitionId, out var definitionWorkers))
        {
            definitionWorkers = new SortedSet<FinalWorkerRetentionEntry>(FinalWorkerRetentionEntryComparer.Instance);
            this.finalWorkersByDefinition[entry.DefinitionId] = definitionWorkers;
        }

        definitionWorkers.Add(entry);
    }

    private IReadOnlyList<WorkerId> TakeOldestFinalWorkers(WorkDefinitionId? definitionId, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        lock (this.sync)
        {
            var workerIds = new List<WorkerId>(count);
            while (workerIds.Count < count)
            {
                var candidates = definitionId is { } id
                    ? this.finalWorkersByDefinition.GetValueOrDefault(id)
                    : this.finalWorkers;
                if (candidates?.Min is not { } entry)
                {
                    break;
                }

                this.RemoveFinalWorkerLocked(entry);
                workerIds.Add(entry.WorkerId);
            }

            return workerIds;
        }
    }

    private void RemoveFinalWorkerLocked(WorkerId workerId)
    {
        if (this.finalWorkerEntriesById.TryGetValue(workerId, out var entry))
        {
            this.RemoveFinalWorkerLocked(entry);
        }
    }

    private void RemoveFinalWorkerLocked(FinalWorkerRetentionEntry entry)
    {
        this.finalWorkerEntriesById.Remove(entry.WorkerId);
        this.finalWorkers.Remove(entry);
        if (this.finalWorkersByDefinition.TryGetValue(entry.DefinitionId, out var definitionWorkers))
        {
            definitionWorkers.Remove(entry);
            if (definitionWorkers.Count == 0)
            {
                this.finalWorkersByDefinition.Remove(entry.DefinitionId);
            }
        }
    }

    private sealed record ScheduledPurge(
        WorkerId WorkerId,
        long ScheduleGeneration);

    private sealed record FinalWorkerRetentionEntry(
        DateTimeOffset CreatedAt,
        WorkerId WorkerId,
        WorkDefinitionId DefinitionId,
        long ScheduleGeneration);

    private sealed class FinalWorkerRetentionEntryComparer : IComparer<FinalWorkerRetentionEntry>
    {
        public static FinalWorkerRetentionEntryComparer Instance { get; } = new();

        public int Compare(FinalWorkerRetentionEntry? x, FinalWorkerRetentionEntry? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            var createdAtComparison = x.CreatedAt.CompareTo(y.CreatedAt);
            return createdAtComparison != 0
                ? createdAtComparison
                : x.WorkerId.Value.CompareTo(y.WorkerId.Value);
        }
    }
}
