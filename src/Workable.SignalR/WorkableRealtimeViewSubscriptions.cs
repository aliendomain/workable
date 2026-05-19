using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;

namespace Workable;

public sealed class WorkableRealtimeViewSubscriptions
{
    private static readonly JsonSerializerOptions KeyJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object gate = new();
    private readonly Dictionary<string, WorkableRealtimeViewSubscription> connectionViewGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubscriptionGroup> groups = new(StringComparer.Ordinal);

    internal async Task<WorkableRealtimeViewSubscription> WatchView(
        string connectionId,
        IGroupManager groupManager,
        IWorkSystem system,
        string viewName,
        WorkViewCriteria criteria,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(groupManager);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);
        ArgumentNullException.ThrowIfNull(criteria);

        var normalizedViewName = NormalizeViewName(viewName);
        var subscription = CreateSubscription(system, normalizedViewName, criteria);
        var connectionViewKey = ConnectionViewKey(connectionId, system.Id, normalizedViewName);
        WorkableRealtimeViewSubscription? oldSubscription = null;
        var addToGroup = false;

        lock (this.gate)
        {
            if (this.connectionViewGroups.TryGetValue(connectionViewKey, out oldSubscription) &&
                oldSubscription is not null &&
                oldSubscription.GroupName == subscription.GroupName)
            {
                return oldSubscription;
            }

            if (oldSubscription is not null)
            {
                ReleaseGroupLocked(oldSubscription.GroupName);
            }

            this.connectionViewGroups[connectionViewKey] = subscription;
            if (this.groups.TryGetValue(subscription.GroupName, out var group))
            {
                group.ConnectionCount++;
            }
            else
            {
                this.groups[subscription.GroupName] = new SubscriptionGroup(subscription, 1);
            }

            addToGroup = true;
        }

        if (oldSubscription is not null)
        {
            await groupManager.RemoveFromGroupAsync(connectionId, oldSubscription.GroupName, cancellationToken);
        }

        if (addToGroup)
        {
            await groupManager.AddToGroupAsync(connectionId, subscription.GroupName, cancellationToken);
        }

        return subscription;
    }

    internal async Task UnwatchView(
        string connectionId,
        IGroupManager groupManager,
        IWorkSystem system,
        string viewName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(groupManager);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);

        var connectionViewKey = ConnectionViewKey(connectionId, system.Id, NormalizeViewName(viewName));
        WorkableRealtimeViewSubscription? subscription = null;

        lock (this.gate)
        {
            if (this.connectionViewGroups.Remove(connectionViewKey, out subscription) &&
                subscription is not null)
            {
                ReleaseGroupLocked(subscription.GroupName);
            }
        }

        if (subscription is not null)
        {
            await groupManager.RemoveFromGroupAsync(connectionId, subscription.GroupName, cancellationToken);
        }
    }

    internal async Task RemoveConnection(
        string connectionId,
        IGroupManager groupManager,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(groupManager);

        WorkableRealtimeViewSubscription[] subscriptions;
        lock (this.gate)
        {
            var keys = this.connectionViewGroups.Keys
                .Where(key => key.StartsWith($"{connectionId}:", StringComparison.Ordinal))
                .ToArray();
            subscriptions = keys
                .Select(key => this.connectionViewGroups[key])
                .ToArray();

            var removedSubscriptions = keys
                .Select(key => this.connectionViewGroups.Remove(key, out var subscription) ? subscription : null)
                .OfType<WorkableRealtimeViewSubscription>();
            foreach (var subscription in removedSubscriptions)
            {
                ReleaseGroupLocked(subscription.GroupName);
            }
        }

        foreach (var subscription in subscriptions)
        {
            await groupManager.RemoveFromGroupAsync(connectionId, subscription.GroupName, cancellationToken);
        }
    }

    internal IReadOnlyList<WorkableRealtimeViewSubscription> GetActiveSubscriptions(IWorkSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        lock (this.gate)
        {
            return [.. this.groups.Values
                .Where(group => group.ConnectionCount > 0 && group.Subscription.SystemId == system.Id)
                .Select(group => group.Subscription)];
        }
    }

    private static WorkableRealtimeViewSubscription CreateSubscription(
        IWorkSystem system,
        string viewName,
        WorkViewCriteria criteria)
    {
        var key = CreateGroupKey(system.Id, viewName, criteria);
        return new WorkableRealtimeViewSubscription(
            system.Id,
            viewName,
            criteria,
            WorkableRealtimeGroups.View(system, key),
            system.Diagnostics.ReadModel.AppliedSequence);
    }

    private static string CreateGroupKey(
        WorkSystemId systemId,
        string viewName,
        WorkViewCriteria criteria)
    {
        var json = JsonSerializer.Serialize(new
        {
            SystemId = systemId.Value,
            ViewName = viewName,
            Criteria = criteria,
        }, KeyJsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ConnectionViewKey(
        string connectionId,
        WorkSystemId systemId,
        string viewName)
        => $"{connectionId}:{systemId.Value:N}:{viewName}";

    private static string NormalizeViewName(string viewName)
        => viewName.Trim().ToLowerInvariant();

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
    }

    private sealed class SubscriptionGroup(
        WorkableRealtimeViewSubscription subscription,
        int connectionCount)
    {
        public WorkableRealtimeViewSubscription Subscription { get; } = subscription;

        public int ConnectionCount { get; set; } = connectionCount;
    }
}
