using System.Collections.Concurrent;

namespace Workable;
internal sealed class WorkConcurrencyCoordinator
{
    private readonly ConcurrentDictionary<WorkDefinitionId, WorkDefinitionConcurrencyManager> managers = [];
    private readonly WorkSystemConcurrencyDiagnosticsTracker diagnostics = new();

    public WorkSystemConcurrencyDiagnostics Diagnostics
        => this.diagnostics.Snapshot(
            [.. this.managers.Values.Select(manager => manager.GetDiagnosticsSnapshot())]);

    public WorkConcurrencyReservation QueueWorker(
        WorkDefinitionId definitionId,
        WorkInput? input,
        WorkConcurrencyConfiguration configuration,
        Func<WorkConcurrencyReservationStatus, WorkerRecord> createWorker)
    {
        var manager = this.GetManager(definitionId);
        return manager.QueueWorker(input, configuration, createWorker);
    }

    public WorkActionOutcome TryStart(
        WorkerRecord worker,
        long expectedRevision,
        bool advancesRevision,
        bool bypassConcurrencyWhenFlexible,
        out CancellationToken executionToken,
        CancellationToken cancellationToken)
    {
        var configuration = worker.Configuration.Coordination.Concurrency;
        var manager = this.GetManager(worker.Work.Definition.Id);
        return manager.TryStart(
            worker,
            expectedRevision,
            advancesRevision,
            bypassConcurrencyWhenFlexible,
            configuration,
            out executionToken,
            cancellationToken);
    }

    public WorkConcurrencyReservationStatus QueueExistingWorkerForStart(WorkerRecord worker)
    {
        var configuration = worker.Configuration.Coordination.Concurrency;
        var manager = this.GetManager(worker.Work.Definition.Id);
        return manager.QueueExistingWorkerForStart(worker, configuration);
    }

    public List<WorkerRecord> ReserveDeferredStarts(WorkDefinitionId definitionId)
    {
        var scheduled = this.managers.TryGetValue(definitionId, out var manager)
            ? manager.ReserveDeferredStarts()
            : [];
        this.diagnostics.RecordDrain(scheduled.Count);
        return scheduled;
    }

    public void Synchronize(WorkerRecord worker)
    {
        if (worker.Configuration.Coordination.IsConcurrencyEnabled)
        {
            this.GetManager(worker.Work.Definition.Id).Track(worker);
            return;
        }

        this.Forget(worker);
    }

    public void Forget(WorkerRecord worker)
    {
        if (this.managers.TryGetValue(worker.Work.Definition.Id, out var manager))
        {
            manager.Forget(worker);
        }
    }

    public void Clear()
    {
        this.managers.Clear();
        this.diagnostics.Clear();
    }

    private WorkDefinitionConcurrencyManager GetManager(WorkDefinitionId definitionId)
        => this.managers.GetOrAdd(definitionId, static id => new WorkDefinitionConcurrencyManager(id));

    private sealed class WorkDefinitionConcurrencyManager(WorkDefinitionId definitionId)
    {
        private readonly Lock sync = new();
        private readonly Dictionary<WorkerId, WorkerRecord> workers = [];
        private readonly Dictionary<WorkerId, WorkerConcurrencyCapacityEntry> capacityEntriesByWorker = [];
        private readonly Dictionary<WorkConcurrencyGroupKey, WorkConcurrencyGroupCounts> capacityCountsByGroup = [];
        private readonly Queue<WorkerId> deferredStarts = [];

        public WorkConcurrencyReservation QueueWorker(
            WorkInput? input,
            WorkConcurrencyConfiguration configuration,
            Func<WorkConcurrencyReservationStatus, WorkerRecord> createWorker)
        {
            lock (this.sync)
            {
                var groupKey = WorkConcurrencyGroupKey.From(configuration.Scope, input);
                var status = this.HasCapacity(configuration, groupKey)
                    ? WorkConcurrencyReservationStatus.Reserved
                    : configuration.LimitReachedBehavior == WorkConcurrencyLimitReachedBehavior.DeferStart
                        ? WorkConcurrencyReservationStatus.Deferred
                        : WorkConcurrencyReservationStatus.Rejected;

                if (status == WorkConcurrencyReservationStatus.Rejected)
                {
                    return new WorkConcurrencyReservation(status, Worker: null);
                }

                var worker = createWorker(status);
                this.TrackLocked(worker);
                if (status == WorkConcurrencyReservationStatus.Deferred)
                {
                    this.deferredStarts.Enqueue(worker.Id);
                }

                return new WorkConcurrencyReservation(status, worker);
            }
        }

        public WorkConcurrencyReservationStatus QueueExistingWorkerForStart(
            WorkerRecord worker,
            WorkConcurrencyConfiguration configuration)
        {
            lock (this.sync)
            {
                this.TrackLocked(worker);

                var groupKey = WorkConcurrencyGroupKey.From(configuration.Scope, worker.Input);
                var status = this.HasCapacity(configuration, groupKey, worker)
                    ? WorkConcurrencyReservationStatus.Reserved
                    : configuration.LimitReachedBehavior == WorkConcurrencyLimitReachedBehavior.DeferStart
                        ? WorkConcurrencyReservationStatus.Deferred
                        : WorkConcurrencyReservationStatus.Rejected;

                if (status == WorkConcurrencyReservationStatus.Deferred)
                {
                    worker.DeferConcurrencyStart();
                    this.TrackLocked(worker);
                    if (!this.deferredStarts.Contains(worker.Id))
                    {
                        this.deferredStarts.Enqueue(worker.Id);
                    }
                }

                return status;
            }
        }

        public WorkActionOutcome TryStart(
            WorkerRecord worker,
            long expectedRevision,
            bool advancesRevision,
            bool bypassConcurrencyWhenFlexible,
            WorkConcurrencyConfiguration configuration,
            out CancellationToken executionToken,
            CancellationToken cancellationToken)
        {
            lock (this.sync)
            {
                if (!bypassConcurrencyWhenFlexible ||
                    configuration.OverrideBehavior == WorkConcurrencyOverrideBehavior.Strict)
                {
                    var groupKey = WorkConcurrencyGroupKey.From(configuration.Scope, worker.Input);
                    if (!this.HasCapacity(configuration, groupKey, worker))
                    {
                        executionToken = default;
                        return WorkActionOutcome.Invalid(
                            WorkAction.Start,
                            worker.ToSnapshot(),
                            [WorkMessage.Info("workable.concurrency.capacity_reached", "Concurrency capacity has been reached for this work group.", "configuration.coordination.concurrency.maximumCapacity")]);
                    }
                }

                var outcome = worker.Start(expectedRevision, advancesRevision, out executionToken, cancellationToken);
                if (outcome.IsAccepted)
                {
                    this.TrackLocked(worker);
                    this.RemoveDeferred(worker.Id);
                }

                return outcome;
            }
        }

        public List<WorkerRecord> ReserveDeferredStarts()
        {
            lock (this.sync)
            {
                var scheduled = new List<WorkerRecord>();
                var retained = new Queue<WorkerId>(this.deferredStarts.Count);
                while (this.deferredStarts.Count > 0)
                {
                    var workerId = this.deferredStarts.Dequeue();
                    if (!this.workers.TryGetValue(workerId, out var worker) ||
                        !worker.IsDeferredConcurrencyStartFor(definitionId))
                    {
                        continue;
                    }

                    var configuration = worker.Configuration.Coordination.Concurrency;
                    var groupKey = WorkConcurrencyGroupKey.From(configuration.Scope, worker.Input);
                    if (!this.HasCapacity(configuration, groupKey))
                    {
                        retained.Enqueue(workerId);
                        continue;
                    }

                    worker.ReserveDeferredConcurrencyStart();
                    this.TrackLocked(worker);
                    scheduled.Add(worker);
                }

                while (retained.TryDequeue(out var workerId))
                {
                    this.deferredStarts.Enqueue(workerId);
                }

                return scheduled;
            }
        }

        public void Track(WorkerRecord worker)
        {
            lock (this.sync)
            {
                this.TrackLocked(worker);
            }
        }

        public void Forget(WorkerRecord worker)
        {
            lock (this.sync)
            {
                this.workers.Remove(worker.Id);
                this.RemoveCapacityEntryLocked(worker.Id);
                this.RemoveDeferred(worker.Id);
            }
        }

        private bool HasCapacity(
            WorkConcurrencyConfiguration configuration,
            WorkConcurrencyGroupKey groupKey,
            WorkerRecord? candidate = null)
        {
            var count = this.capacityCountsByGroup.TryGetValue(groupKey, out var counts)
                ? counts.CountFor(configuration.BlockingMode)
                : 0;
            var candidateAlreadyCounts = candidate is not null &&
                this.capacityEntriesByWorker.TryGetValue(candidate.Id, out var candidateEntry) &&
                candidateEntry.GroupKey == groupKey &&
                candidateEntry.Bucket.CountsFor(configuration.BlockingMode);

            return count < configuration.MaximumCapacity ||
                candidateAlreadyCounts && count == configuration.MaximumCapacity;
        }

        private void TrackLocked(WorkerRecord worker)
        {
            this.workers[worker.Id] = worker;
            this.RemoveCapacityEntryLocked(worker.Id);
            if (!worker.TryGetConcurrencyCapacityContribution(out var scope, out var bucket) ||
                bucket is null)
            {
                return;
            }

            var entry = new WorkerConcurrencyCapacityEntry(
                WorkConcurrencyGroupKey.From(scope, worker.Input),
                bucket.Value);
            this.capacityEntriesByWorker[worker.Id] = entry;
            this.GetOrAddCounts(entry.GroupKey).Add(entry.Bucket);
        }

        private void RemoveCapacityEntryLocked(WorkerId workerId)
        {
            if (!this.capacityEntriesByWorker.Remove(workerId, out var entry))
            {
                return;
            }

            if (!this.capacityCountsByGroup.TryGetValue(entry.GroupKey, out var counts))
            {
                return;
            }

            counts.Remove(entry.Bucket);
            if (counts.IsEmpty)
            {
                this.capacityCountsByGroup.Remove(entry.GroupKey);
            }
        }

        private WorkConcurrencyGroupCounts GetOrAddCounts(WorkConcurrencyGroupKey groupKey)
        {
            if (!this.capacityCountsByGroup.TryGetValue(groupKey, out var counts))
            {
                counts = new WorkConcurrencyGroupCounts();
                this.capacityCountsByGroup[groupKey] = counts;
            }

            return counts;
        }

        private void RemoveDeferred(WorkerId workerId)
        {
            if (!this.deferredStarts.Contains(workerId))
            {
                return;
            }

            var retained = new Queue<WorkerId>(this.deferredStarts.Count);
            while (this.deferredStarts.TryDequeue(out var current))
            {
                if (current != workerId)
                {
                    retained.Enqueue(current);
                }
            }

            while (retained.TryDequeue(out var current))
            {
                this.deferredStarts.Enqueue(current);
            }
        }

        public WorkDefinitionConcurrencyDiagnosticsSnapshot GetDiagnosticsSnapshot()
        {
            lock (this.sync)
            {
                var oldestDeferredStartAt = this.deferredStarts
                    .Select(workerId => this.workers.TryGetValue(workerId, out var worker)
                        ? worker.CreatedAt
                        : (DateTimeOffset?)null)
                    .Where(createdAt => createdAt is not null)
                    .Min();
                return new WorkDefinitionConcurrencyDiagnosticsSnapshot(
                    this.deferredStarts.Count,
                    oldestDeferredStartAt);
            }
        }
    }

    private sealed class WorkConcurrencyGroupCounts
    {
        private int executing;
        private int paused;
        private int failed;

        public bool IsEmpty => this.executing == 0 && this.paused == 0 && this.failed == 0;

        public void Add(WorkConcurrencyCapacityBucket bucket)
        {
            switch (bucket)
            {
                case WorkConcurrencyCapacityBucket.Executing:
                    this.executing++;
                    break;
                case WorkConcurrencyCapacityBucket.Paused:
                    this.paused++;
                    break;
                case WorkConcurrencyCapacityBucket.Failed:
                    this.failed++;
                    break;
            }
        }

        public void Remove(WorkConcurrencyCapacityBucket bucket)
        {
            switch (bucket)
            {
                case WorkConcurrencyCapacityBucket.Executing:
                    this.executing = Math.Max(0, this.executing - 1);
                    break;
                case WorkConcurrencyCapacityBucket.Paused:
                    this.paused = Math.Max(0, this.paused - 1);
                    break;
                case WorkConcurrencyCapacityBucket.Failed:
                    this.failed = Math.Max(0, this.failed - 1);
                    break;
            }
        }

        public int CountFor(WorkConcurrencyBlockingMode blockingMode)
        {
            var count = this.executing;
            if (blockingMode is WorkConcurrencyBlockingMode.WhileExecutingOrPaused or WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed)
            {
                count += this.paused;
            }

            if (blockingMode is WorkConcurrencyBlockingMode.WhileExecutingOrFailed or WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed)
            {
                count += this.failed;
            }

            return count;
        }
    }

    private readonly record struct WorkerConcurrencyCapacityEntry(
        WorkConcurrencyGroupKey GroupKey,
        WorkConcurrencyCapacityBucket Bucket);

    private readonly record struct WorkConcurrencyGroupKey(
        WorkConcurrencyScope Scope,
        WorkSubjectId? SubjectId,
        WorkConcurrencyKey? ConcurrencyKey)
    {
        public static WorkConcurrencyGroupKey From(WorkConcurrencyScope scope, WorkInput? input)
            => scope switch
            {
                WorkConcurrencyScope.PerSubject => new(scope, input?.SubjectId, null),
                WorkConcurrencyScope.PerConcurrencyKey => new(scope, null, input?.ConcurrencyKey),
                _ => new(WorkConcurrencyScope.PerDefinition, null, null),
            };
    }
}

internal readonly record struct WorkConcurrencyReservation(
    WorkConcurrencyReservationStatus Status,
    WorkerRecord? Worker);

internal enum WorkConcurrencyReservationStatus
{
    Reserved,
    Deferred,
    Rejected,
}

internal enum WorkConcurrencyCapacityBucket
{
    Executing,
    Paused,
    Failed,
}

internal static class WorkConcurrencyCapacityBucketExtensions
{
    public static bool CountsFor(this WorkConcurrencyCapacityBucket bucket, WorkConcurrencyBlockingMode blockingMode)
        => bucket switch
        {
            WorkConcurrencyCapacityBucket.Executing => true,
            WorkConcurrencyCapacityBucket.Paused => blockingMode is WorkConcurrencyBlockingMode.WhileExecutingOrPaused or WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed,
            WorkConcurrencyCapacityBucket.Failed => blockingMode is WorkConcurrencyBlockingMode.WhileExecutingOrFailed or WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed,
            _ => false,
        };
}
