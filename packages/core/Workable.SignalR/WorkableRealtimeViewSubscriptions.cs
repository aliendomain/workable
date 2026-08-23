using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Workable;

/// <summary>
/// Tracks active named-view subscriptions for the Workable SignalR adapter.
/// </summary>
/// <remarks>
/// Most hosts use this type indirectly through <see cref="WorkableRealtimeHub"/>. The internal snapshot method
/// supports runtime verification without exposing subscription state through a host endpoint.
/// </remarks>
public sealed class WorkableRealtimeViewSubscriptions
{
    private static readonly JsonSerializerOptions KeyJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object gate = new();
    private readonly WorkableSignalROptions options;
    private readonly Dictionary<string, WorkableRealtimeViewSubscription> connectionViewGroups = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SubscriptionGroup> groups = new(StringComparer.Ordinal);
    private readonly HashSet<WorkSystemId> streamingSystems = [];
    private readonly HashSet<WorkSystemId> systemsThatHaveStreamed = [];
    private readonly HashSet<string> groupsReadyForReconciliation = new(StringComparer.Ordinal);
    private TaskCompletionSource streamingChanged = CreateStreamingSignal();
    private TaskCompletionSource seedChanged = CreateStreamingSignal();
    private TaskCompletionSource reconciliationChanged = CreateStreamingSignal();

    /// <summary>
    /// Creates a tracker with the default Workable SignalR subscription limits.
    /// </summary>
    public WorkableRealtimeViewSubscriptions()
        : this(Options.Create(new WorkableSignalROptions()))
    {
    }

    /// <summary>
    /// Creates a tracker with the configured Workable SignalR subscription limits.
    /// </summary>
    /// <param name="options">The realtime adapter options.</param>
    public WorkableRealtimeViewSubscriptions(IOptions<WorkableSignalROptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options.Value;
    }

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
                BeginSeedLocked(oldSubscription.GroupName);
                return oldSubscription;
            }

            if (oldSubscription is not null)
            {
                ReleaseGroupLocked(oldSubscription.GroupName);
            }
            else
            {
                this.EnsureSubscriptionCapacityLocked(connectionId);
            }

            this.connectionViewGroups[connectionViewKey] = subscription;
            if (this.groups.TryGetValue(subscription.GroupName, out var group))
            {
                group.ConnectionCount++;
                group.PendingSeedCount++;
            }
            else
            {
                this.groups[subscription.GroupName] = new SubscriptionGroup(subscription, 1)
                {
                    PendingSeedCount = 1,
                };
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
                        CompleteSeedLocked(subscription.GroupName);
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
            if (this.connectionViewGroups.Remove(connectionViewKey, out subscription))
            {
                ReleaseGroupLocked(subscription!.GroupName);
            }
        }

        if (subscription is not null)
        {
            await groupManager.RemoveFromGroupAsync(connectionId, subscription.GroupName, cancellationToken);
        }
    }

    internal void RemoveConnection(string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        lock (this.gate)
        {
            var keys = this.connectionViewGroups.Keys
                .Where(key => key.StartsWith($"{connectionId}:", StringComparison.Ordinal))
                .ToArray();

            foreach (var key in keys)
            {
                var subscription = this.connectionViewGroups[key];
                this.connectionViewGroups.Remove(key);
                ReleaseGroupLocked(subscription.GroupName);
            }
        }
    }

    internal IReadOnlyList<WorkableRealtimeViewSubscription> GetActiveSubscriptions(IWorkSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        lock (this.gate)
        {
            return [.. this.groups.Values
                .Where(group => group.Subscription.SystemId == system.Id)
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

    internal bool SetStreaming(IWorkSystem system, bool isStreaming)
    {
        ArgumentNullException.ThrowIfNull(system);

        lock (this.gate)
        {
            var isRestart = isStreaming &&
                this.systemsThatHaveStreamed.Contains(system.Id) &&
                !this.streamingSystems.Contains(system.Id);
            var changed = isStreaming
                ? this.streamingSystems.Add(system.Id)
                : this.streamingSystems.Remove(system.Id);
            if (!changed)
            {
                return false;
            }

            if (isStreaming)
            {
                this.systemsThatHaveStreamed.Add(system.Id);
            }

            var completed = this.streamingChanged;
            this.streamingChanged = CreateStreamingSignal();
            completed.TrySetResult();
            return isRestart;
        }
    }

    internal async Task WaitForStreaming(IWorkSystem system, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(system);

        while (true)
        {
            Task wait;
            lock (this.gate)
            {
                if (this.streamingSystems.Contains(system.Id))
                {
                    return;
                }

                wait = this.streamingChanged.Task;
            }

            await wait.WaitAsync(cancellationToken);
        }
    }

    internal void CompleteSeed(string groupName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);

        lock (this.gate)
        {
            CompleteSeedLocked(groupName);
        }
    }

    internal async Task WaitForSeed(string groupName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);

        while (true)
        {
            Task wait;
            lock (this.gate)
            {
                if (!this.groups.TryGetValue(groupName, out var group) || group.PendingSeedCount == 0)
                {
                    return;
                }

                wait = this.seedChanged.Task;
            }

            await wait.WaitAsync(cancellationToken);
        }
    }

    internal bool DeferBroadcastUntilSeeded(string groupName, bool reconcileAfterSeed = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupName);

        lock (this.gate)
        {
            if (!this.groups.TryGetValue(groupName, out var group))
            {
                return true;
            }

            if (group.PendingSeedCount == 0)
            {
                return false;
            }

            group.ReconcileAfterSeed |= reconcileAfterSeed;
            return true;
        }
    }

    internal async Task<IReadOnlyList<WorkableRealtimeViewSubscription>> WaitForSeedReconciliations(
        IWorkSystem system,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(system);

        while (true)
        {
            Task wait;
            lock (this.gate)
            {
                var subscriptions = this.groupsReadyForReconciliation
                    .Select(groupName => this.groups.TryGetValue(groupName, out var group) ? group : null)
                    .Where(group => group?.Subscription.SystemId == system.Id)
                    .Select(group => group!.Subscription)
                    .ToArray();
                if (subscriptions.Length > 0)
                {
                    foreach (var subscription in subscriptions)
                    {
                        this.groupsReadyForReconciliation.Remove(subscription.GroupName);
                    }

                    return subscriptions;
                }

                wait = this.reconciliationChanged.Task;
            }

            await wait.WaitAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Gets internal snapshots for the active named-view subscriptions that belong to one Workable system.
    /// </summary>
    /// <param name="system">The system whose realtime view subscriptions should be described.</param>
    /// <returns>The current named-view subscription snapshots for the system.</returns>
    internal IReadOnlyList<WorkableRealtimeViewSubscriptionSnapshot> GetSubscriptionSnapshots(IWorkSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        lock (this.gate)
        {
            return [.. this.connectionViewGroups.Values
                .Where(subscription => subscription.SystemId == system.Id)
                .Select(subscription => new WorkableRealtimeViewSubscriptionSnapshot(
                    subscription.ConnectionId,
                    subscription.SubscriptionId,
                    subscription.ViewName,
                    subscription.GroupName,
                    subscription.Criteria,
                    subscription.InitialReadModelSequence,
                    this.groups[subscription.GroupName].ConnectionCount))];
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
        var key = CreateGroupKey(system.Id, viewName, criteria, authorization);
        return new WorkableRealtimeViewSubscription(
            connectionId,
            subscriptionId,
            system.Id,
            viewName,
            criteria,
            WorkableRealtimeGroups.View(system, key),
            GetAppliedSequence(system),
            GetWorkflowSequence(system, viewName),
            authorization);
    }

    private static long GetAppliedSequence(IWorkSystem system)
        => system is IWorkSystemReadModelClock clock
            ? clock.AppliedSequence
            : 0;

    private static long GetWorkflowSequence(IWorkSystem system, string viewName)
        => WorkableRealtimeWorkflowViews.IsWorkflowView(viewName) &&
            system is IWorkSystemWorkflowClock clock
                ? clock.WorkflowSequence
                : 0;

    private static string CreateGroupKey(
        WorkSystemId systemId,
        string viewName,
        WorkViewCriteria criteria,
        WorkAuthorizationSnapshot authorization)
    {
        var json = JsonSerializer.Serialize(new
        {
            SystemId = systemId.Value,
            ViewName = viewName,
            Criteria = criteria,
            authorization.ReadFingerprint,
            authorization.Actor,
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

    private static TaskCompletionSource CreateStreamingSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private void BeginSeedLocked(string groupName)
    {
        if (this.groups.TryGetValue(groupName, out var group))
        {
            group.PendingSeedCount++;
        }
    }

    private void CompleteSeedLocked(string groupName)
    {
        if (!this.groups.TryGetValue(groupName, out var group) || group.PendingSeedCount == 0)
        {
            return;
        }

        group.PendingSeedCount--;
        if (group.PendingSeedCount == 0)
        {
            SignalSeedChangedLocked();
            if (group.ReconcileAfterSeed)
            {
                group.ReconcileAfterSeed = false;
                this.groupsReadyForReconciliation.Add(groupName);
                SignalReconciliationChangedLocked();
            }
        }
    }

    private void SignalSeedChangedLocked()
    {
        var completed = this.seedChanged;
        this.seedChanged = CreateStreamingSignal();
        completed.TrySetResult();
    }

    private void SignalReconciliationChangedLocked()
    {
        var completed = this.reconciliationChanged;
        this.reconciliationChanged = CreateStreamingSignal();
        completed.TrySetResult();
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
            this.groupsReadyForReconciliation.Remove(groupName);
            if (group.PendingSeedCount > 0)
            {
                SignalSeedChangedLocked();
            }
        }
    }

    private void EnsureSubscriptionCapacityLocked(string connectionId)
    {
        if (this.connectionViewGroups.Count >= this.options.MaximumSubscriptionsPerKind ||
            this.connectionViewGroups.Values.Count(subscription =>
                string.Equals(subscription.ConnectionId, connectionId, StringComparison.Ordinal)) >=
                this.options.MaximumSubscriptionsPerConnectionPerKind)
        {
            throw new HubException("The Workable realtime named-view subscription limit was reached.");
        }
    }

    private sealed class SubscriptionGroup(
        WorkableRealtimeViewSubscription subscription,
        int connectionCount)
    {
        public WorkableRealtimeViewSubscription Subscription { get; } = subscription;

        public int ConnectionCount { get; set; } = connectionCount;

        public int PendingSeedCount { get; set; }

        public bool ReconcileAfterSeed { get; set; }
    }
}
