using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Workable;
internal sealed class WorkableRealtimeBroadcaster(
    IWorkSystemRegistry registry,
    IHubContext<WorkableRealtimeHub> hub,
    WorkableViewQueryAdapter views,
    WorkableRealtimeEventSubscriptions eventSubscriptions,
    WorkableRealtimeViewSubscriptions viewSubscriptions,
    IOptions<WorkableSignalROptions> options) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = registry.Systems
            .Select(system => this.BroadcastSystem(system, stoppingToken))
            .ToArray();

        return tasks.Length == 0 ? Task.CompletedTask : Task.WhenAll(tasks);
    }

    private async Task BroadcastSystem(IWorkSystem system, CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(
                this.BroadcastEvents(system, cancellationToken),
                this.BroadcastViews(system, cancellationToken),
                this.BroadcastDiagnosticsViews(system, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task BroadcastEvents(
        IWorkSystem system,
        CancellationToken cancellationToken)
    {
        var pumps = new Dictionary<string, EventPump>(StringComparer.Ordinal);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var activeSubscriptions = eventSubscriptions
                    .GetActiveSubscriptions(system)
                    .ToDictionary(subscription => subscription.GroupName, StringComparer.Ordinal);

                foreach (var groupName in pumps.Keys.ToArray())
                {
                    if (!activeSubscriptions.ContainsKey(groupName))
                    {
                        await StopEventPump(pumps[groupName]);
                        pumps.Remove(groupName);
                    }
                }

                foreach (var subscription in activeSubscriptions.Values)
                {
                    if (!pumps.ContainsKey(subscription.GroupName))
                    {
                        pumps[subscription.GroupName] = this.StartEventPump(system, subscription, cancellationToken);
                    }
                }

                var observedVersion = eventSubscriptions.Version;
                await eventSubscriptions.WaitForChange(observedVersion, cancellationToken);
            }
        }
        finally
        {
            foreach (var pump in pumps.Values)
            {
                await StopEventPump(pump);
            }
        }
    }

    private EventPump StartEventPump(
        IWorkSystem system,
        WorkableRealtimeEventSubscriptions.EventSubscription subscription,
        CancellationToken cancellationToken)
    {
        var pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return new EventPump(
            pumpCancellation,
            this.BroadcastEventGroup(system, subscription, pumpCancellation.Token));
    }

    private async Task BroadcastEventGroup(
        IWorkSystem system,
        WorkableRealtimeEventSubscriptions.EventSubscription subscription,
        CancellationToken cancellationToken)
    {
        await using var events = system.Events.Subscribe(
            subscription.Filter,
            new WorkEventSubscriptionOptions(
                options.Value.EventSubscriptionCapacity,
                options.Value.EventOverflowBehavior));
        eventSubscriptions.SetStreaming(subscription.GroupName, isStreaming: true);
        try
        {
            await foreach (var workEvent in events.Read(cancellationToken))
            {
                await hub.Clients
                    .Group(subscription.GroupName)
                    .SendAsync(
                        WorkableRealtimeClientMethods.WorkEvent,
                        WorkableRealtimeEvent.From(workEvent),
                        cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        finally
        {
            eventSubscriptions.SetStreaming(subscription.GroupName, isStreaming: false);
        }
    }

    private static async Task StopEventPump(EventPump pump)
    {
        await pump.Cancellation.CancelAsync();
        try
        {
            await pump.Task;
        }
        catch (OperationCanceledException)
        {
            // Expected when the last SignalR event watcher leaves a group.
        }

        pump.Cancellation.Dispose();
    }

    private async Task BroadcastViews(
        IWorkSystem system,
        CancellationToken cancellationToken)
    {
        var lastPublishedSequencesByGroup = new Dictionary<string, long>(StringComparer.Ordinal);
        using var timer = new PeriodicTimer(options.Value.PublishInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var subscriptions = viewSubscriptions
                .GetActiveSubscriptions(system)
                .Where(subscription => !IsDiagnosticsView(subscription))
                .ToArray();
            var activeGroups = subscriptions
                .Select(subscription => subscription.GroupName)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var groupName in lastPublishedSequencesByGroup.Keys.ToArray())
            {
                if (!activeGroups.Contains(groupName))
                {
                    lastPublishedSequencesByGroup.Remove(groupName);
                }
            }

            if (subscriptions.Length == 0)
            {
                continue;
            }

            var diagnostics = system.Diagnostics.ReadModel;

            foreach (var subscription in subscriptions)
            {
                var lastPublishedSequence = lastPublishedSequencesByGroup.TryGetValue(subscription.GroupName, out var sequence)
                    ? sequence
                    : subscription.InitialReadModelSequence;
                if (lastPublishedSequence == diagnostics.AppliedSequence)
                {
                    lastPublishedSequencesByGroup[subscription.GroupName] = diagnostics.AppliedSequence;
                    continue;
                }

                await this.BroadcastView(system, subscription, cancellationToken);
                lastPublishedSequencesByGroup[subscription.GroupName] = diagnostics.AppliedSequence;
            }
        }
    }

    private async Task BroadcastDiagnosticsViews(
        IWorkSystem system,
        CancellationToken cancellationToken)
    {
        var alertStatesByGroup = new Dictionary<string, DiagnosticsAlertState>(StringComparer.Ordinal);
        using var timer = new PeriodicTimer(NormalizeInterval(
            options.Value.DiagnosticsPublishInterval,
            TimeSpan.FromMilliseconds(250)));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var subscriptions = viewSubscriptions
                .GetActiveSubscriptions(system)
                .Where(IsDiagnosticsView)
                .ToArray();
            var activeGroups = subscriptions
                .Select(subscription => subscription.GroupName)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var groupName in alertStatesByGroup.Keys.ToArray())
            {
                if (!activeGroups.Contains(groupName))
                {
                    alertStatesByGroup.Remove(groupName);
                }
            }

            foreach (var subscription in subscriptions)
            {
                if (IsDiagnosticsAlertChangesSubscription(subscription))
                {
                    var alertState = CreateDiagnosticsAlertState(system, subscription);
                    if (alertStatesByGroup.TryGetValue(subscription.GroupName, out var previous) &&
                        previous == alertState)
                    {
                        continue;
                    }

                    alertStatesByGroup[subscription.GroupName] = alertState;
                    if (previous is null && !alertState.IsAlerting)
                    {
                        continue;
                    }

                    await this.BroadcastDiagnosticsAlertView(subscription, alertState, cancellationToken);
                    continue;
                }

                await this.BroadcastView(system, subscription, cancellationToken);
            }
        }
    }

    private async Task BroadcastView(
        IWorkSystem system,
        WorkableRealtimeViewSubscription subscription,
        CancellationToken cancellationToken)
    {
        var view = await views.View(
            system,
            subscription.ViewName,
            subscription.Criteria,
            cancellationToken: cancellationToken);
        await hub.Clients
            .Group(subscription.GroupName)
            .SendAsync(
                WorkableRealtimeClientMethods.ViewUpdated,
                view,
                cancellationToken);
    }

    private async Task BroadcastDiagnosticsAlertView(
        WorkableRealtimeViewSubscription subscription,
        DiagnosticsAlertState alertState,
        CancellationToken cancellationToken)
    {
        var components = new Dictionary<string, WorkComponentResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in subscription.Criteria.Components ?? [])
        {
            if (string.Equals(component.Type, "queueDiagnostics", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(component.Type, "queueMessages", StringComparison.OrdinalIgnoreCase))
            {
                components[component.Id] = new WorkComponentResult(
                    "ok",
                    new WorkQueueDiagnosticsCompactComponent(
                        alertState.RejectedWorkCount,
                        alertState.HasRejectedWork,
                        alertState.LastRejectedAt,
                        alertState.LastRejectedCode,
                        alertState.LastRejectedMessage),
                    Shape: component.Shape);
            }
            else if (string.Equals(component.Type, "readModelDiagnostics", StringComparison.OrdinalIgnoreCase))
            {
                components[component.Id] = new WorkComponentResult(
                    "ok",
                    new WorkReadModelDiagnosticsCompactComponent(
                        alertState.ReadModelPendingUpdateCount,
                        alertState.IsReadModelBehind,
                        alertState.ReadModelWarningThreshold,
                        alertState.HasProjectorFailure,
                        alertState.ProjectorFailureType,
                        alertState.ProjectorFailureMessage),
                    Shape: component.Shape);
            }
            else if (string.Equals(component.Type, "retentionDiagnostics", StringComparison.OrdinalIgnoreCase))
            {
                components[component.Id] = new WorkComponentResult(
                    "ok",
                    new WorkRetentionDiagnosticsCompactComponent(
                        alertState.TrackedFinalWorkerCount,
                        alertState.ScheduledPurgeCount,
                        alertState.OldestDuePurgeAge,
                        alertState.IsRetentionBehind,
                        alertState.RetentionWarningSeconds,
                        alertState.HasSchedulerFailure,
                        alertState.SchedulerFailureType,
                        alertState.SchedulerFailureMessage),
                    Shape: component.Shape);
            }
        }

        if (components.Count == 0)
        {
            return;
        }

        await hub.Clients
            .Group(subscription.GroupName)
            .SendAsync(
                WorkableRealtimeClientMethods.ViewUpdated,
                new WorkComponentQueryResult(DateTimeOffset.UtcNow, components),
                cancellationToken);
    }

    private static bool IsDiagnosticsView(WorkableRealtimeViewSubscription subscription)
        => string.Equals(subscription.ViewName, "diagnostics", StringComparison.OrdinalIgnoreCase);

    private static bool IsDiagnosticsAlertChangesSubscription(WorkableRealtimeViewSubscription subscription)
        => subscription.Criteria.Components?
            .Any(component =>
                IsAlertDiagnosticsComponent(component) &&
                string.Equals(component.Shape, WorkComponentShapes.Compact, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(GetStringOption(component.Options, "publishMode"), "alertChanges", StringComparison.OrdinalIgnoreCase)) == true;

    private static bool IsAlertDiagnosticsComponent(WorkComponentRequest component)
        => string.Equals(component.Type, "queueDiagnostics", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(component.Type, "queueMessages", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(component.Type, "readModelDiagnostics", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(component.Type, "retentionDiagnostics", StringComparison.OrdinalIgnoreCase);

    private static DiagnosticsAlertState CreateDiagnosticsAlertState(
        IWorkSystem system,
        WorkableRealtimeViewSubscription subscription)
    {
        var queue = system.Diagnostics.Queue;
        var readModel = system.Diagnostics.ReadModel;
        var readModelThreshold = GetReadModelDiagnosticsWarningThreshold(subscription);
        var readModelLagSeverity = readModel.PendingUpdateCount >= readModelThreshold * 10L
            ? DiagnosticsLagSeverity.Critical
            : readModel.PendingUpdateCount >= readModelThreshold
                ? DiagnosticsLagSeverity.Warning
                : DiagnosticsLagSeverity.Normal;
        var retention = system.Diagnostics.Retention;
        var retentionWarningSeconds = GetRetentionDiagnosticsWarningSeconds(subscription);
        var retentionLagSeverity = retention.OldestDuePurgeAge >= TimeSpan.FromSeconds(retentionWarningSeconds * 10L)
            ? DiagnosticsLagSeverity.Critical
            : retention.OldestDuePurgeAge >= TimeSpan.FromSeconds(retentionWarningSeconds)
                ? DiagnosticsLagSeverity.Warning
                : DiagnosticsLagSeverity.Normal;

        return new DiagnosticsAlertState(
            queue.RejectedWorkCount,
            queue.LastRejectedAt,
            queue.LastRejectedCode,
            queue.LastRejectedMessage,
            readModel.PendingUpdateCount,
            readModelThreshold,
            readModelLagSeverity,
            readModel.HasProjectorFailure,
            readModel.ProjectorFailureType,
            readModel.ProjectorFailureMessage,
            retention.TrackedFinalWorkerCount,
            retention.ScheduledPurgeCount,
            retention.OldestDuePurgeAge,
            retentionWarningSeconds,
            retentionLagSeverity,
            retention.HasSchedulerFailure,
            retention.SchedulerFailureType,
            retention.SchedulerFailureMessage);
    }

    private static int GetReadModelDiagnosticsWarningThreshold(WorkableRealtimeViewSubscription subscription)
        => Math.Max(1, subscription.Criteria.Components?
            .Where(component => string.Equals(component.Type, "readModelDiagnostics", StringComparison.OrdinalIgnoreCase))
            .Select(component => GetInt32Option(component.Options, "warningThreshold"))
            .FirstOrDefault(value => value.HasValue) ?? 100);

    private static int GetRetentionDiagnosticsWarningSeconds(WorkableRealtimeViewSubscription subscription)
        => Math.Max(1, subscription.Criteria.Components?
            .Where(component => string.Equals(component.Type, "retentionDiagnostics", StringComparison.OrdinalIgnoreCase))
            .Select(component => GetInt32Option(component.Options, "warningSeconds"))
            .FirstOrDefault(value => value.HasValue) ?? 30);

    private static string? GetStringOption(JsonElement? options, string propertyName)
        => options.HasValue &&
            options.Value.ValueKind == JsonValueKind.Object &&
            options.Value.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.String
                ? property.GetString()
                : null;

    private static int? GetInt32Option(JsonElement? options, string propertyName)
        => options.HasValue &&
            options.Value.ValueKind == JsonValueKind.Object &&
            options.Value.TryGetProperty(propertyName, out var property) &&
            property.TryGetInt32(out var value)
                ? value
                : null;

    private static TimeSpan NormalizeInterval(TimeSpan interval, TimeSpan fallback)
        => interval > TimeSpan.Zero ? interval : fallback;

    private enum DiagnosticsLagSeverity
    {
        Normal,
        Warning,
        Critical,
    }

    private sealed record DiagnosticsAlertState(
        long RejectedWorkCount,
        DateTimeOffset? LastRejectedAt,
        string? LastRejectedCode,
        string? LastRejectedMessage,
        long ReadModelPendingUpdateCount,
        int ReadModelWarningThreshold,
        DiagnosticsLagSeverity ReadModelLagSeverity,
        bool HasProjectorFailure,
        string? ProjectorFailureType,
        string? ProjectorFailureMessage,
        int TrackedFinalWorkerCount,
        int ScheduledPurgeCount,
        TimeSpan OldestDuePurgeAge,
        int RetentionWarningSeconds,
        DiagnosticsLagSeverity RetentionLagSeverity,
        bool HasSchedulerFailure,
        string? SchedulerFailureType,
        string? SchedulerFailureMessage)
    {
        public bool HasRejectedWork => this.RejectedWorkCount > 0;

        public bool IsReadModelBehind => this.ReadModelLagSeverity != DiagnosticsLagSeverity.Normal;

        public bool IsRetentionBehind => this.RetentionLagSeverity != DiagnosticsLagSeverity.Normal;

        public bool IsAlerting =>
            this.HasRejectedWork ||
            this.IsReadModelBehind ||
            this.HasProjectorFailure ||
            this.IsRetentionBehind ||
            this.HasSchedulerFailure;
    }

    private sealed record EventPump(
        CancellationTokenSource Cancellation,
        Task Task);
}
