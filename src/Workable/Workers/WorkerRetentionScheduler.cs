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
    private static readonly WorkerState[] FinalStates = [WorkerState.Canceled, WorkerState.Completed];
    private readonly PriorityQueue<ScheduledPurge, DateTimeOffset> scheduledPurges = new();
    private readonly Dictionary<WorkDefinitionId, int> countRetentionTargetsByDefinition = [];
    private readonly SemaphoreSlim signal = new(0);
    private readonly Lock sync = new();
    private CancellationTokenSource? cancellation;
    private Task? schedulerTask;
    private bool systemCountRetentionDirty;
    private bool signalPending;

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
            this.countRetentionTargetsByDefinition.Clear();
            this.systemCountRetentionDirty = false;
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
            this.scheduledPurges.Enqueue(new ScheduledPurge(worker.Id), dueAt);
            this.countRetentionTargetsByDefinition[worker.Work.Definition.Id] = worker.Configuration.Retention.MaximumFinalWorkers;
            this.systemCountRetentionDirty = true;
        }

        this.Signal();
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
        var definitionIds = definitionId is { } id ? new HashSet<WorkDefinitionId> { id } : null;
        var purgedCount = 0;
        foreach (var state in FinalStates)
        {
            foreach (var workerId in index.ByState(state, definitionIds))
            {
                if (workers.TryGetValue(workerId, out var worker) &&
                    worker.IsFinal &&
                    (definitionId is null || worker.Work.Definition.Id == definitionId))
                {
                    this.TryPurge(worker);
                    purgedCount++;
                    if (purgedCount >= excessCount)
                    {
                        return;
                    }
                }
            }
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

    private void TryPurge(ScheduledPurge scheduledPurge)
    {
        if (!workers.TryGetValue(scheduledPurge.WorkerId, out var worker) || !worker.IsFinal)
        {
            return;
        }

        this.TryPurge(worker);
    }

    private void TryPurge(WorkerRecord worker)
    {
        var outcome = purge(worker, worker.Revision);
        if (outcome.IsAccepted)
        {
            publishPurgeEvent(worker);
        }
    }

    private sealed record ScheduledPurge(WorkerId WorkerId);
}
