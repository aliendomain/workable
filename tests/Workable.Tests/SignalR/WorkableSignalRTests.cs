using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Workable;

namespace Workable.Tests;

[Trait("Category", "SignalR")]
public sealed class WorkableSignalRTests
{
    private static readonly TimeSpan ManualViewPublishInterval = TimeSpan.FromMinutes(7);
    private static readonly TimeSpan ManualDiagnosticsPublishInterval = TimeSpan.FromMinutes(11);

    [Fact]
    public async Task HostEndpointReportsRealtimeDisabledWhenSignalRIsNotRegistered()
    {
        using var host = await CreateHost(addSignalR: false);
        var client = host.GetTestClient();

        var response = await client.GetFromJsonAsync<WorkableHttpHostDescriptor>("/workable/host", JsonOptions());
        Assert.NotNull(response);

        Assert.False(response.Capabilities.Realtime.Enabled);
        Assert.Null(response.Capabilities.Realtime.Transport);
        Assert.Null(response.Capabilities.Realtime.HubPath);
    }

    [Fact]
    public async Task HostEndpointReportsRealtimeEnabledWhenSignalRIsRegistered()
    {
        using var host = await CreateHost(addSignalR: true);
        var client = host.GetTestClient();

        var response = await client.GetFromJsonAsync<WorkableHttpHostDescriptor>("/workable/host", JsonOptions());
        Assert.NotNull(response);
        var capabilities = response.Capabilities;

        Assert.True(capabilities.Realtime.Enabled);
        Assert.Equal("signalr", capabilities.Realtime.Transport);
        Assert.Equal("/workable/realtime", capabilities.Realtime.HubPath);
    }

    [Fact]
    public async Task HostEndpointReportsRealtimeForConnectOnlyCaller()
    {
        using var host = await CreateHost(
            addSignalR: true,
            groups: TransportAuthorizationTestSupport.ConnectGroups);
        var client = host.GetTestClient();

        var response = await client.GetFromJsonAsync<WorkableHttpHostDescriptor>("/workable/host", JsonOptions());
        Assert.NotNull(response);

        Assert.True(response.Capabilities.Realtime.Enabled);
        Assert.Equal("signalr", response.Capabilities.Realtime.Transport);
        Assert.Equal("/workable/realtime", response.Capabilities.Realtime.HubPath);
    }

    [Fact]
    public async Task HostEndpointUsesMappedRealtimeHubPath()
    {
        using var host = await CreateHost(addSignalR: true, hubPath: "/custom/realtime");
        var client = host.GetTestClient();

        var response = await client.GetFromJsonAsync<WorkableHttpHostDescriptor>("/workable/host", JsonOptions());
        Assert.NotNull(response);

        Assert.Equal("/custom/realtime", response.Capabilities.Realtime.HubPath);
    }

    [Fact]
    public async Task HostEndpointIncludesRealtimeCapabilities()
    {
        using var host = await CreateHost(addSignalR: true);
        var client = host.GetTestClient();

        var response = await client.GetFromJsonAsync<WorkableHttpHostDescriptor>("/workable/host", JsonOptions());

        Assert.NotNull(response);
        var system = Assert.Single(response.Systems);
        Assert.True(system.IsDefault);
        Assert.True(response.Capabilities.Realtime.Enabled);
        Assert.Equal("signalr", response.Capabilities.Realtime.Transport);
        Assert.Equal("/workable/realtime", response.Capabilities.Realtime.HubPath);
    }

    [Fact]
    public async Task SignalREventStreamSubscribesOnlyWhileEventWatchersAreActive()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var stream = GetEventStream(system);
        await using var connection = CreateConnection(host);

        await connection.StartAsync();

        Assert.Equal(0, stream.ActiveSubscriptionCount);
        await connection.InvokeAsync("WatchEvents", new WorkableRealtimeEventCriteria(), null);
        await TestEventually.Until(() => stream.ActiveSubscriptionCount == 1);

        await connection.InvokeAsync("UnwatchEvents", new WorkableRealtimeEventCriteria(), null);

        await TestEventually.Until(() => stream.ActiveSubscriptionCount == 0);
    }

    [Fact]
    public async Task EventWatcherReceivesOnlySelectedEventTypes()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var eventSubscriptions = host.Services.GetRequiredService<WorkableRealtimeEventSubscriptions>();
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        var events = Channel.CreateUnbounded<WorkableRealtimeEvent>();
        CaptureRealtimeEvents(connection, events);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchEvents",
            new WorkableRealtimeEventCriteria(["worker.completed"]),
            null);

        var session = Session(system);
        var definition = session.Catalog.Definitions.Single(work => work.Name == "signalr.view");
        var handle = await session.Queue.Enqueue(definition.Name);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        await handle.WaitForCompletion();

        var completed = await ReadUntil(
            events.Reader,
            workEvent => workEvent.EventType == "worker.completed");
        var debugSubscription = Assert.Single(eventSubscriptions.GetDebugSubscriptions(system));
        var filter = debugSubscription.Filter ?? throw new InvalidOperationException("Expected filtered event subscription.");

        Assert.Equal(handle.WorkerId, completed.WorkerId);
        Assert.Equal("worker.completed", completed.EventType);
        Assert.Equal(definition.Id, completed.WorkDefinitionId);
        Assert.Equal(definition.Name, completed.WorkDefinitionName);
        Assert.Equal(["worker.completed"], Required(filter.EventTypes).ToArray());
    }

    [Fact]
    public async Task EventWatcherReceivesAllEventsWhenCriteriaIsEmpty()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        var events = Channel.CreateUnbounded<WorkableRealtimeEvent>();
        CaptureRealtimeEvents(connection, events);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchEvents",
            new WorkableRealtimeEventCriteria(),
            null);

        var session = Session(system);
        var definition = session.Catalog.Definitions.Single(work => work.Name == "signalr.view");
        var handle = await session.Queue.Enqueue(definition.Name);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        await handle.WaitForCompletion();

        var queued = await ReadUntil(
            events.Reader,
            workEvent => workEvent.WorkerId == handle.WorkerId && workEvent.EventType == "worker.queued");
        var completed = await ReadUntil(
            events.Reader,
            workEvent => workEvent.WorkerId == handle.WorkerId && workEvent.EventType == "worker.completed");

        Assert.Equal(handle.WorkerId, queued.WorkerId);
        Assert.Equal("worker.queued", queued.EventType);
        Assert.Equal(definition.Id, queued.WorkDefinitionId);
        Assert.Equal(definition.Name, queued.WorkDefinitionName);
        Assert.Equal(handle.WorkerId, completed.WorkerId);
        Assert.Equal("worker.completed", completed.EventType);
        Assert.Equal(definition.Id, completed.WorkDefinitionId);
        Assert.Equal(definition.Name, completed.WorkDefinitionName);
    }

    [Fact]
    public async Task EventWatcherFiltersByDefinitionAndKey()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var eventSubscriptions = host.Services.GetRequiredService<WorkableRealtimeEventSubscriptions>();
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        var definition = Session(system).Catalog.Definitions.Single(work => work.Name == "signalr.view");
        var acceptedIdentifier = new WorkIdentifier("batch", "accepted");
        await using var connection = CreateConnection(host);
        var events = Channel.CreateUnbounded<WorkableRealtimeEvent>();
        CaptureRealtimeEvents(connection, events);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchEvents",
            new WorkableRealtimeEventCriteria(
                EventTypes: ["worker.completed"],
                DefinitionIds: [definition.Id.Value.ToString("D")],
                Keys:
                [
                    new WorkableRealtimeEventKeyCriteria(
                        WorkKeyKind.Identifier,
                        acceptedIdentifier.Type,
                        acceptedIdentifier.Value),
                ]),
            null);

        var session = Session(system);
        var accepted = await session.Queue.Enqueue("signalr.view", WorkInput.Empty.WithIdentifier(acceptedIdentifier));
        var ignored = await session.Queue.Enqueue("signalr.view", WorkInput.Empty.WithIdentifier(new WorkIdentifier("batch", "ignored")));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        await Task.WhenAll(accepted.WaitForCompletion(), ignored.WaitForCompletion());

        var completed = await ReadUntil(
            events.Reader,
            workEvent => workEvent.EventType == "worker.completed");
        var debugSubscription = Assert.Single(eventSubscriptions.GetDebugSubscriptions(system));
        var filter = debugSubscription.Filter ?? throw new InvalidOperationException("Expected filtered event subscription.");
        var key = Assert.Single(Required(filter.Keys));

        Assert.Equal(accepted.WorkerId, completed.WorkerId);
        Assert.Equal("worker.completed", completed.EventType);
        Assert.Equal(definition.Id, completed.WorkDefinitionId);
        Assert.Equal(definition.Name, completed.WorkDefinitionName);
        Assert.Equal([acceptedIdentifier], completed.Identifiers.ToArray());
        Assert.Equal([definition.Id], Required(filter.DefinitionIds).ToArray());
        Assert.Equal(WorkKeyKind.Identifier, key.Kind);
        Assert.Equal(acceptedIdentifier.Type, key.Type);
        Assert.Equal(acceptedIdentifier.Value, key.Value);
    }

    [Fact]
    public async Task EventWatcherReceivesBurstsAsBatches()
    {
        using var host = await CreateHost(addSignalR: true, configureSignalR: options =>
        {
            options.BatchTimeWindow = TimeSpan.FromMilliseconds(500);
            options.EventMaxBatchSize = 10;
        });
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        var batches = Channel.CreateUnbounded<WorkableRealtimeEventBatch>();
        connection.On<WorkableRealtimeEventBatch>(
            WorkableRealtimeClientMethods.WorkEvents,
            batch => batches.Writer.TryWrite(batch));
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchEvents",
            new WorkableRealtimeEventCriteria(["worker.completed"]),
            null);

        var session = Session(system);
        var definition = session.Catalog.Definitions.Single(work => work.Name == "signalr.view");
        var handles = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => session.Queue.Enqueue(definition.Name)));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        await Task.WhenAll(handles.Select(handle => handle.WaitForCompletion()));
        var expectedWorkerIds = handles
            .Select(handle => handle.WorkerId ?? throw new InvalidOperationException("Expected accepted worker."))
            .ToHashSet();

        var batch = await ReadUntil(
            batches.Reader,
            batch => batch.Events.Count == expectedWorkerIds.Count &&
                batch.Events.Select(workEvent => workEvent.WorkerId).OfType<WorkerId>().ToHashSet().SetEquals(expectedWorkerIds));
        var actualWorkerIds = batch.Events
            .Select(workEvent => workEvent.WorkerId)
            .OfType<WorkerId>()
            .ToHashSet();

        Assert.Equal(3, batch.Events.Count);
        Assert.All(batch.Events, workEvent =>
        {
            Assert.Equal("worker.completed", workEvent.EventType);
            Assert.Equal(definition.Id, workEvent.WorkDefinitionId);
            Assert.Equal(definition.Name, workEvent.WorkDefinitionName);
        });
        Assert.Equal(
            expectedWorkerIds.OrderBy(static workerId => workerId.Value).ToArray(),
            actualWorkerIds.OrderBy(static workerId => workerId.Value).ToArray());
    }

    [Fact]
    public async Task EventWatcherRejectsUnknownSystemNames()
    {
        using var host = await CreateHost(addSignalR: true);
        await using var connection = CreateConnection(host);
        await connection.StartAsync();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => connection.InvokeAsync(
            "WatchEvents",
            new WorkableRealtimeEventCriteria(),
            "missing-system"));

        Assert.Contains("missing-system", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not found", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ViewWatcherReceivesRequestedOverviewComponentsOnly()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        const string subscriptionId = "overview";
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, views);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
            subscriptionId,
            "overview",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest("system", "system"),
                new WorkComponentRequest("workers", "workers", Shape: WorkComponentShapes.Compact),
            ]),
            null);

        var initial = await ReadUntil(views.Reader, view => view.Components.ContainsKey("workers"));
        var initialWorkers = Assert.IsType<JsonElement>(initial.Components["workers"].Data);

        var handle = await Session(system).Queue.Enqueue("signalr.view");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        var completion = await handle.WaitForCompletion();
        Assert.True(completion.IsCompletedSuccessfully);

        var updated = await ReadUntil(views.Reader, view => view.GeneratedAt > initial.GeneratedAt);
        var workers = Assert.IsType<JsonElement>(updated.Components["workers"].Data);

        Assert.Equal(["system", "workers"], initial.Components.Keys.Order().ToArray());
        Assert.Equal(["system", "workers"], updated.Components.Keys.Order().ToArray());
        Assert.Equal("compact", initial.Components["workers"].Shape);
        Assert.Equal("compact", updated.Components["workers"].Shape);
        Assert.Equal(0, initialWorkers.GetProperty("activeWorkerCount").GetInt32());
        Assert.Equal(0, initialWorkers.GetProperty("failedWorkerCount").GetInt32());
        Assert.False(initialWorkers.TryGetProperty("finalWorkerCount", out _));
        Assert.Equal(0, workers.GetProperty("activeWorkerCount").GetInt32());
        Assert.Equal(0, workers.GetProperty("failedWorkerCount").GetInt32());
        Assert.False(workers.TryGetProperty("finalWorkerCount", out _));
    }

    [Fact]
    public async Task ViewWatcherContinuesPublishingOverviewThroughputWithoutReadModelChanges()
    {
        var timers = new ManualRealtimeTimerFactory();
        using var host = await CreateHost(
            addSignalR: true,
            configureServices: services => services.AddSingleton<IWorkableRealtimeTimerFactory>(timers),
            configureSignalR: options =>
            {
                options.PublishInterval = ManualViewPublishInterval;
                options.DiagnosticsPublishInterval = ManualDiagnosticsPublishInterval;
            });
        await using var connection = CreateConnection(host);
        const string subscriptionId = "overview";
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, views);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
            subscriptionId,
            "overview",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest(
                    "throughput",
                    "throughput",
                    JsonSerializer.SerializeToElement(new { windowSeconds = 60, bucketSeconds = 1 }),
                    WorkComponentShapes.Standard),
            ]),
            null);

        var initial = await ReadUntil(views.Reader, view => view.Components.ContainsKey("throughput"));
        await TestEventually.ClockAfter(initial.GeneratedAt);
        await timers.TickWhenReady(ManualViewPublishInterval);
        var updated = await ReadUntil(
            views.Reader,
            view => view.GeneratedAt > initial.GeneratedAt &&
                view.Components.ContainsKey("throughput"));

        Assert.Equal(["throughput"], updated.Components.Keys.ToArray());
    }

    [Fact]
    public async Task WorkerOverviewWatcherReceivesInitialSnapshotAndLifecycleUpdates()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        const string subscriptionId = "worker-overview";
        var updates = Channel.CreateUnbounded<WorkWorkerOverviewRealtimeUpdate>();
        CaptureWorkerOverviewUpdates(connection, subscriptionId, updates);
        await connection.StartAsync();

        var handle = await Session(system).Queue.Enqueue("signalr.worker");
        var workerId = handle.WorkerId ?? throw new InvalidOperationException("Expected worker id.");

        await connection.InvokeAsync(
            "WatchWorkerOverview",
            subscriptionId,
            workerId.Value.ToString("D"),
            new WorkWorkerOverviewRealtimeCriteria(
                WorkerControls: WorkComponentShapes.Standard,
                WorkerDuration: WorkComponentShapes.Standard,
                WorkerTimeline: WorkComponentShapes.Standard),
            null);

        var initial = await ReadUntil(
            updates.Reader,
            update => update.Worker?.WorkerId == workerId);

        var initialWorker = Require(initial.Worker);
        Assert.Equal(workerId, initialWorker.WorkerId);
        Assert.Equal(WorkerState.Queued, initialWorker.State);
        Assert.Null(initial.LatestIteration);

        var session = Session(system);
        var worker = await session.Query.Worker(workerId)
            ?? throw new InvalidOperationException("Expected worker.");

        try
        {
            var start = await session.Workers.Execute(worker.Version, WorkAction.Start);
            Assert.True(start.IsAccepted);

            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var started = await ReadUntil(
                updates.Reader,
                update =>
                    update.Worker?.WorkerId == workerId &&
                    update.Worker.State == WorkerState.Running &&
                    update.LatestIteration?.WorkerId == workerId &&
                    update.LatestIteration.Status == WorkCompletionStatus.Executing);

            var startedWorker = Require(started.Worker);
            var startedIteration = Require(started.LatestIteration);
            Assert.Equal(workerId, startedWorker.WorkerId);
            Assert.Equal(WorkerState.Running, startedWorker.State);
            Assert.Equal(workerId, startedIteration.WorkerId);
            Assert.Equal(WorkCompletionStatus.Executing, startedIteration.Status);

            gate.Release.TrySetResult();
            var completion = await handle.WaitForCompletion();
            Assert.True(completion.IsCompletedSuccessfully);

            var completed = await ReadUntil(
                updates.Reader,
                update =>
                    update.Worker?.WorkerId == workerId &&
                    update.Worker.State == WorkerState.Completed &&
                    update.LatestIteration?.WorkerId == workerId &&
                    update.LatestIteration.Status == WorkCompletionStatus.Completed);

            var completedWorker = Require(completed.Worker);
            var completedIteration = Require(completed.LatestIteration);
            Assert.Equal(WorkerState.Completed, completedWorker.State);
            Assert.Equal(workerId, completedIteration.WorkerId);
            Assert.Equal(WorkCompletionStatus.Completed, completedIteration.Status);
            Assert.NotNull(completedIteration.CompletedAt);
        }
        finally
        {
            gate.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task WorkerOverviewWatcherRejectsInvalidWorkerIds()
    {
        using var host = await CreateHost(addSignalR: true);
        await using var connection = CreateConnection(host);
        await connection.StartAsync();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => connection.InvokeAsync(
            "WatchWorkerOverview",
            "worker-panel",
            "not-a-guid",
            new WorkWorkerOverviewRealtimeCriteria(),
            null));

        Assert.Contains("not-a-guid", exception.Message, StringComparison.Ordinal);
        Assert.Contains("not valid", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task WorkerOverviewWatcherRewatchWithNewSubscriptionIdReceivesSubsequentUpdates()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        const string firstSubscriptionId = "worker-overview-first";
        const string secondSubscriptionId = "worker-overview-second";
        var firstUpdates = Channel.CreateUnbounded<WorkWorkerOverviewRealtimeUpdate>();
        var secondUpdates = Channel.CreateUnbounded<WorkWorkerOverviewRealtimeUpdate>();
        CaptureWorkerOverviewUpdates(connection, firstSubscriptionId, firstUpdates);
        CaptureWorkerOverviewUpdates(connection, secondSubscriptionId, secondUpdates);
        await connection.StartAsync();

        var handle = await Session(system).Queue.Enqueue("signalr.worker");
        var workerId = handle.WorkerId ?? throw new InvalidOperationException("Expected worker id.");
        var criteria = new WorkWorkerOverviewRealtimeCriteria(
            WorkerControls: WorkComponentShapes.Standard,
            WorkerDuration: WorkComponentShapes.Standard,
            WorkerTimeline: WorkComponentShapes.Standard);

        await connection.InvokeAsync(
            "WatchWorkerOverview",
            firstSubscriptionId,
            workerId.Value.ToString("D"),
            criteria,
            null);

        await ReadUntil(
            firstUpdates.Reader,
            update => update.Worker?.WorkerId == workerId);

        await connection.InvokeAsync(
            "WatchWorkerOverview",
            secondSubscriptionId,
            workerId.Value.ToString("D"),
            criteria,
            null);

        await ReadUntil(
            secondUpdates.Reader,
            update => update.Worker?.WorkerId == workerId);

        var session = Session(system);
        var worker = await session.Query.Worker(workerId)
            ?? throw new InvalidOperationException("Expected worker.");

        try
        {
            var start = await session.Workers.Execute(worker.Version, WorkAction.Start);
            Assert.True(start.IsAccepted);

            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var started = await ReadUntil(
                secondUpdates.Reader,
                update =>
                    update.Worker?.WorkerId == workerId &&
                    update.Worker.State == WorkerState.Running &&
                    update.LatestIteration?.WorkerId == workerId &&
                    update.LatestIteration.Status == WorkCompletionStatus.Executing);

            Assert.Equal(WorkerState.Running, Require(started.Worker).State);
            Assert.Equal(WorkCompletionStatus.Executing, Require(started.LatestIteration).Status);
        }
        finally
        {
            gate.Release.TrySetResult();
        }
    }

    [Fact]
    public async Task ViewWatcherReceivesDiagnosticsViewOnDiagnosticsInterval()
    {
        var timers = new ManualRealtimeTimerFactory();
        using var host = await CreateHost(
            addSignalR: true,
            configureServices: services => services.AddSingleton<IWorkableRealtimeTimerFactory>(timers),
            configureSignalR: options =>
            {
                options.PublishInterval = ManualViewPublishInterval;
                options.DiagnosticsPublishInterval = ManualDiagnosticsPublishInterval;
            });
        await using var connection = CreateConnection(host);
        const string subscriptionId = "diagnostics";
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, views);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
            subscriptionId,
            "diagnostics",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest(
                    "queueDiagnostics",
                    "queueDiagnostics",
                    JsonSerializer.SerializeToElement(new { publishMode = "continuous" }),
                    WorkComponentShapes.Compact),
                new WorkComponentRequest(
                    "readModelDiagnostics",
                    "readModelDiagnostics",
                    JsonSerializer.SerializeToElement(new { warningThreshold = 100 }),
                    WorkComponentShapes.Compact),
                new WorkComponentRequest(
                    "retentionDiagnostics",
                    "retentionDiagnostics",
                    JsonSerializer.SerializeToElement(new { warningSeconds = 30 }),
                    WorkComponentShapes.Compact),
                new WorkComponentRequest(
                    "concurrencyDiagnostics",
                    "concurrencyDiagnostics",
                    JsonSerializer.SerializeToElement(new { warningSeconds = 30 }),
                    WorkComponentShapes.Compact),
                new WorkComponentRequest(
                    "durabilityDiagnostics",
                    "durabilityDiagnostics",
                    JsonSerializer.SerializeToElement(new
                    {
                        acceptedWorkerWarningSeconds = 30,
                        cleanupWarningSeconds = 30,
                    }),
                    WorkComponentShapes.Compact),
                new WorkComponentRequest(
                    "idempotencyDiagnostics",
                    "idempotencyDiagnostics",
                    JsonSerializer.SerializeToElement(new { publishMode = "continuous" }),
                    WorkComponentShapes.Compact),
            ]),
            null);

        var initial = await ReadUntil(
            views.Reader,
            view => view.Components.ContainsKey("readModelDiagnostics"));
        await TestEventually.ClockAfter(initial.GeneratedAt);
        await timers.TickWhenReady(ManualDiagnosticsPublishInterval);
        var updated = await ReadUntil(
            views.Reader,
            view => view.GeneratedAt > initial.GeneratedAt &&
                view.Components.ContainsKey("queueDiagnostics") &&
                view.Components.ContainsKey("readModelDiagnostics") &&
                view.Components.ContainsKey("retentionDiagnostics") &&
                view.Components.ContainsKey("concurrencyDiagnostics") &&
                view.Components.ContainsKey("durabilityDiagnostics") &&
                view.Components.ContainsKey("idempotencyDiagnostics"));
        var queue = Assert.IsType<JsonElement>(updated.Components["queueDiagnostics"].Data);
        var diagnostics = Assert.IsType<JsonElement>(updated.Components["readModelDiagnostics"].Data);
        var retention = Assert.IsType<JsonElement>(updated.Components["retentionDiagnostics"].Data);
        var concurrency = Assert.IsType<JsonElement>(updated.Components["concurrencyDiagnostics"].Data);
        var durability = Assert.IsType<JsonElement>(updated.Components["durabilityDiagnostics"].Data);
        var idempotency = Assert.IsType<JsonElement>(updated.Components["idempotencyDiagnostics"].Data);

        Assert.Equal([
            "queueDiagnostics",
            "readModelDiagnostics",
            "retentionDiagnostics",
            "concurrencyDiagnostics",
            "durabilityDiagnostics",
            "idempotencyDiagnostics",
        ], updated.Components.Keys.ToArray());
        Assert.Equal("compact", updated.Components["queueDiagnostics"].Shape);
        Assert.Equal(0, queue.GetProperty("rejectedWorkCount").GetInt64());
        Assert.False(queue.GetProperty("hasRejectedWork").GetBoolean());
        Assert.Equal(0, queue.GetProperty("alertableRejectedWorkCount").GetInt64());
        Assert.False(queue.GetProperty("hasAlertableRejectedWork").GetBoolean());
        Assert.Equal(JsonValueKind.Null, queue.GetProperty("lastRejectedCode").ValueKind);
        Assert.Equal(JsonValueKind.Null, queue.GetProperty("lastAlertableRejectedCode").ValueKind);
        Assert.Equal("compact", updated.Components["readModelDiagnostics"].Shape);
        Assert.Equal(0, diagnostics.GetProperty("pendingUpdateCount").GetInt64());
        Assert.False(diagnostics.GetProperty("isReadModelBehind").GetBoolean());
        Assert.Equal(100, diagnostics.GetProperty("readModelLagWarningThreshold").GetInt32());
        Assert.False(diagnostics.GetProperty("hasProjectorFailure").GetBoolean());
        Assert.Equal("compact", updated.Components["retentionDiagnostics"].Shape);
        Assert.Equal(0, retention.GetProperty("scheduledPurgeCount").GetInt32());
        Assert.False(retention.GetProperty("isRetentionBehind").GetBoolean());
        Assert.Equal(30, retention.GetProperty("retentionLagWarningSeconds").GetInt32());
        Assert.False(retention.GetProperty("hasSchedulerFailure").GetBoolean());
        Assert.Equal("compact", updated.Components["concurrencyDiagnostics"].Shape);
        Assert.Equal(0, concurrency.GetProperty("deferredStartCount").GetInt32());
        Assert.Equal(0, concurrency.GetProperty("lastDrainReleasedCount").GetInt32());
        Assert.False(concurrency.GetProperty("isConcurrencyBehind").GetBoolean());
        Assert.Equal(30, concurrency.GetProperty("concurrencyLagWarningSeconds").GetInt32());
        Assert.Equal("compact", updated.Components["durabilityDiagnostics"].Shape);
        Assert.Equal(0, durability.GetProperty("acceptedWaiterCount").GetInt32());
        Assert.Equal(0, durability.GetProperty("pendingCleanupCount").GetInt32());
        Assert.False(durability.GetProperty("isAcceptedWorkerMaterializationBehind").GetBoolean());
        Assert.Equal(30, durability.GetProperty("acceptedWorkerWarningSeconds").GetInt32());
        Assert.False(durability.GetProperty("isCleanupBehind").GetBoolean());
        Assert.Equal(30, durability.GetProperty("cleanupWarningSeconds").GetInt32());
        Assert.False(durability.GetProperty("hasReaderFailure").GetBoolean());
        Assert.False(durability.GetProperty("hasLeaseRenewalFailure").GetBoolean());
        Assert.False(durability.GetProperty("hasCleanupFailure").GetBoolean());
        Assert.Equal("compact", updated.Components["idempotencyDiagnostics"].Shape);
        Assert.Equal(0, idempotency.GetProperty("duplicateRejectionCount").GetInt64());
        Assert.Equal(JsonValueKind.Null, idempotency.GetProperty("lastDuplicateRejectedStorage").ValueKind);
    }

    [Fact]
    public async Task DiagnosticsAlertChangeWatcherReceivesAlertPayloadWhenSystemCapacityIsReached()
    {
        using var host = await CreateHost(
            addSignalR: true,
            configureWorkable: builder => builder.UseCapacity(new WorkSystemCapacityConfiguration
            {
                MaximumWorkers = 1,
            }));
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        await using var connection = CreateConnection(host);
        const string subscriptionId = "diagnostics";
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, views);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
            subscriptionId,
            "diagnostics",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest(
                    "queueDiagnostics",
                    "queueDiagnostics",
                    JsonSerializer.SerializeToElement(new
                    {
                        publishMode = "alertChanges",
                    }),
                    WorkComponentShapes.Compact),
            ]),
            null);
        var initial = await ReadUntil(
            views.Reader,
            view => view.Components.ContainsKey("queueDiagnostics"));

        var session = Session(system);
        _ = await session.Queue.Enqueue("signalr.worker");
        var rejected = await session.Queue.Enqueue("signalr.worker");
        Assert.False(rejected.QueueOutcome.IsAccepted);

        var updated = await ReadUntil(
            views.Reader,
            view =>
            {
                if (view.GeneratedAt <= initial.GeneratedAt ||
                    !view.Components.TryGetValue("queueDiagnostics", out var component))
                {
                    return false;
                }

                var diagnostics = Assert.IsType<JsonElement>(component.Data);
                return diagnostics.TryGetProperty("hasAlertableRejectedWork", out var hasRejectedWork) &&
                    hasRejectedWork.GetBoolean();
            });
        var data = Assert.IsType<JsonElement>(updated.Components["queueDiagnostics"].Data);

        Assert.Equal(1, data.GetProperty("rejectedWorkCount").GetInt64());
        Assert.True(data.GetProperty("hasRejectedWork").GetBoolean());
        Assert.Equal(1, data.GetProperty("alertableRejectedWorkCount").GetInt64());
        Assert.True(data.GetProperty("hasAlertableRejectedWork").GetBoolean());
        Assert.Equal("workable.system.capacity_reached", data.GetProperty("lastRejectedCode").GetString());
        Assert.Equal("workable.system.capacity_reached", data.GetProperty("lastAlertableRejectedCode").GetString());
    }

    [Fact]
    public async Task DiagnosticsAlertChangeWatcherReceivesAlertPayloadWhenReadModelFallsBehind()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        const string subscriptionId = "diagnostics";
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        CaptureRealtimeViews(connection, subscriptionId, views);
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
            subscriptionId,
            "diagnostics",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest(
                    "readModelDiagnostics",
                    "readModelDiagnostics",
                    JsonSerializer.SerializeToElement(new
                    {
                        publishMode = "alertChanges",
                        warningThreshold = 1,
                    }),
                    WorkComponentShapes.Compact),
            ]),
            null);
        var initial = await ReadUntil(
            views.Reader,
            view => view.Components.ContainsKey("readModelDiagnostics"));

        using var enqueueCancellation = new CancellationTokenSource();
        var session = Session(system);
        var enqueuePressure = Enumerable.Range(0, 4)
            .Select(index => Task.Run(async () =>
            {
                while (!enqueueCancellation.IsCancellationRequested)
                {
                    _ = await session.Queue.Enqueue("signalr.view");
                }
            }))
            .ToArray();

        try
        {
            await TestEventually.Until(() => session.Diagnostics.ReadModel.PendingUpdateCount >= 1);

            var updated = await ReadUntil(
                views.Reader,
                view =>
                {
                    if (view.GeneratedAt <= initial.GeneratedAt ||
                        !view.Components.TryGetValue("readModelDiagnostics", out var component))
                    {
                        return false;
                    }

                    var diagnostics = Assert.IsType<JsonElement>(component.Data);
                    return diagnostics.TryGetProperty("isReadModelBehind", out var behind) &&
                        behind.GetBoolean();
                });
            var data = Assert.IsType<JsonElement>(updated.Components["readModelDiagnostics"].Data);

            Assert.True(data.GetProperty("pendingUpdateCount").GetInt64() >= 1);
            Assert.True(data.GetProperty("isReadModelBehind").GetBoolean());
            Assert.Equal(1, data.GetProperty("readModelLagWarningThreshold").GetInt32());
            Assert.False(data.GetProperty("hasProjectorFailure").GetBoolean());
        }
        finally
        {
            enqueueCancellation.Cancel();
            await Task.WhenAll(enqueuePressure).WaitAsync(TimeSpan.FromSeconds(5));

            if (!gate.Release.Task.IsCompleted)
            {
                gate.Release.SetResult();
            }
        }
    }

    [Fact]
    public async Task DiagnosticsAlertChangeWatcherRequiresDiagnosticsPermission()
    {
        using var host = await CreateHost(
            addSignalR: true,
            configureWorkable: builder => builder.UseCapacity(new WorkSystemCapacityConfiguration
            {
                MaximumWorkers = 1,
            }),
            groups: TransportAuthorizationTestSupport.ConnectGroups);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var viewSubscriptions = host.Services.GetRequiredService<WorkableRealtimeViewSubscriptions>();
        await using var connection = CreateConnection(host);
        const string subscriptionId = "diagnostics";
        await connection.StartAsync();

        var exception = await Assert.ThrowsAnyAsync<Exception>(() => connection.InvokeAsync(
            "WatchView",
            subscriptionId,
            "diagnostics",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest(
                    "queueDiagnostics",
                    "queueDiagnostics",
                    JsonSerializer.SerializeToElement(new
                    {
                        publishMode = "alertChanges",
                    }),
                    WorkComponentShapes.Compact),
            ]),
            null));

        Assert.Contains("diagnostics permission", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(viewSubscriptions.GetDebugSubscriptions(system));

        var session = Session(system);
        _ = await session.Queue.Enqueue("signalr.worker");
        var rejected = await session.Queue.Enqueue("signalr.worker");

        Assert.False(rejected.QueueOutcome.IsAccepted);
        Assert.Empty(viewSubscriptions.GetDebugSubscriptions(system));
    }

    private static async Task<IHost> CreateHost(
        bool addSignalR,
        string? hubPath = null,
        Action<WorkableSignalROptions>? configureSignalR = null,
        Action<IWorkSystemBuilder>? configureWorkable = null,
        bool authenticated = true,
        IEnumerable<string>? groups = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddTransportTestAuthorization(groups);
                    services.AddSingleton<SignalRWorkGate>();
                    configureServices?.Invoke(services);
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        configureWorkable?.Invoke(builder);
                        builder.AddAuthorizedTransportWork(
                            WorkDefinition.Create(
                                "signalr.worker",
                                configuration: WorkConfiguration.Default with
                                {
                                    Start = WorkStartConfiguration.DoNotStart,
                                }),
                            SuccessfulWork);
                        builder.AddAuthorizedTransportWork(WorkDefinition.Create("signalr.view"), SuccessfulWork);
                    });
                    services.AddWorkableHttpApi();
                    if (addSignalR)
                    {
                        services.AddWorkableSignalR(options =>
                        {
                            options.PublishInterval = TimeSpan.FromMilliseconds(50);
                            options.DiagnosticsPublishInterval = TimeSpan.FromMilliseconds(50);
                            configureSignalR?.Invoke(options);
                        });
                    }
                });
                web.Configure(app =>
                {
                    if (authenticated)
                    {
                        app.Use(async (context, next) =>
                        {
                            context.User = CreateTransportPrincipal(groups: groups);
                            await next();
                        });
                    }

                    app.UseRouting();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapWorkableApi("/workable");
                        if (addSignalR)
                        {
                            endpoints.MapWorkableSignalR(hubPath);
                        }
                    });
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static HubConnection CreateConnection(IHost host, string? accessToken = null)
        => new HubConnectionBuilder()
            .WithUrl(
                "http://localhost/workable/realtime",
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => host.GetTestServer().CreateHandler();
                    if (accessToken is not null)
                    {
                        options.AccessTokenProvider = () => Task.FromResult<string?>(accessToken);
                    }
                })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            })
            .Build();

    [Fact]
    public async Task AnonymousSignalRConnectionIsRejected()
    {
        using var host = await CreateHost(addSignalR: true, authenticated: false);
        await using var connection = CreateConnection(host);

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }

    [Fact]
    public async Task SignalRCanUseExplicitWorkableAuthenticationSchemeWithoutChangingHostDefaultScheme()
    {
        using var host = await CreateExplicitSchemeSignalRHost();
        await using var unauthorized = CreateConnection(host);

        await Assert.ThrowsAnyAsync<Exception>(() => unauthorized.StartAsync());

        await using var authorized = CreateConnection(
            host,
            accessToken: WorkableSchemeAuthenticationTestSupport.WorkableToken);
        await authorized.StartAsync();
        await authorized.InvokeAsync("WatchEvents", new WorkableRealtimeEventCriteria(), null);
    }

    [Fact]
    public async Task SignalRUsesWorkableTransportSchemeWhenHostFallbackPolicyTargetsAnotherScheme()
    {
        using var host = await CreateExplicitSchemeSignalRHostWithFallbackPolicy();
        await using var connection = CreateConnection(
            host,
            accessToken: WorkableSchemeAuthenticationTestSupport.WorkableToken);

        await connection.StartAsync();
        await connection.InvokeAsync("WatchEvents", new WorkableRealtimeEventCriteria(), null);
    }

    private static async Task<IHost> CreateExplicitSchemeSignalRHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddWorkableSchemeTestAuthentication();
                    services.AddTransportTestAuthorization();
                    services.AddSingleton<SignalRWorkGate>();
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        builder.AddAuthorizedTransportWork(
                            WorkDefinition.Create(
                                "signalr.worker",
                                configuration: WorkConfiguration.Default with
                                {
                                    Start = WorkStartConfiguration.DoNotStart,
                                }),
                            SuccessfulWork);
                        builder.AddAuthorizedTransportWork(WorkDefinition.Create("signalr.view"), SuccessfulWork);
                    });
                    services.AddWorkableHttpApi();
                    services.AddWorkableSignalR(options =>
                    {
                        options.PublishInterval = TimeSpan.FromMilliseconds(50);
                        options.DiagnosticsPublishInterval = TimeSpan.FromMilliseconds(50);
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapWorkableApi("/workable");
                        endpoints.MapWorkableSignalR();
                    });
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> CreateExplicitSchemeSignalRHostWithFallbackPolicy()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddWorkableSchemeTestAuthentication();
                    services.AddAuthorization(options =>
                    {
                        options.FallbackPolicy = new AuthorizationPolicyBuilder(
                            WorkableSchemeAuthenticationTestSupport.AmbientScheme)
                            .RequireClaim("host-app")
                            .Build();
                    });
                    services.AddTransportTestAuthorization();
                    services.AddSingleton<SignalRWorkGate>();
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        builder.AddAuthorizedTransportWork(
                            WorkDefinition.Create(
                                "signalr.worker",
                                configuration: WorkConfiguration.Default with
                                {
                                    Start = WorkStartConfiguration.DoNotStart,
                                }),
                            SuccessfulWork);
                        builder.AddAuthorizedTransportWork(WorkDefinition.Create("signalr.view"), SuccessfulWork);
                    });
                    services.AddWorkableHttpApi();
                    services.AddWorkableSignalR(options =>
                    {
                        options.PublishInterval = TimeSpan.FromMilliseconds(50);
                        options.DiagnosticsPublishInterval = TimeSpan.FromMilliseconds(50);
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapWorkableApi("/workable");
                        endpoints.MapWorkableSignalR();
                    });
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static void CaptureRealtimeEvents(
        HubConnection connection,
        Channel<WorkableRealtimeEvent> events)
    {
        connection.On<WorkableRealtimeEvent>(
            WorkableRealtimeClientMethods.WorkEvent,
            workEvent => events.Writer.TryWrite(workEvent));
        connection.On<WorkableRealtimeEventBatch>(
            WorkableRealtimeClientMethods.WorkEvents,
            batch =>
            {
                foreach (var workEvent in batch.Events)
                {
                    events.Writer.TryWrite(workEvent);
                }
            });
    }

    private static void CaptureRealtimeViews(
        HubConnection connection,
        string subscriptionId,
        Channel<WorkComponentQueryResult> views)
    {
        connection.On<WorkableRealtimeViewEnvelope<WorkComponentQueryResult>>(
            WorkableRealtimeClientMethods.ViewUpdated,
            envelope =>
            {
                if (string.Equals(envelope.SubscriptionId, subscriptionId, StringComparison.Ordinal))
                {
                    views.Writer.TryWrite(envelope.Result);
                }
            });
    }

    private static void CaptureWorkerOverviewUpdates(
        HubConnection connection,
        string subscriptionId,
        Channel<WorkWorkerOverviewRealtimeUpdate> updates)
    {
        connection.On<WorkableRealtimeViewEnvelope<WorkWorkerOverviewRealtimeUpdate>>(
            WorkableRealtimeClientMethods.WorkerOverviewUpdated,
            envelope =>
            {
                if (string.Equals(envelope.SubscriptionId, subscriptionId, StringComparison.Ordinal))
                {
                    updates.Writer.TryWrite(envelope.Result);
                }
            });
    }

    private static async Task<T> ReadUntil<T>(
        ChannelReader<T> reader,
        Func<T, bool> predicate)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await foreach (var item in reader.ReadAllAsync(cancellation.Token))
        {
            if (predicate(item))
            {
                return item;
            }
        }

        throw new InvalidOperationException("Expected item was not received.");
    }

    private static IReadOnlySet<T> Required<T>(IReadOnlySet<T>? values)
        => values ?? throw new InvalidOperationException("Expected values.");

    private static T Require<T>(T? value)
        where T : class
    {
        Assert.NotNull(value);
        return value;
    }

    private static IWorkSystemSession Session(IWorkSystem system)
        => TransportAuthorizationTestSupport.CreateTransportSession(
            system,
            WorkInvocationChannel.DotNet,
            description: "Use SignalR test session.");

    private static ClaimsPrincipal CreateTransportPrincipal(IEnumerable<string>? groups = null)
        => TransportAuthorizationTestSupport.CreateTransportPrincipal(
            id: "signalr-user-1",
            name: "SignalR User",
            email: "signalr.user@example.test",
            groups: groups);

    private static WorkEventStream GetEventStream(IWorkSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        var field = system.GetType().GetField("events", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected in-memory event stream field.");
        return Assert.IsType<WorkEventStream>(field.GetValue(system));
    }

    private static System.Text.Json.JsonSerializerOptions JsonOptions()
    {
        var options = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
    {
        var gate = context.Services.GetRequiredService<SignalRWorkGate>();
        gate.Entered.TrySetResult();
        return CompleteWhenReleased(gate, cancellationToken);
    }

    private static async Task<WorkExecutionResult> CompleteWhenReleased(
        SignalRWorkGate gate,
        CancellationToken cancellationToken)
    {
        await gate.Release.Task.WaitAsync(cancellationToken);
        return WorkExecutionResult.Success();
    }

    private sealed class SignalRWorkGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class ManualRealtimeTimerFactory : IWorkableRealtimeTimerFactory
    {
        private readonly object gate = new();
        private readonly Dictionary<TimeSpan, ManualRealtimeTimer> timers = new();

        public IWorkableRealtimeTimer Create(TimeSpan interval)
        {
            lock (this.gate)
            {
                if (!this.timers.TryGetValue(interval, out var timer))
                {
                    timer = new ManualRealtimeTimer();
                    this.timers[interval] = timer;
                }

                return timer;
            }
        }

        public async Task TickWhenReady(TimeSpan interval)
        {
            var timer = await TestEventually.UntilNotNull(
                () => Task.FromResult(this.GetTimer(interval)),
                $"Expected realtime timer for interval {interval} to be created.");
            timer.Tick();
        }

        private ManualRealtimeTimer? GetTimer(TimeSpan interval)
        {
            lock (this.gate)
            {
                return this.timers.TryGetValue(interval, out var timer) ? timer : null;
            }
        }
    }

    private sealed class ManualRealtimeTimer : IWorkableRealtimeTimer
    {
        private readonly Channel<bool> ticks = Channel.CreateUnbounded<bool>();

        public async ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await this.ticks.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return false;
            }
        }

        public void Tick()
            => this.ticks.Writer.TryWrite(true);

        public void Dispose()
            => this.ticks.Writer.TryComplete();
    }
}
