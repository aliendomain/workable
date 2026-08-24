using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Workable;

/// <summary>
/// Tracks active raw event subscriptions for the Workable SignalR adapter.
/// </summary>
/// <remarks>
/// Most hosts use this type indirectly through <see cref="WorkableRealtimeHub"/>. The internal snapshot method
/// supports runtime verification without exposing subscription state through a host endpoint.
/// </remarks>
public sealed class WorkableRealtimeEventSubscriptions
{
    private readonly object gate = new();
    private readonly WorkableSignalROptions options;
    private readonly Dictionary<string, EventSubscription> connectionGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EventSubscriptionGroup> groups = new(StringComparer.Ordinal);
    private readonly HashSet<string> streamingGroups = new(StringComparer.Ordinal);
    private TaskCompletionSource changed = CreateChangeSignal();
    private long version;

    /// <summary>
    /// Creates a tracker with the default Workable SignalR subscription limits.
    /// </summary>
    public WorkableRealtimeEventSubscriptions()
        : this(Options.Create(new WorkableSignalROptions()))
    {
    }

    /// <summary>
    /// Creates a tracker with the configured Workable SignalR subscription limits.
    /// </summary>
    /// <param name="options">The realtime adapter options.</param>
    public WorkableRealtimeEventSubscriptions(IOptions<WorkableSignalROptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
    }

    internal long Version => Volatile.Read(ref this.version);

    internal async Task WatchEvents(
        string connectionId,
        IGroupManager groupManager,
        IWorkSystem system,
        IWorkCatalog catalog,
        WorkableRealtimeEventCriteria? criteria,
        WorkAuthorizationSnapshot authorization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        var filter = this.CreateFilter(catalog, criteria);
        await this.WatchEvents(
            connectionId,
            groupManager,
            system,
            filter,
            authorization,
            cancellationToken);
    }

    internal async Task WatchEvents(
        string connectionId,
        IGroupManager groupManager,
        IWorkSystem system,
        WorkEventFilter? filter,
        WorkAuthorizationSnapshot authorization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        var groupName = CreateSystemEventsGroupName(system, filter, authorization.ReadFingerprint);
        await this.WatchGroup(
            connectionId,
            groupManager,
            system.Id,
            groupName,
            filter,
            authorization,
            cancellationToken);
        await this.WaitForStreaming(groupName, cancellationToken);
    }

    internal Task UnwatchEvents(
        string connectionId,
        IGroupManager groupManager,
        IWorkSystem system,
        IWorkCatalog catalog,
        WorkableRealtimeEventCriteria? criteria,
        CancellationToken cancellationToken)
    {
        var filter = this.CreateFilter(catalog, criteria);
        return this.UnwatchGroup(
            connectionId,
            groupManager,
            system.Id,
            filter,
            cancellationToken);
    }

    internal void RemoveConnection(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        lock (this.gate)
        {
            var keys = this.connectionGroups.Keys
                .Where(key => key.StartsWith($"{connectionId}:", StringComparison.Ordinal))
                .ToArray();

            foreach (var key in keys.Where(this.connectionGroups.ContainsKey))
            {
                var subscription = this.connectionGroups[key];
                this.connectionGroups.Remove(key);
                this.ReleaseGroupLocked(subscription.GroupName);
            }
        }
    }

    internal IReadOnlyList<EventSubscription> GetActiveSubscriptions(IWorkSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        lock (this.gate)
        {
            return [.. this.groups.Values
                .Where(group => group.Subscription.SystemId == system.Id)
                .Select(group => group.Subscription)];
        }
    }

    /// <summary>
    /// Gets internal snapshots for the active raw event subscriptions that belong to one Workable system.
    /// </summary>
    /// <param name="system">The system whose realtime event subscriptions should be described.</param>
    /// <returns>The current raw event subscription snapshots for the system.</returns>
    internal IReadOnlyList<WorkableRealtimeEventSubscriptionSnapshot> GetSubscriptionSnapshots(IWorkSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        lock (this.gate)
        {
            return [.. this.connectionGroups.Values
                .Where(subscription => subscription.SystemId == system.Id)
                .Select(subscription => new WorkableRealtimeEventSubscriptionSnapshot(
                    subscription.ConnectionId,
                    subscription.GroupName,
                    subscription.Filter,
                    this.groups[subscription.GroupName].ConnectionCount,
                    this.streamingGroups.Contains(subscription.GroupName)))];
        }
    }

    internal Task WaitForChange(long observedVersion, CancellationToken cancellationToken)
    {
        Task wait;
        lock (this.gate)
        {
            if (this.version != observedVersion)
            {
                return Task.CompletedTask;
            }

            wait = this.changed.Task;
        }

        return wait.WaitAsync(cancellationToken);
    }

    internal void SetStreaming(string groupName, bool isStreaming)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);

        lock (this.gate)
        {
            var changed = isStreaming
                ? this.streamingGroups.Add(groupName)
                : this.streamingGroups.Remove(groupName);
            if (changed)
            {
                this.SignalChangedLocked();
            }
        }
    }

    private async Task WaitForStreaming(string groupName, CancellationToken cancellationToken)
    {
        while (true)
        {
            Task wait;
            lock (this.gate)
            {
                if (this.streamingGroups.Contains(groupName))
                {
                    return;
                }

                if (!this.groups.ContainsKey(groupName))
                {
                    return;
                }

                wait = this.changed.Task;
            }

            await wait.WaitAsync(cancellationToken);
        }
    }

    private async Task WatchGroup(
        string connectionId,
        IGroupManager groupManager,
        WorkSystemId systemId,
        string groupName,
        WorkEventFilter? filter,
        WorkAuthorizationSnapshot authorization,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(groupManager);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);
        ArgumentNullException.ThrowIfNull(authorization);

        var connectionGroupKey = ConnectionGroupKey(connectionId, systemId, filter);
        EventSubscription? oldSubscription = null;
        EventSubscription? subscription = null;
        var addToGroup = false;

        lock (this.gate)
        {
            if (this.connectionGroups.TryGetValue(connectionGroupKey, out oldSubscription) &&
                oldSubscription is not null &&
                oldSubscription.GroupName == groupName)
            {
                return;
            }

            if (oldSubscription is not null)
            {
                this.ReleaseGroupLocked(oldSubscription.GroupName);
            }
            else
            {
                this.EnsureSubscriptionCapacityLocked(connectionId);
            }

            subscription = new EventSubscription(connectionId, systemId, groupName, filter, authorization);
            this.connectionGroups[connectionGroupKey] = subscription;
            if (this.groups.TryGetValue(groupName, out var group))
            {
                group.ConnectionCount++;
            }
            else
            {
                this.groups[groupName] = new EventSubscriptionGroup(subscription, 1);
            }

            addToGroup = true;
        }

        if (oldSubscription is not null)
        {
            await groupManager.RemoveFromGroupAsync(connectionId, oldSubscription.GroupName, cancellationToken);
        }

        if (addToGroup)
        {
            try
            {
                await groupManager.AddToGroupAsync(connectionId, groupName, cancellationToken);
            }
            catch
            {
                lock (this.gate)
                {
                    if (this.connectionGroups.TryGetValue(connectionGroupKey, out var current) &&
                        ReferenceEquals(current, subscription))
                    {
                        this.connectionGroups.Remove(connectionGroupKey);
                        this.ReleaseGroupLocked(subscription.GroupName);
                    }
                }

                throw;
            }

            lock (this.gate)
            {
                this.SignalChangedLocked();
            }
        }
    }

    private async Task UnwatchGroup(
        string connectionId,
        IGroupManager groupManager,
        WorkSystemId systemId,
        WorkEventFilter? filter,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(groupManager);

        var connectionGroupKey = ConnectionGroupKey(connectionId, systemId, filter);
        var removeFromGroup = false;
        var groupName = string.Empty;

        lock (this.gate)
        {
            if (this.connectionGroups.Remove(connectionGroupKey, out var subscription))
            {
                this.ReleaseGroupLocked(subscription.GroupName);
                groupName = subscription.GroupName;
                removeFromGroup = true;
            }
        }

        if (removeFromGroup)
        {
            await groupManager.RemoveFromGroupAsync(connectionId, groupName, cancellationToken);
        }
    }

    private static string ConnectionGroupKey(
        string connectionId,
        WorkSystemId systemId,
        WorkEventFilter? filter)
        => $"{connectionId}:{systemId.Value:N}:{CreateFilterKey(filter)}";

    internal WorkEventFilter? CreateFilter(IWorkCatalog catalog, WorkableRealtimeEventCriteria? criteria)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var eventTypes = this.NormalizeValues(criteria?.EventTypes, "eventTypes");
        var definitionNames = this.NormalizeValues(criteria?.DefinitionNames, "definitionNames");

        if (criteria?.Keys is { Count: > 0 } keysCriteria &&
            keysCriteria.Count > this.options.MaximumEventFilterValuesPerField)
        {
            throw new ArgumentException(
                $"Realtime event filter 'keys' cannot contain more than {this.options.MaximumEventFilterValuesPerField} values.",
                "keys");
        }

        var keys = criteria?.Keys?
            .Select(this.NormalizeKey)
            .Distinct()
            .OrderBy(key => key.Kind?.ToString() ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key.Type, StringComparer.Ordinal)
            .ThenBy(key => key.Value, StringComparer.Ordinal)
            .ToArray();

        return eventTypes is { Length: > 0 } ||
            definitionNames is { Length: > 0 } ||
            keys is { Length: > 0 }
            ? new WorkEventFilter(
                DefinitionNames: definitionNames?.ToHashSet(StringComparer.OrdinalIgnoreCase),
                Keys: keys?.ToHashSet(),
                EventTypes: eventTypes?.ToHashSet(StringComparer.OrdinalIgnoreCase))
            : null;
    }

    private string[]? NormalizeValues(IReadOnlyList<string>? values, string fieldName)
    {
        if (values is null)
        {
            return null;
        }

        if (values.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException($"Realtime event filter '{fieldName}' values cannot be empty.", fieldName);
        }

        if (values.Count > this.options.MaximumEventFilterValuesPerField)
        {
            throw new ArgumentException(
                $"Realtime event filter '{fieldName}' cannot contain more than {this.options.MaximumEventFilterValuesPerField} values.",
                fieldName);
        }

        if (values.Any(value => value.Trim().Length > this.options.MaximumEventFilterValueLength))
        {
            throw new ArgumentException(
                $"Realtime event filter '{fieldName}' values cannot exceed {this.options.MaximumEventFilterValueLength} characters.",
                fieldName);
        }

        return [.. values
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)];
    }

    private static string CreateSystemEventsGroupName(
        IWorkSystem system,
        WorkEventFilter? filter,
        string readFingerprint)
    {
        if (filter is null ||
            (filter.EventTypes is not { Count: > 0 } &&
                filter.DefinitionNames is not { Count: > 0 } &&
                filter.Keys is not { Count: > 0 }))
        {
            return WorkableRealtimeGroups.SystemEvents(system, readFingerprint);
        }

        return WorkableRealtimeGroups.SystemEvents(
            system,
            $"filter:{CreateFilterKey(filter)}:read:{readFingerprint}");
    }

    private WorkEventKeyFilter NormalizeKey(WorkableRealtimeEventKeyCriteria criteria)
    {
        if (criteria is null || string.IsNullOrWhiteSpace(criteria.Type) || string.IsNullOrWhiteSpace(criteria.Value))
        {
            throw new ArgumentException("Realtime event filter keys require non-empty type and value fields.", "keys");
        }

        if (criteria.Kind is { } kind && !Enum.IsDefined(typeof(WorkKeyKind), kind))
        {
            throw new ArgumentException("Realtime event filter key kinds must be a defined WorkKeyKind value.", "keys");
        }

        var type = criteria.Type.Trim();
        var value = criteria.Value.Trim();
        if (type.Length > this.options.MaximumEventFilterValueLength ||
            value.Length > this.options.MaximumEventFilterValueLength)
        {
            throw new ArgumentException(
                $"Realtime event filter key type and value cannot exceed {this.options.MaximumEventFilterValueLength} characters.",
                "keys");
        }
        return new WorkEventKeyFilter(criteria.Kind, type, value);
    }

    private static string CreateFilterKey(WorkEventFilter? filter)
    {
        if (filter is null ||
            filter == new WorkEventFilter())
        {
            return "system";
        }

        var canonical = JsonSerializer.Serialize(new
        {
            WorkerId = filter.WorkerId is { } workerId
                ? workerId.Value.ToString("N")
                : null,
            filter.DefinitionName,
            DefinitionNames = filter.DefinitionNames?
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            Subject = filter.SubjectId is { } subject
                ? new { subject.Type, subject.Value }
                : null,
            Concurrency = filter.ConcurrencyKey is { } concurrency
                ? new { concurrency.Type, concurrency.Value }
                : null,
            Identifier = filter.Identifier is { } identifier
                ? new { identifier.Type, identifier.Value }
                : null,
            Keys = filter.Keys?
                .OrderBy(static key => key.Kind)
                .ThenBy(static key => key.Type, StringComparer.Ordinal)
                .ThenBy(static key => key.Value, StringComparer.Ordinal)
                .Select(static key => new
                {
                    Kind = key.Kind is { } kind ? (int?)kind : null,
                    key.Type,
                    key.Value,
                })
                .ToArray(),
            filter.EventType,
            EventTypes = filter.EventTypes?
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            DefinitionKind = filter.DefinitionKind is { } definitionKind
                ? (int?)definitionKind
                : null,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();
    }

    private void ReleaseGroupLocked(string groupName)
    {
        if (!this.groups.TryGetValue(groupName, out var group))
        {
            return;
        }

        group.ConnectionCount--;
        if (group.ConnectionCount <= 0)
        {
            this.groups.Remove(groupName);
        }

        this.SignalChangedLocked();
    }

    private void EnsureSubscriptionCapacityLocked(string connectionId)
    {
        if (this.connectionGroups.Count >= this.options.MaximumSubscriptionsPerKind ||
            this.connectionGroups.Values.Count(subscription =>
                string.Equals(subscription.ConnectionId, connectionId, StringComparison.Ordinal)) >=
                this.options.MaximumSubscriptionsPerConnectionPerKind)
        {
            throw new HubException("The Workable realtime raw-event subscription limit was reached.");
        }
    }

    private void SignalChangedLocked()
    {
        Interlocked.Increment(ref this.version);
        var completed = this.changed;
        this.changed = CreateChangeSignal();
        completed.TrySetResult();
    }

    private static TaskCompletionSource CreateChangeSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal sealed record EventSubscription(
        string ConnectionId,
        WorkSystemId SystemId,
        string GroupName,
        WorkEventFilter? Filter,
        WorkAuthorizationSnapshot Authorization);

    private sealed class EventSubscriptionGroup(
        EventSubscription subscription,
        int connectionCount)
    {
        public EventSubscription Subscription { get; } = subscription;

        public int ConnectionCount { get; set; } = connectionCount;
    }
}
