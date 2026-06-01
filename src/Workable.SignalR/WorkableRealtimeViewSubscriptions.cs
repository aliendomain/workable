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
        string subscriptionId,
        string viewName,
        WorkViewCriteria criteria,
        WorkAuthorizationSnapshot authorization,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(groupManager);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(viewName);
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(authorization);

        var normalizedViewName = NormalizeViewName(viewName);
        var normalizedSubscriptionId = NormalizeSubscriptionId(subscriptionId);
        var subscription = CreateSubscription(connectionId, system, normalizedSubscriptionId, normalizedViewName, criteria, authorization);
        var connectionViewKey = ConnectionViewKey(connectionId, system.Id, normalizedSubscriptionId);
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
            try
            {
                await groupManager.AddToGroupAsync(connectionId, subscription.GroupName, cancellationToken);
            }
            catch
            {
                lock (this.gate)
                {
                    if (this.connectionViewGroups.TryGetValue(connectionViewKey, out var current) &&
                        ReferenceEquals(current, subscription))
                    {
                        this.connectionViewGroups.Remove(connectionViewKey);
                        ReleaseGroupLocked(subscription.GroupName);
                    }
                }

                throw;
            }
        }

        return subscription;
    }

    internal async Task UnwatchView(
        string connectionId,
        IGroupManager groupManager,
        IWorkSystem system,
        string subscriptionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(groupManager);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);

        var connectionViewKey = ConnectionViewKey(connectionId, system.Id, NormalizeSubscriptionId(subscriptionId));
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

    internal IReadOnlyList<WorkableRealtimeViewSubscription> GetGroupSubscriptions(string groupName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);

        lock (this.gate)
        {
            return [.. this.connectionViewGroups.Values
                .Where(subscription => string.Equals(subscription.GroupName, groupName, StringComparison.Ordinal))];
        }
    }

    public IReadOnlyList<WorkableRealtimeDebugViewSubscriptionSnapshot> GetDebugSubscriptions(IWorkSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        lock (this.gate)
        {
            return [.. this.connectionViewGroups.Values
                .Where(subscription => subscription.SystemId == system.Id)
                .Select(subscription => new WorkableRealtimeDebugViewSubscriptionSnapshot(
                    subscription.ConnectionId,
                    subscription.SubscriptionId,
                    subscription.ViewName,
                    subscription.GroupName,
                    subscription.Criteria,
                    subscription.InitialReadModelSequence,
                    this.groups.TryGetValue(subscription.GroupName, out var group)
                        ? group.ConnectionCount
                        : 0))];
        }
    }

    private static WorkableRealtimeViewSubscription CreateSubscription(
        string connectionId,
        IWorkSystem system,
        string subscriptionId,
        string viewName,
        WorkViewCriteria criteria,
        WorkAuthorizationSnapshot authorization)
    {
        var key = CreateGroupKey(system.Id, viewName, criteria, authorization.ReadFingerprint);
        return new WorkableRealtimeViewSubscription(
            connectionId,
            subscriptionId,
            system.Id,
            viewName,
            criteria,
            WorkableRealtimeGroups.View(system, key),
            GetAppliedSequence(system),
            authorization);
    }

    private static long GetAppliedSequence(IWorkSystem system)
        => system is IWorkSystemReadModelClock clock
            ? clock.AppliedSequence
            : 0;

    private static string CreateGroupKey(
        WorkSystemId systemId,
        string viewName,
        WorkViewCriteria criteria,
        string readFingerprint)
    {
        var json = JsonSerializer.Serialize(new
        {
            SystemId = systemId.Value,
            ViewName = viewName,
            Criteria = criteria,
            ReadFingerprint = readFingerprint,
        }, KeyJsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ConnectionViewKey(
        string connectionId,
        WorkSystemId systemId,
        string subscriptionId)
        => $"{connectionId}:{systemId.Value:N}:{subscriptionId}";

    private static string NormalizeViewName(string viewName)
        => viewName.Trim().ToLowerInvariant();

    private static string NormalizeSubscriptionId(string subscriptionId)
        => subscriptionId.Trim();

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
