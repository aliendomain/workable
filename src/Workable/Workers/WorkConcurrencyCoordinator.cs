using System.Collections.Concurrent;

namespace Workable;
internal sealed class WorkConcurrencyCoordinator
{
    private readonly ConcurrentDictionary<WorkDefinitionId, WorkDefinitionConcurrencyManager> managers = [];

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
        var configuration = worker.Configuration.Concurrency;
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
        var configuration = worker.Configuration.Concurrency;
        var manager = this.GetManager(worker.Work.Definition.Id);
        return manager.QueueExistingWorkerForStart(worker, configuration);
    }

    public List<WorkerRecord> ReserveDeferredStarts(WorkDefinitionId definitionId)
    {
        return this.managers.TryGetValue(definitionId, out var manager)
            ? manager.ReserveDeferredStarts()
            : [];
    }

    public void Synchronize(WorkerRecord worker)
    {
        if (worker.Configuration.Concurrency.IsEnabled)
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
        => this.managers.Clear();

    private WorkDefinitionConcurrencyManager GetManager(WorkDefinitionId definitionId)
        => this.managers.GetOrAdd(definitionId, static id => new WorkDefinitionConcurrencyManager(id));

    private sealed class WorkDefinitionConcurrencyManager(WorkDefinitionId definitionId)
    {
        private readonly Lock sync = new();
        private readonly Dictionary<WorkerId, WorkerRecord> workers = [];
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
                            [WorkMessage.Info("workable.concurrency.capacity_reached", "Concurrency capacity has been reached for this work group.", "configuration.concurrency.maximumCapacity")]);
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

                    var configuration = worker.Configuration.Concurrency;
                    var groupKey = WorkConcurrencyGroupKey.From(configuration.Scope, worker.Input);
                    if (!this.HasCapacity(configuration, groupKey))
                    {
                        retained.Enqueue(workerId);
                        continue;
                    }

                    worker.ReserveDeferredConcurrencyStart();
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
                this.RemoveDeferred(worker.Id);
            }
        }

        private bool HasCapacity(
            WorkConcurrencyConfiguration configuration,
            WorkConcurrencyGroupKey groupKey,
            WorkerRecord? candidate = null)
        {
            var count = 0;
            var candidateAlreadyCounts = false;

            foreach (var worker in this.workers.Values)
            {
                if (!worker.CountsAgainstConcurrencyCapacity(configuration.BlockingMode))
                {
                    continue;
                }

                if (WorkConcurrencyGroupKey.From(configuration.Scope, worker.Input) != groupKey)
                {
                    continue;
                }

                count++;
                if (ReferenceEquals(worker, candidate))
                {
                    candidateAlreadyCounts = true;
                }
            }

            return count < configuration.MaximumCapacity ||
                candidateAlreadyCounts && count == configuration.MaximumCapacity;
        }

        private void TrackLocked(WorkerRecord worker)
        {
            this.workers[worker.Id] = worker;
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
    }

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
