using Workable;

namespace Workable.Tests;

[Trait("Category", "Systems")]
public sealed class SessionWorkCatalogShould
{
    [Fact]
    public async Task ExposeRequestContextAndDelegateCatalogOperations()
    {
        var definition = WorkDefinition.Create(
            "session.catalog.work",
            category: "Session:Catalog");
        var catalog = new WorkSystemCatalog(
            [CreateRegisteredWork(definition)],
            persistenceStoreAvailable: false);
        catalog.Freeze();
        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.HttpApi,
            new WorkActor("catalog-user", "Catalog User"),
            "Session catalog test.");
        var sessionCatalog = new SessionWorkCatalog(catalog, requestContext);

        var definitions = sessionCatalog.Definitions;
        var category = sessionCatalog.ListByCategory("Session");
        var byName = RequireFound(sessionCatalog.TryGet("SESSION.CATALOG.WORK", out var foundByName), foundByName);
        var outcome = await sessionCatalog.Reconfigure(
            definition.Version,
            new WorkDefinitionReconfiguration(WorkerOptions.Default with { ProfilingEnabled = true }));

        Assert.Same(requestContext, sessionCatalog.RequestContext);
        Assert.True(sessionCatalog.IsFrozen);
        Assert.Equal(definition.Id, Assert.Single(definitions).Id);
        Assert.Equal(definition.Id, Assert.Single(category).Id);
        Assert.Equal(definition.Id, byName.Id);
        Assert.Equal(WorkDefinitionReconfigurationStatus.Accepted, outcome.Status);
        Assert.NotNull(outcome.Definition);
        Assert.True(outcome.Definition.DefaultOptions.ProfilingEnabled);
        var updated = RequireFound(catalog.TryGet(definition.Name, out var foundUpdated), foundUpdated);
        Assert.True(updated.DefaultOptions.ProfilingEnabled);
    }

    private static RegisteredWork CreateRegisteredWork(WorkDefinition definition)
        => new(definition, _ => new NoopExecutor(), []);

    private static WorkDefinition RequireFound(bool found, WorkDefinition? definition)
    {
        Assert.True(found);
        return definition ?? throw new InvalidOperationException("Expected definition to be found.");
    }

    private sealed class NoopExecutor : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}
