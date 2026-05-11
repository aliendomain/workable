using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Mcp")]
public sealed class WorkableMcpTests
{
    [Fact]
    public void ToolDescriptorsUseDefinitionSchemasAndMetadata()
    {
        var metadata = new WorkDefinitionMetadata(
            Purpose: "Expose cache refresh to external callers.",
            Risk: WorkRisk.Medium,
            RequiresApproval: true,
            Capabilities: ["cache-write"]);
        var definition = WorkDefinition.Create(
            "cache.refresh",
            "Refreshes cached data.",
            "Cache",
            inputSchema: new WorkSchema("""{"type":"object","properties":{"key":{"type":"string"}}}"""),
            outputSchema: new WorkSchema("""{"type":"object","properties":{"refreshed":{"type":"boolean"}}}"""),
            metadata: metadata,
            configuration: AllowMcp());
        var system = CreateSystem(definition, SuccessfulWork);

        var descriptor = Assert.Single(system.GetMcpToolDescriptors());

        Assert.Equal("cache.refresh", descriptor.Name);
        Assert.Equal("Refreshes cached data.", descriptor.Description);
        Assert.Equal("Cache", descriptor.Category);
        Assert.Equal(definition.Id, descriptor.DefinitionId);
        Assert.Equal(definition.InputSchema.JsonSchema, descriptor.InputSchemaJson);
        Assert.Equal(definition.OutputSchema.JsonSchema, descriptor.OutputSchemaJson);
        Assert.False(descriptor.UsesFallbackInputSchema);
        Assert.Same(metadata, descriptor.Metadata);
    }

    [Fact]
    public void ToolDescriptorsCanUseOrExcludeFallbackInputSchema()
    {
        var definition = WorkDefinition.Create("maintenance.ping", "Pings maintenance.", configuration: AllowMcp());
        var system = CreateSystem(definition, SuccessfulWork);

        var included = Assert.Single(system.GetMcpToolDescriptors());
        var excluded = system.GetMcpToolDescriptors(new WorkableMcpToolCatalogOptions
        {
            IncludeDefinitionsWithoutJsonSchema = false,
        });

        Assert.True(included.UsesFallbackInputSchema);
        Assert.Equal("""{"type":"object","additionalProperties":true}""", included.InputSchemaJson);
        Assert.Empty(excluded);
    }

    [Fact]
    public async Task InvokeMcpToolQueuesWorkAndReturnsCompletedOutput()
    {
        var definition = WorkDefinition.Create("echo", "Echoes input.", configuration: AllowMcp());
        await using var system = CreateSystem(definition, (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Success(input is null ? WorkOutput.Empty : WorkOutput.FromData(input))));
        await system.Start();

        using var document = JsonDocument.Parse("""{"message":"hello"}""");
        var result = await system.InvokeMcpTool("echo", document.RootElement);

        Assert.True(result.IsCompletedSuccessfully);
        Assert.Equal(WorkableMcpInvocationStatus.Completed, result.Status);
        Assert.NotNull(result.WorkerId);
        Assert.Equal("""{"message":"hello"}""", result.Output?.Json);
    }

    [Fact]
    public async Task InvokeMcpToolCanReturnAfterAccepted()
    {
        var definition = WorkDefinition.Create(
            "manual.work",
            "Queues without waiting.",
            configuration: AllowMcp() with { Start = WorkStartConfiguration.DoNotStart });
        await using var system = CreateSystem(definition, SuccessfulWork);
        await system.Start();

        var result = await system.InvokeMcpTool(
            "manual.work",
            options: new WorkableMcpInvocationOptions
            {
                Completion = WorkableMcpInvocationCompletion.ReturnAfterAccepted,
            });

        Assert.Equal(WorkableMcpInvocationStatus.Accepted, result.Status);
        Assert.True(result.QueueOutcome.IsAccepted);
        Assert.Null(result.Completion);
    }

    [Fact]
    public async Task InvokeMcpToolReturnsRejectedForUnknownWork()
    {
        await using var system = CreateSystem(WorkDefinition.Create("known"), SuccessfulWork);
        await system.Start();

        var result = await system.InvokeMcpTool("missing");

        Assert.Equal(WorkableMcpInvocationStatus.Rejected, result.Status);
        Assert.False(result.QueueOutcome.IsAccepted);
        Assert.Contains(result.Messages, message => message.Code == "workable.definition.not_found");
    }

    [Fact]
    public async Task McpDoesNotExposeOrInvokeWorkByDefault()
    {
        await using var system = CreateSystem(WorkDefinition.Create("dotnet.http.default"), SuccessfulWork);
        await system.Start();

        var descriptors = system.GetMcpToolDescriptors();
        var result = await system.InvokeMcpTool("dotnet.http.default");

        Assert.Empty(descriptors);
        Assert.Equal(WorkableMcpInvocationStatus.Rejected, result.Status);
        Assert.Contains(result.Messages, message => message.Code == "workable.invocation.channel_not_allowed");
    }

    [Fact]
    public void McpServerRegistersRouterAndOptions()
    {
        using var provider = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(WorkDefinition.Create("known"), SuccessfulWork))
            .AddWorkableMcpServer()
            .BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<WorkableMcpToolRouter>());
        Assert.NotNull(provider.GetRequiredService<IOptions<WorkableMcpServerOptions>>().Value);
    }

    [Fact]
    public void McpServerToolsIncludeSafeWorkNamesAndQueryTools()
    {
        var definition = WorkDefinition.Create("cache.refresh", "Refreshes cached data.", configuration: AllowMcp());
        using var provider = CreateProvider(definition, SuccessfulWork);
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();

        var tools = router.GetTools();

        Assert.Contains(tools, tool =>
            tool.Kind == WorkableMcpServerToolKind.Work &&
            tool.ToolName == "workable_work_cache_refresh" &&
            tool.WorkName == "cache.refresh");
        Assert.Contains(tools, tool =>
            tool.Kind == WorkableMcpServerToolKind.Query &&
            tool.ToolName == "workable_query_workers");
        Assert.Contains(tools, tool =>
            tool.Kind == WorkableMcpServerToolKind.Query &&
            tool.ToolName == "workable_get_worker_status_summary");
        Assert.Contains(tools, tool =>
            tool.Kind == WorkableMcpServerToolKind.Action &&
            tool.ToolName == "workable_cancel_worker" &&
            tool.Description?.Contains("Permanently stop", StringComparison.OrdinalIgnoreCase) == true);
    }

    [Fact]
    public void McpServerDisambiguatesWorkToolNameCollisions()
    {
        using var provider = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(WorkDefinition.Create("cache.refresh", configuration: AllowMcp()), SuccessfulWork);
                builder.AddWork(WorkDefinition.Create("cache_refresh", configuration: AllowMcp()), SuccessfulWork);
            })
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();

        var workTools = router.GetTools()
            .Where(tool => tool.Kind == WorkableMcpServerToolKind.Work)
            .ToList();

        Assert.Equal(2, workTools.Count);
        Assert.Equal(2, workTools.Select(tool => tool.ToolName).Distinct(StringComparer.Ordinal).Count());
        Assert.All(workTools, tool => Assert.StartsWith("workable_work_cache_refresh_", tool.ToolName));
    }

    [Fact]
    public async Task McpServerCanInvokeWorkToolThroughSafeToolName()
    {
        var definition = WorkDefinition.Create("echo.message", "Echoes input.", configuration: AllowMcp());
        await using var provider = CreateProvider(definition, (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Success(input is null ? WorkOutput.Empty : WorkOutput.FromData(input))));
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        await system.Start();

        using var document = JsonDocument.Parse("""{"message":"hello"}""");
        var result = await router.CallTool("workable_work_echo_message", document.RootElement);

        Assert.False(result.IsError);
        Assert.Contains("\"status\":\"Completed\"", result.Json);
        Assert.Contains("\"json\":\"{\\u0022message\\u0022:\\u0022hello\\u0022}\"", result.Json);
    }

    [Fact]
    public async Task McpRouterCanTargetNamedSystem()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder =>
            {
                builder.AddWork(WorkDefinition.Create("default.echo", configuration: AllowMcp()), SuccessfulWork);
            })
            .AddWorkableSystem("remote", builder =>
            {
                builder.AddWork(WorkDefinition.Create("remote.echo", configuration: AllowMcp()), (context, input, cancellationToken) =>
                    Task.FromResult(WorkExecutionResult.Success(input is null ? WorkOutput.Empty : WorkOutput.FromData(input))));
            })
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(registry.TryGet("remote", out var remote));
        await remote.Start();
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();

        var tools = router.GetTools(systemName: "remote");

        Assert.Contains(tools, tool => tool.ToolName == "workable_work_remote_echo");
        Assert.DoesNotContain(tools, tool => tool.ToolName == "workable_work_default_echo");

        using var document = JsonDocument.Parse("""{"message":"hello remote"}""");
        var result = await router.CallTool("workable_work_remote_echo", document.RootElement, systemName: "remote");

        Assert.False(result.IsError);
        Assert.Contains("hello remote", result.Json);
    }

    [Fact]
    public async Task McpRouterReturnsToolErrorForUnknownNamedSystem()
    {
        await using var provider = CreateProvider(WorkDefinition.Create("known", configuration: AllowMcp()), SuccessfulWork);
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();

        var result = await router.CallTool("workable_query_workers", arguments: null, systemName: "missing");

        Assert.True(result.IsError);
        Assert.Contains("workable.mcp.system_not_found", result.Json);
    }

    [Fact]
    public async Task MappedHttpMcpServerListsToolsAndCallsWorkThroughHttpTransport()
    {
        var observedOrigin = new TaskCompletionSource<WorkOrigin>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = await CreateMcpHttpHost(execute: (context, input, cancellationToken) =>
        {
            observedOrigin.TrySetResult(context.Origin);
            return Task.FromResult(WorkExecutionResult.Success(input is null ? WorkOutput.Empty : WorkOutput.FromData(input)));
        });
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/mcp"),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();

        Assert.Contains(tools, tool => tool.Name == "workable_work_echo_message");

        var result = await client.CallToolAsync(
            "workable_work_echo_message",
            new Dictionary<string, object?>
            {
                ["message"] = "hello over mcp",
            });
        var json = JsonSerializer.Serialize(result);

        Assert.False(result.IsError);
        Assert.Contains("Completed", json);
        Assert.Contains("hello over mcp", json);

        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var workers = await system.Query.QueryWorkers(new WorkerQuery(DefinitionName: "echo.message"));
        var worker = Assert.Single(workers.Workers);

        Assert.Equal(WorkInvocationChannel.Mcp, worker.Origin.Channel);
        Assert.Equal("mcp-user-1", worker.Origin.Actor.Id);
        Assert.Equal("mcp.user@example.com", worker.Origin.Actor.Email);
        Assert.Equal("/mcp", worker.Origin.Url);

        var executionOrigin = await observedOrigin.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(WorkInvocationChannel.Mcp, executionOrigin.Channel);
        Assert.Equal("mcp-user-1", executionOrigin.Actor.Id);
    }

    [Fact]
    public async Task MappedHttpMcpEndpointCanTargetNamedSystem()
    {
        using var host = await CreateNamedMcpHttpHost();
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/mcp/remote"),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();

        Assert.Contains(tools, tool => tool.Name == "workable_work_remote_echo");
        Assert.DoesNotContain(tools, tool => tool.Name == "workable_work_default_echo");

        var result = await client.CallToolAsync(
            "workable_work_remote_echo",
            new Dictionary<string, object?>
            {
                ["message"] = "named endpoint",
            });
        var registry = host.Services.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(registry.TryGet("remote", out var remote));
        var workers = await remote.Query.QueryWorkers(new WorkerQuery(DefinitionName: "remote.echo"));
        var worker = Assert.Single(workers.Workers);

        Assert.False(result.IsError);
        Assert.Equal(WorkInvocationChannel.Mcp, worker.Origin.Channel);
        Assert.Equal("/mcp/remote", worker.Origin.Url);
    }

    [Fact]
    public async Task MappedHttpMcpNamedEndpointWorkerActionsStayOnNamedSystem()
    {
        using var host = await CreateNamedMcpHttpHost(
            remoteDefinition: WorkDefinition.Create(
                "remote.manual",
                "Manual remote work.",
                configuration: AllowMcp() with
                {
                    Start = WorkStartConfiguration.DoNotStart,
                }));
        var registry = host.Services.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(registry.TryGet("remote", out var remote));
        var queue = await remote.Queue.Enqueue("remote.manual");
        var worker = await remote.Query.GetWorker(queue.WorkerId ?? throw new InvalidOperationException("Expected worker."))
            ?? throw new InvalidOperationException("Expected worker.");
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/mcp/remote"),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport);

        var result = await client.CallToolAsync(
            "workable_cancel_worker",
            new Dictionary<string, object?>
            {
                ["workerId"] = worker.Id.Value.ToString("D"),
                ["revision"] = worker.Revision,
            });

        var updated = await remote.Query.GetWorker(worker.Id)
            ?? throw new InvalidOperationException("Expected worker.");
        var history = Assert.Single(updated.ActionHistory);

        Assert.False(result.IsError);
        Assert.Equal(WorkerState.Canceled, updated.State);
        Assert.Equal(WorkAction.Cancel, history.Action);
        Assert.Equal(WorkInvocationChannel.Mcp, history.Origin.Channel);
        Assert.Equal("/mcp/remote", history.Origin.Url);
    }

    [Fact]
    public async Task MappedHttpMcpAnonymousRequestRecordsUnknownActor()
    {
        using var host = await CreateMcpHttpHost(authenticated: false);
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/mcp"),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport);

        var result = await client.CallToolAsync(
            "workable_work_echo_message",
            new Dictionary<string, object?>
            {
                ["message"] = "anonymous over mcp",
            });

        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var workers = await system.Query.QueryWorkers(new WorkerQuery(DefinitionName: "echo.message"));
        var worker = Assert.Single(workers.Workers);

        Assert.False(result.IsError);
        Assert.Equal(WorkInvocationChannel.Mcp, worker.Origin.Channel);
        Assert.Equal(WorkActor.Unknown, worker.Origin.Actor);
        Assert.Equal("/mcp", worker.Origin.Url);
    }

    [Fact]
    public async Task MappedHttpMcpServerCanApplyWorkerActionsWithMcpOrigin()
    {
        using var host = await CreateMcpHttpHost(
            definition: WorkDefinition.Create(
                "manual.mcp",
                "Manual MCP work.",
                configuration: AllowMcp() with
                {
                    Start = WorkStartConfiguration.DoNotStart,
                }));
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var queue = await system.Queue.Enqueue("manual.mcp");
        var worker = await system.Query.GetWorker(queue.WorkerId ?? throw new InvalidOperationException("Expected worker."))
            ?? throw new InvalidOperationException("Expected worker.");
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/mcp"),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport);

        var result = await client.CallToolAsync(
            "workable_cancel_worker",
            new Dictionary<string, object?>
            {
                ["workerId"] = worker.Id.Value.ToString("D"),
                ["revision"] = worker.Revision,
            });

        var updated = await system.Query.GetWorker(worker.Id)
            ?? throw new InvalidOperationException("Expected worker.");
        var history = Assert.Single(updated.ActionHistory);
        var json = JsonSerializer.Serialize(result);

        Assert.False(result.IsError);
        Assert.Contains("Accepted", json);
        Assert.Equal(WorkerState.Canceled, updated.State);
        Assert.Equal(WorkAction.Cancel, history.Action);
        Assert.Equal(WorkInvocationChannel.Mcp, history.Origin.Channel);
        Assert.Equal("mcp-user-1", history.Origin.Actor.Id);
    }

    [Fact]
    public async Task McpServerQueryToolsCanObserveWorkers()
    {
        var definition = WorkDefinition.Create("reports.generate", "Generates report.", configuration: AllowMcp());
        await using var provider = CreateProvider(definition, SuccessfulWork);
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        await system.Start();

        var handle = await system.Queue.Enqueue("reports.generate", WorkInput.Empty);
        var completion = await handle.WaitForCompletion();
        Assert.Equal(WorkCompletionStatus.Completed, completion.Status);

        using var queryArguments = JsonDocument.Parse("""{"workName":"reports.generate","states":["Completed"]}""");
        var queryResult = await router.CallTool("workable_query_workers", queryArguments.RootElement);
        var statusResult = await router.CallTool("workable_get_worker_status_summary", queryArguments.RootElement);

        Assert.False(queryResult.IsError);
        Assert.Contains("\"totalCount\":1", queryResult.Json);
        Assert.Contains(handle.WorkerId!.Value.Value.ToString("D"), queryResult.Json);
        Assert.False(statusResult.IsError);
        Assert.Contains("\"Completed\":1", statusResult.Json);
    }

    [Fact]
    public async Task McpServerGetWorkerReturnsSnapshot()
    {
        var definition = WorkDefinition.Create("data.import", "Imports data.", configuration: AllowMcp());
        await using var provider = CreateProvider(definition, SuccessfulWork);
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        await system.Start();

        var handle = await system.Queue.Enqueue("data.import", WorkInput.Empty);
        await handle.WaitForCompletion();

        using var arguments = JsonDocument.Parse($$"""{"workerId":"{{handle.WorkerId!.Value.Value:D}}"}""");
        var result = await router.CallTool("workable_get_worker", arguments.RootElement);

        Assert.False(result.IsError);
        Assert.Contains("\"found\":true", result.Json);
        Assert.Contains("\"definitionName\":\"data.import\"", result.Json);
    }

    [Fact]
    public async Task McpServerReturnsToolErrorForUnknownTool()
    {
        await using var provider = CreateProvider(WorkDefinition.Create("known", configuration: AllowMcp()), SuccessfulWork);
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();

        var result = await router.CallTool("missing_tool", arguments: null);

        Assert.True(result.IsError);
        Assert.Contains("workable.mcp.tool_not_found", result.Json);
    }

    private static IWorkSystem CreateSystem(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute)
        => CreateProvider(definition, execute)
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

    private static ServiceProvider CreateProvider(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute)
        => new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, execute))
            .AddWorkableMcpServer()
            .BuildServiceProvider()
            ;

    private static async Task<IHost> CreateMcpHttpHost(
        bool authenticated = true,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>>? execute = null,
        WorkDefinition? definition = null)
    {
        execute ??= (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Success(input is null ? WorkOutput.Empty : WorkOutput.FromData(input)));
        definition ??= WorkDefinition.Create(
            "echo.message",
            "Echoes input.",
            configuration: AllowMcp());

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
                        builder.AddWork(definition, execute);
                    });
                    services.AddWorkableMcpServer();
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
                                new Claim(ClaimTypes.NameIdentifier, "mcp-user-1"),
                                new Claim(ClaimTypes.Name, "MCP User"),
                                new Claim(ClaimTypes.Email, "mcp.user@example.com"),
                            ], "Test"));
                            await next();
                        });
                    }

                    app.UseEndpoints(endpoints => endpoints.MapWorkableMcp("/mcp"));
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> CreateNamedMcpHttpHost(WorkDefinition? remoteDefinition = null)
    {
        remoteDefinition ??= WorkDefinition.Create("remote.echo", configuration: AllowMcp());

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
                        builder.AddWork(WorkDefinition.Create("default.echo", configuration: AllowMcp()), SuccessfulWork);
                    });
                    services.AddWorkableSystem("remote", builder =>
                    {
                        builder.StartWithHost();
                        builder.AddWork(remoteDefinition, (context, input, cancellationToken) =>
                            Task.FromResult(WorkExecutionResult.Success(input is null ? WorkOutput.Empty : WorkOutput.FromData(input))));
                    });
                    services.AddWorkableMcpServer();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapWorkableMcp("/mcp");
                        endpoints.MapWorkableMcp("/mcp/remote", systemName: "remote");
                    });
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

    private static WorkConfiguration AllowMcp()
        => WorkConfiguration.Default with
        {
            Invocation = WorkInvocationConfiguration.Allow(
                WorkInvocationChannel.DotNet,
                WorkInvocationChannel.HttpApi,
                WorkInvocationChannel.Mcp),
        };
}
