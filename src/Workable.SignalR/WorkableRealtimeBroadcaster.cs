using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Workable;
internal sealed class WorkableRealtimeBroadcaster(
    IWorkSystemRegistry registry,
    IHubContext<WorkableRealtimeHub> hub,
    WorkableViewQueryAdapter views,
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
            var publishSignal = new PublishSignal();
            await using var subscription = system.Events.Subscribe(
                options: new WorkEventSubscriptionOptions(
                    options.Value.EventSubscriptionCapacity,
                    options.Value.EventOverflowBehavior));

            await Task.WhenAll(
                this.BroadcastEvents(system, subscription, publishSignal, cancellationToken),
                this.BroadcastViews(system, publishSignal, cancellationToken),
                this.BroadcastDiagnosticsViews(system, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
    }

    private async Task BroadcastEvents(
        IWorkSystem system,
        IWorkEventSubscription subscription,
        PublishSignal publishSignal,
        CancellationToken cancellationToken)
    {
        await foreach (var workEvent in subscription.Read(cancellationToken))
        {
            publishSignal.MarkDirty();
            var realtimeEvent = WorkableRealtimeEvent.From(workEvent);

            await hub.Clients
                .Group(WorkableRealtimeGroups.SystemEvents(system))
                .SendAsync(WorkableRealtimeClientMethods.WorkEvent, realtimeEvent, cancellationToken);

            if (workEvent.WorkerId is { } workerId)
            {
                await hub.Clients
                    .Group(WorkableRealtimeGroups.Worker(system, workerId))
                    .SendAsync(WorkableRealtimeClientMethods.WorkEvent, realtimeEvent, cancellationToken);
            }

            if (workEvent.DefinitionId is { } definitionId)
            {
                await hub.Clients
                    .Group(WorkableRealtimeGroups.Definition(system, definitionId))
                    .SendAsync(WorkableRealtimeClientMethods.WorkEvent, realtimeEvent, cancellationToken);
            }
        }
    }

    private async Task BroadcastViews(
        IWorkSystem system,
        PublishSignal publishSignal,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(options.Value.PublishInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            if (!publishSignal.TryConsumeDirty())
            {
                continue;
            }

            foreach (var subscription in viewSubscriptions.GetActiveSubscriptions(system))
            {
                if (IsDiagnosticsView(subscription))
                {
                    continue;
                }

                await this.BroadcastView(system, subscription, cancellationToken);
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
            if (!string.Equals(component.Type, "readModelDiagnostics", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            components[component.Id] = new WorkComponentResult(
                "ok",
                new WorkReadModelDiagnosticsCompactComponent(
                    alertState.PendingUpdateCount,
                    alertState.IsReadModelBehind,
                    alertState.WarningThreshold,
                    alertState.HasProjectorFailure,
                    alertState.ProjectorFailureType,
                    alertState.ProjectorFailureMessage),
                Shape: component.Shape);
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
                string.Equals(component.Type, "readModelDiagnostics", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(component.Shape, WorkComponentShapes.Compact, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(GetStringOption(component.Options, "publishMode"), "alertChanges", StringComparison.OrdinalIgnoreCase)) == true;

    private static DiagnosticsAlertState CreateDiagnosticsAlertState(
        IWorkSystem system,
        WorkableRealtimeViewSubscription subscription)
    {
        var readModel = system.Diagnostics.ReadModel;
        var threshold = GetReadModelDiagnosticsWarningThreshold(subscription);
        var lagSeverity = readModel.PendingUpdateCount >= threshold * 10L
            ? DiagnosticsLagSeverity.Critical
            : readModel.PendingUpdateCount >= threshold
                ? DiagnosticsLagSeverity.Warning
                : DiagnosticsLagSeverity.Normal;

        return new DiagnosticsAlertState(
            readModel.PendingUpdateCount,
            threshold,
            lagSeverity,
            readModel.HasProjectorFailure,
            readModel.ProjectorFailureType,
            readModel.ProjectorFailureMessage);
    }

    private static int GetReadModelDiagnosticsWarningThreshold(WorkableRealtimeViewSubscription subscription)
        => Math.Max(1, subscription.Criteria.Components?
            .Where(component => string.Equals(component.Type, "readModelDiagnostics", StringComparison.OrdinalIgnoreCase))
            .Select(component => GetInt32Option(component.Options, "warningThreshold"))
            .FirstOrDefault(value => value.HasValue) ?? 100);

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
        long PendingUpdateCount,
        int WarningThreshold,
        DiagnosticsLagSeverity LagSeverity,
        bool HasProjectorFailure,
        string? ProjectorFailureType,
        string? ProjectorFailureMessage)
    {
        public bool IsReadModelBehind => this.LagSeverity != DiagnosticsLagSeverity.Normal;

        public bool IsAlerting => this.LagSeverity != DiagnosticsLagSeverity.Normal || this.HasProjectorFailure;
    }

    private sealed class PublishSignal
    {
        private int isDirty;

        public void MarkDirty()
            => Volatile.Write(ref this.isDirty, 1);

        public bool TryConsumeDirty()
            => Interlocked.Exchange(ref this.isDirty, 0) == 1;
    }
}
