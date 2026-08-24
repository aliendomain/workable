using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Mcp")]
public sealed class WorkableMcpTests
{
    [Fact]
    public void McpRegistrationAppliesHostToolOptions()
    {
        using var provider = new ServiceCollection()
            .AddWorkableMcpServer(options => options.IncludeActionTools = false)
            .BuildServiceProvider();

        Assert.False(provider.GetRequiredService<IOptions<WorkableMcpServerOptions>>().Value.IncludeActionTools);
    }

    [Fact]
    public void McpMappingRejectsUnauthorisedAndUnknownSystems()
    {
        using var unauthorisedProvider = new ServiceCollection()
            .AddRouting()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create("mcp.mapping.unauthorised", configuration: AllowMcp()),
                SuccessfulWork))
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var unauthorisedEndpoints = new TestEndpointRouteBuilder(unauthorisedProvider);

        var unauthorised = Assert.Throws<InvalidOperationException>(() =>
        {
            unauthorisedEndpoints.MapWorkableMcp(useHostFallbackPolicy: true);
        });

        Assert.Contains("requires authorization-enabled systems", unauthorised.Message, StringComparison.Ordinal);

        using var authorisedProvider = CreateProvider(
            WorkDefinition.Create("mcp.mapping.authorised", configuration: AllowMcp()),
            SuccessfulWork);
        var authorisedEndpoints = new TestEndpointRouteBuilder(authorisedProvider);

        var missing = Assert.Throws<InvalidOperationException>(() =>
        {
            authorisedEndpoints.MapWorkableMcp(systemName: "missing", useHostFallbackPolicy: true);
        });

        Assert.Contains("was not found", missing.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpServerExposesAndCallsPersistentExecutionDiagnosticTools()
    {
        var repository = new TestExecutionDiagnosticsRepository();
        var now = DateTimeOffset.UtcNow;
        repository.QueryResult = new WorkExecutionDiagnosticQueryResult(
        [
            new WorkExecutionDiagnosticSummary(
                Guid.NewGuid(),
                WorkSystemId.New(),
                null,
                WorkerId.New(),
                1,
                WorkDefinitionId.New(),
                "diagnostic-work",
                WorkCompletionStatus.Completed,
                1,
                now.AddSeconds(-1),
                now,
                TimeSpan.FromSeconds(1),
                WorkExecutionDiagnosticCaptureSource.WorkConfiguration,
                WorkProfileCaptureMode.Bounded,
                new WorkExecutionDiagnosticInstrumentationAvailability(
                    SqlClientProfilingAvailable: true,
                    HttpClientProfilingAvailable: false),
                false,
                0,
                0,
                now.AddDays(1),
                []),
        ]);
        var summary = Assert.Single(repository.QueryResult.Items);
        repository.Artifact = new WorkExecutionDiagnosticArtifact(
            summary,
            [
                new WorkExecutionDiagnosticLogRecord(
                    summary.DiagnosticId,
                    0,
                    now,
                    LogLevel.Warning,
                    "diagnostic.category",
                    new EventId(9, "McpDiagnostic"),
                    "MCP persisted warning",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null),
            ],
            null);
        await using var provider = new ServiceCollection()
            .AddTransportTestAuthorization()
            .AddSingleton<IWorkExecutionDiagnosticsRepository>(repository)
            .AddWorkableSystem(builder =>
            {
                builder.RequireAuthorization();
                builder.ConfigureTransportSystemAuthorization();
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create("diagnostic-work", configuration: AllowMcp()),
                    SuccessfulWork);
            })
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        var requestContext = CreateMcpRequestContext("Inspect persisted execution evidence.");

        var tools = await router.GetTools(requestContext);
        using var arguments = JsonDocument.Parse(
            """{"definitionName":"diagnostic-work","minimumLogLevel":"Warning","take":12}""");
        var result = await router.CallTool(
            "workable_query_execution_diagnostics",
            arguments.RootElement,
            options: null,
            systemName: null,
            requestContext);
        using var getArguments = JsonDocument.Parse(
            $$"""{"workerId":"{{summary.WorkerId.Value:D}}","sequence":{{summary.IterationSequence}}}""");
        var getResult = await router.CallTool(
            "workable_get_execution_diagnostic",
            getArguments.RootElement,
            options: null,
            systemName: null,
            requestContext);
        repository.Artifact = null;
        var missingGetResult = await router.CallTool(
            "workable_get_execution_diagnostic",
            getArguments.RootElement,
            options: null,
            systemName: null,
            requestContext);

        Assert.Contains(tools, tool => tool.ToolName == "workable_query_execution_diagnostics");
        Assert.Contains(tools, tool => tool.ToolName == "workable_get_execution_diagnostic");
        Assert.False(result.IsError);
        Assert.Contains("\"sqlClientProfilingAvailable\":true", result.Json, StringComparison.Ordinal);
        Assert.Contains("\"httpClientProfilingAvailable\":false", result.Json, StringComparison.Ordinal);
        Assert.Contains("\"profileDropped\":false", result.Json, StringComparison.Ordinal);
        Assert.Equal("diagnostic-work", repository.LastCriteria?.DefinitionName);
        Assert.Equal(LogLevel.Warning, repository.LastCriteria?.MinimumLogLevel);
        Assert.Equal(12, repository.LastCriteria?.Take);
        Assert.False(getResult.IsError);
        Assert.Contains("MCP persisted warning", getResult.Json, StringComparison.Ordinal);
        Assert.False(missingGetResult.IsError);
        Assert.Contains("\"found\":false", missingGetResult.Json, StringComparison.Ordinal);
        Assert.Equal(summary.WorkerId, repository.LastGetRequest?.WorkerId);
        Assert.Equal(summary.IterationSequence, repository.LastGetRequest?.IterationSequence);
    }

    [Fact]
    public async Task McpPersistentExecutionDiagnosticToolsRequireDiagnosticsAccessEvenWhenCalledDirectly()
    {
        var repository = new TestExecutionDiagnosticsRepository();
        await using var provider = new ServiceCollection()
            .AddTransportTestAuthorization(TransportAuthorizationTestSupport.ReadGroups)
            .AddSingleton<IWorkExecutionDiagnosticsRepository>(repository)
            .AddWorkableSystem(builder =>
            {
                builder.RequireAuthorization();
                builder.ConfigureTransportSystemAuthorization();
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create("diagnostic-read-auth", configuration: AllowMcp()),
                    SuccessfulWork);
            })
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        var requestContext = CreateMcpRequestContext("Attempt diagnostics without permission.");

        var tools = await router.GetTools(requestContext);
        using var arguments = JsonDocument.Parse("""{"take":10}""");
        var result = await router.CallTool(
            "workable_query_execution_diagnostics",
            arguments.RootElement,
            options: null,
            systemName: null,
            requestContext);

        Assert.DoesNotContain(tools, tool =>
            tool.ToolName is "workable_query_execution_diagnostics" or "workable_get_execution_diagnostic");
        Assert.True(result.IsError);
        Assert.Contains("workable.mcp.tool_not_found", result.Json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(WorkExecutionDiagnosticCriteria.MaximumTake + 1)]
    public async Task McpPersistentExecutionDiagnosticQueryRejectsOutOfRangeTake(int take)
    {
        var repository = new TestExecutionDiagnosticsRepository();
        await using var provider = new ServiceCollection()
            .AddTransportTestAuthorization()
            .AddSingleton<IWorkExecutionDiagnosticsRepository>(repository)
            .AddWorkableSystem(builder =>
            {
                builder.RequireAuthorization();
                builder.ConfigureTransportSystemAuthorization();
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create("diagnostic-query-bound", configuration: AllowMcp()),
                    SuccessfulWork);
            })
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        using var arguments = JsonDocument.Parse($$"""{"take":{{take}}}""");

        var result = await router.CallTool(
            "workable_query_execution_diagnostics",
            arguments.RootElement,
            options: null,
            systemName: null,
            CreateMcpRequestContext("Attempt an unbounded diagnostics query."));

        Assert.True(result.IsError);
        Assert.Contains("workable.mcp.arguments_invalid", result.Json, StringComparison.Ordinal);
        Assert.Contains("between 1 and 1000", result.Json, StringComparison.Ordinal);
        Assert.Null(repository.LastCriteria);
    }

    [Fact]
    public async Task McpRedactsExecutionDiagnosticRepositoryFailures()
    {
        const string SensitiveMessage = "database-password=repository-secret";
        var repository = new TestExecutionDiagnosticsRepository
        {
            QueryException = new ArgumentException(SensitiveMessage),
        };
        await using var provider = new ServiceCollection()
            .AddTransportTestAuthorization()
            .AddSingleton<IWorkExecutionDiagnosticsRepository>(repository)
            .AddWorkableSystem(builder =>
            {
                builder.RequireAuthorization();
                builder.ConfigureTransportSystemAuthorization();
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create("diagnostic-redaction", configuration: AllowMcp()),
                    SuccessfulWork);
            })
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        using var arguments = JsonDocument.Parse("""{"take":10}""");

        var result = await router.CallTool(
            "workable_query_execution_diagnostics",
            arguments.RootElement,
            options: null,
            systemName: null,
            CreateMcpRequestContext("Verify provider failure redaction."));

        Assert.True(result.IsError);
        Assert.Contains("workable.mcp.tool_failed", result.Json, StringComparison.Ordinal);
        Assert.DoesNotContain(SensitiveMessage, result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolDescriptorsUseDefinitionSchemasAndMetadata()
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
        var session = await CreateMcpSession(system, "Read MCP tool descriptors.");

        var descriptor = Assert.Single(session.GetMcpToolDescriptors());

        Assert.Equal("cache.refresh", descriptor.Name);
        Assert.Equal("Refreshes cached data.", descriptor.Description);
        Assert.Equal("Cache", descriptor.Category);
        Assert.Equal(definition.InputSchema.JsonSchema, descriptor.InputSchemaJson);
        Assert.Equal(definition.OutputSchema.JsonSchema, descriptor.OutputSchemaJson);
        Assert.False(descriptor.UsesFallbackInputSchema);
        Assert.NotSame(metadata, descriptor.Metadata);
        Assert.Equal(metadata.Purpose, descriptor.Metadata?.Purpose);
        Assert.Equal(metadata.Capabilities, descriptor.Metadata?.Capabilities);
    }

    [Fact]
    public async Task ToolDescriptorsCanUseOrExcludeFallbackInputSchema()
    {
        var definition = WorkDefinition.Create("maintenance.ping", "Pings maintenance.", configuration: AllowMcp());
        var system = CreateSystem(definition, SuccessfulWork);
        var session = await CreateMcpSession(system, "Read MCP tool descriptors.");

        var included = Assert.Single(session.GetMcpToolDescriptors());
        var excluded = session.GetMcpToolDescriptors(new WorkableMcpToolCatalogOptions
        {
            IncludeDefinitionsWithoutJsonSchema = false,
        });

        Assert.True(included.UsesFallbackInputSchema);
        Assert.Equal("""{"type":"object","additionalProperties":true}""", included.InputSchemaJson);
        Assert.Empty(excluded);
    }

    [Fact]
    public async Task ToolDescriptorsUseDiscoveryWithoutGrantingMcpInvocation()
    {
        var definition = WorkDefinition.Create(
            "discovery.only",
            inputSchema: WorkSchema.FromType<string>(),
            configuration: AllowMcp());
        using var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new FixedGroupProvider([]))
            .AddWorkableSystem(builder => builder
                .RequireAuthorization()
                .AddWork(
                    definition,
                    SuccessfulWork,
                    configure: null,
                    authorize: authorize => authorize.AllowDiscoverToKnownAuthenticatedUsers()))
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.Mcp,
            new WorkActor(Id: "known-mcp-discoverer"),
            "Discover but do not invoke an MCP tool.",
            isAuthenticated: true);

        var tools = await router.GetTools(requestContext);
        var workTool = Assert.Single(tools, tool => tool.Kind == WorkableMcpServerToolKind.Work);
        using var queryArguments = JsonDocument.Parse("""{"take":1}""");
        using var actionArguments = JsonDocument.Parse($$"""{"workerId":"{{Guid.NewGuid():D}}","revision":0}""");
        var result = await router.CallTool(
            workTool.ToolName,
            arguments: null,
            options: null,
            systemName: null,
            requestContext);
        var queryResult = await router.CallTool(
            "workable_query_workers",
            queryArguments.RootElement,
            options: null,
            systemName: null,
            requestContext);
        var actionResult = await router.CallTool(
            "workable_cancel_worker",
            actionArguments.RootElement,
            options: null,
            systemName: null,
            requestContext);

        Assert.Equal(definition.Name, workTool.WorkName);
        Assert.DoesNotContain(tools, tool => tool.Kind is WorkableMcpServerToolKind.Query or WorkableMcpServerToolKind.Action);
        Assert.True(result.IsError);
        Assert.Contains("Unauthorized", result.Json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("workable.mcp.tool_not_found", queryResult.Json, StringComparison.Ordinal);
        Assert.Contains("workable.mcp.tool_not_found", actionResult.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpActionToolsReflectExactWorkAndWorkflowOperationPermissions()
    {
        var definition = WorkDefinition.Create(
            "queue.only",
            inputSchema: WorkSchema.FromType<string>(),
            configuration: AllowMcp());
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new FixedGroupProvider(["queue.only"]))
            .AddWorkableSystem(builder =>
            {
                builder.RequireAuthorization();
                builder.AddWork(
                    definition,
                    SuccessfulWork,
                    configure: null,
                    authorize: authorization => authorization.AllowQueueToGroups("queue.only"));
                builder.AddWorkflow(
                    WorkflowDefinition.Create("workflow.queue.only"),
                    workflow => workflow.DispatchWork("child", definition),
                    authorize: authorization => authorization.AllowQueueToGroups("queue.only"));
            })
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        var requestContext = CreateMcpRequestContext("List operation-scoped MCP tools.");

        var tools = await router.GetTools(requestContext);
        using var workerArguments = JsonDocument.Parse(
            $$"""{"workerId":"{{Guid.NewGuid():D}}","revision":0}""");
        using var workflowArguments = JsonDocument.Parse(
            $$"""{"runId":"{{Guid.NewGuid():D}}"}""");
        var workerAction = await router.CallTool(
            "workable_cancel_worker",
            workerArguments.RootElement,
            null,
            null,
            requestContext);
        var workflowAction = await router.CallTool(
            "workable_cancel_workflow",
            workflowArguments.RootElement,
            null,
            null,
            requestContext);

        Assert.Contains(tools, tool => tool.Kind == WorkableMcpServerToolKind.Work && tool.WorkName == definition.Name);
        Assert.Contains(tools, tool => tool.ToolName == "workable_start_workflow");
        Assert.DoesNotContain(tools, tool => tool.Kind == WorkableMcpServerToolKind.Query);
        Assert.DoesNotContain(tools, tool => tool.ToolName is
            "workable_start_worker" or
            "workable_pause_worker" or
            "workable_cancel_worker" or
            "workable_push_worker" or
            "workable_purge_worker" or
            "workable_reconfigure_work_definition" or
            "workable_start_workflow_run" or
            "workable_pause_workflow_run" or
            "workable_stop_workflow" or
            "workable_cancel_workflow");
        Assert.Contains("workable.mcp.tool_not_found", workerAction.Json, StringComparison.Ordinal);
        Assert.Contains("workable.mcp.tool_not_found", workflowAction.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpCanReconfigureDiscoverableDefinitionWithoutReadPermission()
    {
        var definition = WorkDefinition.Create("reconfigure.only", configuration: AllowMcp());
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new FixedGroupProvider(["reconfigure.only"]))
            .AddWorkableSystem(builder =>
            {
                builder.RequireAuthorization();
                builder.ConfigureAuthorization(authorization =>
                    authorization.AllowControlSystemToGroups("reconfigure.only"));
                builder.AddWork(
                    definition,
                    SuccessfulWork,
                    configure: null,
                    authorize: authorization => authorization
                        .AllowReadToGroups("inspect")
                        .AllowOperationsToGroups(
                            ["reconfigure.only"],
                            WorkOperationPermissions.ReconfigureDefinition));
            })
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        var requestContext = CreateMcpRequestContext("Reconfigure without definition read access.");
        await system.Start(requestContext);

        var callerSession = await system.CreateSession(requestContext);
        var tools = await router.GetTools(requestContext);
        using var arguments = JsonDocument.Parse($$"""
            {
              "name": "{{definition.Name}}",
              "revision": {{definition.Revision}},
              "defaultOptions": {
                "profilingEnabled": true
              }
            }
            """);
        var result = await router.CallTool(
            "workable_reconfigure_work_definition",
            arguments.RootElement,
            null,
            null,
            requestContext);
        var inspector = await CreateMcpSessionWithGroups(system, "Inspect reconfigured definition.", "inspect");
        var reconfigured = Assert.Single(inspector.Catalog.Definitions);

        Assert.Empty(callerSession.Catalog.Definitions);
        Assert.Single(callerSession.Discovery.Definitions);
        Assert.Contains(tools, tool => tool.ToolName == "workable_reconfigure_work_definition");
        Assert.DoesNotContain(tools, tool => tool.Kind == WorkableMcpServerToolKind.Query);
        Assert.False(result.IsError);
        var resultJson = JsonNode.Parse(result.Json)?.AsObject()
            ?? throw new InvalidOperationException("Expected definition reconfiguration response.");
        Assert.Equal("Accepted", resultJson["status"]?.GetValue<string>());
        Assert.Null(resultJson["definition"]);
        Assert.Equal(1, resultJson["revision"]?.GetValue<long>());
        Assert.Equal(1, reconfigured.Revision);
        Assert.True(reconfigured.DefaultOptions.ProfilingEnabled);

        var conflict = await router.CallTool(
            "workable_reconfigure_work_definition",
            arguments.RootElement,
            null,
            null,
            requestContext);
        var conflictJson = JsonNode.Parse(conflict.Json)?.AsObject()
            ?? throw new InvalidOperationException("Expected definition conflict response.");
        Assert.Equal("Conflict", conflictJson["status"]?.GetValue<string>());
        Assert.Null(conflictJson["definition"]);
        Assert.Equal(1, conflictJson["revision"]?.GetValue<long>());
    }

    [Fact]
    public async Task InvokeMcpToolQueuesWorkAndReturnsCompletedOutput()
    {
        var definition = WorkDefinition.Create("echo", "Echoes input.", configuration: AllowMcp());
        await using var system = CreateSystem(definition, (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Success(input is null ? WorkOutput.Empty : WorkOutput.FromData(input))));
        await system.Start();
        var session = await CreateMcpSession(system, "Invoke MCP tool.");

        using var document = JsonDocument.Parse("""{"message":"hello"}""");
        var result = await session.InvokeMcpTool("echo", document.RootElement);

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
        var session = await CreateMcpSession(system, "Invoke MCP tool.");

        var result = await session.InvokeMcpTool(
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
        var session = await CreateMcpSession(system, "Invoke MCP tool.");

        var result = await session.InvokeMcpTool("missing");

        Assert.Equal(WorkableMcpInvocationStatus.Rejected, result.Status);
        Assert.False(result.QueueOutcome.IsAccepted);
        Assert.Contains(result.Messages, message => message.Code == "workable.definition.not_found");
    }

    [Fact]
    public async Task McpDoesNotExposeOrInvokeWorkByDefault()
    {
        await using var system = CreateSystem(WorkDefinition.Create("dotnet.http.default"), SuccessfulWork);
        await system.Start();
        var session = await CreateMcpSession(system, "Invoke MCP tool.");

        var descriptors = session.GetMcpToolDescriptors();
        var result = await session.InvokeMcpTool("dotnet.http.default");

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
    public async Task McpServerToolsIncludeSafeWorkNamesAndQueryTools()
    {
        var definition = WorkDefinition.Create("cache.refresh", "Refreshes cached data.", configuration: AllowMcp());
        using var provider = CreateProvider(definition, SuccessfulWork);
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();

        var tools = await router.GetTools(CreateMcpRequestContext("List MCP server tools."));

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
            tool.Kind == WorkableMcpServerToolKind.Query &&
            tool.ToolName == "workable_get_worker_iteration" &&
            tool.Description?.Contains("iteration sequence", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(tools, tool =>
            tool.Kind == WorkableMcpServerToolKind.Query &&
            tool.ToolName == "workable_query_worker_iterations" &&
            tool.Description?.Contains("transient retries", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(tools, tool =>
            tool.ToolName is "workable_query_execution_diagnostics" or "workable_get_execution_diagnostic");
        Assert.Contains(tools, tool =>
            tool.Kind == WorkableMcpServerToolKind.Query &&
            tool.ToolName == "workable_query_worker_keys" &&
            tool.Description?.Contains("claim id", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(tools, tool =>
            tool.Kind == WorkableMcpServerToolKind.Query &&
            tool.ToolName == "workable_query_worker_key_types" &&
            tool.Description?.Contains("claim work", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(tools, tool =>
            tool.Kind == WorkableMcpServerToolKind.Query &&
            tool.ToolName == "workable_query_work_iteration_keys" &&
            tool.Description?.Contains("actual executions", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(tools, tool =>
            tool.Kind == WorkableMcpServerToolKind.Query &&
            tool.ToolName == "workable_query_work_iteration_key_types" &&
            tool.Description?.Contains("claim work", StringComparison.OrdinalIgnoreCase) == true);
        Assert.DoesNotContain(tools, tool => IsWorkflowTool(tool.ToolName));
        Assert.Contains(tools, tool =>
            tool.Kind == WorkableMcpServerToolKind.Action &&
            tool.ToolName == "workable_cancel_worker" &&
            tool.Description?.Contains("Permanently stop", StringComparison.OrdinalIgnoreCase) == true);
        Assert.Contains(tools, tool =>
            tool.Kind == WorkableMcpServerToolKind.Action &&
            tool.ToolName == "workable_reconfigure_work_definition" &&
            tool.Description?.Contains("future queued workers", StringComparison.OrdinalIgnoreCase) == true);

        var workTool = Assert.Single(tools, tool => tool.ToolName == "workable_work_cache_refresh");
        var workSchema = JsonNode.Parse(workTool.InputSchemaJson)
            ?? throw new InvalidOperationException("Expected work tool schema JSON.");
        Assert.NotNull(workSchema["oneOf"]?[1]?["properties"]?["description"]);

        var actionTool = Assert.Single(tools, tool => tool.ToolName == "workable_cancel_worker");
        var actionSchema = JsonNode.Parse(actionTool.InputSchemaJson)
            ?? throw new InvalidOperationException("Expected action tool schema JSON.");
        Assert.NotNull(actionSchema["properties"]?["description"]);

        var reconfigureTool = Assert.Single(tools, tool => tool.ToolName == "workable_reconfigure_work_definition");
        var reconfigureSchema = JsonNode.Parse(reconfigureTool.InputSchemaJson)
            ?? throw new InvalidOperationException("Expected reconfigure tool schema JSON.");
        Assert.NotNull(reconfigureSchema["properties"]?["description"]);
    }

    [Fact]
    public async Task McpServerDisambiguatesWorkToolNameCollisions()
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

        var workTools = (await router.GetTools(CreateMcpRequestContext("List MCP server tools.")))
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

        using var document = JsonDocument.Parse("""{"input":{"message":"hello"},"description":"Queue this work from the MCP router test."}""");
        var result = await router.CallTool(
            "workable_work_echo_message",
            document.RootElement,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Invoke MCP work tool."));
        var session = await CreateMcpSession(system, "Inspect MCP work tool invocation.");
        var workers = await session.Query.Workers(new WorkerCriteria(DefinitionName: "echo.message"));
        var worker = await session.Query.Worker(Assert.Single(workers.Workers).Id)
            ?? throw new InvalidOperationException("Expected worker.");

        Assert.False(result.IsError);
        Assert.Contains("\"status\":\"Completed\"", result.Json);
        Assert.Contains("\"json\":\"{\\u0022message\\u0022:\\u0022hello\\u0022}\"", result.Json);
        Assert.Equal("Queue this work from the MCP router test.", worker.RequestContext.Description);
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

        var tools = await router.GetTools(
            CreateMcpRequestContext("List MCP server tools."),
            systemName: "remote");

        Assert.Contains(tools, tool => tool.ToolName == "workable_work_remote_echo");
        Assert.DoesNotContain(tools, tool => tool.ToolName == "workable_work_default_echo");

        using var document = JsonDocument.Parse("""{"message":"hello remote"}""");
        var result = await router.CallTool(
            "workable_work_remote_echo",
            document.RootElement,
            options: null,
            systemName: "remote",
            requestContext: CreateMcpRequestContext("Invoke MCP work tool."));

        Assert.False(result.IsError);
        Assert.Contains("hello remote", result.Json);
    }

    [Fact]
    public async Task McpRouterReturnsToolErrorForUnknownNamedSystem()
    {
        await using var provider = CreateProvider(WorkDefinition.Create("known", configuration: AllowMcp()), SuccessfulWork);
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();

        var tools = await router.GetTools(
            CreateMcpRequestContext("List MCP server tools."),
            systemName: "missing");
        var result = await router.CallTool(
            "workable_query_workers",
            arguments: null,
            options: null,
            systemName: "missing",
            requestContext: CreateMcpRequestContext("Invoke MCP query tool."));

        Assert.Empty(tools);
        Assert.True(result.IsError);
        Assert.Contains("workable.mcp.system_not_found", result.Json);
    }

    [Fact]
    public async Task McpRouterNamedSystemRequiresAnySystemAccess()
    {
        await using var provider = new ServiceCollection()
            .AddTransportTestAuthorization(Array.Empty<string>())
            .AddWorkableSystem("remote", builder =>
            {
                builder.RequireAuthorization();
                builder.ConfigureTransportSystemAuthorization();
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create("remote.echo", configuration: AllowMcp()),
                    SuccessfulWork);
            })
            .AddWorkableSystem("empty", builder => builder.RequireAuthorization())
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        var requestContext = CreateMcpRequestContext("Invoke MCP named system without connect.");

        var tools = await router.GetTools(
            requestContext,
            systemName: "remote");
        var emptyTools = await router.GetTools(
            requestContext,
            systemName: "empty");
        var result = await router.CallTool(
            "workable_query_work_definitions",
            arguments: null,
            options: null,
            systemName: "remote",
            requestContext: requestContext);

        Assert.Empty(tools);
        Assert.Empty(emptyTools);
        Assert.True(result.IsError);
        Assert.Contains("workable.mcp.system_not_found", result.Json, StringComparison.Ordinal);
        Assert.DoesNotContain("access", result.Json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappedHttpMcpServerListsToolsAndCallsWorkThroughHttpTransport()
    {
        var observedRequestContext = new TaskCompletionSource<WorkRequestContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = await CreateMcpHttpHost(execute: (context, input, cancellationToken) =>
        {
            observedRequestContext.TrySetResult(context.RequestContext);
            return Task.FromResult(WorkExecutionResult.Success(input is null ? WorkOutput.Empty : WorkOutput.FromData(input)));
        });
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/workable/mcp"),
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
        await WaitForReadModel(system);
        var session = await CreateMcpSession(system, "Inspect MCP worker state.");
        var workers = await session.Query.Workers(new WorkerCriteria(DefinitionName: "echo.message"));
        var worker = await session.Query.Worker(Assert.Single(workers.Workers).Id)
            ?? throw new InvalidOperationException("Expected worker.");

        Assert.Equal(WorkInvocationChannel.Mcp, worker.Origin.Channel);
        Assert.Equal("mcp-user-1", worker.Origin.Actor.Id);
        Assert.Equal("mcp.user@example.com", worker.Origin.Actor.Email);
        Assert.Equal("/workable/mcp", worker.RequestContext.Url);

        var executionRequestContext = await observedRequestContext.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(WorkInvocationChannel.Mcp, executionRequestContext.Channel);
        Assert.Equal("mcp-user-1", executionRequestContext.Actor.Id);
    }

    [Fact]
    public async Task MappedHttpMcpEndpointCanTargetNamedSystem()
    {
        using var host = await CreateNamedMcpHttpHost();
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/workable/systems/remote/mcp"),
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
        await WaitForReadModel(remote);
        var session = await CreateMcpSession(remote, "Inspect named MCP worker state.");
        var workers = await session.Query.Workers(new WorkerCriteria(DefinitionName: "remote.echo"));
        var worker = await session.Query.Worker(Assert.Single(workers.Workers).Id)
            ?? throw new InvalidOperationException("Expected worker.");

        Assert.False(result.IsError);
        Assert.Equal(WorkInvocationChannel.Mcp, worker.Origin.Channel);
        Assert.Equal("/workable/systems/remote/mcp", worker.RequestContext.Url);
    }

    [Fact]
    public async Task MappedHttpMcpDefaultEndpointUsesResolvedNamedSystemForAuthorization()
    {
        var observedGroups = new CapturingAuthorizationGroupContextProvider();
        using var host = await CreateNamedDefaultMcpHttpHost(observedGroups);
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/workable/mcp"),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();

        Assert.Contains(tools, tool => tool.Name == "workable_work_remote_echo");
        var observedSystemNames = observedGroups.SystemNames;
        Assert.NotEmpty(observedSystemNames);
        Assert.All(observedSystemNames, systemName => Assert.Equal("remote", systemName));
    }

    [Fact]
    public async Task MappedHttpMcpNamedEndpointRequiresAnySystemAccess()
    {
        using var host = await CreateNamedMcpHttpHost(
            groups: Array.Empty<string>());
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/workable/systems/remote/mcp"),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();
        var result = await client.CallToolAsync(
            "workable_query_work_definitions",
            new Dictionary<string, object?>());
        var json = JsonSerializer.Serialize(result);

        Assert.Empty(tools);
        Assert.True(result.IsError);
        Assert.Contains("workable.mcp.system_not_found", json, StringComparison.Ordinal);
        Assert.DoesNotContain("access", json, StringComparison.OrdinalIgnoreCase);
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
        var session = await CreateMcpSession(remote, "Prepare named MCP worker action.");
        var queue = await session.Queue.Enqueue("remote.manual");
        await WaitForReadModel(remote);
        var worker = await session.Query.Worker(queue.WorkerId ?? throw new InvalidOperationException("Expected worker."))
            ?? throw new InvalidOperationException("Expected worker.");
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/workable/systems/remote/mcp"),
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
                ["description"] = "Cancel this worker from the MCP HTTP transport test.",
            });

        await WaitForReadModel(remote);
        var updated = await session.Query.Worker(worker.Id)
            ?? throw new InvalidOperationException("Expected worker.");
        var history = Assert.Single(updated.ActionHistory);

        Assert.False(result.IsError);
        Assert.Equal(WorkerState.Canceled, updated.State);
        Assert.Equal(WorkAction.Cancel, history.Action);
        Assert.Equal(WorkInvocationChannel.Mcp, history.Origin.Channel);
        Assert.Equal("/workable/systems/remote/mcp", history.RequestContext.Url);
    }

    [Fact]
    public async Task MappedHttpMcpAnonymousRequestIsRejected()
    {
        using var host = await CreateMcpHttpHost(authenticated: false);
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/workable/mcp"),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
        await Assert.ThrowsAnyAsync<Exception>(() => McpClient.CreateAsync(transport));
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var session = await CreateMcpSession(system, "Inspect rejected anonymous MCP request.");
        var workers = await session.Query.Workers(new WorkerCriteria(DefinitionName: "echo.message"));

        Assert.Empty(workers.Workers);
    }

    [Fact]
    public async Task MappedHttpMcpAnonymousRequestIsRejectedBeforeBodyHandling()
    {
        using var host = await CreateMcpHttpHost(authenticated: false);
        var httpClient = host.GetTestClient();
        using var content = new StringContent("{", System.Text.Encoding.UTF8, "application/json");

        using var response = await httpClient.PostAsync("/workable/mcp", content);

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task MappedHttpMcpCanUseExplicitWorkableAuthenticationSchemeWithoutChangingHostDefaultScheme()
    {
        using var host = await CreateExplicitSchemeMcpHttpHost();
        var httpClient = host.GetTestClient();

        using var unauthorizedContent = new StringContent("{", System.Text.Encoding.UTF8, "application/json");
        using var unauthorized = await httpClient.PostAsync("/workable/mcp", unauthorizedContent);
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        Assert.Equal(
            WorkableSchemeAuthenticationTestSupport.WorkableBearerScheme,
            Assert.Single(unauthorized.Headers.GetValues(WorkableSchemeChallengeProbe.HeaderName)));

        httpClient.DefaultRequestHeaders.Authorization = WorkableSchemeAuthenticationTestSupport.CreateBearerHeader();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/workable/mcp"),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();

        Assert.Contains(tools, tool => tool.Name == "workable_work_echo_message");
    }

    [Fact]
    public async Task MappedHttpMcpPreservesAHostRedirectChallenge()
    {
        using var host = await CreateExplicitSchemeMcpHttpHost();
        var challenge = host.Services.GetRequiredService<WorkableSchemeChallengeProbe>();
        challenge.StatusCode = StatusCodes.Status302Found;
        challenge.Location = "/host-login";
        var httpClient = host.GetTestClient();
        using var content = new StringContent("{", System.Text.Encoding.UTF8, "application/json");

        using var response = await httpClient.PostAsync("/workable/mcp", content);

        Assert.Equal(System.Net.HttpStatusCode.Found, response.StatusCode);
        Assert.Equal("/host-login", response.Headers.Location?.OriginalString);
        Assert.Equal(
            WorkableSchemeAuthenticationTestSupport.WorkableBearerScheme,
            Assert.Single(response.Headers.GetValues(WorkableSchemeChallengeProbe.HeaderName)));
        Assert.Empty(await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task MappedHttpMcpPreservesHostFallbackPolicyWhenAWorkableTransportSchemeIsSelected()
    {
        using var host = await CreateExplicitSchemeMcpHttpHost(
            configureFallbackPolicy: true,
            useHostFallbackPolicy: true);
        var httpClient = host.GetTestClient();
        httpClient.DefaultRequestHeaders.Authorization =
            WorkableSchemeAuthenticationTestSupport.CreateBearerHeader();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/workable/mcp"),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var client = await McpClient.CreateAsync(transport);
        });
    }

    [Fact]
    public async Task MappedHttpMcpAppliesTheHostDefaultAuthorizationPolicy()
    {
        using var host = await CreateExplicitSchemeMcpHttpHost(useDefaultPolicy: true);
        var httpClient = host.GetTestClient();
        httpClient.DefaultRequestHeaders.Authorization =
            WorkableSchemeAuthenticationTestSupport.CreateBearerHeader();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/workable/mcp"),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
        {
            await using var client = await McpClient.CreateAsync(transport);
        });
    }

    [Fact]
    public async Task MappedHttpMcpCanUseAHostNamedAuthorizationPolicy()
    {
        const string PolicyName = "HostWorkableMcp";
        using var host = await CreateExplicitSchemeMcpHttpHost(
            useDefaultPolicy: true,
            authorizationPolicy: PolicyName);
        var httpClient = host.GetTestClient();
        httpClient.DefaultRequestHeaders.Authorization =
            WorkableSchemeAuthenticationTestSupport.CreateBearerHeader();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/workable/mcp"),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();

        Assert.Contains(tools, tool => tool.Name == "workable_work_echo_message");
    }

    [Fact]
    public async Task MappedHttpMcpRejectsSelectingNamedAndFallbackPoliciesTogether()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateExplicitSchemeMcpHttpHost(
                authorizationPolicy: "HostWorkableMcp",
                useHostFallbackPolicy: true));

        Assert.Equal("useHostFallbackPolicy", exception.ParamName);
    }

    [Fact]
    public async Task MappedHttpMcpRejectsABlankNamedAuthorizationPolicy()
    {
        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            CreateExplicitSchemeMcpHttpHost(authorizationPolicy: " "));

        Assert.Equal("authorizationPolicy", exception.ParamName);
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
        var session = await CreateMcpSession(system, "Prepare MCP worker action.");
        var queue = await session.Queue.Enqueue("manual.mcp");
        var worker = await session.Query.Worker(queue.WorkerId ?? throw new InvalidOperationException("Expected worker."))
            ?? throw new InvalidOperationException("Expected worker.");
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/workable/mcp"),
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
                ["description"] = "Cancel this worker from the MCP HTTP transport test.",
            });

        await WaitForReadModel(system);
        var updated = await session.Query.Worker(worker.Id)
            ?? throw new InvalidOperationException("Expected worker.");
        var history = Assert.Single(updated.ActionHistory);
        var json = JsonSerializer.Serialize(result);

        Assert.False(result.IsError);
        Assert.Contains("Accepted", json);
        Assert.Equal(WorkerState.Canceled, updated.State);
        Assert.Equal(WorkAction.Cancel, history.Action);
        Assert.Equal(WorkInvocationChannel.Mcp, history.Origin.Channel);
        Assert.Equal("mcp-user-1", history.Origin.Actor.Id);
        Assert.Equal("Cancel this worker from the MCP HTTP transport test.", history.RequestContext.Description);
    }

    [Fact]
    public async Task MappedHttpMcpServerUsesRequestContextAuthorizationForToolDiscovery()
    {
        using var host = await CreateAuthorizedMcpHttpHost();
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/workable/mcp"),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport);

        var tools = await client.ListToolsAsync();

        Assert.Contains(tools, tool => tool.Name == "workable_work_allowed_authorization");
        Assert.DoesNotContain(tools, tool => tool.Name == "workable_work_hidden_authorization");

        var result = await client.CallToolAsync(
            "workable_query_work_definitions",
            new Dictionary<string, object?>
            {
                ["category"] = "",
            });
        var json = JsonSerializer.Serialize(result);

        Assert.False(result.IsError);
        Assert.Contains("allowed.authorization", json);
        Assert.DoesNotContain("hidden.authorization", json);
    }

    [Fact]
    public async Task MappedHttpMcpServerRejectsUnauthorizedWorkerActions()
    {
        using var host = await CreateAuthorizedMcpHttpHost(
            groups: ["billing.read"],
            allowedDefinition: WorkDefinition.Create(
                "allowed.authorization",
                configuration: AllowMcp() with
                {
                    Start = WorkStartConfiguration.DoNotStart,
                }));
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var seedSession = await CreateMcpSessionWithGroups(
            system,
            "Seed MCP unauthorized action test worker.",
            "billing.read",
            "billing.ops");
        var queue = await seedSession.Queue.Enqueue("allowed.authorization");
        await WaitForReadModel(system);
        var worker = await seedSession.Query.Worker(queue.WorkerId ?? throw new InvalidOperationException("Expected worker."))
            ?? throw new InvalidOperationException("Expected worker.");
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/workable/mcp"),
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
        var json = JsonSerializer.Serialize(result);
        var updated = await seedSession.Query.Worker(worker.Id)
            ?? throw new InvalidOperationException("Expected worker.");

        Assert.True(result.IsError);
        Assert.Contains("workable.mcp.tool_not_found", json);
        Assert.Equal(WorkerState.Queued, updated.State);
    }

    [Fact]
    public async Task MappedHttpMcpServerCanStartWorkflow()
    {
        using var host = await CreateMcpHttpHost(builder =>
        {
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("mcp.http.workflow.child", configuration: AllowMcp()),
                SuccessfulWork);
            builder.AddWorkflow(
                WorkflowDefinition.Create("mcp.http.workflow.start"),
                workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("mcp.http.workflow.child")),
                authorize => authorize.AllowOperateToGroups(TransportAuthorizationTestSupport.OperateGroups.ToArray()));
        });
        var system = Assert.IsType<InMemoryWorkSystem>(host.Services.GetRequiredService<IWorkSystemRegistry>().Default);
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/workable/mcp"),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport);

        var result = await client.CallToolAsync(
            "workable_start_workflow",
            new Dictionary<string, object?>
            {
                ["name"] = "mcp.http.workflow.start",
                ["description"] = "Start workflow from the MCP HTTP transport test.",
            });
        var json = ReadToolText(result);
        var runId = new WorkflowRunId(Guid.Parse(
            JsonNode.Parse(json)?["runId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected workflow run id.")));

        WorkflowRunSnapshot? completed = null;
        await TestEventually.Until(
            () =>
            {
                completed = system.WorkflowRuntime.Get(runId);
                return completed?.Status is WorkflowRunStatus.Completed or WorkflowRunStatus.Failed or WorkflowRunStatus.Canceled;
            },
            "Expected the MCP-started workflow to complete.");

        Assert.False(result.IsError);
        Assert.Contains("Accepted", json);
        Assert.Contains("mcp.http.workflow.start", json);
        Assert.True(
            completed?.Status == WorkflowRunStatus.Completed,
            JsonSerializer.Serialize(completed));
        Assert.Equal("mcp.http.workflow.start", completed?.DefinitionName);
        Assert.Single(completed!.Steps.Single(step => step.Name == "dispatch").WorkerIds);
    }

    [Fact]
    public async Task MappedHttpMcpServerCanPauseWorkflowBeforeLaterSteps()
    {
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastRuns = 0;
        using var host = await CreateMcpHttpHost(builder =>
        {
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("mcp.http.workflow.stop.slow", configuration: AllowMcp()),
                async (_, _, cancellationToken) =>
                {
                    slowStarted.TrySetResult();
                    await slowRelease.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("mcp.http.workflow.stop.fast", configuration: AllowMcp()),
                (_, _, _) =>
                {
                    Interlocked.Increment(ref fastRuns);
                    return Task.FromResult(WorkExecutionResult.Success());
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("mcp.http.workflow.stop"),
                workflow => workflow
                    .DispatchWork("slow", WorkDefinition.Create("mcp.http.workflow.stop.slow"))
                    .Join("join")
                    .DispatchWork("fast", WorkDefinition.Create("mcp.http.workflow.stop.fast")),
                authorize => authorize.AllowOperateToGroups(TransportAuthorizationTestSupport.OperateGroups.ToArray()));
        });
        var system = Assert.IsType<InMemoryWorkSystem>(host.Services.GetRequiredService<IWorkSystemRegistry>().Default);
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/workable/mcp"),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport);
        var started = await client.CallToolAsync(
            "workable_start_workflow",
            new Dictionary<string, object?>
            {
                ["name"] = "mcp.http.workflow.stop",
                ["description"] = "Start workflow for the MCP HTTP stop test.",
            });
        var startedJson = JsonNode.Parse(ReadToolText(started))?.AsObject()
            ?? throw new InvalidOperationException("Expected workflow start response.");
        var runId = new WorkflowRunId(Guid.Parse(
            startedJson["runId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected workflow run id.")));
        await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var result = await client.CallToolAsync(
            "workable_stop_workflow",
            new Dictionary<string, object?>
            {
                ["runId"] = runId.Value.ToString("D"),
                ["description"] = "Stop workflow gracefully from the MCP HTTP transport test.",
            });
        var json = ReadToolText(result);
        slowRelease.TrySetResult();

        WorkflowRunSnapshot? completed = null;
        await TestEventually.Until(
            () =>
            {
                completed = system.WorkflowRuntime.Get(runId);
                return completed?.Status == WorkflowRunStatus.Paused;
            },
            "Expected the MCP HTTP-stopped workflow to settle as paused.");

        Assert.False(started.IsError);
        Assert.False(result.IsError);
        Assert.Contains("Accepted", json);
        Assert.Contains("Pause", json);
        Assert.Equal(0, Volatile.Read(ref fastRuns));
        Assert.Equal(WorkflowRunStatus.Paused, completed?.Status);
    }

    [Fact]
    public async Task MappedHttpMcpServerCanCancelWorkflowAndOutstandingChildren()
    {
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = await CreateMcpHttpHost(builder =>
        {
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("mcp.http.workflow.cancel.child", configuration: AllowMcp()),
                async (_, _, cancellationToken) =>
                {
                    childStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("mcp.http.workflow.cancel"),
                workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("mcp.http.workflow.cancel.child")),
                authorize => authorize.AllowOperateToGroups(TransportAuthorizationTestSupport.OperateGroups.ToArray()));
        });
        var system = Assert.IsType<InMemoryWorkSystem>(host.Services.GetRequiredService<IWorkSystemRegistry>().Default);
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/workable/mcp"),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport);
        var started = await client.CallToolAsync(
            "workable_start_workflow",
            new Dictionary<string, object?>
            {
                ["name"] = "mcp.http.workflow.cancel",
                ["description"] = "Start workflow for the MCP HTTP cancel test.",
            });
        var startedJson = JsonNode.Parse(ReadToolText(started))?.AsObject()
            ?? throw new InvalidOperationException("Expected workflow start response.");
        var runId = new WorkflowRunId(Guid.Parse(
            startedJson["runId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected workflow run id.")));
        await childStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var result = await client.CallToolAsync(
            "workable_cancel_workflow",
            new Dictionary<string, object?>
            {
                ["runId"] = runId.Value.ToString("D"),
                ["description"] = "Cancel workflow immediately from the MCP HTTP transport test.",
            });
        var json = ReadToolText(result);

        WorkflowRunSnapshot? completed = null;
        await TestEventually.Until(
            () =>
            {
                completed = system.WorkflowRuntime.Get(runId);
                return completed?.Status == WorkflowRunStatus.Canceled;
            },
            "Expected the MCP HTTP-canceled workflow to settle as canceled.");

        await WaitForReadModel(system);
        var childWorkerId = completed!.Steps.Single(step => step.Name == "dispatch").WorkerIds.Single();
        WorkerSnapshot? child = null;
        await TestEventually.Until(
            async () =>
            {
                child = await (await system.CreateSession(CreateMcpRequestContext("Inspect canceled MCP HTTP workflow child.")))
                    .Query.Worker(childWorkerId);
                return child?.State == WorkerState.Canceled;
            },
            "Expected the canceled MCP HTTP workflow child to settle into the final canceled state.",
            timeout: TimeSpan.FromSeconds(10));

        Assert.False(started.IsError);
        Assert.False(result.IsError);
        Assert.Contains("Accepted", json);
        Assert.Contains("Cancel", json);
        Assert.Equal(WorkflowRunStatus.Canceled, completed.Status);
        Assert.Equal(WorkerState.Canceled, child!.State);
    }

    [Fact]
    public async Task MappedHttpMcpWorkflowStartReturnsToolErrorForUnknownWorkflow()
    {
        using var host = await CreateMcpHttpHost(builder =>
        {
            var child = WorkDefinition.Create("mcp.http.workflow.known.child", configuration: AllowMcp());
            builder.AddAuthorizedTransportWork(child, SuccessfulWork);
            builder.AddWorkflow(
                WorkflowDefinition.Create("mcp.http.workflow.known"),
                workflow => workflow.DispatchWork("child", child),
                authorize => authorize.AllowOperateToGroups(
                    TransportAuthorizationTestSupport.OperateGroups.ToArray()));
        });
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/workable/mcp"),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport);

        var result = await client.CallToolAsync(
            "workable_start_workflow",
            new Dictionary<string, object?>
            {
                ["name"] = "mcp.http.workflow.missing",
            });
        var json = ReadToolText(result);

        Assert.True(result.IsError);
        Assert.Contains("workable.workflow.definition.not_found", json);
    }

    [Fact]
    public async Task MappedHttpMcpWorkflowActionReturnsToolErrorForUnknownRun()
    {
        using var host = await CreateMcpHttpHost(builder =>
        {
            var child = WorkDefinition.Create("mcp.http.workflow.action.child", configuration: AllowMcp());
            builder.AddAuthorizedTransportWork(child, SuccessfulWork);
            builder.AddWorkflow(
                WorkflowDefinition.Create("mcp.http.workflow.action"),
                workflow => workflow.DispatchWork("child", child),
                authorize => authorize.AllowOperateToGroups(
                    TransportAuthorizationTestSupport.OperateGroups.ToArray()));
        });
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/workable/mcp"),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport);

        var result = await client.CallToolAsync(
            "workable_cancel_workflow",
            new Dictionary<string, object?>
            {
                ["runId"] = Guid.NewGuid().ToString("D"),
                ["description"] = "Cancel a missing workflow run from the MCP HTTP transport test.",
            });
        var json = ReadToolText(result);

        Assert.True(result.IsError);
        Assert.Contains("workable.workflow.run.not_found", json);
    }

    [Fact]
    public async Task MappedHttpMcpWorkflowStartRequiresWorkflowOperatePermission()
    {
        using var host = await CreateMcpHttpHost(
            builder =>
            {
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create("mcp.http.workflow.secured.child", configuration: AllowMcp()),
                    SuccessfulWork);
                builder.AddWorkflow(
                    WorkflowDefinition.Create("mcp.http.workflow.secured"),
                    workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("mcp.http.workflow.secured.child")),
                    authorize => authorize.AllowOperateToGroups("workflow.ops"));
            },
            groups: TransportAuthorizationTestSupport.SystemAdministratorGroups);
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/workable/mcp"),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport);

        var result = await client.CallToolAsync(
            "workable_start_workflow",
            new Dictionary<string, object?>
            {
                ["name"] = "mcp.http.workflow.secured",
            });
        var json = ReadToolText(result);

        Assert.True(result.IsError);
        Assert.Contains("workable.mcp.tool_not_found", json);
    }

    [Fact]
    public async Task MappedHttpMcpWorkflowActionReturnsToolErrorForInvalidArguments()
    {
        using var host = await CreateMcpHttpHost(builder =>
        {
            var child = WorkDefinition.Create("mcp.http.workflow.invalid.child", configuration: AllowMcp());
            builder.AddAuthorizedTransportWork(child, SuccessfulWork);
            builder.AddWorkflow(
                WorkflowDefinition.Create("mcp.http.workflow.invalid"),
                workflow => workflow.DispatchWork("child", child),
                authorize => authorize.AllowOperateToGroups(
                    TransportAuthorizationTestSupport.OperateGroups.ToArray()));
        });
        var httpClient = host.GetTestClient();
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions
            {
                Endpoint = new Uri("http://localhost/workable/mcp"),
            },
            httpClient,
            loggerFactory: null,
            ownsHttpClient: false);
        await using var client = await McpClient.CreateAsync(transport);

        var result = await client.CallToolAsync(
            "workable_stop_workflow",
            new Dictionary<string, object?>());
        var json = ReadToolText(result);

        Assert.True(result.IsError);
        Assert.Contains("workable.mcp.arguments_invalid", json);
    }

    [Fact]
    public async Task McpServerQueryToolsCanObserveWorkers()
    {
        var definition = WorkDefinition.Create("reports.generate", "Generates report.", configuration: AllowMcp());
        await using var provider = CreateProvider(definition, SuccessfulWork);
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        await system.Start();

        var session = await CreateMcpSession(system, "Queue MCP worker for query tool test.");
        var handle = await session.Queue.Enqueue(
            "reports.generate",
            WorkInput.Empty,
            options: new WorkerOptions(ProfilingEnabled: true));
        var completion = await handle.WaitForCompletion();
        await WaitForReadModel(system);
        Assert.Equal(WorkCompletionStatus.Completed, completion.Status);

        using var queryArguments = JsonDocument.Parse("""{"workName":"reports.generate","states":["Completed"],"profilingEnabled":true}""");
        var queryResult = await router.CallTool(
            "workable_query_workers",
            queryArguments.RootElement,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Invoke MCP query tool."));
        var statusResult = await router.CallTool(
            "workable_get_worker_status_summary",
            queryArguments.RootElement,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Invoke MCP query tool."));

        Assert.False(queryResult.IsError);
        Assert.Contains("\"totalCount\":1", queryResult.Json);
        Assert.Contains(handle.WorkerId!.Value.Value.ToString("D"), queryResult.Json);
        Assert.False(statusResult.IsError);
        Assert.Contains("\"Completed\":1", statusResult.Json);
    }

    [Fact]
    public async Task McpServerQueryToolsCanObserveWorkerIterations()
    {
        var definition = WorkDefinition.Create("claim.review", "Reviews a claim.", category: "Claims", configuration: AllowMcp());
        await using var provider = CreateProvider(definition, (context, input, cancellationToken) =>
        {
            context.AddIdentifier(new WorkIdentifier("claim-note", "CLM-123-note"));
            return Task.FromResult(WorkExecutionResult.Success());
        });
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        await system.Start();

        var session = await CreateMcpSession(system, "Queue MCP worker iteration test.");
        var handle = await session.Queue.Enqueue(
            "claim.review",
            WorkInput.Empty
                .WithSubject(new WorkSubjectId("claim", "CLM-123"))
                .WithIdentifier(new WorkIdentifier("invoice", "INV-456")));
        await handle.WaitForCompletion();
        await WaitForReadModel(system);

        using var queryArguments = JsonDocument.Parse("""{"workName":"claim.review","statuses":["Completed"],"identifierType":"claim-note","identifierValue":"CLM-123-note"}""");
        using var getArguments = JsonDocument.Parse($$"""{"workerId":"{{handle.WorkerId!.Value.Value:D}}","sequence":1}""");
        var queryResult = await router.CallTool(
            "workable_query_worker_iterations",
            queryArguments.RootElement,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Invoke MCP query tool."));
        var getResult = await router.CallTool(
            "workable_get_worker_iteration",
            getArguments.RootElement,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Invoke MCP query tool."));

        Assert.False(queryResult.IsError);
        Assert.Contains("\"totalCount\":1", queryResult.Json);
        Assert.Contains("\"definitionName\":\"claim.review\"", queryResult.Json);
        Assert.Contains("\"sequence\":1", queryResult.Json);
        Assert.False(getResult.IsError);
        Assert.Contains("\"found\":true", getResult.Json);
        Assert.Contains("\"status\":\"Completed\"", getResult.Json);
    }

    [Fact]
    public async Task McpServerQueryToolsCanSearchWorkerAndIterationKeys()
    {
        var definition = WorkDefinition.Create("claim.review", "Reviews a claim.", configuration: AllowMcp());
        await using var provider = CreateProvider(definition, (context, input, cancellationToken) =>
        {
            context.AddIdentifier(new WorkIdentifier("claim-note", "CLM-123-note"));
            return Task.FromResult(WorkExecutionResult.Success());
        });
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        await system.Start();

        var session = await CreateMcpSession(system, "Queue MCP key query test.");
        var handle = await session.Queue.Enqueue(
            "claim.review",
            WorkInput.Empty
                .WithSubject(new WorkSubjectId("claim", "CLM-123"))
                .WithConcurrencyKey(new WorkConcurrencyKey("tenant", "west"))
                .WithIdentifier(new WorkIdentifier("invoice", "INV-456")));
        await handle.WaitForCompletion();
        await WaitForReadModel(system);

        using var keysArguments = JsonDocument.Parse("""{"search":"claim id CLM-123"}""");
        using var typesArguments = JsonDocument.Parse("""{"search":"claim work"}""");
        var requestContext = CreateMcpRequestContext("Invoke MCP query tool.");
        var keysResult = await router.CallTool("workable_query_worker_keys", keysArguments.RootElement, null, null, requestContext);
        var typesResult = await router.CallTool("workable_query_worker_key_types", typesArguments.RootElement, null, null, requestContext);
        var iterationKeysResult = await router.CallTool("workable_query_work_iteration_keys", keysArguments.RootElement, null, null, requestContext);
        var iterationTypesResult = await router.CallTool("workable_query_work_iteration_key_types", typesArguments.RootElement, null, null, requestContext);

        Assert.False(keysResult.IsError);
        Assert.Contains("\"kind\":\"Subject\"", keysResult.Json);
        Assert.Contains("\"type\":\"claim\"", keysResult.Json);
        Assert.Contains("\"value\":\"CLM-123\"", keysResult.Json);
        Assert.Contains("\"workers\":[", keysResult.Json);
        Assert.Contains("\"definitionName\":\"claim.review\"", keysResult.Json);
        Assert.Contains("\"type\":\"claim-note\"", keysResult.Json);
        Assert.False(typesResult.IsError);
        Assert.Contains("\"type\":\"claim\"", typesResult.Json);
        Assert.Contains("\"workerCount\":1", typesResult.Json);
        Assert.Contains("\"workers\":[", typesResult.Json);
        Assert.False(iterationKeysResult.IsError);
        Assert.Contains("\"kind\":\"Subject\"", iterationKeysResult.Json);
        Assert.Contains("\"type\":\"claim\"", iterationKeysResult.Json);
        Assert.Contains("\"value\":\"CLM-123\"", iterationKeysResult.Json);
        Assert.Contains("\"iterations\":[", iterationKeysResult.Json);
        Assert.Contains("\"status\":\"Completed\"", iterationKeysResult.Json);
        Assert.False(iterationTypesResult.IsError);
        Assert.Contains("\"type\":\"claim\"", iterationTypesResult.Json);
        Assert.Contains("\"iterationCount\":1", iterationTypesResult.Json);
        Assert.Contains("\"iterations\":[", iterationTypesResult.Json);
    }

    [Fact]
    public async Task McpServerGetWorkerReturnsSnapshot()
    {
        var definition = WorkDefinition.Create("data.import", "Imports data.", configuration: AllowMcp());
        await using var provider = CreateProvider(definition, SuccessfulWork);
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        await system.Start();

        var session = await CreateMcpSession(system, "Queue MCP worker snapshot test.");
        var handle = await session.Queue.Enqueue("data.import", WorkInput.Empty);
        await handle.WaitForCompletion();

        using var arguments = JsonDocument.Parse($$"""{"workerId":"{{handle.WorkerId!.Value.Value:D}}"}""");
        var result = await router.CallTool(
            "workable_get_worker",
            arguments.RootElement,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Invoke MCP query tool."));

        Assert.False(result.IsError);
        Assert.Contains("\"found\":true", result.Json);
        Assert.Contains("\"definitionName\":\"data.import\"", result.Json);
    }

    [Fact]
    public async Task McpServerGetWorkInfoDistinguishesKnownMissingAndBlankNames()
    {
        var definition = WorkDefinition.Create("mcp.work.info", "Describes MCP work.", configuration: AllowMcp());
        await using var provider = CreateProvider(definition, SuccessfulWork);
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        using var knownArguments = JsonDocument.Parse("""{"name":"mcp.work.info"}""");
        using var missingArguments = JsonDocument.Parse("""{"name":"mcp.work.missing"}""");
        using var blankArguments = JsonDocument.Parse("""{"name":" "}""");

        var known = await router.CallTool(
            "workable_get_work_info",
            knownArguments.RootElement,
            null,
            null,
            CreateMcpRequestContext("Get known work info."));
        var missing = await router.CallTool(
            "workable_get_work_info",
            missingArguments.RootElement,
            null,
            null,
            CreateMcpRequestContext("Get missing work info."));
        var blank = await router.CallTool(
            "workable_get_work_info",
            blankArguments.RootElement,
            null,
            null,
            CreateMcpRequestContext("Get blank work info."));

        Assert.Contains("\"found\":true", known.Json);
        Assert.Contains("mcp.work.info", known.Json);
        Assert.Contains("\"found\":false", missing.Json);
        Assert.Contains("mcp.work.missing", missing.Json);
        Assert.Contains("\"found\":false", blank.Json);
    }

    [Fact]
    public async Task McpDefinitionReconfigurationReturnsNotFoundAndConvertsMalformedJsonToToolErrors()
    {
        await using var provider = CreateProvider(
            WorkDefinition.Create("mcp.reconfigure.known", configuration: AllowMcp()),
            SuccessfulWork);
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        using var missingArguments = JsonDocument.Parse("""{"name":"mcp.reconfigure.missing","revision":0,"defaultOptions":{"profilingEnabled":true}}""");
        using var malformedArguments = JsonDocument.Parse("""{"name":"mcp.reconfigure.known","revision":0,"changes":{"configuration":"invalid"}}""");

        var missing = await router.CallTool(
            "workable_reconfigure_work_definition",
            missingArguments.RootElement,
            null,
            null,
            CreateMcpRequestContext("Reconfigure missing work."));
        var malformed = await router.CallTool(
            "workable_reconfigure_work_definition",
            malformedArguments.RootElement,
            null,
            null,
            CreateMcpRequestContext("Reconfigure work with malformed JSON."));

        Assert.Contains("\"status\":\"NotFound\"", missing.Json);
        Assert.True(malformed.IsError);
        Assert.Contains("workable.mcp.arguments_invalid", malformed.Json);
    }

    [Theory]
    [InlineData("{\"name\":\"mcp.reconfigure.strict\",\"revision\":0}")]
    [InlineData("{\"name\":\"mcp.reconfigure.strict\",\"revision\":0,\"changes\":{}}")]
    [InlineData("{\"name\":\"mcp.reconfigure.strict\",\"revision\":0,\"changes\":\"invalid\"}")]
    [InlineData("{\"name\":\"mcp.reconfigure.strict\",\"revision\":0,\"defaultOptions\":\"invalid\"}")]
    [InlineData("{\"name\":\"mcp.reconfigure.strict\",\"revision\":0,\"configuration\":[]}")]
    [InlineData("{\"name\":\"mcp.reconfigure.strict\",\"revision\":0,\"changes\":{\"defaultOptions\":{}},\"defaultOptions\":{}}")]
    [InlineData("{\"name\":\"mcp.reconfigure.strict\",\"revision\":0,\"changes\":{\"unsupported\":{}}}")]
    [InlineData("{\"name\":\"mcp.reconfigure.strict\",\"revision\":0,\"changes\":{\"configuration\":{},\"Configuration\":{}}}")]
    [InlineData("{\"name\":\"mcp.reconfigure.strict\",\"revision\":0,\"defaultOptions\":{},\"DefaultOptions\":{}}")]
    [InlineData("{\"name\":\"mcp.reconfigure.strict\",\"revision\":0,\"defaultOptions\":{\"profilngEnabled\":true}}")]
    [InlineData("{\"name\":\"mcp.reconfigure.strict\",\"revision\":0,\"defaultOptions\":{\"profilingEnabled\":true},\"unsupported\":true}")]
    [InlineData("{\"name\":\"mcp.reconfigure.strict\",\"revision\":0,\"defaultOptions\":{\"profilingEnabled\":true,\"ProfilingEnabled\":false}}")]
    [InlineData("{\"name\":\"mcp.reconfigure.strict\",\"revision\":0,\"configuration\":{\"coordination\":{\"mode\":\"Local\",\"Mode\":\"Persistent\"}}}")]
    [InlineData("{\"name\":\"mcp.reconfigure.strict\",\"revision\":0,\"configuration\":{}}")]
    [InlineData("{\"name\":\"mcp.reconfigure.strict\",\"revision\":0,\"defaultOptions\":{\"unsupportedArray\":[{\"value\":true}]}}")]
    public async Task McpDefinitionReconfigurationRejectsAmbiguousOrMalformedChangeShapes(string json)
    {
        var definition = WorkDefinition.Create("mcp.reconfigure.strict", configuration: AllowMcp());
        await using var provider = CreateProvider(definition, SuccessfulWork);
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        using var arguments = JsonDocument.Parse(json);

        var result = await router.CallTool(
            "workable_reconfigure_work_definition",
            arguments.RootElement,
            null,
            null,
            CreateMcpRequestContext("Reject malformed definition reconfiguration."));

        Assert.True(result.IsError);
        Assert.Contains("workable.mcp.arguments_invalid", result.Json, StringComparison.Ordinal);
        var session = await CreateMcpSession(
            provider.GetRequiredService<IWorkSystemRegistry>().Default,
            "Verify malformed reconfiguration changed nothing.");
        Assert.Equal(0, Assert.Single(session.Catalog.Definitions).Revision);
    }

    [Fact]
    public async Task McpDefinitionReconfigurationRejectsUndefinedProfilingCaptureMode()
    {
        var definition = WorkDefinition.Create("mcp.reconfigure.undefined-profile", configuration: AllowMcp());
        await using var provider = CreateProvider(definition, SuccessfulWork);
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        using var arguments = JsonDocument.Parse("""
            {
              "name": "mcp.reconfigure.undefined-profile",
              "revision": 0,
              "defaultOptions": {
                "profilingCaptureMode": 999
              }
            }
            """);

        var result = await router.CallTool(
            "workable_reconfigure_work_definition",
            arguments.RootElement,
            null,
            null,
            CreateMcpRequestContext("Reject undefined profiling capture mode."));
        var session = await CreateMcpSession(
            provider.GetRequiredService<IWorkSystemRegistry>().Default,
            "Verify undefined profiling capture mode changed nothing.");

        Assert.True(result.IsError);
        Assert.Contains("workable.options.profiling_capture_mode.invalid", result.Json, StringComparison.Ordinal);
        Assert.Equal(0, Assert.Single(session.Catalog.Definitions).Revision);
    }

    [Fact]
    public async Task McpDefinitionReconfigurationAcceptsNestedChanges()
    {
        var definition = WorkDefinition.Create("mcp.reconfigure.nested", configuration: AllowMcp());
        await using var provider = CreateProvider(definition, SuccessfulWork);
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        using var arguments = JsonDocument.Parse("""
            {
              "name": "mcp.reconfigure.nested",
              "revision": 0,
              "changes": {
                "defaultOptions": {
                  "profilingEnabled": true
                }
              }
            }
            """);

        var result = await router.CallTool(
            "workable_reconfigure_work_definition",
            arguments.RootElement,
            null,
            null,
            CreateMcpRequestContext("Apply nested definition reconfiguration."));

        Assert.False(result.IsError);
        Assert.Contains("\"status\":\"Accepted\"", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpCustomSystemDoesNotExpandCoarseOperateCountsIntoActionTools()
    {
        await using var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new FixedGroupProvider(["coarse.operate"]))
            .AddWorkableSystem(builder =>
            {
                builder.RequireAuthorization();
                builder.AddWork(
                    WorkDefinition.Create("mcp.coarse.operation", configuration: AllowMcp()),
                    SuccessfulWork,
                    configure: null,
                    authorize: authorization => authorization.AllowOperateToGroups("coarse.operate"));
            })
            .BuildServiceProvider();
        var inner = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await inner.Start();
        var coarseSystem = new CoarseOperationWorkSystem(inner);
        var router = new WorkableMcpToolRouter(new SingleSystemRegistry(coarseSystem));
        var options = new WorkableMcpServerOptions
        {
            IncludeWorkTools = false,
            IncludeQueryTools = false,
            IncludeActionTools = true,
        };
        var requestContext = CreateMcpRequestContext("Discover conservative custom-system tools.");

        var tools = await router.GetTools(requestContext, options);
        using var arguments = JsonDocument.Parse("""{"workerId":"11111111-1111-1111-1111-111111111111","revision":0}""");
        var directCall = await router.CallTool(
            "workable_start_worker",
            arguments.RootElement,
            options,
            null,
            requestContext);

        Assert.Empty(tools);
        Assert.True(directCall.IsError);
        Assert.Contains("workable.mcp.tool_not_found", directCall.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpServerOptionsCanDisableEveryBuiltInToolCategory()
    {
        var definition = WorkDefinition.Create("mcp.options.disabled", configuration: AllowMcp());
        await using var provider = CreateProvider(definition, SuccessfulWork);
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        var options = new WorkableMcpServerOptions
        {
            IncludeWorkTools = false,
            IncludeQueryTools = false,
            IncludeActionTools = false,
        };
        var requestContext = CreateMcpRequestContext("Verify an intentionally empty MCP surface.");

        var tools = await router.GetTools(requestContext, options);
        var result = await router.CallTool(
            "workable_query_workers",
            arguments: null,
            options,
            systemName: null,
            requestContext);

        Assert.Empty(tools);
        Assert.True(result.IsError);
        Assert.Contains("workable.mcp.tool_not_found", result.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpServerCanReconfigureWorkDefinitionDefaults()
    {
        var definition = WorkDefinition.Create("mcp.definition.reconfigure", "Can change defaults.", configuration: AllowMcp());
        await using var provider = CreateProvider(definition, SuccessfulWork);
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        await system.Start();

        using var arguments = JsonDocument.Parse($$"""
            {
              "description": "Reconfigure defaults from the MCP tool test.",
              "name": "{{definition.Name}}",
              "revision": {{definition.Revision}},
              "defaultOptions": {
                "profilingEnabled": true
              }
            }
            """);
        var result = await router.CallTool(
            "workable_reconfigure_work_definition",
            arguments.RootElement,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Invoke MCP action tool."));
        var session = await CreateMcpSession(system, "Inspect reconfigured MCP definition.");
        var handle = await session.Queue.Enqueue(definition.Name);
        var worker = await session.Query.Worker(handle.WorkerId ?? throw new InvalidOperationException("Expected worker."));

        Assert.False(result.IsError);
        Assert.Contains("\"status\":\"Accepted\"", result.Json);
        Assert.Contains("\"revision\":1", result.Json);
        Assert.NotNull(worker);
        Assert.True(worker.Options.ProfilingEnabled);
    }

    [Fact]
    public async Task McpServerCanStartWorkflowWithInput()
    {
        WorkInput? captured = null;
        await using var provider = new ServiceCollection()
            .AddTransportTestAuthorization()
            .AddWorkableSystem(builder =>
            {
                builder.RequireAuthorization();
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create("mcp.workflow.input.child", configuration: AllowMcp()),
                    (_, input, _) =>
                    {
                        captured = input;
                        return Task.FromResult(WorkExecutionResult.Success());
                    });
                builder.AddWorkflow(
                    WorkflowDefinition.Create("mcp.workflow.input"),
                    workflow => workflow.DispatchWorkFromWorkflowInput(
                        "dispatch",
                        WorkDefinition.Create("mcp.workflow.input.child")),
                    authorize => authorize.AllowOperateToGroups(TransportAuthorizationTestSupport.OperateGroups.ToArray()));
            })
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        await system.Start();

        var workflowRequestContext = CreateMcpRequestContext("Start MCP workflow with input.");
        var workflowAccess = await system.DescribeAccess(workflowRequestContext);
        Assert.Equal(1, workflowAccess.OperableWorkflowDefinitionCount);
        using var arguments = JsonDocument.Parse("""
            {
              "name": "mcp.workflow.input",
              "waitForCompletion": true,
              "input": {
                "externalKey": "mcp-42"
              }
            }
            """);
        var result = await router.CallTool(
            "workable_start_workflow",
            arguments.RootElement,
            options: null,
            systemName: null,
            requestContext: workflowRequestContext);

        Assert.False(result.IsError);
        Assert.Contains("\"status\":\"Completed\"", result.Json);
        Assert.NotNull(captured);
        var payload = captured!.ToValue<WorkflowMcpInput>()
            ?? throw new InvalidOperationException("Expected MCP workflow input payload.");
        Assert.Equal("mcp-42", payload.ExternalKey);
        Assert.Contains(
            captured.Identifiers!,
            identifier => identifier.Type == "workflow-run");
        Assert.DoesNotContain(
            captured.Identifiers!,
            identifier => identifier.Type is "workflow-definition" or "workflow-step");
    }

    [Fact]
    public async Task McpServerCanStartAndPauseWorkflow()
    {
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastRuns = 0;
        await using var provider = new ServiceCollection()
            .AddTransportTestAuthorization()
            .AddWorkableSystem(builder =>
            {
                builder.RequireAuthorization();
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create("mcp.workflow.slow", configuration: AllowMcp()),
                    async (_, _, cancellationToken) =>
                    {
                        slowStarted.TrySetResult();
                        await slowRelease.Task.WaitAsync(cancellationToken);
                        return WorkExecutionResult.Success();
                    });
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create("mcp.workflow.fast", configuration: AllowMcp()),
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref fastRuns);
                        return Task.FromResult(WorkExecutionResult.Success());
                    });
                builder.AddWorkflow(
                    WorkflowDefinition.Create("mcp.workflow.stop"),
                    workflow => workflow
                        .DispatchWork("slow", WorkDefinition.Create("mcp.workflow.slow"))
                        .Join("join")
                        .DispatchWork("fast", WorkDefinition.Create("mcp.workflow.fast")),
                    authorize => authorize.AllowOperateToGroups(TransportAuthorizationTestSupport.OperateGroups.ToArray()));
            })
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        await system.Start();

        using var startArguments = JsonDocument.Parse("""{"name":"mcp.workflow.stop"}""");
        var started = await router.CallTool(
            "workable_start_workflow",
            startArguments.RootElement,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Start MCP workflow."));
        var startedJson = JsonNode.Parse(started.Json)?.AsObject()
            ?? throw new InvalidOperationException("Expected workflow start response.");
        var runId = startedJson["runId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected workflow run id.");
        await slowStarted.Task.WaitAsync(CancellationToken.None);

        using var stopArguments = JsonDocument.Parse($$"""{"runId":"{{runId}}"}""");
        var stopped = await router.CallTool(
            "workable_stop_workflow",
            stopArguments.RootElement,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Stop MCP workflow."));
        var stoppedJson = JsonNode.Parse(stopped.Json)?.AsObject()
            ?? throw new InvalidOperationException("Expected workflow stop response.");
        slowRelease.TrySetResult();

        WorkflowRunSnapshot? completed = null;
        await TestEventually.Until(
            () =>
            {
                completed = ((InMemoryWorkSystem)system).WorkflowRuntime.Get(new WorkflowRunId(Guid.Parse(runId)));
                return completed?.Status == WorkflowRunStatus.Paused;
            },
            "Expected the MCP-stopped workflow to settle as paused.");

        Assert.False(started.IsError);
        Assert.False(stopped.IsError);
        Assert.Null(startedJson["run"]);
        Assert.Null(stoppedJson["run"]);
        Assert.Equal(0, Volatile.Read(ref fastRuns));
        Assert.Equal(WorkflowRunStatus.Paused, completed!.Status);
    }

    [Fact]
    public async Task McpServerCanPauseAndResumeWorkflowRunsThroughExplicitRunTools()
    {
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastRuns = 0;
        await using var provider = new ServiceCollection()
            .AddTransportTestAuthorization()
            .AddWorkableSystem(builder =>
            {
                builder.RequireAuthorization();
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create("mcp.workflow.pause.slow", configuration: AllowMcp()),
                    async (_, _, cancellationToken) =>
                    {
                        slowStarted.TrySetResult();
                        await slowRelease.Task.WaitAsync(cancellationToken);
                        return WorkExecutionResult.Success();
                    });
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create("mcp.workflow.pause.fast", configuration: AllowMcp()),
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref fastRuns);
                        return Task.FromResult(WorkExecutionResult.Success());
                    });
                builder.AddWorkflow(
                    WorkflowDefinition.Create("mcp.workflow.pause"),
                    workflow => workflow
                        .DispatchWork("slow", WorkDefinition.Create("mcp.workflow.pause.slow"))
                        .Join("join")
                        .DispatchWork("fast", WorkDefinition.Create("mcp.workflow.pause.fast")),
                    authorize => authorize.AllowOperateToGroups(TransportAuthorizationTestSupport.OperateGroups.ToArray()));
            })
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        await system.Start();

        using var startArguments = JsonDocument.Parse("""{"name":"mcp.workflow.pause"}""");
        var started = await router.CallTool(
            "workable_start_workflow",
            startArguments.RootElement,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Start MCP workflow for explicit run-tool control."));
        var startedJson = JsonNode.Parse(started.Json)?.AsObject()
            ?? throw new InvalidOperationException("Expected workflow start response.");
        var runId = startedJson["runId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected workflow run id.");
        await slowStarted.Task.WaitAsync(CancellationToken.None);

        using var pauseArguments = JsonDocument.Parse($$"""{"runId":"{{runId}}"}""");
        var paused = await router.CallTool(
            "workable_pause_workflow_run",
            pauseArguments.RootElement,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Pause MCP workflow through the explicit run tool."));

        slowRelease.TrySetResult();

        WorkflowRunSnapshot? pausedRun = null;
        await TestEventually.Until(
            () =>
            {
                pausedRun = ((InMemoryWorkSystem)system).WorkflowRuntime.Get(new WorkflowRunId(Guid.Parse(runId)));
                return pausedRun?.Status == WorkflowRunStatus.Paused;
            },
            "Expected the MCP-paused workflow to settle as paused.");

        Assert.False(started.IsError);
        Assert.False(paused.IsError);
        Assert.Contains("Accepted", paused.Json);
        Assert.Equal(0, Volatile.Read(ref fastRuns));

        using var resumeArguments = JsonDocument.Parse($$"""{"runId":"{{runId}}"}""");
        var resumed = await router.CallTool(
            "workable_start_workflow_run",
            resumeArguments.RootElement,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Resume MCP workflow through the explicit run tool."));

        WorkflowRunSnapshot? completed = null;
        await TestEventually.Until(
            () =>
            {
                completed = ((InMemoryWorkSystem)system).WorkflowRuntime.Get(new WorkflowRunId(Guid.Parse(runId)));
                return completed?.Status == WorkflowRunStatus.Completed;
            },
            "Expected the MCP-started workflow run to resume and complete.");

        Assert.False(resumed.IsError);
        Assert.Contains("Accepted", resumed.Json);
        Assert.Equal(1, Volatile.Read(ref fastRuns));
        Assert.Equal(WorkflowRunStatus.Completed, completed!.Status);
    }

    [Fact]
    public async Task McpServerCanCancelWorkflow()
    {
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = new ServiceCollection()
            .AddTransportTestAuthorization()
            .AddWorkableSystem(builder =>
            {
                builder.RequireAuthorization();
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create("mcp.workflow.cancel.child", configuration: AllowMcp()),
                    async (_, _, cancellationToken) =>
                    {
                        childStarted.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return WorkExecutionResult.Success();
                    });
                builder.AddWorkflow(
                    WorkflowDefinition.Create("mcp.workflow.cancel"),
                    workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("mcp.workflow.cancel.child")),
                    authorize => authorize.AllowOperateToGroups(TransportAuthorizationTestSupport.OperateGroups.ToArray()));
            })
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        await system.Start();

        using var startArguments = JsonDocument.Parse("""{"name":"mcp.workflow.cancel"}""");
        var started = await router.CallTool(
            "workable_start_workflow",
            startArguments.RootElement,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Start cancelable MCP workflow."));
        var startedJson = JsonNode.Parse(started.Json)?.AsObject()
            ?? throw new InvalidOperationException("Expected workflow start response.");
        var runId = startedJson["runId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected workflow run id.");
        await childStarted.Task.WaitAsync(CancellationToken.None);

        using var cancelArguments = JsonDocument.Parse($$"""{"runId":"{{runId}}"}""");
        var canceled = await router.CallTool(
            "workable_cancel_workflow",
            cancelArguments.RootElement,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Cancel MCP workflow."));

        WorkflowRunSnapshot? completed = null;
        await TestEventually.Until(
            () =>
            {
                completed = ((InMemoryWorkSystem)system).WorkflowRuntime.Get(new WorkflowRunId(Guid.Parse(runId)));
                return completed?.Status == WorkflowRunStatus.Canceled;
            },
            "Expected the MCP-canceled workflow to complete as canceled.");

        var childWorkerId = completed!.Steps.Single(step => step.Name == "dispatch").WorkerIds.Single();
        WorkerSnapshot? child = null;
        await TestEventually.Until(
            async () =>
            {
                child = await (await system.CreateSession(CreateMcpRequestContext("Inspect canceled MCP workflow child.")))
                    .Query.Worker(childWorkerId);
                return child?.State == WorkerState.Canceled;
            },
            "Expected the canceled MCP workflow child to settle into the final canceled state.",
            timeout: TimeSpan.FromSeconds(10));

        Assert.False(started.IsError);
        Assert.False(canceled.IsError);
        Assert.Equal(WorkflowRunStatus.Canceled, completed.Status);
        Assert.Equal(WorkerState.Canceled, child!.State);
    }

    [Fact]
    public async Task McpServerCanQueryWorkflowRunsAndGetWorkflowRunDetail()
    {
        var emailStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoiceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = new ServiceCollection()
            .AddTransportTestAuthorization()
            .AddWorkableSystem(builder =>
            {
                builder.RequireAuthorization();
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create("mcp.workflow.observe.email", configuration: AllowMcp()),
                    async (_, _, cancellationToken) =>
                    {
                        emailStarted.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                        return WorkExecutionResult.Success();
                    });
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create("mcp.workflow.observe.invoice", configuration: AllowMcp()),
                    async (_, _, cancellationToken) =>
                    {
                        invoiceStarted.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                        return WorkExecutionResult.Success();
                    });
                builder.AddWorkflow(
                    WorkflowDefinition.Create("mcp.workflow.observe"),
                    workflow => workflow
                        .RunParallel("notify", parallel => parallel
                            .DispatchWork("email", WorkDefinition.Create("mcp.workflow.observe.email"))
                            .DispatchWork("invoice", WorkDefinition.Create("mcp.workflow.observe.invoice")))
                        .Join("settle"),
                    authorize: authorize => authorize
                        .AllowReadToGroups(TransportAuthorizationTestSupport.ReadGroups.ToArray())
                        .AllowOperateToGroups(TransportAuthorizationTestSupport.OperateGroups.ToArray()));
            })
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        await system.Start();

        using var startArguments = JsonDocument.Parse("""{"name":"mcp.workflow.observe"}""");
        var started = await router.CallTool(
            "workable_start_workflow",
            startArguments.RootElement,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Start observable MCP workflow."));
        var startedJson = JsonNode.Parse(started.Json)?.AsObject()
            ?? throw new InvalidOperationException("Expected workflow start response.");
        var runId = startedJson["runId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected workflow run id.");
        await Task.WhenAll(emailStarted.Task, invoiceStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));

        using var queryArguments = JsonDocument.Parse("""{"childSampleSize":3}""");
        var queried = await router.CallTool(
            "workable_query_workflow_runs",
            queryArguments.RootElement,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Query workflow runs."));
        var queriedJson = JsonNode.Parse(queried.Json)?.AsObject()
            ?? throw new InvalidOperationException("Expected workflow query response.");

        using var detailArguments = JsonDocument.Parse($$"""{"runId":"{{runId}}","childSampleSize":3}""");
        var detailed = await router.CallTool(
            "workable_get_workflow_run",
            detailArguments.RootElement,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Get workflow run detail."));
        var detailJson = JsonNode.Parse(detailed.Json)?.AsObject()
            ?? throw new InvalidOperationException("Expected workflow detail response.");

        release.TrySetResult();

        Assert.False(started.IsError);
        Assert.False(queried.IsError);
        Assert.False(detailed.IsError);
        var runs = queriedJson["runs"]?.AsArray()
            ?? throw new InvalidOperationException("Expected workflow runs array.");
        var run = Assert.Single(runs, item => string.Equals(item?["runId"]?.GetValue<string>(), runId, StringComparison.OrdinalIgnoreCase))!;
        Assert.Equal("mcp.workflow.observe", run["definitionName"]?.GetValue<string>());
        Assert.Equal("notify", run["currentStepName"]?.GetValue<string>());
        Assert.Equal(2, run["outstandingChildren"]?["total"]?.GetValue<int>());

        Assert.True(detailJson["found"]?.GetValue<bool>() ?? false);
        Assert.Null(detailJson["run"]?["definitionName"]);
        var notify = detailJson["run"]?["steps"]?.AsArray()
            ?.Single(step => string.Equals(step?["name"]?.GetValue<string>(), "notify", StringComparison.Ordinal));
        var notifyChildren = notify?["steps"]?.AsArray()
            .Select(step => step?["name"]?.GetValue<string>() ?? string.Empty)
            .ToArray()
            ?? throw new InvalidOperationException("Expected notify child steps.");
        Assert.Equal(["email", "invoice"], notifyChildren);
    }

    [Fact]
    public async Task McpServerWorkflowQueriesSupportFiltersAndNotFound()
    {
        var emailStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoiceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var provider = new ServiceCollection()
            .AddTransportTestAuthorization()
            .AddWorkableSystem(builder =>
            {
                builder.RequireAuthorization();
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create("mcp.workflow.filter.email", configuration: AllowMcp()),
                    async (_, _, cancellationToken) =>
                    {
                        emailStarted.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                        return WorkExecutionResult.Success();
                    });
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create("mcp.workflow.filter.invoice", configuration: AllowMcp()),
                    async (_, _, cancellationToken) =>
                    {
                        invoiceStarted.TrySetResult();
                        await release.Task.WaitAsync(cancellationToken);
                        return WorkExecutionResult.Success();
                    });
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create("mcp.workflow.filter.done.child", configuration: AllowMcp()),
                    SuccessfulWork);
                builder.AddWorkflow(
                    WorkflowDefinition.Create("mcp.workflow.filter.running"),
                    workflow => workflow
                        .RunParallel("notify", parallel => parallel
                            .DispatchWork("email", WorkDefinition.Create("mcp.workflow.filter.email"))
                            .DispatchWork("invoice", WorkDefinition.Create("mcp.workflow.filter.invoice")))
                        .Join("settle"),
                    authorize: authorize => authorize
                        .AllowReadToGroups(TransportAuthorizationTestSupport.ReadGroups.ToArray())
                        .AllowOperateToGroups(TransportAuthorizationTestSupport.OperateGroups.ToArray()));
                builder.AddWorkflow(
                    WorkflowDefinition.Create("mcp.workflow.filter.completed"),
                    workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("mcp.workflow.filter.done.child")),
                    authorize: authorize => authorize
                        .AllowReadToGroups(TransportAuthorizationTestSupport.ReadGroups.ToArray())
                        .AllowOperateToGroups(TransportAuthorizationTestSupport.OperateGroups.ToArray()));
            })
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        await system.Start();

        using var runningStartArguments = JsonDocument.Parse("""{"name":"mcp.workflow.filter.running"}""");
        var runningStarted = await router.CallTool("workable_start_workflow", runningStartArguments.RootElement, null, null, CreateMcpRequestContext("Start running workflow."));
        var runId = JsonNode.Parse(runningStarted.Json)?["runId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected workflow run id.");
        using var completedStartArguments = JsonDocument.Parse("""{"name":"mcp.workflow.filter.completed","waitForCompletion":true}""");
        await router.CallTool("workable_start_workflow", completedStartArguments.RootElement, null, null, CreateMcpRequestContext("Start completed workflow."));
        await Task.WhenAll(emailStarted.Task, invoiceStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));

        using var filteredArguments = JsonDocument.Parse("""{"definitionName":"mcp.workflow.filter.running"}""");
        var filtered = await router.CallTool("workable_query_workflow_runs", filteredArguments.RootElement, null, null, CreateMcpRequestContext("Filter workflow runs."));
        var filteredJson = JsonNode.Parse(filtered.Json)?.AsObject() ?? throw new InvalidOperationException("Expected workflow query response.");
        Assert.Single(filteredJson["runs"]?.AsArray() ?? throw new InvalidOperationException("Expected workflow runs."));

        using var pagedArguments = JsonDocument.Parse("""{"includeFinal":true,"skip":1,"take":1}""");
        var paged = await router.CallTool("workable_query_workflow_runs", pagedArguments.RootElement, null, null, CreateMcpRequestContext("Page workflow runs."));
        var pagedJson = JsonNode.Parse(paged.Json)?.AsObject() ?? throw new InvalidOperationException("Expected paged workflow query response.");
        Assert.Single(pagedJson["runs"]?.AsArray() ?? throw new InvalidOperationException("Expected paged workflow runs."));
        Assert.Equal(2, pagedJson["totalCount"]?.GetValue<int>());
        Assert.Equal(1, pagedJson["skip"]?.GetValue<int>());
        Assert.Equal(1, pagedJson["take"]?.GetValue<int>());

        using var detailArguments = JsonDocument.Parse($$"""{"runId":"{{runId}}","childSampleSize":1}""");
        var detail = await router.CallTool("workable_get_workflow_run", detailArguments.RootElement, null, null, CreateMcpRequestContext("Get workflow detail."));
        var detailJson = JsonNode.Parse(detail.Json)?.AsObject() ?? throw new InvalidOperationException("Expected workflow detail response.");
        var notify = detailJson["run"]?["steps"]?.AsArray()
            ?.Single(step => string.Equals(step?["name"]?.GetValue<string>(), "notify", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Expected notify step.");
        Assert.Equal(1, notify["childSample"]?.AsArray()?.Count);
        Assert.Null(notify["additionalChildCount"]);

        using var missingArguments = JsonDocument.Parse($$"""{"runId":"{{Guid.NewGuid():D}}"}""");
        var missing = await router.CallTool("workable_get_workflow_run", missingArguments.RootElement, null, null, CreateMcpRequestContext("Get missing workflow detail."));
        var missingJson = JsonNode.Parse(missing.Json)?.AsObject() ?? throw new InvalidOperationException("Expected workflow detail response.");
        using var negativeSampleArguments = JsonDocument.Parse("""{"childSampleSize":-1}""");
        var negativeSample = await router.CallTool(
            "workable_query_workflow_runs",
            negativeSampleArguments.RootElement,
            null,
            null,
            CreateMcpRequestContext("Reject negative child sample size."));
        using var oversizedSampleArguments = JsonDocument.Parse(
            $$"""{"runId":"{{runId}}","childSampleSize":{{WorkflowRunViewAdapter.MaximumChildSampleSize + 1}}}""");
        var oversizedSample = await router.CallTool(
            "workable_get_workflow_run",
            oversizedSampleArguments.RootElement,
            null,
            null,
            CreateMcpRequestContext("Reject oversized child sample size."));
        using var oversizedPageArguments = JsonDocument.Parse(
            $$"""{"take":{{WorkflowRunViewAdapter.MaximumRunPageSize + 1}}}""");
        var oversizedPage = await router.CallTool(
            "workable_query_workflow_runs",
            oversizedPageArguments.RootElement,
            null,
            null,
            CreateMcpRequestContext("Reject oversized workflow page."));
        using var oversizedSkipArguments = JsonDocument.Parse(
            $$"""{"skip":{{WorkflowRunViewAdapter.MaximumRunPageSkip + 1}}}""");
        var oversizedSkip = await router.CallTool(
            "workable_query_workflow_runs",
            oversizedSkipArguments.RootElement,
            null,
            null,
            CreateMcpRequestContext("Reject oversized workflow offset."));

        release.TrySetResult();

        Assert.False(missing.IsError);
        Assert.False(missingJson["found"]?.GetValue<bool>() ?? true);
        Assert.True(negativeSample.IsError);
        Assert.True(oversizedSample.IsError);
        Assert.True(oversizedPage.IsError);
        Assert.True(oversizedSkip.IsError);
        Assert.Contains("between 0 and 25", negativeSample.Json);
        Assert.Contains("between 0 and 25", oversizedSample.Json);
        Assert.Contains("between 1 and 100", oversizedPage.Json);
        Assert.Contains($"between 0 and {WorkflowRunViewAdapter.MaximumRunPageSkip}", oversizedSkip.Json);
    }

    [Fact]
    public async Task McpServerWorkflowQueriesHideUnreadableWorkflowRuns()
    {
        await using var provider = new ServiceCollection()
            .AddTransportTestAuthorization()
            .AddWorkableSystem(builder =>
            {
                builder.RequireAuthorization();
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create("mcp.workflow.read.secured.child", configuration: AllowMcp()),
                    SuccessfulWork);
                builder.AddWorkflow(
                    WorkflowDefinition.Create("mcp.workflow.read.secured"),
                    workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("mcp.workflow.read.secured.child")),
                    authorize: authorize => authorize
                        .AllowReadToGroups("workflow.read")
                        .AllowOperateToGroups("workflow.ops"));
            })
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        await system.Start();

        using var startArguments = JsonDocument.Parse("""{"name":"mcp.workflow.read.secured","waitForCompletion":true}""");
        var actor = TransportAuthorizationTestSupport.CreateActor(
            id: "mcp-seed-user-1",
            name: "MCP Seed User",
            email: "mcp.seed@example.test");
        var seedContext = WorkRequestContext.Create(
            WorkInvocationChannel.Mcp,
            actor,
            "Seed readable workflow.") with
        {
            Authorization = WorkAuthorizationSnapshot.CreateForSystem(
                systemName: null,
                actor,
                ["workflow.read", "workflow.ops"],
                readableDefinitionIds: null),
        };
        var started = await router.CallTool(
            "workable_start_workflow",
            startArguments.RootElement,
            options: null,
            systemName: null,
            requestContext: seedContext);
        var runId = JsonNode.Parse(started.Json)?["runId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected workflow run id.");

        using var queryArguments = JsonDocument.Parse("""{}""");
        var hiddenList = await router.CallTool(
            "workable_query_workflow_runs",
            queryArguments.RootElement,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Query workflow runs without workflow read."));

        using var detailArguments = JsonDocument.Parse($$"""{"runId":"{{runId}}"}""");
        var hiddenDetail = await router.CallTool(
            "workable_get_workflow_run",
            detailArguments.RootElement,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Get workflow detail without workflow read."));

        Assert.Contains("workable.mcp.tool_not_found", hiddenList.Json, StringComparison.Ordinal);
        Assert.Contains("workable.mcp.tool_not_found", hiddenDetail.Json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task McpServerReturnsToolErrorForUnknownTool()
    {
        await using var provider = CreateProvider(WorkDefinition.Create("known", configuration: AllowMcp()), SuccessfulWork);
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();

        var result = await router.CallTool(
            "missing_tool",
            arguments: null,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Invoke missing MCP tool."));

        Assert.True(result.IsError);
        Assert.Contains("workable.mcp.tool_not_found", result.Json);
    }

    [Fact]
    public async Task McpServerReturnsToolErrorForInvalidArguments()
    {
        await using var provider = CreateProvider(WorkDefinition.Create("known", configuration: AllowMcp()), SuccessfulWork);
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();

        var result = await router.CallTool(
            "workable_cancel_worker",
            arguments: null,
            options: null,
            systemName: null,
            requestContext: CreateMcpRequestContext("Invoke MCP action tool with invalid arguments."));

        Assert.True(result.IsError);
        Assert.Contains("workable.mcp.arguments_invalid", result.Json);
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
            .AddTransportTestAuthorization()
            .AddWorkableSystem(builder =>
            {
                builder.RequireAuthorization();
                builder.AddAuthorizedTransportWork(definition, execute);
            })
            .AddWorkableMcpServer()
            .BuildServiceProvider()
            ;

    private static Task<IHost> CreateMcpHttpHost(
        bool authenticated = true,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>>? execute = null,
        WorkDefinition? definition = null,
        IEnumerable<string>? groups = null)
        => CreateMcpHttpHost(
            builder =>
            {
                execute ??= (context, input, cancellationToken) =>
                    Task.FromResult(WorkExecutionResult.Success(input is null ? WorkOutput.Empty : WorkOutput.FromData(input)));
                definition ??= WorkDefinition.Create(
                    "echo.message",
                    "Echoes input.",
                    configuration: AllowMcp());
                builder.AddAuthorizedTransportWork(definition, execute);
            },
            authenticated,
            groups);

    private static async Task<IHost> CreateMcpHttpHost(
        Action<IWorkSystemBuilder> configure,
        bool authenticated = true,
        IEnumerable<string>? groups = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddTransportTestAuthorization(groups);
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        configure(builder);
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
                            context.User = TransportAuthorizationTestSupport.CreateTransportPrincipal(
                                id: "mcp-user-1",
                                name: "MCP User",
                                email: "mcp.user@example.com",
                                groups: groups);
                            await next();
                        });
                    }

                    app.UseEndpoints(endpoints => endpoints.MapWorkableMcp(useHostFallbackPolicy: true));
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> CreateNamedMcpHttpHost(
        WorkDefinition? remoteDefinition = null,
        IEnumerable<string>? groups = null)
    {
        remoteDefinition ??= WorkDefinition.Create("remote.echo", configuration: AllowMcp());

        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddTransportTestAuthorization(groups);
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        builder.AddAuthorizedTransportWork(
                            WorkDefinition.Create("default.echo", configuration: AllowMcp()),
                            SuccessfulWork);
                    });
                    services.AddWorkableSystem("remote", builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        builder.AddAuthorizedTransportWork(remoteDefinition, (context, input, cancellationToken) =>
                            Task.FromResult(WorkExecutionResult.Success(input is null ? WorkOutput.Empty : WorkOutput.FromData(input))));
                    });
                    services.AddWorkableMcpServer();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.Use(async (context, next) =>
                    {
                        context.User = TransportAuthorizationTestSupport.CreateTransportPrincipal(
                            id: "mcp-user-1",
                            name: "MCP User",
                            email: "mcp.user@example.com",
                            groups: groups);
                        await next();
                    });
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapWorkableMcp(useHostFallbackPolicy: true);
                        endpoints.MapWorkableMcp(
                            "/workable/systems/remote/mcp",
                            systemName: "remote",
                            useHostFallbackPolicy: true);
                    });
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> CreateNamedDefaultMcpHttpHost(
        CapturingAuthorizationGroupContextProvider observedGroups)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddTransportTestAuthorization();
                    services.AddSingleton<IWorkAuthorizationGroupContextProvider>(observedGroups);
                    services.AddWorkableSystem("remote", builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.AddAuthorizedTransportWork(
                            WorkDefinition.Create("remote.echo", configuration: AllowMcp()),
                            SuccessfulWork);
                    });
                    services.AddWorkableMcpServer();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.Use(async (context, next) =>
                    {
                        context.User = TransportAuthorizationTestSupport.CreateTransportPrincipal(
                            id: "mcp-user-1",
                            name: "MCP User",
                            email: "mcp.user@example.com");
                        await next();
                    });
                    app.UseEndpoints(endpoints => endpoints.MapWorkableMcp(useHostFallbackPolicy: true));
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> CreateAuthorizedMcpHttpHost(
        IEnumerable<string>? groups = null,
        WorkDefinition? allowedDefinition = null)
    {
        groups ??= ["billing.read", "billing.ops"];
        allowedDefinition ??= WorkDefinition.Create("allowed.authorization", configuration: AllowMcp());
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
                        builder.RequireAuthorization();
                        builder.AddWork(
                            allowedDefinition,
                            SuccessfulWork,
                            configure: null,
                            authorize: authorize => authorize.RequireGroups(
                                readGroups: ["billing.read"],
                                operateGroups: ["billing.ops"]));
                        builder.AddWork(
                            WorkDefinition.Create("hidden.authorization", configuration: AllowMcp()),
                            SuccessfulWork,
                            configure: null,
                            authorize: authorize => authorize.RequireGroups(
                                readGroups: ["hidden.read"],
                                operateGroups: ["hidden.ops"]));
                    });
                    services.AddWorkableMcpServer();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.Use(async (context, next) =>
                    {
                        context.User = new ClaimsPrincipal(new ClaimsIdentity(
                            CreateAuthenticatedClaims(groups),
                            "Test"));
                        await next();
                    });
                    app.UseEndpoints(endpoints => endpoints.MapWorkableMcp(useHostFallbackPolicy: true));
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> CreateExplicitSchemeMcpHttpHost(
        bool configureFallbackPolicy = false,
        bool useDefaultPolicy = false,
        string? authorizationPolicy = null,
        bool useHostFallbackPolicy = false)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddWorkableSchemeTestAuthentication();
                    if (configureFallbackPolicy || useDefaultPolicy || !string.IsNullOrWhiteSpace(authorizationPolicy))
                    {
                        services.AddAuthorization(options =>
                        {
                            var policy = new AuthorizationPolicyBuilder(
                                WorkableSchemeAuthenticationTestSupport.AmbientScheme)
                                .RequireClaim("host-app")
                                .Build();
                            if (useDefaultPolicy)
                            {
                                options.DefaultPolicy = policy;
                            }
                            else if (configureFallbackPolicy)
                            {
                                options.FallbackPolicy = policy;
                            }

                            if (!string.IsNullOrWhiteSpace(authorizationPolicy))
                            {
                                options.AddPolicy(
                                    authorizationPolicy,
                                    new AuthorizationPolicyBuilder(
                                        WorkableSchemeAuthenticationTestSupport.WorkableBearerScheme)
                                        .RequireAuthenticatedUser()
                                        .Build());
                            }
                        });
                    }
                    services.AddTransportTestAuthorization();
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        builder.AddAuthorizedTransportWork(
                            WorkDefinition.Create("echo.message", configuration: AllowMcp()),
                            SuccessfulWork);
                    });
                    services.AddWorkableMcpServer();
                });
                web.Configure(app =>
                {
                    app.UseAuthentication();
                    app.UseRouting();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapWorkableMcp(
                        authorizationPolicy: authorizationPolicy,
                        useHostFallbackPolicy: useHostFallbackPolicy));
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

    private static ValueTask<IWorkSystemSession> CreateMcpSession(
        IWorkSystem system,
        string description)
        => TransportAuthorizationTestSupport.CreateTransportSession(
            system,
            WorkInvocationChannel.Mcp,
            description: description);

    [Fact]
    public async Task McpRouterDispatchesEveryWorkerActionTool()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create("mcp.worker.action.branches", configuration: AllowMcp()),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success())))
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        var requestContext = CreateMcpRequestContext("Dispatch every worker action tool.");
        using var arguments = JsonDocument.Parse(
            $$"""{"workerId":"{{Guid.NewGuid():D}}","revision":0}""");

        foreach (var toolName in new[]
        {
            "workable_start_worker",
            "workable_pause_worker",
            "workable_cancel_worker",
            "workable_push_worker",
            "workable_purge_worker",
        })
        {
            var result = await router.CallTool(
                toolName,
                arguments.RootElement,
                options: null,
                systemName: null,
                requestContext);

            Assert.DoesNotContain("workable.mcp.tool_not_found", result.Json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task McpRouterDispatchesEveryAdvertisedQueryTool()
    {
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder => builder
                .AddWork(
                    WorkDefinition.Create("mcp.query.branches", configuration: AllowMcp()),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()))
                .AddWorkflow(
                    WorkflowDefinition.Create("mcp.query.workflow.branches"),
                    workflow => workflow.DispatchWork(
                        "child",
                        WorkDefinition.Create("mcp.query.branches", configuration: AllowMcp()))))
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        var requestContext = CreateMcpRequestContext("Dispatch every advertised query tool.");
        var queryTools = (await router.GetTools(requestContext))
            .Where(tool => tool.Kind == WorkableMcpServerToolKind.Query)
            .ToArray();
        using var arguments = JsonDocument.Parse($$"""
            {
              "workerId": "{{Guid.NewGuid():D}}",
              "sequence": 1,
              "runId": "{{Guid.NewGuid():D}}",
              "name": "mcp.query.branches",
              "take": 1
            }
            """);

        Assert.True(queryTools.Length >= 10);
        foreach (var tool in queryTools)
        {
            var result = await router.CallTool(
                tool.ToolName,
                arguments.RootElement,
                options: null,
                systemName: null,
                requestContext);

            Assert.DoesNotContain("workable.mcp.tool_not_found", result.Json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task McpRouterDispatchesEveryAdvertisedActionTool()
    {
        var definition = WorkDefinition.Create("mcp.action.branches", configuration: AllowMcp());
        await using var provider = new ServiceCollection()
            .AddWorkableSystem(builder => builder
                .AddWork(
                    definition,
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()))
                .AddWorkflow(
                    WorkflowDefinition.Create("mcp.action.workflow.branches"),
                    workflow => workflow.DispatchWork("child", definition)))
            .AddWorkableMcpServer()
            .BuildServiceProvider();
        var router = provider.GetRequiredService<WorkableMcpToolRouter>();
        var requestContext = CreateMcpRequestContext("Dispatch every advertised action tool.");
        var actionTools = (await router.GetTools(requestContext))
            .Where(tool => tool.Kind == WorkableMcpServerToolKind.Action)
            .ToArray();
        using var arguments = JsonDocument.Parse($$"""
            {
              "workerId": "{{Guid.NewGuid():D}}",
              "revision": 0,
              "runId": "{{Guid.NewGuid():D}}",
              "name": "mcp.action.workflow.branches",
              "defaultOptions": { "profilingEnabled": true }
            }
            """);

        Assert.True(actionTools.Length >= 10);
        foreach (var tool in actionTools)
        {
            var result = await router.CallTool(
                tool.ToolName,
                arguments.RootElement,
                options: null,
                systemName: null,
                requestContext);

            Assert.DoesNotContain("workable.mcp.tool_not_found", result.Json, StringComparison.Ordinal);
        }
    }

    private static WorkRequestContext CreateMcpRequestContext(string description)
        => WorkRequestContext.Create(
            WorkInvocationChannel.Mcp,
            TransportAuthorizationTestSupport.CreateActor(
                id: "mcp-router-user-1",
                name: "MCP Router User",
                email: "mcp.router@example.test"),
            description);

    private static string ReadToolText(CallToolResult result)
        => Assert.IsType<TextContentBlock>(Assert.Single(result.Content)).Text;

    private static ValueTask<IWorkSystemSession> CreateMcpSessionWithGroups(
        IWorkSystem system,
        string description,
        params string[] groups)
    {
        var actor = TransportAuthorizationTestSupport.CreateActor(
            id: "mcp-seed-user-1",
            name: "MCP Seed User",
            email: "mcp.seed@example.test");
        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.Mcp,
            actor,
            description) with
        {
            Authorization = WorkAuthorizationSnapshot.CreateForSystem(
                system.Name,
                actor,
                groups,
                readableDefinitionIds: null),
        };
        return system.CreateSession(requestContext);
    }

    private static IEnumerable<Claim> CreateAuthenticatedClaims(IEnumerable<string> groups)
    {
        yield return new Claim(ClaimTypes.NameIdentifier, "mcp-auth-user-1");

        foreach (var group in groups)
        {
            yield return new Claim("groups", group);
        }
    }

    private static async Task WaitForReadModel(IWorkSystem system)
    {
        var session = await CreateMcpSessionWithGroups(
            system,
            "Wait for MCP test read model projection.",
            InternalWorkAuthorizationGroups.SystemAdministrator);
        await TestEventually.Until(
            () => session.Diagnostics.ReadModel.PendingUpdateCount == 0,
            "Expected the MCP test read model projection to drain.");
    }

    private static WorkConfiguration AllowMcp()
        => WorkConfiguration.Default with
        {
            Invocation = WorkInvocationConfiguration.Allow(
                WorkInvocationChannel.InProcess,
                WorkInvocationChannel.HttpApi,
                WorkInvocationChannel.Mcp),
        };

    private static bool IsWorkflowTool(string toolName)
        => toolName is
            "workable_start_workflow" or
            "workable_start_workflow_run" or
            "workable_pause_workflow_run" or
            "workable_stop_workflow" or
            "workable_cancel_workflow" or
            "workable_query_workflow_runs" or
            "workable_get_workflow_run";

    private sealed class FixedGroupProvider(IEnumerable<string> groups) : IWorkAuthorizationGroupProvider
    {
        private readonly IReadOnlySet<string> groups = groups.ToHashSet(StringComparer.OrdinalIgnoreCase);

        public ValueTask<IReadOnlySet<string>> GetGroups(
            WorkActor actor,
            string? systemName,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(this.groups);
    }

    private sealed class CapturingAuthorizationGroupContextProvider : IWorkAuthorizationGroupContextProvider
    {
        private readonly object gate = new();
        private readonly List<string?> systemNames = [];

        public IReadOnlyList<string?> SystemNames
        {
            get
            {
                lock (this.gate)
                {
                    return [.. this.systemNames];
                }
            }
        }

        public ValueTask<IReadOnlySet<string>?> GetCurrentGroups(
            WorkActor actor,
            string? systemName,
            CancellationToken cancellationToken = default)
        {
            lock (this.gate)
            {
                this.systemNames.Add(systemName);
            }

            return ValueTask.FromResult<IReadOnlySet<string>?>(null);
        }
    }

    private sealed class CoarseOperationWorkSystem(IWorkSystem inner) : IWorkSystem
    {
        public WorkSystemId Id => inner.Id;

        public string? Name => inner.Name;

        public bool RequiresAuthorization => inner.RequiresAuthorization;

        public WorkSystemState State => inner.State;

        public IWorkCatalog Catalog => inner.Catalog;

        public IWorkQueueService Queue => inner.Queue;

        public IWorkerOperations Workers => inner.Workers;

        public IWorkQueryService Query => inner.Query;

        public IWorkEventStream Events => inner.Events;

        public IWorkIterationStatusStream IterationStatuses => inner.IterationStatuses;

        public IWorkChangeStream Changes => inner.Changes;

        public IWorkSystemDiagnostics Diagnostics => inner.Diagnostics;

        public async ValueTask<WorkSystemAccessSummary> DescribeAccess(
            WorkRequestContext requestContext,
            CancellationToken cancellationToken = default)
        {
            var access = await inner.DescribeAccess(requestContext, cancellationToken);
            return access with
            {
                CanOperateAllWork = false,
            };
        }

        public ValueTask<IWorkSystemSession> CreateSession(
            WorkRequestContext requestContext,
            CancellationToken cancellationToken = default)
            => inner.CreateSession(requestContext, cancellationToken);

        public Task Start(WorkRequestContext requestContext, CancellationToken cancellationToken = default)
            => inner.Start(requestContext, cancellationToken);

        public Task<WorkSystemStopResult> Stop(
            WorkRequestContext requestContext,
            CancellationToken cancellationToken = default)
            => inner.Stop(requestContext, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SingleSystemRegistry(IWorkSystem system) : IWorkSystemRegistry
    {
        public IWorkSystem Default => system;

        public IReadOnlyCollection<IWorkSystem> Systems => [system];

        public bool TryGet(string name, [NotNullWhen(true)] out IWorkSystem? workSystem)
        {
            workSystem = string.Equals(name, system.Name, StringComparison.OrdinalIgnoreCase)
                ? system
                : null;
            return workSystem is not null;
        }
    }

    private sealed class TestEndpointRouteBuilder(IServiceProvider services) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = services;

        public ICollection<EndpointDataSource> DataSources { get; } = [];

        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(this.ServiceProvider);
    }

    private sealed record WorkflowMcpInput(string ExternalKey);
}
