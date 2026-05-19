using System.Linq;
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
    IHostApplicationLifetime lifetime,
    IOptions<WorkableSignalROptions> options) : BackgroundService, IWorkSystemLifecycleObserver
{
    private IDisposable? stoppingRegistration;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        this.stoppingRegistration = lifetime.ApplicationStopping.Register(this.BroadcastApplicationStopping);
        return base.StartAsync(cancellationToken);
    }

    public override void Dispose()
    {
        this.stoppingRegistration?.Dispose();
        base.Dispose();
    }

    public async Task SystemStopping(
        IWorkSystem system,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
    {
        await this.BroadcastSystemStopping(system, cancellationToken);
    }

    private void BroadcastApplicationStopping()
    {
        try
        {
            this.BroadcastApplicationStoppingAsync()
                .Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
            // Shutdown notifications are best-effort and must not block host shutdown.
        }
    }

    private async Task BroadcastApplicationStoppingAsync()
    {
        foreach (var system in registry.Systems.Where(system => system.State != WorkSystemState.Stopped))
        {
            await this.BroadcastSystemStopping(system, CancellationToken.None, WorkSystemState.Stopping);
        }
    }

    private async Task BroadcastSystemStopping(
        IWorkSystem system,
        CancellationToken cancellationToken,
        WorkSystemState? systemState = null)
    {
        var subscriptions = viewSubscriptions
            .GetActiveSubscriptions(system)
            .Where(IsDiagnosticsView)
            .ToArray();
        if (subscriptions.Length == 0)
        {
            return;
        }

        foreach (var subscription in subscriptions)
        {
            await this.BroadcastDiagnosticsAlertView(
                subscription,
                CreateDiagnosticsAlertState(system, subscription, systemState),
                cancellationToken);
        }
    }

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
                foreach (var groupName in pumps.Keys
                    .Where(groupName => pumps[groupName].Task.IsCompleted)
                    .ToArray())
                {
                    await StopEventPump(pumps[groupName]);
                    pumps.Remove(groupName);
                }

                var activeSubscriptions = eventSubscriptions
                    .GetActiveSubscriptions(system)
                    .ToDictionary(subscription => subscription.GroupName, StringComparer.Ordinal);

                foreach (var groupName in pumps.Keys
                    .Where(groupName => !activeSubscriptions.ContainsKey(groupName))
                    .ToArray())
                {
                    await StopEventPump(pumps[groupName]);
                    pumps.Remove(groupName);
                }

                foreach (var subscription in activeSubscriptions.Values
                    .Where(subscription => !pumps.ContainsKey(subscription.GroupName)))
                {
                    pumps[subscription.GroupName] = this.StartEventPump(system, subscription, cancellationToken);
                }

                var observedVersion = eventSubscriptions.Version;
                await WaitForEventSubscriptionChangeOrPumpCompletion(
                    eventSubscriptions,
                    observedVersion,
                    pumps.Values,
                    cancellationToken);
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
        IAsyncEnumerator<WorkEvent>? reader = null;
        Task<bool>? pendingRead = null;
        try
        {
            reader = events.Read(cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                pendingRead ??= reader.MoveNextAsync().AsTask();
                if (!await pendingRead)
                {
                    break;
                }

                pendingRead = null;
                var batch = await this.CollectEventBatch(reader, pendingRead, reader.Current, cancellationToken);
                await this.SendEventBatch(
                    subscription.GroupName,
                    batch.Events,
                    cancellationToken);
                pendingRead = batch.PendingRead;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (NotSupportedException) when (cancellationToken.IsCancellationRequested)
        {
            // Async iterators can reject disposal while cancellation is unwinding a pending read.
            return;
        }
        finally
        {
            if (pendingRead is not null && cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await pendingRead;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Expected when an event pump is stopped while a read is pending.
                }
                catch (NotSupportedException)
                {
                    // Expected when cancellation races async iterator disposal.
                }
            }

            if (reader is not null && !cancellationToken.IsCancellationRequested)
            {
                await reader.DisposeAsync();
            }

            eventSubscriptions.SetStreaming(subscription.GroupName, isStreaming: false);
        }
    }

    private async Task<EventBatch> CollectEventBatch(
        IAsyncEnumerator<WorkEvent> reader,
        Task<bool>? pendingRead,
        WorkEvent firstEvent,
        CancellationToken cancellationToken)
    {
        var batchWindow = NormalizeEventBatchWindow(options.Value);
        var maxBatchSize = Math.Max(1, options.Value.EventMaxBatchSize);
        if (maxBatchSize == 1 || batchWindow <= TimeSpan.Zero)
        {
            return new EventBatch([firstEvent], pendingRead);
        }

        var events = new List<WorkEvent> { firstEvent };
        var delay = Task.Delay(batchWindow, cancellationToken);
        while (events.Count < maxBatchSize)
        {
            pendingRead ??= reader.MoveNextAsync().AsTask();
            var completed = await Task.WhenAny(pendingRead, delay);
            if (completed != pendingRead)
            {
                break;
            }

            if (!await pendingRead)
            {
                return new EventBatch(events, null);
            }

            pendingRead = null;
            events.Add(reader.Current);
        }

        if (!delay.IsCompleted)
        {
            await delay;
        }

        return new EventBatch(events, pendingRead);
    }

    private async Task SendEventBatch(
        string groupName,
        IReadOnlyList<WorkEvent> events,
        CancellationToken cancellationToken)
    {
        if (events.Count == 1)
        {
            await hub.Clients
                .Group(groupName)
                .SendAsync(
                    WorkableRealtimeClientMethods.WorkEvent,
                    WorkableRealtimeEvent.From(events[0]),
                    cancellationToken);
            return;
        }

        await hub.Clients
            .Group(groupName)
            .SendAsync(
                WorkableRealtimeClientMethods.WorkEvents,
                WorkableRealtimeEventBatch.From(events),
                cancellationToken);
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
        catch (NotSupportedException) when (pump.Cancellation.IsCancellationRequested)
        {
            // Expected when an event reader is disposed while a pending read is canceled.
        }

        pump.Cancellation.Dispose();
    }

    private static async Task WaitForEventSubscriptionChangeOrPumpCompletion(
        WorkableRealtimeEventSubscriptions eventSubscriptions,
        long observedVersion,
        IEnumerable<EventPump> pumps,
        CancellationToken cancellationToken)
    {
        var changeTask = eventSubscriptions.WaitForChange(observedVersion, cancellationToken);
        var pumpTasks = pumps.Select(pump => pump.Task).ToArray();
        if (pumpTasks.Length == 0)
        {
            await changeTask;
            return;
        }

        await Task.WhenAny(changeTask, Task.WhenAny(pumpTasks));
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
            foreach (var groupName in lastPublishedSequencesByGroup.Keys
                .Where(groupName => !activeGroups.Contains(groupName))
                .ToArray())
            {
                lastPublishedSequencesByGroup.Remove(groupName);
            }

            if (subscriptions.Length == 0)
            {
                continue;
            }

            var diagnostics = system.Diagnostics.ReadModel;

            foreach (var subscription in subscriptions)
            {
                var requiresIntervalPublish = views.RequiresIntervalPublish(
                    subscription.ViewName,
                    subscription.Criteria);
                var lastPublishedSequence = lastPublishedSequencesByGroup.TryGetValue(subscription.GroupName, out var sequence)
                    ? sequence
                    : subscription.InitialReadModelSequence;
                if (!requiresIntervalPublish && lastPublishedSequence == diagnostics.AppliedSequence)
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
            else if (string.Equals(component.Type, "systemDiagnostics", StringComparison.OrdinalIgnoreCase))
            {
                components[component.Id] = new WorkComponentResult(
                    "ok",
                    new WorkSystemDiagnosticsCompactComponent(
                        alertState.SystemName,
                        alertState.SystemState,
                        alertState.IsShuttingDown),
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
            string.Equals(component.Type, "systemDiagnostics", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(component.Type, "readModelDiagnostics", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(component.Type, "retentionDiagnostics", StringComparison.OrdinalIgnoreCase);

    private static DiagnosticsAlertState CreateDiagnosticsAlertState(
        IWorkSystem system,
        WorkableRealtimeViewSubscription subscription,
        WorkSystemState? systemState = null)
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
            system.Name,
            systemState ?? system.State,
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

    private static TimeSpan NormalizeEventBatchWindow(WorkableSignalROptions signalROptions)
    {
        var minimum = NormalizeInterval(
            signalROptions.EventMinimumBatchWindow,
            TimeSpan.FromMilliseconds(100));
        var requested = NormalizeInterval(
            signalROptions.EventBatchWindow,
            TimeSpan.FromSeconds(1));
        return requested < minimum ? minimum : requested;
    }

    private enum DiagnosticsLagSeverity
    {
        Normal,
        Warning,
        Critical,
    }

    private sealed record DiagnosticsAlertState(
        string? SystemName,
        WorkSystemState SystemState,
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
        public bool IsShuttingDown => this.SystemState == WorkSystemState.Stopping;

        public bool HasRejectedWork => this.RejectedWorkCount > 0;

        public bool IsReadModelBehind => this.ReadModelLagSeverity != DiagnosticsLagSeverity.Normal;

        public bool IsRetentionBehind => this.RetentionLagSeverity != DiagnosticsLagSeverity.Normal;

        public bool IsAlerting =>
            this.HasRejectedWork ||
            this.IsReadModelBehind ||
            this.HasProjectorFailure ||
            this.IsRetentionBehind ||
            this.HasSchedulerFailure ||
            this.IsShuttingDown;
    }

    private sealed record EventPump(
        CancellationTokenSource Cancellation,
        Task Task);

    private sealed record EventBatch(
        IReadOnlyList<WorkEvent> Events,
        Task<bool>? PendingRead);
}
