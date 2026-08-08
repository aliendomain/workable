using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Channels;

namespace Workable;

internal sealed class WorkIterationStatusStream : IWorkIterationStatusStream, IAsyncDisposable
{
    private const int DefaultRetainedItemCapacity = 4_096;
    private const int DefaultReplayPayloadByteCapacity = 4 * 1_024 * 1_024;
    private const int DefaultSystemReplayItemCapacity = 65_536;
    private const int DefaultSystemReplayByteCapacity = 64 * 1_024 * 1_024;
    private const int DefaultMaximumPayloadBytes = 32 * 1_024;
    private const int DefaultMaximumTypeBytes = 256;
    private const int DefaultMaximumSubscriptions = 4_096;
    private const int DefaultMaximumSubscriptionsPerIteration = 64;
    private const int RetentionIndexCompactionSlack = 256;

    private readonly WorkSystemId workSystemId;
    private readonly string? workSystemName;
    private readonly int retainedItemCapacity;
    private readonly int replayPayloadByteCapacity;
    private readonly int systemReplayItemCapacity;
    private readonly int systemReplayByteCapacity;
    private readonly int maximumPayloadBytes;
    private readonly int maximumTypeBytes;
    private readonly int maximumSubscriptions;
    private readonly int maximumSubscriptionsPerIteration;
    private readonly Lock registrySync = new();
    private readonly object systemRetentionSync = new();
    private readonly ConcurrentDictionary<WorkerIterationReference, IterationBuffer> buffers = [];
    private readonly PriorityQueue<RetentionEntry, long> retentionIndex = new();
    private readonly HashSet<long> pendingRetentionOrders = [];
    private long nextRetentionOrder;
    private long retainedBytes;
    private int retainedItems;
    private int subscriptionCount;
    private int isDisposed;

    public WorkIterationStatusStream(
        WorkSystemId workSystemId,
        string? workSystemName,
        int retainedItemCapacity = DefaultRetainedItemCapacity,
        int replayPayloadByteCapacity = DefaultReplayPayloadByteCapacity,
        int systemReplayItemCapacity = DefaultSystemReplayItemCapacity,
        int systemReplayByteCapacity = DefaultSystemReplayByteCapacity,
        int maximumPayloadBytes = DefaultMaximumPayloadBytes,
        int maximumTypeBytes = DefaultMaximumTypeBytes,
        int maximumSubscriptions = DefaultMaximumSubscriptions,
        int maximumSubscriptionsPerIteration = DefaultMaximumSubscriptionsPerIteration)
    {
        if (retainedItemCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retainedItemCapacity),
                "The retained iteration status item capacity must be greater than zero.");
        }

        if (maximumPayloadBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumPayloadBytes),
                "The maximum iteration status payload bytes must be greater than zero.");
        }

        if (maximumTypeBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumTypeBytes),
                "The maximum iteration status type bytes must be greater than zero.");
        }

        if (replayPayloadByteCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(replayPayloadByteCapacity),
                "The replay byte capacity must be greater than zero.");
        }

        if (replayPayloadByteCapacity < (long)maximumPayloadBytes + maximumTypeBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(replayPayloadByteCapacity),
                "The replay byte capacity cannot be less than the combined maximum type and payload bytes.");
        }

        if (systemReplayItemCapacity < retainedItemCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(systemReplayItemCapacity),
                "The system replay item capacity cannot be less than the per-iteration item capacity.");
        }

        if (systemReplayByteCapacity < replayPayloadByteCapacity)
        {
            throw new ArgumentOutOfRangeException(
                nameof(systemReplayByteCapacity),
                "The system replay byte capacity cannot be less than the per-iteration replay byte capacity.");
        }

        if (maximumSubscriptionsPerIteration <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSubscriptionsPerIteration),
                "The maximum subscriptions per iteration must be greater than zero.");
        }

        if (maximumSubscriptions < maximumSubscriptionsPerIteration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumSubscriptions),
                "The maximum system subscriptions cannot be less than the per-iteration subscription limit.");
        }

        this.workSystemId = workSystemId;
        this.workSystemName = workSystemName;
        this.retainedItemCapacity = retainedItemCapacity;
        this.replayPayloadByteCapacity = replayPayloadByteCapacity;
        this.systemReplayItemCapacity = systemReplayItemCapacity;
        this.systemReplayByteCapacity = systemReplayByteCapacity;
        this.maximumPayloadBytes = maximumPayloadBytes;
        this.maximumTypeBytes = maximumTypeBytes;
        this.maximumSubscriptions = maximumSubscriptions;
        this.maximumSubscriptionsPerIteration = maximumSubscriptionsPerIteration;
    }

    public IWorkIterationStatusSubscription Subscribe(
        WorkerIterationReference iteration,
        long afterSequence = 0)
    {
        if (afterSequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(afterSequence), "The status sequence cursor cannot be negative.");
        }

        ObjectDisposedException.ThrowIf(this.IsDisposed, this);
        if (!this.buffers.TryGetValue(iteration, out var buffer))
        {
            throw Unavailable(iteration);
        }

        lock (buffer.Sync)
        {
            ObjectDisposedException.ThrowIf(this.IsDisposed, this);
            if (buffer.IsForgotten)
            {
                throw Unavailable(iteration);
            }

            ValidateCursor(iteration, buffer, afterSequence);
            if (buffer.Subscriptions.Count >= this.maximumSubscriptionsPerIteration)
            {
                throw new WorkIterationStatusSubscriptionLimitException(
                    iteration,
                    this.maximumSubscriptionsPerIteration,
                    isSystemLimit: false);
            }

            var totalSubscriptions = Interlocked.Increment(ref this.subscriptionCount);
            if (totalSubscriptions > this.maximumSubscriptions)
            {
                Interlocked.Decrement(ref this.subscriptionCount);
                throw new WorkIterationStatusSubscriptionLimitException(
                    iteration,
                    this.maximumSubscriptions,
                    isSystemLimit: true);
            }

            var subscription = new WorkIterationStatusSubscription(this, buffer, afterSequence + 1);
            buffer.Subscriptions.Add(subscription);
            return subscription;
        }
    }

    internal void Begin(WorkerIterationReference iteration, string workDefinitionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workDefinitionName);
        var buffer = this.GetOrCreate(iteration, workDefinitionName);
        lock (buffer.Sync)
        {
            ObjectDisposedException.ThrowIf(this.IsDisposed, this);
            ValidateDefinition(buffer, workDefinitionName);
            if (buffer.IsForgotten)
            {
                throw new InvalidOperationException("A forgotten iteration status stream cannot be restarted.");
            }
        }
    }

    internal void Publish(
        WorkerIterationReference iteration,
        string workDefinitionName,
        WorkIterationStatusUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        ArgumentException.ThrowIfNullOrWhiteSpace(update.Type);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDefinitionName);
        var trimmedType = update.Type.AsSpan().Trim();
        var typeBytes = Encoding.UTF8.GetByteCount(trimmedType);
        if (typeBytes > this.maximumTypeBytes)
        {
            throw new WorkIterationStatusTypeTooLargeException(typeBytes, this.maximumTypeBytes);
        }

        var type = trimmedType.Length == update.Type.Length ? update.Type : trimmedType.ToString();
        var payloadBytes = update.Data is { } data
            ? Encoding.UTF8.GetByteCount(data.GetRawText())
            : 0;
        if (payloadBytes > this.maximumPayloadBytes)
        {
            throw new WorkIterationStatusPayloadTooLargeException(payloadBytes, this.maximumPayloadBytes);
        }

        var retainedBytes = checked(typeBytes + payloadBytes);
        var buffer = this.GetOrCreate(iteration, workDefinitionName);
        var retention = this.BeginRetention(iteration);
        BufferedStatusItem buffered;
        WorkIterationStatusSubscription[] subscriptions;
        try
        {
            lock (buffer.Sync)
            {
                ObjectDisposedException.ThrowIf(this.IsDisposed, this);
                ValidateDefinition(buffer, workDefinitionName);
                if (buffer.IsCompleted)
                {
                    throw new InvalidOperationException(
                        $"Worker '{iteration.WorkerId}' iteration {iteration.Sequence} has already completed its status stream.");
                }

                var sequence = ++buffer.LastSequence;
                var item = new WorkIterationStatusItem(
                    DateTimeOffset.UtcNow,
                    this.workSystemId,
                    this.workSystemName,
                    iteration,
                    sequence,
                    buffer.WorkDefinitionName,
                    type,
                    update.Data?.Clone());
                buffered = new BufferedStatusItem(item, retainedBytes, retention);
                buffer.Append(buffered);
                Interlocked.Increment(ref this.retainedItems);
                Interlocked.Add(ref this.retainedBytes, retainedBytes);
                buffer.Trim(this.retainedItemCapacity, this.replayPayloadByteCapacity);
                subscriptions = [.. buffer.Subscriptions];
            }
        }
        finally
        {
            this.CompleteRetention(retention);
        }

        Pulse(subscriptions);
    }

    internal void Complete(WorkIterationStatusCompletion completion)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var iteration = new WorkerIterationReference(completion.WorkerId, completion.Iteration.Sequence);
        if (!completion.Iteration.IsFinal)
        {
            throw new ArgumentException("An iteration status completion must contain a final iteration.", nameof(completion));
        }

        this.Complete(iteration, completion);
    }

    internal void Complete(WorkerIterationReference iteration)
        => this.Complete(iteration, completion: null);

    private void Complete(
        WorkerIterationReference iteration,
        WorkIterationStatusCompletion? completion)
    {
        if (!this.buffers.TryGetValue(iteration, out var buffer))
        {
            return;
        }

        WorkIterationStatusSubscription[] subscriptions;
        lock (buffer.Sync)
        {
            if (this.IsDisposed || buffer.IsCompleted)
            {
                return;
            }

            buffer.IsCompleted = true;
            buffer.Completion = completion;
            subscriptions = [.. buffer.Subscriptions];
        }

        Pulse(subscriptions);
    }

    internal void Forget(WorkerIterationReference iteration)
    {
        if (!this.buffers.TryGetValue(iteration, out var buffer))
        {
            return;
        }

        var subscriptions = this.Forget(buffer);
        this.CompactRetentionIndex();
        Pulse(subscriptions);
    }

    internal void Forget(WorkerId workerId)
    {
        var subscriptions = new List<WorkIterationStatusSubscription>();
        foreach (var pair in this.buffers.Where(pair => pair.Key.WorkerId == workerId))
        {
            subscriptions.AddRange(this.Forget(pair.Value));
        }

        this.CompactRetentionIndex();
        Pulse(subscriptions);
    }

    internal bool TryGetDefinitionName(WorkerIterationReference iteration, out string? workDefinitionName)
    {
        if (this.buffers.TryGetValue(iteration, out var buffer))
        {
            lock (buffer.Sync)
            {
                if (!buffer.IsForgotten)
                {
                    workDefinitionName = buffer.WorkDefinitionName;
                    return true;
                }
            }
        }

        workDefinitionName = null;
        return false;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.isDisposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        IterationBuffer[] buffers;
        lock (this.registrySync)
        {
            buffers = [.. this.buffers.Values];
            this.buffers.Clear();
        }

        var subscriptions = new List<WorkIterationStatusSubscription>();
        foreach (var buffer in buffers)
        {
            lock (buffer.Sync)
            {
                buffer.IsForgotten = true;
                buffer.IsCompleted = true;
                subscriptions.AddRange(buffer.Subscriptions);
                buffer.ClearRetained();
            }
        }

        lock (this.systemRetentionSync)
        {
            this.retentionIndex.Clear();
            this.pendingRetentionOrders.Clear();
        }

        Pulse(subscriptions);
        return ValueTask.CompletedTask;
    }

    private bool IsDisposed => Volatile.Read(ref this.isDisposed) != 0;

    internal int RetentionIndexCount
    {
        get
        {
            lock (this.systemRetentionSync)
            {
                return this.retentionIndex.Count;
            }
        }
    }

    internal int RetainedItemCount => Volatile.Read(ref this.retainedItems);

    private IterationBuffer GetOrCreate(WorkerIterationReference iteration, string workDefinitionName)
    {
        if (this.buffers.TryGetValue(iteration, out var existing))
        {
            return existing;
        }

        lock (this.registrySync)
        {
            ObjectDisposedException.ThrowIf(this.IsDisposed, this);
            if (this.buffers.TryGetValue(iteration, out existing))
            {
                return existing;
            }

            var created = new IterationBuffer(this, iteration, workDefinitionName);
            this.buffers[iteration] = created;
            return created;
        }
    }

    private WorkIterationStatusSubscription[] Forget(IterationBuffer buffer)
    {
        lock (buffer.Sync)
        {
            if (buffer.IsForgotten)
            {
                return [];
            }

            buffer.IsForgotten = true;
            buffer.IsCompleted = true;
            var subscriptions = buffer.Subscriptions.ToArray();
            if (subscriptions.Length == 0)
            {
                buffer.ClearRetained();
                this.buffers.TryRemove(buffer.Iteration, out _);
            }

            return subscriptions;
        }
    }

    private ReadResult ReadNext(WorkIterationStatusSubscription subscription)
    {
        var buffer = subscription.Buffer;
        lock (buffer.Sync)
        {
            if (subscription.IsDisposed || this.IsDisposed)
            {
                return ReadResult.Completed;
            }

            if (subscription.NextSequence <= buffer.LastSequence)
            {
                if (buffer.RetainedCount == 0)
                {
                    throw CreateGap(subscription.Iteration, subscription.NextSequence - 1, buffer);
                }

                if (subscription.NextSequence < buffer.FirstItem.Sequence)
                {
                    throw CreateGap(subscription.Iteration, subscription.NextSequence - 1, buffer);
                }

                var item = buffer.Get(subscription.NextSequence);
                subscription.NextSequence++;
                return new ReadResult(item, IsCompleted: false);
            }

            return buffer.IsCompleted ? ReadResult.Completed : ReadResult.Pending;
        }
    }

    private void Remove(WorkIterationStatusSubscription subscription)
    {
        var buffer = subscription.Buffer;
        var clearedRetained = false;
        lock (buffer.Sync)
        {
            if (!buffer.Subscriptions.Remove(subscription))
            {
                return;
            }

            Interlocked.Decrement(ref this.subscriptionCount);
            if (buffer.IsForgotten && buffer.Subscriptions.Count == 0)
            {
                buffer.ClearRetained();
                this.buffers.TryRemove(buffer.Iteration, out _);
                clearedRetained = true;
            }
        }

        if (clearedRetained)
        {
            this.CompactRetentionIndex();
        }
    }

    private RetentionEntry BeginRetention(WorkerIterationReference iteration)
    {
        lock (this.systemRetentionSync)
        {
            // Index the publication before taking the iteration lock so later publishers cannot overtake an
            // earlier item during system-wide eviction. A pending head pauses eviction until its append settles.
            var retention = new RetentionEntry(iteration, ++this.nextRetentionOrder);
            this.retentionIndex.Enqueue(retention, retention.Order);
            this.pendingRetentionOrders.Add(retention.Order);
            return retention;
        }
    }

    private void CompleteRetention(RetentionEntry retention)
    {
        lock (this.systemRetentionSync)
        {
            this.pendingRetentionOrders.Remove(retention.Order);
            this.MaintainSystemRetentionLocked();
            this.CompactRetentionIndexIfNeededLocked();
        }
    }

    private void MaintainSystemRetentionLocked()
    {
        while (this.IsOverSystemRetentionCapacity())
        {
            if (!this.retentionIndex.TryPeek(out var oldest, out _) ||
                this.pendingRetentionOrders.Contains(oldest.Order))
            {
                // The publisher that owns this ordered head will resume retention maintenance in its finally path.
                return;
            }

            this.retentionIndex.Dequeue();
            if (!this.buffers.TryGetValue(oldest.Iteration, out var buffer))
            {
                continue;
            }

            if (!buffer.TryEvictFirst(oldest))
            {
                continue;
            }
        }
    }

    private void CompactRetentionIndex()
    {
        lock (this.systemRetentionSync)
        {
            this.CompactRetentionIndexIfNeededLocked();
        }
    }

    private void CompactRetentionIndexIfNeededLocked()
    {
        var retained = Volatile.Read(ref this.retainedItems);
        var maximumIndexCount = retained == 0
            ? 0
            : checked((retained * 2) + RetentionIndexCompactionSlack);
        if (this.retentionIndex.Count <= maximumIndexCount)
        {
            return;
        }

        var active = this.retentionIndex.UnorderedItems
            .Where(entry =>
                this.pendingRetentionOrders.Contains(entry.Element.Order) ||
                this.IsRetentionActive(entry.Element))
            .Select(static entry => entry.Element)
            .ToArray();
        this.retentionIndex.Clear();
        foreach (var item in active)
        {
            this.retentionIndex.Enqueue(item, item.Order);
        }
    }

    private bool IsRetentionActive(RetentionEntry retention)
        => this.buffers.TryGetValue(retention.Iteration, out var buffer) &&
            buffer.ContainsRetention(retention.Order);

    private bool IsOverSystemRetentionCapacity()
        => Volatile.Read(ref this.retainedItems) > this.systemReplayItemCapacity ||
            Volatile.Read(ref this.retainedBytes) > this.systemReplayByteCapacity;

    private void Release(int retainedBytes)
    {
        Interlocked.Decrement(ref this.retainedItems);
        Interlocked.Add(ref this.retainedBytes, -retainedBytes);
    }

    private static void ValidateDefinition(IterationBuffer buffer, string workDefinitionName)
    {
        if (!string.Equals(buffer.WorkDefinitionName, workDefinitionName, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("An iteration status stream cannot change work definitions.");
        }
    }

    private static void ValidateCursor(
        WorkerIterationReference iteration,
        IterationBuffer buffer,
        long afterSequence)
    {
        if (afterSequence > buffer.LastSequence)
        {
            throw new ArgumentOutOfRangeException(
                nameof(afterSequence),
                afterSequence,
                $"The status sequence cursor cannot exceed the last published sequence {buffer.LastSequence}.");
        }

        if (afterSequence == buffer.LastSequence)
        {
            return;
        }

        if (buffer.RetainedCount == 0 || afterSequence + 1 < buffer.FirstItem.Sequence)
        {
            throw CreateGap(iteration, afterSequence, buffer);
        }
    }

    private static WorkIterationStatusGapException CreateGap(
        WorkerIterationReference iteration,
        long afterSequence,
        IterationBuffer buffer)
        => buffer.RetainedCount == 0
            ? new WorkIterationStatusGapException(iteration, afterSequence, null, null)
            : new WorkIterationStatusGapException(
                iteration,
                afterSequence,
                buffer.FirstItem.Sequence,
                buffer.LastSequence);

    private static KeyNotFoundException Unavailable(WorkerIterationReference iteration)
        => new($"Worker '{iteration.WorkerId}' iteration {iteration.Sequence} does not have a status stream.");

    private static void Pulse(IEnumerable<WorkIterationStatusSubscription> subscriptions)
    {
        foreach (var subscription in subscriptions)
        {
            subscription.Pulse();
        }
    }

    private sealed class IterationBuffer(
        WorkIterationStatusStream owner,
        WorkerIterationReference iteration,
        string workDefinitionName)
    {
        private const int CompactionThreshold = 256;
        private readonly List<BufferedStatusItem?> items = [];
        private int firstIndex;
        private long retainedBytes;

        public Lock Sync { get; } = new();

        public WorkerIterationReference Iteration { get; } = iteration;

        public string WorkDefinitionName { get; } = workDefinitionName;

        public int RetainedCount => this.items.Count - this.firstIndex;

        public WorkIterationStatusItem FirstItem => this.items[this.firstIndex]!.Value.Item;

        public HashSet<WorkIterationStatusSubscription> Subscriptions { get; } = [];

        public long LastSequence { get; set; }

        public bool IsCompleted { get; set; }

        public WorkIterationStatusCompletion? Completion { get; set; }

        public bool IsForgotten { get; set; }

        public void Append(BufferedStatusItem item)
        {
            this.items.Add(item);
            this.retainedBytes += item.RetainedBytes;
        }

        public void Trim(int itemCapacity, int byteCapacity)
        {
            while (this.RetainedCount > itemCapacity || this.retainedBytes > byteCapacity)
            {
                this.RemoveFirst();
            }

            this.CompactIfNeeded();
        }

        public bool TryEvictFirst(RetentionEntry expected)
        {
            lock (this.Sync)
            {
                if (this.RetainedCount == 0 ||
                    this.items[this.firstIndex]!.Value.Retention.Order != expected.Order)
                {
                    return false;
                }

                this.RemoveFirst();
                this.CompactIfNeeded();
                return true;
            }
        }

        public bool ContainsRetention(long order)
        {
            lock (this.Sync)
            {
                if (this.RetainedCount == 0)
                {
                    return false;
                }

                // Iteration buffers only remove a retained prefix, so the active orders form one contiguous range.
                return order >= this.items[this.firstIndex]!.Value.Retention.Order &&
                    order <= this.items[^1]!.Value.Retention.Order;
            }
        }

        public WorkIterationStatusItem Get(long sequence)
        {
            var retainedOffset = checked((int)(sequence - this.FirstItem.Sequence));
            return this.items[this.firstIndex + retainedOffset]!.Value.Item;
        }

        public void ClearRetained()
        {
            while (this.RetainedCount > 0)
            {
                this.RemoveFirst();
            }

            this.items.Clear();
            this.firstIndex = 0;
        }

        private void RemoveFirst()
        {
            var removed = this.items[this.firstIndex]!;
            this.items[this.firstIndex] = null;
            this.firstIndex++;
            this.retainedBytes -= removed.Value.RetainedBytes;
            owner.Release(removed.Value.RetainedBytes);
        }

        private void CompactIfNeeded()
        {
            if (this.firstIndex >= CompactionThreshold && this.firstIndex >= this.items.Count / 2)
            {
                this.items.RemoveRange(0, this.firstIndex);
                this.firstIndex = 0;
            }
        }
    }

    private readonly record struct BufferedStatusItem(
        WorkIterationStatusItem Item,
        int RetainedBytes,
        RetentionEntry Retention);

    private readonly record struct RetentionEntry(
        WorkerIterationReference Iteration,
        long Order);

    private readonly record struct ReadResult(WorkIterationStatusItem? Item, bool IsCompleted)
    {
        public static ReadResult Pending { get; } = new(Item: null, IsCompleted: false);

        public static ReadResult Completed { get; } = new(Item: null, IsCompleted: true);
    }

    private sealed class WorkIterationStatusSubscription(
        WorkIterationStatusStream owner,
        IterationBuffer buffer,
        long nextSequence) : IWorkIterationStatusSubscription
    {
        private readonly Channel<bool> signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
        private int isDisposed;
        private int isReading;

        public IterationBuffer Buffer { get; } = buffer;

        public WorkerIterationReference Iteration => this.Buffer.Iteration;

        public long NextSequence { get; set; } = nextSequence;

        public bool IsDisposed => Volatile.Read(ref this.isDisposed) == 1;

        public WorkIterationStatusCompletion? Completion
        {
            get
            {
                lock (this.Buffer.Sync)
                {
                    return this.Buffer.Completion;
                }
            }
        }

        public IAsyncEnumerable<WorkIterationStatusItem> Read(CancellationToken cancellationToken = default)
            => this.ReadCore(cancellationToken);

        public ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref this.isDisposed, 1) == 0)
            {
                owner.Remove(this);
                this.signal.Writer.TryComplete();
            }

            return ValueTask.CompletedTask;
        }

        public void Pulse()
            => this.signal.Writer.TryWrite(true);

        private async IAsyncEnumerable<WorkIterationStatusItem> ReadCore(
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref this.isReading, 1) != 0)
            {
                throw new InvalidOperationException("An iteration status subscription can only be read once.");
            }

            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var result = owner.ReadNext(this);
                    if (result.Item is { } item)
                    {
                        yield return item;
                        continue;
                    }

                    if (result.IsCompleted)
                    {
                        yield break;
                    }

                    try
                    {
                        await this.signal.Reader.ReadAsync(cancellationToken);
                    }
                    catch (ChannelClosedException)
                    {
                        yield break;
                    }
                }
            }
            finally
            {
                await this.DisposeAsync();
            }
        }
    }
}
