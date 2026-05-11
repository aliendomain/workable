using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Workable;

namespace Workable.Tests;

[Trait("Category", "HttpApi")]
public sealed class WorkableHttpApiTests
{
    [Fact]
    public async Task HttpApiReturnsAfterAcceptedByDefault()
    {
        var (system, http) = CreateHost(WorkDefinition.Create("http.default"), (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Success(input is null ? WorkOutput.Empty : WorkOutput.FromData(input))));
        await system.Start();

        using var input = JsonDocument.Parse("""{"id":"123"}""");
        var result = await http.Queue("http.default", new WorkableHttpWorkRequest(input.RootElement));

        Assert.Equal(WorkableHttpWorkStatus.Accepted, result.Status);
        Assert.True(result.QueueOutcome.IsAccepted);
        Assert.NotNull(result.WorkerId);
        Assert.Null(result.Completion);
        Assert.Null(result.Output);
    }

    [Fact]
    public async Task HttpApiCanWaitForCompletionWhenRequested()
    {
        var (system, http) = CreateHost(WorkDefinition.Create("http.wait"), (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Success(input is null ? WorkOutput.Empty : WorkOutput.FromData(input))));
        await system.Start();

        using var input = JsonDocument.Parse("""{"id":"123"}""");
        var result = await http.Queue(
            "http.wait",
            new WorkableHttpWorkRequest(input.RootElement, WorkableHttpCompletion.WaitForCompletion));

        Assert.Equal(WorkableHttpWorkStatus.Completed, result.Status);
        Assert.Equal("""{"id":"123"}""", result.Output?.Json);
    }

    [Fact]
    public async Task HttpApiCanQueueByDefinitionId()
    {
        var definition = WorkDefinition.Create("http.by-id");
        var (system, http) = CreateHost(definition, (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Success(input is null ? WorkOutput.Empty : WorkOutput.FromData(input))));
        await system.Start();

        using var input = JsonDocument.Parse("""{"id":"by-id"}""");
        var result = await http.Queue(
            definition.Id,
            new WorkableHttpWorkRequest(input.RootElement, WorkableHttpCompletion.WaitForCompletion));

        Assert.Equal(WorkableHttpWorkStatus.Completed, result.Status);
        Assert.Equal(definition.Id, result.QueueOutcome.DefinitionId);
        Assert.Equal("""{"id":"by-id"}""", result.Output?.Json);
    }

    [Fact]
    public async Task HttpApiQueueRequestCanProvideOptionsAndInputMetadata()
    {
        var definition = WorkDefinition.Create(
            "http.metadata",
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        var (system, http) = CreateHost(definition, SuccessfulWork);
        await system.Start();

        using var input = JsonDocument.Parse("""{"id":"metadata"}""");
        var result = await http.Queue(
            "http.metadata",
            new WorkableHttpWorkRequest(
                input.RootElement,
                Options: new WorkerOptions(ProfilingEnabled: true),
                SubjectId: new WorkSubjectId("user", "123"),
                ConcurrencyKey: new WorkConcurrencyKey("tenant", "abc"),
                Identifiers: new HashSet<WorkIdentifier> { new("invoice", "456") }));
        var worker = await system.Query.GetWorker(result.WorkerId ?? throw new InvalidOperationException("Expected worker id."));

        Assert.Equal(WorkableHttpWorkStatus.Accepted, result.Status);
        Assert.NotNull(worker);
        Assert.True(worker.Options.ProfilingEnabled);
        Assert.Equal(new WorkSubjectId("user", "123"), worker.SubjectId);
        Assert.Equal(new WorkConcurrencyKey("tenant", "abc"), worker.ConcurrencyKey);
        Assert.Contains(new WorkIdentifier("invoice", "456"), worker.Identifiers);
    }

    [Fact]
    public async Task HttpApiRejectsWorkWhenChannelIsNotAllowed()
    {
        var definition = WorkDefinition.Create(
            "dotnet.only",
            configuration: WorkConfiguration.Default with
            {
                Invocation = WorkInvocationConfiguration.Allow(WorkInvocationChannel.DotNet),
            });
        var (system, http) = CreateHost(definition, SuccessfulWork);
        await system.Start();

        var result = await http.Queue("dotnet.only");

        Assert.Equal(WorkableHttpWorkStatus.Rejected, result.Status);
        Assert.Contains(result.Messages, message => message.Code == "workable.invocation.channel_not_allowed");
    }

    [Fact]
    public async Task HttpApiCanReturnAfterAccepted()
    {
        var definition = WorkDefinition.Create(
            "manual.http",
            configuration: WorkConfiguration.Default with { Start = WorkStartConfiguration.DoNotStart });
        var (system, http) = CreateHost(definition, SuccessfulWork);
        await system.Start();

        var result = await http.Queue(
            "manual.http",
            new WorkableHttpWorkRequest(Completion: WorkableHttpCompletion.ReturnAfterAccepted));

        Assert.Equal(WorkableHttpWorkStatus.Accepted, result.Status);
        Assert.True(result.QueueOutcome.IsAccepted);
        Assert.Null(result.Completion);
    }

    [Fact]
    public async Task HttpApiExposesQueryMethods()
    {
        var (system, http) = CreateHost(builder =>
        {
            builder.AddWork(WorkDefinition.Create("http.query.one", category: "Http"), SuccessfulWork);
            builder.AddWork(WorkDefinition.Create("http.query.two", category: "Http"), SuccessfulWork);
        });
        await system.Start();

        var handle = await system.Queue.Enqueue("http.query.one", WorkInput.Empty.WithIdentifier(new WorkIdentifier("batch", "1")));
        var completion = await handle.WaitForCompletion();
        Assert.True(completion.IsCompletedSuccessfully);

        var workerId = handle.WorkerId ?? throw new InvalidOperationException("Expected worker id.");
        var worker = await http.GetWorker(workerId);
        var workers = await http.QueryWorkers(new WorkerQuery(Identifier: new WorkIdentifier("batch", "1")));
        var byName = await http.GetWorkInfo("http.query.one");
        var byId = await http.GetWorkInfo(byName?.Definition.Id ?? throw new InvalidOperationException("Expected work info."));
        var definitions = await http.QueryWorkDefinitions(new WorkDefinitionQuery(Category: "Http"));
        var summary = await http.GetWorkerStatusSummary(new WorkerQuery(DefinitionName: "http.query.one"));
        var systemSummary = await http.GetWorkerStatusSummary();

        Assert.NotNull(worker);
        Assert.Single(workers.Workers);
        Assert.NotNull(byName);
        var requiredByName = byName;
        Assert.Equal("http.query.one", requiredByName.Definition.Name);
        Assert.Equal(requiredByName.Definition.Id, byId?.Definition.Id);
        Assert.Equal(2, definitions.Count);
        Assert.Equal(1, summary.Total);
        Assert.Equal(1, summary.Counts[WorkerState.Completed]);
        Assert.Equal(1, systemSummary.Total);
        Assert.Equal(1, systemSummary.Counts[WorkerState.Completed]);
    }

    [Fact]
    public async Task HttpApiCanExecuteWorkerActions()
    {
        var definition = WorkDefinition.Create(
            "http.action",
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        var (system, http) = CreateHost(definition, SuccessfulWork);
        await system.Start();

        var queue = await http.Queue("http.action");
        var worker = await http.GetWorker(queue.WorkerId ?? throw new InvalidOperationException("Expected worker id."));
        var outcome = await http.Execute(
            worker!.Id,
            WorkAction.Cancel,
            new WorkableHttpWorkerActionRequest(worker.Revision));
        var canceled = await http.GetWorker(worker.Id);

        Assert.True(outcome.IsAccepted);
        Assert.Equal(WorkerState.Canceled, canceled?.State);
    }

    [Fact]
    public async Task HttpApiCanReconfigureWorker()
    {
        var definition = WorkDefinition.Create(
            "http.reconfigure",
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        var (system, http) = CreateHost(definition, SuccessfulWork);
        await system.Start();

        var queue = await http.Queue("http.reconfigure");
        var worker = await http.GetWorker(queue.WorkerId ?? throw new InvalidOperationException("Expected worker id."));
        var outcome = await http.Reconfigure(
            worker!.Id,
            new WorkableHttpWorkerReconfigurationRequest(
                worker.Revision,
                new WorkerReconfiguration(ProfilingEnabled: true)));

        Assert.True(outcome.IsAccepted);
        Assert.True(outcome.Worker?.Options.ProfilingEnabled);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("CANCEL")]
    [InlineData("CaNcEl")]
    public void HttpActionRouteBindingParsesActionsCaseInsensitively(string action)
    {
        Assert.True(WorkableHttpRouteBinding.TryParseAction(action, out var parsed));
        Assert.Equal(WorkAction.Cancel, parsed);
    }

    [Fact]
    public async Task MappedHttpRoutesAndEnumJsonAreCaseInsensitive()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;

        var queueResponse = await client.PostAsJsonAsync(
            "/WORKABLE/WORK/http.route.case",
            new
            {
                completion = "returnafteraccepted",
            });
        queueResponse.EnsureSuccessStatusCode();
        var queueJson = JsonNode.Parse(await queueResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var workerId = Guid.Parse(queueJson["workerId"]?["value"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected worker id."));
        var worker = await system.Query.GetWorker(new WorkerId(workerId))
            ?? throw new InvalidOperationException("Expected worker.");
        await using var actionSubscription = system.Events.Subscribe(new WorkEventFilter(WorkerId: worker.Id, EventType: "worker.cancel"));
        await using var actionReader = actionSubscription.Read().GetAsyncEnumerator();

        Assert.Equal(WorkInvocationChannel.HttpApi, worker.Origin.Channel);
        Assert.Equal("user-123", worker.Origin.Actor.Id);
        Assert.Equal("greya@example.test", worker.Origin.Actor.Email);
        Assert.Contains("/WORKABLE/WORK/http.route.case", worker.Origin.Url, StringComparison.OrdinalIgnoreCase);

        var actionResponse = await client.PostAsJsonAsync(
            $"/WORKABLE/WORKERS/{workerId:D}/ACTIONS/cancel",
            new
            {
                revision = worker.Revision,
            });
        actionResponse.EnsureSuccessStatusCode();
        var summaryResponse = await client.GetAsync("/workable/workers/status-summary");
        summaryResponse.EnsureSuccessStatusCode();
        var summaryJson = JsonNode.Parse(await summaryResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");

        var canceled = await system.Query.GetWorker(new WorkerId(workerId));
        var actionEvent = await ReadNext(actionReader);
        Assert.Equal(WorkerState.Canceled, canceled?.State);
        Assert.Equal(WorkInvocationChannel.HttpApi, actionEvent.Origin?.Channel);
        Assert.Equal("user-123", actionEvent.Origin?.Actor.Id);
        Assert.Contains("/WORKABLE/WORKERS/", actionEvent.Origin?.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, summaryJson["total"]?.GetValue<int>());
    }

    [Fact]
    public async Task MappedHttpQueueByDefinitionIdRecordsHttpOrigin()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        Assert.True(system.Catalog.TryGet("http.route.case", out var definition));

        var response = await client.PostAsJsonAsync(
            $"/workable/definitions/{definition.Id.Value:D}/queue",
            new
            {
                completion = "returnAfterAccepted",
            });
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var workerId = Guid.Parse(json["workerId"]?["value"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected worker id."));

        var worker = await system.Query.GetWorker(new WorkerId(workerId))
            ?? throw new InvalidOperationException("Expected worker.");

        Assert.Equal(WorkInvocationChannel.HttpApi, worker.Origin.Channel);
        Assert.Equal("user-123", worker.Origin.Actor.Id);
        Assert.Equal("greya@example.test", worker.Origin.Actor.Email);
        Assert.Contains($"/workable/definitions/{definition.Id.Value:D}/queue", worker.Origin.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappedHttpAnonymousRequestRecordsUnknownActor()
    {
        using var host = await CreateHttpHost(authenticated: false);
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;

        var response = await client.PostAsJsonAsync(
            "/workable/work/http.route.case",
            new
            {
                completion = "returnAfterAccepted",
            });
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var workerId = Guid.Parse(json["workerId"]?["value"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected worker id."));

        var worker = await system.Query.GetWorker(new WorkerId(workerId))
            ?? throw new InvalidOperationException("Expected worker.");

        Assert.Equal(WorkInvocationChannel.HttpApi, worker.Origin.Channel);
        Assert.Equal(WorkActor.Unknown, worker.Origin.Actor);
    }

    [Fact]
    public async Task MappedHttpReconfigureEventUsesHttpOrigin()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;

        var queueResponse = await client.PostAsJsonAsync(
            "/workable/work/http.route.case",
            new
            {
                completion = "returnAfterAccepted",
            });
        queueResponse.EnsureSuccessStatusCode();
        var queueJson = JsonNode.Parse(await queueResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var workerId = Guid.Parse(queueJson["workerId"]?["value"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected worker id."));
        var worker = await system.Query.GetWorker(new WorkerId(workerId))
            ?? throw new InvalidOperationException("Expected worker.");
        await using var reconfigureSubscription = system.Events.Subscribe(new WorkEventFilter(WorkerId: worker.Id, EventType: "worker.reconfigured"));
        await using var reconfigureReader = reconfigureSubscription.Read().GetAsyncEnumerator();

        var reconfigureResponse = await client.PostAsJsonAsync(
            $"/workable/workers/{workerId:D}/reconfigure",
            new
            {
                revision = worker.Revision,
                changes = new
                {
                    profilingEnabled = true,
                },
            });
        reconfigureResponse.EnsureSuccessStatusCode();

        var reconfigureEvent = await ReadNext(reconfigureReader);

        Assert.Equal(WorkInvocationChannel.HttpApi, reconfigureEvent.Origin?.Channel);
        Assert.Equal("user-123", reconfigureEvent.Origin?.Actor.Id);
        Assert.Contains($"/workable/workers/{workerId:D}/reconfigure", reconfigureEvent.Origin?.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappedHttpRoutesCanTargetNamedSystem()
    {
        using var host = await CreateMultiSystemHttpHost();
        var client = host.GetTestClient();
        var registry = host.Services.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(registry.TryGet("background", out var background));

        var definitionsResponse = await client.GetAsync("/workable/systems/background/definitions");
        definitionsResponse.EnsureSuccessStatusCode();
        var definitionsJson = await definitionsResponse.Content.ReadAsStringAsync();

        Assert.Contains("http.named", definitionsJson);
        Assert.DoesNotContain("http.default", definitionsJson);

        var queueResponse = await client.PostAsJsonAsync(
            "/workable/systems/background/work/http.named",
            new
            {
                completion = "returnAfterAccepted",
            });
        queueResponse.EnsureSuccessStatusCode();
        var queueJson = JsonNode.Parse(await queueResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var workerId = Guid.Parse(queueJson["workerId"]?["value"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected worker id."));

        var worker = await background.Query.GetWorker(new WorkerId(workerId))
            ?? throw new InvalidOperationException("Expected worker.");

        Assert.Equal("http.named", worker.DefinitionName);
        Assert.Equal(WorkInvocationChannel.HttpApi, worker.Origin.Channel);
        Assert.Contains("/workable/systems/background/work/http.named", worker.Origin.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappedHttpNamedSystemWorkerOperationsDoNotFallBackToDefaultSystem()
    {
        using var host = await CreateMultiSystemHttpHost();
        var client = host.GetTestClient();
        var registry = host.Services.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(registry.TryGet("background", out var background));

        var queueResponse = await client.PostAsJsonAsync(
            "/workable/systems/background/work/http.named",
            new
            {
                completion = "returnAfterAccepted",
            });
        queueResponse.EnsureSuccessStatusCode();
        var queueJson = JsonNode.Parse(await queueResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var workerId = Guid.Parse(queueJson["workerId"]?["value"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected worker id."));

        var defaultGetResponse = await client.GetAsync($"/workable/workers/{workerId:D}");
        var namedGetResponse = await client.GetAsync($"/workable/systems/background/workers/{workerId:D}");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, defaultGetResponse.StatusCode);
        namedGetResponse.EnsureSuccessStatusCode();
        var namedWorkerJson = await namedGetResponse.Content.ReadAsStringAsync();
        Assert.Contains("http.named", namedWorkerJson);

        var worker = await background.Query.GetWorker(new WorkerId(workerId))
            ?? throw new InvalidOperationException("Expected worker.");
        var defaultActionResponse = await client.PostAsJsonAsync(
            $"/workable/workers/{workerId:D}/actions/cancel",
            new
            {
                revision = worker.Revision,
            });

        Assert.Equal(System.Net.HttpStatusCode.NotFound, defaultActionResponse.StatusCode);

        var reconfigureResponse = await client.PostAsJsonAsync(
            $"/workable/systems/background/workers/{workerId:D}/reconfigure",
            new
            {
                revision = worker.Revision,
                changes = new
                {
                    profilingEnabled = true,
                },
            });
        reconfigureResponse.EnsureSuccessStatusCode();
        worker = await background.Query.GetWorker(new WorkerId(workerId))
            ?? throw new InvalidOperationException("Expected worker.");
        Assert.True(worker.Options.ProfilingEnabled);

        var actionResponse = await client.PostAsJsonAsync(
            $"/workable/systems/background/workers/{workerId:D}/actions/cancel",
            new
            {
                revision = worker.Revision,
            });
        actionResponse.EnsureSuccessStatusCode();

        var canceled = await background.Query.GetWorker(new WorkerId(workerId))
            ?? throw new InvalidOperationException("Expected worker.");
        Assert.Equal(WorkerState.Canceled, canceled.State);
        Assert.Equal(WorkInvocationChannel.HttpApi, canceled.ActionHistory.Last().Origin.Channel);
        Assert.Contains("/workable/systems/background/workers/", canceled.ActionHistory.Last().Origin.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappedHttpNamedSystemRouteReturnsNotFoundForUnknownSystem()
    {
        using var host = await CreateMultiSystemHttpHost();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/workable/systems/missing/definitions");

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("workable.http.system.not_found", json);
    }

    private static (IWorkSystem System, WorkableHttpWorkService Http) CreateHost(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute)
        => CreateHost(builder => builder.AddWork(definition, execute));

    private static (IWorkSystem System, WorkableHttpWorkService Http) CreateHost(
        Action<IWorkSystemBuilder> configure)
    {
        var provider = new ServiceCollection()
            .AddWorkableSystem(configure)
            .AddWorkableHttpApi()
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        return (system, provider.GetRequiredService<WorkableHttpWorkService>());
    }

    private static async Task<IHost> CreateHttpHost(bool authenticated = true)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.AddWork(
                            WorkDefinition.Create(
                                "http.route.case",
                                configuration: WorkConfiguration.Default with
                                {
                                    Start = WorkStartConfiguration.DoNotStart,
                                }),
                            SuccessfulWork);
                    });
                    services.AddWorkableHttpApi();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    if (authenticated)
                    {
                        app.Use(async (context, next) =>
                        {
                            context.User = new ClaimsPrincipal(new ClaimsIdentity(
                                [
                                    new Claim(ClaimTypes.NameIdentifier, "user-123"),
                                    new Claim(ClaimTypes.Name, "Greya"),
                                    new Claim(ClaimTypes.Email, "greya@example.test"),
                                ],
                                "Test"));
                            await next();
                        });
                    }

                    app.UseEndpoints(endpoints => endpoints.MapWorkableApi("/workable"));
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> CreateMultiSystemHttpHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.AddWork(
                            WorkDefinition.Create(
                                "http.default",
                                configuration: WorkConfiguration.Default with
                                {
                                    Start = WorkStartConfiguration.DoNotStart,
                                }),
                            SuccessfulWork);
                    });
                    services.AddWorkableSystem("background", builder =>
                    {
                        builder.StartWithHost();
                        builder.AddWork(
                            WorkDefinition.Create(
                                "http.named",
                                configuration: WorkConfiguration.Default with
                                {
                                    Start = WorkStartConfiguration.DoNotStart,
                                }),
                            SuccessfulWork);
                    });
                    services.AddWorkableHttpApi();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapWorkableApi("/workable"));
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private static async Task<WorkEvent> ReadNext(IAsyncEnumerator<WorkEvent> reader)
    {
        var hasEvent = await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(hasEvent);
        return reader.Current;
    }
}
