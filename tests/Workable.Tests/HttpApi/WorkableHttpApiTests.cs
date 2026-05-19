using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;
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
    private static readonly string[] CompletedFailedStates = ["Completed", "Failed"];
    private static readonly string[] CompletedStatuses = ["Completed"];

    [Fact]
    public async Task HttpApiReturnsAfterAcceptedByDefault()
    {
        var (system, http) = CreateHost(WorkDefinition.Create("http.default"), (context, input, cancellationToken) =>
            Task.FromResult(WorkExecutionResult.Success(input is null ? WorkOutput.Empty : WorkOutput.FromData(input))));
        await system.Start();

        using var input = JsonDocument.Parse("""{"id":"123"}""");
        var result = await http.Queue.Queue(system, "http.default", new WorkableHttpWorkRequest(input.RootElement));

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
        var result = await http.Queue.Queue(system,
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
        var result = await http.Queue.Queue(system,
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
        var result = await http.Queue.Queue(system,
            "http.metadata",
            new WorkableHttpWorkRequest(
                input.RootElement,
                Options: new WorkableHttpWorkerOptions(ProfilingEnabled: true),
                SubjectId: new WorkSubjectId("user", "123"),
                ConcurrencyKey: new WorkConcurrencyKey("tenant", "abc"),
                Identifiers: new HashSet<WorkIdentifier> { new("invoice", "456") }));
        var worker = await system.Query.Worker(result.WorkerId ?? throw new InvalidOperationException("Expected worker id."));

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

        var result = await http.Queue.Queue(system, "dotnet.only");

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

        var definitions = http.Catalog.GetDefinitions(system);
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
                nameof(WorkableHttpWorkConfiguration.Idempotency),
                nameof(WorkableHttpWorkConfiguration.Recurrence),
                nameof(WorkableHttpWorkConfiguration.TransientRetry),
                nameof(WorkableHttpWorkConfiguration.Logging),
                nameof(WorkableHttpWorkConfiguration.Retention),
                nameof(WorkableHttpWorkConfiguration.Concurrency),
                nameof(WorkableHttpWorkConfiguration.QueueDurability),
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

        var result = await http.Queue.Queue(system,
            "http.invocation.dto",
            new WorkableHttpWorkRequest(
                Options: new WorkableHttpWorkerOptions(
                    Configuration: WorkableHttpWorkConfiguration.From(WorkConfiguration.Default with
                    {
                        Start = WorkStartConfiguration.DoNotStart,
                    }))));
        var worker = await system.Query.Worker(result.WorkerId ?? throw new InvalidOperationException("Expected worker id."));

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

        var result = await http.Queue.Queue(system,
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
        var worker = await http.Query.Worker(system, workerId);
        var workers = await http.Query.Workers(system, new WorkerCriteria(Identifier: new WorkIdentifier("batch", "1")));
        var byName = await http.Query.WorkInfo(system, "http.query.one");
        var byId = await http.Query.WorkInfo(system, byName?.Definition.Id ?? throw new InvalidOperationException("Expected work info."));
        var definitions = await http.Query.WorkDefinitions(system, new WorkDefinitionCriteria(Category: "Http"));
        var summary = await http.Query.WorkerStatusSummary(system, new WorkerCriteria(DefinitionName: "http.query.one"));
        var systemSummary = await http.Query.WorkerStatusSummary(system);

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
        await (await system.Queue.Enqueue(
            "http.overview.complete",
            WorkInput.Empty.WithIdentifier(new WorkIdentifier("case", "complete")))).WaitForCompletion();
        await (await system.Queue.Enqueue(
            "http.overview.failed",
            WorkInput.Empty.WithIdentifier(new WorkIdentifier("case", "failed")))).WaitForCompletion();
        await WaitForThroughputBucketToClose();

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
        await (await system.Queue.Enqueue("http.overview.complete")).WaitForCompletion();
        await (await system.Queue.Enqueue("http.overview.failed")).WaitForCompletion();
        await (await system.Queue.Enqueue(
            "http.overview.complete",
            options: new WorkerOptions(ProfilingEnabled: true))).WaitForCompletion();

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

        var handle = await system.Queue.Enqueue(
            "http.route.case",
            WorkInput.Empty.WithIdentifier(new WorkIdentifier("batch", "iteration")),
            options: new WorkerOptions(Configuration: WorkConfiguration.Default));
        await handle.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));
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
        Assert.NotNull(iteration?["workerState"]);
        Assert.NotNull(iteration?["identifiers"]);
    }

    [Fact]
    public async Task MappedHttpRouteCanSearchWorkerAndIterationKeysAndTypes()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;

        var keyedWorker = await system.Queue.Enqueue(
            "http.route.case",
            WorkInput.Empty
                .WithSubject(new WorkSubjectId("claim", "CLM-123"))
                .WithConcurrencyKey(new WorkConcurrencyKey("tenant", "west"))
                .WithIdentifier(new WorkIdentifier("invoice", "INV-456")),
            options: new WorkerOptions(Configuration: WorkConfiguration.Default));
        await keyedWorker.WaitForCompletion().WaitAsync(TimeSpan.FromSeconds(5));

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
            type["workerCountByKind"]?["Subject"]?.GetValue<int>() == 1 &&
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
            type["iterationCountByKind"]?["Subject"]?.GetValue<int>() == 1 &&
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

        var queue = await http.Queue.Queue(system, "http.action");
        var worker = await http.Query.Worker(system, queue.WorkerId ?? throw new InvalidOperationException("Expected worker id."));
        var outcome = await http.Workers.Execute(system,
            worker!.Id,
            WorkAction.Cancel,
            new WorkableHttpWorkerActionRequest(worker.Revision));
        var canceled = await http.Query.Worker(system, worker.Id);

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

        var queue = await http.Queue.Queue(system, "http.reconfigure");
        var worker = await http.Query.Worker(system, queue.WorkerId ?? throw new InvalidOperationException("Expected worker id."));
        var outcome = await http.Workers.Reconfigure(system,
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

        var outcome = await http.Catalog.ReconfigureDefinition(system,
            definition.Id,
            new WorkableHttpDefinitionReconfigurationRequest(
                definition.Revision,
                new WorkDefinitionReconfiguration(
                    DefaultOptions: new WorkerOptions(ProfilingEnabled: true))));

        var handle = await system.Queue.Enqueue(definition.Id);
        var worker = await system.Query.Worker(handle.WorkerId ?? throw new InvalidOperationException("Expected worker id."));

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
        var definition = system.Catalog.Definitions.Single(definition => definition.Name == "http.route.case");

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
        Assert.True(system.Catalog.TryGet(definition.Id, out var updated));
        Assert.True(updated.DefaultOptions.ProfilingEnabled);
    }

    [Fact]
    public async Task MappedHttpRouteReturnsConflictForStaleDefinitionRevision()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        var definition = system.Catalog.Definitions.Single(definition => definition.Name == "http.route.case");

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
        var worker = await system.Query.Worker(new WorkerId(workerId))
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

        var canceled = await system.Query.Worker(new WorkerId(workerId));
        var actionEvent = await ReadNext(actionReader);
        Assert.Equal(WorkerState.Canceled, canceled?.State);
        Assert.Equal(WorkInvocationChannel.HttpApi, actionEvent.Origin?.Channel);
        Assert.Equal("user-123", actionEvent.Origin?.Actor.Id);
        Assert.Contains("/WORKABLE/WORKERS/", actionEvent.Origin?.Url, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, summaryJson["total"]?.GetValue<int>());
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
        var idempotency = json["tabs"]?.AsArray().FirstOrDefault(tab => tab?["id"]?.GetValue<string>() == "idempotency")
            ?? throw new InvalidOperationException("Expected idempotency tab.");
        var fields = idempotency["fields"]?.AsArray()
            ?? throw new InvalidOperationException("Expected idempotency fields.");

        Assert.Contains(fields, field => field?["path"]?.GetValue<string>() == "subjectId.type");
        Assert.Contains(fields, field => field?["path"]?.GetValue<string>() == "subjectId.value");
        Assert.DoesNotContain("invocation", json.ToJsonString(), StringComparison.OrdinalIgnoreCase);
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

        var worker = await system.Query.Worker(new WorkerId(workerId))
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

        Assert.Contains(channels, channel => channel?.GetValue<string>() == "DotNet");
        Assert.DoesNotContain(channels, channel => channel?.GetValue<string>() == "HttpApi");
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
    }

    [Fact]
    public async Task MappedHttpWorkInfoCanBeReadByWorkNameOrId()
    {
        using var host = await CreateHttpHost();
        var client = host.GetTestClient();
        var system = host.Services.GetRequiredService<IWorkSystemRegistry>().Default;
        Assert.True(system.Catalog.TryGet("http.route.case", out var definition));

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

        var worker = await system.Query.Worker(new WorkerId(workerId))
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
        var worker = await system.Query.Worker(new WorkerId(workerId))
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

        var worker = await background.Query.Worker(new WorkerId(workerId))
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

        var response = await client.GetAsync("/workable/systems");
        response.EnsureSuccessStatusCode();
        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");
        var systems = json["systems"]?.AsArray()
            ?? throw new InvalidOperationException("Expected systems array.");

        Assert.Equal(2, systems.Count);
        Assert.Contains(systems, system =>
            system?["name"] is null &&
            system?["isDefault"]?.GetValue<bool>() == true &&
            system?["capabilities"]?["realtime"]?["enabled"]?.GetValue<bool>() == false);
        Assert.Contains(systems, system =>
            system?["name"]?.GetValue<string>() == "background" &&
            system?["isDefault"]?.GetValue<bool>() == false &&
            system?["capabilities"]?["realtime"]?["enabled"]?.GetValue<bool>() == false);
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
        await system.Query.Workers();

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
        Assert.Empty(stopJson["forceCanceledWorkers"]?.AsArray()
            ?? throw new InvalidOperationException("Expected force-canceled worker array."));
    }

    [Fact]
    public async Task MappedHttpLifecycleStopReturnsForceCanceledWorkerNames()
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
        var names = stopJson["forceCanceledWorkerNames"]?.AsArray()
            ?? throw new InvalidOperationException("Expected force-canceled worker names.");
        var summaries = stopJson["forceCanceledWorkerSummaries"]?.AsArray()
            ?? throw new InvalidOperationException("Expected force-canceled worker summaries.");

        var name = Assert.Single(names)
            ?? throw new InvalidOperationException("Expected force-canceled worker name.");
        Assert.Equal("http.shutdown.force", name.GetValue<string>());
        var summary = Assert.Single(summaries)
            ?? throw new InvalidOperationException("Expected force-canceled worker summary.");
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

        var actionResponse = await client.PostAsJsonAsync(
            "/workable/workers/actions/cancel",
            new
            {
                category = "Billing",
            });
        actionResponse.EnsureSuccessStatusCode();
        var actionJson = JsonNode.Parse(await actionResponse.Content.ReadAsStringAsync())
            ?? throw new InvalidOperationException("Expected JSON response.");

        var billing = await system.Query.Worker(new WorkerId(billingQueue))
            ?? throw new InvalidOperationException("Expected billing worker.");
        var email = await system.Query.Worker(new WorkerId(emailQueue))
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

        var worker = await background.Query.Worker(new WorkerId(workerId))
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
        worker = await background.Query.Worker(new WorkerId(workerId))
            ?? throw new InvalidOperationException("Expected worker.");
        Assert.True(worker.Options.ProfilingEnabled);

        var actionResponse = await client.PostAsJsonAsync(
            $"/workable/systems/background/workers/{workerId:D}/actions/cancel",
            new
            {
                revision = worker.Revision,
            });
        actionResponse.EnsureSuccessStatusCode();

        var canceled = await background.Query.Worker(new WorkerId(workerId))
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

    private static (IWorkSystem System, HttpAdapterServices Http) CreateHost(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute)
        => CreateHost(builder => builder.AddWork(definition, execute));

    private static (IWorkSystem System, HttpAdapterServices Http) CreateHost(
        Action<IWorkSystemBuilder> configure)
    {
        var provider = new ServiceCollection()
            .AddWorkableSystem(configure)
            .AddWorkableHttpApi()
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        return (system, new HttpAdapterServices(
            provider.GetRequiredService<WorkableHttpSystemResolver>(),
            provider.GetRequiredService<WorkableHttpCatalogAdapter>(),
            provider.GetRequiredService<WorkableHttpQueueAdapter>(),
            provider.GetRequiredService<WorkableHttpQueryAdapter>(),
            provider.GetRequiredService<WorkableHttpWorkerAdapter>()));
    }

    private sealed record HttpAdapterServices(
        WorkableHttpSystemResolver Systems,
        WorkableHttpCatalogAdapter Catalog,
        WorkableHttpQueueAdapter Queue,
        WorkableHttpQueryAdapter Query,
        WorkableHttpWorkerAdapter Workers);

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

    private static async Task<IHost> CreateManualHttpHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddWorkableSystem(builder => builder.AddWork(
                        WorkDefinition.Create(
                            "http.lifecycle",
                            configuration: WorkConfiguration.Default with
                            {
                                Start = WorkStartConfiguration.DoNotStart,
                            }),
                        SuccessfulWork));
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

    private static async Task<IHost> CreateShutdownHttpHost()
    {
        var host = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<HttpShutdownTracker>();
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.UseShutdownGracePeriod(TimeSpan.FromMilliseconds(20));
                        builder.AddWork(
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
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.AddWork(
                            WorkDefinition.Create(
                                "http.discovery.allowed",
                                configuration: WorkConfiguration.Default),
                            SuccessfulWork);
                        builder.AddWork(
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
                    services.AddWorkableSystem(builder =>
                    {
                        builder.StartWithHost();
                        builder.AddWork(WorkDefinition.Create("billing.invoice.generate", category: "Billing:Invoices"), SuccessfulWork);
                        builder.AddWork(WorkDefinition.Create("billing.payment.capture", category: "Billing:Payments"), SuccessfulWork);
                        builder.AddWork(WorkDefinition.Create("operations.cleanup", category: "Operations"), SuccessfulWork);
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

    private static async Task<IHost> CreateOverviewHttpHost()
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
                        builder.AddWork(WorkDefinition.Create("http.overview.complete", category: "Http"), SuccessfulWork);
                        builder.AddWork(
                            WorkDefinition.Create("http.overview.failed", category: "Http"),
                            (context, input, cancellationToken) => Task.FromResult(WorkExecutionResult.Failure([WorkMessage.Error("http.failed", "Failed.")])));
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

    private static async Task<IHost> CreateBulkActionHttpHost()
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
                                "http.bulk.billing",
                                category: "Billing:Invoices",
                                configuration: WorkConfiguration.Default with
                                {
                                    Start = WorkStartConfiguration.DoNotStart,
                                }),
                            SuccessfulWork);
                        builder.AddWork(
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
}
