using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Workable;

namespace Workable.Tests;

[Trait("Category", "HttpApi")]
public sealed class WorkableHttpApiTests
{
    private static readonly string[] CompletedFailedStates = ["Completed", "Failed"];
    private static readonly string[] CompletedStatuses = ["Completed"];

    [Fact]
    public async Task HttpApiReturnsAfterAcceptedByDefault()
    {
        var (system, http) = CreateHost(WorkDefinition.Create("http.default"), (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Success(input is null ? WorkOutput.Empty : WorkOutput.FromData(input))));
        await system.Start();

        using var input = JsonDocument.Parse("""{"id":"123"}""");
        var result = await http.Queue.Enqueue(system.Name, "http.default", DirectRequestContext(), new WorkableHttpWorkRequest(input.RootElement));

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
        var result = await http.Queue.Enqueue(system.Name,
            "http.wait",
            DirectRequestContext(),
            new WorkableHttpWorkRequest(input.RootElement, WorkableHttpCompletion.WaitForCompletion));

        Assert.Equal(WorkableHttpWorkStatus.Completed, result.Status);
        Assert.Equal("""{"id":"123"}""", result.Output?.Json);
    }

    [Fact]
    public async Task HttpApiCanQueueByName()
    {
        var definition = WorkDefinition.Create("http.by-name");
        var (system, http) = CreateHost(definition, (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Success(input is null ? WorkOutput.Empty : WorkOutput.FromData(input))));
        await system.Start();

        using var input = JsonDocument.Parse("""{"id":"by-name"}""");
        var result = await http.Queue.Enqueue(system.Name,
            definition.Name,
            DirectRequestContext(),
            new WorkableHttpWorkRequest(input.RootElement, WorkableHttpCompletion.WaitForCompletion));

        Assert.Equal(WorkableHttpWorkStatus.Completed, result.Status);
        Assert.Equal("""{"id":"by-name"}""", result.Output?.Json);
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
        var result = await http.Queue.Enqueue(system.Name,
            "http.metadata",
            DirectRequestContext(),
            new WorkableHttpWorkRequest(
                input.RootElement,
                Options: new WorkableHttpWorkerOptions(ProfilingEnabled: true),
                SubjectId: new WorkSubjectId("user", "123"),
                ConcurrencyKey: new WorkConcurrencyKey("tenant", "abc"),
                Identifiers: new HashSet<WorkIdentifier> { new("invoice", "456") }));
        var worker = await Direct(system).Query.Worker(result.WorkerId ?? throw new InvalidOperationException("Expected worker id."));

        Assert.Equal(WorkableHttpWorkStatus.Accepted, result.Status);
        Assert.NotNull(worker);
        Assert.True(worker.Options.ProfilingEnabled);
        Assert.Equal(new WorkSubjectId("user", "123"), worker.SubjectId);
        Assert.Equal(new WorkConcurrencyKey("tenant", "abc"), worker.ConcurrencyKey);
        Assert.Contains(new WorkIdentifier("invoice", "456"), worker.Identifiers);
    }

    [Fact]
    public async Task MappedHttpRoutesCreateListConsumeAndDeleteFullProfileCaptureRules()
    {
        const string definitionName = "http.profile-capture-rule";
        using var host = await CreateHttpHost(builder =>
        {
            builder.ConfigureProfiling(maximumAutomaticInstrumentationNodes: 1);
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create(definitionName),
                SuccessfulWork);
        }, groups: TransportAuthorizationTestSupport.SystemAdministratorGroups
            .Concat(TransportAuthorizationTestSupport.OperateGroups));
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;

        var createdResponse = await client.PostAsJsonAsync(
            "/workable/profiling/capture-rules",
            new
            {
                definitionName,
                maximumMatches = 2,
                expiresAfterMinutes = 15,
                description = "Investigate slow orders",
            });
        createdResponse.EnsureSuccessStatusCode();
        var created = await createdResponse.Content.ReadFromJsonAsync<WorkableHttpProfilingCaptureRule>()
            ?? throw new InvalidOperationException("Expected a profile capture rule.");

        var state = await client.GetFromJsonAsync<WorkableHttpProfilingCaptureState>(
            "/workable/profiling/capture-rules")
            ?? throw new InvalidOperationException("Expected profile capture state.");

        Assert.Equal(definitionName, created.DefinitionName);
        Assert.Equal("user-123", created.CreatedBy.Id);
        Assert.Equal(2, created.RemainingMatches);
        Assert.Equal(1, state.MaximumAutomaticInstrumentationNodes);
        Assert.Equal(created.Id, Assert.Single(state.Rules).Id);

        var first = await (await Direct(system).Queue.Enqueue(definitionName)).WaitForCompletion();
        var firstWorker = first.Worker ?? throw new InvalidOperationException(
            $"Expected completed worker, received {first.Status}: {string.Join("; ", first.Messages.Select(message => message.Text))}");
        Assert.True(firstWorker.Options.ProfilingEnabled);
        Assert.Equal(WorkProfileCaptureMode.Full, firstWorker.Options.ProfilingCaptureMode);
        Assert.NotNull(firstWorker.Profile);

        state = await client.GetFromJsonAsync<WorkableHttpProfilingCaptureState>(
            "/workable/profiling/capture-rules")
            ?? throw new InvalidOperationException("Expected profile capture state.");
        Assert.Equal(1, Assert.Single(state.Rules).RemainingMatches);

        var deleteResponse = await client.DeleteAsync($"/workable/profiling/capture-rules/{created.Id}");
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        Assert.Empty((await client.GetFromJsonAsync<WorkableHttpProfilingCaptureState>(
            "/workable/profiling/capture-rules"))!.Rules);
    }

    [Fact]
    public async Task MappedHttpRouteRejectsInvalidProfileCaptureRule()
    {
        using var host = await CreateHttpHost(
            builder => builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("http.profile-capture-invalid"),
                SuccessfulWork),
            groups: TransportAuthorizationTestSupport.SystemAdministratorGroups);
        var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync(
            "/workable/profiling/capture-rules",
            new
            {
                maximumMatches = 0,
                expiresAfterMinutes = 30,
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(
            "workable.profiling.capture_rule.invalid",
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);

        var oversizedSelector = await client.PostAsJsonAsync(
            "/workable/profiling/capture-rules",
            new
            {
                actorId = new string('a', WorkProfileCaptureRuleStore.MaximumSelectorLength + 1),
            });

        Assert.Equal(HttpStatusCode.BadRequest, oversizedSelector.StatusCode);
        Assert.Contains(
            WorkProfileCaptureRuleStore.MaximumSelectorLength.ToString(),
            await oversizedSelector.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task MappedHttpActorCaptureRuleAppliesToWorkQueuedByThatUser()
    {
        const string definitionName = "http.profile-capture-actor";
        using var host = await CreateHttpHost(
            builder => builder.AddAuthorizedTransportWork(
                WorkDefinition.Create(definitionName),
                SuccessfulWork),
            groups: TransportAuthorizationTestSupport.SystemAdministratorGroups
                .Concat(TransportAuthorizationTestSupport.OperateGroups));
        var client = host.GetTestClient();

        (await client.PostAsJsonAsync(
            "/workable/profiling/capture-rules",
            new
            {
                actorId = "user-123",
                maximumMatches = 1,
                expiresAfterMinutes = 5,
            })).EnsureSuccessStatusCode();

        var queueResponse = await client.PostAsJsonAsync(
            $"/workable/work/{definitionName}",
            new { completion = "waitForCompletion" });
        queueResponse.EnsureSuccessStatusCode();
        var queueJson = JsonNode.Parse(await queueResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected queue result.");
        var workerId = new WorkerId(Guid.Parse(
            queueJson["workerId"]?["value"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Expected queued worker id.")));
        var worker = await Direct(host.Services.GetRequiredService<IWorkSystemRegistry>().Default)
            .Query.Worker(workerId)
            ?? throw new InvalidOperationException("Expected queued worker.");

        Assert.Equal("Completed", queueJson["status"]?.GetValue<string>());
        Assert.True(worker.Options.ProfilingEnabled);
        Assert.Equal(
            WorkProfileCaptureMode.Full,
            worker.Options.ProfilingCaptureMode);
        Assert.Empty((await client.GetFromJsonAsync<WorkableHttpProfilingCaptureState>(
            "/workable/profiling/capture-rules"))!.Rules);
    }

    [Fact]
    public async Task MappedHttpProfileCaptureRulesRequireDiagnosticsAccessInsteadOfSystemControl()
    {
        using var deniedHost = await CreateHttpHost(
            groups: TransportAuthorizationTestSupport.WorkAdministratorGroups);
        var deniedClient = deniedHost.GetTestClient();

        var deniedList = await deniedClient.GetAsync("/workable/profiling/capture-rules");
        var deniedCreate = await deniedClient.PostAsJsonAsync(
            "/workable/profiling/capture-rules",
            new { definitionName = "http.route.case" });

        Assert.Equal(HttpStatusCode.Forbidden, deniedList.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, deniedCreate.StatusCode);

        using var allowedHost = await CreateHttpHost(
            groups: TransportAuthorizationTestSupport.WorkAdministratorGroups
                .Concat(TransportAuthorizationTestSupport.DiagnosticsGroups));
        var allowedClient = allowedHost.GetTestClient();

        var createdResponse = await allowedClient.PostAsJsonAsync(
            "/workable/profiling/capture-rules",
            new { definitionName = "http.route.case" });
        createdResponse.EnsureSuccessStatusCode();
        var created = await createdResponse.Content.ReadFromJsonAsync<WorkableHttpProfilingCaptureRule>()
            ?? throw new InvalidOperationException("Expected a profile capture rule.");
        var delete = await allowedClient.DeleteAsync($"/workable/profiling/capture-rules/{created.Id}");

        Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MappedHttpWorkerProfilesRequireDiagnosticsAccess(bool canViewDiagnostics)
    {
        const string definitionName = "http.profile-authorization";
        var groups = canViewDiagnostics
            ? TransportAuthorizationTestSupport.WorkAdministratorGroups
                .Concat(TransportAuthorizationTestSupport.DiagnosticsGroups)
            : TransportAuthorizationTestSupport.WorkAdministratorGroups;
        using var host = await CreateHttpHost(
            builder => builder.AddAuthorizedTransportWork(
                WorkDefinition.Create(
                    definitionName,
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                (context, _, _) =>
                {
                    context.Profile.AddInfo("Sensitive profile node", new { Secret = "profile-secret" });
                    return Task.FromResult(WorkExecutionResult.Success());
                }),
            groups: groups);
        var client = host.GetTestClient();

        var queueResponse = await client.PostAsJsonAsync(
            $"/workable/work/{definitionName}",
            new { completion = "waitForCompletion" });
        queueResponse.EnsureSuccessStatusCode();
        var queueJson = JsonNode.Parse(await queueResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected queue response JSON.");
        var workerId = Guid.Parse(
            queueJson["workerId"]?["value"]?.GetValue<string>()
                ?? throw new InvalidOperationException("Expected worker id."));
        var completionProfile = queueJson["completion"]?["worker"]?["profile"];

        var workerResponse = await client.GetAsync($"/workable/workers/{workerId:D}");
        workerResponse.EnsureSuccessStatusCode();
        var workerText = await workerResponse.Content.ReadAsStringAsync();
        var workerJson = JsonNode.Parse(workerText)
            ?? throw new InvalidOperationException("Expected worker response JSON.");

        var iterationResponse = await client.GetAsync($"/workable/workers/{workerId:D}/iterations/1");
        iterationResponse.EnsureSuccessStatusCode();
        var iterationText = await iterationResponse.Content.ReadAsStringAsync();
        var iterationJson = JsonNode.Parse(iterationText)
            ?? throw new InvalidOperationException("Expected iteration response JSON.");

        Assert.Equal(canViewDiagnostics, completionProfile is JsonObject);
        Assert.Equal(canViewDiagnostics, workerJson["profile"] is JsonObject);
        Assert.Equal(canViewDiagnostics, workerJson["iterations"]?[0]?["profile"] is JsonObject);
        Assert.Equal(canViewDiagnostics, iterationJson["profile"] is JsonObject);
        Assert.Equal(canViewDiagnostics, workerText.Contains("profile-secret", StringComparison.Ordinal));
        Assert.Equal(canViewDiagnostics, iterationText.Contains("profile-secret", StringComparison.Ordinal));

        if (canViewDiagnostics)
        {
            Assert.Equal(
                "application",
                completionProfile?["root"]?["instrumentation"]?.GetValue<string>());
            var authoritative = await Direct(host.Services.GetRequiredService<IWorkSystemRegistry>().Default)
                .Query.Worker(new WorkerId(workerId));
            Assert.NotNull(authoritative?.Profile);
            Assert.NotNull(Assert.Single(authoritative!.Iterations).Profile);
        }

        var actionResponse = await client.PostAsJsonAsync(
            $"/workable/workers/{workerId:D}/actions/cancel",
            new
            {
                revision = workerJson["revision"]?.GetValue<long>()
                    ?? throw new InvalidOperationException("Expected worker revision."),
            });
        var actionText = await actionResponse.Content.ReadAsStringAsync();
        var actionJson = JsonNode.Parse(actionText)
            ?? throw new InvalidOperationException("Expected worker action response JSON.");
        Assert.NotNull(actionJson["worker"]);
        Assert.Equal(canViewDiagnostics, actionJson["worker"]?["profile"] is JsonObject);
        Assert.Equal(canViewDiagnostics, actionText.Contains("profile-secret", StringComparison.Ordinal));
    }

    [Fact]
    public async Task HttpApiRejectsWorkWhenChannelIsNotAllowed()
    {
        var definition = WorkDefinition.Create(
            "dotnet.only",
            configuration: WorkConfiguration.Default with
            {
                Invocation = WorkInvocationConfiguration.Allow(WorkInvocationChannel.InProcess),
            });
        var (system, http) = CreateHost(definition, SuccessfulWork);
        await system.Start();

        var result = await http.Queue.Enqueue(system.Name, "dotnet.only", DirectRequestContext());

        Assert.Equal(WorkableHttpWorkStatus.Rejected, result.Status);
        Assert.Contains(result.Messages, message => message.Code == "workable.invocation.channel_not_allowed");
    }

    [Fact]
    public async Task HttpDefinitionsListIncludesWorkThatCannotBeQueuedThroughHttp()
    {
        var definition = WorkDefinition.Create(
            "dotnet.visible",
            configuration: WorkConfiguration.Default with
            {
                Invocation = WorkInvocationConfiguration.Allow(WorkInvocationChannel.InProcess),
            });
        var (system, http) = CreateHost(definition, SuccessfulWork);
        await system.Start();

        var definitions = http.Catalog.GetDefinitions(Direct(system));
        var listed = Assert.Single(definitions);

        Assert.Equal("dotnet.visible", listed.Name);
        Assert.Contains(WorkInvocationChannel.InProcess, listed.Configuration.Invocation.AllowedChannels);
        Assert.DoesNotContain(WorkInvocationChannel.HttpApi, listed.Configuration.Invocation.AllowedChannels);
    }

    [Fact]
    public void HttpQueueConfigurationDtoDoesNotExposeInvocation()
    {
        Assert.Null(typeof(WorkableHttpWorkConfiguration).GetProperty("Invocation"));
        Assert.Equal(
            [
                nameof(WorkableHttpWorkConfiguration.Start),
                nameof(WorkableHttpWorkConfiguration.Coordination),
                nameof(WorkableHttpWorkConfiguration.Recurrence),
                nameof(WorkableHttpWorkConfiguration.TransientRetry),
                nameof(WorkableHttpWorkConfiguration.FailedWorker),
                nameof(WorkableHttpWorkConfiguration.Logging),
                nameof(WorkableHttpWorkConfiguration.Retention),
            ],
            typeof(WorkableHttpWorkConfiguration)
                .GetProperties()
                .Where(property => property.Name != nameof(WorkableHttpWorkConfiguration.Default))
                .Select(property => property.Name)
                .ToArray());
    }

    [Fact]
    public async Task HttpApiQueueConfigurationDtoCannotOverrideDefinitionInvocation()
    {
        var definition = WorkDefinition.Create("http.invocation.dto");
        var (system, http) = CreateHost(definition, SuccessfulWork);
        await system.Start();

        var result = await http.Queue.Enqueue(system.Name,
            "http.invocation.dto",
            DirectRequestContext(),
            new WorkableHttpWorkRequest(
                Options: new WorkableHttpWorkerOptions(
                    Configuration: WorkableHttpWorkConfiguration.From(WorkConfiguration.Default with
                    {
                        Start = WorkStartConfiguration.DoNotStart,
                    }))));
        var worker = await Direct(system).Query.Worker(result.WorkerId ?? throw new InvalidOperationException("Expected worker id."));

        Assert.Equal(WorkableHttpWorkStatus.Accepted, result.Status);
        Assert.NotNull(worker);
        Assert.Equal(WorkStartPolicy.DoNotStart, worker.Configuration.Start.Policy);
        Assert.True(worker.Configuration.Invocation.Allows(WorkInvocationChannel.HttpApi));
        Assert.False(worker.Configuration.Invocation.Allows(WorkInvocationChannel.Mcp));
    }

    [Fact]
    public async Task MappedHttpQueueAcceptsLegacyConfigurationPayloadWithoutFailedWorker()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;

        var configuration = JsonSerializer.SerializeToNode(WorkableHttpWorkConfiguration.Default)
            ?.AsObject()
            ?? throw new InvalidOperationException("Expected HTTP work configuration JSON object.");
        configuration.Remove("failedWorker");

        var response = await client.PostAsJsonAsync(
            "/workable/work/http.route.case",
            new
            {
                completion = "returnAfterAccepted",
                options = new
                {
                    configuration,
                },
            });

        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var workerId = Guid.Parse(json["workerId"]?["value"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected worker id."));
        var worker = await Direct(system).Query.Worker(new WorkerId(workerId));

        Assert.NotNull(worker);
        Assert.Equal(WorkFailedWorkerConfiguration.Default, worker.Configuration.FailedWorker);
    }

    [Fact]
    public async Task HttpApiCanReturnAfterAccepted()
    {
        var definition = WorkDefinition.Create(
            "manual.http",
            configuration: WorkConfiguration.Default with { Start = WorkStartConfiguration.DoNotStart });
        var (system, http) = CreateHost(definition, SuccessfulWork);
        await system.Start();

        var result = await http.Queue.Enqueue(system.Name,
            "manual.http",
            DirectRequestContext(),
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

        var handle = await Direct(system).Queue.Enqueue("http.query.one", WorkInput.Empty.WithIdentifier(new WorkIdentifier("batch", "1")));
        var completion = await handle.WaitForCompletion();
        Assert.True(completion.IsCompletedSuccessfully);
        await WaitForReadModel(system);

        var workerId = handle.WorkerId ?? throw new InvalidOperationException("Expected worker id.");
        var worker = await http.Query.Worker(Direct(system), workerId);
        var workers = await http.Query.Workers(Direct(system), new WorkerCriteria(Identifier: new WorkIdentifier("batch", "1")));
        var byName = await http.Query.WorkInfo(Direct(system), "http.query.one");
        var definitions = await http.Query.WorkDefinitions(Direct(system), new WorkDefinitionCriteria(Category: "Http"));
        var summary = await http.Query.WorkerStatusSummary(Direct(system), new WorkerCriteria(DefinitionName: "http.query.one"));
        var systemSummary = await http.Query.WorkerStatusSummary(Direct(system));

        Assert.NotNull(worker);
        Assert.Single(workers.Workers);
        Assert.NotNull(byName);
        var requiredByName = byName;
        Assert.Equal("http.query.one", requiredByName.Definition.Name);
        Assert.Equal(2, definitions.Count);
        Assert.Equal(1, summary.Total);
        Assert.Equal(1, summary.Counts[WorkerState.Completed]);
        Assert.Equal(1, systemSummary.Total);
        Assert.Equal(1, systemSummary.Counts[WorkerState.Completed]);
    }

    [Fact]
    public async Task MappedHttpRouteDoesNotExposeLegacyOverviewRoutes()
    {
        using var host = await CreateOverviewHttpHost();
        var client = host.GetTestClient();

        var overview = await client.GetAsync("/workable/overview");
        var overviewSlice = await client.GetAsync("/workable/overview/counts");
        var namedOverview = await client.GetAsync("/workable/systems/default/overview");

        Assert.Equal(HttpStatusCode.NotFound, overview.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, overviewSlice.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, namedOverview.StatusCode);
    }

    [Fact]
    public async Task MappedHttpRouteCanQueryViewAndComponents()
    {
        using var host = await CreateOverviewHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        await (await Direct(system).Queue.Enqueue(
            "http.overview.complete",
            WorkInput.Empty.WithIdentifier(new WorkIdentifier("case", "complete")))).WaitForCompletion();
        await (await Direct(system).Queue.Enqueue(
            "http.overview.failed",
            WorkInput.Empty.WithIdentifier(new WorkIdentifier("case", "failed")))).WaitForCompletion();
        await TestEventually.ThroughputBucketClosed();
        await WaitForReadModel(system);

        var viewResponse = await client.PostAsJsonAsync(
            "/workable/views/overview",
            new
            {
                scope = new
                {
                    category = "Http",
                    definitionName = "http.overview.complete",
                },
                components = new object[]
                {
                    new { id = "system", type = "system", shape = "detailed" },
                    new { id = "workers", type = "workers", shape = "standard" },
                    new { id = "workersCompact", type = "workers", shape = "compact" },
                    new { id = "iterations", type = "iterations", shape = "compact" },
                    new
                    {
                        id = "throughput",
                        type = "throughput",
                        shape = "standard",
                        options = new
                        {
                            bucketSeconds = 1,
                            windowSeconds = 60,
                        },
                    },
                },
            });
        viewResponse.EnsureSuccessStatusCode();
        var view = JsonNode.Parse(await viewResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected view JSON response.");
        var viewComponents = view["components"]?.AsObject()
            ?? throw new InvalidOperationException("Expected view components.");
        var throughputComponent = viewComponents["throughput"]
            ?? throw new InvalidOperationException("Expected throughput component.");
        var throughputBuckets = throughputComponent["data"]?["throughput"]?["buckets"]?.AsArray()
            ?? throw new InvalidOperationException("Expected throughput buckets.");
        var throughputExecutionSummary = throughputComponent["data"]?["throughput"]?["executionSummary"]
            ?? throw new InvalidOperationException("Expected throughput execution summary.");
        var throughputLiveSummary = throughputComponent["data"]?["throughput"]?["liveSummary"]
            ?? throw new InvalidOperationException("Expected throughput live summary.");
        var catalogResponse = await client.PostAsJsonAsync(
            "/workable/components/catalog",
            new
            {
                scope = new
                {
                    category = "Http",
                },
            });
        catalogResponse.EnsureSuccessStatusCode();
        var catalog = JsonNode.Parse(await catalogResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected catalog JSON response.");
        var catalogComponent = catalog["components"]?["catalog"]
            ?? throw new InvalidOperationException("Expected catalog component.");
        var catalogDefinitions = catalogComponent["data"]?["catalogDefinitions"]?.AsArray()
            ?? throw new InvalidOperationException("Expected catalog definitions.");
        var defaultViewResponse = await client.PostAsJsonAsync(
            "/workable/views/overview",
            new
            {
                scope = new
                {
                    category = "Http",
                },
            });
        defaultViewResponse.EnsureSuccessStatusCode();
        var defaultView = JsonNode.Parse(await defaultViewResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected default view JSON response.");
        var defaultComponents = defaultView["components"]?.AsObject()
            ?? throw new InvalidOperationException("Expected default view components.");
        var failedWorkersResponse = await client.PostAsJsonAsync(
            "/workable/components/failedWorkers",
            new
            {
                scope = new
                {
                    category = "Http",
                },
                shape = "standard",
            });
        failedWorkersResponse.EnsureSuccessStatusCode();
        var failedWorkersQuery = JsonNode.Parse(await failedWorkersResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected failed workers JSON response.");
        var failedWorkersComponent = failedWorkersQuery["components"]?["failedWorkers"]
            ?? throw new InvalidOperationException("Expected failed workers component.");
        var failedWorker = Assert.Single(failedWorkersComponent["data"]?.AsArray()
            ?? throw new InvalidOperationException("Expected failed workers component data."));
        var detailedFailedWorkersResponse = await client.PostAsJsonAsync(
            "/workable/components/failedWorkers",
            new
            {
                scope = new
                {
                    category = "Http",
                },
                shape = "detailed",
            });
        detailedFailedWorkersResponse.EnsureSuccessStatusCode();
        var detailedFailedWorkersQuery = JsonNode.Parse(await detailedFailedWorkersResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected detailed failed workers JSON response.");
        var detailedFailedWorkersComponent = detailedFailedWorkersQuery["components"]?["failedWorkers"]
            ?? throw new InvalidOperationException("Expected detailed failed workers component.");
        var detailedFailedWorker = Assert.Single(detailedFailedWorkersComponent["data"]?.AsArray()
            ?? throw new InvalidOperationException("Expected detailed failed workers component data."));
        var detailedFailedIterationsResponse = await client.PostAsJsonAsync(
            "/workable/components/failedIterations",
            new
            {
                scope = new
                {
                    category = "Http",
                },
                shape = "detailed",
            });
        detailedFailedIterationsResponse.EnsureSuccessStatusCode();
        var detailedFailedIterationsQuery = JsonNode.Parse(await detailedFailedIterationsResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected detailed failed iterations JSON response.");
        var detailedFailedIterationsComponent = detailedFailedIterationsQuery["components"]?["failedIterations"]
            ?? throw new InvalidOperationException("Expected detailed failed iterations component.");
        var detailedFailedIteration = Assert.Single(detailedFailedIterationsComponent["data"]?.AsArray()
            ?? throw new InvalidOperationException("Expected detailed failed iterations component data."));
        var compactThroughputResponse = await client.PostAsJsonAsync(
            "/workable/components/throughput",
            new
            {
                scope = new
                {
                    category = "Http",
                },
                shape = "compact",
                options = new
                {
                    bucketSeconds = 1,
                    windowSeconds = 60,
                },
            });
        compactThroughputResponse.EnsureSuccessStatusCode();
        var compactThroughputQuery = JsonNode.Parse(await compactThroughputResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected compact throughput JSON response.");
        var compactThroughputComponent = compactThroughputQuery["components"]?["throughput"]
            ?? throw new InvalidOperationException("Expected compact throughput component.");

        Assert.Equal(["iterations", "system", "throughput", "workers", "workersCompact"], viewComponents.Select(component => component.Key).Order().ToArray());
        Assert.Equal("ok", viewComponents["system"]?["status"]?.GetValue<string>());
        Assert.Equal("detailed", viewComponents["system"]?["shape"]?.GetValue<string>());
        Assert.Equal("Started", viewComponents["system"]?["data"]?["systemState"]?.GetValue<string>());
        Assert.Equal("ok", viewComponents["workers"]?["status"]?.GetValue<string>());
        Assert.Equal("standard", viewComponents["workers"]?["shape"]?.GetValue<string>());
        Assert.Equal(1, viewComponents["workers"]?["data"]?["finalWorkerCount"]?.GetValue<int>());
        Assert.Equal(0, viewComponents["workers"]?["data"]?["failedWorkerCount"]?.GetValue<int>());
        Assert.Equal(1, viewComponents["workers"]?["data"]?["workerCountByState"]?["Completed"]?.GetValue<int>());
        Assert.Null(viewComponents["workers"]?["data"]?["workerCountByState"]?["Failed"]);
        Assert.Equal("ok", viewComponents["workersCompact"]?["status"]?.GetValue<string>());
        Assert.Equal("compact", viewComponents["workersCompact"]?["shape"]?.GetValue<string>());
        Assert.Equal(0, viewComponents["workersCompact"]?["data"]?["activeWorkerCount"]?.GetValue<int>());
        Assert.Equal(0, viewComponents["workersCompact"]?["data"]?["failedWorkerCount"]?.GetValue<int>());
        Assert.Null(viewComponents["workersCompact"]?["data"]?["definitionCount"]);
        Assert.Null(viewComponents["workersCompact"]?["data"]?["finalWorkerCount"]);
        Assert.Null(viewComponents["workersCompact"]?["data"]?["workerCountByState"]);
        Assert.Equal("ok", viewComponents["iterations"]?["status"]?.GetValue<string>());
        Assert.Equal("compact", viewComponents["iterations"]?["shape"]?.GetValue<string>());
        Assert.Equal(1, viewComponents["iterations"]?["data"]?["iterationCountByStatus"]?["Completed"]?.GetValue<int>());
        Assert.Null(viewComponents["iterations"]?["data"]?["iterationCountByStatus"]?["Failed"]);
        Assert.Null(viewComponents["iterations"]?["data"]?["completedIterationCount"]);
        Assert.Null(viewComponents["iterations"]?["data"]?["failedIterationCount"]);
        Assert.Null(viewComponents["iterations"]?["data"]?["commonKeyTypes"]);
        Assert.Equal("ok", throughputComponent["status"]?.GetValue<string>());
        Assert.Equal("standard", throughputComponent["shape"]?.GetValue<string>());
        Assert.Equal(1, throughputBuckets.Sum(bucket => bucket?["started"]?.GetValue<int>() ?? 0));
        Assert.Equal(1, throughputBuckets.Sum(bucket => bucket?["completed"]?.GetValue<int>() ?? 0));
        Assert.Equal(0, throughputBuckets.Sum(bucket => bucket?["failed"]?.GetValue<int>() ?? 0));
        Assert.Null(throughputBuckets.FirstOrDefault()?["executionCount"]);
        Assert.Null(throughputBuckets.FirstOrDefault()?["slowestExecutionMilliseconds"]);
        Assert.Null(throughputBuckets.FirstOrDefault()?["p95ExecutionMilliseconds"]);
        Assert.Null(throughputBuckets.FirstOrDefault()?["p99ExecutionMilliseconds"]);
        Assert.Equal(1, throughputComponent["data"]?["throughput"]?["settledCount"]?.GetValue<int>());
        Assert.Equal(1, throughputExecutionSummary["executionCount"]?.GetValue<int>());
        Assert.True(throughputExecutionSummary["averageExecutionMilliseconds"]?.GetValue<double>() >= 0);
        Assert.True(throughputExecutionSummary["slowestExecutionMilliseconds"]?.GetValue<double>() >= 0);
        Assert.True(throughputExecutionSummary["p95ExecutionMilliseconds"]?.GetValue<double>() >= 0);
        Assert.True(throughputExecutionSummary["p99ExecutionMilliseconds"]?.GetValue<double>() >= 0);
        Assert.Equal(60, throughputLiveSummary["rateWindowSeconds"]?.GetValue<int>());
        Assert.Null(throughputLiveSummary["windowSeconds"]);
        Assert.NotNull(throughputLiveSummary["startedPerSecond"]);
        Assert.NotNull(throughputLiveSummary["completedPerSecond"]);
        Assert.NotNull(throughputLiveSummary["failedPerSecond"]);
        Assert.NotNull(throughputLiveSummary["inFlightDeltaPerSecond"]);
        Assert.Null(throughputLiveSummary["executionCount"]);
        Assert.Null(throughputLiveSummary["averageExecutionMilliseconds"]);
        Assert.Null(throughputLiveSummary["slowestExecutionMilliseconds"]);
        Assert.Null(throughputLiveSummary["p95ExecutionMilliseconds"]);
        Assert.Null(throughputLiveSummary["p99ExecutionMilliseconds"]);
        Assert.Equal("ok", compactThroughputComponent["status"]?.GetValue<string>());
        Assert.Equal("compact", compactThroughputComponent["shape"]?.GetValue<string>());
        Assert.Null(compactThroughputComponent["data"]?["throughput"]?["buckets"]);
        Assert.Null(compactThroughputComponent["data"]?["throughput"]?["bucketSeconds"]);
        Assert.True(compactThroughputComponent["data"]?["throughput"]?["settledCount"]?.GetValue<int>() >= 1);
        Assert.NotNull(compactThroughputComponent["data"]?["throughput"]?["executionSummary"]);
        Assert.NotNull(compactThroughputComponent["data"]?["throughput"]?["liveSummary"]);
        Assert.Equal("ok", catalogComponent["status"]?.GetValue<string>());
        Assert.Equal("detailed", catalogComponent["shape"]?.GetValue<string>());
        Assert.Contains(catalogDefinitions, definition => definition?["name"]?.GetValue<string>() == "http.overview.complete");
        Assert.Equal("ok", failedWorkersComponent["status"]?.GetValue<string>());
        Assert.Equal("standard", failedWorkersComponent["shape"]?.GetValue<string>());
        Assert.Equal("http.overview.failed", failedWorker?["definitionName"]?.GetValue<string>());
        Assert.Null(failedWorker?["state"]);
        Assert.NotNull(failedWorker?["id"]);
        Assert.NotNull(failedWorker?["revision"]);
        Assert.NotNull(failedWorker?["updatedAt"]);
        Assert.NotNull(failedWorker?["totalExecutionDuration"]);
        Assert.Null(failedWorker?["definitionId"]);
        Assert.Null(failedWorker?["subjectId"]);
        Assert.Null(failedWorker?["identifiers"]);
        Assert.Equal("ok", detailedFailedWorkersComponent["status"]?.GetValue<string>());
        Assert.Equal("detailed", detailedFailedWorkersComponent["shape"]?.GetValue<string>());
        Assert.Equal("http.overview.failed", detailedFailedWorker?["definitionName"]?.GetValue<string>());
        Assert.Equal("Failed", detailedFailedWorker?["state"]?.GetValue<string>());
        Assert.Null(detailedFailedWorker?["definitionId"]);
        Assert.Null(detailedFailedWorker?["createdAt"]);
        Assert.NotNull(detailedFailedWorker?["identifiers"]);
        Assert.Equal("standard", defaultComponents["failedIterations"]?["shape"]?.GetValue<string>());
        Assert.Equal("standard", defaultComponents["completedIterations"]?["shape"]?.GetValue<string>());
        var defaultFailedIteration = Assert.Single(defaultComponents["failedIterations"]?["data"]?.AsArray()
            ?? throw new InvalidOperationException("Expected default failed iterations data."));
        Assert.Equal("http.overview.failed", defaultFailedIteration?["definitionName"]?.GetValue<string>());
        Assert.NotNull(defaultFailedIteration?["workerId"]);
        Assert.NotNull(defaultFailedIteration?["sequence"]);
        Assert.NotNull(defaultFailedIteration?["completedAt"]);
        Assert.NotNull(defaultFailedIteration?["executionDuration"]);
        Assert.Null(defaultFailedIteration?["workerState"]);
        Assert.Null(defaultFailedIteration?["subjectId"]);
        Assert.Null(defaultFailedIteration?["identifiers"]);
        Assert.Equal("ok", detailedFailedIterationsComponent["status"]?.GetValue<string>());
        Assert.Equal("detailed", detailedFailedIterationsComponent["shape"]?.GetValue<string>());
        Assert.Equal("http.overview.failed", detailedFailedIteration?["definitionName"]?.GetValue<string>());
        Assert.NotNull(detailedFailedIteration?["workerState"]);
        Assert.NotNull(detailedFailedIteration?["identifiers"]);
        Assert.Equal("standard", defaultComponents["workers"]?["shape"]?.GetValue<string>());
        Assert.Equal("standard", defaultComponents["failedWorkers"]?["shape"]?.GetValue<string>());
        Assert.DoesNotContain("catalog", defaultComponents.Select(component => component.Key));
        Assert.DoesNotContain("throughput", defaultComponents.Select(component => component.Key));
    }

    [Fact]
    public async Task MappedHttpRouteCanQueryWorkerGridView()
    {
        using var host = await CreateOverviewHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        await (await Direct(system).Queue.Enqueue("http.overview.complete")).WaitForCompletion();
        await (await Direct(system).Queue.Enqueue("http.overview.failed")).WaitForCompletion();
        await (await Direct(system).Queue.Enqueue(
            "http.overview.complete",
            options: new WorkerOptions(ProfilingEnabled: true))).WaitForCompletion();
        await WaitForReadModel(system);

        var response = await client.PostAsJsonAsync(
            "/workable/views/workers",
            new
            {
                components = new object[]
                {
                    new
                    {
                        id = "workerGrid",
                        type = "workerGrid",
                        shape = "detailed",
                        options = new
                        {
                            states = CompletedFailedStates,
                            skip = 0,
                            take = 50,
                        },
                    },
                },
            });
        var configurationResponse = await client.PostAsJsonAsync(
            "/workable/views/workers",
            new
            {
                components = new object[]
                {
                    new
                    {
                        id = "workerGrid",
                        type = "workerGrid",
                        shape = "detailed",
                        options = new
                        {
                            configuration = new
                            {
                                profilingEnabled = true,
                            },
                            skip = 0,
                            take = 50,
                        },
                    },
                },
            });
        response.EnsureSuccessStatusCode();
        configurationResponse.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var configurationJson = JsonNode.Parse(await configurationResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected configuration JSON response.");
        var grid = json["components"]?["workerGrid"]?["data"]
            ?? throw new InvalidOperationException("Expected worker grid component.");
        var configurationGrid = configurationJson["components"]?["workerGrid"]?["data"]
            ?? throw new InvalidOperationException("Expected configured worker grid component.");

        Assert.Equal(3, grid["totalCount"]?.GetValue<int>());
        var workers = grid["workers"]?.AsArray()
            ?? throw new InvalidOperationException("Expected workers array.");
        var configuredWorkers = configurationGrid["workers"]?.AsArray()
            ?? throw new InvalidOperationException("Expected configured workers array.");
        Assert.Contains(workers, worker => worker?["state"]?.GetValue<string>() == "Completed");
        Assert.Contains(workers, worker => worker?["state"]?.GetValue<string>() == "Failed");
        Assert.Contains(workers, worker =>
            worker is not null &&
            worker["state"]?.GetValue<string>() == "Completed" &&
            worker["isFinal"]?.GetValue<bool>() == true);
        Assert.Contains(workers, worker =>
            worker is not null &&
            worker["state"]?.GetValue<string>() == "Failed" &&
            worker["isFinal"]?.GetValue<bool>() == false);
        Assert.NotNull(workers.FirstOrDefault()?["identifiers"]);
        Assert.Equal(1, configurationGrid["totalCount"]?.GetValue<int>());
        Assert.Equal("http.overview.complete", Assert.Single(configuredWorkers)?["definitionName"]?.GetValue<string>());
    }

    [Fact]
    public async Task MappedHttpRouteCanGetAndQueryIterationGridView()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;

        var handle = await Direct(system).Queue.Enqueue(
            "http.route.case",
            WorkInput.Empty.WithIdentifier(new WorkIdentifier("batch", "iteration")),
            options: new WorkerOptions(Configuration: WorkConfiguration.Default));
        await handle.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForReadModel(system);
        var workerId = handle.WorkerId?.Value
            ?? throw new InvalidOperationException("Expected worker id.");

        var getResponse = await client.GetAsync($"/workable/workers/{workerId:D}/iterations/1");
        var queryResponse = await client.PostAsJsonAsync(
            "/workable/views/iterations",
            new
            {
                scope = new
                {
                    definitionName = "http.route.case",
                },
                components = new object[]
                {
                    new
                    {
                        id = "iterationGrid",
                        type = "iterationGrid",
                        shape = "detailed",
                        options = new
                        {
                            keyType = "batch",
                            statuses = CompletedStatuses,
                            skip = 0,
                            take = 50,
                        },
                    },
                },
            });

        getResponse.EnsureSuccessStatusCode();
        queryResponse.EnsureSuccessStatusCode();
        var getJson = JsonNode.Parse(await getResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected iteration JSON response.");
        var queryJson = JsonNode.Parse(await queryResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected iteration query JSON response.");
        var grid = queryJson["components"]?["iterationGrid"]?["data"]
            ?? throw new InvalidOperationException("Expected iteration grid component.");
        var iterations = grid["iterations"]?.AsArray()
            ?? throw new InvalidOperationException("Expected iterations array.");

        Assert.Equal(1, getJson["sequence"]?.GetValue<int>());
        Assert.Equal("Completed", getJson["status"]?.GetValue<string>());
        Assert.Equal(1, grid["totalCount"]?.GetValue<int>());
        var iteration = Assert.Single(iterations);
        Assert.Equal(workerId.ToString("D"), iteration?["workerId"]?["value"]?.GetValue<string>());
        Assert.Equal("Completed", iteration?["status"]?.GetValue<string>());
        Assert.True(iteration?["isFinal"]?.GetValue<bool>());
        Assert.NotNull(iteration?["workerState"]);
        Assert.NotNull(iteration?["identifiers"]);
    }

    [Fact]
    public async Task MappedHttpRouteCanFilterWorkerOverviewActivity()
    {
        using var host = await CreateHttpHost(builder =>
        {
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("http.route.overview.activity"),
                (context, input, cancellationToken) =>
                {
                    var logger = context.Services.GetRequiredService<ILoggerFactory>().CreateLogger("http.route.overview.activity");
                    logger.LogInformation("http info");
                    logger.LogWarning("http warning");
                    logger.LogError("http error");
                    return Task.FromResult(WorkExecutionResult.Success());
                },
                configuration => configuration.ConfigureLogging(level: LogLevel.Information));
        });
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var session = TransportAuthorizationTestSupport.CreateTransportSession(
            system,
            WorkInvocationChannel.HttpApi,
            new WorkActor("http-overview-user", "HTTP Overview User"));

        var handle = await session.Queue.Enqueue(
            "http.route.overview.activity",
            options: new WorkerOptions(ProfilingEnabled: true));
        await handle.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForReadModel(system);
        var workerId = handle.WorkerId?.Value
            ?? throw new InvalidOperationException("Expected worker id.");

        var logsResponse = await client.GetAsync(
            $"/workable/workers/{workerId:D}/overview?activity=Logs&activityTake=10&logLevels=Warning&logSort=Asc");
        var timelineResponse = await client.GetAsync(
            $"/workable/workers/{workerId:D}/overview?activity=Timeline&activityTake=10&timelineCategories=SystemEvent");

        logsResponse.EnsureSuccessStatusCode();
        timelineResponse.EnsureSuccessStatusCode();

        var logsJson = JsonNode.Parse(await logsResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected log overview JSON response.");
        var timelineJson = JsonNode.Parse(await timelineResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected timeline overview JSON response.");
        var timelineItems = timelineJson["timeline"]?["page"]?["items"]?.AsArray()
            ?? throw new InvalidOperationException("Expected filtered timeline page.");

        Assert.Equal("Logs", logsJson["activity"]?.GetValue<string>());
        Assert.NotNull(logsJson["logs"]?["page"]);

        Assert.NotEmpty(timelineItems);
        Assert.All(timelineItems, item => Assert.Equal("SystemEvent", item?["category"]?.GetValue<string>()));
    }

    [Fact]
    public async Task MappedHttpRouteCanScopeWorkerOverviewLogsToASpecificIteration()
    {
        using var host = await CreateHttpHost(builder =>
        {
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("http.route.overview.logs.sequence"),
                (context, input, cancellationToken) =>
                {
                    var logger = context.Services.GetRequiredService<ILoggerFactory>().CreateLogger("http.route.overview.logs.sequence");
                    logger.LogInformation("http overview info");
                    logger.LogWarning("http overview warning");
                    logger.LogError("http overview error");
                    return Task.FromResult(WorkExecutionResult.Success());
                },
                configuration => configuration.ConfigureLogging(level: LogLevel.Information));
        });
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var session = TransportAuthorizationTestSupport.CreateTransportSession(system, WorkInvocationChannel.HttpApi);

        var handle = await session.Queue.Enqueue(
            "http.route.overview.logs.sequence",
            options: new WorkerOptions(ProfilingEnabled: true));
        await handle.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForReadModel(system);
        var workerId = handle.WorkerId?.Value
            ?? throw new InvalidOperationException("Expected worker id.");

        var response = await client.GetAsync(
            $"/workable/workers/{workerId:D}/overview?activity=Logs&activityTake=10&logIterationSequence=1");

        response.EnsureSuccessStatusCode();

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected log overview JSON response.");
        Assert.Equal("Logs", json["activity"]?.GetValue<string>());
        Assert.NotNull(json["logs"]?["page"]);
    }

    [Fact]
    public async Task MappedHttpRouteCanReadWorkerOverviewLogsWithoutFullWorkerOverviewPayload()
    {
        using var host = await CreateHttpHost(builder =>
        {
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("http.route.overview.logs.tight"),
                (context, input, cancellationToken) =>
                {
                    var logger = context.Services.GetRequiredService<ILoggerFactory>().CreateLogger("http.route.overview.logs.tight");
                    logger.LogInformation("http overview info");
                    logger.LogWarning("http overview warning");
                    logger.LogError("http overview error");
                    return Task.FromResult(WorkExecutionResult.Success());
                },
                configuration => configuration.ConfigureLogging(level: LogLevel.Information));
        });
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var session = TransportAuthorizationTestSupport.CreateTransportSession(system, WorkInvocationChannel.HttpApi);

        var handle = await session.Queue.Enqueue(
            "http.route.overview.logs.tight",
            options: new WorkerOptions(ProfilingEnabled: true));
        await handle.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForReadModel(system);
        var workerId = handle.WorkerId?.Value
            ?? throw new InvalidOperationException("Expected worker id.");

        var json = await GetJson(
            client,
            $"/workable/workers/{workerId:D}/overview/logs?activityTake=10&logLevels=Warning&logSort=Asc");

        Assert.NotNull(json["summary"]);
        Assert.NotNull(json["page"]?["items"]);
        Assert.Null(json["worker"]);
        Assert.Null(json["timeline"]);
        var items = json["page"]?["items"]?.AsArray()
            ?? throw new InvalidOperationException("Expected log page items.");
        Assert.NotNull(items);
    }

    [Fact]
    public async Task MappedHttpRouteCanReadWorkerOverviewTimelineWithoutFullWorkerOverviewPayload()
    {
        using var host = await CreateHttpHost(builder =>
        {
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("http.route.overview.timeline.tight"),
                (context, input, cancellationToken) =>
                {
                    return Task.FromResult(WorkExecutionResult.Success());
                });
        });
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var session = TransportAuthorizationTestSupport.CreateTransportSession(
            system,
            WorkInvocationChannel.HttpApi,
            new WorkActor("http-overview-user", "HTTP Overview User"));

        var handle = await session.Queue.Enqueue("http.route.overview.timeline.tight");
        await handle.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForReadModel(system);
        var workerId = handle.WorkerId?.Value
            ?? throw new InvalidOperationException("Expected worker id.");

        var json = await GetJson(
            client,
            $"/workable/workers/{workerId:D}/overview/timeline?activityTake=10&timelineCategories=SystemEvent");

        Assert.NotNull(json["summary"]);
        Assert.NotNull(json["page"]?["items"]);
        Assert.Null(json["worker"]);
        Assert.Null(json["logs"]);
        var items = json["page"]?["items"]?.AsArray()
            ?? throw new InvalidOperationException("Expected timeline page items.");
        Assert.NotEmpty(items);
        Assert.All(items, item => Assert.Equal("SystemEvent", item?["category"]?.GetValue<string>()));
    }

    [Fact]
    public async Task MappedHttpRouteCanReadPagedIterationMessages()
    {
        using var host = await CreateHttpHost(builder =>
        {
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("http.route.iteration.messages"),
                (context, input, cancellationToken) => Task.FromResult(WorkExecutionResult.Success(messages:
                [
                    new WorkMessage(
                        "http.iteration.warning",
                        WorkMessageSeverity.Warning,
                        "HTTP iteration warning.",
                        "messages.warning",
                        new Dictionary<string, object?> { ["slot"] = 2 })
                    {
                        OccurredAt = DateTimeOffset.Parse("2026-05-29T11:00:02Z"),
                    },
                    new WorkMessage(
                        "http.iteration.information",
                        WorkMessageSeverity.Information,
                        "HTTP iteration information.",
                        "messages.information",
                        new Dictionary<string, object?> { ["slot"] = 1 })
                    {
                        OccurredAt = DateTimeOffset.Parse("2026-05-29T11:00:01Z"),
                    },
                    new WorkMessage(
                        "http.iteration.debug",
                        WorkMessageSeverity.Debug,
                        "HTTP iteration debug.",
                        "messages.debug",
                        new Dictionary<string, object?> { ["slot"] = 3 })
                    {
                        OccurredAt = DateTimeOffset.Parse("2026-05-29T11:00:03Z"),
                    },
                ])));
        });
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var session = TransportAuthorizationTestSupport.CreateTransportSession(system, WorkInvocationChannel.HttpApi);

        var handle = await session.Queue.Enqueue("http.route.iteration.messages");
        await handle.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForReadModel(system);
        var workerId = handle.WorkerId?.Value
            ?? throw new InvalidOperationException("Expected worker id.");

        var firstPage = await GetJson(
            client,
            $"/workable/workers/{workerId:D}/iterations/1/overview/messages?take=1&sort=Asc&severities=Information,Warning");
        var firstCursor = firstPage["page"]?["cursor"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected message page cursor.");
        var secondPage = await GetJson(
            client,
            $"/workable/workers/{workerId:D}/iterations/1/overview/messages?take=1&sort=Asc&severities=Information,Warning&cursor={Uri.EscapeDataString(firstCursor)}");

        Assert.Equal(3, firstPage["summary"]?["total"]?.GetValue<int>());
        Assert.Equal(1, firstPage["summary"]?["warning"]?.GetValue<int>());
        Assert.Equal(1, firstPage["summary"]?["information"]?.GetValue<int>());
        Assert.Equal(1, firstPage["summary"]?["debug"]?.GetValue<int>());
        Assert.Equal("http.iteration.information", firstPage["page"]?["items"]?[0]?["code"]?.GetValue<string>());
        Assert.Equal("messages.information", firstPage["page"]?["items"]?[0]?["target"]?.GetValue<string>());
        Assert.NotNull(firstPage["page"]?["items"]?[0]?["occurredAt"]?.GetValue<string>());
        Assert.Equal(1, firstPage["page"]?["items"]?[0]?["metadata"]?["slot"]?.GetValue<int>());
        Assert.True(firstPage["page"]?["hasMore"]?.GetValue<bool>());

        Assert.Equal("http.iteration.warning", secondPage["page"]?["items"]?[0]?["code"]?.GetValue<string>());
        Assert.False(secondPage["page"]?["hasMore"]?.GetValue<bool>());
    }

    [Fact]
    public async Task MappedHttpRouteCanReadIterationOverviewSnapshot()
    {
        using var host = await CreateHttpHost(builder =>
        {
            builder.AddWork<HttpIterationDetailExecutor>(
                WorkDefinition.Create("http.route.iteration.overview"),
                configuration => configuration.ConfigureLogging(level: LogLevel.Information),
                authorize => authorize.RequireGroups(
                    TransportAuthorizationTestSupport.ReadGroups,
                    TransportAuthorizationTestSupport.OperateGroups));
        },
        configureServices: services => services.AddSingleton<IWorkSystemCapabilityContributor, TestSqlProfilingCapabilityContributor>());
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var session = TransportAuthorizationTestSupport.CreateTransportSession(system, WorkInvocationChannel.HttpApi);

        var handle = await session.Queue.Enqueue(
            "http.route.iteration.overview",
            WorkInput.FromValue(new { attempt = 7 })
                .WithSubject(new WorkSubjectId("claim", "CLM-123"))
                .WithConcurrencyKey(new WorkConcurrencyKey("tenant", "west"))
                .WithIdentifier(new WorkIdentifier("invoice", "INV-456")),
            options: new WorkerOptions(ProfilingEnabled: true));
        await handle.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForReadModel(system);
        var workerId = handle.WorkerId?.Value
            ?? throw new InvalidOperationException("Expected worker id.");
        var snapshot = await Direct(system).Query.WorkerIteration(new WorkerIterationReference(new WorkerId(workerId), 1))
            ?? throw new InvalidOperationException("Expected iteration snapshot.");

        Assert.Equal(2, snapshot.Messages.Count);
        Assert.Equal(3, snapshot.Logs.Count);
        var json = await GetJson(client, $"/workable/workers/{workerId:D}/iterations/1/overview");

        Assert.Equal("Logs", json["activity"]?.GetValue<string>());
        Assert.True(json["capabilities"]?["sqlProfilingAvailable"]?.GetValue<bool>());
        Assert.Equal(workerId.ToString("D"), json["worker"]?["workerId"]?["value"]?.GetValue<string>());
        Assert.Equal("http.route.iteration.overview", json["worker"]?["definitionName"]?.GetValue<string>());
        Assert.Equal("claim", json["worker"]?["subjectId"]?["type"]?.GetValue<string>());
        Assert.Equal("CLM-123", json["worker"]?["subjectId"]?["value"]?.GetValue<string>());
        Assert.Equal("tenant", json["worker"]?["concurrencyKey"]?["type"]?.GetValue<string>());
        Assert.Equal("west", json["worker"]?["concurrencyKey"]?["value"]?.GetValue<string>());
        Assert.Contains(json["worker"]?["identifiers"]?.AsArray() ?? [], identifier => identifier?["value"]?.GetValue<string>() == "INV-456");
        Assert.Contains("\"attempt\":7", json["input"]?["json"]?.GetValue<string>() ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(1, json["iteration"]?["sequence"]?.GetValue<int>());
        Assert.Equal(1, json["iteration"]?["attemptCount"]?.GetValue<int>());
        Assert.Equal("Completed", json["iteration"]?["status"]?.GetValue<string>());
        Assert.NotNull(json["iteration"]?["profile"]?["root"]?["label"]?.GetValue<string>());
        Assert.Equal("application", json["iteration"]?["profile"]?["root"]?["instrumentation"]?.GetValue<string>());
        Assert.Equal(2, json["messages"]?["summary"]?["total"]?.GetValue<int>());
        Assert.Equal(1, json["messages"]?["summary"]?["warning"]?.GetValue<int>());
        Assert.Equal(1, json["messages"]?["summary"]?["information"]?.GetValue<int>());
        Assert.Null(json["messages"]?["page"]);
        Assert.Equal(3, json["logs"]?["summary"]?["total"]?.GetValue<int>());
        Assert.Equal(1, json["logs"]?["summary"]?["information"]?.GetValue<int>());
        Assert.Equal(1, json["logs"]?["summary"]?["warning"]?.GetValue<int>());
        Assert.Equal(1, json["logs"]?["summary"]?["error"]?.GetValue<int>());
        Assert.Equal("HTTP iteration error log.", json["logs"]?["page"]?["items"]?[0]?["message"]?.GetValue<string>());
    }

    [Fact]
    public async Task MappedHttpRouteCanReadFilteredIterationOverviewMessagesWithoutInputOutputOrProfile()
    {
        using var host = await CreateHttpHost(builder =>
        {
            builder.AddWork<HttpIterationDetailExecutor>(
                WorkDefinition.Create("http.route.iteration.overview.messages"),
                configuration => configuration.ConfigureLogging(level: LogLevel.Information),
                authorize => authorize.RequireGroups(
                    TransportAuthorizationTestSupport.ReadGroups,
                    TransportAuthorizationTestSupport.OperateGroups));
        });
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var session = TransportAuthorizationTestSupport.CreateTransportSession(system, WorkInvocationChannel.HttpApi);

        var handle = await session.Queue.Enqueue(
            "http.route.iteration.overview.messages",
            WorkInput.FromValue(new { attempt = 3 }),
            options: new WorkerOptions(ProfilingEnabled: true));
        await handle.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForReadModel(system);
        var workerId = handle.WorkerId?.Value
            ?? throw new InvalidOperationException("Expected worker id.");

        var json = await GetJson(
            client,
            $"/workable/workers/{workerId:D}/iterations/1/overview?activity=Messages&activityTake=1&messageSort=Asc&severities=Information,Warning&includeInput=false&includeOutput=false&includeProfile=false");

        Assert.Equal("Messages", json["activity"]?.GetValue<string>());
        Assert.Null(json["input"]);
        Assert.Null(json["iteration"]?["output"]);
        Assert.Null(json["iteration"]?["profile"]);
        Assert.Equal(2, json["messages"]?["summary"]?["total"]?.GetValue<int>());
        Assert.Equal(1, json["messages"]?["summary"]?["warning"]?.GetValue<int>());
        Assert.Equal(1, json["messages"]?["summary"]?["information"]?.GetValue<int>());
        Assert.Equal("http.iteration.information", json["messages"]?["page"]?["items"]?[0]?["code"]?.GetValue<string>());
        Assert.True(json["messages"]?["page"]?["hasMore"]?.GetValue<bool>());
        Assert.Equal(3, json["logs"]?["summary"]?["total"]?.GetValue<int>());
        Assert.Null(json["logs"]?["page"]);
    }

    [Fact]
    public async Task MappedHttpRouteCanReadPagedIterationLogs()
    {
        using var host = await CreateHttpHost(builder =>
        {
            builder.AddWork<HttpIterationDetailExecutor>(
                WorkDefinition.Create("http.route.iteration.logs"),
                configuration => configuration.ConfigureLogging(level: LogLevel.Information),
                authorize => authorize.RequireGroups(
                    TransportAuthorizationTestSupport.ReadGroups,
                    TransportAuthorizationTestSupport.OperateGroups));
        });
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var session = TransportAuthorizationTestSupport.CreateTransportSession(system, WorkInvocationChannel.HttpApi);

        var handle = await session.Queue.Enqueue("http.route.iteration.logs");
        await handle.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForReadModel(system);
        var workerId = handle.WorkerId?.Value
            ?? throw new InvalidOperationException("Expected worker id.");
        var snapshot = await Direct(system).Query.WorkerIteration(new WorkerIterationReference(new WorkerId(workerId), 1))
            ?? throw new InvalidOperationException("Expected iteration snapshot.");

        Assert.Equal(3, snapshot.Logs.Count);

        var firstPage = await GetJson(
            client,
            $"/workable/workers/{workerId:D}/iterations/1/overview/logs?take=1&sort=Asc&logLevels=Warning,Error");
        var firstCursor = firstPage["page"]?["cursor"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected log page cursor.");
        var secondPage = await GetJson(
            client,
            $"/workable/workers/{workerId:D}/iterations/1/overview/logs?take=1&sort=Asc&logLevels=Warning,Error&cursor={Uri.EscapeDataString(firstCursor)}");

        Assert.Equal(3, firstPage["summary"]?["total"]?.GetValue<int>());
        Assert.Equal(1, firstPage["summary"]?["warning"]?.GetValue<int>());
        Assert.Equal(1, firstPage["summary"]?["error"]?.GetValue<int>());
        Assert.Equal(1, firstPage["summary"]?["information"]?.GetValue<int>());
        Assert.Equal("Warning", firstPage["page"]?["items"]?[0]?["level"]?.GetValue<string>());
        Assert.Equal("HTTP iteration warning log.", firstPage["page"]?["items"]?[0]?["message"]?.GetValue<string>());
        Assert.True(firstPage["page"]?["hasMore"]?.GetValue<bool>());

        Assert.Equal("Error", secondPage["page"]?["items"]?[0]?["level"]?.GetValue<string>());
        Assert.Equal("HTTP iteration error log.", secondPage["page"]?["items"]?[0]?["message"]?.GetValue<string>());
        Assert.False(secondPage["page"]?["hasMore"]?.GetValue<bool>());
    }

    [Fact]
    public async Task MappedHttpRouteCanSearchWorkerAndIterationKeysAndTypes()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;

        var keyedWorker = await Direct(system).Queue.Enqueue(
            "http.route.case",
            WorkInput.Empty
                .WithSubject(new WorkSubjectId("claim", "CLM-123"))
                .WithConcurrencyKey(new WorkConcurrencyKey("tenant", "west"))
                .WithIdentifier(new WorkIdentifier("invoice", "INV-456")),
            options: new WorkerOptions(Configuration: WorkConfiguration.Default));
        await keyedWorker.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForReadModel(system);

        var keysResponse = await client.PostAsJsonAsync(
            "/workable/work-keys/query",
            new
            {
                search = "claim id CLM-123",
            });
        var typesResponse = await client.GetAsync("/workable/work-keys/types?search=claim%20work&skip=0&take=10");
        var filteredTypesResponse = await client.PostAsJsonAsync(
            "/workable/work-keys/types/query",
            new
            {
                type = "claim",
                states = CompletedStatuses,
                skip = 0,
                take = 10,
            });
        var iterationKeysResponse = await client.PostAsJsonAsync(
            "/workable/work-iteration-keys/query",
            new
            {
                search = "claim id CLM-123",
            });
        var iterationTypesResponse = await client.GetAsync("/workable/work-iteration-keys/types?search=claim%20work&skip=0&take=10");
        var filteredIterationTypesResponse = await client.PostAsJsonAsync(
            "/workable/work-iteration-keys/types/query",
            new
            {
                type = "claim",
                statuses = CompletedStatuses,
                skip = 0,
                take = 10,
            });

        keysResponse.EnsureSuccessStatusCode();
        typesResponse.EnsureSuccessStatusCode();
        filteredTypesResponse.EnsureSuccessStatusCode();
        iterationKeysResponse.EnsureSuccessStatusCode();
        iterationTypesResponse.EnsureSuccessStatusCode();
        filteredIterationTypesResponse.EnsureSuccessStatusCode();
        var keysJson = JsonNode.Parse(await keysResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var typesJson = JsonNode.Parse(await typesResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var filteredTypesJson = JsonNode.Parse(await filteredTypesResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var iterationKeysJson = JsonNode.Parse(await iterationKeysResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected iteration keys JSON response.");
        var iterationTypesJson = JsonNode.Parse(await iterationTypesResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected iteration types JSON response.");
        var filteredIterationTypesJson = JsonNode.Parse(await filteredIterationTypesResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected filtered iteration types JSON response.");
        var keys = keysJson["keys"]?.AsArray()
            ?? throw new InvalidOperationException("Expected keys array.");
        var types = typesJson["types"]?.AsArray()
            ?? throw new InvalidOperationException("Expected types array.");
        var filteredTypes = filteredTypesJson["types"]?.AsArray()
            ?? throw new InvalidOperationException("Expected filtered types array.");
        var iterationKeys = iterationKeysJson["keys"]?.AsArray()
            ?? throw new InvalidOperationException("Expected iteration keys array.");
        var iterationTypes = iterationTypesJson["types"]?.AsArray()
            ?? throw new InvalidOperationException("Expected iteration types array.");
        var filteredIterationTypes = filteredIterationTypesJson["types"]?.AsArray()
            ?? throw new InvalidOperationException("Expected filtered iteration types array.");

        Assert.Contains(keys, key =>
            key?["kind"]?.GetValue<string>() == "Subject" &&
            key["type"]?.GetValue<string>() == "claim" &&
            key["value"]?.GetValue<string>() == "CLM-123" &&
            key["workers"]?.AsArray().Count == 1 &&
            key["workers"]?.AsArray().Single()?["definitionName"]?.GetValue<string>() == "http.route.case");
        Assert.Contains(types, type =>
            type?["type"]?.GetValue<string>() == "claim" &&
            type["workerCount"]?.GetValue<int>() == 1 &&
            type["workers"]?.AsArray().Count == 1);
        Assert.Contains(filteredTypes, type =>
            type?["type"]?.GetValue<string>() == "claim" &&
            type["workers"]?.AsArray().Single()?["state"]?.GetValue<string>() == "Completed");
        Assert.Contains(iterationKeys, key =>
            key?["kind"]?.GetValue<string>() == "Subject" &&
            key["type"]?.GetValue<string>() == "claim" &&
            key["value"]?.GetValue<string>() == "CLM-123" &&
            key["iterations"]?.AsArray().Count == 1 &&
            key["iterations"]?.AsArray().Single()?["status"]?.GetValue<string>() == "Completed");
        Assert.Contains(iterationTypes, type =>
            type?["type"]?.GetValue<string>() == "claim" &&
            type["iterationCount"]?.GetValue<int>() == 1 &&
            type["iterations"]?.AsArray().Count == 1);
        Assert.Contains(filteredIterationTypes, type =>
            type?["type"]?.GetValue<string>() == "claim" &&
            type["iterations"]?.AsArray().Single()?["status"]?.GetValue<string>() == "Completed");
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
        var session = Direct(system);

        var queue = await http.Queue.Enqueue(system.Name, "http.action", DirectRequestContext());
        var worker = await http.Query.Worker(session, queue.WorkerId ?? throw new InvalidOperationException("Expected worker id."));
        var outcome = await http.Workers.Execute(session,
            worker!.Id,
            WorkAction.Cancel,
            new WorkableHttpWorkerActionRequest(worker.Revision));
        var canceled = await http.Query.Worker(session, worker.Id);

        Assert.True(outcome.IsAccepted);
        Assert.Equal(WorkerState.Canceled, canceled?.State);
    }

    [Fact]
    public async Task HttpApiCanStartWorkflowAndWaitForCompletion()
    {
        var (system, http) = CreateHost(builder =>
        {
            builder.AddWork(WorkDefinition.Create("http.workflow.child"), SuccessfulWork);
            builder.AddWorkflow(
                WorkflowDefinition.Create("http.workflow.start"),
                workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("http.workflow.child")));
        });
        await system.Start();

        var result = await http.Workflows.Start(
            system,
            "http.workflow.start",
            DirectRequestContext(),
            new WorkableHttpWorkflowStartRequest(Completion: WorkableHttpCompletion.WaitForCompletion));

        Assert.True(result.IsAccepted);
        Assert.NotNull(result.RunId);
        Assert.NotNull(result.Run);
        Assert.Equal(WorkflowRunStatus.Completed, result.Run!.Status);
    }

    [Fact]
    public async Task HttpApiCanStartWorkflowWithInput()
    {
        WorkInput? captured = null;
        var (system, http) = CreateHost(builder =>
        {
            builder.AddWork(
                WorkDefinition.Create("http.workflow.input.child"),
                (_, input, _) =>
                {
                    captured = input;
                    return Task.FromResult(WorkExecutionResult.Success());
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("http.workflow.input"),
                workflow => workflow.DispatchWorkFromWorkflowInput(
                    "dispatch",
                    WorkDefinition.Create("http.workflow.input.child")));
        });
        await system.Start();
        using var document = JsonDocument.Parse("""{"externalKey":"http-42"}""");

        var result = await http.Workflows.Start(
            system,
            "http.workflow.input",
            DirectRequestContext(),
            new WorkableHttpWorkflowStartRequest(
                Input: document.RootElement.Clone(),
                Completion: WorkableHttpCompletion.WaitForCompletion,
                SubjectId: new WorkSubjectId("external", "http-42")));

        Assert.True(result.IsAccepted);
        Assert.NotNull(captured);
        var payload = Assert.IsType<WorkflowHttpInput>(captured!.ToValue<WorkflowHttpInput>());
        Assert.Equal("http-42", payload.ExternalKey);
        Assert.Equal(new WorkSubjectId("external", "http-42"), captured.SubjectId);
        Assert.NotNull(captured.Identifiers);
        Assert.Contains(
            captured.Identifiers,
            identifier => identifier.Type == "workflow-step" && identifier.Value == "dispatch");
    }

    [Theory]
    [InlineData(WorkflowCommandStatus.Accepted, WorkableHttpWorkflowStartStatus.Accepted)]
    [InlineData(WorkflowCommandStatus.Running, WorkableHttpWorkflowStartStatus.Accepted)]
    [InlineData(WorkflowCommandStatus.Paused, WorkableHttpWorkflowStartStatus.Accepted)]
    [InlineData(WorkflowCommandStatus.Blocked, WorkableHttpWorkflowStartStatus.Accepted)]
    [InlineData(WorkflowCommandStatus.Completed, WorkableHttpWorkflowStartStatus.Accepted)]
    [InlineData(WorkflowCommandStatus.Failed, WorkableHttpWorkflowStartStatus.Accepted)]
    [InlineData(WorkflowCommandStatus.Canceled, WorkableHttpWorkflowStartStatus.Accepted)]
    [InlineData(WorkflowCommandStatus.NotFound, WorkableHttpWorkflowStartStatus.NotFound)]
    [InlineData(WorkflowCommandStatus.Unauthorized, WorkableHttpWorkflowStartStatus.Unauthorized)]
    [InlineData(WorkflowCommandStatus.Invalid, WorkableHttpWorkflowStartStatus.Invalid)]
    public void HttpWorkflowAdapterMapsStartStatuses(
        WorkflowCommandStatus source,
        WorkableHttpWorkflowStartStatus expected)
    {
        var actual = InvokePrivateHttpWorkflowAdapter<WorkableHttpWorkflowStartStatus>(
            "MapStartStatus",
            [typeof(WorkflowCommandStatus)],
            source);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(WorkflowCommandStatus.Accepted, WorkableHttpWorkflowActionStatus.Accepted)]
    [InlineData(WorkflowCommandStatus.NotFound, WorkableHttpWorkflowActionStatus.NotFound)]
    [InlineData(WorkflowCommandStatus.Unauthorized, WorkableHttpWorkflowActionStatus.Unauthorized)]
    [InlineData(WorkflowCommandStatus.Invalid, WorkableHttpWorkflowActionStatus.Invalid)]
    public void HttpWorkflowAdapterMapsActionStatuses(
        WorkflowCommandStatus source,
        WorkableHttpWorkflowActionStatus expected)
    {
        var actual = InvokePrivateHttpWorkflowAdapter<WorkableHttpWorkflowActionStatus>(
            "MapActionStatus",
            [typeof(WorkflowCommandStatus)],
            source);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task HttpWorkflowAdapterUsesCommandRunForWaitStartResponse()
    {
        var runId = new WorkflowRunId(Guid.NewGuid());
        var completedAt = DateTimeOffset.UtcNow;
        var commandRun = new WorkflowCommandRun(
            runId,
            "http.workflow.command.snapshot",
            WorkflowRunStatus.Completed,
            [],
            completedAt.AddSeconds(-1),
            completedAt.AddSeconds(-1),
            completedAt,
            []);
        var (system, _) = CreateHost(builder => builder.AddWorkflow(
            WorkflowDefinition.Create("http.workflow.command.snapshot"),
            workflow => workflow.Join("complete")));
        var adapter = new WorkableHttpWorkflowAdapter(new StubWorkflowCommandDispatcher
        {
            StartResult = new WorkflowCommandResult(
                WorkflowCommandStatus.Completed,
                runId,
                WorkflowRunStatus.Completed,
                commandRun,
                ErrorCode: null,
                ErrorMessage: null,
                Messages: []),
        });

        var result = await adapter.Start(
            system,
            "http.workflow.command.snapshot",
            DirectRequestContext(),
            new WorkableHttpWorkflowStartRequest(Completion: WorkableHttpCompletion.WaitForCompletion));

        Assert.Equal(WorkableHttpWorkflowStartStatus.Accepted, result.Status);
        Assert.Equal(runId.Value, result.RunId);
        Assert.NotNull(result.Run);
        Assert.Equal("http.workflow.command.snapshot", result.Run.DefinitionName);
        Assert.Equal(WorkflowRunStatus.Completed, result.Run.Status);
    }

    [Fact]
    public async Task HttpWorkflowAdapterDoesNotHydrateUnauthorizedActionRunById()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (system, _) = CreateHost(builder =>
        {
            builder.AddWork(
                WorkDefinition.Create("http.workflow.command.secured.child"),
                async (_, _, cancellationToken) =>
                {
                    started.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("http.workflow.command.secured"),
                workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("http.workflow.command.secured.child")));
        });
        await system.Start();
        var runtime = Assert.IsType<InMemoryWorkSystem>(system).WorkflowRuntime;
        var handle = runtime.Start("http.workflow.command.secured", DirectRequestContext());
        var runId = handle.RunId ?? throw new InvalidOperationException("Expected workflow run id.");
        await started.Task.WaitAsync(CancellationToken.None);
        Assert.NotNull(runtime.Get(runId));
        var adapter = new WorkableHttpWorkflowAdapter(new StubWorkflowCommandDispatcher
        {
            ExecuteResult = new WorkflowCommandResult(
                WorkflowCommandStatus.Unauthorized,
                runId,
                RunStatus: null,
                Run: null,
                ErrorCode: "workable.workflow.run.unauthorized",
                ErrorMessage: "You are not authorized to operate this workflow run.",
                Messages: [WorkMessage.Error("workable.workflow.run.unauthorized", "You are not authorized to operate this workflow run.")]),
        });

        try
        {
            var result = await adapter.Execute(
                system,
                runId,
                WorkflowAction.Cancel,
                DirectRequestContext());

            Assert.Equal(WorkableHttpWorkflowActionStatus.Unauthorized, result.Status);
            Assert.Equal(runId.Value, result.RunId);
            Assert.Null(result.Run);
        }
        finally
        {
            release.TrySetResult();
        }
    }

    private static T InvokePrivateHttpWorkflowAdapter<T>(
        string name,
        Type[] parameterTypes,
        object argument)
    {
        var method = typeof(WorkableHttpWorkflowAdapter).GetMethod(
            name,
            BindingFlags.NonPublic | BindingFlags.Static,
            binder: null,
            parameterTypes,
            modifiers: null)
            ?? throw new InvalidOperationException($"Expected private method '{name}'.");

        return (T)(method.Invoke(null, [argument])
            ?? throw new InvalidOperationException($"Private method '{name}' returned null."));
    }

    [Fact]
    public async Task HttpApiCanPauseWorkflowGracefully()
    {
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastRuns = 0;
        var (system, http) = CreateHost(builder =>
        {
            builder.AddWork(
                WorkDefinition.Create("http.workflow.slow"),
                async (_, _, cancellationToken) =>
                {
                    slowStarted.TrySetResult();
                    await slowRelease.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddWork(
                WorkDefinition.Create("http.workflow.fast"),
                (_, _, _) =>
                {
                    Interlocked.Increment(ref fastRuns);
                    return Task.FromResult(WorkExecutionResult.Success());
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("http.workflow.stop"),
                workflow => workflow
                    .DispatchWork("slow", WorkDefinition.Create("http.workflow.slow"))
                    .Join("join")
                    .DispatchWork("fast", WorkDefinition.Create("http.workflow.fast")));
        });
        await system.Start();

        var started = await http.Workflows.Start(system, "http.workflow.stop", DirectRequestContext(), null);
        await slowStarted.Task.WaitAsync(CancellationToken.None);

        var stopped = await http.Workflows.Execute(
            system,
            new WorkflowRunId(started.RunId!.Value),
            WorkflowAction.Pause,
            DirectRequestContext("Stop workflow gracefully."));
        slowRelease.TrySetResult();

        WorkflowRunSnapshot? completed = null;
        await TestEventually.Until(
            () =>
            {
                completed = ((InMemoryWorkSystem)system).WorkflowRuntime.Get(new WorkflowRunId(started.RunId!.Value));
                return completed?.Status == WorkflowRunStatus.Paused;
            },
            "Expected the gracefully paused workflow to settle before dispatching later steps.");

        Assert.True(stopped.IsAccepted);
        Assert.Equal(0, Volatile.Read(ref fastRuns));
        Assert.Equal(WorkflowRunStatus.Paused, completed!.Status);
    }

    [Fact]
    public async Task HttpApiCanCancelWorkflowAndOutstandingChildren()
    {
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (system, http) = CreateHost(builder =>
        {
            builder.AddWork(
                WorkDefinition.Create("http.workflow.cancel.child"),
                async (_, _, cancellationToken) =>
                {
                    childStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("http.workflow.cancel"),
                workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("http.workflow.cancel.child")));
        });
        await system.Start();

        var started = await http.Workflows.Start(system, "http.workflow.cancel", DirectRequestContext(), null);
        await childStarted.Task.WaitAsync(CancellationToken.None);

        var canceled = await http.Workflows.Execute(
            system,
            new WorkflowRunId(started.RunId!.Value),
            WorkflowAction.Cancel,
            DirectRequestContext("Cancel workflow immediately."));

        WorkflowRunSnapshot? completed = null;
        await TestEventually.Until(
            () =>
            {
                completed = ((InMemoryWorkSystem)system).WorkflowRuntime.Get(new WorkflowRunId(started.RunId!.Value));
                return completed?.Status == WorkflowRunStatus.Canceled;
            },
            "Expected the canceled workflow to complete as canceled.");

        var childWorkerId = completed!.Steps.Single(step => step.Name == "dispatch").WorkerIds.Single();
        WorkerSnapshot? child = null;
        await TestEventually.Until(
            async () =>
            {
                child = await Direct(system).Query.Worker(childWorkerId);
                return child?.State == WorkerState.Canceled;
            },
            "Expected the canceled workflow child to settle into the final canceled state.",
            timeout: TimeSpan.FromSeconds(10));

        Assert.True(canceled.IsAccepted);
        Assert.Equal(WorkflowRunStatus.Canceled, completed.Status);
        Assert.Equal(WorkerState.Canceled, child!.State);
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
        var session = Direct(system);

        var queue = await http.Queue.Enqueue(system.Name, "http.reconfigure", DirectRequestContext());
        var worker = await http.Query.Worker(session, queue.WorkerId ?? throw new InvalidOperationException("Expected worker id."));
        var outcome = await http.Workers.Reconfigure(session,
            worker!.Id,
            new WorkableHttpWorkerReconfigurationRequest(
                worker.Revision,
                new WorkerReconfiguration(ProfilingEnabled: true)));

        Assert.True(outcome.IsAccepted);
        Assert.True(outcome.Worker?.Options.ProfilingEnabled);
    }

    [Fact]
    public async Task HttpApiCanReconfigureWorkDefinitionDefaults()
    {
        var definition = WorkDefinition.Create(
            "http.definition.reconfigure",
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
            });
        var (system, http) = CreateHost(definition, SuccessfulWork);
        await system.Start();
        var session = Direct(system);

        var outcome = await http.Catalog.ReconfigureDefinition(session,
            definition.Name,
            new WorkableHttpDefinitionReconfigurationRequest(
                definition.Revision,
                new WorkDefinitionReconfiguration(
                    DefaultOptions: new WorkerOptions(ProfilingEnabled: true))));

        var handle = await Direct(system).Queue.Enqueue(definition.Name);
        var worker = await Direct(system).Query.Worker(handle.WorkerId ?? throw new InvalidOperationException("Expected worker id."));

        Assert.True(outcome.IsAccepted);
        Assert.Equal(1, outcome.Definition?.Revision);
        Assert.True(outcome.Definition?.DefaultOptions.ProfilingEnabled);
        Assert.NotNull(worker);
        Assert.True(worker.Options.ProfilingEnabled);
    }

    [Fact]
    public async Task MappedHttpRouteCanReconfigureWorkDefinitionDefaults()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var definition = Direct(system).Catalog.Definitions.Single(definition => definition.Name == "http.route.case");

        var response = await client.PostAsJsonAsync(
            $"/workable/definitions/{definition.Name}/reconfigure",
            new WorkableHttpDefinitionReconfigurationRequest(
                definition.Revision,
                new WorkDefinitionReconfiguration(
                    DefaultOptions: new WorkerOptions(ProfilingEnabled: true))));
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");

        Assert.Equal("Accepted", json["status"]?.GetValue<string>());
        Assert.Equal(1, json["definition"]?["revision"]?.GetValue<int>());
        Assert.True(Direct(system).Catalog.TryGet(definition.Name, out var updated));
        Assert.True(updated.DefaultOptions.ProfilingEnabled);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MappedHttpFullProfileCaptureDefinitionDefaultsRequireDiagnosticsAccess(
        bool canViewDiagnostics)
    {
        var groups = canViewDiagnostics
            ? TransportAuthorizationTestSupport.WorkAdministratorGroups
                .Concat(TransportAuthorizationTestSupport.DiagnosticsGroups)
            : TransportAuthorizationTestSupport.WorkAdministratorGroups;
        using var host = await CreateHttpHost(groups: groups);
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var definition = Direct(system).Catalog.Definitions.Single(definition => definition.Name == "http.route.case");

        var response = await client.PostAsJsonAsync(
            $"/workable/definitions/{definition.Name}/reconfigure",
            new WorkableHttpDefinitionReconfigurationRequest(
                definition.Revision,
                new WorkDefinitionReconfiguration(
                    DefaultOptions: WorkerOptions.Default with
                    {
                        ProfilingEnabled = true,
                        ProfilingCaptureMode = WorkProfileCaptureMode.Full,
                    })));

        Assert.Equal(
            canViewDiagnostics ? HttpStatusCode.OK : HttpStatusCode.Forbidden,
            response.StatusCode);
        Assert.True(Direct(system).Catalog.TryGet(definition.Name, out var current));
        Assert.Equal(
            canViewDiagnostics ? WorkProfileCaptureMode.Full : WorkProfileCaptureMode.Bounded,
            current.DefaultOptions.ProfilingCaptureMode);
    }

    [Fact]
    public async Task MappedHttpRouteReturnsConflictForStaleDefinitionRevision()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var definition = Direct(system).Catalog.Definitions.Single(definition => definition.Name == "http.route.case");

        var first = await client.PostAsJsonAsync(
            $"/workable/definitions/{definition.Name}/reconfigure",
            new WorkableHttpDefinitionReconfigurationRequest(
                definition.Revision,
                new WorkDefinitionReconfiguration(DefaultOptions: new WorkerOptions(ProfilingEnabled: true))));
        var second = await client.PostAsJsonAsync(
            $"/workable/definitions/{definition.Name}/reconfigure",
            new WorkableHttpDefinitionReconfigurationRequest(
                definition.Revision,
                new WorkDefinitionReconfiguration(DefaultOptions: new WorkerOptions(ProfilingEnabled: false))));

        first.EnsureSuccessStatusCode();
        Assert.Equal(System.Net.HttpStatusCode.Conflict, second.StatusCode);
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
        Assert.True(Direct(system).Catalog.TryGet("http.route.case", out var definition));
        await using var queuedSubscription = Direct(system).Events.Subscribe(new WorkEventFilter(DefinitionName: definition.Name, EventType: "worker.queued"));
        await using var queuedReader = queuedSubscription.Read().GetAsyncEnumerator();

        var queueResponse = await client.PostAsJsonAsync(
            "/WORKABLE/WORK/http.route.case",
            new
            {
                completion = "returnafteraccepted",
                description = "Queue this worker from the HTTP API test.",
            });
        queueResponse.EnsureSuccessStatusCode();
        var queueJson = JsonNode.Parse(await queueResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var workerId = Guid.Parse(queueJson["workerId"]?["value"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected worker id."));
        var session = Direct(system);
        var worker = await session.Query.Worker(new WorkerId(workerId))
            ?? throw new InvalidOperationException("Expected worker.");
        await using var actionSubscription = session.Events.Subscribe(new WorkEventFilter(WorkerId: worker.Id, EventType: "worker.cancel"));
        await using var actionReader = actionSubscription.Read().GetAsyncEnumerator();

        Assert.Equal(WorkInvocationChannel.HttpApi, worker.Origin.Channel);
        Assert.Equal(WorkOriginSurface.WorkableAdapter, worker.Origin.Surface);
        Assert.Equal("user-123", worker.Origin.Actor.Id);
        Assert.Equal("greya@example.test", worker.Origin.Actor.Email);
        Assert.Contains("/WORKABLE/WORK/http.route.case", worker.RequestContext.Url, StringComparison.OrdinalIgnoreCase);

        var actionResponse = await client.PostAsJsonAsync(
            $"/WORKABLE/WORKERS/{workerId:D}/ACTIONS/cancel",
            new
            {
                revision = worker.Revision,
                description = "Cancel this worker from the HTTP API test.",
            });
        actionResponse.EnsureSuccessStatusCode();
        var summaryResponse = await client.GetAsync("/workable/workers/status-summary");
        summaryResponse.EnsureSuccessStatusCode();
        var summaryJson = JsonNode.Parse(await summaryResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");

        var queuedEvent = await ReadNext(queuedReader);
        var canceled = await session.Query.Worker(new WorkerId(workerId));
        var actionEvent = await ReadNext(actionReader);
        var queuedOrigin = RequiredData(queuedEvent).GetProperty("origin");
        var actionOrigin = RequiredData(actionEvent).GetProperty("origin");
        Assert.Equal(WorkerState.Canceled, canceled?.State);
        Assert.Equal(worker.DefinitionName, actionEvent.WorkDefinitionName);
        Assert.Equal(1, summaryJson["total"]?.GetValue<int>());
        Assert.Equal("HttpApi", queuedOrigin.GetProperty("channel").GetString());
        Assert.Equal("WorkableAdapter", queuedOrigin.GetProperty("surface").GetString());
        Assert.Equal("user-123", queuedOrigin.GetProperty("actor").GetProperty("id").GetString());
        Assert.Equal("greya@example.test", queuedOrigin.GetProperty("actor").GetProperty("email").GetString());
        Assert.Equal("Queue this worker from the HTTP API test.", queuedOrigin.GetProperty("description").GetString());
        Assert.Contains("/WORKABLE/WORK/http.route.case", queuedOrigin.GetProperty("url").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("HttpApi", actionOrigin.GetProperty("channel").GetString());
        Assert.Equal("WorkableAdapter", actionOrigin.GetProperty("surface").GetString());
        Assert.Equal("user-123", actionOrigin.GetProperty("actor").GetProperty("id").GetString());
        Assert.Equal("greya@example.test", actionOrigin.GetProperty("actor").GetProperty("email").GetString());
        Assert.Equal("Cancel this worker from the HTTP API test.", actionOrigin.GetProperty("description").GetString());
        Assert.Contains("/WORKABLE/WORKERS/", actionOrigin.GetProperty("url").GetString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappedHttpRouteExposesQueueRequestSchema()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/workable/queue-request/schema");
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");

        var schema = JsonNode.Parse(json["schema"]?["jsonSchema"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected queue request schema."))
            ?? throw new InvalidOperationException("Expected queue request schema JSON.");
        Assert.NotNull(schema["properties"]?["completion"]);
        Assert.NotNull(schema["properties"]?["description"]);
        Assert.NotNull(schema["properties"]?["options"]);
        Assert.False(json["capabilities"]?["persistentCoordinationAvailable"]?.GetValue<bool>());
        var queue = json["tabs"]?.AsArray().FirstOrDefault(tab => tab?["id"]?.GetValue<string>() == "queue")
            ?? throw new InvalidOperationException("Expected queue tab.");
        var queueFields = queue["fields"]?.AsArray()
            ?? throw new InvalidOperationException("Expected queue fields.");
        var coordination = json["tabs"]?.AsArray().FirstOrDefault(tab => tab?["id"]?.GetValue<string>() == "coordination")
            ?? throw new InvalidOperationException("Expected coordination tab.");
        var fields = coordination["fields"]?.AsArray()
            ?? throw new InvalidOperationException("Expected coordination fields.");
        var schemaJson = schema.ToJsonString();

        Assert.Contains(queueFields, field => field?["path"]?.GetValue<string>() == "description");
        Assert.Contains(queueFields, field => field?["path"]?.GetValue<string>() == "subjectId.type");
        Assert.Contains(queueFields, field => field?["path"]?.GetValue<string>() == "subjectId.value");
        Assert.DoesNotContain(fields, field => field?["path"]?.GetValue<string>() == "subjectId.type");
        Assert.DoesNotContain(fields, field => field?["path"]?.GetValue<string>() == "subjectId.value");
        Assert.Contains(fields, field => field?["path"]?.GetValue<string>() == "options.configuration.coordination.storage");
        Assert.Contains(fields, field => field?["path"]?.GetValue<string>() == "options.configuration.coordination.idempotency.isEnabled");
        Assert.Contains(fields, field => field?["path"]?.GetValue<string>() == "options.configuration.coordination.concurrency.isEnabled");
        Assert.Contains(fields, field => field?["path"]?.GetValue<string>() == "options.configuration.coordination.durability.isEnabled");
        Assert.DoesNotContain("usesPersistentStorage", schemaJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isIdempotencyEnabled", schemaJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isPersistentIdempotencyEnabled", schemaJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isConcurrencyEnabled", schemaJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isPersistentConcurrencyEnabled", schemaJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isDurabilityEnabled", schemaJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("requiresPersistenceStore", schemaJson, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("invocation", json.ToJsonString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappedHttpWorkerConfigurationIncludesDefinitionInfoAndQueueSchema()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;

        var queueResponse = await client.PostAsJsonAsync(
            "/workable/work/http.route.case",
            new
            {
                completion = "returnAfterAccepted",
                input = new
                {
                    attempt = 1,
                },
                subjectId = new
                {
                    type = "claim",
                    value = "CLM-123",
                },
                concurrencyKey = new
                {
                    type = "tenant",
                    value = "west",
                },
            });
        queueResponse.EnsureSuccessStatusCode();
        var queueJson = JsonNode.Parse(await queueResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected queue JSON response.");
        var workerId = Guid.Parse(queueJson["workerId"]?["value"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected worker id."));

        await WaitForReadModel(system);

        var response = await client.GetAsync($"/workable/workers/{workerId:D}/configuration");
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");

        Assert.NotNull(json["configuration"]);
        Assert.Equal("CLM-123", json["subjectId"]?["value"]?.GetValue<string>());
        Assert.Equal("west", json["concurrencyKey"]?["value"]?.GetValue<string>());
        Assert.Contains("\"attempt\":1", json["input"]?["json"]?.GetValue<string>() ?? string.Empty, StringComparison.Ordinal);
        Assert.NotNull(json["definitionInfo"]?["definition"]);
        Assert.Equal("http.route.case", json["definitionInfo"]?["definition"]?["name"]?.GetValue<string>());
        Assert.NotNull(json["queueRequestSchema"]?["schema"]?["jsonSchema"]);
        Assert.NotNull(json["queueRequestSchema"]?["tabs"]);
    }

    [Fact]
    public async Task MappedHttpQueueByNameRecordsHttpOrigin()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        Assert.True(Direct(system).Catalog.TryGet("http.route.case", out var definition));

        var response = await client.PostAsJsonAsync(
            $"/workable/work/{definition.Name}",
            new
            {
                completion = "returnAfterAccepted",
            });
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var workerId = Guid.Parse(json["workerId"]?["value"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected worker id."));

        var worker = await Direct(system).Query.Worker(new WorkerId(workerId))
            ?? throw new InvalidOperationException("Expected worker.");

        Assert.Equal(WorkInvocationChannel.HttpApi, worker.Origin.Channel);
        Assert.Equal("user-123", worker.Origin.Actor.Id);
        Assert.Equal("greya@example.test", worker.Origin.Actor.Email);
        Assert.Contains($"/workable/work/{definition.Name}", worker.RequestContext.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappedHttpDefinitionsListIncludesInvocationChannels()
    {
        using var host = await CreateDefinitionDiscoveryHttpHost();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/workable/definitions");
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var definitions = json.AsArray();

        var dotNetOnly = definitions.Single(definition => definition?["name"]?.GetValue<string>() == "http.discovery.dotnet-only")
            ?? throw new InvalidOperationException("Expected dotnet-only definition.");
        var channels = dotNetOnly["configuration"]?["invocation"]?["allowedChannels"]?.AsArray()
            ?? throw new InvalidOperationException("Expected invocation channels.");
        var jsonText = dotNetOnly.ToJsonString();

        Assert.Contains(channels, channel => channel?.GetValue<string>() == "InProcess");
        Assert.DoesNotContain(channels, channel => channel?.GetValue<string>() == "HttpApi");
        Assert.DoesNotContain("usesPersistentStorage", jsonText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isIdempotencyEnabled", jsonText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isPersistentIdempotencyEnabled", jsonText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isConcurrencyEnabled", jsonText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isPersistentConcurrencyEnabled", jsonText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("isDurabilityEnabled", jsonText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("requiresPersistenceStore", jsonText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappedHttpDefinitionsCanReturnCatalogLevels()
    {
        using var host = await CreateDefinitionCatalogHttpHost();
        var client = host.GetTestClient();

        var root = await GetJson(client, "/workable/definitions?level=true");
        var billing = await GetJson(client, "/workable/definitions?level=true&category=Billing");
        var invoices = await GetJson(client, "/workable/definitions?level=true&category=Billing%3AInvoices");

        var rootCategories = root["categories"]?.AsArray()
            ?? throw new InvalidOperationException("Expected root categories.");
        var rootBilling = rootCategories.Single(category => category?["path"]?.GetValue<string>() == "Billing")
            ?? throw new InvalidOperationException("Expected Billing category.");
        Assert.Equal(2, rootBilling["count"]?.GetValue<int>());
        Assert.Contains(rootCategories, category => category?["path"]?.GetValue<string>() == "Operations");
        Assert.Empty(root["definitions"]?.AsArray()
            ?? throw new InvalidOperationException("Expected root definitions."));

        var billingCategoryLabels = billing["categories"]?.AsArray()
            .Select(category => category?["label"]?.GetValue<string>() ?? "")
            .ToArray()
            ?? throw new InvalidOperationException("Expected Billing categories.");
        Assert.Equal(["Invoices", "Payments"], billingCategoryLabels);
        Assert.Empty(billing["definitions"]?.AsArray()
            ?? throw new InvalidOperationException("Expected Billing definitions."));

        Assert.Empty(invoices["categories"]?.AsArray()
            ?? throw new InvalidOperationException("Expected Invoices categories."));
        var definition = Assert.Single(invoices["definitions"]?.AsArray()
            ?? throw new InvalidOperationException("Expected Invoices definitions."));
        Assert.Equal("billing.invoice.generate", definition?["name"]?.GetValue<string>());
        Assert.Equal("Billing:Invoices", definition?["category"]?.GetValue<string>());
        Assert.Null(definition?["configuration"]);
        Assert.Null(definition?["defaultOptions"]);
        Assert.Null(definition?["inputSchema"]);
        Assert.Null(definition?["outputSchema"]);
    }

    [Fact]
    public async Task MappedHttpDefinitionsCanReadSingleDefinition()
    {
        using var host = await CreateDefinitionCatalogHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var definition = Direct(system).Catalog.Definitions.Single(definition => definition.Name == "billing.invoice.generate");

        var json = await GetJson(client, $"/workable/definitions/{definition.Name}");

        Assert.Equal("billing.invoice.generate", json["name"]?.GetValue<string>());
        Assert.NotNull(json["configuration"]);
        Assert.NotNull(json["defaultOptions"]);
    }

    [Fact]
    public async Task MappedHttpWorkInfoCanBeReadByWorkName()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        Assert.True(Direct(system).Catalog.TryGet("http.route.case", out var definition));

        var byName = await client.GetAsync("/workable/work/http.route.case/info");

        byName.EnsureSuccessStatusCode();
        var byNameJson = JsonNode.Parse(await byName.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");

        Assert.Equal("http.route.case", byNameJson["definition"]?["name"]?.GetValue<string>());
        Assert.NotNull(byNameJson["queueRequestSchema"]?["schema"]?["jsonSchema"]);
        Assert.NotNull(byNameJson["queueRequestSchema"]?["tabs"]);
    }

    [Fact]
    public async Task MappedHttpAnonymousRequestIsRejected()
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
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var workers = await Direct(system).Query.Workers(new WorkerCriteria(DefinitionName: "http.route.case"));
        Assert.Empty(workers.Workers);
    }

    [Fact]
    public async Task MappedHttpAnonymousRequestIsRejectedBeforeBodyBinding()
    {
        using var host = await CreateHttpHost(authenticated: false);
        var client = host.GetTestClient();
        using var content = new StringContent("{", System.Text.Encoding.UTF8, "application/json");

        using var response = await client.PostAsync("/workable/work/http.route.case", content);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task SurfaceGateAllowsPreflightAndReturnsItsStableAnonymousChallenge()
    {
        static void DisableTransportPolicy(IServiceCollection services)
            => services.PostConfigure<WorkableAspNetCoreAuthorizationOptions>(options =>
                options.TransportAuthenticationScheme = null);

        using var preflightHost = await CreateHttpHost(
            authenticated: false,
            surfaceAccessGroups: ["surface.operator"],
            configureServices: DisableTransportPolicy);
        using var preflightRequest = new HttpRequestMessage(
            HttpMethod.Options,
            "/workable/definitions");
        using var preflight = await preflightHost.GetTestClient().SendAsync(preflightRequest);

        using var anonymousHost = await CreateHttpHost(
            authenticated: false,
            surfaceAccessGroups: ["surface.operator"],
            configureServices: DisableTransportPolicy);
        using var anonymous = await anonymousHost.GetTestClient().GetAsync("/workable/definitions");
        var anonymousJson = await anonymous.Content.ReadAsStringAsync();

        Assert.NotEqual(HttpStatusCode.Unauthorized, preflight.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, preflight.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        Assert.Contains("workable.http.authentication_required", anonymousJson);
    }

    [Fact]
    public async Task MappingRejectsAnyAuthorizationDisabledSystemAndNamesIt()
    {
        using var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddWorkableSystem(builder => builder.RequireAuthorization(false));
                    services.AddWorkableHttpApi();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapWorkableApi("/workable"));
                });
            })
            .Build();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("<default>", exception.Message, StringComparison.Ordinal);
        Assert.Contains("do not require authorization", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedConfiguredUrlsDoNotEnableDebugRoutes()
    {
        using var host = await CreateHttpHost(
            authenticated: false,
            configuredUrls: "not-an-absolute-url");

        using var response = await host.GetTestClient().GetAsync("/workable/debug/realtime");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MappedHttpCanUseExplicitWorkableAuthenticationSchemeWithoutChangingHostDefaultScheme()
    {
        using var host = await CreateExplicitSchemeHttpHost();
        var client = host.GetTestClient();

        using var unauthorized = await client.GetAsync("/workable/host");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        client.DefaultRequestHeaders.Authorization = WorkableSchemeAuthenticationTestSupport.CreateBearerHeader();
        using var authorized = await client.GetAsync("/workable/host");

        authorized.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task MappedHttpUsesWorkableTransportSchemeWhenHostFallbackPolicyTargetsAnotherScheme()
    {
        using var host = await CreateExplicitSchemeHttpHostWithFallbackPolicy();
        var client = host.GetTestClient();
        client.DefaultRequestHeaders.Authorization = WorkableSchemeAuthenticationTestSupport.CreateBearerHeader();

        using var response = await client.GetAsync("/workable/host");
        response.EnsureSuccessStatusCode();

        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected host JSON response.");
        var systems = body["systems"]?.AsArray()
            ?? throw new InvalidOperationException("Expected systems array.");
        var system = Assert.Single(systems);
        Assert.NotNull(system?["access"]);
    }

    [Fact]
    public async Task DebugRealtimeEndpointIsAvailableWithoutAuthenticationForLocalRequests()
    {
        using var host = await CreateHttpHost(authenticated: false, development: true);
        var client = host.GetTestClient();

        var body = await GetJson(client, "/workable/debug/realtime");

        Assert.NotNull(body["eventSubscriptions"]);
        Assert.NotNull(body["viewSubscriptions"]);
        Assert.NotNull(body["workerOverviewSubscriptions"]);
    }

    [Fact]
    public async Task DebugRealtimeEndpointCanFilterByConnectionId()
    {
        using var host = await CreateHttpHost(authenticated: false, development: true);
        var client = host.GetTestClient();

        var body = await GetJson(client, "/workable/debug/realtime?connectionId=missing-connection");

        Assert.Empty(body["eventSubscriptions"]?.AsArray() ?? []);
        Assert.Empty(body["viewSubscriptions"]?.AsArray() ?? []);
        Assert.Empty(body["workerOverviewSubscriptions"]?.AsArray() ?? []);
    }

    [Fact]
    public async Task DebugRealtimeEndpointIsNotRegisteredOutsideDevelopmentOrLoopbackUrls()
    {
        using var host = await CreateHttpHost(authenticated: false);
        var client = host.GetTestClient();

        using var response = await client.GetAsync("/workable/debug/realtime");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DebugRealtimeEndpointIsNotRegisteredWhenConfiguredUrlsMixLoopbackAndNonLoopbackHosts()
    {
        using var host = await CreateHttpHost(
            authenticated: false,
            configuredUrls: "http://localhost:5050;http://0.0.0.0:5051");
        var client = host.GetTestClient();

        using var response = await client.GetAsync("/workable/debug/realtime");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
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
                description = "Queue this worker from the HTTP API test.",
                completion = "returnAfterAccepted",
            });
        queueResponse.EnsureSuccessStatusCode();
        var queueJson = JsonNode.Parse(await queueResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var workerId = Guid.Parse(queueJson["workerId"]?["value"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected worker id."));
        var session = Direct(system);
        var worker = await session.Query.Worker(new WorkerId(workerId))
            ?? throw new InvalidOperationException("Expected worker.");
        await using var reconfigureSubscription = session.Events.Subscribe(new WorkEventFilter(WorkerId: worker.Id, EventType: "worker.reconfigured"));
        await using var reconfigureReader = reconfigureSubscription.Read().GetAsyncEnumerator();

        var reconfigureResponse = await client.PostAsJsonAsync(
            $"/workable/workers/{workerId:D}/reconfigure",
            new
            {
                revision = worker.Revision,
                description = "Reconfigure this worker from the HTTP API test.",
                changes = new
                {
                    profilingEnabled = true,
                },
            });
        reconfigureResponse.EnsureSuccessStatusCode();

        var reconfigureEvent = await ReadNext(reconfigureReader);
        var origin = RequiredData(reconfigureEvent).GetProperty("origin");

        Assert.Equal(worker.DefinitionName, reconfigureEvent.WorkDefinitionName);
        Assert.Equal("HttpApi", origin.GetProperty("channel").GetString());
        Assert.Equal("WorkableAdapter", origin.GetProperty("surface").GetString());
        Assert.Equal("user-123", origin.GetProperty("actor").GetProperty("id").GetString());
        Assert.Equal("greya@example.test", origin.GetProperty("actor").GetProperty("email").GetString());
        Assert.Equal("Reconfigure this worker from the HTTP API test.", origin.GetProperty("description").GetString());
        Assert.Contains("/workable/workers/", origin.GetProperty("url").GetString(), StringComparison.OrdinalIgnoreCase);
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

        var worker = await Direct(background).Query.Worker(new WorkerId(workerId))
            ?? throw new InvalidOperationException("Expected worker.");

        Assert.Equal("http.named", worker.DefinitionName);
        Assert.Equal(WorkInvocationChannel.HttpApi, worker.Origin.Channel);
        Assert.Equal(WorkOriginSurface.WorkableAdapter, worker.Origin.Surface);
        Assert.Contains("/workable/systems/background/work/http.named", worker.RequestContext.Url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappedHttpRouteCanRequireSurfaceAccessOuterGate()
    {
        using var host = await CreateHttpHost(
            groups: TransportAuthorizationTestSupport.SystemAdministratorGroups,
            surfaceAccessGroups: ["workable.surface"]);
        var client = host.GetTestClient();

        var response = await client.GetAsync("/workable/host");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var message = Assert.Single(json["messages"]?.AsArray()
            ?? throw new InvalidOperationException("Expected messages."));
        Assert.Equal("workable.http.surface.access_denied", message?["code"]?.GetValue<string>());
    }

    [Fact]
    public async Task MappedHttpRouteAllowsConfiguredSurfaceAccessOuterGate()
    {
        using var host = await CreateHttpHost(
            groups: TransportAuthorizationTestSupport.SystemAdministratorGroups.Concat(["workable.surface"]),
            surfaceAccessGroups: ["workable.surface"]);
        var client = host.GetTestClient();

        var response = await client.GetAsync("/workable/host");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task MappedHttpRouteAllowsConfiguredBuiltInSurfaceGroupWithoutAdministratorRoles()
    {
        var groups = TransportAuthorizationTestSupport.BuiltInHttpApiSurfaceGroups
            .Concat(TransportAuthorizationTestSupport.ReadAllWorkGroups)
            .ToArray();
        using var host = await CreateHttpHost(
            builder =>
            {
                builder.ConfigureAuthorization(authorization => authorization
                    .AllowBuiltInHttpApiToGroups(TransportAuthorizationTestSupport.BuiltInHttpApiSurfaceGroups.ToArray())
                    .AllowReadAllWorkToGroups(TransportAuthorizationTestSupport.ReadAllWorkGroups.ToArray()));
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create(
                        "http.surface.case",
                        configuration: WorkConfiguration.Default with
                        {
                            Start = WorkStartConfiguration.DoNotStart,
                        }),
                    SuccessfulWork);
            },
            groups: groups);
        var client = host.GetTestClient();

        var definitionsResponse = await client.GetAsync("/workable/definitions");
        var hostResponse = await client.GetAsync("/workable/host");

        definitionsResponse.EnsureSuccessStatusCode();
        hostResponse.EnsureSuccessStatusCode();
        var definitions = JsonNode.Parse(await definitionsResponse.Content.ReadAsStringAsync())?.AsArray()
            ?? throw new InvalidOperationException("Expected definitions array.");
        var hostJson = JsonNode.Parse(await hostResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var system = Assert.Single(hostJson["systems"]?.AsArray()
            ?? throw new InvalidOperationException("Expected systems array."));

        Assert.Single(definitions);
        Assert.False(system?["access"]?["isSystemAdministrator"]?.GetValue<bool>() ?? true);
        Assert.False(system?["access"]?["isWorkAdministrator"]?.GetValue<bool>() ?? true);
        Assert.True(system?["access"]?["canReadAllWork"]?.GetValue<bool>() ?? false);
    }

    [Fact]
    public async Task MappedHttpNamedSystemRouteAllowsConfiguredBuiltInSurfaceGroupWithoutAdministratorRoles()
    {
        var groups = TransportAuthorizationTestSupport.BuiltInHttpApiSurfaceGroups
            .Concat(TransportAuthorizationTestSupport.ReadAllWorkGroups)
            .ToArray();
        using var host = await CreateMultiSystemHttpHost(
            groups: groups,
            configureSystems: builder => builder.ConfigureAuthorization(authorization => authorization
                .AllowBuiltInHttpApiToGroups(TransportAuthorizationTestSupport.BuiltInHttpApiSurfaceGroups.ToArray())
                .AllowReadAllWorkToGroups(TransportAuthorizationTestSupport.ReadAllWorkGroups.ToArray())));
        var client = host.GetTestClient();

        var response = await client.GetAsync("/workable/systems/background/definitions");

        response.EnsureSuccessStatusCode();
        var definitions = JsonNode.Parse(await response.Content.ReadAsStringAsync())?.AsArray()
            ?? throw new InvalidOperationException("Expected definitions array.");
        Assert.Single(definitions);
    }

    [Fact]
    public async Task MappedHttpRouteListsAvailableSystemsWithCapabilities()
    {
        using var host = await CreateMultiSystemHttpHost();
        var client = host.GetTestClient();

        var response = await client.GetAsync("/workable/host");
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        Assert.False(json["capabilities"]?["realtime"]?["enabled"]?.GetValue<bool>() ?? true);
        var systems = json["systems"]?.AsArray()
            ?? throw new InvalidOperationException("Expected systems array.");

        Assert.Equal(2, systems.Count);
        Assert.Contains(systems, system =>
            system is JsonObject candidate &&
            candidate["name"] is null &&
            candidate["isDefault"] is JsonValue isDefault &&
            isDefault.GetValue<bool>() &&
            candidate["capabilities"]?["persistentCoordinationAvailable"] is JsonValue persistentCoordinationAvailable &&
            !persistentCoordinationAvailable.GetValue<bool>() &&
            candidate["capabilities"]?["sqlProfilingAvailable"] is JsonValue sqlProfilingAvailable &&
            !sqlProfilingAvailable.GetValue<bool>() &&
            candidate["access"]?["isSystemAdministrator"] is JsonValue isSystemAdministrator &&
            isSystemAdministrator.GetValue<bool>() &&
            candidate["access"]?["isWorkAdministrator"] is JsonValue isWorkAdministrator &&
            isWorkAdministrator.GetValue<bool>() &&
            candidate["access"]?["canViewDiagnostics"] is JsonValue canViewDiagnostics &&
            canViewDiagnostics.GetValue<bool>() &&
            candidate["access"]?["canControlSystem"] is JsonValue canControlSystem &&
            canControlSystem.GetValue<bool>() &&
            candidate["access"]?["canReadAllWork"] is JsonValue canReadAllWork &&
            canReadAllWork.GetValue<bool>() &&
            candidate["access"]?["canOperateAllWork"] is JsonValue canOperateAllWork &&
            canOperateAllWork.GetValue<bool>() &&
            candidate["access"]?["totalDefinitionCount"] is JsonValue totalDefinitionCount &&
            totalDefinitionCount.GetValue<int>() == 1 &&
            candidate["access"]?["readableDefinitionCount"] is JsonValue readableDefinitionCount &&
            readableDefinitionCount.GetValue<int>() == 1 &&
            candidate["access"]?["operableDefinitionCount"] is JsonValue operableDefinitionCount &&
            operableDefinitionCount.GetValue<int>() == 1);
        Assert.Contains(systems, system =>
            system is JsonObject candidate &&
            candidate["name"]?.GetValue<string>() == "background" &&
            candidate["isDefault"] is JsonValue isDefault &&
            !isDefault.GetValue<bool>() &&
            candidate["capabilities"]?["persistentCoordinationAvailable"] is JsonValue persistentCoordinationAvailable &&
            !persistentCoordinationAvailable.GetValue<bool>() &&
            candidate["capabilities"]?["sqlProfilingAvailable"] is JsonValue sqlProfilingAvailable &&
            !sqlProfilingAvailable.GetValue<bool>());
    }

    [Fact]
    public async Task MappedHttpRouteListsSqlProfilingCapabilityWhenRegistered()
    {
        using var host = await CreateHttpHost(
            configureServices: services => services.AddSingleton<IWorkSystemCapabilityContributor, TestSqlProfilingCapabilityContributor>());
        var client = host.GetTestClient();

        var response = await client.GetAsync("/workable/host");
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var systems = json["systems"]?.AsArray()
            ?? throw new InvalidOperationException("Expected systems array.");

        Assert.Contains(systems, system =>
            system is JsonObject candidate &&
            candidate["isDefault"] is JsonValue isDefault &&
            isDefault.GetValue<bool>() &&
            candidate["capabilities"]?["sqlProfilingAvailable"] is JsonValue sqlProfilingAvailable &&
            sqlProfilingAvailable.GetValue<bool>());
    }

    [Fact]
    public async Task MappedHttpRouteListsHttpClientProfilingCapabilityWhenRegistered()
    {
        using var host = await CreateHttpHost(
            configureServices: services => services.AddWorkableHttpClientProfiling());
        var client = host.GetTestClient();

        var response = await client.GetAsync("/workable/host");
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var systems = json["systems"]?.AsArray()
            ?? throw new InvalidOperationException("Expected systems array.");

        Assert.Contains(systems, system =>
            system is JsonObject candidate &&
            candidate["isDefault"] is JsonValue isDefault &&
            isDefault.GetValue<bool>() &&
            candidate["capabilities"]?["httpClientProfilingAvailable"] is JsonValue httpClientProfilingAvailable &&
            httpClientProfilingAvailable.GetValue<bool>());
    }

    [Fact]
    public async Task MappedHttpRouteFiltersSystemsWithoutAnyAccess()
    {
        using var host = await CreateMultiSystemHttpHost(Array.Empty<string>());
        var client = host.GetTestClient();

        var response = await client.GetAsync("/workable/host");
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var systems = json["systems"]?.AsArray()
            ?? throw new InvalidOperationException("Expected systems array.");

        Assert.Empty(systems);
    }

    [Fact]
    public async Task MappedHttpNamedSystemRoutesRequireBuiltInSurfaceAccess()
    {
        using var host = await CreateMultiSystemHttpHost(Array.Empty<string>());
        var client = host.GetTestClient();

        var definitionsResponse = await client.GetAsync("/workable/systems/background/definitions");
        var definitionsJson = await definitionsResponse.Content.ReadAsStringAsync();
        var queueResponse = await client.PostAsJsonAsync(
            "/workable/systems/background/work/http.named",
            new
            {
                completion = "returnAfterAccepted",
            });
        var queueJson = await queueResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, definitionsResponse.StatusCode);
        Assert.Contains("workable.http.surface.system_access_denied", definitionsJson);
        Assert.Equal(HttpStatusCode.Forbidden, queueResponse.StatusCode);
        Assert.Contains("workable.http.surface.system_access_denied", queueJson);
    }

    [Fact]
    public async Task MappedHttpNamedSystemRoutesResolveAuthorizationGroupsOncePerSurface()
    {
        var groups = TransportAuthorizationTestSupport.SystemAdministratorGroups
            .Concat(TransportAuthorizationTestSupport.ReadAllWorkGroups)
            .Concat(["workable.surface"])
            .ToArray();
        var provider = new CountingWorkAuthorizationGroupProvider(groups);
        using var host = await CreateMultiSystemHttpHost(
            groups,
            services =>
            {
                services.AddSingleton(provider);
                services.AddSingleton<IWorkAuthorizationGroupProvider>(provider);
            },
            surfaceAccessGroups: ["workable.surface"]);
        var client = host.GetTestClient();

        var response = await client.GetAsync("/workable/systems/background/definitions");

        response.EnsureSuccessStatusCode();
        Assert.Equal(1, provider.GetCallCount(systemName: null));
        Assert.Equal(1, provider.GetCallCount("background"));
    }

    [Fact]
    public async Task MappedHttpRouteFiltersSystemsWithoutBuiltInSurfaceAccess()
    {
        using var host = await CreateMultiSystemHttpHost(TransportAuthorizationTestSupport.ReadGroups);
        var client = host.GetTestClient();

        var response = await client.GetAsync("/workable/host");
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var systems = json["systems"]?.AsArray()
            ?? throw new InvalidOperationException("Expected systems array.");

        Assert.Empty(systems);
    }

    [Fact]
    public async Task MappedHttpDiagnosticsRouteReturnsSystemCounters()
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
        await Direct(system).Query.Workers();

        var response = await client.GetAsync("/workable/diagnostics");
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var readModel = json["readModel"]
            ?? throw new InvalidOperationException("Expected read model diagnostics.");
        var queue = json["queue"]
            ?? throw new InvalidOperationException("Expected queue diagnostics.");
        var retention = json["retention"]
            ?? throw new InvalidOperationException("Expected retention diagnostics.");
        var concurrency = json["concurrency"]
            ?? throw new InvalidOperationException("Expected concurrency diagnostics.");
        var durability = json["durability"]
            ?? throw new InvalidOperationException("Expected durability diagnostics.");
        var idempotency = json["idempotency"]
            ?? throw new InvalidOperationException("Expected idempotency diagnostics.");

        Assert.Equal("Started", json["state"]?.GetValue<string>());
        Assert.Equal(0, queue["rejectedWorkCount"]?.GetValue<long>());
        Assert.Null(queue["lastRejectedAt"]);
        Assert.True(readModel["enqueuedSequence"]?.GetValue<long>() > 0);
        Assert.Equal(
            readModel["enqueuedSequence"]?.GetValue<long>(),
            readModel["appliedSequence"]?.GetValue<long>());
        Assert.Equal(0, readModel["pendingUpdateCount"]?.GetValue<long>());
        Assert.True(readModel["appliedUpdateCount"]?.GetValue<long>() > 0);
        Assert.True(readModel["publishedSnapshotCount"]?.GetValue<long>() > 0);
        Assert.False(readModel["hasProjectorFailure"]?.GetValue<bool>());
        Assert.True(retention["trackedFinalWorkerCount"]?.GetValue<int>() >= 0);
        Assert.True(retention["scheduledPurgeCount"]?.GetValue<int>() >= 0);
        Assert.True(retention["scheduledPurgeHighWaterMark"]?.GetValue<int>() >= 0);
        Assert.False(retention["hasSchedulerFailure"]?.GetValue<bool>());
        Assert.True(concurrency["deferredStartCount"]?.GetValue<int>() >= 0);
        Assert.True(concurrency["lastDrainReleasedCount"]?.GetValue<int>() >= 0);
        Assert.True(durability["acceptedWaiterCount"]?.GetValue<int>() >= 0);
        Assert.True(durability["pendingCleanupCount"]?.GetValue<int>() >= 0);
        Assert.False(durability["hasReaderFailure"]?.GetValue<bool>());
        Assert.False(durability["hasLeaseRenewalFailure"]?.GetValue<bool>());
        Assert.False(durability["hasCleanupFailure"]?.GetValue<bool>());
        Assert.True(idempotency["duplicateRejectionCount"]?.GetValue<long>() >= 0);
        Assert.True(idempotency.AsObject().ContainsKey("lastDuplicateRejectedStorage"));
    }

    [Fact]
    public async Task MappedHttpDiagnosticsRouteRequiresDiagnosticsPermission()
    {
        using var host = await CreateHttpHost(groups: TransportAuthorizationTestSupport.WorkAdministratorGroups);
        var client = host.GetTestClient();

        var response = await client.GetAsync("/workable/diagnostics");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("diagnostics", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappedHttpDiagnosticsViewRouteRequiresDiagnosticsPermission()
    {
        using var host = await CreateHttpHost(groups: TransportAuthorizationTestSupport.WorkAdministratorGroups);
        var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync("/workable/views/diagnostics", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("diagnostics", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappedHttpDiagnosticsComponentRouteRequiresDiagnosticsPermission()
    {
        using var host = await CreateHttpHost(groups: TransportAuthorizationTestSupport.WorkAdministratorGroups);
        var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync("/workable/components/readModelDiagnostics", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("diagnostics", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappedHttpLifecycleRoutesCanStartAndStopSystem()
    {
        using var host = await CreateManualHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;

        var startResponse = await client.PostAsync("/workable/lifecycle/start", content: null);
        startResponse.EnsureSuccessStatusCode();
        var startJson = JsonNode.Parse(await startResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");

        var stopResponse = await client.PostAsync("/workable/lifecycle/stop", content: null);
        stopResponse.EnsureSuccessStatusCode();
        var stopJson = JsonNode.Parse(await stopResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");

        Assert.Equal(WorkSystemState.Stopped, system.State);
        Assert.Equal("Started", startJson["state"]?.GetValue<string>());
        Assert.Equal("Stopped", stopJson["state"]?.GetValue<string>());
        Assert.Empty(stopJson["forceInterruptedWorkers"]?.AsArray()
            ?? throw new InvalidOperationException("Expected force-interrupted worker array."));
    }

    [Fact]
    public async Task MappedHttpLifecycleRoutesRequireControlSystemPermission()
    {
        using var host = await CreateManualHttpHost(TransportAuthorizationTestSupport.WorkAdministratorGroups);
        var client = host.GetTestClient();

        var startResponse = await client.PostAsync("/workable/lifecycle/start", content: null);
        var stopResponse = await client.PostAsync("/workable/lifecycle/stop", content: null);

        Assert.Equal(HttpStatusCode.Forbidden, startResponse.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, stopResponse.StatusCode);
    }

    [Fact]
    public async Task MappedHttpLifecycleStopReturnsForceInterruptedWorkerNames()
    {
        using var host = await CreateShutdownHttpHost();
        var client = host.GetTestClient();
        var tracker = host.Services.GetRequiredService<HttpShutdownTracker>();

        var queueResponse = await client.PostAsJsonAsync(
            "/workable/work/http.shutdown.force",
            new
            {
                completion = "returnAfterAccepted",
            });
        queueResponse.EnsureSuccessStatusCode();
        await tracker.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stopResponse = await client.PostAsync("/workable/lifecycle/stop", content: null);
        stopResponse.EnsureSuccessStatusCode();
        var stopJson = JsonNode.Parse(await stopResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var names = stopJson["forceInterruptedWorkerNames"]?.AsArray()
            ?? throw new InvalidOperationException("Expected force-interrupted worker names.");
        var summaries = stopJson["forceInterruptedWorkerSummaries"]?.AsArray()
            ?? throw new InvalidOperationException("Expected force-interrupted worker summaries.");

        var name = Assert.Single(names)
            ?? throw new InvalidOperationException("Expected force-interrupted worker name.");
        Assert.Equal("http.shutdown.force", name.GetValue<string>());
        var summary = Assert.Single(summaries)
            ?? throw new InvalidOperationException("Expected force-interrupted worker summary.");
        Assert.Equal("http.shutdown.force", summary["definitionName"]?.GetValue<string>());
    }

    [Fact]
    public async Task MappedHttpWorkflowStartRouteCanWaitForCompletion()
    {
        using var host = await CreateHttpHost(builder =>
        {
            builder.AddAuthorizedTransportWork(WorkDefinition.Create("http.workflow.child"), SuccessfulWork);
            builder.AddWorkflow(
                WorkflowDefinition.Create("http.workflow.start"),
                workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("http.workflow.child")),
                authorize => authorize.AllowOperateToGroups(TransportAuthorizationTestSupport.OperateGroups.ToArray()));
        });
        var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync(
            "/workable/workflows/http.workflow.start",
            new
            {
                completion = "waitForCompletion",
                description = "Run workflow to completion from the HTTP API test.",
            });
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");

        Assert.Equal("Accepted", json["status"]?.GetValue<string>());
        Assert.NotNull(json["runId"]);
        Assert.Equal("Completed", json["run"]?["status"]?.GetValue<string>());
        Assert.Equal("http.workflow.start", json["run"]?["definitionName"]?.GetValue<string>());
    }

    [Fact]
    public async Task MappedHttpWorkflowStartRouteCanProvideWorkflowInput()
    {
        WorkInput? captured = null;
        using var host = await CreateHttpHost(builder =>
        {
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("http.workflow.route.input.child"),
                (_, input, _) =>
                {
                    captured = input;
                    return Task.FromResult(WorkExecutionResult.Success());
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("http.workflow.route.input"),
                workflow => workflow.DispatchWorkFromWorkflowInput(
                    "dispatch",
                    WorkDefinition.Create("http.workflow.route.input.child")),
                authorize => authorize.AllowOperateToGroups(TransportAuthorizationTestSupport.OperateGroups.ToArray()));
        });
        var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync(
            "/workable/workflows/http.workflow.route.input",
            new
            {
                input = new
                {
                    externalKey = "route-42",
                },
                completion = "waitForCompletion",
                description = "Run workflow with input from the HTTP API route test.",
                identifiers = new[]
                {
                    new
                    {
                        type = "external",
                        value = "route-42",
                    },
                },
            });
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");

        Assert.Equal("Accepted", json["status"]?.GetValue<string>());
        Assert.Equal("Completed", json["run"]?["status"]?.GetValue<string>());
        Assert.NotNull(captured);
        var payload = captured!.ToValue<WorkflowHttpInput>()
            ?? throw new InvalidOperationException("Expected workflow input payload.");
        Assert.Equal("route-42", payload.ExternalKey);
        Assert.Contains(new WorkIdentifier("external", "route-42"), captured.Identifiers!);
        Assert.Contains(
            captured.Identifiers!,
            identifier => identifier.Type == "workflow-step" && identifier.Value == "dispatch");
    }

    [Fact]
    public async Task MappedHttpWorkflowQueryRoutesReturnRunningWorkflowSummariesAndDetail()
    {
        var emailStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoiceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = await CreateHttpHost(builder =>
        {
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("http.workflow.observe.email"),
                async (_, _, cancellationToken) =>
                {
                    emailStarted.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("http.workflow.observe.invoice"),
                async (_, _, cancellationToken) =>
                {
                    invoiceStarted.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("http.workflow.observe"),
                workflow => workflow
                    .RunParallel("notify", parallel => parallel
                        .DispatchWork("email", WorkDefinition.Create("http.workflow.observe.email"))
                        .DispatchWork("invoice", WorkDefinition.Create("http.workflow.observe.invoice")))
                    .Join("settle"),
                authorize => authorize
                    .AllowReadToGroups(TransportAuthorizationTestSupport.ReadGroups.ToArray())
                    .AllowOperateToGroups(TransportAuthorizationTestSupport.OperateGroups.ToArray()));
        });
        var client = host.GetTestClient();

        var startResponse = await client.PostAsJsonAsync(
            "/workable/workflows/http.workflow.observe",
            new
            {
                completion = "returnAfterAccepted",
            });
        startResponse.EnsureSuccessStatusCode();
        var startJson = JsonNode.Parse(await startResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var runId = Guid.Parse(startJson["runId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected workflow run id."));
        await Task.WhenAll(emailStarted.Task, invoiceStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));

        var listResponse = await client.GetAsync("/workable/workflow-runs");
        listResponse.EnsureSuccessStatusCode();
        var listJson = JsonNode.Parse(await listResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new InvalidOperationException("Expected workflow list response.");

        var detailResponse = await client.GetAsync($"/workable/workflow-runs/{runId:D}");
        detailResponse.EnsureSuccessStatusCode();
        var detailJson = JsonNode.Parse(await detailResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new InvalidOperationException("Expected workflow detail response.");

        release.TrySetResult();

        var runs = listJson["runs"]?.AsArray()
            ?? throw new InvalidOperationException("Expected workflow runs array.");
        var run = Assert.Single(runs, item => string.Equals(item?["runId"]?.GetValue<string>(), runId.ToString("D"), StringComparison.OrdinalIgnoreCase))!;
        Assert.Equal("http.workflow.observe", run["definitionName"]?.GetValue<string>());
        Assert.Equal("notify", run["currentStepName"]?.GetValue<string>());
        Assert.Equal("WaitingOnChildren", run["currentStepStatus"]?.GetValue<string>());
        Assert.Equal(2, run["outstandingChildren"]?["total"]?.GetValue<int>());
        Assert.Null(run["outstandingChildren"]?["byState"]);

        Assert.Null(detailJson["definitionName"]);
        Assert.Equal("notify", detailJson["currentStepName"]?.GetValue<string>());
        Assert.Equal(2, detailJson["outstandingChildren"]?["total"]?.GetValue<int>());
        var steps = detailJson["steps"]?.AsArray()
            ?? throw new InvalidOperationException("Expected workflow steps.");
        var notify = steps.Single(step => string.Equals(step?["name"]?.GetValue<string>(), "notify", StringComparison.Ordinal));
        Assert.Equal("WaitingOnChildren", notify?["status"]?.GetValue<string>());
        Assert.Equal(2, notify?["children"]?["total"]?.GetValue<int>());
        var notifyChildren = notify?["steps"]?.AsArray()
            .Select(step => step?["name"]?.GetValue<string>() ?? string.Empty)
            .ToArray()
            ?? throw new InvalidOperationException("Expected notify child steps.");
        Assert.Equal(["email", "invoice"], notifyChildren);
    }

    [Fact]
    public async Task MappedHttpWorkflowQueryRoutesSupportFiltersAndChildSampleSize()
    {
        var emailStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var invoiceStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = await CreateHttpHost(builder =>
        {
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("http.workflow.filter.email"),
                async (_, _, cancellationToken) =>
                {
                    emailStarted.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("http.workflow.filter.invoice"),
                async (_, _, cancellationToken) =>
                {
                    invoiceStarted.TrySetResult();
                    await release.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("http.workflow.filter.done.child"),
                SuccessfulWork);
            builder.AddWorkflow(
                WorkflowDefinition.Create("http.workflow.filter.running"),
                workflow => workflow
                    .RunParallel("notify", parallel => parallel
                        .DispatchWork("email", WorkDefinition.Create("http.workflow.filter.email"))
                        .DispatchWork("invoice", WorkDefinition.Create("http.workflow.filter.invoice")))
                    .Join("settle"),
                authorize => authorize
                    .AllowReadToGroups(TransportAuthorizationTestSupport.ReadGroups.ToArray())
                    .AllowOperateToGroups(TransportAuthorizationTestSupport.OperateGroups.ToArray()));
            builder.AddWorkflow(
                WorkflowDefinition.Create("http.workflow.filter.completed"),
                workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("http.workflow.filter.done.child")),
                authorize => authorize
                    .AllowReadToGroups(TransportAuthorizationTestSupport.ReadGroups.ToArray())
                    .AllowOperateToGroups(TransportAuthorizationTestSupport.OperateGroups.ToArray()));
        });
        var client = host.GetTestClient();

        var runningStart = await client.PostAsJsonAsync("/workable/workflows/http.workflow.filter.running", new { completion = "returnAfterAccepted" });
        runningStart.EnsureSuccessStatusCode();
        var runningJson = JsonNode.Parse(await runningStart.Content.ReadAsStringAsync()) ?? throw new InvalidOperationException("Expected JSON.");
        var runId = Guid.Parse(runningJson["runId"]?.GetValue<string>() ?? throw new InvalidOperationException("Expected workflow run id."));
        var completedStart = await client.PostAsJsonAsync("/workable/workflows/http.workflow.filter.completed", new { completion = "waitForCompletion" });
        completedStart.EnsureSuccessStatusCode();
        await Task.WhenAll(emailStarted.Task, invoiceStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));

        var activeOnly = await client.GetAsync("/workable/workflow-runs?definitionName=http.workflow.filter.running");
        activeOnly.EnsureSuccessStatusCode();
        var activeOnlyJson = JsonNode.Parse(await activeOnly.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new InvalidOperationException("Expected workflow list response.");
        Assert.Single(activeOnlyJson["runs"]?.AsArray() ?? throw new InvalidOperationException("Expected workflow runs."));

        var includeFinal = await client.GetAsync("/workable/workflow-runs?includeFinal=true");
        includeFinal.EnsureSuccessStatusCode();
        var includeFinalJson = JsonNode.Parse(await includeFinal.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new InvalidOperationException("Expected workflow list response.");
        Assert.Equal(2, includeFinalJson["runs"]?.AsArray()?.Count);

        var detailResponse = await client.GetAsync($"/workable/workflow-runs/{runId:D}?childSampleSize=1");
        detailResponse.EnsureSuccessStatusCode();
        var detailJson = JsonNode.Parse(await detailResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new InvalidOperationException("Expected workflow detail response.");
        var childrenResponse = await client.GetAsync($"/workable/workflow-runs/{runId:D}/steps/notify/children?skip=1&take=1");
        childrenResponse.EnsureSuccessStatusCode();
        var childrenJson = JsonNode.Parse(await childrenResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new InvalidOperationException("Expected workflow child page response.");

        release.TrySetResult();

        var notify = detailJson["steps"]?.AsArray()
            ?.Single(step => string.Equals(step?["name"]?.GetValue<string>(), "notify", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Expected notify step.");
        Assert.Equal(1, notify["childSample"]?.AsArray()?.Count);
        Assert.Null(notify["additionalChildCount"]);
        Assert.Equal(2, childrenJson["totalCount"]?.GetValue<int>());
        Assert.Equal(1, childrenJson["skip"]?.GetValue<int>());
        Assert.Equal(1, childrenJson["take"]?.GetValue<int>());
        var childWorker = Assert.Single(childrenJson["workers"]?.AsArray() ?? throw new InvalidOperationException("Expected workers page."));
        Assert.Equal("http.workflow.filter.invoice", childWorker?["definitionName"]?.GetValue<string>());
    }

    [Fact]
    public async Task MappedHttpWorkflowQueryDetailRouteReturnsNotFoundForUnknownRun()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();

        var response = await client.GetAsync($"/workable/workflow-runs/{Guid.NewGuid():D}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task MappedHttpWorkflowQueryRoutesHideUnreadableWorkflowRuns()
    {
        using var host = await CreateHttpHost(
            builder =>
            {
                builder.ConfigureAuthorization(authorization => authorization
                    .AllowBuiltInHttpApiToGroups(TransportAuthorizationTestSupport.BuiltInHttpApiSurfaceGroups.ToArray()));
                builder.AddAuthorizedTransportWork(WorkDefinition.Create("http.workflow.read.secured.child"), SuccessfulWork);
                builder.AddWorkflow(
                    WorkflowDefinition.Create("http.workflow.read.secured"),
                    workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("http.workflow.read.secured.child")),
                    authorize => authorize
                        .AllowReadToGroups("workflow.read")
                        .AllowOperateToGroups("workflow.ops"));
            },
            groups: TransportAuthorizationTestSupport.BuiltInHttpApiSurfaceGroups);
        var client = host.GetTestClient();
        var system = Assert.IsType<InMemoryWorkSystem>(host.Services.GetRequiredService<IWorkSystemRegistry>().Default);

        var actor = new WorkActor("workflow-seed-user", "Workflow Seed User");
        var handle = system.WorkflowRuntime.Start(
            "http.workflow.read.secured",
            WorkRequestContext.Create(
                WorkInvocationChannel.InProcess,
                actor,
                isAuthenticated: true) with
            {
                Authorization = WorkAuthorizationSnapshot.Create(
                    actor,
                    ["workflow.read", "workflow.ops"],
                    readableDefinitionIds: null),
            });
        await handle.WaitForCompletion();
        var runId = handle.RunId?.Value ?? throw new InvalidOperationException("Expected workflow run id.");

        var listResponse = await client.GetAsync("/workable/workflow-runs?includeFinal=true");
        listResponse.EnsureSuccessStatusCode();
        var listJson = JsonNode.Parse(await listResponse.Content.ReadAsStringAsync())?.AsObject()
            ?? throw new InvalidOperationException("Expected workflow list response.");
        var detailResponse = await client.GetAsync($"/workable/workflow-runs/{runId:D}");

        Assert.Empty(listJson["runs"]?.AsArray() ?? throw new InvalidOperationException("Expected workflow runs."));
        Assert.Equal(HttpStatusCode.NotFound, detailResponse.StatusCode);
    }

    [Fact]
    public async Task MappedHttpWorkflowStopRouteGracefullyPausesBeforeLaterSteps()
    {
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastRuns = 0;
        using var host = await CreateHttpHost(builder =>
        {
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("http.workflow.stop.slow"),
                async (_, _, cancellationToken) =>
                {
                    slowStarted.TrySetResult();
                    await slowRelease.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("http.workflow.stop.fast"),
                (_, _, _) =>
                {
                    Interlocked.Increment(ref fastRuns);
                    return Task.FromResult(WorkExecutionResult.Success());
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("http.workflow.stop"),
                workflow => workflow
                    .DispatchWork("slow", WorkDefinition.Create("http.workflow.stop.slow"))
                    .Join("join")
                    .DispatchWork("fast", WorkDefinition.Create("http.workflow.stop.fast")),
                authorize => authorize.AllowOperateToGroups(TransportAuthorizationTestSupport.OperateGroups.ToArray()));
        });
        var client = host.GetTestClient();
        var system = Assert.IsType<InMemoryWorkSystem>(host.Services.GetRequiredService<IWorkSystemRegistry>().Default);

        var startResponse = await client.PostAsJsonAsync(
            "/workable/workflows/http.workflow.stop",
            new
            {
                completion = "returnAfterAccepted",
            });
        startResponse.EnsureSuccessStatusCode();
        var startJson = JsonNode.Parse(await startResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var runId = Guid.Parse(startJson["runId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected workflow run id."));
        await slowStarted.Task.WaitAsync(CancellationToken.None);

        var stopResponse = await client.PostAsJsonAsync(
            $"/workable/workflow-runs/{runId:D}/actions/stop",
            new
            {
                description = "Stop workflow gracefully from the HTTP API test.",
            });
        stopResponse.EnsureSuccessStatusCode();
        var stopJson = JsonNode.Parse(await stopResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");

        slowRelease.TrySetResult();

        WorkflowRunSnapshot? completed = null;
        await TestEventually.Until(
            () =>
            {
                completed = system.WorkflowRuntime.Get(new WorkflowRunId(runId));
                return completed?.Status == WorkflowRunStatus.Paused;
            },
            "Expected the HTTP-stopped workflow to settle as paused.");

        Assert.Equal("Accepted", stopJson["status"]?.GetValue<string>());
        Assert.Equal("Pause", stopJson["action"]?.GetValue<string>());
        Assert.Equal(0, Volatile.Read(ref fastRuns));
        Assert.Equal(WorkflowRunStatus.Paused, completed?.Status);
    }

    [Fact]
    public async Task MappedHttpWorkflowPauseAndStartRoutesPauseAndResumeWorkflowRuns()
    {
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slowRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastRuns = 0;
        using var host = await CreateHttpHost(builder =>
        {
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("http.workflow.pause.slow"),
                async (_, _, cancellationToken) =>
                {
                    slowStarted.TrySetResult();
                    await slowRelease.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("http.workflow.pause.fast"),
                (_, _, _) =>
                {
                    Interlocked.Increment(ref fastRuns);
                    return Task.FromResult(WorkExecutionResult.Success());
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("http.workflow.pause"),
                workflow => workflow
                    .DispatchWork("slow", WorkDefinition.Create("http.workflow.pause.slow"))
                    .Join("join")
                    .DispatchWork("fast", WorkDefinition.Create("http.workflow.pause.fast")),
                authorize => authorize.AllowOperateToGroups(TransportAuthorizationTestSupport.OperateGroups.ToArray()));
        });
        var client = host.GetTestClient();
        var system = Assert.IsType<InMemoryWorkSystem>(host.Services.GetRequiredService<IWorkSystemRegistry>().Default);

        var startResponse = await client.PostAsJsonAsync(
            "/workable/workflows/http.workflow.pause",
            new
            {
                completion = "returnAfterAccepted",
            });
        startResponse.EnsureSuccessStatusCode();
        var startJson = JsonNode.Parse(await startResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var runId = Guid.Parse(startJson["runId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected workflow run id."));
        await slowStarted.Task.WaitAsync(CancellationToken.None);

        var pauseResponse = await client.PostAsJsonAsync(
            $"/workable/workflow-runs/{runId:D}/actions/pause",
            new
            {
                description = "Pause workflow through the explicit HTTP pause route.",
            });
        pauseResponse.EnsureSuccessStatusCode();
        var pauseJson = JsonNode.Parse(await pauseResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");

        slowRelease.TrySetResult();

        WorkflowRunSnapshot? paused = null;
        await TestEventually.Until(
            () =>
            {
                paused = system.WorkflowRuntime.Get(new WorkflowRunId(runId));
                return paused?.Status == WorkflowRunStatus.Paused;
            },
            "Expected the HTTP-paused workflow to settle as paused.");

        Assert.Equal("Accepted", pauseJson["status"]?.GetValue<string>());
        Assert.Equal("Pause", pauseJson["action"]?.GetValue<string>());
        Assert.Equal(0, Volatile.Read(ref fastRuns));

        var resumeResponse = await client.PostAsJsonAsync(
            $"/workable/workflow-runs/{runId:D}/actions/start",
            new
            {
                description = "Resume workflow through the explicit HTTP start route.",
            });
        resumeResponse.EnsureSuccessStatusCode();
        var resumeJson = JsonNode.Parse(await resumeResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");

        WorkflowRunSnapshot? completed = null;
        await TestEventually.Until(
            () =>
            {
                completed = system.WorkflowRuntime.Get(new WorkflowRunId(runId));
                return completed?.Status == WorkflowRunStatus.Completed;
            },
            "Expected the HTTP-started workflow to resume and complete.");

        Assert.Equal("Accepted", resumeJson["status"]?.GetValue<string>());
        Assert.Equal("Start", resumeJson["action"]?.GetValue<string>());
        Assert.Equal(1, Volatile.Read(ref fastRuns));
        Assert.Equal(WorkflowRunStatus.Completed, completed?.Status);
    }

    [Fact]
    public async Task MappedHttpWorkflowCancelRouteCancelsOutstandingChildren()
    {
        var childStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = await CreateHttpHost(builder =>
        {
            builder.AddAuthorizedTransportWork(
                WorkDefinition.Create("http.workflow.cancel.child"),
                async (_, _, cancellationToken) =>
                {
                    childStarted.TrySetResult();
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return WorkExecutionResult.Success();
                });
            builder.AddWorkflow(
                WorkflowDefinition.Create("http.workflow.cancel"),
                workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("http.workflow.cancel.child")),
                authorize => authorize.AllowOperateToGroups(TransportAuthorizationTestSupport.OperateGroups.ToArray()));
        });
        var client = host.GetTestClient();
        var system = Assert.IsType<InMemoryWorkSystem>(host.Services.GetRequiredService<IWorkSystemRegistry>().Default);

        var startResponse = await client.PostAsJsonAsync(
            "/workable/workflows/http.workflow.cancel",
            new
            {
                completion = "returnAfterAccepted",
            });
        startResponse.EnsureSuccessStatusCode();
        var startJson = JsonNode.Parse(await startResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var runId = Guid.Parse(startJson["runId"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected workflow run id."));
        await childStarted.Task.WaitAsync(CancellationToken.None);

        var cancelResponse = await client.PostAsJsonAsync(
            $"/workable/workflow-runs/{runId:D}/actions/cancel",
            new
            {
                description = "Cancel workflow immediately from the HTTP API test.",
            });
        cancelResponse.EnsureSuccessStatusCode();
        var cancelJson = JsonNode.Parse(await cancelResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");

        WorkflowRunSnapshot? completed = null;
        await TestEventually.Until(
            () =>
            {
                completed = system.WorkflowRuntime.Get(new WorkflowRunId(runId));
                return completed?.Status == WorkflowRunStatus.Canceled;
            },
            "Expected the HTTP-canceled workflow to settle as canceled.");

        var childWorkerId = completed!.Steps.Single(step => step.Name == "dispatch").WorkerIds.Single();
        WorkerSnapshot? child = null;
        await TestEventually.Until(
            async () =>
            {
                child = await Direct(system).Query.Worker(childWorkerId);
                return child?.State == WorkerState.Canceled;
            },
            "Expected the HTTP-canceled workflow child to settle into the final canceled state.",
            timeout: TimeSpan.FromSeconds(10));

        Assert.Equal("Accepted", cancelJson["status"]?.GetValue<string>());
        Assert.Equal("Cancel", cancelJson["action"]?.GetValue<string>());
        Assert.Equal(WorkerState.Canceled, child!.State);
    }

    [Fact]
    public async Task MappedHttpWorkflowStartRouteReturnsNotFoundForUnknownWorkflow()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync(
            "/workable/workflows/http.workflow.missing",
            new
            {
                completion = "returnAfterAccepted",
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("workable.workflow.definition.not_found", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MappedHttpWorkflowActionRouteReturnsNotFoundForUnknownRun()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync(
            $"/workable/workflow-runs/{Guid.NewGuid():D}/actions/cancel",
            new
            {
                description = "Cancel a missing workflow run from the HTTP API test.",
            });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Contains("workable.workflow.run.not_found", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MappedHttpWorkflowActionRouteRejectsUnsupportedActions()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync(
            $"/workable/workflow-runs/{Guid.NewGuid():D}/actions/not-a-real-action",
            new
            {
                description = "Attempt unsupported workflow action from the HTTP API test.",
            });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("workable.http.workflow.action.invalid", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MappedHttpWorkerActionRoutesRejectUnsupportedActionsWithTheStableContract()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();

        var bulk = await client.PostAsJsonAsync(
            "/workable/workers/actions/not-a-real-action",
            new { description = "Attempt unsupported bulk worker action." });
        var single = await client.PostAsJsonAsync(
            $"/workable/workers/{Guid.NewGuid():D}/actions/not-a-real-action",
            new { revision = 1, description = "Attempt unsupported worker action." });

        Assert.Equal(HttpStatusCode.BadRequest, bulk.StatusCode);
        Assert.Contains("workable.http.action.invalid", await bulk.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.BadRequest, single.StatusCode);
        Assert.Contains("workable.http.action.invalid", await single.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MappedHttpQueryRoutesReturnTheirDocumentedSuccessPayloads()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();

        var components = await client.PostAsJsonAsync(
            "/workable/components/query",
            new
            {
                components = new[]
                {
                    new { id = "system", type = "system", shape = "compact" },
                },
            });
        var definitions = await client.PostAsJsonAsync("/workable/definitions/query", new { });
        var definitionInfo = await client.GetAsync("/workable/definitions/http.route.case/info");
        var statusSummary = await client.PostAsJsonAsync("/workable/workers/status-summary", new { });

        components.EnsureSuccessStatusCode();
        definitions.EnsureSuccessStatusCode();
        definitionInfo.EnsureSuccessStatusCode();
        statusSummary.EnsureSuccessStatusCode();
        Assert.Contains("system", await components.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("http.route.case", await definitions.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("http.route.case", await definitionInfo.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Contains("counts", await statusSummary.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappedHttpWorkflowStartRouteRequiresWorkflowOperatePermission()
    {
        using var host = await CreateHttpHost(
            builder =>
            {
                builder.AddAuthorizedTransportWork(WorkDefinition.Create("http.workflow.secured.child"), SuccessfulWork);
                builder.AddWorkflow(
                    WorkflowDefinition.Create("http.workflow.secured"),
                    workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("http.workflow.secured.child")),
                    authorize => authorize.AllowOperateToGroups("workflow.ops"));
            },
            groups: TransportAuthorizationTestSupport.SystemAdministratorGroups);
        var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync(
            "/workable/workflows/http.workflow.secured",
            new
            {
                completion = "returnAfterAccepted",
            });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("workable.workflow.definition.unauthorized", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MappedHttpBulkActionRouteCanTargetCategory()
    {
        using var host = await CreateBulkActionHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;

        var billingQueue = await QueueAndReadWorkerId(client, "/workable/work/http.bulk.billing");
        var emailQueue = await QueueAndReadWorkerId(client, "/workable/work/http.bulk.email");
        await WaitForReadModel(system);

        var actionResponse = await client.PostAsJsonAsync(
            "/workable/workers/actions/cancel",
            new
            {
                category = "Billing",
                description = "Cancel all billing workers from the HTTP API test.",
            });
        actionResponse.EnsureSuccessStatusCode();
        var actionJson = JsonNode.Parse(await actionResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        await WaitForReadModel(system);

        var billing = await Direct(system).Query.Worker(new WorkerId(billingQueue))
            ?? throw new InvalidOperationException("Expected billing worker.");
        var email = await Direct(system).Query.Worker(new WorkerId(emailQueue))
            ?? throw new InvalidOperationException("Expected email worker.");

        Assert.Equal(1, actionJson["matchedWorkerCount"]?.GetValue<int>());
        Assert.Equal(1, actionJson["acceptedCount"]?.GetValue<int>());
        Assert.Equal(WorkerState.Canceled, billing.State);
        Assert.Equal(WorkerState.Queued, email.State);
        Assert.Equal(WorkInvocationChannel.HttpApi, billing.ActionHistory[^1].Origin.Channel);
        Assert.Equal("Cancel all billing workers from the HTTP API test.", billing.ActionHistory[^1].RequestContext.Description);
        Assert.Contains("/workable/workers/actions/cancel", billing.ActionHistory[^1].RequestContext.Url, StringComparison.OrdinalIgnoreCase);
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

        var worker = await Direct(background).Query.Worker(new WorkerId(workerId))
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
        worker = await Direct(background).Query.Worker(new WorkerId(workerId))
            ?? throw new InvalidOperationException("Expected worker.");
        Assert.True(worker.Options.ProfilingEnabled);

        var actionResponse = await client.PostAsJsonAsync(
            $"/workable/systems/background/workers/{workerId:D}/actions/cancel",
            new
            {
                revision = worker.Revision,
            });
        actionResponse.EnsureSuccessStatusCode();

        var canceled = await Direct(background).Query.Worker(new WorkerId(workerId))
            ?? throw new InvalidOperationException("Expected worker.");
        Assert.Equal(WorkerState.Canceled, canceled.State);
        Assert.Equal(WorkInvocationChannel.HttpApi, canceled.ActionHistory[^1].Origin.Channel);
        Assert.Contains("/workable/systems/background/workers/", canceled.ActionHistory[^1].RequestContext.Url, StringComparison.OrdinalIgnoreCase);
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

    [Fact]
    public async Task EveryMappedNamedSystemQueryRouteReturnsStructuredNotFoundForUnknownSystem()
    {
        using var host = await CreateMultiSystemHttpHost();
        var client = host.GetTestClient();
        const string workerId = "11111111-2222-3333-4444-555555555555";
        var routes = new (HttpMethod Method, string Path)[]
        {
            (HttpMethod.Post, "/components/query"),
            (HttpMethod.Post, "/components/system"),
            (HttpMethod.Post, "/views/overview"),
            (HttpMethod.Post, "/definitions/query"),
            (HttpMethod.Get, "/definitions/missing/info"),
            (HttpMethod.Get, "/work/missing/info"),
            (HttpMethod.Get, $"/workers/{workerId}"),
            (HttpMethod.Get, $"/workers/{workerId}/configuration"),
            (HttpMethod.Get, $"/workers/{workerId}/overview"),
            (HttpMethod.Get, $"/workers/{workerId}/overview/logs"),
            (HttpMethod.Get, $"/workers/{workerId}/overview/timeline"),
            (HttpMethod.Get, $"/workers/{workerId}/iterations/1"),
            (HttpMethod.Get, $"/workers/{workerId}/iterations/1/overview"),
            (HttpMethod.Get, $"/workers/{workerId}/iterations/1/overview/messages"),
            (HttpMethod.Get, $"/workers/{workerId}/iterations/1/overview/logs"),
            (HttpMethod.Get, "/workers/status-summary"),
            (HttpMethod.Post, "/workers/status-summary"),
            (HttpMethod.Post, "/work-keys/query"),
            (HttpMethod.Get, "/work-keys/types"),
            (HttpMethod.Post, "/work-keys/types/query"),
            (HttpMethod.Post, "/work-iteration-keys/query"),
            (HttpMethod.Get, "/work-iteration-keys/types"),
            (HttpMethod.Post, "/work-iteration-keys/types/query"),
            (HttpMethod.Get, "/profiling/capture-rules"),
            (HttpMethod.Post, "/profiling/capture-rules"),
            (HttpMethod.Delete, "/profiling/capture-rules/11111111-2222-3333-4444-555555555555"),
            (HttpMethod.Post, "/workers/actions/pause"),
            (HttpMethod.Post, $"/workers/{workerId}/actions/pause"),
            (HttpMethod.Post, $"/workers/{workerId}/reconfigure"),
            (HttpMethod.Get, "/workflow-runs"),
            (HttpMethod.Get, $"/workflow-runs/{workerId}"),
            (HttpMethod.Get, $"/workflow-runs/{workerId}/steps/step/children"),
            (HttpMethod.Post, "/workflows/example"),
            (HttpMethod.Post, $"/workflow-runs/{workerId}/actions/cancel"),
        };

        foreach (var (method, path) in routes)
        {
            using var request = new HttpRequestMessage(
                method,
                $"/workable/systems/missing{path}");
            if (method == HttpMethod.Post)
            {
                request.Content = JsonContent.Create(new { });
            }

            using var response = await client.SendAsync(request);
            var json = await response.Content.ReadAsStringAsync();

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("workable.http.system.not_found", json);
        }
    }

    [Fact]
    public async Task MappedHttpRouteRequiresBuiltInSurfaceAccessBeforeDefinitionAuthorization()
    {
        using var host = await CreateAuthorizedHttpHost();
        var client = host.GetTestClient();
        Assert.True(host.Services.GetRequiredService<IWorkSystemRegistry>().Default.RequiresAuthorization);

        var definitionsResponse = await client.GetAsync("/workable/definitions");
        var definitionsJson = await definitionsResponse.Content.ReadAsStringAsync();

        var allowedResponse = await client.PostAsJsonAsync(
            "/workable/work/allowed.authorization",
            new
            {
                completion = "returnAfterAccepted",
            });
        var allowedJson = await allowedResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Forbidden, definitionsResponse.StatusCode);
        Assert.Contains("workable.http.surface.system_access_denied", definitionsJson);
        Assert.Equal(HttpStatusCode.Forbidden, allowedResponse.StatusCode);
        Assert.Contains("workable.http.surface.system_access_denied", allowedJson);
    }

    private static (IWorkSystem System, HttpAdapterServices Http) CreateHost(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute)
        => CreateHost(builder => builder.AddWork(definition, execute));

    private static (IWorkSystem System, HttpAdapterServices Http) CreateHost(
        Action<IWorkSystemBuilder> configure)
    {
        var provider = new ServiceCollection()
            .AddTransportTestAuthorization()
            .AddWorkableSystem(configure)
            .AddWorkableHttpApi()
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        return (system, new HttpAdapterServices(
            provider.GetRequiredService<WorkableHttpTopologyResolver>(),
            provider.GetRequiredService<WorkableHttpCatalogAdapter>(),
            provider.GetRequiredService<WorkableHttpQueueAdapter>(),
            provider.GetRequiredService<WorkableHttpQueryAdapter>(),
            provider.GetRequiredService<WorkableHttpWorkflowAdapter>(),
            provider.GetRequiredService<WorkableHttpWorkerAdapter>()));
    }

    private sealed record HttpAdapterServices(
        WorkableHttpTopologyResolver Topology,
        WorkableHttpCatalogAdapter Catalog,
        WorkableHttpQueueAdapter Queue,
        WorkableHttpQueryAdapter Query,
        WorkableHttpWorkflowAdapter Workflows,
        WorkableHttpWorkerAdapter Workers);

    private sealed class StubWorkflowCommandDispatcher : IWorkflowCommandDispatcher
    {
        public WorkflowCommandResult? StartResult { get; init; }

        public WorkflowCommandResult? ExecuteResult { get; init; }

        public Task<WorkflowCommandResult> Start(
            string workflowName,
            WorkRequestContext requestContext,
            WorkflowCommandOptions? options = null,
            CancellationToken cancellationToken = default)
            => this.Start(
                systemName: null,
                workflowName,
                requestContext,
                options,
                cancellationToken);

        public Task<WorkflowCommandResult> Start(
            string workflowName,
            WorkRequestContext requestContext,
            WorkInput? input,
            WorkflowCommandOptions? options = null,
            CancellationToken cancellationToken = default)
            => this.Start(
                systemName: null,
                workflowName,
                requestContext,
                input,
                options,
                cancellationToken);

        public Task<WorkflowCommandResult> Start(
            string? systemName,
            string workflowName,
            WorkRequestContext requestContext,
            WorkflowCommandOptions? options = null,
            CancellationToken cancellationToken = default)
            => this.Start(
                systemName,
                workflowName,
                requestContext,
                input: null,
                options,
                cancellationToken);

        public Task<WorkflowCommandResult> Start(
            string? systemName,
            string workflowName,
            WorkRequestContext requestContext,
            WorkInput? input,
            WorkflowCommandOptions? options = null,
            CancellationToken cancellationToken = default)
            => Task.FromResult(this.StartResult ?? throw new InvalidOperationException("Expected start result."));

        public Task<WorkflowCommandResult> Execute(
            WorkflowRunId runId,
            WorkflowRunAction action,
            WorkRequestContext requestContext,
            CancellationToken cancellationToken = default)
            => this.Execute(
                systemName: null,
                runId,
                action,
                requestContext,
                cancellationToken);

        public Task<WorkflowCommandResult> Execute(
            string? systemName,
            WorkflowRunId runId,
            WorkflowRunAction action,
            WorkRequestContext requestContext,
            CancellationToken cancellationToken = default)
            => Task.FromResult(this.ExecuteResult ?? throw new InvalidOperationException("Expected execute result."));
    }

    private static Task<IHost> CreateHttpHost(
        bool authenticated = true,
        IEnumerable<string>? groups = null,
        bool development = false,
        string? configuredUrls = null,
        IEnumerable<string>? surfaceAccessGroups = null,
        Action<IServiceCollection>? configureServices = null)
        => CreateHttpHost(
            builder =>
            {
                builder.AddAuthorizedTransportWork(
                    WorkDefinition.Create(
                        "http.route.case",
                        configuration: WorkConfiguration.Default with
                        {
                            Start = WorkStartConfiguration.DoNotStart,
                        }),
                    SuccessfulWork);
            },
            authenticated,
            groups,
            development,
            configuredUrls,
            surfaceAccessGroups,
            configureServices);

    private static async Task<IHost> CreateHttpHost(
        Action<IWorkSystemBuilder> configure,
        bool authenticated = true,
        IEnumerable<string>? groups = null,
        bool development = false,
        string? configuredUrls = null,
        IEnumerable<string>? surfaceAccessGroups = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                if (development)
                {
                    web.UseEnvironment(Environments.Development);
                }

                if (!string.IsNullOrWhiteSpace(configuredUrls))
                {
                    web.UseSetting(WebHostDefaults.ServerUrlsKey, configuredUrls);
                }

                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddTransportTestAuthorization(groups);
                    configureServices?.Invoke(services);
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        configure(builder);
                    });
                    services.AddWorkableHttpApi(options =>
                    {
                        if (surfaceAccessGroups is not null)
                        {
                            options.SurfaceAccessGroups = surfaceAccessGroups.ToArray();
                        }
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    if (authenticated)
                    {
                        app.Use(async (context, next) =>
                        {
                            context.User = CreateTransportPrincipal(
                                id: "user-123",
                                name: "Greya",
                                email: "greya@example.test",
                                groups: groups);
                            await next();
                        });
                    }

                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapWorkableApi("/workable"));
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private sealed class TestSqlProfilingCapabilityContributor : IWorkSystemCapabilityContributor
    {
        public void ConfigureCapabilities(WorkSystemCapabilitiesBuilder capabilities)
        {
            ArgumentNullException.ThrowIfNull(capabilities);
            capabilities.SqlProfilingAvailable = true;
        }
    }

    private static async Task<IHost> CreateMultiSystemHttpHost(
        IEnumerable<string>? groups = null,
        Action<IServiceCollection>? configureServices = null,
        Action<IWorkSystemBuilder>? configureSystems = null,
        IEnumerable<string>? surfaceAccessGroups = null)
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
                        configureSystems?.Invoke(builder);
                        builder.AddAuthorizedTransportWork(
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
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        configureSystems?.Invoke(builder);
                        builder.AddAuthorizedTransportWork(
                            WorkDefinition.Create(
                                "http.named",
                                configuration: WorkConfiguration.Default with
                                {
                                    Start = WorkStartConfiguration.DoNotStart,
                                }),
                            SuccessfulWork);
                    });
                    configureServices?.Invoke(services);
                    services.AddWorkableHttpApi(options =>
                    {
                        if (surfaceAccessGroups is not null)
                        {
                            options.SurfaceAccessGroups = surfaceAccessGroups.ToArray();
                        }
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.Use(async (context, next) =>
                    {
                        context.User = CreateTransportPrincipal(
                            id: "user-123",
                            name: "Greya",
                            email: "greya@example.test",
                            groups: groups);
                        await next();
                    });
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapWorkableApi("/workable"));
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> CreateManualHttpHost(IEnumerable<string>? groups = null)
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
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        builder.AddAuthorizedTransportWork(
                            WorkDefinition.Create(
                                "http.lifecycle",
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
                    app.Use(async (context, next) =>
                    {
                        context.User = CreateTransportPrincipal(
                            id: "user-123",
                            name: "Greya",
                            email: "greya@example.test",
                            groups: groups);
                        await next();
                    });
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapWorkableApi("/workable"));
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> CreateShutdownHttpHost(IEnumerable<string>? groups = null)
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<HttpShutdownTracker>();
                    services.AddTransportTestAuthorization(groups);
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        builder.UseShutdownGracePeriod(TimeSpan.FromMilliseconds(20));
                        builder.AddAuthorizedTransportWork(
                            WorkDefinition.Create("http.shutdown.force"),
                            async (context, input, cancellationToken) =>
                            {
                                context.Services.GetRequiredService<HttpShutdownTracker>().Started.TrySetResult();
                                await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
                                return WorkExecutionResult.Success();
                            });
                    });
                    services.AddWorkableHttpApi();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.Use(async (context, next) =>
                    {
                        context.User = CreateTransportPrincipal(
                            id: "user-123",
                            name: "Greya",
                            email: "greya@example.test",
                            groups: groups);
                        await next();
                    });
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapWorkableApi("/workable"));
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> CreateDefinitionDiscoveryHttpHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddTransportTestAuthorization();
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        builder.AddAuthorizedTransportWork(
                            WorkDefinition.Create(
                                "http.discovery.allowed",
                                configuration: WorkConfiguration.Default),
                            SuccessfulWork);
                        builder.AddAuthorizedTransportWork(
                            WorkDefinition.Create(
                                "http.discovery.dotnet-only",
                                configuration: WorkConfiguration.Default with
                                {
                                    Invocation = WorkInvocationConfiguration.Allow(WorkInvocationChannel.InProcess),
                                }),
                            SuccessfulWork);
                    });
                    services.AddWorkableHttpApi();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.Use(async (context, next) =>
                    {
                        context.User = CreateTransportPrincipal(
                            id: "user-123",
                            name: "Greya",
                            email: "greya@example.test");
                        await next();
                    });
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapWorkableApi("/workable"));
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> CreateDefinitionCatalogHttpHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddTransportTestAuthorization();
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        builder.AddAuthorizedTransportWork(WorkDefinition.Create("billing.invoice.generate", category: "Billing:Invoices"), SuccessfulWork);
                        builder.AddAuthorizedTransportWork(WorkDefinition.Create("billing.payment.capture", category: "Billing:Payments"), SuccessfulWork);
                        builder.AddAuthorizedTransportWork(WorkDefinition.Create("operations.cleanup", category: "Operations"), SuccessfulWork);
                    });
                    services.AddWorkableHttpApi();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.Use(async (context, next) =>
                    {
                        context.User = CreateTransportPrincipal(
                            id: "user-123",
                            name: "Greya",
                            email: "greya@example.test");
                        await next();
                    });
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapWorkableApi("/workable"));
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> CreateOverviewHttpHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddTransportTestAuthorization();
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        builder.AddAuthorizedTransportWork(WorkDefinition.Create("http.overview.complete", category: "Http"), SuccessfulWork);
                        builder.AddAuthorizedTransportWork(
                            WorkDefinition.Create("http.overview.failed", category: "Http"),
                            (context, input, cancellationToken) => Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("http.failed", "Failed.")])));
                    });
                    services.AddWorkableHttpApi();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.Use(async (context, next) =>
                    {
                        context.User = CreateTransportPrincipal(
                            id: "user-123",
                            name: "Greya",
                            email: "greya@example.test");
                        await next();
                    });
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapWorkableApi("/workable"));
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> CreateAuthorizedHttpHost()
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
                        builder.RequireAuthorization();
                        builder.AddWork(
                            WorkDefinition.Create("allowed.authorization"),
                            SuccessfulWork,
                            configure: null,
                            authorize: authorize => authorize.RequireGroups(
                                readGroups: ["billing.read"],
                                operateGroups: ["billing.ops"]));
                        builder.AddWork(
                            WorkDefinition.Create("hidden.authorization"),
                            SuccessfulWork,
                            configure: null,
                            authorize: authorize => authorize.RequireGroups(
                                readGroups: ["hidden.read"],
                                operateGroups: ["hidden.ops"]));
                    });
                    services.AddWorkableHttpApi();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.Use(async (context, next) =>
                    {
                        context.User = new ClaimsPrincipal(new ClaimsIdentity(
                            [
                                new Claim(ClaimTypes.NameIdentifier, "auth-user-1"),
                                new Claim("groups", "billing.read"),
                                new Claim("groups", "billing.ops"),
                            ],
                            "Test"));
                        await next();
                    });
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapWorkableApi("/workable"));
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> CreateExplicitSchemeHttpHost()
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
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        builder.AddAuthorizedTransportWork(WorkDefinition.Create("http.scheme"), SuccessfulWork);
                    });
                    services.AddWorkableHttpApi();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapWorkableApi("/workable"));
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> CreateExplicitSchemeHttpHostWithFallbackPolicy()
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
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        builder.AddAuthorizedTransportWork(WorkDefinition.Create("http.scheme"), SuccessfulWork);
                    });
                    services.AddWorkableHttpApi();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapWorkableApi("/workable"));
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static async Task<IHost> CreateBulkActionHttpHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddTransportTestAuthorization();
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        builder.AddAuthorizedTransportWork(
                            WorkDefinition.Create(
                                "http.bulk.billing",
                                category: "Billing:Invoices",
                                configuration: WorkConfiguration.Default with
                                {
                                    Start = WorkStartConfiguration.DoNotStart,
                            }),
                            SuccessfulWork);
                        builder.AddAuthorizedTransportWork(
                            WorkDefinition.Create(
                                "http.bulk.email",
                                category: "Email",
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
                    app.Use(async (context, next) =>
                    {
                        context.User = CreateTransportPrincipal(
                            id: "user-123",
                            name: "Greya",
                            email: "greya@example.test");
                        await next();
                    });
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapWorkableApi("/workable"));
                });
            })
            .Build();

        await host.StartAsync();
        return host;
    }

    private static IWorkSystemSession Direct(IWorkSystem system)
        => CreateTransportSession(
            system,
            WorkInvocationChannel.HttpApi,
            description: "Use HTTP API test session.");

    private static WorkRequestContext DirectRequestContext(string description = "Use HTTP API test request context.")
        => CreateTransportRequestContext(
            WorkInvocationChannel.HttpApi,
            description);

    private static IWorkSystemSession CreateTransportSession(
        IWorkSystem system,
        WorkInvocationChannel channel,
        string description)
        => TransportAuthorizationTestSupport.CreateTransportSession(
            system,
            channel,
            description: description);

    private static WorkRequestContext CreateTransportRequestContext(
        WorkInvocationChannel channel,
        string description)
        => TransportAuthorizationTestSupport.CreateTransportRequestContext(
            channel,
            description: description);

    private static ClaimsPrincipal CreateTransportPrincipal(
        string id,
        string name,
        string email,
        IEnumerable<string>? groups = null)
        => TransportAuthorizationTestSupport.CreateTransportPrincipal(id, name, email, groups);

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private sealed class HttpShutdownTracker
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private static async Task<Guid> QueueAndReadWorkerId(HttpClient client, string path)
    {
        var response = await client.PostAsJsonAsync(
            path,
            new
            {
                completion = "returnAfterAccepted",
            });
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        return Guid.Parse(json["workerId"]?["value"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected worker id."));
    }

    private static async Task<JsonNode> GetJson(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
    }

    private static JsonElement RequiredData(WorkEvent workEvent)
        => workEvent.Data ?? throw new InvalidOperationException($"Expected data for event '{workEvent.EventType}'.");

    private static async Task<WorkEvent> ReadNext(IAsyncEnumerator<WorkEvent> reader)
    {
        var hasEvent = await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(hasEvent);
        return reader.Current;
    }

    private static async Task WaitForReadModel(IWorkSystem system)
    {
        var session = DiagnosticsSession(system);
        await TestEventually.Until(
            () => session.Diagnostics.ReadModel.PendingUpdateCount == 0,
            "Expected the HTTP API test read model projection to drain.");
    }

    private static IWorkSystemSession DiagnosticsSession(IWorkSystem system)
    {
        var actor = TransportAuthorizationTestSupport.CreateActor(
            id: "http-diagnostics-user-1",
            name: "HTTP Diagnostics User",
            email: "http.diagnostics@example.test");
        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.HttpApi,
            actor,
            "Wait for HTTP API test read model projection.") with
        {
            Authorization = WorkAuthorizationSnapshot.Create(
                actor,
                [InternalWorkAuthorizationGroups.SystemAdministrator],
                readableDefinitionIds: null),
        };
        return system.CreateSession(requestContext);
    }

    private sealed class HttpIterationDetailExecutor(ILogger<HttpIterationDetailExecutor> logger) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        {
            logger.LogInformation("HTTP iteration info log.");
            logger.LogWarning("HTTP iteration warning log.");
            logger.LogError("HTTP iteration error log.");

            return Task.FromResult(WorkExecutionResult.Success(messages:
            [
                new WorkMessage(
                    "http.iteration.warning",
                    WorkMessageSeverity.Warning,
                    "HTTP iteration warning.",
                    "messages.warning",
                    new Dictionary<string, object?> { ["slot"] = 2 })
                {
                    OccurredAt = DateTimeOffset.Parse("2026-05-29T11:00:02Z"),
                },
                new WorkMessage(
                    "http.iteration.information",
                    WorkMessageSeverity.Information,
                    "HTTP iteration information.",
                    "messages.information",
                    new Dictionary<string, object?> { ["slot"] = 1 })
                {
                    OccurredAt = DateTimeOffset.Parse("2026-05-29T11:00:01Z"),
                },
            ]));
        }
    }

    private sealed record WorkflowHttpInput(string ExternalKey);

    private sealed class CountingWorkAuthorizationGroupProvider(IEnumerable<string> groups) : IWorkAuthorizationGroupProvider
    {
        private const string DefaultSystemCacheKey = "<default>";
        private readonly IReadOnlySet<string> groups = new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, int> callCounts = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlySet<string> GetGroups(WorkActor actor, string? systemName)
        {
            this.callCounts.AddOrUpdate(GetCacheKey(systemName), 1, static (_, count) => count + 1);
            return actor == WorkActor.Unknown
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : this.groups;
        }

        public int GetCallCount(string? systemName)
            => this.callCounts.TryGetValue(GetCacheKey(systemName), out var count) ? count : 0;

        private static string GetCacheKey(string? systemName)
            => string.IsNullOrWhiteSpace(systemName) ? DefaultSystemCacheKey : systemName;
    }
}
