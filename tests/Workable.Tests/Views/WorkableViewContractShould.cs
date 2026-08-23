using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using System.Reflection;
using System.Text.Json;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Views")]
public sealed class WorkableViewContractShould
{
    [Fact]
    public void NormalizeEveryDocumentedNamedViewToItsDefaultComponents()
    {
        var adapter = new WorkableViewQueryAdapter();

        AssertComponents(adapter, "overview", "system", "workers", "failedWorkers", "iterations", "failedIterations", "completedIterations");
        AssertComponents(adapter, "workers", "workerGrid");
        AssertComponents(adapter, "iterations", "iterationGrid");
        AssertComponents(adapter, "worker", "workerDetail", "workerCurrentIteration");
        AssertComponents(adapter, "diagnostics", "queueDiagnostics", "readModelDiagnostics", "retentionDiagnostics", "concurrencyDiagnostics", "durabilityDiagnostics", "idempotencyDiagnostics");
        AssertComponents(adapter, "workflow-runs", "workflowRuns");
        AssertComponents(adapter, "workflow-run", "workflowRun");
    }

    [Fact]
    public void NormalizeComponentShapesToTheSupportedWireContract()
    {
        var adapter = new WorkableViewQueryAdapter();
        var normalized = adapter.NormalizeComponentCriteria(new WorkComponentCriteria(Components:
        [
            new("workers", "workers", Shape: WorkComponentShapes.Detailed),
            new("throughput", "throughput", Shape: WorkComponentShapes.Detailed),
            new("failedWorkers", "failedWorkers", Shape: WorkComponentShapes.Compact),
            new("workerGrid", "workerGrid", Shape: WorkComponentShapes.Compact),
            new("iterationGrid", "iterationGrid", Shape: WorkComponentShapes.Standard),
            new("queue", "queueDiagnostics", Shape: WorkComponentShapes.Standard),
            new("readModel", "readModelDiagnostics", Shape: WorkComponentShapes.Standard),
            new("retention", "retentionDiagnostics", Shape: WorkComponentShapes.Standard),
            new("concurrency", "concurrencyDiagnostics", Shape: WorkComponentShapes.Standard),
            new("durability", "durabilityDiagnostics", Shape: WorkComponentShapes.Standard),
            new("idempotency", "idempotencyDiagnostics", Shape: WorkComponentShapes.Standard),
            new("defaultShape", "system"),
            new("custom", "custom", Shape: " custom-shape "),
        ]));
        var components = normalized.Components!.ToDictionary(component => component.Id);

        Assert.Equal(WorkComponentShapes.Standard, components["workers"].Shape);
        Assert.Equal(WorkComponentShapes.Standard, components["throughput"].Shape);
        Assert.Equal(WorkComponentShapes.Standard, components["failedWorkers"].Shape);
        Assert.Equal(WorkComponentShapes.Detailed, components["workerGrid"].Shape);
        Assert.Equal(WorkComponentShapes.Detailed, components["iterationGrid"].Shape);
        Assert.Equal(WorkComponentShapes.Detailed, components["queue"].Shape);
        Assert.Equal(WorkComponentShapes.Detailed, components["readModel"].Shape);
        Assert.Equal(WorkComponentShapes.Detailed, components["retention"].Shape);
        Assert.Equal(WorkComponentShapes.Detailed, components["concurrency"].Shape);
        Assert.Equal(WorkComponentShapes.Detailed, components["durability"].Shape);
        Assert.Equal(WorkComponentShapes.Detailed, components["idempotency"].Shape);
        Assert.Equal(WorkComponentShapes.Detailed, components["defaultShape"].Shape);
        Assert.Equal("custom-shape", components["custom"].Shape);
    }

    [Fact]
    public async Task MaterializeTheDefaultOverviewComponentsFromAuthoritativeRuntimeState()
    {
        var runningStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseRunning = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.AddWork(
                WorkDefinition.Create("views.contract.success", category: "Views:Contract"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
            builder.AddWork(
                WorkDefinition.Create("views.contract.failure", category: "Views:Contract"),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Failure(
                    [WorkMessage.Error("views.contract.failure", "Expected test failure.")])));
            builder.AddWork(
                WorkDefinition.Create("views.contract.running", category: "Views:Contract"),
                async (_, _, cancellationToken) =>
                {
                    runningStarted.TrySetResult();
                    await releaseRunning.Task.WaitAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                });
        });
        await using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var session = await TransportAuthorizationTestSupport.CreateTransportSession(
            system,
            WorkInvocationChannel.InProcess,
            description: "Verify the documented overview component contract.");

        var completedHandle = await session.Queue.Enqueue("views.contract.success");
        var failedHandle = await session.Queue.Enqueue("views.contract.failure");
        var runningHandle = await session.Queue.Enqueue("views.contract.running");
        await completedHandle.WaitForCompletion();
        await failedHandle.WaitForCompletion();
        await runningStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await TestEventually.ReadModelDrained(system);

        var adapter = new WorkableViewQueryAdapter();
        var result = await adapter.View(session, "overview");
        var alternateShapes = await adapter.Components(session, new WorkComponentCriteria(Components:
        [
            new("workersCompact", "workers", Shape: WorkComponentShapes.Compact),
            new("iterationsCompact", "iterations", Shape: WorkComponentShapes.Compact),
            new("failedWorkersDetailed", "failedWorkers", Shape: WorkComponentShapes.Detailed),
            new("failedIterationsDetailed", "failedIterations", Shape: WorkComponentShapes.Detailed),
            new("completedIterationsDetailed", "completedIterations", Shape: WorkComponentShapes.Detailed),
            new("throughputCompact", "throughput", Shape: WorkComponentShapes.Compact),
            new("throughputStandard", "throughput", Shape: WorkComponentShapes.Standard),
            new(
                "workerDetail",
                "workerDetail",
                JsonSerializer.SerializeToElement(new { workerId = failedHandle.WorkerId!.Value.Value.ToString("D") })),
            new(
                "workerCurrentIteration",
                "workerCurrentIteration",
                JsonSerializer.SerializeToElement(new { workerId = runningHandle.WorkerId!.Value.Value.ToString("D") })),
        ]));
        releaseRunning.TrySetResult();
        await runningHandle.WaitForCompletion();

        Assert.Equal(
            ["completedIterations", "failedIterations", "failedWorkers", "iterations", "system", "workers"],
            result.Components.Keys.Order().ToArray());
        Assert.All(result.Components.Values, component => Assert.Equal("ok", component.Status));
        var workers = Assert.IsType<WorkOverviewWorkersStandardComponent>(result.Components["workers"].Data);
        Assert.Equal(1, workers.DefinitionCount);
        Assert.Equal(1, workers.FailedWorkerCount);
        Assert.Single(Assert.IsType<WorkOverviewFailedWorkerStandard[]>(result.Components["failedWorkers"].Data));
        Assert.IsType<WorkOverviewIterationsStandardComponent>(result.Components["iterations"].Data);
        Assert.Single(Assert.IsType<WorkOverviewIterationStandard[]>(result.Components["failedIterations"].Data));
        Assert.Single(Assert.IsType<WorkOverviewIterationStandard[]>(result.Components["completedIterations"].Data));
        Assert.IsType<WorkOverviewWorkersCompactComponent>(alternateShapes.Components["workersCompact"].Data);
        Assert.IsType<WorkOverviewIterationsCompactComponent>(alternateShapes.Components["iterationsCompact"].Data);
        Assert.Single(Assert.IsType<WorkOverviewFailedWorkerDetailed[]>(alternateShapes.Components["failedWorkersDetailed"].Data));
        Assert.Single(Assert.IsType<WorkOverviewIterationDetailed[]>(alternateShapes.Components["failedIterationsDetailed"].Data));
        Assert.Single(Assert.IsType<WorkOverviewIterationDetailed[]>(alternateShapes.Components["completedIterationsDetailed"].Data));
        Assert.IsType<WorkOverviewThroughputCompactComponent>(alternateShapes.Components["throughputCompact"].Data);
        Assert.IsType<WorkOverviewThroughputStandardComponent>(alternateShapes.Components["throughputStandard"].Data);
        Assert.IsType<WorkerSnapshot>(alternateShapes.Components["workerDetail"].Data);
        Assert.IsType<WorkerIterationSnapshot>(alternateShapes.Components["workerCurrentIteration"].Data);
    }

    [Fact]
    public async Task ReturnEveryDiagnosticsFacetAndIsolateUnknownComponentErrors()
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(_ => { });
        await using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var session = await TransportAuthorizationTestSupport.CreateTransportSession(
            system,
            WorkInvocationChannel.InProcess,
            description: "Verify documented diagnostics component contracts.");
        var adapter = new WorkableViewQueryAdapter();

        var diagnostics = await adapter.View(session, "diagnostics");
        var detailedDiagnostics = await adapter.Components(session, new WorkComponentCriteria(Components:
        [
            new("queue", "queueDiagnostics", Shape: WorkComponentShapes.Detailed),
            new("readModel", "readModelDiagnostics", Shape: WorkComponentShapes.Detailed),
            new("retention", "retentionDiagnostics", Shape: WorkComponentShapes.Detailed),
            new("concurrency", "concurrencyDiagnostics", Shape: WorkComponentShapes.Detailed),
            new("durability", "durabilityDiagnostics", Shape: WorkComponentShapes.Detailed),
            new("idempotency", "idempotencyDiagnostics", Shape: WorkComponentShapes.Detailed),
        ]));
        var mixed = await adapter.Components(session, new WorkComponentCriteria(Components:
        [
            new("system", "system"),
            new("unknown", "not-a-component", JsonSerializer.SerializeToElement(new { value = true })),
        ]));
        var unknownView = await adapter.View(session, "not-a-view");

        Assert.Equal(6, diagnostics.Components.Count);
        Assert.All(diagnostics.Components.Values, component =>
        {
            Assert.Equal("ok", component.Status);
            Assert.Equal(WorkComponentShapes.Compact, component.Shape);
        });
        Assert.IsType<WorkQueueDiagnosticsCompactComponent>(diagnostics.Components["queueDiagnostics"].Data);
        Assert.IsType<WorkReadModelDiagnosticsCompactComponent>(diagnostics.Components["readModelDiagnostics"].Data);
        Assert.IsType<WorkRetentionDiagnosticsCompactComponent>(diagnostics.Components["retentionDiagnostics"].Data);
        Assert.IsType<WorkConcurrencyDiagnosticsCompactComponent>(diagnostics.Components["concurrencyDiagnostics"].Data);
        Assert.IsType<WorkDurabilityDiagnosticsCompactComponent>(diagnostics.Components["durabilityDiagnostics"].Data);
        Assert.IsType<WorkIdempotencyDiagnosticsCompactComponent>(diagnostics.Components["idempotencyDiagnostics"].Data);
        Assert.IsType<WorkQueueDiagnosticsDetailedComponent>(detailedDiagnostics.Components["queue"].Data);
        Assert.IsType<WorkReadModelDiagnosticsDetailedComponent>(detailedDiagnostics.Components["readModel"].Data);
        Assert.IsType<WorkRetentionDiagnosticsDetailedComponent>(detailedDiagnostics.Components["retention"].Data);
        Assert.IsType<WorkConcurrencyDiagnosticsDetailedComponent>(detailedDiagnostics.Components["concurrency"].Data);
        Assert.IsType<WorkDurabilityDiagnosticsDetailedComponent>(detailedDiagnostics.Components["durability"].Data);
        Assert.IsType<WorkIdempotencyDiagnosticsDetailedComponent>(detailedDiagnostics.Components["idempotency"].Data);
        Assert.Equal("ok", mixed.Components["system"].Status);
        Assert.Equal("error", mixed.Components["unknown"].Status);
        Assert.Contains("Unknown component", mixed.Components["unknown"].Error);
        Assert.Equal("error", unknownView.Components["not-a-view"].Status);
        Assert.Contains("Unknown view", unknownView.Components["not-a-view"].Error);
    }

    [Fact]
    public async Task BuildCatalogNavigationLevelsWithDirectDefinitionsAndChildCounts()
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(WorkDefinition.Create("catalog.direct.a", category: "Operations"), SuccessfulWork);
            builder.AddWork(WorkDefinition.Create("catalog.direct.b", category: "Operations"), SuccessfulWork);
            builder.AddWork(WorkDefinition.Create("catalog.billing.a", category: "Operations:Billing"), SuccessfulWork);
            builder.AddWork(WorkDefinition.Create("catalog.billing.b", category: "Operations:Billing"), SuccessfulWork);
            builder.AddWork(WorkDefinition.Create("catalog.shipping", category: "Operations:Shipping"), SuccessfulWork);
            builder.AddWork(WorkDefinition.Create("catalog.other", category: "Other"), SuccessfulWork);
        });
        await using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var session = await system.CreateSession(WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var result = await new WorkableViewQueryAdapter().Components(
            session,
            new WorkComponentCriteria(
                new WorkSystemCriteria(Category: "Operations"),
                [new WorkComponentRequest("catalog", "catalog")]));
        var data = JsonSerializer.SerializeToElement(result.Components["catalog"].Data);
        var categories = data.GetProperty("CatalogCategories");
        var definitions = data.GetProperty("CatalogDefinitions");

        Assert.Equal(["Billing", "Shipping"], categories.EnumerateArray()
            .Select(category => category.GetProperty("Label").GetString()!)
            .ToArray());
        Assert.Equal([2, 1], categories.EnumerateArray()
            .Select(category => category.GetProperty("Count").GetInt32())
            .ToArray());
        Assert.Equal(["catalog.direct.a", "catalog.direct.b"], definitions.EnumerateArray()
            .Select(definition => definition.GetProperty("Name").GetString()!)
            .ToArray());
    }

    [Fact]
    public async Task IsolateMalformedWorkerComponentOptionsWithoutDroppingValidComponents()
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.RequireAuthorization(false));
        await using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var session = await system.CreateSession(WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var result = await new WorkableViewQueryAdapter().Components(
            session,
            new WorkComponentCriteria(Components:
            [
                new WorkComponentRequest("system", "system"),
                new WorkComponentRequest("missing-worker-id", "workerDetail"),
                new WorkComponentRequest(
                    "invalid-worker-id",
                    "workerCurrentIteration",
                    JsonSerializer.SerializeToElement(new { workerId = "not-a-guid" })),
                new WorkComponentRequest(
                    "blank-actor-id",
                    "workerGrid",
                    JsonSerializer.SerializeToElement(new { actorId = " " })),
            ]));

        Assert.Equal("ok", result.Components["system"].Status);
        Assert.Equal("error", result.Components["missing-worker-id"].Status);
        Assert.Contains("workerId is required", result.Components["missing-worker-id"].Error);
        Assert.Equal("error", result.Components["invalid-worker-id"].Status);
        Assert.Contains("valid GUID", result.Components["invalid-worker-id"].Error);
        Assert.Equal("error", result.Components["blank-actor-id"].Status);
        Assert.Contains("actorId must not be empty", result.Components["blank-actor-id"].Error);
    }

    [Fact]
    public async Task RedactUnexpectedComponentFailureDetails()
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.RequireAuthorization(false));
        await using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var inner = await system.CreateSession(WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var query = DispatchProxy.Create<IWorkQueryService, ThrowingQueryProxy>();
        ((ThrowingQueryProxy)(object)query).Exception = new ArgumentException("database-password=secret");
        var session = new QueryOverrideSession(inner, query);

        var result = await new WorkableViewQueryAdapter(NullLogger<WorkableViewQueryAdapter>.Instance).Components(
            session,
            new WorkComponentCriteria(Components:
            [
                new WorkComponentRequest(
                    "worker",
                    "workerDetail",
                    JsonSerializer.SerializeToElement(new { workerId = Guid.NewGuid() })),
            ]));

        var component = result.Components["worker"];
        Assert.Equal("error", component.Status);
        Assert.Equal("The component query failed.", component.Error);
        Assert.DoesNotContain("database-password", component.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PropagateComponentQueryCancellation()
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.RequireAuthorization(false));
        await using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var inner = await system.CreateSession(WorkRequestContext.Create(WorkInvocationChannel.InProcess));
        var query = DispatchProxy.Create<IWorkQueryService, ThrowingQueryProxy>();
        ((ThrowingQueryProxy)(object)query).Exception = new OperationCanceledException("query canceled");
        var session = new QueryOverrideSession(inner, query);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new WorkableViewQueryAdapter().Components(
                session,
                new WorkComponentCriteria(Components:
                [
                    new WorkComponentRequest(
                        "worker",
                        "workerDetail",
                        JsonSerializer.SerializeToElement(new { workerId = Guid.NewGuid() })),
                ])));
    }

    [Fact]
    public async Task ComposeWorkerAndIterationGridsForExactAndFacetKeyFilters()
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.AddWork(WorkDefinition.Create("views.grid.billing", category: "Grid:Billing"), SuccessfulWork);
            builder.AddWork(WorkDefinition.Create("views.grid.shipping", category: "Grid:Shipping"), SuccessfulWork);
        });
        await using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var session = await TransportAuthorizationTestSupport.CreateTransportSession(system);
        var accountA = new WorkSubjectId("account", "A");
        var accountB = new WorkSubjectId("account", "B");

        await (await session.Queue.Enqueue(
            "views.grid.billing",
            WorkInput.Empty.WithSubject(accountA).WithIdentifier(new WorkIdentifier("invoice", "INV-1"))))
            .WaitForCompletion();
        await (await session.Queue.Enqueue(
            "views.grid.billing",
            WorkInput.Empty.WithSubject(accountB).WithIdentifier(new WorkIdentifier("invoice", "INV-2"))))
            .WaitForCompletion();
        await (await session.Queue.Enqueue(
            "views.grid.shipping",
            WorkInput.Empty.WithSubject(accountA).WithIdentifier(new WorkIdentifier("invoice", "INV-3"))))
            .WaitForCompletion();
        await TestEventually.ReadModelDrained(system);

        var exactOptions = JsonSerializer.SerializeToElement(new
        {
            keyKind = WorkKeyKind.Subject,
            keyType = accountA.Type,
            keyValue = accountA.Value,
        });
        var facetOptions = JsonSerializer.SerializeToElement(new
        {
            keyKind = WorkKeyKind.Identifier,
            keyType = " invoice ",
            skip = -10,
            take = 1,
        });
        var result = await new WorkableViewQueryAdapter().Components(session, new WorkComponentCriteria(
            Scope: new WorkSystemCriteria(Category: "Grid:Billing", IncludeSubcategories: true),
            Components:
            [
                new("exactWorkers", "workerGrid", exactOptions),
                new("facetWorkers", "workerGrid", facetOptions),
                new("exactIterations", "iterationGrid", exactOptions),
                new("facetIterations", "iterationGrid", facetOptions),
            ]));

        var exactWorkers = Assert.IsType<WorkViewWorkerGridDetailedComponent>(result.Components["exactWorkers"].Data);
        var facetWorkers = Assert.IsType<WorkViewWorkerGridDetailedComponent>(result.Components["facetWorkers"].Data);
        var exactIterations = Assert.IsType<WorkViewIterationGridDetailedComponent>(result.Components["exactIterations"].Data);
        var facetIterations = Assert.IsType<WorkViewIterationGridDetailedComponent>(result.Components["facetIterations"].Data);
        Assert.Equal("views.grid.billing", Assert.Single(exactWorkers.Workers).DefinitionName);
        Assert.Equal(2, facetWorkers.TotalCount);
        Assert.Single(facetWorkers.Workers);
        Assert.Equal(0, facetWorkers.Skip);
        Assert.Equal(1, facetWorkers.Take);
        Assert.Equal("views.grid.billing", Assert.Single(exactIterations.Iterations).DefinitionName);
        Assert.Equal(2, facetIterations.TotalCount);
        Assert.Single(facetIterations.Iterations);
        Assert.Equal(0, facetIterations.Skip);
        Assert.Equal(1, facetIterations.Take);
    }

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private sealed class QueryOverrideSession(
        IWorkSystemSession inner,
        IWorkQueryService query) : IWorkSystemSession
    {
        public string? SystemName => inner.SystemName;

        public WorkSystemState SystemState => inner.SystemState;

        public WorkSystemCapabilities Capabilities => inner.Capabilities;

        public IWorkSystemDiagnostics Diagnostics => inner.Diagnostics;

        public IWorkCatalog Catalog => inner.Catalog;

        public IWorkQueueService Queue => inner.Queue;

        public IWorkerOperations Workers => inner.Workers;

        public IWorkQueryService Query => query;

        public IWorkEventStream Events => inner.Events;

        public IWorkIterationStatusStream IterationStatuses => inner.IterationStatuses;

        public IWorkChangeStream Changes => inner.Changes;
    }

    public class ThrowingQueryProxy : DispatchProxy
    {
        public Exception Exception { get; set; } = new InvalidOperationException("Query failure.");

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
            => throw this.Exception;
    }

    private static void AssertComponents(
        WorkableViewQueryAdapter adapter,
        string viewName,
        params string[] expectedTypes)
    {
        var actual = adapter.NormalizeViewCriteria(viewName).Components;

        Assert.NotNull(actual);
        Assert.Equal(expectedTypes, actual.Select(component => component.Type).ToArray());
    }
}
