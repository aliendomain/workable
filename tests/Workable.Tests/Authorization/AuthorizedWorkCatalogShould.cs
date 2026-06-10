using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class AuthorizedWorkCatalogShould
{
    [Fact]
    public void ExposeOnlyReadableDefinitions()
    {
        var catalog = CreateCatalog(
            out var visible,
            out var hidden);
        var authorized = new AuthorizedWorkCatalog(
            catalog,
            catalog,
            new WorkAuthorizationEvaluator(catalog, Groups("visible.read"), false),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var definitions = authorized.Definitions;
        var categoryDefinitions = authorized.ListByCategory("Operations");
        var byName = RequireFound(authorized.TryGet(visible.Name, out var foundByName), foundByName);
        var hiddenByName = authorized.TryGet(hidden.Name, out var hiddenName);

        Assert.Equal(visible.Id, Assert.Single(definitions).Id);
        Assert.Equal(visible.Id, Assert.Single(categoryDefinitions).Id);
        Assert.Equal(visible.Id, byName.Id);
        Assert.False(hiddenByName);
        Assert.Null(hiddenName);
    }

    [Fact]
    public async Task DelegateReconfigurationForOperableDefinitions()
    {
        var catalog = CreateCatalog(
            out var visible,
            out _);
        var authorized = new AuthorizedWorkCatalog(
            catalog,
            catalog,
            new WorkAuthorizationEvaluator(catalog, Groups("visible.operate"), false),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var outcome = await authorized.Reconfigure(
            visible.Version,
            new WorkDefinitionReconfiguration(WorkerOptions.Default with { ProfilingEnabled = true }));

        Assert.Equal(WorkDefinitionReconfigurationStatus.Accepted, outcome.Status);
        Assert.NotNull(outcome.Definition);
        Assert.True(outcome.Definition.DefaultOptions.ProfilingEnabled);
        var updated = RequireFound(catalog.TryGet(visible.Name, out var found), found);
        Assert.True(updated.DefaultOptions.ProfilingEnabled);
        Assert.Equal(visible.Revision + 1, updated.Revision);
    }

    [Fact]
    public async Task ReturnUnauthorizedWithoutReconfiguringInoperableDefinitions()
    {
        var catalog = CreateCatalog(
            out _,
            out var hidden);
        var authorized = new AuthorizedWorkCatalog(
            catalog,
            catalog,
            new WorkAuthorizationEvaluator(catalog, Groups("visible.operate"), false),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var outcome = await authorized.Reconfigure(
            hidden.Version,
            new WorkDefinitionReconfiguration(WorkerOptions.Default with { ProfilingEnabled = true }));

        Assert.Equal(WorkDefinitionReconfigurationStatus.Unauthorized, outcome.Status);
        Assert.Null(outcome.Definition);
        Assert.Contains(hidden.Name, Assert.Single(outcome.Messages).Text);
        var current = RequireFound(catalog.TryGet(hidden.Name, out var found), found);
        Assert.False(current.DefaultOptions.ProfilingEnabled);
        Assert.Equal(hidden.Revision, current.Revision);
    }

    [Fact]
    public async Task ReturnUnauthorizedWhenDefinitionReconfigurationPermissionIsMissing()
    {
        var authorization = new WorkAuthorizationBuilder();
        authorization.AllowWorkerActionsToGroups("visible.operate");
        var registration = authorization.BuildRegistration();
        var visible = WorkDefinition.Create(
            "visible.work",
            category: "Operations",
            authorization: registration.DefinitionAuthorization);
        var catalog = new WorkSystemCatalog(
            [CreateRegisteredWork(visible, registration)],
            persistenceStoreAvailable: false);
        var authorized = new AuthorizedWorkCatalog(
            catalog,
            catalog,
            new WorkAuthorizationEvaluator(catalog, Groups("visible.operate"), false),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var outcome = await authorized.Reconfigure(
            visible.Version,
            new WorkDefinitionReconfiguration(WorkerOptions.Default with { ProfilingEnabled = true }));

        Assert.Equal(WorkDefinitionReconfigurationStatus.Unauthorized, outcome.Status);
        var current = RequireFound(catalog.TryGet(visible.Name, out var found), found);
        Assert.False(current.DefaultOptions.ProfilingEnabled);
        Assert.Equal(visible.Revision, current.Revision);
    }

    [Fact]
    public async Task ApplyDefinitionReconfigurationRequirements()
    {
        var builder = new WorkAuthorizationBuilder();
        builder.AllowOperationsToGroups(
            ["visible.operate"],
            WorkOperationPermissions.ReconfigureDefinition,
            operate => operate.WhenDefinitionReconfiguringRequire(context => context.Changes.Configuration is not null));
        var registration = builder.BuildRegistration();
        var visible = WorkDefinition.Create(
            "visible.work",
            category: "Operations",
            authorization: registration.DefinitionAuthorization);
        var catalog = new WorkSystemCatalog(
            [CreateRegisteredWork(visible, registration)],
            persistenceStoreAvailable: false);
        var authorized = new AuthorizedWorkCatalog(
            catalog,
            catalog,
            new WorkAuthorizationEvaluator(catalog, Groups("visible.operate"), false),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess));

        var rejected = await authorized.Reconfigure(
            visible.Version,
            new WorkDefinitionReconfiguration(WorkerOptions.Default with { ProfilingEnabled = true }));
        var accepted = await authorized.Reconfigure(
            visible.Version,
            new WorkDefinitionReconfiguration(
                WorkerOptions.Default with { ProfilingEnabled = true },
                WorkConfiguration.Default));

        Assert.Equal(WorkDefinitionReconfigurationStatus.Unauthorized, rejected.Status);
        Assert.Equal(WorkDefinitionReconfigurationStatus.Accepted, accepted.Status);
    }

    private static WorkSystemCatalog CreateCatalog(
        out WorkDefinition visible,
        out WorkDefinition hidden)
    {
        visible = CreateDefinition("visible.work", "visible.read", "visible.operate");
        hidden = CreateDefinition("hidden.work", "hidden.read", "hidden.operate");
        return new WorkSystemCatalog(
            [
                CreateRegisteredWork(visible),
                CreateRegisteredWork(hidden),
            ],
            persistenceStoreAvailable: false);
    }

    private static WorkDefinition CreateDefinition(
        string name,
        string readGroup,
        string operateGroup)
        => WorkDefinition.Create(
            name,
            category: "Operations",
            authorization: WorkDefinitionAuthorization.Create(
                readGroups: [readGroup],
                operateGroups: [operateGroup]));

    private static RegisteredWork CreateRegisteredWork(WorkDefinition definition)
        => new(definition, _ => new NoopExecutor(), []);

    private static RegisteredWork CreateRegisteredWork(
        WorkDefinition definition,
        WorkAuthorizationRegistration registration)
        => new(
            definition,
            _ => new NoopExecutor(),
            [],
            [],
            [],
            registration.OperateAuthorization);

    private static IReadOnlySet<string> Groups(params string[] groups)
        => groups.ToHashSet(StringComparer.OrdinalIgnoreCase);

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
