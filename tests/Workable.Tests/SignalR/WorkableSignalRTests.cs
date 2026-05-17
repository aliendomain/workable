using System.Net.Http.Json;
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
        var stream = Assert.IsType<WorkEventStream>(system.Events);
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
        connection.On<WorkableRealtimeEvent>(WorkableRealtimeClientMethods.WorkEvent, workEvent => events.Writer.TryWrite(workEvent));
        await connection.StartAsync();

        var handle = await system.Queue.Enqueue("signalr.worker");
        var workerId = handle.WorkerId ?? throw new InvalidOperationException("Expected worker id.");
        await connection.InvokeAsync("WatchWorker", workerId.Value.ToString("D"), null);

        var worker = await system.Query.Worker(workerId)
            ?? throw new InvalidOperationException("Expected worker.");
        var start = await system.Workers.Execute(worker.Version, WorkAction.Start);
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

        var handle = await system.Queue.Enqueue("signalr.view");
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
                    "readModelDiagnostics",
                    "readModelDiagnostics",
                    JsonSerializer.SerializeToElement(new { warningThreshold = 100 }),
                    WorkComponentShapes.Compact),
            ]),
            null);

        var initial = await ReadUntil(
            views.Reader,
            view => view.Components.ContainsKey("readModelDiagnostics"));
        var updated = await ReadUntil(
            views.Reader,
            view => view.GeneratedAt > initial.GeneratedAt &&
                view.Components.ContainsKey("readModelDiagnostics"));
        var diagnostics = Assert.IsType<JsonElement>(updated.Components["readModelDiagnostics"].Data);

        Assert.Equal(["readModelDiagnostics"], updated.Components.Keys.ToArray());
        Assert.Equal("compact", updated.Components["readModelDiagnostics"].Shape);
        Assert.True(diagnostics.TryGetProperty("pendingUpdateCount", out _));
        Assert.True(diagnostics.TryGetProperty("isReadModelBehind", out _));
        Assert.True(diagnostics.TryGetProperty("readModelLagWarningThreshold", out _));
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

        try
        {
            var enqueueBurst = Task.Run(async () =>
            {
                for (var index = 0; index < 5_000; index++)
                {
                    _ = await system.Queue.Enqueue("signalr.view");
                }
            });

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

            await enqueueBurst.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(data.GetProperty("pendingUpdateCount").GetInt64() >= 1);
        }
        finally
        {
            if (!gate.Release.Task.IsCompleted)
            {
                gate.Release.SetResult();
            }
        }
    }

    private static async Task<IHost> CreateHost(bool addSignalR, string? hubPath = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<SignalRWorkGate>();
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.AddWork(
                            WorkDefinition.Create(
                                "signalr.worker",
                                configuration: WorkConfiguration.Default with
                                {
                                    Start = WorkStartConfiguration.DoNotStart,
                                }),
                            SuccessfulWork);
                        builder.AddWork(WorkDefinition.Create("signalr.view"), SuccessfulWork);
                    });
                    services.AddWorkableHttpApi();
                    if (addSignalR)
                    {
                        services.AddWorkableSignalR(options =>
                        {
                            options.PublishInterval = TimeSpan.FromMilliseconds(50);
                            options.DiagnosticsPublishInterval = TimeSpan.FromMilliseconds(50);
                        });
                    }
                });
                web.Configure(app =>
                {
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
