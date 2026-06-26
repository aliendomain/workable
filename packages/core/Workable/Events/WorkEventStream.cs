using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Workable;
internal sealed class WorkEventStream : IWorkEventStream, IAsyncDisposable
{
    private static readonly WorkEventSubscriptionOptions DefaultOptions = new();
    private readonly Lock sync = new();
    private SubscriptionIndex index = SubscriptionIndex.Empty;
    private bool isDisposed;

    internal int ActiveSubscriptionCount
        => Volatile.Read(ref this.index).ActiveCount;

    public IWorkEventSubscription Subscribe(
        WorkEventFilter? filter = null,
        WorkEventSubscriptionOptions? options = null)
    {
        options ??= DefaultOptions;
        if (options.Capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Capacity, "Event subscription capacity must be greater than zero.");
        }

        var subscription = new WorkEventSubscription(this, filter, options);
        lock (this.sync)
        {
            ObjectDisposedException.ThrowIf(this.isDisposed, this);
            this.index = SubscriptionIndex.Create([.. this.index.All, subscription]);
        }

        return subscription;
    }

    public void Publish(WorkEvent workEvent)
    {
        ArgumentNullException.ThrowIfNull(workEvent);

        if (!this.TryGetActiveIndex(out var index))
        {
            return;
        }

        PublishToSubscribers(index, workEvent);
    }

    internal void Publish<TState>(TState state, Func<TState, WorkEvent> createEvent)
    {
        ArgumentNullException.ThrowIfNull(createEvent);

        if (!this.TryGetActiveIndex(out var index))
        {
            return;
        }

        PublishToSubscribers(index, createEvent(state));
    }

    internal void Publish<TState>(
        TState state,
        Func<TState, WorkEventMetadata> createMetadata,
        Func<TState, WorkEvent> createEvent)
    {
        ArgumentNullException.ThrowIfNull(createMetadata);
        ArgumentNullException.ThrowIfNull(createEvent);

        if (!this.TryGetActiveIndex(out var index))
        {
            return;
        }

        PublishToSubscribers(index, state, createMetadata, createEvent);
    }

    internal void Publish<TState>(
        WorkEventMetadata metadata,
        TState state,
        Func<TState, WorkEvent> createEvent)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(createEvent);

        if (!this.TryGetActiveIndex(out var index))
        {
            return;
        }

        WorkEvent? workEvent = null;
        PublishToUnfilteredSubscribers(index.Unfiltered, ref workEvent, state, createEvent);
        if (!index.HasFilteredSubscriptions)
        {
            return;
        }

        PublishToFilteredSubscribers(index, metadata, ref workEvent, state, createEvent);
    }

    public ValueTask DisposeAsync()
    {
        WorkEventSubscription[] subscribers;
        lock (this.sync)
        {
            if (this.isDisposed)
            {
                return ValueTask.CompletedTask;
            }

            this.isDisposed = true;
            subscribers = this.index.All;
            this.index = SubscriptionIndex.Empty;
        }

        foreach (var subscription in subscribers)
        {
            subscription.DisposeFromOwner();
        }

        return ValueTask.CompletedTask;
    }

    private void Remove(WorkEventSubscription subscription)
    {
        lock (this.sync)
        {
            var subscriptions = this.index.All;
            var index = Array.IndexOf(subscriptions, subscription);
            if (index < 0)
            {
                return;
            }

            if (subscriptions.Length == 1)
            {
                this.index = SubscriptionIndex.Empty;
                return;
            }

            var remaining = new WorkEventSubscription[subscriptions.Length - 1];
            if (index > 0)
            {
                Array.Copy(subscriptions, 0, remaining, 0, index);
            }

            if (index < subscriptions.Length - 1)
            {
                Array.Copy(subscriptions, index + 1, remaining, index, subscriptions.Length - index - 1);
            }

            this.index = SubscriptionIndex.Create(remaining);
        }
    }

    private bool TryGetActiveIndex([NotNullWhen(true)] out SubscriptionIndex? index)
    {
        if (Volatile.Read(ref this.isDisposed))
        {
            index = null;
            return false;
        }

        index = Volatile.Read(ref this.index);
        return index.ActiveCount > 0;
    }

    private static void PublishToSubscribers(SubscriptionIndex index, WorkEvent workEvent)
    {
        PublishToSubscribers(index.Unfiltered, workEvent);
        PublishToIdentifierSubscribers(index.IdentifierSubscriptions, workEvent.Identifiers, workEvent);
        PublishToSubscribers(index.ScannedFiltered, workEvent);
    }

    private static void PublishToSubscribers(WorkEventSubscription[] subscribers, WorkEvent workEvent)
    {
        foreach (var subscription in subscribers)
        {
            subscription.Publish(workEvent);
        }
    }

    private static void PublishToSubscribers<TState>(
        SubscriptionIndex index,
        TState state,
        Func<TState, WorkEventMetadata> createMetadata,
        Func<TState, WorkEvent> createEvent)
    {
        var hasFilteredSubscribers = index.HasFilteredSubscriptions;
        WorkEventMetadata? metadata = hasFilteredSubscribers
            ? createMetadata(state)
            : null;
        WorkEvent? workEvent = null;

        PublishToUnfilteredSubscribers(index.Unfiltered, ref workEvent, state, createEvent);
        if (!hasFilteredSubscribers)
        {
            return;
        }

        PublishToFilteredSubscribers(index, metadata!, ref workEvent, state, createEvent);
    }

    private static void PublishToIdentifierSubscribers(
        IReadOnlyDictionary<WorkIdentifier, WorkEventSubscription[]> subscriptionsByIdentifier,
        IReadOnlySet<WorkIdentifier> identifiers,
        WorkEvent workEvent)
    {
        if (subscriptionsByIdentifier.Count == 0 || identifiers.Count == 0)
        {
            return;
        }

        foreach (var identifier in identifiers)
        {
            if (!subscriptionsByIdentifier.TryGetValue(identifier, out var subscribers))
            {
                continue;
            }

            PublishToSubscribers(subscribers, workEvent);
        }
    }

    private static void PublishToUnfilteredSubscribers<TState>(
        WorkEventSubscription[] subscribers,
        ref WorkEvent? workEvent,
        TState state,
        Func<TState, WorkEvent> createEvent)
    {
        foreach (var subscription in subscribers)
        {
            if (!subscription.ShouldPublishUnfiltered())
            {
                continue;
            }

            workEvent ??= createEvent(state);
            subscription.PublishMatched(workEvent);
        }
    }

    private static void PublishToFilteredSubscribers<TState>(
        SubscriptionIndex index,
        WorkEventMetadata metadata,
        ref WorkEvent? workEvent,
        TState state,
        Func<TState, WorkEvent> createEvent)
    {
        if (index.IdentifierSubscriptions.Count > 0)
        {
            foreach (var identifier in metadata.Identifiers)
            {
                if (!index.IdentifierSubscriptions.TryGetValue(identifier, out var subscribers))
                {
                    continue;
                }

                foreach (var subscription in subscribers)
                {
                    if (!subscription.ShouldPublish(metadata))
                    {
                        continue;
                    }

                    workEvent ??= createEvent(state);
                    subscription.PublishMatched(workEvent);
                }
            }
        }

        foreach (var subscription in index.ScannedFiltered)
        {
            if (!subscription.ShouldPublish(metadata))
            {
                continue;
            }

            workEvent ??= createEvent(state);
            subscription.PublishMatched(workEvent);
        }
    }

    private sealed class SubscriptionIndex(
        WorkEventSubscription[] all,
        WorkEventSubscription[] unfiltered,
        WorkEventSubscription[] scannedFiltered,
        IReadOnlyDictionary<WorkIdentifier, WorkEventSubscription[]> identifierSubscriptions)
    {
        public static SubscriptionIndex Empty { get; } = new(
            [],
            [],
            [],
            new Dictionary<WorkIdentifier, WorkEventSubscription[]>());

        public WorkEventSubscription[] All { get; } = all;

        public WorkEventSubscription[] Unfiltered { get; } = unfiltered;

        public WorkEventSubscription[] ScannedFiltered { get; } = scannedFiltered;

        public IReadOnlyDictionary<WorkIdentifier, WorkEventSubscription[]> IdentifierSubscriptions { get; } = identifierSubscriptions;

        public int ActiveCount => this.All.Length;

        public bool HasFilteredSubscriptions
            => this.ScannedFiltered.Length > 0 || this.IdentifierSubscriptions.Count > 0;

        public static SubscriptionIndex Create(WorkEventSubscription[] subscriptions)
        {
            if (subscriptions.Length == 0)
            {
                return Empty;
            }

            var unfiltered = new List<WorkEventSubscription>();
            var scannedFiltered = new List<WorkEventSubscription>();
            var byIdentifier = new Dictionary<WorkIdentifier, List<WorkEventSubscription>>();

            foreach (var subscription in subscriptions)
            {
                if (!subscription.HasFilter)
                {
                    unfiltered.Add(subscription);
                    continue;
                }

                if (subscription.IdentifierFilter is { } identifier)
                {
                    if (!byIdentifier.TryGetValue(identifier, out var indexed))
                    {
                        indexed = [];
                        byIdentifier[identifier] = indexed;
                    }

                    indexed.Add(subscription);
                    continue;
                }

                scannedFiltered.Add(subscription);
            }

            return new SubscriptionIndex(
                subscriptions,
                [.. unfiltered],
                [.. scannedFiltered],
                byIdentifier.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToArray()));
        }
    }

    private sealed class WorkEventSubscription(
        WorkEventStream owner,
        WorkEventFilter? filter,
        WorkEventSubscriptionOptions options) : IWorkEventSubscription, IWorkEventSubscriptionDiagnostics
    {
        private readonly int capacity = options.Capacity;
        private readonly WorkEventOverflowBehavior overflowBehavior = options.OverflowBehavior;
        private readonly Channel<WorkEvent> events = Channel.CreateBounded<WorkEvent>(
            new BoundedChannelOptions(options.Capacity)
            {
                FullMode = ToBoundedChannelFullMode(options.OverflowBehavior),
                SingleReader = true,
                SingleWriter = false,
            });
        private int isDisposed;
        private int queuedCount;
        private int peakQueuedCount;
        private long acceptedEventCount;
        private long deliveredEventCount;
        private long droppedEventCount;

        internal bool HasFilter => filter is not null;

        internal WorkIdentifier? IdentifierFilter => filter?.Identifier;

        public IAsyncEnumerable<WorkEvent> Read(CancellationToken cancellationToken = default)
            => new WorkEventSubscriptionEnumerable(this, cancellationToken);

        public ValueTask DisposeAsync()
        {
            this.DisposeSubscription();
            return ValueTask.CompletedTask;
        }

        public WorkEventSubscriptionDiagnosticsSnapshot GetDiagnosticsSnapshot()
            => new(
                this.capacity,
                this.overflowBehavior,
                Volatile.Read(ref this.queuedCount),
                Volatile.Read(ref this.peakQueuedCount),
                Interlocked.Read(ref this.acceptedEventCount),
                Interlocked.Read(ref this.deliveredEventCount),
                Interlocked.Read(ref this.droppedEventCount));

        private void DisposeSubscription()
        {
            if (Interlocked.Exchange(ref this.isDisposed, 1) == 1)
            {
                return;
            }

            this.events.Writer.TryComplete();
            owner.Remove(this);
        }

        internal void DisposeFromOwner()
        {
            if (Interlocked.Exchange(ref this.isDisposed, 1) == 0)
            {
                this.events.Writer.TryComplete();
            }
        }

        internal void Publish(WorkEvent workEvent)
        {
            if (Volatile.Read(ref this.isDisposed) == 1 ||
                filter?.Matches(workEvent) == false)
            {
                return;
            }

            if (!this.CanAcceptWrite())
            {
                Interlocked.Increment(ref this.droppedEventCount);
                return;
            }

            this.PublishMatched(workEvent);
        }

        internal void PublishMatched(WorkEvent workEvent)
        {
            var replacedExistingEvent = this.overflowBehavior != WorkEventOverflowBehavior.DropWrite &&
                Volatile.Read(ref this.queuedCount) >= this.capacity;
            if (this.events.Writer.TryWrite(workEvent))
            {
                Interlocked.Increment(ref this.acceptedEventCount);
                if (replacedExistingEvent)
                {
                    Interlocked.Increment(ref this.droppedEventCount);
                }
                this.TrackQueuedWrite();
            }
            else
            {
                Interlocked.Increment(ref this.droppedEventCount);
            }
        }

        internal bool ShouldPublish(WorkEventMetadata metadata)
            => Volatile.Read(ref this.isDisposed) == 0 &&
                this.CanAcceptWrite() &&
                (filter is null ||
                    ((filter.WorkerId is null || filter.WorkerId == metadata.WorkerId) &&
                        (filter.DefinitionName is null || string.Equals(filter.DefinitionName, metadata.DefinitionName, StringComparison.OrdinalIgnoreCase)) &&
                        (filter.DefinitionNames is not { Count: > 0 } ||
                            (!string.IsNullOrWhiteSpace(metadata.DefinitionName) &&
                                filter.DefinitionNames.Contains(metadata.DefinitionName, StringComparer.OrdinalIgnoreCase))) &&
                        (filter.SubjectId is null || filter.SubjectId == metadata.SubjectId) &&
                        (filter.ConcurrencyKey is null || filter.ConcurrencyKey == metadata.ConcurrencyKey) &&
                        (filter.Identifier is null || metadata.ContainsIdentifier(filter.Identifier.Value)) &&
                        metadata.ContainsAnyKey(filter.Keys) &&
                        filter.EventTypeMatches(metadata.EventType)));

        internal bool ShouldPublishUnfiltered()
            => Volatile.Read(ref this.isDisposed) == 0 &&
                this.CanAcceptWrite();

        private bool CanAcceptWrite()
            => this.overflowBehavior != WorkEventOverflowBehavior.DropWrite ||
                Volatile.Read(ref this.queuedCount) < this.capacity;

        private void TrackQueuedWrite()
        {
            while (true)
            {
                var current = Volatile.Read(ref this.queuedCount);
                if (current >= this.capacity)
                {
                    return;
                }

                if (Interlocked.CompareExchange(ref this.queuedCount, current + 1, current) == current)
                {
                    TrackPeakQueuedCount(current + 1);
                    return;
                }
            }
        }

        private void TrackPeakQueuedCount(int queued)
        {
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

        private static BoundedChannelFullMode ToBoundedChannelFullMode(WorkEventOverflowBehavior behavior)
            => behavior switch
            {
                WorkEventOverflowBehavior.DropNewest => BoundedChannelFullMode.DropNewest,
                WorkEventOverflowBehavior.DropWrite => BoundedChannelFullMode.DropWrite,
                _ => BoundedChannelFullMode.DropOldest,
            };

        private sealed class WorkEventSubscriptionEnumerable(
            WorkEventSubscription subscription,
            CancellationToken cancellationToken) : IAsyncEnumerable<WorkEvent>
        {
            public IAsyncEnumerator<WorkEvent> GetAsyncEnumerator(CancellationToken enumeratorCancellationToken = default)
                => new WorkEventSubscriptionEnumerator(
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

        private sealed class WorkEventSubscriptionEnumerator(
            WorkEventSubscription subscription,
            CancellationToken cancellationToken,
            CancellationTokenSource? linkedCancellation) : IAsyncEnumerator<WorkEvent>
        {
            public WorkEvent Current { get; private set; } = null!;

            public async ValueTask<bool> MoveNextAsync()
            {
                while (await subscription.events.Reader.WaitToReadAsync(cancellationToken))
                {
                    if (!subscription.events.Reader.TryRead(out var workEvent))
                    {
                        continue;
                    }

                    Interlocked.Decrement(ref subscription.queuedCount);
                    Interlocked.Increment(ref subscription.deliveredEventCount);
                    this.Current = workEvent;
                    return true;
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
