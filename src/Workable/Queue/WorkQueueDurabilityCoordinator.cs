using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Channels;

namespace Workable;

internal sealed class WorkQueueDurabilityCoordinator(
    IWorkPersistenceStore? store,
    WorkSystemId workSystemId,
    string? workSystemName,
    Func<bool> isAcceptingWork,
    Func<CancellationToken> getSystemExecutionToken,
    Func<WorkQueueDurabilityEntry, CancellationToken, Task> acceptPersistedEntry,
    Action<WorkerId> leaseLost,
    TimeSpan? readerPollInterval = null,
    TimeSpan? leaseRenewalInterval = null,
    TimeSpan? retryDelay = null,
    TimeSpan? readerSignalDebounce = null,
    TimeSpan? leaseDuration = null,
    int batchSize = 100)
{
    private readonly ConcurrentDictionary<WorkerId, WorkQueueDurabilityLease> leases = [];
    private readonly ConcurrentDictionary<WorkerId, byte> idempotencyReservations = [];
    private readonly ConcurrentDictionary<WorkerId, byte> retainedFailures = [];
    private readonly ConcurrentDictionary<WorkerId, TaskCompletionSource<WorkerRecord>> acceptedWorkerWaiters = [];
    private readonly Channel<WorkQueueDurabilityCleanupItem> cleanup = Channel.CreateUnbounded<WorkQueueDurabilityCleanupItem>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly SemaphoreSlim readerSignal = new(0, 1);
    private readonly TimeSpan defaultReaderPollInterval = readerPollInterval ?? WorkQueueDurabilityConfiguration.DefaultFallbackPollingInterval;
    private readonly TimeSpan leaseRenewalInterval = leaseRenewalInterval ?? TimeSpan.FromSeconds(10);
    private readonly TimeSpan retryDelay = retryDelay ?? TimeSpan.FromSeconds(1);
    private readonly TimeSpan readerSignalDebounce = readerSignalDebounce ?? TimeSpan.FromMilliseconds(50);
    private readonly TimeSpan cleanupDebounce = TimeSpan.FromMilliseconds(50);
    private readonly TimeSpan leaseDuration = leaseDuration ?? TimeSpan.FromMinutes(1);
    private readonly string ownerId = $"{Environment.MachineName}:{Guid.NewGuid():N}";
    private readonly int claimBatchSize = batchSize;
    private long readerPollIntervalTicks = (readerPollInterval ?? WorkQueueDurabilityConfiguration.DefaultFallbackPollingInterval).Ticks;
    private int readerSignalPending;
    private Task? readerTask;
    private Task? leaseRenewalTask;
    private Task? cleanupTask;

    public async Task InitializeAndDrain(
        IReadOnlyList<WorkDefinition> definitions,
        CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return;
        }

        await store.Initialize(
            new WorkQueueDurabilityInitializationContext(
                workSystemId,
                workSystemName,
                definitions),
            cancellationToken);
        this.ConfigureFallbackPolling(definitions);
        await this.DrainUntilEmpty(cancellationToken);
    }

    public async Task<WorkQueueOutcome> Enqueue(
        WorkQueueDurabilityEnqueueRequest request,
        CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return WorkQueueOutcome.Invalid(
                request.Definition.Id,
                [WorkMessage.Error(
                    "workable.queue_durability.store_required",
                    "Durable queueing is enabled for this work, but no durable queue store is registered.",
                    "configuration.queueDurability")]);
        }

        try
        {
            await store.Enqueue(request, cancellationToken);
        }
        catch (WorkQueueDurabilityDuplicateException exception)
        {
            return WorkQueueOutcome.Invalid(
                request.Definition.Id,
                [WorkMessage.Error(
                    "workable.queue_durability.duplicate",
                    exception.Message,
                    "input.subjectId")]);
        }

        return WorkQueueOutcome.Accepted(
            request.Definition.Id,
            request.WorkerId,
            [WorkMessage.Info(
                "workable.queue_durability.persisted",
                "Worker enqueue was persisted to the durable queue store and will start after the durable row is visible to the queue reader.",
                "configuration.queueDurability")]);
    }

    public async Task<WorkQueueOutcome> ReserveIdempotency(
        WorkIdempotencyPersistenceRequest request,
        CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return WorkQueueOutcome.Invalid(
                request.Definition.Id,
                [WorkMessage.Error(
                    "workable.idempotency.persistence_store_required",
                    "Persistence-backed idempotency is enabled for this work, but no work persistence store is registered.",
                    "configuration.idempotency.storage")]);
        }

        try
        {
            await store.ReserveIdempotency(request, cancellationToken);
            this.idempotencyReservations[request.WorkerId] = 0;
        }
        catch (WorkQueueDurabilityDuplicateException exception)
        {
            return WorkQueueOutcome.Invalid(
                request.Definition.Id,
                [WorkMessage.Error(
                    "workable.idempotency.duplicate_subject",
                    exception.Message,
                    "input.subjectId")]);
        }

        return WorkQueueOutcome.Accepted(request.Definition.Id, request.WorkerId);
    }

    public WorkQueueDurabilityEnqueueRequest CreateRequest(
        WorkerId workerId,
        RegisteredWork registeredWork,
        WorkInput? input,
        WorkerOptions options,
        WorkConfiguration configuration,
        WorkOrigin origin,
        DateTimeOffset createdAt,
        WorkQueueDurabilityIdempotency? idempotency)
        => new(
            workSystemId,
            workSystemName,
            workerId,
            registeredWork.Definition,
            input,
            options,
            configuration,
            origin,
            createdAt,
            idempotency,
            options.QueueDurabilityTransaction);

    public WorkIdempotencyPersistenceRequest CreateIdempotencyRequest(
        WorkerId workerId,
        RegisteredWork registeredWork,
        WorkSubjectId subjectId,
        WorkerOptions options,
        WorkOrigin origin,
        DateTimeOffset createdAt)
        => new(
            workSystemId,
            workSystemName,
            workerId,
            registeredWork.Definition,
            subjectId,
            origin,
            createdAt,
            options.QueueDurabilityTransaction);

    public void StartBackgroundTasks()
    {
        if (store is null)
        {
            return;
        }

        var executionToken = getSystemExecutionToken();
        this.readerTask = Task.Run(() => this.RunReader(executionToken), CancellationToken.None);
        this.leaseRenewalTask = Task.Run(() => this.RunLeaseRenewal(executionToken), CancellationToken.None);
        this.cleanupTask = Task.Run(() => this.RunCleanup(CancellationToken.None), CancellationToken.None);
    }

    public async Task StopBackgroundTasks(CancellationToken cancellationToken)
    {
        if (store is null || this.cleanupTask is null)
        {
            return;
        }

        var flushed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await this.cleanup.Writer.WriteAsync(
            new WorkQueueDurabilityCleanupItem(default, WorkQueueDurabilityCleanupKind.Flush, flushed),
            cancellationToken);
        await flushed.Task.WaitAsync(cancellationToken);
    }

    public void SignalReader()
    {
        if (store is null ||
            Interlocked.Exchange(ref this.readerSignalPending, 1) == 1)
        {
            return;
        }

        this.readerSignal.Release();
    }

    public void SignalAccepted(WorkerRecord worker)
    {
        if (this.acceptedWorkerWaiters.TryRemove(worker.Id, out var accepted))
        {
            accepted.TrySetResult(worker);
        }
    }

    public IWorkerHandle CreateHandle(
        WorkQueueOutcome outcome,
        Func<WorkerId, WorkerRecord?> getExisting)
        => WorkerHandle.AcceptedWhenAvailable(
            outcome,
            (workerId, cancellationToken) => this.WaitForAcceptedWorker(workerId, getExisting, cancellationToken));

    private async Task<WorkerRecord?> WaitForAcceptedWorker(
        WorkerId workerId,
        Func<WorkerId, WorkerRecord?> getExisting,
        CancellationToken cancellationToken)
    {
        if (getExisting(workerId) is { } existing)
        {
            return existing;
        }

        var accepted = this.acceptedWorkerWaiters.GetOrAdd(
            workerId,
            _ => new TaskCompletionSource<WorkerRecord>(TaskCreationOptions.RunContinuationsAsynchronously));

        if (getExisting(workerId) is { } materialized)
        {
            if (this.TryRemoveAcceptedWaiter(workerId, accepted))
            {
                accepted.TrySetResult(materialized);
            }

            return materialized;
        }

        try
        {
            return await accepted.Task.WaitAsync(cancellationToken);
        }
        catch
        {
            if (accepted.Task.IsCompleted)
            {
                return await accepted.Task;
            }

            this.TryRemoveAcceptedWaiter(workerId, accepted);
            throw;
        }
    }

    private bool TryRemoveAcceptedWaiter(
        WorkerId workerId,
        TaskCompletionSource<WorkerRecord> waiter)
        => ((ICollection<KeyValuePair<WorkerId, TaskCompletionSource<WorkerRecord>>>)this.acceptedWorkerWaiters)
            .Remove(new KeyValuePair<WorkerId, TaskCompletionSource<WorkerRecord>>(workerId, waiter));

    public void TrackLease(WorkerId workerId, WorkQueueDurabilityLease lease)
        => this.leases[workerId] = lease;

    public async Task CompleteDurably(
        WorkerId workerId,
        IWorkQueueDurabilityTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (store is null)
        {
            throw new InvalidOperationException("Durable completion requires a registered work persistence store.");
        }

        if (!this.leases.ContainsKey(workerId) &&
            !this.idempotencyReservations.ContainsKey(workerId) &&
            !this.retainedFailures.ContainsKey(workerId))
        {
            throw new InvalidOperationException(
                "Durable completion requires a persisted durable queue row or persistence-backed idempotency reservation for the worker.");
        }

        this.leases.TryGetValue(workerId, out var lease);
        var request = new WorkQueueDurabilityCleanupRequest(workerId, lease);
        try
        {
            await store.DeleteFinal([request], transaction, cancellationToken);
            this.RemoveLeaseIfCurrent(lease);
            this.idempotencyReservations.TryRemove(workerId, out _);
            this.retainedFailures.TryRemove(workerId, out _);
        }
        catch (WorkQueueDurabilityLeaseLostException exception)
        {
            this.HandleLostLeases(exception.Leases);
            throw;
        }
    }

    public void RetainFailed(WorkerId workerId)
    {
        if (store is null ||
            !this.leases.ContainsKey(workerId) &&
            !this.idempotencyReservations.ContainsKey(workerId) &&
            !this.retainedFailures.ContainsKey(workerId))
        {
            return;
        }

        this.QueueCleanup(workerId, WorkQueueDurabilityCleanupKind.RetainFailed);
    }

    public void DeleteFinal(WorkerId workerId)
    {
        if (store is null ||
            !this.leases.ContainsKey(workerId) &&
            !this.idempotencyReservations.ContainsKey(workerId) &&
            !this.retainedFailures.ContainsKey(workerId))
        {
            return;
        }

        this.QueueCleanup(workerId, WorkQueueDurabilityCleanupKind.DeleteFinal);
    }

    private async Task RunReader(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && isAcceptingWork())
        {
            try
            {
                var signaled = await this.WaitForReaderSignalOrFallback(cancellationToken);
                if (signaled)
                {
                    await Task.Delay(this.readerSignalDebounce, cancellationToken);
                    Interlocked.Exchange(ref this.readerSignalPending, 0);
                }

                await this.DrainUntilEmpty(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                if (!await this.DelayUnlessStopping(this.retryDelay, cancellationToken))
                {
                    return;
                }
            }
        }
    }

    private Task<bool> WaitForReaderSignalOrFallback(CancellationToken cancellationToken)
        => this.readerSignal.WaitAsync(this.ReaderPollInterval, cancellationToken);

    private void QueueCleanup(WorkerId workerId, WorkQueueDurabilityCleanupKind kind)
    {
        var item = new WorkQueueDurabilityCleanupItem(workerId, kind);
        if (!this.cleanup.Writer.TryWrite(item))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await this.cleanup.Writer.WriteAsync(item, CancellationToken.None);
                }
                catch (ChannelClosedException)
                {
                }
            }, CancellationToken.None);
        }
    }

    private async Task RunCleanup(CancellationToken cancellationToken)
    {
        var pending = new List<WorkQueueDurabilityCleanupItem>();
        var flushes = new List<TaskCompletionSource>();
        while (await this.WaitForCleanupWork(pending, cancellationToken))
        {
            try
            {
                for (var index = pending.Count - 1; index >= 0; index--)
                {
                    if (pending[index].Kind == WorkQueueDurabilityCleanupKind.Flush)
                    {
                        if (pending[index].Flushed is { } flushed)
                        {
                            flushes.Add(flushed);
                        }

                        pending.RemoveAt(index);
                    }
                }

                await this.ProcessCleanupBatch(pending, cancellationToken);
                pending.Clear();
                foreach (var flushed in flushes)
                {
                    flushed.TrySetResult();
                }

                flushes.Clear();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                if (!await this.DelayUnlessStopping(TimeSpan.FromSeconds(10), cancellationToken))
                {
                    return;
                }
            }
        }
    }

    private async Task<bool> WaitForCleanupWork(
        List<WorkQueueDurabilityCleanupItem> pending,
        CancellationToken cancellationToken)
    {
        if (pending.Count > 0)
        {
            return true;
        }

        if (!await this.cleanup.Reader.WaitToReadAsync(cancellationToken))
        {
            return false;
        }

        while (this.cleanup.Reader.TryRead(out var item))
        {
            pending.Add(item);
        }

        if (pending.Count == 0)
        {
            return true;
        }

        await Task.Delay(this.cleanupDebounce, cancellationToken);
        while (this.cleanup.Reader.TryRead(out var item))
        {
            pending.Add(item);
        }

        return true;
    }

    private async Task ProcessCleanupBatch(
        List<WorkQueueDurabilityCleanupItem> pending,
        CancellationToken cancellationToken)
    {
        if (store is null || pending.Count == 0)
        {
            return;
        }

        var latestByWorker = new Dictionary<WorkerId, WorkQueueDurabilityCleanupKind>();
        foreach (var item in pending.Where(item =>
            item.Kind == WorkQueueDurabilityCleanupKind.DeleteFinal ||
            !latestByWorker.ContainsKey(item.WorkerId)))
        {
            latestByWorker[item.WorkerId] = item.Kind;
        }

        var delete = CreateCleanupRequests(latestByWorker, WorkQueueDurabilityCleanupKind.DeleteFinal);
        var retain = CreateCleanupRequests(latestByWorker, WorkQueueDurabilityCleanupKind.RetainFailed);

        await this.ProcessCleanupItems(
            delete,
            store.DeleteFinal,
            item =>
            {
                this.RemoveLeaseIfCurrent(item.Lease);
                this.idempotencyReservations.TryRemove(item.WorkerId, out _);
                this.retainedFailures.TryRemove(item.WorkerId, out _);
            },
            cancellationToken);

        await this.ProcessCleanupItems(
            retain,
            store.RetainFailed,
            item =>
            {
                this.RemoveLeaseIfCurrent(item.Lease);
                this.retainedFailures[item.WorkerId] = 0;
            },
            cancellationToken);
    }

    private List<WorkQueueDurabilityCleanupRequest> CreateCleanupRequests(
        Dictionary<WorkerId, WorkQueueDurabilityCleanupKind> items,
        WorkQueueDurabilityCleanupKind kind)
    {
        var requests = new List<WorkQueueDurabilityCleanupRequest>();
        foreach (var item in items)
        {
            if (item.Value != kind)
            {
                continue;
            }

            this.leases.TryGetValue(item.Key, out var lease);
            requests.Add(new WorkQueueDurabilityCleanupRequest(item.Key, lease));
        }

        return requests;
    }

    private async Task ProcessCleanupItems(
        List<WorkQueueDurabilityCleanupRequest> requests,
        Func<IReadOnlyList<WorkQueueDurabilityCleanupRequest>, CancellationToken, Task> cleanupAction,
        Action<WorkQueueDurabilityCleanupRequest> completed,
        CancellationToken cancellationToken)
    {
        while (requests.Count > 0 && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                await cleanupAction(requests, cancellationToken);
                foreach (var request in requests)
                {
                    completed(request);
                }

                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (WorkQueueDurabilityLeaseLostException exception)
            {
                this.HandleLostLeases(exception.Leases);
                var lostWorkerIds = exception.Leases.Select(lease => lease.WorkerId).ToHashSet();
                foreach (var request in requests.Where(request => !lostWorkerIds.Contains(request.WorkerId)))
                {
                    completed(request);
                }

                return;
            }
            catch
            {
                if (!await this.DelayUnlessStopping(TimeSpan.FromSeconds(10), cancellationToken))
                {
                    return;
                }
            }
        }
    }

    private async Task RunLeaseRenewal(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && isAcceptingWork())
        {
            try
            {
                await Task.Delay(this.leaseRenewalInterval, cancellationToken);
                var activeLeases = this.leases.Values.ToList();
                if (activeLeases.Count > 0 && store is not null)
                {
                    await store.RenewLeases(activeLeases, this.leaseDuration, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (WorkQueueDurabilityLeaseLostException exception)
            {
                this.HandleLostLeases(exception.Leases);
            }
            catch
            {
                if (!await this.DelayUnlessStopping(this.retryDelay, cancellationToken))
                {
                    return;
                }
            }
        }
    }

    private async Task DrainUntilEmpty(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (await this.DrainOnce(cancellationToken) == 0)
                {
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                if (!await this.DelayUnlessStopping(this.retryDelay, cancellationToken))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return;
                }
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<int> DrainOnce(CancellationToken cancellationToken)
    {
        if (store is null)
        {
            return 0;
        }

        var request = new WorkQueueDurabilityClaimRequest(
            workSystemName,
            this.ownerId,
            BatchSize: this.claimBatchSize,
            LeaseDuration: this.leaseDuration);

        var count = 0;
        await foreach (var entry in store.ClaimReady(request, cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await acceptPersistedEntry(entry, cancellationToken);
            count++;
        }

        return count;
    }

    private void ConfigureFallbackPolling(IReadOnlyList<WorkDefinition> definitions)
    {
        var configuredInterval = definitions
            .Where(definition => definition.Configuration.QueueDurability.IsEnabled)
            .Select(definition => definition.Configuration.QueueDurability.FallbackPollingInterval)
            .DefaultIfEmpty(this.defaultReaderPollInterval)
            .Min();

        Interlocked.Exchange(ref this.readerPollIntervalTicks, configuredInterval.Ticks);
    }

    private TimeSpan ReaderPollInterval
        => TimeSpan.FromTicks(Interlocked.Read(ref this.readerPollIntervalTicks));

    private void HandleLostLeases(IReadOnlyList<WorkQueueDurabilityLease> lostLeases)
    {
        foreach (var lostLease in lostLeases.Where(this.RemoveLeaseIfCurrent))
        {
            leaseLost(lostLease.WorkerId);
        }
    }

    private bool RemoveLeaseIfCurrent(WorkQueueDurabilityLease? lease)
    {
        if (lease is null)
        {
            return false;
        }

        return this.leases.TryGetValue(lease.WorkerId, out var current) &&
            current.LeaseId == lease.LeaseId &&
            ((ICollection<KeyValuePair<WorkerId, WorkQueueDurabilityLease>>)this.leases)
                .Remove(new KeyValuePair<WorkerId, WorkQueueDurabilityLease>(lease.WorkerId, current));
    }

    private async Task<bool> DelayUnlessStopping(TimeSpan delay, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(delay, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private sealed record WorkQueueDurabilityCleanupItem(
        WorkerId WorkerId,
        WorkQueueDurabilityCleanupKind Kind,
        TaskCompletionSource? Flushed = null);

    private enum WorkQueueDurabilityCleanupKind
    {
        RetainFailed,
        DeleteFinal,
        Flush,
    }
}
