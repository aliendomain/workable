using System.Linq;
using Microsoft.AspNetCore.SignalR;

namespace Workable;

public sealed class WorkableRealtimeEventSubscriptions
{
    private readonly object gate = new();
    private readonly Dictionary<string, EventSubscription> connectionGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, EventSubscriptionGroup> groups = new(StringComparer.Ordinal);
    private readonly HashSet<string> streamingGroups = new(StringComparer.Ordinal);
    private TaskCompletionSource changed = CreateChangeSignal();
    private long version;

    internal long Version => Volatile.Read(ref this.version);

    internal async Task WatchEvents(
        string connectionId,
        IGroupManager groupManager,
        IWorkSystem system,
        WorkableRealtimeEventCriteria? criteria,
        WorkAuthorizationSnapshot authorization,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        var filter = CreateFilter(criteria);
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
        WorkableRealtimeEventCriteria? criteria,
        CancellationToken cancellationToken)
    {
        var filter = CreateFilter(criteria);
        return this.UnwatchGroup(
            connectionId,
            groupManager,
            system.Id,
            filter,
            cancellationToken);
    }

    internal async Task RemoveConnection(
        string connectionId,
        IGroupManager groupManager,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(groupManager);

        EventSubscription[] subscriptions;
        lock (this.gate)
        {
            var keys = this.connectionGroups.Keys
                .Where(key => key.StartsWith($"{connectionId}:", StringComparison.Ordinal))
                .ToArray();
            subscriptions = keys
                .Select(key => this.connectionGroups[key])
                .ToArray();

            foreach (var key in keys.Where(this.connectionGroups.ContainsKey))
            {
                var subscription = this.connectionGroups[key];
                this.connectionGroups.Remove(key);
                this.ReleaseGroupLocked(subscription.GroupName);
            }
        }

        foreach (var subscription in subscriptions)
        {
            await groupManager.RemoveFromGroupAsync(connectionId, subscription.GroupName, cancellationToken);
        }
    }

    internal IReadOnlyList<EventSubscription> GetActiveSubscriptions(IWorkSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        lock (this.gate)
        {
            return [.. this.groups.Values
                .Where(group =>
                group.ConnectionCount > 0 &&
                group.Subscription.SystemId == system.Id)
                .Select(group => group.Subscription)];
        }
    }

    public IReadOnlyList<WorkableRealtimeDebugEventSubscriptionSnapshot> GetDebugSubscriptions(IWorkSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        lock (this.gate)
        {
            return [.. this.connectionGroups.Values
                .Where(subscription => subscription.SystemId == system.Id)
                .Select(subscription => new WorkableRealtimeDebugEventSubscriptionSnapshot(
                    subscription.ConnectionId,
                    subscription.GroupName,
                    subscription.Filter,
                    this.groups.TryGetValue(subscription.GroupName, out var group)
                        ? group.ConnectionCount
                        : 0,
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

            var subscription = new EventSubscription(connectionId, systemId, groupName, filter, authorization);
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
                    if (this.connectionGroups.Remove(connectionGroupKey, out var subscription))
                    {
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

    private static WorkEventFilter? CreateFilter(WorkableRealtimeEventCriteria? criteria)
    {
        var eventTypes = criteria?.EventTypes?
            .Select(eventType => eventType.Trim())
            .Where(eventType => eventType.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var definitionIds = criteria?.DefinitionIds?
            .Select(definitionId => definitionId.Trim())
            .Where(definitionId => Guid.TryParse(definitionId, out _))
            .Select(definitionId => new WorkDefinitionId(Guid.Parse(definitionId)))
            .Distinct()
            .OrderBy(definitionId => definitionId.Value)
            .ToArray();

        var keys = criteria?.Keys?
            .Select(NormalizeKey)
            .Where(key => key is not null)
            .Select(key => key!)
            .Distinct()
            .OrderBy(key => key.Kind?.ToString() ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(key => key.Type, StringComparer.Ordinal)
            .ThenBy(key => key.Value, StringComparer.Ordinal)
            .ToArray();

        return eventTypes is { Length: > 0 } ||
            definitionIds is { Length: > 0 } ||
            keys is { Length: > 0 }
            ? new WorkEventFilter(
                DefinitionIds: definitionIds?.ToHashSet(),
                Keys: keys?.ToHashSet(),
                EventTypes: eventTypes?.ToHashSet(StringComparer.OrdinalIgnoreCase))
            : null;
    }

    private static string CreateSystemEventsGroupName(
        IWorkSystem system,
        WorkEventFilter? filter,
        string readFingerprint)
    {
        if (filter is null ||
            (filter.EventTypes is not { Count: > 0 } &&
                filter.DefinitionIds is not { Count: > 0 } &&
                filter.Keys is not { Count: > 0 }))
        {
            return WorkableRealtimeGroups.SystemEvents(system, readFingerprint);
        }

        var parts = new List<string>();
        if (filter.EventTypes is { Count: > 0 })
        {
            parts.Add("types:" + string.Join(
                ",",
                filter.EventTypes
                    .Order(StringComparer.OrdinalIgnoreCase)
                    .Select(Uri.EscapeDataString)));
        }

        if (filter.DefinitionIds is { Count: > 0 })
        {
            parts.Add("definitions:" + string.Join(
                ",",
                filter.DefinitionIds
                    .OrderBy(definitionId => definitionId.Value)
                    .Select(definitionId => definitionId.Value.ToString("N"))));
        }

        if (filter.Keys is { Count: > 0 })
        {
            parts.Add("keys:" + string.Join(
                ",",
                filter.Keys
                    .OrderBy(key => key.Kind?.ToString() ?? "", StringComparer.OrdinalIgnoreCase)
                    .ThenBy(key => key.Type, StringComparer.Ordinal)
                    .ThenBy(key => key.Value, StringComparer.Ordinal)
                    .Select(key => Uri.EscapeDataString($"{key.Kind?.ToString() ?? "Any"}:{key.Type}:{key.Value}"))));
        }

        parts.Add($"read:{readFingerprint}");
        return WorkableRealtimeGroups.SystemEvents(system, string.Join("|", parts));
    }

    private static WorkEventKeyFilter? NormalizeKey(WorkableRealtimeEventKeyCriteria criteria)
    {
        var type = criteria.Type.Trim();
        var value = criteria.Value.Trim();
        return type.Length == 0 || value.Length == 0
            ? null
            : new WorkEventKeyFilter(criteria.Kind, type, value);
    }

    private static string CreateFilterKey(WorkEventFilter? filter)
    {
        if (filter is null ||
            filter == new WorkEventFilter())
        {
            return "system";
        }

        var parts = new List<string>();
        if (filter.WorkerId is { } workerId)
        {
            parts.Add($"worker:{workerId.Value:N}");
        }

        if (filter.DefinitionId is { } definitionId)
        {
            parts.Add($"definition:{definitionId.Value:N}");
        }

        if (filter.DefinitionIds is { Count: > 0 })
        {
            parts.Add("definitions:" + string.Join(
                ",",
                filter.DefinitionIds
                    .OrderBy(static definition => definition.Value)
                    .Select(static definition => definition.Value.ToString("N"))));
        }

        if (filter.Keys is { Count: > 0 })
        {
            parts.Add("keys:" + string.Join(
                ",",
                filter.Keys
                    .OrderBy(static key => key.Kind?.ToString() ?? "", StringComparer.OrdinalIgnoreCase)
                    .ThenBy(static key => key.Type, StringComparer.Ordinal)
                    .ThenBy(static key => key.Value, StringComparer.Ordinal)
                    .Select(static key => $"{key.Kind?.ToString() ?? "Any"}:{key.Type}:{key.Value}")));
        }

        if (filter.EventTypes is { Count: > 0 })
        {
            parts.Add("types:" + string.Join(
                ",",
                filter.EventTypes.Order(StringComparer.OrdinalIgnoreCase)));
        }

        return parts.Count == 0
            ? "system"
            : string.Join("|", parts);
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
