using System.Net.Http.Json;
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
        Assert.Contains("system-dashboard", capabilities.Realtime.Features ?? []);
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
    public async Task DashboardWatcherReceivesInitialAndCoalescedOverviewUpdates()
    {
        using var host = await CreateHost(addSignalR: true);
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var gate = host.Services.GetRequiredService<SignalRWorkGate>();
        await using var connection = CreateConnection(host);
        var dashboards = Channel.CreateUnbounded<WorkableRealtimeDashboard>();
        connection.On<WorkableRealtimeDashboard>(
            WorkableRealtimeClientMethods.DashboardUpdated,
            dashboard => dashboards.Writer.TryWrite(dashboard));
        await connection.StartAsync();
        await connection.InvokeAsync("WatchDashboard", null);

        var initial = await ReadUntil(dashboards.Reader, dashboard => dashboard.CompletedIterationCount == 0);

        var handle = await system.Queue.Enqueue("signalr.dashboard");
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        gate.Release.SetResult();
        var completion = await handle.WaitForCompletion();
        Assert.True(completion.IsCompletedSuccessfully);

        var updated = await ReadUntil(dashboards.Reader, dashboard => dashboard.CompletedIterationCount == 1);

        Assert.Equal(system.Id, initial.SystemId);
        Assert.Equal(system.Id, updated.SystemId);
        Assert.Equal(WorkSystemState.Started, updated.SystemState);
        Assert.Equal(0, updated.DefinitionCount);
        Assert.Equal(0, updated.ActiveWorkerCount);
        Assert.Equal(1, updated.FinalWorkerCount);
        Assert.Equal(0, updated.FailedWorkerCount);
        Assert.Equal(1, updated.WorkerCountByState[WorkerState.Completed]);
        Assert.Equal(0, updated.FailedIterationCount);
        Assert.Equal(1, updated.IterationCountByStatus[WorkCompletionStatus.Completed]);
        Assert.Empty(updated.FailedWorkers);
        Assert.Equal("signalr.dashboard", Assert.Single(updated.CompletedIterations).DefinitionName);
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
                        builder.AddWork(WorkDefinition.Create("signalr.dashboard"), SuccessfulWork);
                    });
                    services.AddWorkableHttpApi();
                    if (addSignalR)
                    {
                        services.AddWorkableSignalR(options =>
                        {
                            options.DashboardPublishInterval = TimeSpan.FromMilliseconds(50);
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
