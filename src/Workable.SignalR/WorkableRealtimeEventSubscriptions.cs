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

    internal async Task WatchSystem(
        string connectionId,
        IGroupManager groupManager,
        IWorkSystem system,
        CancellationToken cancellationToken)
    {
        var groupName = WorkableRealtimeGroups.SystemEvents(system);
        await this.WatchGroup(
            connectionId,
            groupManager,
            system.Id,
            groupName,
            filter: null,
            cancellationToken);
        await this.WaitForStreaming(groupName, cancellationToken);
    }

    internal Task UnwatchSystem(
        string connectionId,
        IGroupManager groupManager,
        IWorkSystem system,
        CancellationToken cancellationToken)
        => this.UnwatchGroup(
            connectionId,
            groupManager,
            WorkableRealtimeGroups.SystemEvents(system),
            cancellationToken);

    internal async Task WatchEvents(
        string connectionId,
        IGroupManager groupManager,
        IWorkSystem system,
        WorkableRealtimeEventCriteria? criteria,
        CancellationToken cancellationToken)
    {
        var filter = CreateFilter(criteria);
        var groupName = CreateSystemEventsGroupName(system, filter);
        await this.WatchGroup(
            connectionId,
            groupManager,
            system.Id,
            groupName,
            filter,
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
            CreateSystemEventsGroupName(system, filter),
            cancellationToken);
    }

    internal async Task WatchWorker(
        string connectionId,
        IGroupManager groupManager,
        IWorkSystem system,
        WorkerId workerId,
        CancellationToken cancellationToken)
    {
        var groupName = WorkableRealtimeGroups.Worker(system, workerId);
        await this.WatchGroup(
            connectionId,
            groupManager,
            system.Id,
            groupName,
            new WorkEventFilter(WorkerId: workerId),
            cancellationToken);
        await this.WaitForStreaming(groupName, cancellationToken);
    }

    internal Task UnwatchWorker(
        string connectionId,
        IGroupManager groupManager,
        IWorkSystem system,
        WorkerId workerId,
        CancellationToken cancellationToken)
        => this.UnwatchGroup(
            connectionId,
            groupManager,
            WorkableRealtimeGroups.Worker(system, workerId),
            cancellationToken);

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
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(groupManager);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);

        var connectionGroupKey = ConnectionGroupKey(connectionId, groupName);
        var addToGroup = false;

        lock (this.gate)
        {
            if (this.connectionGroups.ContainsKey(connectionGroupKey))
            {
                return;
            }

            var subscription = new EventSubscription(systemId, groupName, filter);
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
        string groupName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(groupManager);
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);

        var connectionGroupKey = ConnectionGroupKey(connectionId, groupName);
        var removeFromGroup = false;

        lock (this.gate)
        {
            if (this.connectionGroups.Remove(connectionGroupKey, out var subscription))
            {
                this.ReleaseGroupLocked(subscription.GroupName);
                removeFromGroup = true;
            }
        }

        if (removeFromGroup)
        {
            await groupManager.RemoveFromGroupAsync(connectionId, groupName, cancellationToken);
        }
    }

    private static string ConnectionGroupKey(string connectionId, string groupName)
        => $"{connectionId}:{groupName}";

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

    private static string CreateSystemEventsGroupName(IWorkSystem system, WorkEventFilter? filter)
    {
        if (filter is null ||
            (filter.EventTypes is not { Count: > 0 } &&
                filter.DefinitionIds is not { Count: > 0 } &&
                filter.Keys is not { Count: > 0 }))
        {
            return WorkableRealtimeGroups.SystemEvents(system);
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
        WorkSystemId SystemId,
        string GroupName,
        WorkEventFilter? Filter);

    private sealed class EventSubscriptionGroup(
        EventSubscription subscription,
        int connectionCount)
    {
        public EventSubscription Subscription { get; } = subscription;

        public int ConnectionCount { get; set; } = connectionCount;
    }
}
