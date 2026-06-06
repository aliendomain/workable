using System.Linq;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Workable;
internal sealed class WorkableRealtimeBroadcaster(
    IWorkSystemRegistry registry,
    IHubContext<WorkableRealtimeHub> hub,
    ILogger<WorkableRealtimeBroadcaster> logger,
    WorkableViewQueryAdapter views,
    WorkableRealtimeEventSubscriptions eventSubscriptions,
    WorkableRealtimeViewSubscriptions viewSubscriptions,
    WorkableRealtimeWorkerOverviewSubscriptions workerOverviewSubscriptions,
    IHostApplicationLifetime lifetime,
    IOptions<WorkableSignalROptions> options,
    IWorkableRealtimeTimerFactory timerFactory,
    WorkableRealtimeBroadcastLaneRunner laneRunner) : BackgroundService, IWorkSystemLifecycleObserver
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
            var session = CreateAuthorizedSession(
                system,
                subscription.Authorization);
            await this.BroadcastDiagnosticsAlertView(
                subscription,
                CreateDiagnosticsAlertState(session, subscription, systemState),
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
                laneRunner.Run(system, "events", this.BroadcastEvents, cancellationToken),
                laneRunner.Run(system, "worker-overview", this.BroadcastWorkerOverviews, cancellationToken),
                laneRunner.Run(system, "views", this.BroadcastViews, cancellationToken),
                laneRunner.Run(system, "diagnostics", this.BroadcastDiagnosticsViews, cancellationToken));
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
                    await StopEventPump(pumps[groupName], logger, $"event group '{groupName}'");
                    pumps.Remove(groupName);
                }

                var activeSubscriptions = eventSubscriptions
                    .GetActiveSubscriptions(system)
                    .ToDictionary(subscription => subscription.GroupName, StringComparer.Ordinal);

                foreach (var groupName in pumps.Keys
                    .Where(groupName => !activeSubscriptions.ContainsKey(groupName))
                    .ToArray())
                {
                    await StopEventPump(pumps[groupName], logger, $"event group '{groupName}'");
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
                await StopEventPump(pump, logger, "event group");
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
        var session = CreateAuthorizedSession(
            system,
            subscription.Authorization);
        await using var events = session.Events.Subscribe(
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
                var batch = await this.CollectEventBatch(
                    subscription,
                    reader,
                    pendingRead,
                    reader.Current,
                    cancellationToken);
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
        WorkableRealtimeEventSubscriptions.EventSubscription subscription,
        IAsyncEnumerator<WorkEvent> reader,
        Task<bool>? pendingRead,
        WorkEvent firstEvent,
        CancellationToken cancellationToken)
    {
        var batchWindow = ResolveTimeWindow(subscription, options.Value);
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

    private async Task<WorkerOverviewEventBatch> CollectWorkerOverviewEventBatch(
        IAsyncEnumerator<WorkEvent> reader,
        Task<bool>? pendingRead,
        WorkEvent firstEvent,
        CancellationToken cancellationToken)
    {
        var batchWindow = NormalizeLiveTimeWindow(options.Value);
        var maxBatchSize = Math.Max(1, options.Value.EventMaxBatchSize);
        if (maxBatchSize == 1 || batchWindow <= TimeSpan.Zero)
        {
            return new WorkerOverviewEventBatch([firstEvent], pendingRead);
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
                return new WorkerOverviewEventBatch(events, null);
            }

            pendingRead = null;
            events.Add(reader.Current);
        }

        if (!delay.IsCompleted)
        {
            await delay;
        }

        return new WorkerOverviewEventBatch(events, pendingRead);
    }

    private static async Task StopEventPump(
        EventPump pump,
        ILogger logger,
        string scope)
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
        catch (Exception exception)
        {
            logger.LogError(exception, "A realtime {Scope} pump faulted and was stopped.", scope);
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

    private static async Task WaitForWorkerOverviewSubscriptionChangeOrPumpCompletion(
        WorkableRealtimeWorkerOverviewSubscriptions subscriptions,
        long observedVersion,
        IEnumerable<EventPump> pumps,
        CancellationToken cancellationToken)
    {
        var changeTask = subscriptions.WaitForChange(observedVersion, cancellationToken);
        var pumpTasks = pumps.Select(pump => pump.Task).ToArray();
        if (pumpTasks.Length == 0)
        {
            await changeTask;
            return;
        }

        await Task.WhenAny(changeTask, Task.WhenAny(pumpTasks));
    }

    private async Task BroadcastWorkerOverviews(
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
                    await StopEventPump(pumps[groupName], logger, $"event group '{groupName}'");
                    pumps.Remove(groupName);
                }

                var activeSubscriptions = workerOverviewSubscriptions
                    .GetActiveSubscriptions(system)
                    .ToDictionary(subscription => subscription.GroupName, StringComparer.Ordinal);

                foreach (var groupName in pumps.Keys
                    .Where(groupName => !activeSubscriptions.ContainsKey(groupName))
                    .ToArray())
                {
                    await StopEventPump(pumps[groupName], logger, $"event group '{groupName}'");
                    pumps.Remove(groupName);
                }

                foreach (var subscription in activeSubscriptions.Values
                    .Where(subscription => !pumps.ContainsKey(subscription.GroupName)))
                {
                    pumps[subscription.GroupName] = this.StartWorkerOverviewPump(system, subscription, cancellationToken);
                }

                var observedVersion = workerOverviewSubscriptions.Version;
                await WaitForWorkerOverviewSubscriptionChangeOrPumpCompletion(
                    workerOverviewSubscriptions,
                    observedVersion,
                    pumps.Values,
                    cancellationToken);
            }
        }
        finally
        {
            foreach (var pump in pumps.Values)
            {
                await StopEventPump(pump, logger, "event group");
            }
        }
    }

    private EventPump StartWorkerOverviewPump(
        IWorkSystem system,
        WorkableRealtimeWorkerOverviewSubscription subscription,
        CancellationToken cancellationToken)
    {
        var pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return new EventPump(
            pumpCancellation,
            this.BroadcastWorkerOverviewGroup(system, subscription, pumpCancellation.Token));
    }

    private async Task BroadcastWorkerOverviewGroup(
        IWorkSystem system,
        WorkableRealtimeWorkerOverviewSubscription subscription,
        CancellationToken cancellationToken)
    {
        var session = CreateAuthorizedSession(
            system,
            subscription.Authorization);
        await using var events = session.Events.Subscribe(
            new WorkEventFilter(WorkerId: subscription.WorkerId),
            new WorkEventSubscriptionOptions(
                options.Value.EventSubscriptionCapacity,
                options.Value.WorkerOverviewEventOverflowBehavior));
        var eventStreamDiagnostics = events as IWorkEventSubscriptionDiagnostics;
        workerOverviewSubscriptions.SetEventStreamDiagnosticsProvider(
            subscription.GroupName,
            eventStreamDiagnostics is null
                ? null
                : eventStreamDiagnostics.GetDiagnosticsSnapshot);
        IAsyncEnumerator<WorkEvent>? reader = null;
        Task<bool>? pendingRead = null;
        string? stopReason = null;
        try
        {
            reader = events.Read(cancellationToken).GetAsyncEnumerator(cancellationToken);
            var current = await views.WorkerOverviewRealtimeState(
                session,
                subscription.WorkerId,
                subscription.Criteria,
                cancellationToken);
            if (current is null)
            {
                workerOverviewSubscriptions.SetStreaming(subscription.GroupName, isStreaming: true);
            }
            else
            {
                var startupBufferedEventLimit = Math.Max(1, options.Value.EventMaxBatchSize);
                var startupBufferedEventCount = 0;
                while (true)
                {
                    var bufferedRead = reader.MoveNextAsync();
                    if (!bufferedRead.IsCompletedSuccessfully)
                    {
                        pendingRead = bufferedRead.AsTask();
                        break;
                    }

                    if (!bufferedRead.Result)
                    {
                        return;
                    }

                    var bufferedUpdate = WorkableRealtimeWorkerOverviewUpdateFactory.Create(
                        reader.Current,
                        current,
                        subscription.Criteria);
                    if (bufferedUpdate is not null)
                    {
                        current = WorkableRealtimeWorkerOverviewUpdateFactory.Apply(
                            current,
                            bufferedUpdate,
                            subscription.Criteria);
                    }

                    startupBufferedEventCount++;
                    if (startupBufferedEventCount >= startupBufferedEventLimit)
                    {
                        // Under a flood, MoveNextAsync can keep completing synchronously and prevent
                        // the worker overview subscription from ever declaring itself live. Cap the
                        // startup prebuffer and let the normal batched loop catch up from there.
                        pendingRead = Task.FromResult(true);
                        break;
                    }
                }

                await this.SendWorkerOverviewUpdateToGroup(
                    subscription.GroupName,
                    WorkableRealtimeWorkerOverviewUpdateFactory.CreateInitial(current),
                    cancellationToken);
                workerOverviewSubscriptions.ReportActivity(subscription.GroupName, DateTimeOffset.UtcNow);
                workerOverviewSubscriptions.SetStreaming(subscription.GroupName, isStreaming: true);
            }
            while (!cancellationToken.IsCancellationRequested)
            {
                pendingRead ??= reader.MoveNextAsync().AsTask();
                if (!await pendingRead)
                {
                    break;
                }

                var batch = await this.CollectWorkerOverviewEventBatch(
                    reader,
                    null,
                    reader.Current,
                    cancellationToken);
                pendingRead = batch.PendingRead;
                if (current is null)
                {
                    current = await views.WorkerOverviewRealtimeState(
                        session,
                        subscription.WorkerId,
                        subscription.Criteria,
                        cancellationToken);
                    if (current is null)
                    {
                        continue;
                    }

                    await this.SendWorkerOverviewUpdateToGroup(
                        subscription.GroupName,
                        WorkableRealtimeWorkerOverviewUpdateFactory.CreateInitial(current),
                        cancellationToken);
                    workerOverviewSubscriptions.ReportActivity(subscription.GroupName, DateTimeOffset.UtcNow);
                    continue;
                }

                var updates = new List<WorkWorkerOverviewRealtimeUpdate>(batch.Events.Count);
                var batchStartState = current;
                var batchState = current;
                foreach (var workEvent in batch.Events)
                {
                    var update = WorkableRealtimeWorkerOverviewUpdateFactory.Create(
                        workEvent,
                        batchState,
                        subscription.Criteria);
                    if (update is null)
                    {
                        continue;
                    }

                    updates.Add(update);
                    batchState = WorkableRealtimeWorkerOverviewUpdateFactory.Apply(
                        batchState,
                        update,
                        subscription.Criteria);
                }

                var batchedUpdate = WorkableRealtimeWorkerOverviewUpdateFactory.Coalesce(
                    batchStartState,
                    updates,
                    subscription.Criteria,
                    out current);
                if (batchedUpdate is null)
                {
                    var skippedUpdateDiagnostics = eventStreamDiagnostics?.GetDiagnosticsSnapshot();
                    if (ShouldResyncWorkerOverviewFromLag(skippedUpdateDiagnostics, options.Value))
                    {
                        await this.SendWorkerOverviewUpdateToGroup(
                            subscription.GroupName,
                            CreateWorkerOverviewRefreshInstruction(skippedUpdateDiagnostics),
                            cancellationToken);
                        return;
                    }

                    continue;
                }

                await this.SendWorkerOverviewUpdateToGroup(
                    subscription.GroupName,
                    batchedUpdate,
                    cancellationToken);
                workerOverviewSubscriptions.ReportActivity(subscription.GroupName, batchedUpdate.GeneratedAt);

                var diagnostics = eventStreamDiagnostics?.GetDiagnosticsSnapshot();
                if (diagnostics is not null && ShouldResyncWorkerOverviewFromLag(diagnostics, options.Value))
                {
                    logger.LogWarning(
                        "SignalR worker overview for worker '{WorkerId}' in system '{SystemName}' is falling behind and will request a resync. Queued={QueuedCount}, Dropped={DroppedEventCount}, Capacity={Capacity}.",
                        subscription.WorkerId.Value,
                        system.Name,
                        diagnostics.QueuedCount,
                        diagnostics.DroppedEventCount,
                        diagnostics.Capacity);
                    await this.SendWorkerOverviewUpdateToGroup(
                        subscription.GroupName,
                        CreateWorkerOverviewRefreshInstruction(diagnostics),
                        cancellationToken);
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            stopReason = "Canceled";
            return;
        }
        catch (NotSupportedException) when (cancellationToken.IsCancellationRequested)
        {
            stopReason = "Canceled";
            return;
        }
        catch (Exception exception)
        {
            stopReason = exception.Message;
            workerOverviewSubscriptions.ReportError(subscription.GroupName, stopReason);
            logger.LogError(
                exception,
                "Failed to broadcast SignalR worker overview for worker '{WorkerId}' in system '{SystemName}' and group '{GroupName}'.",
                subscription.WorkerId.Value,
                system.Name,
                subscription.GroupName);
            throw;
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
                    // Expected when the worker overview pump is stopped while a read is pending.
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

            workerOverviewSubscriptions.SetEventStreamDiagnosticsProvider(subscription.GroupName, null);
            if (!cancellationToken.IsCancellationRequested && stopReason is not null)
            {
                workerOverviewSubscriptions.ReportError(subscription.GroupName, stopReason);
            }
            workerOverviewSubscriptions.SetStreaming(subscription.GroupName, isStreaming: false);
        }
    }

    private async Task BroadcastViews(
        IWorkSystem system,
        CancellationToken cancellationToken)
    {
        var lastPublishedSequencesByGroup = new Dictionary<string, long>(StringComparer.Ordinal);
        using var timer = timerFactory.Create(options.Value.PublishInterval);
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

            if (system is not IWorkSystemReadModelClock readModelClock)
            {
                foreach (var subscription in subscriptions)
                {
                    try
                    {
                        await this.BroadcastView(system, subscription, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(
                            exception,
                            "Failed to broadcast SignalR view '{ViewName}' for system '{SystemName}' and group '{GroupName}'.",
                            subscription.ViewName,
                            system.Name,
                            subscription.GroupName);
                    }
                }

                continue;
            }

            var appliedSequence = readModelClock.AppliedSequence;

            foreach (var subscription in subscriptions)
            {
                var requiresIntervalPublish = views.RequiresIntervalPublish(
                    subscription.ViewName,
                    subscription.Criteria);
                var lastPublishedSequence = lastPublishedSequencesByGroup.TryGetValue(subscription.GroupName, out var sequence)
                    ? sequence
                    : subscription.InitialReadModelSequence;
                if (!WorkableRealtimeBroadcastRules.ShouldPublishView(
                    requiresIntervalPublish,
                    lastPublishedSequence,
                    appliedSequence))
                {
                    lastPublishedSequencesByGroup[subscription.GroupName] = appliedSequence;
                    continue;
                }

                try
                {
                    await this.BroadcastView(system, subscription, cancellationToken);
                    lastPublishedSequencesByGroup[subscription.GroupName] = appliedSequence;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Failed to broadcast SignalR view '{ViewName}' for system '{SystemName}' and group '{GroupName}'.",
                        subscription.ViewName,
                        system.Name,
                        subscription.GroupName);
                }
            }
        }
    }

    private async Task BroadcastDiagnosticsViews(
        IWorkSystem system,
        CancellationToken cancellationToken)
    {
        var alertStatesByGroup = new Dictionary<string, WorkableRealtimeDiagnosticsAlertState>(StringComparer.Ordinal);
        using var timer = timerFactory.Create(NormalizeInterval(
            options.Value.DiagnosticsPublishInterval,
            TimeSpan.FromMilliseconds(750)));
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            var subscriptions = viewSubscriptions
                .GetActiveSubscriptions(system)
                .Where(IsDiagnosticsView)
                .ToArray();
            var activeGroups = subscriptions
                .Select(subscription => subscription.GroupName)
                .ToHashSet(StringComparer.Ordinal);
            foreach (var groupName in alertStatesByGroup.Keys
                .Where(groupName => !activeGroups.Contains(groupName))
                .ToArray())
            {
                alertStatesByGroup.Remove(groupName);
            }

            foreach (var subscription in subscriptions)
            {
                if (IsDiagnosticsAlertChangesSubscription(subscription))
                {
                    try
                    {
                        var session = CreateAuthorizedSession(
                            system,
                            subscription.Authorization);
                        var alertState = CreateDiagnosticsAlertState(session, subscription);
                        alertStatesByGroup.TryGetValue(subscription.GroupName, out var previous);
                        if (!WorkableRealtimeBroadcastRules.ShouldPublishDiagnosticsAlertChange(previous, alertState))
                        {
                            alertStatesByGroup[subscription.GroupName] = alertState;
                            continue;
                        }

                        await this.BroadcastDiagnosticsAlertView(subscription, alertState, cancellationToken);
                        alertStatesByGroup[subscription.GroupName] = alertState;
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception)
                    {
                        alertStatesByGroup.Remove(subscription.GroupName);
                        logger.LogError(
                            exception,
                            "Failed to broadcast SignalR diagnostics alert view for system '{SystemName}' and group '{GroupName}'.",
                            system.Name,
                            subscription.GroupName);
                    }
                    continue;
                }

                try
                {
                    await this.BroadcastView(system, subscription, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    logger.LogError(
                        exception,
                        "Failed to broadcast SignalR diagnostics view '{ViewName}' for system '{SystemName}' and group '{GroupName}'.",
                        subscription.ViewName,
                        system.Name,
                        subscription.GroupName);
                }
            }
        }
    }

    private async Task BroadcastView(
        IWorkSystem system,
        WorkableRealtimeViewSubscription subscription,
        CancellationToken cancellationToken)
    {
        var session = CreateAuthorizedSession(
            system,
            subscription.Authorization);
        var view = await views.View(
            session,
            subscription.ViewName,
            subscription.Criteria,
            cancellationToken: cancellationToken);
        await this.SendViewUpdateToGroup(
            subscription.GroupName,
            subscription.ViewName,
            view,
            cancellationToken);
    }

    private async Task BroadcastDiagnosticsAlertView(
        WorkableRealtimeViewSubscription subscription,
        WorkableRealtimeDiagnosticsAlertState alertState,
        CancellationToken cancellationToken)
    {
        var components = new Dictionary<string, WorkComponentResult>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in subscription.Criteria.Components ?? [])
        {
            if (string.Equals(component.Type, "queueDiagnostics", StringComparison.OrdinalIgnoreCase))
            {
                components[component.Id] = new WorkComponentResult(
                    "ok",
                    new WorkQueueDiagnosticsCompactComponent(
                        alertState.RejectedWorkCount,
                        alertState.HasRejectedWork,
                        alertState.LastRejectedAt,
                        alertState.LastRejectedCode,
                        alertState.LastRejectedMessage,
                        alertState.AlertableRejectedWorkCount,
                        alertState.HasAlertableRejectedWork,
                        alertState.LastAlertableRejectedCode,
                        alertState.LastAlertableRejectedMessage),
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
            else if (string.Equals(component.Type, "concurrencyDiagnostics", StringComparison.OrdinalIgnoreCase))
            {
                components[component.Id] = new WorkComponentResult(
                    "ok",
                    new WorkConcurrencyDiagnosticsCompactComponent(
                        alertState.DeferredStartCount,
                        alertState.OldestDeferredStartAge,
                        alertState.LastDrainReleasedCount,
                        alertState.IsConcurrencyBehind,
                        alertState.ConcurrencyWarningSeconds),
                    Shape: component.Shape);
            }
            else if (string.Equals(component.Type, "durabilityDiagnostics", StringComparison.OrdinalIgnoreCase))
            {
                components[component.Id] = new WorkComponentResult(
                    "ok",
                    new WorkDurabilityDiagnosticsCompactComponent(
                        alertState.AcceptedWaiterCount,
                        alertState.OldestAcceptedWaiterAge,
                        alertState.PendingCleanupCount,
                        alertState.OldestPendingCleanupAge,
                        alertState.IsAcceptedWorkerMaterializationBehind,
                        alertState.AcceptedWorkerWarningSeconds,
                        alertState.IsCleanupBehind,
                        alertState.CleanupWarningSeconds,
                        alertState.HasReaderFailure,
                        alertState.ReaderFailureType,
                        alertState.ReaderFailureMessage,
                        alertState.HasLeaseRenewalFailure,
                        alertState.LeaseRenewalFailureType,
                        alertState.LeaseRenewalFailureMessage,
                        alertState.HasCleanupFailure,
                        alertState.CleanupFailureType,
                        alertState.CleanupFailureMessage),
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

        await this.SendViewUpdateToGroup(
            subscription.GroupName,
            subscription.ViewName,
            new WorkComponentQueryResult(DateTimeOffset.UtcNow, components),
            cancellationToken);
    }

    private async Task SendWorkerOverviewUpdateToGroup(
        string groupName,
        WorkWorkerOverviewRealtimeUpdate update,
        CancellationToken cancellationToken)
    {
        var subscriptions = workerOverviewSubscriptions.GetGroupSubscriptions(groupName);
        foreach (var subscription in subscriptions)
        {
            await hub.Clients
                .Client(subscription.ConnectionId)
                .SendAsync(
                    WorkableRealtimeClientMethods.WorkerOverviewUpdated,
                    new WorkableRealtimeViewEnvelope<WorkWorkerOverviewRealtimeUpdate>(
                        subscription.SubscriptionId,
                        "worker-overview",
                        update),
                    cancellationToken);
        }
    }

    private static bool ShouldResyncWorkerOverviewFromLag(
        WorkEventSubscriptionDiagnosticsSnapshot? diagnostics,
        WorkableSignalROptions options)
    {
        if (diagnostics is null)
        {
            return false;
        }

        if (diagnostics.DroppedEventCount > 0)
        {
            return true;
        }

        var threshold = Math.Clamp(
            options.WorkerOverviewResyncQueuedEventThreshold,
            1,
            diagnostics.Capacity);
        return diagnostics.QueuedCount >= threshold;
    }

    private static WorkWorkerOverviewRealtimeUpdate CreateWorkerOverviewRefreshInstruction(
        WorkEventSubscriptionDiagnosticsSnapshot? diagnostics)
    {
        var reason = diagnostics is null
            ? "Realtime worker updates fell behind and should be refreshed."
            : $"Realtime worker updates fell behind (queued {diagnostics.QueuedCount}/{diagnostics.Capacity}, dropped {diagnostics.DroppedEventCount}).";
        return new WorkWorkerOverviewRealtimeUpdate(
            DateTimeOffset.UtcNow,
            RequiresRefresh: true,
            RefreshReason: reason);
    }

    private async Task SendViewUpdateToGroup(
        string groupName,
        string viewName,
        WorkComponentQueryResult view,
        CancellationToken cancellationToken)
    {
        var subscriptions = viewSubscriptions.GetGroupSubscriptions(groupName);
        foreach (var subscription in subscriptions)
        {
            await hub.Clients
                .Client(subscription.ConnectionId)
                .SendAsync(
                    WorkableRealtimeClientMethods.ViewUpdated,
                    new WorkableRealtimeViewEnvelope<WorkComponentQueryResult>(
                        subscription.SubscriptionId,
                        viewName,
                        view),
                    cancellationToken);
        }
    }

    private static bool IsDiagnosticsView(WorkableRealtimeViewSubscription subscription)
        => string.Equals(subscription.ViewName, "diagnostics", StringComparison.OrdinalIgnoreCase);

    private static IWorkSystemSession CreateAuthorizedSession(
        IWorkSystem system,
        WorkAuthorizationSnapshot authorization)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(authorization);

        return system.CreateSession(new WorkRequestContext(
            Origin: WorkOrigin.Create(
                WorkInvocationChannel.SignalR,
                authorization.Actor),
            Authorization: authorization));
    }

    private static bool IsDiagnosticsAlertChangesSubscription(WorkableRealtimeViewSubscription subscription)
        => subscription.Criteria.Components?
            .Any(component =>
                IsAlertDiagnosticsComponent(component) &&
                string.Equals(component.Shape, WorkComponentShapes.Compact, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(GetStringOption(component.Options, "publishMode"), "alertChanges", StringComparison.OrdinalIgnoreCase)) == true;

    private static bool IsAlertDiagnosticsComponent(WorkComponentRequest component)
        => string.Equals(component.Type, "queueDiagnostics", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(component.Type, "systemDiagnostics", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(component.Type, "readModelDiagnostics", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(component.Type, "retentionDiagnostics", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(component.Type, "concurrencyDiagnostics", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(component.Type, "durabilityDiagnostics", StringComparison.OrdinalIgnoreCase);

    private static WorkableRealtimeDiagnosticsAlertState CreateDiagnosticsAlertState(
        IWorkSystemSession session,
        WorkableRealtimeViewSubscription subscription,
        WorkSystemState? systemState = null)
    {
        var queue = session.Diagnostics.Queue;
        var readModel = session.Diagnostics.ReadModel;
        var readModelThreshold = GetReadModelDiagnosticsWarningThreshold(subscription);
        var readModelLagSeverity = readModel.PendingUpdateCount >= readModelThreshold * 10L
            ? WorkableRealtimeDiagnosticsLagSeverity.Critical
            : readModel.PendingUpdateCount >= readModelThreshold
                ? WorkableRealtimeDiagnosticsLagSeverity.Warning
                : WorkableRealtimeDiagnosticsLagSeverity.Normal;
        var retention = session.Diagnostics.Retention;
        var retentionWarningSeconds = GetRetentionDiagnosticsWarningSeconds(subscription);
        var retentionLagSeverity = retention.OldestDuePurgeAge >= TimeSpan.FromSeconds(retentionWarningSeconds * 10L)
            ? WorkableRealtimeDiagnosticsLagSeverity.Critical
            : retention.OldestDuePurgeAge >= TimeSpan.FromSeconds(retentionWarningSeconds)
                ? WorkableRealtimeDiagnosticsLagSeverity.Warning
                : WorkableRealtimeDiagnosticsLagSeverity.Normal;
        var concurrency = session.Diagnostics.Concurrency;
        var concurrencyWarningSeconds = GetConcurrencyDiagnosticsWarningSeconds(subscription);
        var concurrencyLagSeverity = concurrency.DeferredStartCount > 0 &&
            concurrency.OldestDeferredStartAge >= TimeSpan.FromSeconds(concurrencyWarningSeconds * 10L)
            ? WorkableRealtimeDiagnosticsLagSeverity.Critical
            : concurrency.DeferredStartCount > 0 &&
                concurrency.OldestDeferredStartAge >= TimeSpan.FromSeconds(concurrencyWarningSeconds)
                ? WorkableRealtimeDiagnosticsLagSeverity.Warning
                : WorkableRealtimeDiagnosticsLagSeverity.Normal;
        var durability = session.Diagnostics.Durability;
        var acceptedWorkerWarningSeconds = GetDurabilityAcceptedWorkerWarningSeconds(subscription);
        var cleanupWarningSeconds = GetDurabilityCleanupWarningSeconds(subscription);
        var acceptedWorkerLagSeverity = durability.AcceptedWaiterCount > 0 &&
            durability.OldestAcceptedWaiterAge >= TimeSpan.FromSeconds(acceptedWorkerWarningSeconds * 10L)
            ? WorkableRealtimeDiagnosticsLagSeverity.Critical
            : durability.AcceptedWaiterCount > 0 &&
                durability.OldestAcceptedWaiterAge >= TimeSpan.FromSeconds(acceptedWorkerWarningSeconds)
                ? WorkableRealtimeDiagnosticsLagSeverity.Warning
                : WorkableRealtimeDiagnosticsLagSeverity.Normal;
        var cleanupLagSeverity = durability.PendingCleanupCount > 0 &&
            durability.OldestPendingCleanupAge >= TimeSpan.FromSeconds(cleanupWarningSeconds * 10L)
            ? WorkableRealtimeDiagnosticsLagSeverity.Critical
            : durability.PendingCleanupCount > 0 &&
                durability.OldestPendingCleanupAge >= TimeSpan.FromSeconds(cleanupWarningSeconds)
                ? WorkableRealtimeDiagnosticsLagSeverity.Warning
                : WorkableRealtimeDiagnosticsLagSeverity.Normal;

        return new WorkableRealtimeDiagnosticsAlertState(
            session.SystemName,
            systemState ?? session.SystemState,
            queue.RejectedWorkCount,
            queue.LastRejectedAt,
            queue.LastRejectedCode,
            queue.LastRejectedMessage,
            queue.AlertableRejectedWorkCount,
            queue.LastAlertableRejectedCode,
            queue.LastAlertableRejectedMessage,
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
            retention.SchedulerFailureMessage,
            concurrency.DeferredStartCount,
            concurrency.OldestDeferredStartAge,
            concurrency.LastDrainReleasedCount,
            concurrencyWarningSeconds,
            concurrencyLagSeverity,
            durability.AcceptedWaiterCount,
            durability.OldestAcceptedWaiterAge,
            acceptedWorkerWarningSeconds,
            acceptedWorkerLagSeverity,
            durability.PendingCleanupCount,
            durability.OldestPendingCleanupAge,
            cleanupWarningSeconds,
            cleanupLagSeverity,
            durability.HasReaderFailure,
            durability.ReaderFailureType,
            durability.ReaderFailureMessage,
            durability.HasLeaseRenewalFailure,
            durability.LeaseRenewalFailureType,
            durability.LeaseRenewalFailureMessage,
            durability.HasCleanupFailure,
            durability.CleanupFailureType,
            durability.CleanupFailureMessage);
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

    private static int GetConcurrencyDiagnosticsWarningSeconds(WorkableRealtimeViewSubscription subscription)
        => Math.Max(1, subscription.Criteria.Components?
            .Where(component => string.Equals(component.Type, "concurrencyDiagnostics", StringComparison.OrdinalIgnoreCase))
            .Select(component => GetInt32Option(component.Options, "warningSeconds"))
            .FirstOrDefault(value => value.HasValue) ?? 30);

    private static int GetDurabilityAcceptedWorkerWarningSeconds(WorkableRealtimeViewSubscription subscription)
        => Math.Max(1, subscription.Criteria.Components?
            .Where(component => string.Equals(component.Type, "durabilityDiagnostics", StringComparison.OrdinalIgnoreCase))
            .Select(component => GetInt32Option(component.Options, "acceptedWorkerWarningSeconds"))
            .FirstOrDefault(value => value.HasValue) ?? 30);

    private static int GetDurabilityCleanupWarningSeconds(WorkableRealtimeViewSubscription subscription)
        => Math.Max(1, subscription.Criteria.Components?
            .Where(component => string.Equals(component.Type, "durabilityDiagnostics", StringComparison.OrdinalIgnoreCase))
            .Select(component => GetInt32Option(component.Options, "cleanupWarningSeconds"))
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

    private static TimeSpan NormalizeBatchTimeWindow(WorkableSignalROptions signalROptions)
    {
        var minimum = NormalizeMinimumTimeWindow(signalROptions);
        var requested = NormalizeInterval(
            signalROptions.BatchTimeWindow,
            TimeSpan.FromSeconds(1));
        return requested < minimum ? minimum : requested;
    }

    private static TimeSpan ResolveTimeWindow(
        WorkableRealtimeEventSubscriptions.EventSubscription subscription,
        WorkableSignalROptions signalROptions)
    {
        if (subscription.Filter?.WorkerId is not null)
        {
            return NormalizeLiveTimeWindow(signalROptions);
        }

        return NormalizeBatchTimeWindow(signalROptions);
    }

    private static TimeSpan NormalizeLiveTimeWindow(WorkableSignalROptions signalROptions)
    {
        var minimum = NormalizeMinimumTimeWindow(signalROptions);
        var requested = NormalizeInterval(
            signalROptions.LiveTimeWindow,
            TimeSpan.FromMilliseconds(100));
        return requested < minimum ? minimum : requested;
    }

    private static TimeSpan NormalizeMinimumTimeWindow(WorkableSignalROptions signalROptions)
        => NormalizeInterval(
            signalROptions.MinimumTimeWindow,
            TimeSpan.FromMilliseconds(100));

    private sealed record EventPump(
        CancellationTokenSource Cancellation,
        Task Task);

    private sealed record EventBatch(
        IReadOnlyList<WorkEvent> Events,
        Task<bool>? PendingRead);

    private sealed record WorkerOverviewEventBatch(
        IReadOnlyList<WorkEvent> Events,
        Task<bool>? PendingRead);
}
