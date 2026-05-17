using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace Workable;
internal sealed class WorkerRetentionScheduler(
    ConcurrentDictionary<WorkerId, WorkerRecord> workers,
    WorkerIndex index,
    WorkSystemRetentionConfiguration systemRetention,
    Func<WorkerRecord, long, WorkActionOutcome> purge,
    Action<WorkerRecord> publishPurgeEvent) : IDisposable
{
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
    {
        if (!worker.IsFinal)
        {
            return;
        }

        var dueAt = DateTimeOffset.UtcNow + worker.Configuration.Retention.PurgeInterval;
        lock (this.sync)
        {
            this.TrackFinalWorkerLocked(worker);
            this.scheduledPurges.Enqueue(new ScheduledPurge(worker.Id), dueAt);
            this.scheduledPurgeHighWaterMark = Math.Max(
                this.scheduledPurgeHighWaterMark,
                this.scheduledPurges.Count);
            this.countRetentionTargetsByDefinition[worker.Work.Definition.Id] = worker.Configuration.Retention.MaximumFinalWorkers;
            this.systemCountRetentionDirty = true;
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

                if (this.TryTakeDuePurge(out var scheduledPurge))
                {
                    this.TryPurge(scheduledPurge);
                    continue;
                }

                if (this.TryTakeCountRetentionWork(out var definitionIds, out var enforceSystemCap))
                {
                    this.EnforceCountRetention(definitionIds, enforceSystemCap);
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

    private bool TryTakeDuePurge([NotNullWhen(true)] out ScheduledPurge? scheduledPurge)
    {
        lock (this.sync)
        {
            if (!this.scheduledPurges.TryPeek(out var _, out var dueAt) ||
                dueAt > DateTimeOffset.UtcNow)
            {
                scheduledPurge = null;
                return false;
            }

            scheduledPurge = this.scheduledPurges.Dequeue();
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
        bool enforceSystemCap)
    {
        foreach (var definitionTarget in definitionTargets)
        {
            this.EnforceDefinitionCountRetention(definitionTarget.Key, definitionTarget.Value);
        }

        if (enforceSystemCap)
        {
            this.EnforceSystemCountRetention();
        }
    }

    private void EnforceDefinitionCountRetention(WorkDefinitionId definitionId, int targetFinalWorkers)
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

        this.PurgeExcessFinalWorkers(definitionId, excessCount);
    }

    private void EnforceSystemCountRetention()
    {
        var excessCount = this.CountFinalWorkers(definitionIds: null) - systemRetention.MaximumFinalWorkers;
        if (excessCount <= 0)
        {
            return;
        }

        this.PurgeExcessFinalWorkers(definitionId: null, excessCount);
    }

    private int CountFinalWorkers(IReadOnlySet<WorkDefinitionId>? definitionIds)
    {
        var counts = index.CountByState(definitionIds);
        return FinalStates.Sum(state => counts.GetValueOrDefault(state));
    }

    private void PurgeExcessFinalWorkers(WorkDefinitionId? definitionId, int excessCount)
    {
        var purgedCount = 0;
        while (purgedCount < excessCount && this.TryTakeOldestFinalWorker(definitionId, out var workerId))
        {
            if (!workers.TryGetValue(workerId, out var worker) ||
                !worker.IsFinal ||
                (definitionId is not null && worker.Work.Definition.Id != definitionId))
            {
                continue;
            }

            if (this.TryPurge(worker))
            {
                purgedCount++;
                continue;
            }

            if (workers.ContainsKey(workerId) && worker.IsFinal)
            {
                this.TrackFinalWorker(worker);
            }

            return;
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

    private void TryPurge(ScheduledPurge scheduledPurge)
    {
        if (!workers.TryGetValue(scheduledPurge.WorkerId, out var worker) || !worker.IsFinal)
        {
            this.Forget(scheduledPurge.WorkerId);
            return;
        }

        this.TryPurge(worker);
    }

    private bool TryPurge(WorkerRecord worker)
    {
        var outcome = purge(worker, worker.Revision);
        if (outcome.IsAccepted)
        {
            publishPurgeEvent(worker);
            return true;
        }

        return false;
    }

    private void TrackFinalWorker(WorkerRecord worker)
    {
        lock (this.sync)
        {
            this.TrackFinalWorkerLocked(worker);
        }
    }

    private void TrackFinalWorkerLocked(WorkerRecord worker)
    {
        var entry = new FinalWorkerRetentionEntry(
            worker.CreatedAt,
            worker.Id,
            worker.Work.Definition.Id);

        this.RemoveFinalWorkerLocked(worker.Id);
        this.finalWorkerEntriesById[worker.Id] = entry;
        this.finalWorkers.Add(entry);

        if (!this.finalWorkersByDefinition.TryGetValue(entry.DefinitionId, out var definitionWorkers))
        {
            definitionWorkers = new SortedSet<FinalWorkerRetentionEntry>(FinalWorkerRetentionEntryComparer.Instance);
            this.finalWorkersByDefinition[entry.DefinitionId] = definitionWorkers;
        }

        definitionWorkers.Add(entry);
    }

    private bool TryTakeOldestFinalWorker(WorkDefinitionId? definitionId, out WorkerId workerId)
    {
        lock (this.sync)
        {
            var candidates = definitionId is { } id
                ? this.finalWorkersByDefinition.GetValueOrDefault(id)
                : this.finalWorkers;
            if (candidates?.Min is not { } entry)
            {
                workerId = default;
                return false;
            }

            this.RemoveFinalWorkerLocked(entry);
            workerId = entry.WorkerId;
            return true;
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

    private sealed record ScheduledPurge(WorkerId WorkerId);

    private sealed record FinalWorkerRetentionEntry(
        DateTimeOffset CreatedAt,
        WorkerId WorkerId,
        WorkDefinitionId DefinitionId);

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
