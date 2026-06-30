using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;

namespace Workable;

internal sealed class WorkEventStream : IWorkEventStream, IAsyncDisposable
{
    private const int DefaultCursorLogCapacity = 8192;
    private static readonly WorkEventSubscriptionOptions DefaultOptions = new();
    private static readonly WorkEventSubscriptionOptions DefaultCursorLogOptions = new(DefaultCursorLogCapacity);
    private readonly Lock sync = new();
    private readonly EventLog eventLog = new();
    private SubscriptionIndex index = SubscriptionIndex.Empty;
    private bool isDisposed;

    internal int ActiveSubscriptionCount
        => Volatile.Read(ref this.index).ActiveCount;

    public IWorkEventSubscription Subscribe(
        WorkEventFilter? filter = null,
        WorkEventSubscriptionOptions? options = null)
    {
        options ??= GetDefaultOptions(filter);
        if (options.Capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.Capacity, "Event subscription capacity must be greater than zero.");
        }

        var deliveryKind = GetDeliveryKind(filter, options);
        var cursorSequence = deliveryKind == WorkEventSubscriptionDeliveryKind.CursorLog
            ? this.eventLog.TailSequence
            : 0;
        var subscription = new WorkEventSubscription(this, filter, options, deliveryKind, cursorSequence);
        lock (this.sync)
        {
            ObjectDisposedException.ThrowIf(this.isDisposed, this);
            this.index = SubscriptionIndex.Create([.. this.index.All, subscription]);
            this.eventLog.Trim(this.index.CursorRetentionCapacity);
        }

        return subscription;
    }

    public void Publish(WorkEvent workEvent)
    {
        ArgumentNullException.ThrowIfNull(workEvent);

        if (!this.TryGetActiveIndex(out var activeIndex))
        {
            return;
        }

        PublishToSubscribers(activeIndex, this.eventLog, workEvent);
    }

    internal void Publish<TState>(TState state, Func<TState, WorkEvent> createEvent)
    {
        ArgumentNullException.ThrowIfNull(createEvent);

        if (!this.TryGetActiveIndex(out var activeIndex))
        {
            return;
        }

        WorkEvent? workEvent = null;
        AppendToCursorLog(activeIndex, this.eventLog, ref workEvent, state, createEvent);
        PublishToRoutedSubscribers(activeIndex, ref workEvent, state, createEvent);
    }

    internal void Publish<TState>(
        TState state,
        Func<TState, WorkEventMetadata> createMetadata,
        Func<TState, WorkEvent> createEvent)
    {
        ArgumentNullException.ThrowIfNull(createMetadata);
        ArgumentNullException.ThrowIfNull(createEvent);

        if (!this.TryGetActiveIndex(out var activeIndex))
        {
            return;
        }

        WorkEventMetadata? metadata = activeIndex.HasRoutedFilteredSubscriptions
            ? createMetadata(state)
            : null;
        WorkEvent? workEvent = null;

        AppendToCursorLog(activeIndex, this.eventLog, ref workEvent, state, createEvent);
        PublishToRoutedUnfilteredSubscribers(activeIndex.RoutedUnfiltered, ref workEvent, state, createEvent);
        if (metadata is not null)
        {
            PublishToRoutedFilteredSubscribers(activeIndex, metadata, ref workEvent, state, createEvent);
        }
    }

    internal void Publish<TState>(
        WorkEventMetadata metadata,
        TState state,
        Func<TState, WorkEvent> createEvent)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(createEvent);

        if (!this.TryGetActiveIndex(out var activeIndex))
        {
            return;
        }

        WorkEvent? workEvent = null;
        AppendToCursorLog(activeIndex, this.eventLog, ref workEvent, state, createEvent);
        PublishToRoutedUnfilteredSubscribers(activeIndex.RoutedUnfiltered, ref workEvent, state, createEvent);
        if (activeIndex.HasRoutedFilteredSubscriptions)
        {
            PublishToRoutedFilteredSubscribers(activeIndex, metadata, ref workEvent, state, createEvent);
        }
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
            this.eventLog.Trim(0);
            this.eventLog.Complete();
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
                this.eventLog.Trim(0);
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
            this.eventLog.Trim(this.index.CursorRetentionCapacity);
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

    private static WorkEventSubscriptionDeliveryKind GetDeliveryKind(
        WorkEventFilter? filter,
        WorkEventSubscriptionOptions options)
        => ShouldUseCursorLog(filter, options)
            ? WorkEventSubscriptionDeliveryKind.CursorLog
            : WorkEventSubscriptionDeliveryKind.RoutedChannel;

    private static WorkEventSubscriptionOptions GetDefaultOptions(WorkEventFilter? filter)
        => ShouldUseCursorLog(filter, DefaultOptions)
            ? DefaultCursorLogOptions
            : DefaultOptions;

    private static bool ShouldUseCursorLog(
        WorkEventFilter? filter,
        WorkEventSubscriptionOptions options)
    {
        if (options.OverflowBehavior != WorkEventOverflowBehavior.DropOldest)
        {
            return false;
        }

        return !HasSelectiveRoutingAnchor(filter);
    }

    private static bool HasSelectiveRoutingAnchor(WorkEventFilter? filter)
        => filter is not null &&
            (filter.WorkerId is not null ||
                !string.IsNullOrWhiteSpace(filter.DefinitionName) ||
                HasValidStringFilter(filter.DefinitionNames) ||
                filter.SubjectId is not null ||
                filter.ConcurrencyKey is not null ||
                filter.Identifier is not null ||
                HasValidKeyFilter(filter.Keys) ||
                !string.IsNullOrWhiteSpace(filter.EventType) ||
                HasValidStringFilter(filter.EventTypes));

    private static bool HasValidStringFilter(IReadOnlySet<string>? values)
        => values is { Count: > 0 } &&
            values.Any(value => !string.IsNullOrWhiteSpace(value));

    private static bool HasValidKeyFilter(IReadOnlySet<WorkEventKeyFilter>? keys)
        => keys is { Count: > 0 } &&
            keys.Any(IsValidKeyFilter);

    private static bool IsValidKeyFilter(WorkEventKeyFilter key)
        => !string.IsNullOrWhiteSpace(key.Type) &&
            !string.IsNullOrWhiteSpace(key.Value);

    private static void PublishToSubscribers(
        SubscriptionIndex index,
        EventLog eventLog,
        WorkEvent workEvent)
    {
        if (index.HasCursorSubscriptions)
        {
            eventLog.Append(workEvent, index.CursorRetentionCapacity);
        }

        PublishToRoutedSubscribers(index, workEvent);
    }

    private static void PublishToRoutedSubscribers(SubscriptionIndex index, WorkEvent workEvent)
    {
        PublishToRoutedSubscribers(index.RoutedUnfiltered, workEvent);
        PublishToRoutedFilteredSubscribers(index, workEvent);
    }

    private static void PublishToRoutedFilteredSubscribers(SubscriptionIndex index, WorkEvent workEvent)
    {
        if (workEvent.WorkerId is { } workerId)
        {
            PublishToRoutedSubscribers(index.WorkerSubscriptions, workerId, workEvent);
        }

        if (workEvent.SubjectId is { } subjectId)
        {
            PublishToRoutedSubscribers(index.SubjectSubscriptions, subjectId, workEvent);
        }

        if (workEvent.ConcurrencyKey is { } concurrencyKey)
        {
            PublishToRoutedSubscribers(index.ConcurrencySubscriptions, concurrencyKey, workEvent);
        }

        if (!string.IsNullOrWhiteSpace(workEvent.WorkDefinitionName))
        {
            PublishToRoutedSubscribers(index.DefinitionSubscriptions, workEvent.WorkDefinitionName, workEvent);
        }

        PublishToRoutedSubscribers(index.EventTypeSubscriptions, workEvent.EventType, workEvent);
        PublishToIdentifierSubscribers(index.IdentifierSubscriptions, workEvent.Identifiers, workEvent);
        PublishToRoutedKeySubscribers(index, workEvent);
        PublishToRoutedSubscribers(index.ScannedFiltered, workEvent);
    }

    private static void PublishToRoutedSubscribers(WorkEventSubscription[] subscribers, WorkEvent workEvent)
    {
        foreach (var subscription in subscribers)
        {
            subscription.Publish(workEvent);
        }
    }

    private static void PublishToRoutedSubscribers<TKey>(
        IReadOnlyDictionary<TKey, WorkEventSubscription[]> subscribersByKey,
        TKey key,
        WorkEvent workEvent)
        where TKey : notnull
    {
        if (!subscribersByKey.TryGetValue(key, out var subscribers))
        {
            return;
        }

        PublishToRoutedSubscribers(subscribers, workEvent);
    }

    private static void PublishToRoutedSubscribers<TState>(
        SubscriptionIndex index,
        ref WorkEvent? workEvent,
        TState state,
        Func<TState, WorkEvent> createEvent)
    {
        PublishToRoutedUnfilteredSubscribers(index.RoutedUnfiltered, ref workEvent, state, createEvent);
        if (index.HasRoutedFilteredSubscriptions)
        {
            workEvent ??= createEvent(state);
            PublishToRoutedFilteredSubscribers(index, workEvent);
        }
    }

    private static void AppendToCursorLog<TState>(
        SubscriptionIndex index,
        EventLog eventLog,
        ref WorkEvent? workEvent,
        TState state,
        Func<TState, WorkEvent> createEvent)
    {
        if (!index.HasCursorSubscriptions)
        {
            return;
        }

        workEvent ??= createEvent(state);
        eventLog.Append(workEvent, index.CursorRetentionCapacity);
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

            PublishToRoutedSubscribers(subscribers, workEvent);
        }
    }

    private static void PublishToRoutedKeySubscribers(SubscriptionIndex index, WorkEvent workEvent)
    {
        HashSet<WorkEventSubscription>? published = null;
        if (workEvent.SubjectId is { } subjectId)
        {
            PublishToRoutedSubscribers(index.KeySubjectSubscriptions, subjectId, workEvent, ref published);
        }

        if (workEvent.ConcurrencyKey is { } concurrencyKey)
        {
            PublishToRoutedSubscribers(index.KeyConcurrencySubscriptions, concurrencyKey, workEvent, ref published);
        }

        if (index.KeyIdentifierSubscriptions.Count == 0 || workEvent.Identifiers.Count == 0)
        {
            return;
        }

        foreach (var identifier in workEvent.Identifiers)
        {
            PublishToRoutedSubscribers(index.KeyIdentifierSubscriptions, identifier, workEvent, ref published);
        }
    }

    private static void PublishToRoutedSubscribers<TKey>(
        IReadOnlyDictionary<TKey, WorkEventSubscription[]> subscribersByKey,
        TKey key,
        WorkEvent workEvent,
        ref HashSet<WorkEventSubscription>? published)
        where TKey : notnull
    {
        if (!subscribersByKey.TryGetValue(key, out var subscribers))
        {
            return;
        }

        published ??= [];
        foreach (var subscription in subscribers.Where(published.Add))
        {
            subscription.Publish(workEvent);
        }
    }

    private static void PublishToRoutedUnfilteredSubscribers<TState>(
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

    private static void PublishToRoutedFilteredSubscribers<TState>(
        SubscriptionIndex index,
        WorkEventMetadata metadata,
        ref WorkEvent? workEvent,
        TState state,
        Func<TState, WorkEvent> createEvent)
    {
        if (metadata.WorkerId is { } workerId)
        {
            PublishToIndexedFilteredSubscribers(index.WorkerSubscriptions, workerId, metadata, ref workEvent, state, createEvent);
        }

        if (metadata.SubjectId is { } subjectId)
        {
            PublishToIndexedFilteredSubscribers(index.SubjectSubscriptions, subjectId, metadata, ref workEvent, state, createEvent);
        }

        if (metadata.ConcurrencyKey is { } concurrencyKey)
        {
            PublishToIndexedFilteredSubscribers(index.ConcurrencySubscriptions, concurrencyKey, metadata, ref workEvent, state, createEvent);
        }

        if (!string.IsNullOrWhiteSpace(metadata.DefinitionName))
        {
            PublishToIndexedFilteredSubscribers(index.DefinitionSubscriptions, metadata.DefinitionName, metadata, ref workEvent, state, createEvent);
        }

        PublishToIndexedFilteredSubscribers(index.EventTypeSubscriptions, metadata.EventType, metadata, ref workEvent, state, createEvent);
        if (index.IdentifierSubscriptions.Count > 0)
        {
            foreach (var identifier in metadata.Identifiers)
            {
                PublishToIndexedFilteredSubscribers(index.IdentifierSubscriptions, identifier, metadata, ref workEvent, state, createEvent);
            }
        }

        PublishToIndexedKeySubscribers(index, metadata, ref workEvent, state, createEvent);

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

    private static void PublishToIndexedKeySubscribers<TState>(
        SubscriptionIndex index,
        WorkEventMetadata metadata,
        ref WorkEvent? workEvent,
        TState state,
        Func<TState, WorkEvent> createEvent)
    {
        HashSet<WorkEventSubscription>? published = null;
        if (metadata.SubjectId is { } subjectId)
        {
            PublishToIndexedFilteredSubscribers(index.KeySubjectSubscriptions, subjectId, metadata, ref workEvent, state, createEvent, ref published);
        }

        if (metadata.ConcurrencyKey is { } concurrencyKey)
        {
            PublishToIndexedFilteredSubscribers(index.KeyConcurrencySubscriptions, concurrencyKey, metadata, ref workEvent, state, createEvent, ref published);
        }

        if (index.KeyIdentifierSubscriptions.Count == 0)
        {
            return;
        }

        foreach (var identifier in metadata.Identifiers)
        {
            PublishToIndexedFilteredSubscribers(index.KeyIdentifierSubscriptions, identifier, metadata, ref workEvent, state, createEvent, ref published);
        }
    }

    private static void PublishToIndexedFilteredSubscribers<TKey, TState>(
        IReadOnlyDictionary<TKey, WorkEventSubscription[]> subscribersByKey,
        TKey key,
        WorkEventMetadata metadata,
        ref WorkEvent? workEvent,
        TState state,
        Func<TState, WorkEvent> createEvent)
        where TKey : notnull
    {
        if (!subscribersByKey.TryGetValue(key, out var subscribers))
        {
            return;
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

    private static void PublishToIndexedFilteredSubscribers<TKey, TState>(
        IReadOnlyDictionary<TKey, WorkEventSubscription[]> subscribersByKey,
        TKey key,
        WorkEventMetadata metadata,
        ref WorkEvent? workEvent,
        TState state,
        Func<TState, WorkEvent> createEvent,
        ref HashSet<WorkEventSubscription>? published)
        where TKey : notnull
    {
        if (!subscribersByKey.TryGetValue(key, out var subscribers))
        {
            return;
        }

        published ??= [];
        foreach (var subscription in subscribers)
        {
            if (!published.Add(subscription) ||
                !subscription.ShouldPublish(metadata))
            {
                continue;
            }

            workEvent ??= createEvent(state);
            subscription.PublishMatched(workEvent);
        }
    }

    private static Dictionary<TKey, WorkEventSubscription[]> ToSubscriptionDictionary<TKey>(
        Dictionary<TKey, List<WorkEventSubscription>> subscriptions)
        where TKey : notnull
    {
        var materialized = new Dictionary<TKey, WorkEventSubscription[]>(subscriptions.Comparer);
        foreach (var pair in subscriptions)
        {
            materialized[pair.Key] = pair.Value.ToArray();
        }

        return materialized;
    }

    private enum WorkEventSubscriptionDeliveryKind
    {
        RoutedChannel,
        CursorLog,
    }

    private sealed class SubscriptionIndex(
        WorkEventSubscription[] all,
        WorkEventSubscription[] cursorLog,
        WorkEventSubscription[] routedUnfiltered,
        WorkEventSubscription[] scannedFiltered,
        IReadOnlyDictionary<WorkerId, WorkEventSubscription[]> workerSubscriptions,
        IReadOnlyDictionary<WorkSubjectId, WorkEventSubscription[]> subjectSubscriptions,
        IReadOnlyDictionary<WorkConcurrencyKey, WorkEventSubscription[]> concurrencySubscriptions,
        IReadOnlyDictionary<string, WorkEventSubscription[]> definitionSubscriptions,
        IReadOnlyDictionary<string, WorkEventSubscription[]> eventTypeSubscriptions,
        IReadOnlyDictionary<WorkIdentifier, WorkEventSubscription[]> identifierSubscriptions,
        IReadOnlyDictionary<WorkSubjectId, WorkEventSubscription[]> keySubjectSubscriptions,
        IReadOnlyDictionary<WorkConcurrencyKey, WorkEventSubscription[]> keyConcurrencySubscriptions,
        IReadOnlyDictionary<WorkIdentifier, WorkEventSubscription[]> keyIdentifierSubscriptions,
        int cursorRetentionCapacity)
    {
        public static SubscriptionIndex Empty { get; } = new(
            [],
            [],
            [],
            [],
            new Dictionary<WorkerId, WorkEventSubscription[]>(),
            new Dictionary<WorkSubjectId, WorkEventSubscription[]>(),
            new Dictionary<WorkConcurrencyKey, WorkEventSubscription[]>(),
            new Dictionary<string, WorkEventSubscription[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, WorkEventSubscription[]>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<WorkIdentifier, WorkEventSubscription[]>(),
            new Dictionary<WorkSubjectId, WorkEventSubscription[]>(),
            new Dictionary<WorkConcurrencyKey, WorkEventSubscription[]>(),
            new Dictionary<WorkIdentifier, WorkEventSubscription[]>(),
            cursorRetentionCapacity: 0);

        public WorkEventSubscription[] All { get; } = all;

        public WorkEventSubscription[] CursorLog { get; } = cursorLog;

        public WorkEventSubscription[] RoutedUnfiltered { get; } = routedUnfiltered;

        public WorkEventSubscription[] ScannedFiltered { get; } = scannedFiltered;

        public IReadOnlyDictionary<WorkerId, WorkEventSubscription[]> WorkerSubscriptions { get; } = workerSubscriptions;

        public IReadOnlyDictionary<WorkSubjectId, WorkEventSubscription[]> SubjectSubscriptions { get; } = subjectSubscriptions;

        public IReadOnlyDictionary<WorkConcurrencyKey, WorkEventSubscription[]> ConcurrencySubscriptions { get; } = concurrencySubscriptions;

        public IReadOnlyDictionary<string, WorkEventSubscription[]> DefinitionSubscriptions { get; } = definitionSubscriptions;

        public IReadOnlyDictionary<string, WorkEventSubscription[]> EventTypeSubscriptions { get; } = eventTypeSubscriptions;

        public IReadOnlyDictionary<WorkIdentifier, WorkEventSubscription[]> IdentifierSubscriptions { get; } = identifierSubscriptions;

        public IReadOnlyDictionary<WorkSubjectId, WorkEventSubscription[]> KeySubjectSubscriptions { get; } = keySubjectSubscriptions;

        public IReadOnlyDictionary<WorkConcurrencyKey, WorkEventSubscription[]> KeyConcurrencySubscriptions { get; } = keyConcurrencySubscriptions;

        public IReadOnlyDictionary<WorkIdentifier, WorkEventSubscription[]> KeyIdentifierSubscriptions { get; } = keyIdentifierSubscriptions;

        public int CursorRetentionCapacity { get; } = cursorRetentionCapacity;

        public int ActiveCount => this.All.Length;

        public bool HasCursorSubscriptions => this.CursorLog.Length > 0;

        public bool HasRoutedFilteredSubscriptions
            => this.ScannedFiltered.Length > 0 ||
                this.WorkerSubscriptions.Count > 0 ||
                this.SubjectSubscriptions.Count > 0 ||
                this.ConcurrencySubscriptions.Count > 0 ||
                this.DefinitionSubscriptions.Count > 0 ||
                this.EventTypeSubscriptions.Count > 0 ||
                this.IdentifierSubscriptions.Count > 0 ||
                this.KeySubjectSubscriptions.Count > 0 ||
                this.KeyConcurrencySubscriptions.Count > 0 ||
                this.KeyIdentifierSubscriptions.Count > 0;

        public static SubscriptionIndex Create(WorkEventSubscription[] subscriptions)
        {
            if (subscriptions.Length == 0)
            {
                return Empty;
            }

            var cursorLog = new List<WorkEventSubscription>();
            var routedUnfiltered = new List<WorkEventSubscription>();
            var scannedFiltered = new List<WorkEventSubscription>();
            var byWorker = new Dictionary<WorkerId, List<WorkEventSubscription>>();
            var bySubject = new Dictionary<WorkSubjectId, List<WorkEventSubscription>>();
            var byConcurrency = new Dictionary<WorkConcurrencyKey, List<WorkEventSubscription>>();
            var byDefinition = new Dictionary<string, List<WorkEventSubscription>>(StringComparer.OrdinalIgnoreCase);
            var byEventType = new Dictionary<string, List<WorkEventSubscription>>(StringComparer.OrdinalIgnoreCase);
            var byIdentifier = new Dictionary<WorkIdentifier, List<WorkEventSubscription>>();
            var byKeySubject = new Dictionary<WorkSubjectId, List<WorkEventSubscription>>();
            var byKeyConcurrency = new Dictionary<WorkConcurrencyKey, List<WorkEventSubscription>>();
            var byKeyIdentifier = new Dictionary<WorkIdentifier, List<WorkEventSubscription>>();
            var cursorRetentionCapacity = 0;

            foreach (var subscription in subscriptions)
            {
                if (subscription.DeliveryKind == WorkEventSubscriptionDeliveryKind.CursorLog)
                {
                    cursorLog.Add(subscription);
                    cursorRetentionCapacity = Math.Max(cursorRetentionCapacity, subscription.Capacity);
                    continue;
                }

                if (!subscription.HasFilter)
                {
                    routedUnfiltered.Add(subscription);
                    continue;
                }

                if (subscription.WorkerIdFilter is { } workerId)
                {
                    Add(byWorker, workerId, subscription);
                    continue;
                }

                if (subscription.SubjectIdFilter is { } subjectId)
                {
                    Add(bySubject, subjectId, subscription);
                    continue;
                }

                if (subscription.ConcurrencyKeyFilter is { } concurrencyKey)
                {
                    Add(byConcurrency, concurrencyKey, subscription);
                    continue;
                }

                if (subscription.IdentifierFilter is { } identifier)
                {
                    Add(byIdentifier, identifier, subscription);
                    continue;
                }

                if (AddKeyFilters(
                    byKeySubject,
                    byKeyConcurrency,
                    byKeyIdentifier,
                    subscription.KeyFilters,
                    subscription))
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(subscription.DefinitionNameFilter))
                {
                    Add(byDefinition, subscription.DefinitionNameFilter, subscription);
                    continue;
                }

                if (subscription.DefinitionNamesFilter is { Count: > 0 } definitionNames)
                {
                    AddEach(byDefinition, definitionNames, subscription);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(subscription.EventTypeFilter))
                {
                    Add(byEventType, subscription.EventTypeFilter, subscription);
                    continue;
                }

                if (subscription.EventTypesFilter is { Count: > 0 } eventTypes)
                {
                    AddEach(byEventType, eventTypes, subscription);
                    continue;
                }

                scannedFiltered.Add(subscription);
            }

            return new SubscriptionIndex(
                subscriptions,
                [.. cursorLog],
                [.. routedUnfiltered],
                [.. scannedFiltered],
                ToSubscriptionDictionary(byWorker),
                ToSubscriptionDictionary(bySubject),
                ToSubscriptionDictionary(byConcurrency),
                ToSubscriptionDictionary(byDefinition),
                ToSubscriptionDictionary(byEventType),
                ToSubscriptionDictionary(byIdentifier),
                ToSubscriptionDictionary(byKeySubject),
                ToSubscriptionDictionary(byKeyConcurrency),
                ToSubscriptionDictionary(byKeyIdentifier),
                cursorRetentionCapacity);
        }

        private static bool AddKeyFilters(
            Dictionary<WorkSubjectId, List<WorkEventSubscription>> subscriptionsBySubject,
            Dictionary<WorkConcurrencyKey, List<WorkEventSubscription>> subscriptionsByConcurrency,
            Dictionary<WorkIdentifier, List<WorkEventSubscription>> subscriptionsByIdentifier,
            IReadOnlySet<WorkEventKeyFilter>? keys,
            WorkEventSubscription subscription)
        {
            if (keys is not { Count: > 0 })
            {
                return false;
            }

            var added = false;
            foreach (var key in keys)
            {
                if (!IsValidKeyFilter(key))
                {
                    continue;
                }

                if (key.Kind is null or WorkKeyKind.Subject)
                {
                    Add(subscriptionsBySubject, new WorkSubjectId(key.Type, key.Value), subscription);
                    added = true;
                }

                if (key.Kind is null or WorkKeyKind.ConcurrencyKey)
                {
                    Add(subscriptionsByConcurrency, new WorkConcurrencyKey(key.Type, key.Value), subscription);
                    added = true;
                }

                if (key.Kind is null or WorkKeyKind.Identifier)
                {
                    Add(subscriptionsByIdentifier, new WorkIdentifier(key.Type, key.Value), subscription);
                    added = true;
                }
            }

            return added;
        }

        private static void Add<TKey>(
            Dictionary<TKey, List<WorkEventSubscription>> subscriptionsByKey,
            TKey key,
            WorkEventSubscription subscription)
            where TKey : notnull
        {
            if (!subscriptionsByKey.TryGetValue(key, out var subscriptions))
            {
                subscriptions = [];
                subscriptionsByKey[key] = subscriptions;
            }

            if (subscriptions.Contains(subscription))
            {
                return;
            }

            subscriptions.Add(subscription);
        }

        private static void AddEach(
            Dictionary<string, List<WorkEventSubscription>> subscriptionsByKey,
            IReadOnlySet<string> keys,
            WorkEventSubscription subscription)
        {
            foreach (var key in keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                {
                    continue;
                }

                Add(subscriptionsByKey, key, subscription);
            }
        }
    }

    private sealed class EventLog
    {
        private readonly Lock sync = new();
        private readonly List<EventLogEntry> entries = [];
        private TaskCompletionSource advanced = CreateSignal();
        private long tailSequence;
        private bool isCompleted;

        public long TailSequence
        {
            get
            {
                lock (this.sync)
                {
                    return this.tailSequence;
                }
            }
        }

        public void Append(WorkEvent workEvent, int retainCapacity)
        {
            TaskCompletionSource signal;
            lock (this.sync)
            {
                if (this.isCompleted)
                {
                    return;
                }

                var sequence = ++this.tailSequence;
                this.entries.Add(new EventLogEntry(sequence, workEvent));
                this.TrimLocked(retainCapacity);
                signal = this.advanced;
                this.advanced = CreateSignal();
            }

            signal.TrySetResult();
        }

        public void Trim(int retainCapacity)
        {
            lock (this.sync)
            {
                this.TrimLocked(retainCapacity);
            }
        }

        public void Complete()
        {
            TaskCompletionSource signal;
            lock (this.sync)
            {
                if (this.isCompleted)
                {
                    return;
                }

                this.isCompleted = true;
                signal = this.advanced;
                this.advanced = CreateSignal();
            }

            signal.TrySetResult();
        }

        public void WakeReaders()
        {
            TaskCompletionSource signal;
            lock (this.sync)
            {
                signal = this.advanced;
                this.advanced = CreateSignal();
            }

            signal.TrySetResult();
        }

        public bool TryRead(long sequence, [NotNullWhen(true)] out WorkEvent? workEvent)
        {
            lock (this.sync)
            {
                if (this.entries.Count == 0)
                {
                    workEvent = null;
                    return false;
                }

                var firstSequence = this.entries[0].Sequence;
                var offset = sequence - firstSequence;
                if (offset < 0 || offset >= this.entries.Count)
                {
                    workEvent = null;
                    return false;
                }

                workEvent = this.entries[(int)offset].Event;
                return true;
            }
        }

        public EventLogPosition Position()
        {
            lock (this.sync)
            {
                var firstSequence = this.entries.Count == 0
                    ? this.tailSequence + 1
                    : this.entries[0].Sequence;
                return new EventLogPosition(firstSequence, this.tailSequence, this.isCompleted);
            }
        }

        public async Task WaitForAdvance(long observedTailSequence, CancellationToken cancellationToken)
        {
            Task wait;
            lock (this.sync)
            {
                if (this.isCompleted || this.tailSequence > observedTailSequence)
                {
                    return;
                }

                wait = this.advanced.Task;
            }

            await wait.WaitAsync(cancellationToken);
        }

        public int CountMatching(long firstSequence, long lastSequence, WorkEventFilter? filter, int maximumCount)
        {
            if (maximumCount <= 0 || firstSequence > lastSequence)
            {
                return 0;
            }

            lock (this.sync)
            {
                if (this.entries.Count == 0)
                {
                    return 0;
                }

                var count = 0;
                foreach (var entry in this.entries)
                {
                    if (entry.Sequence < firstSequence)
                    {
                        continue;
                    }

                    if (entry.Sequence > lastSequence)
                    {
                        break;
                    }

                    if (filter?.Matches(entry.Event) == false)
                    {
                        continue;
                    }

                    count++;
                    if (count >= maximumCount)
                    {
                        return count;
                    }
                }

                return count;
            }
        }

        private void TrimLocked(int retainCapacity)
        {
            var removeCount = this.entries.Count - Math.Max(0, retainCapacity);
            if (removeCount > 0)
            {
                this.entries.RemoveRange(0, removeCount);
            }
        }

        private static TaskCompletionSource CreateSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly record struct EventLogEntry(long Sequence, WorkEvent Event);
    }

    private readonly record struct EventLogPosition(
        long FirstSequence,
        long TailSequence,
        bool IsCompleted);

    private sealed class WorkEventSubscription(
        WorkEventStream owner,
        WorkEventFilter? filter,
        WorkEventSubscriptionOptions options,
        WorkEventSubscriptionDeliveryKind deliveryKind,
        long cursorSequence) : IWorkEventSubscription, IWorkEventSubscriptionDiagnostics
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
        private readonly long cursorStartSequence = cursorSequence;
        private long cursorSequence = cursorSequence;
        private int isDisposed;
        private int queuedCount;
        private int peakQueuedCount;
        private long acceptedEventCount;
        private long deliveredEventCount;
        private long droppedEventCount;

        internal WorkEventSubscriptionDeliveryKind DeliveryKind { get; } = deliveryKind;

        internal WorkEventStream Owner { get; } = owner;

        internal int Capacity => this.capacity;

        internal bool HasFilter => filter is not null;

        internal WorkerId? WorkerIdFilter => filter?.WorkerId;

        internal WorkSubjectId? SubjectIdFilter => filter?.SubjectId;

        internal WorkConcurrencyKey? ConcurrencyKeyFilter => filter?.ConcurrencyKey;

        internal WorkIdentifier? IdentifierFilter => filter?.Identifier;

        internal IReadOnlySet<WorkEventKeyFilter>? KeyFilters => filter?.Keys;

        internal string? DefinitionNameFilter => filter?.DefinitionName;

        internal IReadOnlySet<string>? DefinitionNamesFilter => filter?.DefinitionNames;

        internal string? EventTypeFilter => filter?.EventType;

        internal IReadOnlySet<string>? EventTypesFilter => filter?.EventTypes;

        public IAsyncEnumerable<WorkEvent> Read(CancellationToken cancellationToken = default)
            => new WorkEventSubscriptionEnumerable(this, cancellationToken);

        public ValueTask DisposeAsync()
        {
            this.DisposeSubscription();
            return ValueTask.CompletedTask;
        }

        public WorkEventSubscriptionDiagnosticsSnapshot GetDiagnosticsSnapshot()
            => this.DeliveryKind == WorkEventSubscriptionDeliveryKind.CursorLog
                ? this.GetCursorDiagnosticsSnapshot()
                : new WorkEventSubscriptionDiagnosticsSnapshot(
                    this.capacity,
                    this.overflowBehavior,
                    Volatile.Read(ref this.queuedCount),
                    Volatile.Read(ref this.peakQueuedCount),
                    Interlocked.Read(ref this.acceptedEventCount),
                    Interlocked.Read(ref this.deliveredEventCount),
                    Interlocked.Read(ref this.droppedEventCount));

        private WorkEventSubscriptionDiagnosticsSnapshot GetCursorDiagnosticsSnapshot()
        {
            var delivered = Interlocked.Read(ref this.deliveredEventCount);
            var dropped = Interlocked.Read(ref this.droppedEventCount);
            var position = this.Owner.eventLog.Position();
            var cursor = Interlocked.Read(ref this.cursorSequence);
            var queued = this.Owner.eventLog.CountMatching(
                cursor + 1,
                position.TailSequence,
                filter,
                this.capacity);
            long accepted;
            if (filter is null)
            {
                accepted = Math.Max(0, position.TailSequence - this.cursorStartSequence);
                dropped = Math.Max(dropped, accepted - delivered - queued);
            }
            else
            {
                accepted = delivered + dropped + queued;
            }

            var peakQueued = Math.Max(Volatile.Read(ref this.peakQueuedCount), queued);
            return new WorkEventSubscriptionDiagnosticsSnapshot(
                this.capacity,
                this.overflowBehavior,
                queued,
                peakQueued,
                accepted,
                delivered,
                Math.Max(0, dropped));
        }

        private void DisposeSubscription()
        {
            if (Interlocked.Exchange(ref this.isDisposed, 1) == 1)
            {
                return;
            }

            this.events.Writer.TryComplete();
            if (this.DeliveryKind == WorkEventSubscriptionDeliveryKind.CursorLog)
            {
                this.Owner.eventLog.WakeReaders();
            }

            this.Owner.Remove(this);
        }

        internal void DisposeFromOwner()
        {
            if (Interlocked.Exchange(ref this.isDisposed, 1) == 0)
            {
                this.events.Writer.TryComplete();
                if (this.DeliveryKind == WorkEventSubscriptionDeliveryKind.CursorLog)
                {
                    this.Owner.eventLog.WakeReaders();
                }
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

        private bool TryReadCursor([NotNullWhen(true)] out WorkEvent? workEvent)
        {
            workEvent = null;
            while (Volatile.Read(ref this.isDisposed) == 0)
            {
                var cursor = Interlocked.Read(ref this.cursorSequence);
                var position = this.Owner.eventLog.Position();
                var nextSequence = cursor + 1;
                if (nextSequence > position.TailSequence)
                {
                    return false;
                }

                if (nextSequence < position.FirstSequence)
                {
                    var dropped = position.FirstSequence - nextSequence;
                    Interlocked.Add(ref this.droppedEventCount, dropped);
                    Interlocked.Exchange(ref this.cursorSequence, position.FirstSequence - 1);
                    continue;
                }

                var firstRetainedByCapacity = Math.Max(
                    nextSequence,
                    position.TailSequence - this.capacity + 1);
                if (firstRetainedByCapacity > nextSequence)
                {
                    var dropped = firstRetainedByCapacity - nextSequence;
                    Interlocked.Add(ref this.droppedEventCount, dropped);
                    Interlocked.Exchange(ref this.cursorSequence, firstRetainedByCapacity - 1);
                    continue;
                }

                if (!this.Owner.eventLog.TryRead(nextSequence, out var candidate))
                {
                    return false;
                }

                Interlocked.Exchange(ref this.cursorSequence, nextSequence);
                if (filter?.Matches(candidate) == false)
                {
                    continue;
                }

                Interlocked.Increment(ref this.deliveredEventCount);
                workEvent = candidate;
                return true;
            }

            return false;
        }

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
                if (subscription.DeliveryKind == WorkEventSubscriptionDeliveryKind.CursorLog)
                {
                    return await this.MoveNextCursor();
                }

                return await this.MoveNextChannel();
            }

            public ValueTask DisposeAsync()
            {
                linkedCancellation?.Dispose();
                subscription.DisposeSubscription();
                return ValueTask.CompletedTask;
            }

            private async ValueTask<bool> MoveNextCursor()
            {
                while (Volatile.Read(ref subscription.isDisposed) == 0)
                {
                    if (subscription.TryReadCursor(out var workEvent))
                    {
                        this.Current = workEvent;
                        return true;
                    }

                    var position = subscription.Owner.eventLog.Position();
                    if (position.IsCompleted)
                    {
                        return false;
                    }

                    if (position.TailSequence > Interlocked.Read(ref subscription.cursorSequence))
                    {
                        continue;
                    }

                    await subscription.Owner.eventLog.WaitForAdvance(position.TailSequence, cancellationToken);
                }

                return false;
            }

            private async ValueTask<bool> MoveNextChannel()
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
        }
    }
}
