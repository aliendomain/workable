using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
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
        var result = await http.Queue.Enqueue(Direct(system), "http.default", new WorkableHttpWorkRequest(input.RootElement));

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
        var result = await http.Queue.Enqueue(Direct(system),
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
        var result = await http.Queue.Enqueue(Direct(system),
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
        var result = await http.Queue.Enqueue(Direct(system),
            "http.metadata",
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

        var result = await http.Queue.Enqueue(Direct(system), "dotnet.only");

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
                Invocation = WorkInvocationConfiguration.Allow(WorkInvocationChannel.DotNet),
            });
        var (system, http) = CreateHost(definition, SuccessfulWork);
        await system.Start();

        var definitions = http.Catalog.GetDefinitions(Direct(system));
        var listed = Assert.Single(definitions);

        Assert.Equal("dotnet.visible", listed.Name);
        Assert.Contains(WorkInvocationChannel.DotNet, listed.Configuration.Invocation.AllowedChannels);
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

        var result = await http.Queue.Enqueue(Direct(system),
            "http.invocation.dto",
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
    public async Task HttpApiCanReturnAfterAccepted()
    {
        var definition = WorkDefinition.Create(
            "manual.http",
            configuration: WorkConfiguration.Default with { Start = WorkStartConfiguration.DoNotStart });
        var (system, http) = CreateHost(definition, SuccessfulWork);
        await system.Start();

        var result = await http.Queue.Enqueue(Direct(system),
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

        var handle = await Direct(system).Queue.Enqueue("http.query.one", WorkInput.Empty.WithIdentifier(new WorkIdentifier("batch", "1")));
        var completion = await handle.WaitForCompletion();
        Assert.True(completion.IsCompletedSuccessfully);
        await WaitForReadModel(system);

        var workerId = handle.WorkerId ?? throw new InvalidOperationException("Expected worker id.");
        var worker = await http.Query.Worker(Direct(system), workerId);
        var workers = await http.Query.Workers(Direct(system), new WorkerCriteria(Identifier: new WorkIdentifier("batch", "1")));
        var byName = await http.Query.WorkInfo(Direct(system), "http.query.one");
        var byId = await http.Query.WorkInfo(Direct(system), byName?.Definition.Id ?? throw new InvalidOperationException("Expected work info."));
        var definitions = await http.Query.WorkDefinitions(Direct(system), new WorkDefinitionCriteria(Category: "Http"));
        var summary = await http.Query.WorkerStatusSummary(Direct(system), new WorkerCriteria(DefinitionName: "http.query.one"));
        var systemSummary = await http.Query.WorkerStatusSummary(Direct(system));

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
        await WaitForThroughputBucketToClose();
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
        Assert.Contains(workers, worker => worker?["state"]?.GetValue<string>() == "Completed" && worker?["isFinal"]?.GetValue<bool>() == true);
        Assert.Contains(workers, worker => worker?["state"]?.GetValue<string>() == "Failed" && worker?["isFinal"]?.GetValue<bool>() == false);
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
            $"/workable/workers/{workerId:D}/iterations/1/messages?take=1&sort=Asc&severities=Information,Warning");
        var firstCursor = firstPage["page"]?["cursor"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected message page cursor.");
        var secondPage = await GetJson(
            client,
            $"/workable/workers/{workerId:D}/iterations/1/messages?take=1&sort=Asc&severities=Information,Warning&cursor={Uri.EscapeDataString(firstCursor)}");

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
    public async Task MappedHttpRouteCanReadIterationDetailSnapshot()
    {
        using var host = await CreateHttpHost(builder =>
        {
            builder.AddWork<HttpIterationDetailExecutor>(
                WorkDefinition.Create("http.route.iteration.detail"),
                configuration => configuration.ConfigureLogging(level: LogLevel.Information),
                authorize => authorize.RequireGroups(
                    TransportAuthorizationTestSupport.ReadGroups,
                    TransportAuthorizationTestSupport.OperateGroups));
        });
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var session = TransportAuthorizationTestSupport.CreateTransportSession(system, WorkInvocationChannel.HttpApi);

        var handle = await session.Queue.Enqueue(
            "http.route.iteration.detail",
            WorkInput.FromValue(new { attempt = 7 })
                .WithSubject(new WorkSubjectId("claim", "CLM-123"))
                .WithConcurrencyKey(new WorkConcurrencyKey("tenant", "west"))
                .WithIdentifier(new WorkIdentifier("invoice", "INV-456")));
        await handle.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForReadModel(system);
        var workerId = handle.WorkerId?.Value
            ?? throw new InvalidOperationException("Expected worker id.");
        var snapshot = await Direct(system).Query.WorkerIteration(new WorkerIterationReference(new WorkerId(workerId), 1))
            ?? throw new InvalidOperationException("Expected iteration snapshot.");

        Assert.Equal(2, snapshot.Messages.Count);
        Assert.Equal(3, snapshot.Logs.Count);
        var json = await GetJson(client, $"/workable/workers/{workerId:D}/iterations/1/detail");

        Assert.Equal(workerId.ToString("D"), json["workerId"]?["value"]?.GetValue<string>());
        Assert.Equal("http.route.iteration.detail", json["definitionName"]?.GetValue<string>());
        Assert.Equal("claim", json["subjectId"]?["type"]?.GetValue<string>());
        Assert.Equal("CLM-123", json["subjectId"]?["value"]?.GetValue<string>());
        Assert.Equal("tenant", json["concurrencyKey"]?["type"]?.GetValue<string>());
        Assert.Equal("west", json["concurrencyKey"]?["value"]?.GetValue<string>());
        Assert.Contains(json["identifiers"]?.AsArray() ?? [], identifier => identifier?["value"]?.GetValue<string>() == "INV-456");
        Assert.Contains("\"attempt\":7", json["input"]?["json"]?.GetValue<string>() ?? string.Empty, StringComparison.Ordinal);
        Assert.Equal(1, json["iteration"]?["sequence"]?.GetValue<int>());
        Assert.Equal(1, json["iteration"]?["attemptCount"]?.GetValue<int>());
        Assert.Equal("Completed", json["iteration"]?["status"]?.GetValue<string>());
        Assert.Equal(2, json["messageSummary"]?["total"]?.GetValue<int>());
        Assert.Equal(1, json["messageSummary"]?["warning"]?.GetValue<int>());
        Assert.Equal(1, json["messageSummary"]?["information"]?.GetValue<int>());
        Assert.Equal(3, json["logs"]?["summary"]?["total"]?.GetValue<int>());
        Assert.Equal(1, json["logs"]?["summary"]?["information"]?.GetValue<int>());
        Assert.Equal(1, json["logs"]?["summary"]?["warning"]?.GetValue<int>());
        Assert.Equal(1, json["logs"]?["summary"]?["error"]?.GetValue<int>());
        Assert.Equal("HTTP iteration error log.", json["logs"]?["page"]?["items"]?[0]?["message"]?.GetValue<string>());
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
            $"/workable/workers/{workerId:D}/iterations/1/logs?take=1&sort=Asc&logLevels=Warning,Error");
        var firstCursor = firstPage["page"]?["cursor"]?.GetValue<string>()
            ?? throw new InvalidOperationException("Expected log page cursor.");
        var secondPage = await GetJson(
            client,
            $"/workable/workers/{workerId:D}/iterations/1/logs?take=1&sort=Asc&logLevels=Warning,Error&cursor={Uri.EscapeDataString(firstCursor)}");

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

        var queue = await http.Queue.Enqueue(session, "http.action");
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

        var queue = await http.Queue.Enqueue(session, "http.reconfigure");
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
            definition.Id,
            new WorkableHttpDefinitionReconfigurationRequest(
                definition.Revision,
                new WorkDefinitionReconfiguration(
                    DefaultOptions: new WorkerOptions(ProfilingEnabled: true))));

        var handle = await Direct(system).Queue.Enqueue(definition.Id);
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
            $"/workable/definitions/{definition.Id.Value:D}/reconfigure",
            new WorkableHttpDefinitionReconfigurationRequest(
                definition.Revision,
                new WorkDefinitionReconfiguration(
                    DefaultOptions: new WorkerOptions(ProfilingEnabled: true))));
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");

        Assert.Equal("Accepted", json["status"]?.GetValue<string>());
        Assert.Equal(1, json["definition"]?["revision"]?.GetValue<int>());
        Assert.True(Direct(system).Catalog.TryGet(definition.Id, out var updated));
        Assert.True(updated.DefaultOptions.ProfilingEnabled);
    }

    [Fact]
    public async Task MappedHttpRouteReturnsConflictForStaleDefinitionRevision()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var definition = Direct(system).Catalog.Definitions.Single(definition => definition.Name == "http.route.case");

        var first = await client.PostAsJsonAsync(
            $"/workable/definitions/{definition.Id.Value:D}/reconfigure",
            new WorkableHttpDefinitionReconfigurationRequest(
                definition.Revision,
                new WorkDefinitionReconfiguration(DefaultOptions: new WorkerOptions(ProfilingEnabled: true))));
        var second = await client.PostAsJsonAsync(
            $"/workable/definitions/{definition.Id.Value:D}/reconfigure",
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
        await using var queuedSubscription = Direct(system).Events.Subscribe(new WorkEventFilter(DefinitionId: definition.Id, EventType: "worker.queued"));
        await using var queuedReader = queuedSubscription.Read().GetAsyncEnumerator();

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
        var session = Direct(system);
        var worker = await session.Query.Worker(new WorkerId(workerId))
            ?? throw new InvalidOperationException("Expected worker.");
        await using var actionSubscription = session.Events.Subscribe(new WorkEventFilter(WorkerId: worker.Id, EventType: "worker.cancel"));
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

        var queuedEvent = await ReadNext(queuedReader);
        var canceled = await session.Query.Worker(new WorkerId(workerId));
        var actionEvent = await ReadNext(actionReader);
        var queuedOrigin = RequiredData(queuedEvent).GetProperty("origin");
        var actionOrigin = RequiredData(actionEvent).GetProperty("origin");
        Assert.Equal(WorkerState.Canceled, canceled?.State);
        Assert.Equal(worker.DefinitionName, actionEvent.WorkDefinitionName);
        Assert.Equal(1, summaryJson["total"]?.GetValue<int>());
        Assert.Equal("HttpApi", queuedOrigin.GetProperty("channel").GetString());
        Assert.Equal("user-123", queuedOrigin.GetProperty("actor").GetProperty("id").GetString());
        Assert.Equal("greya@example.test", queuedOrigin.GetProperty("actor").GetProperty("email").GetString());
        Assert.Contains("/WORKABLE/WORK/http.route.case", queuedOrigin.GetProperty("url").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("HttpApi", actionOrigin.GetProperty("channel").GetString());
        Assert.Equal("user-123", actionOrigin.GetProperty("actor").GetProperty("id").GetString());
        Assert.Equal("greya@example.test", actionOrigin.GetProperty("actor").GetProperty("email").GetString());
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
    public async Task MappedHttpQueueByDefinitionIdRecordsHttpOrigin()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        Assert.True(Direct(system).Catalog.TryGet("http.route.case", out var definition));

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

        var worker = await Direct(system).Query.Worker(new WorkerId(workerId))
            ?? throw new InvalidOperationException("Expected worker.");

        Assert.Equal(WorkInvocationChannel.HttpApi, worker.Origin.Channel);
        Assert.Equal("user-123", worker.Origin.Actor.Id);
        Assert.Equal("greya@example.test", worker.Origin.Actor.Email);
        Assert.Contains($"/workable/definitions/{definition.Id.Value:D}/queue", worker.Origin.Url, StringComparison.OrdinalIgnoreCase);
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

        Assert.Contains(channels, channel => channel?.GetValue<string>() == "DotNet");
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

        var json = await GetJson(client, $"/workable/definitions/{definition.Id.Value:D}");

        Assert.Equal("billing.invoice.generate", json["name"]?.GetValue<string>());
        Assert.NotNull(json["configuration"]);
        Assert.NotNull(json["defaultOptions"]);
    }

    [Fact]
    public async Task MappedHttpWorkInfoCanBeReadByWorkNameOrId()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        Assert.True(Direct(system).Catalog.TryGet("http.route.case", out var definition));

        var byName = await client.GetAsync("/workable/work/http.route.case/info");
        var byId = await client.GetAsync($"/workable/work/id/{definition.Id.Value:D}/info");

        byName.EnsureSuccessStatusCode();
        byId.EnsureSuccessStatusCode();
        var byNameJson = JsonNode.Parse(await byName.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var byIdJson = JsonNode.Parse(await byId.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");

        Assert.Equal("http.route.case", byNameJson["definition"]?["name"]?.GetValue<string>());
        Assert.Equal("http.route.case", byIdJson["definition"]?["name"]?.GetValue<string>());
        Assert.NotNull(byNameJson["queueRequestSchema"]?["schema"]?["jsonSchema"]);
        Assert.NotNull(byIdJson["queueRequestSchema"]?["tabs"]);
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
        Assert.True(system?["access"]?["canConnect"]?.GetValue<bool>() == true);
    }

    [Fact]
    public async Task DebugRealtimeEndpointIsAvailableWithoutAuthenticationForLocalRequests()
    {
        using var host = await CreateHttpHost(authenticated: false, development: true);
        var client = host.GetTestClient();

        var body = await GetJson(client, "/workable/debug/realtime");

        Assert.NotNull(body["systemId"]);
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
        Assert.Equal("user-123", origin.GetProperty("actor").GetProperty("id").GetString());
        Assert.Equal("greya@example.test", origin.GetProperty("actor").GetProperty("email").GetString());
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
        Assert.Contains("/workable/systems/background/work/http.named", worker.Origin.Url, StringComparison.OrdinalIgnoreCase);
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
            candidate["access"]?["canConnect"] is JsonValue canConnect &&
            canConnect.GetValue<bool>() &&
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
            !persistentCoordinationAvailable.GetValue<bool>());
    }

    [Fact]
    public async Task MappedHttpRouteFiltersSystemsWithoutConnectPermission()
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
    public async Task MappedHttpRouteIncludesConnectOnlyAccessSummary()
    {
        using var host = await CreateMultiSystemHttpHost(TransportAuthorizationTestSupport.ConnectGroups);
        var client = host.GetTestClient();

        var response = await client.GetAsync("/workable/host");
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var systems = json["systems"]?.AsArray()
            ?? throw new InvalidOperationException("Expected systems array.");

        Assert.Equal(2, systems.Count);
        Assert.All(systems, system =>
        {
            var access = system?["access"]?.AsObject()
                ?? throw new InvalidOperationException("Expected access object.");

            Assert.True(access["canConnect"]?.GetValue<bool>());
            Assert.False(access["isSystemAdministrator"]?.GetValue<bool>());
            Assert.False(access["isWorkAdministrator"]?.GetValue<bool>());
            Assert.False(access["canViewDiagnostics"]?.GetValue<bool>());
            Assert.False(access["canControlSystem"]?.GetValue<bool>());
            Assert.False(access["canReadAllWork"]?.GetValue<bool>());
            Assert.False(access["canOperateAllWork"]?.GetValue<bool>());
            Assert.Equal(1, access["totalDefinitionCount"]?.GetValue<int>());
            Assert.Equal(0, access["readableDefinitionCount"]?.GetValue<int>());
            Assert.Equal(0, access["operableDefinitionCount"]?.GetValue<int>());
        });
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

        Assert.Equal(system.Id.Value.ToString(), json["id"]?["value"]?.GetValue<string>());
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
        using var host = await CreateHttpHost(groups: TransportAuthorizationTestSupport.ReadGroups);
        var client = host.GetTestClient();

        var response = await client.GetAsync("/workable/diagnostics");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("diagnostics", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappedHttpDiagnosticsViewRouteRequiresDiagnosticsPermission()
    {
        using var host = await CreateHttpHost(groups: TransportAuthorizationTestSupport.ReadGroups);
        var client = host.GetTestClient();

        var response = await client.PostAsJsonAsync("/workable/views/diagnostics", new { });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("diagnostics", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MappedHttpDiagnosticsComponentRouteRequiresDiagnosticsPermission()
    {
        using var host = await CreateHttpHost(groups: TransportAuthorizationTestSupport.ReadGroups);
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
        using var host = await CreateManualHttpHost(TransportAuthorizationTestSupport.ReadGroups);
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
        Assert.Contains("/workable/workers/actions/cancel", billing.ActionHistory[^1].Origin.Url, StringComparison.OrdinalIgnoreCase);
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
        Assert.Contains("/workable/systems/background/workers/", canceled.ActionHistory[^1].Origin.Url, StringComparison.OrdinalIgnoreCase);
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
    public async Task MappedHttpRouteUsesRequestContextAuthorizationForDefinitionsAndQueue()
    {
        using var host = await CreateAuthorizedHttpHost();
        var client = host.GetTestClient();
        Assert.True(host.Services.GetRequiredService<IWorkSystemRegistry>().Default.RequiresAuthorization);

        var definitionsResponse = await client.GetAsync("/workable/definitions");
        definitionsResponse.EnsureSuccessStatusCode();
        var definitionsJson = await definitionsResponse.Content.ReadAsStringAsync();

        Assert.Contains("allowed.authorization", definitionsJson);
        Assert.DoesNotContain("hidden.authorization", definitionsJson);

        var allowedResponse = await client.PostAsJsonAsync(
            "/workable/work/allowed.authorization",
            new
            {
                completion = "returnAfterAccepted",
            });
        allowedResponse.EnsureSuccessStatusCode();

        var hiddenResponse = await client.PostAsJsonAsync(
            "/workable/work/hidden.authorization",
            new
            {
                completion = "returnAfterAccepted",
            });

        Assert.Equal(HttpStatusCode.Forbidden, hiddenResponse.StatusCode);
        var hiddenJson = await hiddenResponse.Content.ReadAsStringAsync();
        Assert.Contains("Unauthorized", hiddenJson, StringComparison.OrdinalIgnoreCase);
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
            provider.GetRequiredService<WorkableHttpWorkerAdapter>()));
    }

    private sealed record HttpAdapterServices(
        WorkableHttpTopologyResolver Topology,
        WorkableHttpCatalogAdapter Catalog,
        WorkableHttpQueueAdapter Queue,
        WorkableHttpQueryAdapter Query,
        WorkableHttpWorkerAdapter Workers);

    private static Task<IHost> CreateHttpHost(
        bool authenticated = true,
        IEnumerable<string>? groups = null,
        bool development = false,
        string? configuredUrls = null)
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
            configuredUrls);

    private static async Task<IHost> CreateHttpHost(
        Action<IWorkSystemBuilder> configure,
        bool authenticated = true,
        IEnumerable<string>? groups = null,
        bool development = false,
        string? configuredUrls = null)
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
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.RequireAuthorization();
                        builder.ConfigureTransportSystemAuthorization();
                        configure(builder);
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

    private static async Task<IHost> CreateMultiSystemHttpHost(IEnumerable<string>? groups = null)
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
                        builder.AddAuthorizedTransportWork(
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
                                    Invocation = WorkInvocationConfiguration.Allow(WorkInvocationChannel.DotNet),
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

    private static IWorkSystemSession CreateTransportSession(
        IWorkSystem system,
        WorkInvocationChannel channel,
        string description)
        => TransportAuthorizationTestSupport.CreateTransportSession(
            system,
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

    private static async Task WaitForThroughputBucketToClose()
    {
        var currentSecond = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        while (DateTimeOffset.UtcNow.ToUnixTimeSeconds() <= currentSecond)
        {
            await Task.Delay(10);
        }
    }

    private static async Task WaitForReadModel(IWorkSystem system)
    {
        var session = DiagnosticsSession(system);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(5);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (session.Diagnostics.ReadModel.PendingUpdateCount == 0)
            {
                return;
            }

            await Task.Delay(10);
        }

        Assert.Equal(0, session.Diagnostics.ReadModel.PendingUpdateCount);
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
}
