using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;

namespace Workable;

public sealed class WorkableRealtimeWorkerOverviewSubscriptions
{
    private static readonly JsonSerializerOptions KeyJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object gate = new();
    private readonly Dictionary<string, WorkableRealtimeWorkerOverviewSubscription> connectionGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubscriptionGroup> groups = new(StringComparer.Ordinal);
    private readonly HashSet<string> streamingGroups = new(StringComparer.Ordinal);
    private TaskCompletionSource changed = CreateChangeSignal();
    private long version;

    internal long Version => Volatile.Read(ref this.version);

    internal async Task<WorkableRealtimeWorkerOverviewSubscription> Watch(
        string connectionId,
        IGroupManager groupManager,
        IWorkSystem system,
        string subscriptionId,
        WorkerId workerId,
        WorkWorkerOverviewRealtimeCriteria criteria,
        WorkAuthorizationSnapshot authorization,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        ArgumentNullException.ThrowIfNull(groupManager);
        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        ArgumentNullException.ThrowIfNull(criteria);
        ArgumentNullException.ThrowIfNull(authorization);

        var normalizedSubscriptionId = NormalizeSubscriptionId(subscriptionId);
        var subscription = CreateSubscription(connectionId, system, normalizedSubscriptionId, workerId, criteria, authorization);
        var connectionKey = ConnectionKey(connectionId, system.Id, normalizedSubscriptionId);
        WorkableRealtimeWorkerOverviewSubscription? previous = null;

        lock (this.gate)
        {
            if (this.connectionGroups.TryGetValue(connectionKey, out previous) &&
                previous is not null &&
                previous.GroupName == subscription.GroupName)
            {
                return previous;
            }

            if (previous is not null)
            {
                ReleaseGroupLocked(previous.GroupName);
            }

            this.connectionGroups[connectionKey] = subscription;
            if (this.groups.TryGetValue(subscription.GroupName, out var group))
            {
                group.ConnectionCount++;
            }
            else
            {
                this.groups[subscription.GroupName] = new SubscriptionGroup(subscription, 1);
            }
        }

        if (previous is not null)
        {
            await groupManager.RemoveFromGroupAsync(connectionId, previous.GroupName, cancellationToken);
        }

        try
        {
            await groupManager.AddToGroupAsync(connectionId, subscription.GroupName, cancellationToken);
        }
        catch
        {
            lock (this.gate)
            {
                if (this.connectionGroups.Remove(connectionKey, out var removed) &&
                    removed is not null)
                {
                    ReleaseGroupLocked(removed.GroupName);
                }
            }

            throw;
        }

        lock (this.gate)
        {
            SignalChangedLocked();
        }

        await this.WaitForStreaming(subscription.GroupName, cancellationToken);
        return subscription;
    }

    internal async Task Unwatch(
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

        var connectionKey = ConnectionKey(connectionId, system.Id, NormalizeSubscriptionId(subscriptionId));
        WorkableRealtimeWorkerOverviewSubscription? subscription = null;

        lock (this.gate)
        {
            if (this.connectionGroups.Remove(connectionKey, out subscription) &&
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

        WorkableRealtimeWorkerOverviewSubscription[] subscriptions;
        lock (this.gate)
        {
            var keys = this.connectionGroups.Keys
                .Where(key => key.StartsWith($"{connectionId}:", StringComparison.Ordinal))
                .ToArray();
            subscriptions = keys
                .Select(key => this.connectionGroups[key])
                .ToArray();

            foreach (var key in keys)
            {
                if (this.connectionGroups.Remove(key, out var subscription) && subscription is not null)
                {
                    ReleaseGroupLocked(subscription.GroupName);
                }
            }
        }

        foreach (var subscription in subscriptions)
        {
            await groupManager.RemoveFromGroupAsync(connectionId, subscription.GroupName, cancellationToken);
        }
    }

    internal IReadOnlyList<WorkableRealtimeWorkerOverviewSubscription> GetActiveSubscriptions(IWorkSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        lock (this.gate)
        {
            return [.. this.groups.Values
                .Where(group => group.ConnectionCount > 0 && group.Subscription.SystemId == system.Id)
                .Select(group => group.Subscription)];
        }
    }

    internal IReadOnlyList<WorkableRealtimeWorkerOverviewSubscription> GetGroupSubscriptions(string groupName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);

        lock (this.gate)
        {
            return [.. this.connectionGroups.Values
                .Where(subscription => string.Equals(subscription.GroupName, groupName, StringComparison.Ordinal))];
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
                SignalChangedLocked();
            }
        }
    }

    private static WorkableRealtimeWorkerOverviewSubscription CreateSubscription(
        string connectionId,
        IWorkSystem system,
        string subscriptionId,
        WorkerId workerId,
        WorkWorkerOverviewRealtimeCriteria criteria,
        WorkAuthorizationSnapshot authorization)
    {
        var key = CreateGroupKey(system.Id, workerId, criteria, authorization.ReadFingerprint);
        return new WorkableRealtimeWorkerOverviewSubscription(
            connectionId,
            subscriptionId,
            system.Id,
            workerId,
            criteria,
            WorkableRealtimeGroups.Worker(system, workerId, key),
            authorization);
    }

    private static string CreateGroupKey(
        WorkSystemId systemId,
        WorkerId workerId,
        WorkWorkerOverviewRealtimeCriteria criteria,
        string readFingerprint)
    {
        var json = JsonSerializer.Serialize(new
        {
            SystemId = systemId.Value,
            WorkerId = workerId.Value,
            Criteria = criteria,
            ReadFingerprint = readFingerprint,
        }, KeyJsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ConnectionKey(string connectionId, WorkSystemId systemId, string subscriptionId)
        => $"{connectionId}:{systemId.Value:N}:{subscriptionId}";

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

        SignalChangedLocked();
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

    private void SignalChangedLocked()
    {
        Interlocked.Increment(ref this.version);
        var completed = this.changed;
        this.changed = CreateChangeSignal();
        completed.TrySetResult();
    }

    private static TaskCompletionSource CreateChangeSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class SubscriptionGroup(
        WorkableRealtimeWorkerOverviewSubscription subscription,
        int connectionCount)
    {
        public WorkableRealtimeWorkerOverviewSubscription Subscription { get; } = subscription;

        public int ConnectionCount { get; set; } = connectionCount;
    }
}
