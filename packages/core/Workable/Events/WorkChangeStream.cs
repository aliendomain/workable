using System.Diagnostics.CodeAnalysis;

namespace Workable;

internal sealed class WorkChangeStream : IWorkChangeStream, IAsyncDisposable
{
    private static readonly WorkChangeSubscriptionOptions DefaultOptions = new();
    private readonly Lock sync = new();
    private WorkChangeSubscription[] subscriptions = [];
    private long sequence;
    private bool isDisposed;

    internal int ActiveSubscriptionCount
        => Volatile.Read(ref this.subscriptions).Length;

    public IWorkChangeSubscription Subscribe(WorkChangeSubscriptionOptions? options = null)
    {
        options ??= DefaultOptions;
        if (options.Capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Capacity, "Change subscription capacity must be greater than zero.");
        }

        var subscription = new WorkChangeSubscription(this, options);
        lock (this.sync)
        {
            ObjectDisposedException.ThrowIf(this.isDisposed, this);
            this.subscriptions = [.. this.subscriptions, subscription];
        }

        return subscription;
    }

    internal void Publish(WorkChangeKey key)
    {
        ArgumentNullException.ThrowIfNull(key);

        if (Volatile.Read(ref this.isDisposed))
        {
            return;
        }

        this.Publish(new WorkChange(
            Interlocked.Increment(ref this.sequence),
            DateTimeOffset.UtcNow,
            key));
    }

    private void Publish(WorkChange change)
    {
        ArgumentNullException.ThrowIfNull(change);

        var subscriptions = Volatile.Read(ref this.subscriptions);
        foreach (var subscription in subscriptions)
        {
            subscription.Publish(change);
        }
    }

    public ValueTask DisposeAsync()
    {
        WorkChangeSubscription[] subscribers;
        lock (this.sync)
        {
            if (this.isDisposed)
            {
                return ValueTask.CompletedTask;
            }

            this.isDisposed = true;
            subscribers = this.subscriptions;
            this.subscriptions = [];
        }

        foreach (var subscription in subscribers)
        {
            subscription.DisposeFromOwner();
        }

        return ValueTask.CompletedTask;
    }

    private void Remove(WorkChangeSubscription subscription)
    {
        lock (this.sync)
        {
            var index = Array.IndexOf(this.subscriptions, subscription);
            if (index < 0)
            {
                return;
            }

            if (this.subscriptions.Length == 1)
            {
                this.subscriptions = [];
                return;
            }

            var remaining = new WorkChangeSubscription[this.subscriptions.Length - 1];
            if (index > 0)
            {
                Array.Copy(this.subscriptions, 0, remaining, 0, index);
            }

            if (index < this.subscriptions.Length - 1)
            {
                Array.Copy(
                    this.subscriptions,
                    index + 1,
                    remaining,
                    index,
                    this.subscriptions.Length - index - 1);
            }

            this.subscriptions = remaining;
        }
    }

    private sealed class WorkChangeSubscription(
        WorkChangeStream owner,
        WorkChangeSubscriptionOptions options) : IWorkChangeSubscription, IWorkChangeSubscriptionDiagnostics
    {
        private readonly Lock sync = new();
        private readonly int capacity = options.Capacity;
        private readonly Queue<WorkChangeKey> pendingOrder = new();
        private readonly Dictionary<WorkChangeKey, WorkChange> pending = [];
        private TaskCompletionSource changed = CreateSignal();
        private int isDisposed;
        private int peakQueuedCount;
        private long acceptedChangeCount;
        private long deliveredChangeCount;
        private long coalescedChangeCount;
        private long droppedChangeCount;

        public IAsyncEnumerable<WorkChange> Read(CancellationToken cancellationToken = default)
            => new WorkChangeSubscriptionEnumerable(this, cancellationToken);

        public ValueTask DisposeAsync()
        {
            this.DisposeSubscription();
            return ValueTask.CompletedTask;
        }

        public WorkChangeSubscriptionDiagnosticsSnapshot GetDiagnosticsSnapshot()
        {
            int queued;
            lock (this.sync)
            {
                queued = this.pending.Count;
            }

            return new WorkChangeSubscriptionDiagnosticsSnapshot(
                this.capacity,
                queued,
                Volatile.Read(ref this.peakQueuedCount),
                Interlocked.Read(ref this.acceptedChangeCount),
                Interlocked.Read(ref this.deliveredChangeCount),
                Interlocked.Read(ref this.coalescedChangeCount),
                Interlocked.Read(ref this.droppedChangeCount));
        }

        internal void Publish(WorkChange change)
        {
            TaskCompletionSource? signal = null;
            lock (this.sync)
            {
                if (Volatile.Read(ref this.isDisposed) == 1)
                {
                    return;
                }

                Interlocked.Increment(ref this.acceptedChangeCount);
                if (this.pending.ContainsKey(change.Key))
                {
                    this.pending[change.Key] = change;
                    Interlocked.Increment(ref this.coalescedChangeCount);
                    return;
                }

                if (this.pending.Count >= this.capacity)
                {
                    var droppedKey = this.pendingOrder.Dequeue();
                    if (this.pending.Remove(droppedKey))
                    {
                        Interlocked.Increment(ref this.droppedChangeCount);
                    }
                }

                this.pending[change.Key] = change;
                this.pendingOrder.Enqueue(change.Key);
                this.TrackPeakQueuedCountLocked();
                signal = this.changed;
                this.changed = CreateSignal();
            }

            signal.TrySetResult();
        }

        internal void DisposeFromOwner()
        {
            this.DisposeSubscription(removeFromOwner: false);
        }

        private void DisposeSubscription(bool removeFromOwner = true)
        {
            if (Interlocked.Exchange(ref this.isDisposed, 1) == 1)
            {
                return;
            }

            TaskCompletionSource signal;
            lock (this.sync)
            {
                this.pending.Clear();
                this.pendingOrder.Clear();
                signal = this.changed;
                this.changed = CreateSignal();
            }

            signal.TrySetResult();
            if (removeFromOwner)
            {
                owner.Remove(this);
            }
        }

        private bool TryRead([NotNullWhen(true)] out WorkChange? change)
        {
            lock (this.sync)
            {
                if (this.pending.Count == 0)
                {
                    change = null;
                    return false;
                }

                var key = this.pendingOrder.Dequeue();
                if (!this.pending.Remove(key, out change))
                {
                    return false;
                }

                Interlocked.Increment(ref this.deliveredChangeCount);
                return true;
            }
        }

        private async Task WaitForChange(CancellationToken cancellationToken)
        {
            Task wait;
            lock (this.sync)
            {
                if (Volatile.Read(ref this.isDisposed) == 1 ||
                    this.pending.Count > 0)
                {
                    return;
                }

                wait = this.changed.Task;
            }

            await wait.WaitAsync(cancellationToken);
        }

        private void TrackPeakQueuedCountLocked()
        {
            var queued = this.pending.Count;
            while (true)
            {
                var currentPeak = Volatile.Read(ref this.peakQueuedCount);
                if (queued <= currentPeak)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref this.peakQueuedCount, queued, currentPeak) == currentPeak)
                {
                    return;
                }
            }
        }

        private static TaskCompletionSource CreateSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private sealed class WorkChangeSubscriptionEnumerable(
            WorkChangeSubscription subscription,
            CancellationToken cancellationToken) : IAsyncEnumerable<WorkChange>
        {
            public IAsyncEnumerator<WorkChange> GetAsyncEnumerator(CancellationToken enumeratorCancellationToken = default)
                => new WorkChangeSubscriptionEnumerator(
                    subscription,
                    CreateEffectiveCancellation(cancellationToken, enumeratorCancellationToken, out var linkedCancellation),
                    linkedCancellation);

            private static CancellationToken CreateEffectiveCancellation(
                CancellationToken subscriptionCancellationToken,
                CancellationToken enumeratorCancellationToken,
                out CancellationTokenSource? linkedCancellation)
            {
                linkedCancellation = null;
                if (!subscriptionCancellationToken.CanBeCanceled)
                {
                    return enumeratorCancellationToken;
                }

                if (!enumeratorCancellationToken.CanBeCanceled ||
                    subscriptionCancellationToken == enumeratorCancellationToken)
                {
                    return subscriptionCancellationToken;
                }

                linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    subscriptionCancellationToken,
                    enumeratorCancellationToken);
                return linkedCancellation.Token;
            }
        }

        private sealed class WorkChangeSubscriptionEnumerator(
            WorkChangeSubscription subscription,
            CancellationToken cancellationToken,
            CancellationTokenSource? linkedCancellation) : IAsyncEnumerator<WorkChange>
        {
            public WorkChange Current { get; private set; } = null!;

            public async ValueTask<bool> MoveNextAsync()
            {
                while (Volatile.Read(ref subscription.isDisposed) == 0)
                {
                    if (subscription.TryRead(out var change))
                    {
                        this.Current = change;
                        return true;
                    }

                    await subscription.WaitForChange(cancellationToken);
                }

                return false;
            }

            public ValueTask DisposeAsync()
            {
                linkedCancellation?.Dispose();
                subscription.DisposeSubscription();
                return ValueTask.CompletedTask;
            }
        }
    }
}
