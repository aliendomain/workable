using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Workable.PerformanceHarness;

internal sealed class TransportBenchmarkHost : IAsyncDisposable
{
    private static readonly TimeSpan SetupTimeout = TimeSpan.FromSeconds(5);
    private readonly IHost host;

    private TransportBenchmarkHost(IHost host)
    {
        this.host = host;
        this.Client = host.GetTestClient();
        this.Router = host.Services.GetRequiredService<WorkableMcpToolRouter>();
        this.System = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        this.Gates = host.Services.GetRequiredService<TransportBenchmarkGates>();
    }

    public HttpClient Client { get; }

    public WorkableMcpToolRouter Router { get; }

    public IWorkSystem System { get; }

    public TransportBenchmarkGates Gates { get; }

    public static async Task<TransportBenchmarkHost> Create(
        bool requiresAuthorization = true,
        CancellationToken cancellationToken = default)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddAuthorization();
                    if (requiresAuthorization)
                    {
                        services.AddAuthentication(BenchmarkAuthenticationHandler.SchemeName)
                            .AddScheme<AuthenticationSchemeOptions, BenchmarkAuthenticationHandler>(
                                BenchmarkAuthenticationHandler.SchemeName,
                                static _ => { });
                        services.Configure<WorkableAspNetCoreAuthorizationOptions>(options =>
                        {
                            options.TransportAuthenticationScheme = BenchmarkAuthenticationHandler.SchemeName;
                        });
                    }
                    services.AddSingleton<TransportBenchmarkGates>();
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization(requiresAuthorization);
                        if (requiresAuthorization)
                        {
                            builder.ConfigureAuthorization(authorization => authorization
                                .AllowControlSystemToGroups(WorkableBenchmarkSystem.OperatorGroup)
                                .AllowDiagnosticsToGroups(WorkableBenchmarkSystem.OperatorGroup)
                                .AllowBuiltInHttpApiToGroups(WorkableBenchmarkSystem.OperatorGroup)
                                .AllowReadAllWorkToGroups(WorkableBenchmarkSystem.OperatorGroup)
                                .AllowOperateAllWorkToGroups(WorkableBenchmarkSystem.OperatorGroup));
                        }
                        builder.AddWork(
                            WorkDefinition.Create(
                                "perf.transport.queued",
                                category: "Perf:Transport",
                                configuration: WorkConfiguration.Default with
                                {
                                    Start = WorkStartConfiguration.DoNotStart,
                                }),
                            CompletedWork,
                            configure: null,
                            authorize: requiresAuthorization ? AllowOperatorGroups : null);
                        builder.AddWork(
                            WorkDefinition.Create("perf.transport.running", category: "Perf:Transport"),
                            async (context, input, token) =>
                            {
                                var gates = context.Services.GetRequiredService<TransportBenchmarkGates>();
                                gates.WorkerStarted.TrySetResult();
                                await gates.WorkerRelease.Task.WaitAsync(token);
                                return WorkExecutionResult.Success();
                            },
                            configure: null,
                            authorize: requiresAuthorization ? AllowOperatorGroups : null);
                        builder.AddWork(
                            WorkDefinition.Create("perf.transport.workflow.fast", category: "Perf:Transport"),
                            CompletedWork,
                            configure: null,
                            authorize: requiresAuthorization ? AllowOperatorGroups : null);
                        builder.AddWork(
                            WorkDefinition.Create("perf.transport.workflow.stop.slow", category: "Perf:Transport"),
                            async (context, input, token) =>
                            {
                                var gates = context.Services.GetRequiredService<TransportBenchmarkGates>();
                                gates.StopWorkflowChildStarted.TrySetResult();
                                await gates.StopWorkflowRelease.Task.WaitAsync(token);
                                return WorkExecutionResult.Success();
                            },
                            configure: null,
                            authorize: requiresAuthorization ? AllowOperatorGroups : null);
                        builder.AddWork(
                            WorkDefinition.Create("perf.transport.workflow.stop.fast", category: "Perf:Transport"),
                            (context, input, token) =>
                            {
                                context.Services.GetRequiredService<TransportBenchmarkGates>().FastWorkflowRuns++;
                                return Task.FromResult(WorkExecutionResult.Success());
                            },
                            configure: null,
                            authorize: requiresAuthorization ? AllowOperatorGroups : null);
                        builder.AddWork(
                            WorkDefinition.Create("perf.transport.workflow.cancel.child", category: "Perf:Transport"),
                            async (context, input, token) =>
                            {
                                var gates = context.Services.GetRequiredService<TransportBenchmarkGates>();
                                gates.CancelWorkflowChildStarted.TrySetResult();
                                await gates.CancelWorkflowRelease.Task.WaitAsync(token);
                                return WorkExecutionResult.Success();
                            },
                            configure: null,
                            authorize: requiresAuthorization ? AllowOperatorGroups : null);
                        builder.AddWorkflow(
                            WorkflowDefinition.Create("perf.transport.workflow.fast"),
                            workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("perf.transport.workflow.fast")),
                            authorize: requiresAuthorization ? AllowWorkflowOperatorGroups : null);
                        builder.AddWorkflow(
                            WorkflowDefinition.Create("perf.transport.workflow.stop"),
                            workflow => workflow
                                .DispatchWork("slow", WorkDefinition.Create("perf.transport.workflow.stop.slow"))
                                .Join("join")
                                .DispatchWork("fast", WorkDefinition.Create("perf.transport.workflow.stop.fast")),
                            authorize: requiresAuthorization ? AllowWorkflowOperatorGroups : null);
                        builder.AddWorkflow(
                            WorkflowDefinition.Create("perf.transport.workflow.cancel"),
                            workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("perf.transport.workflow.cancel.child")),
                            authorize: requiresAuthorization ? AllowWorkflowOperatorGroups : null);
                    });
                    if (requiresAuthorization)
                    {
                        services.AddWorkableHttpApi();
                    }
                    services.AddWorkableSignalR(options =>
                    {
                        options.PublishInterval = TimeSpan.FromMilliseconds(25);
                        options.DiagnosticsPublishInterval = TimeSpan.FromMilliseconds(25);
                    });
                    services.AddWorkableMcpServer();
                });
                web.Configure(app =>
                {
                    if (requiresAuthorization)
                    {
                        app.Use(async (context, next) =>
                        {
                            context.User = BenchmarkAuthenticationHandler.CreatePrincipal();
                            await next();
                        });
                    }
                    app.UseRouting();
                    if (requiresAuthorization)
                    {
                        app.UseAuthentication();
                    }
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        if (requiresAuthorization)
                        {
                            endpoints.MapWorkableApi("/workable");
                        }
                        endpoints.MapWorkableSignalR("/workable/realtime");
                    });
                });
            })
            .Build();

        await host.StartAsync(cancellationToken);
        return new TransportBenchmarkHost(host);
    }

    public WorkRequestContext CreateTransportRequestContext(string description)
        => BenchmarkRequestContexts.CreateOperator(description);

    public HubConnection CreateSignalRConnection()
        => new HubConnectionBuilder()
            .WithUrl(
                "http://localhost/workable/realtime",
                options =>
                {
                    options.Transports = HttpTransportType.LongPolling;
                    options.HttpMessageHandlerFactory = _ => this.host.GetTestServer().CreateHandler();
                })
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
            })
            .Build();

    public async Task<(WorkerId WorkerId, long Revision)> QueueHttpQueuedWorker(CancellationToken cancellationToken = default)
    {
        var response = await this.Client.PostAsJsonAsync(
            "/workable/work/perf.transport.queued",
            new
            {
                description = "Queue transport benchmark worker.",
                completion = "returnAfterAccepted",
            },
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var workerId = await ReadQueuedWorkerId(response, cancellationToken);
        var worker = await this.GetWorker(workerId, cancellationToken);
        return (worker.Id, worker.Revision);
    }

    public async Task<(WorkerId WorkerId, long Revision)> QueueDirectQueuedWorker(
        WorkInput? input = null,
        CancellationToken cancellationToken = default)
    {
        var handle = await this.System.CreateSession(this.CreateTransportRequestContext("Queue direct transport benchmark worker."))
            .Queue.Enqueue(
                "perf.transport.queued",
                input ?? WorkableBenchmarkSystem.CreateInput(Environment.TickCount & int.MaxValue),
                cancellationToken: cancellationToken);
        var worker = await this.GetWorker(
            handle.WorkerId ?? throw new InvalidOperationException("Expected queued transport worker id."),
            cancellationToken);
        return (worker.Id, worker.Revision);
    }

    public async Task<(WorkerId WorkerId, long Revision)> QueueDirectRunningWorker(
        WorkInput? input = null,
        CancellationToken cancellationToken = default)
    {
        var handle = await this.System.CreateSession(this.CreateTransportRequestContext("Queue direct running transport benchmark worker."))
            .Queue.Enqueue(
                "perf.transport.running",
                input ?? WorkableBenchmarkSystem.CreateInput(Environment.TickCount & int.MaxValue),
                cancellationToken: cancellationToken);
        var workerId = handle.WorkerId ?? throw new InvalidOperationException("Expected running transport worker id.");
        await this.Gates.WorkerStarted.Task.WaitAsync(SetupTimeout, cancellationToken);
        var worker = await this.GetWorker(workerId, cancellationToken);
        return (worker.Id, worker.Revision);
    }

    public async Task<IReadOnlyList<WorkerId>> SeedQueuedWorkers(
        int workerCount,
        int startIndex = 0,
        CancellationToken cancellationToken = default)
    {
        var session = this.System.CreateSession(this.CreateTransportRequestContext("Seed transport benchmark workers."));
        var workerIds = new List<WorkerId>(workerCount);
        var criteria = new WorkerCriteria(DefinitionName: "perf.transport.queued");
        for (var index = 0; index < workerCount; index++)
        {
            var handle = await session.Queue.Enqueue(
                "perf.transport.queued",
                WorkableBenchmarkSystem.CreateInput(startIndex + index),
                cancellationToken: cancellationToken);
            workerIds.Add(handle.WorkerId ?? throw new InvalidOperationException("Expected seeded worker id."));
        }

        var spins = 0;
        while (true)
        {
            var summary = await session.Query.WorkerStatusSummary(criteria, cancellationToken);
            if (summary.Total >= workerIds.Count)
            {
                break;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (spins++ < 10)
            {
                await Task.Yield();
            }
            else
            {
                await Task.Delay(1, cancellationToken);
            }
        }

        return workerIds;
    }

    public async Task<WorkflowRunId> StartHttpWorkflow(string workflowName, CancellationToken cancellationToken = default)
    {
        var response = await this.Client.PostAsJsonAsync(
            $"/workable/workflows/{workflowName}",
            new { completion = "returnAfterAccepted" },
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadWorkflowRunId(response, cancellationToken);
    }

    public async Task<WorkflowRunId> StartMcpWorkflow(string workflowName, CancellationToken cancellationToken = default)
    {
        using var arguments = JsonDocument.Parse($$"""{"name":"{{workflowName}}"}""");
        var result = await this.Router.CallTool(
            "workable_start_workflow",
            arguments.RootElement,
            options: null,
            systemName: null,
            requestContext: this.CreateTransportRequestContext("Start MCP benchmark workflow."),
            cancellationToken);
        if (result.IsError)
        {
            throw new InvalidOperationException(result.Json);
        }

        var json = JsonNode.Parse(result.Json)?.AsObject()
            ?? throw new InvalidOperationException("Expected MCP workflow start response.");
        return new WorkflowRunId(Guid.Parse(
            json["runId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected MCP workflow run id.")));
    }

    public WorkflowRunId StartDirectWorkflow(string workflowName, CancellationToken cancellationToken = default)
        => new(WorkflowBenchmarkReflection.Start(
            this.System,
            workflowName,
            this.CreateTransportRequestContext($"Start direct transport benchmark workflow '{workflowName}'."),
            cancellationToken));

    public async Task<WorkerSnapshot> GetWorker(WorkerId workerId, CancellationToken cancellationToken = default)
        => await this.System.CreateSession(this.CreateTransportRequestContext("Read transport benchmark worker."))
            .Query.Worker(workerId, cancellationToken)
            ?? throw new InvalidOperationException($"Expected worker '{workerId.Value:D}'.");

    public async Task<(WorkerId WorkerId, long Revision)> ReadWorkerVersion(
        string definitionName,
        CancellationToken cancellationToken = default)
    {
        var result = await this.System.CreateSession(this.CreateTransportRequestContext("Read transport benchmark worker version."))
            .Query.Workers(
                new WorkerCriteria(
                    DefinitionName: definitionName,
                    Take: 1),
                cancellationToken);
        var worker = result.Workers.SingleOrDefault()
            ?? throw new InvalidOperationException($"Expected worker for definition '{definitionName}'.");
        return (worker.Id, worker.Revision);
    }

    public async ValueTask DisposeAsync()
    {
        this.Client.Dispose();
        await this.host.StopAsync();
        this.host.Dispose();
    }

    private static async Task<WorkerId> ReadQueuedWorkerId(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))
            ?? throw new InvalidOperationException("Expected HTTP queue response.");
        var workerId = Guid.Parse(
            json["workerId"]?["value"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected worker id."));
        return new WorkerId(workerId);
    }

    private static async Task<WorkflowRunId> ReadWorkflowRunId(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))
            ?? throw new InvalidOperationException("Expected workflow response.");
        return new WorkflowRunId(Guid.Parse(
            json["runId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected workflow run id.")));
    }

    private static Task<WorkExecutionResult> CompletedWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private static void AllowOperatorGroups(IWorkAuthorizationBuilder authorization)
        => authorization.RequireGroups(
            [WorkableBenchmarkSystem.OperatorGroup],
            [WorkableBenchmarkSystem.OperatorGroup]);

    private static void AllowWorkflowOperatorGroups(IWorkAuthorizationBuilder authorization)
        => authorization.AllowOperateToGroups(WorkableBenchmarkSystem.OperatorGroup);
}

internal sealed class TransportBenchmarkGates
{
    public TaskCompletionSource WorkerStarted { get; private set; } = NewSignal();

    public TaskCompletionSource WorkerRelease { get; private set; } = NewSignal();

    public TaskCompletionSource StopWorkflowChildStarted { get; private set; } = NewSignal();

    public TaskCompletionSource StopWorkflowRelease { get; private set; } = NewSignal();

    public TaskCompletionSource CancelWorkflowChildStarted { get; private set; } = NewSignal();

    public TaskCompletionSource CancelWorkflowRelease { get; private set; } = NewSignal();

    public int FastWorkflowRuns;

    public void Reset()
    {
        this.WorkerStarted = NewSignal();
        this.WorkerRelease = NewSignal();
        this.StopWorkflowChildStarted = NewSignal();
        this.StopWorkflowRelease = NewSignal();
        this.CancelWorkflowChildStarted = NewSignal();
        this.CancelWorkflowRelease = NewSignal();
        this.FastWorkflowRuns = 0;
    }

    private static TaskCompletionSource NewSignal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
