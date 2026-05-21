using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Channels;
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
    [Fact]
    public async Task SystemsEndpointReportsRealtimeDisabledWhenSignalRIsNotRegistered()
    {
        using var host = await CreateHost(addSignalR: false);
        var client = host.GetTestClient();

        var systems = await client.GetFromJsonAsync<WorkableHttpSystems>("/workable/systems", JsonOptions());
        var system = Assert.Single(systems?.Systems ?? []);

        Assert.False(system.Capabilities.Realtime.Enabled);
        Assert.Null(system.Capabilities.Realtime.Transport);
        Assert.Null(system.Capabilities.Realtime.HubPath);
    }

    [Fact]
    public async Task SystemsEndpointReportsRealtimeEnabledWhenSignalRIsRegistered()
    {
        using var host = await CreateHost(addSignalR: true);
        var client = host.GetTestClient();

        var systems = await client.GetFromJsonAsync<WorkableHttpSystems>("/workable/systems", JsonOptions());
        var system = Assert.Single(systems?.Systems ?? []);
        var capabilities = system.Capabilities;

        Assert.True(capabilities.Realtime.Enabled);
        Assert.Equal("signalr", capabilities.Realtime.Transport);
        Assert.Equal("/workable/realtime", capabilities.Realtime.HubPath);
        Assert.Contains("worker-events", capabilities.Realtime.Features ?? []);
        Assert.Contains("component-views", capabilities.Realtime.Features ?? []);
        Assert.Contains("diagnostics-view", capabilities.Realtime.Features ?? []);
    }

    [Fact]
    public async Task SystemsEndpointUsesMappedRealtimeHubPath()
    {
        using var host = await CreateHost(addSignalR: true, hubPath: "/custom/realtime");
        var client = host.GetTestClient();

        var systems = await client.GetFromJsonAsync<WorkableHttpSystems>("/workable/systems", JsonOptions());
        var system = Assert.Single(systems?.Systems ?? []);

        Assert.Equal("/custom/realtime", system.Capabilities.Realtime.HubPath);
    }

    [Fact]
    public async Task SystemsEndpointIncludesRealtimeCapabilities()
    {
        using var host = await CreateHost(addSignalR: true);
        var client = host.GetTestClient();

        var systems = await client.GetFromJsonAsync<WorkableHttpSystems>("/workable/systems", JsonOptions());

        Assert.NotNull(systems);
        var system = Assert.Single(systems.Systems);
        Assert.True(system.IsDefault);
        Assert.True(system.Capabilities.Realtime.Enabled);
        Assert.Equal("signalr", system.Capabilities.Realtime.Transport);
        Assert.Equal("/workable/realtime", system.Capabilities.Realtime.HubPath);
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
        var watchedWorkerId = WorkerId.New();
        await connection.InvokeAsync("WatchWorker", watchedWorkerId.Value.ToString("D"), null);
        await Eventually(() => stream.ActiveSubscriptionCount == 1);

        await connection.InvokeAsync("UnwatchWorker", watchedWorkerId.Value.ToString("D"), null);

        await Eventually(() => stream.ActiveSubscriptionCount == 0);
    }

    [Fact]
    public async Task WorkerWatcherReceivesEventsForThatWorker()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        var events = Channel.CreateUnbounded<WorkableRealtimeEvent>();
        CaptureRealtimeEvents(connection, events);
        await connection.StartAsync();

        var handle = await Session(system).Queue.Enqueue("signalr.worker");
        var workerId = handle.WorkerId ?? throw new InvalidOperationException("Expected worker id.");
        await connection.InvokeAsync("WatchWorker", workerId.Value.ToString("D"), null);

        var session = Session(system);
        var worker = await session.Query.Worker(workerId)
            ?? throw new InvalidOperationException("Expected worker.");
        var start = await session.Workers.Execute(worker.Version, WorkAction.Start);
        Assert.True(start.IsAccepted);

        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();

        var completed = await ReadUntil(
            events.Reader,
            workEvent => workEvent.WorkerId == workerId && workEvent.EventType == "worker.completed");

        Assert.Equal(workerId, completed.WorkerId);
        Assert.Equal(
            "signalr.worker",
            completed.Data?.GetProperty("worker").GetProperty("definitionName").GetString());
    }

    [Fact]
    public async Task EventWatcherReceivesOnlySelectedEventTypes()
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
            new WorkableRealtimeEventCriteria(["worker.completed"]),
            null);

        var handle = await Session(system).Queue.Enqueue("signalr.view");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        await handle.WaitForCompletion();

        var completed = await ReadUntil(
            events.Reader,
            workEvent => workEvent.EventType == "worker.completed");
        var receivedQueued = await TryReadUntil(
            events.Reader,
            workEvent => workEvent.EventType == "worker.queued",
            TimeSpan.FromMilliseconds(250));

        Assert.Equal(handle.WorkerId, completed.WorkerId);
        Assert.False(receivedQueued);
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

        var handle = await Session(system).Queue.Enqueue("signalr.view");
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
        Assert.Equal(handle.WorkerId, completed.WorkerId);
    }

    [Fact]
    public async Task EventWatcherFiltersByDefinitionAndKey()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
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
        var receivedIgnored = await TryReadUntil(
            events.Reader,
            workEvent => workEvent.WorkerId == ignored.WorkerId,
            TimeSpan.FromMilliseconds(250));

        Assert.Equal(accepted.WorkerId, completed.WorkerId);
        Assert.False(receivedIgnored);
    }

    [Fact]
    public async Task EventWatcherReceivesBurstsAsBatches()
    {
        using var host = await CreateHost(addSignalR: true, configureSignalR: options =>
        {
            options.EventBatchWindow = TimeSpan.FromMilliseconds(100);
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
        var handles = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => session.Queue.Enqueue("signalr.view")));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        await Task.WhenAll(handles.Select(handle => handle.WaitForCompletion()));

        var batch = await ReadUntil(
            batches.Reader,
            batch => batch.Events.Count >= 2);

        Assert.All(batch.Events, workEvent => Assert.Equal("worker.completed", workEvent.EventType));
    }

    [Fact]
    public async Task ViewWatcherReceivesRequestedOverviewComponentsOnly()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        connection.On<WorkComponentQueryResult>(
            WorkableRealtimeClientMethods.ViewUpdated,
            view => views.Writer.TryWrite(view));
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
            "overview",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest("system", "system"),
                new WorkComponentRequest("workers", "workers", Shape: WorkComponentShapes.Compact),
            ]),
            null);

        var initial = await ReadUntil(views.Reader, view => view.Components.ContainsKey("workers"));

        var handle = await Session(system).Queue.Enqueue("signalr.view");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        var completion = await handle.WaitForCompletion();
        Assert.True(completion.IsCompletedSuccessfully);

        var updated = await ReadUntil(views.Reader, view => view.GeneratedAt > initial.GeneratedAt);
        var workers = Assert.IsType<JsonElement>(updated.Components["workers"].Data);

        Assert.Equal(["system", "workers"], initial.Components.Keys.Order().ToArray());
        Assert.Equal(["system", "workers"], updated.Components.Keys.Order().ToArray());
        Assert.Equal("compact", updated.Components["workers"].Shape);
        Assert.True(workers.TryGetProperty("activeWorkerCount", out _));
        Assert.False(workers.TryGetProperty("finalWorkerCount", out _));
    }

    [Fact]
    public async Task ViewWatcherContinuesPublishingOverviewThroughputWithoutReadModelChanges()
    {
        using var host = await CreateHost(addSignalR: true);
        await using var connection = CreateConnection(host);
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        connection.On<WorkComponentQueryResult>(
            WorkableRealtimeClientMethods.ViewUpdated,
            view => views.Writer.TryWrite(view));
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
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
        var updated = await ReadUntil(
            views.Reader,
            view => view.GeneratedAt > initial.GeneratedAt &&
                view.Components.ContainsKey("throughput"));

        Assert.Equal(["throughput"], updated.Components.Keys.ToArray());
    }

    [Fact]
    public async Task ViewWatcherReceivesDiagnosticsViewOnDiagnosticsInterval()
    {
        using var host = await CreateHost(addSignalR: true);
        await using var connection = CreateConnection(host);
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        connection.On<WorkComponentQueryResult>(
            WorkableRealtimeClientMethods.ViewUpdated,
            view => views.Writer.TryWrite(view));
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
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
        Assert.True(queue.TryGetProperty("rejectedWorkCount", out _));
        Assert.True(queue.TryGetProperty("hasRejectedWork", out _));
        Assert.True(queue.TryGetProperty("alertableRejectedWorkCount", out _));
        Assert.True(queue.TryGetProperty("hasAlertableRejectedWork", out _));
        Assert.Equal("compact", updated.Components["readModelDiagnostics"].Shape);
        Assert.True(diagnostics.TryGetProperty("pendingUpdateCount", out _));
        Assert.True(diagnostics.TryGetProperty("isReadModelBehind", out _));
        Assert.True(diagnostics.TryGetProperty("readModelLagWarningThreshold", out _));
        Assert.Equal("compact", updated.Components["retentionDiagnostics"].Shape);
        Assert.True(retention.TryGetProperty("scheduledPurgeCount", out _));
        Assert.True(retention.TryGetProperty("isRetentionBehind", out _));
        Assert.True(retention.TryGetProperty("retentionLagWarningSeconds", out _));
        Assert.Equal("compact", updated.Components["concurrencyDiagnostics"].Shape);
        Assert.True(concurrency.TryGetProperty("deferredStartCount", out _));
        Assert.True(concurrency.TryGetProperty("lastDrainReleasedCount", out _));
        Assert.True(concurrency.TryGetProperty("isConcurrencyBehind", out _));
        Assert.True(concurrency.TryGetProperty("concurrencyLagWarningSeconds", out _));
        Assert.Equal("compact", updated.Components["durabilityDiagnostics"].Shape);
        Assert.True(durability.TryGetProperty("acceptedWaiterCount", out _));
        Assert.True(durability.TryGetProperty("pendingCleanupCount", out _));
        Assert.True(durability.TryGetProperty("hasReaderFailure", out _));
        Assert.Equal("compact", updated.Components["idempotencyDiagnostics"].Shape);
        Assert.True(idempotency.TryGetProperty("duplicateRejectionCount", out _));
        Assert.True(idempotency.TryGetProperty("lastDuplicateRejectedStorage", out _));
    }

    [Fact]
    public async Task DiagnosticsAlertChangeWatcherDoesNotReceiveHealthyTimerPayloads()
    {
        using var host = await CreateHost(addSignalR: true);
        await using var connection = CreateConnection(host);
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        connection.On<WorkComponentQueryResult>(
            WorkableRealtimeClientMethods.ViewUpdated,
            view => views.Writer.TryWrite(view));
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
            "diagnostics",
            new WorkViewCriteria(Components:
            [
                new WorkComponentRequest(
                    "readModelDiagnostics",
                    "readModelDiagnostics",
                    JsonSerializer.SerializeToElement(new
                    {
                        publishMode = "alertChanges",
                        warningThreshold = 100,
                    }),
                    WorkComponentShapes.Compact),
            ]),
            null);

        var initial = await ReadUntil(
            views.Reader,
            view => view.Components.ContainsKey("readModelDiagnostics"));
        var receivedHealthyTick = await TryReadUntil(
            views.Reader,
            view => view.GeneratedAt > initial.GeneratedAt,
            TimeSpan.FromMilliseconds(250));

        Assert.False(receivedHealthyTick);
    }

    [Fact]
    public async Task DiagnosticsAlertChangeWatcherDoesNotReceiveAlertPayloadWhenRejectedWorkIsNotAlertable()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        await using var connection = CreateConnection(host);
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        connection.On<WorkComponentQueryResult>(
            WorkableRealtimeClientMethods.ViewUpdated,
            view => views.Writer.TryWrite(view));
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
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

        var rejected = await Session(system).Queue.Enqueue("signalr.missing");
        Assert.False(rejected.QueueOutcome.IsAccepted);

        var receivedAlert = await TryReadUntil(
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
            },
            TimeSpan.FromMilliseconds(250));

        Assert.False(receivedAlert);
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
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        connection.On<WorkComponentQueryResult>(
            WorkableRealtimeClientMethods.ViewUpdated,
            view => views.Writer.TryWrite(view));
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
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
        Assert.Equal(1, data.GetProperty("alertableRejectedWorkCount").GetInt64());
        Assert.Equal("workable.system.capacity_reached", data.GetProperty("lastAlertableRejectedCode").GetString());
    }

    [Fact]
    public async Task DiagnosticsAlertChangeWatcherReceivesAlertPayloadWhenReadModelFallsBehind()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        var views = Channel.CreateUnbounded<WorkComponentQueryResult>();
        connection.On<WorkComponentQueryResult>(
            WorkableRealtimeClientMethods.ViewUpdated,
            view => views.Writer.TryWrite(view));
        await connection.StartAsync();
        await connection.InvokeAsync(
            "WatchView",
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
            await Eventually(() => system.Diagnostics.ReadModel.PendingUpdateCount >= 1);

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

    private static async Task<IHost> CreateHost(
        bool addSignalR,
        string? hubPath = null,
        Action<WorkableSignalROptions>? configureSignalR = null,
        Action<IWorkSystemBuilder>? configureWorkable = null,
        bool authenticated = true)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddTransportTestAuthorization();
                    services.AddSingleton<SignalRWorkGate>();
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
                            context.User = CreateTransportPrincipal();
                            await next();
                        });
                    }

                    app.UseRouting();
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

    private static HubConnection CreateConnection(IHost host)
        => new HubConnectionBuilder()
            .WithUrl(
                "http://localhost/workable/realtime",
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => host.GetTestServer().CreateHandler();
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

    private static async Task<bool> TryReadUntil<T>(
        ChannelReader<T> reader,
        Func<T, bool> predicate,
        TimeSpan timeout)
    {
        using var cancellation = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var item in reader.ReadAllAsync(cancellation.Token))
            {
                if (predicate(item))
                {
                    return true;
                }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            return false;
        }

        return false;
    }

    private static async Task Eventually(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.True(condition(), "Expected condition to become true.");
    }

    private static IWorkSystemSession Session(IWorkSystem system)
        => TransportAuthorizationTestSupport.CreateTransportSession(
            system,
            WorkInvocationChannel.DotNet,
            description: "Use SignalR test session.");

    private static ClaimsPrincipal CreateTransportPrincipal()
        => TransportAuthorizationTestSupport.CreateTransportPrincipal(
            id: "signalr-user-1",
            name: "SignalR User",
            email: "signalr.user@example.test");

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
}
