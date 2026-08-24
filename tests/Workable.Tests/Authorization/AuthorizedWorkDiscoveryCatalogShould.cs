using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class AuthorizedWorkDiscoveryCatalogShould
{
    [Fact]
    public void ExposeOnlyDiscoverableRedactedDescriptorsAcrossEveryLookupShape()
    {
        var examplePrompts = new List<string> { "Original prompt" };
        var capabilities = new List<string> { "original-capability" };
        var metadata = new WorkDefinitionMetadata(
            "Exercise discovery projection.",
            ExamplePrompts: examplePrompts,
            Capabilities: capabilities);
        var explicitDiscovery = CreateDefinition(
            "explicit.discovery",
            discoverGroups: ["discoverers"],
            metadata: metadata,
            allowMcp: true);
        var readable = CreateDefinition("read.implies.discovery", readGroups: ["readers"]);
        var operable = CreateDefinition("operate.implies.discovery", operateGroups: ["operators"]);
        var hidden = CreateDefinition("hidden.discovery", discoverGroups: ["other"]);
        var catalog = CreateCatalog(explicitDiscovery, readable, operable, hidden);
        var authorization = new WorkAuthorizationEvaluator(
            catalog,
            Groups("discoverers", "readers", "operators"),
            isKnownAuthenticatedUser: false);
        var discovery = new AuthorizedWorkDiscoveryCatalog(catalog, authorization);

        var definitions = discovery.Definitions.OrderBy(definition => definition.Name).ToArray();
        var byCategory = discovery.ListByCategory("Discovery");
        var mcpDefinitions = discovery.ListInvocableBy(WorkInvocationChannel.Mcp);
        var found = discovery.TryGet(explicitDiscovery.Name, out var descriptor);
        var hiddenFound = discovery.TryGet(hidden.Name, out var hiddenDescriptor);

        Assert.Equal(3, definitions.Length);
        Assert.Equal(3, byCategory.Count);
        Assert.Equal(explicitDiscovery.Name, Assert.Single(mcpDefinitions).Name);
        Assert.True(found);
        Assert.NotNull(descriptor);
        Assert.Equal(explicitDiscovery.Name, descriptor.Name);
        Assert.Equal(explicitDiscovery.InputSchema, descriptor.InputSchema);
        var listedDescriptor = Assert.Single(definitions, definition => definition.Name == explicitDiscovery.Name);
        Assert.True(catalog.TryGet(explicitDiscovery.Name, out var catalogDefinition));
        Assert.NotSame(metadata, descriptor.Metadata);
        Assert.Same(catalogDefinition.Metadata, descriptor.Metadata);
        Assert.Same(listedDescriptor.Metadata, descriptor.Metadata);
        Assert.Same(listedDescriptor.Metadata?.ExamplePrompts, descriptor.Metadata?.ExamplePrompts);
        Assert.Same(listedDescriptor.Metadata?.Capabilities, descriptor.Metadata?.Capabilities);
        Assert.Equal(examplePrompts, descriptor.Metadata?.ExamplePrompts);
        Assert.Equal(capabilities, descriptor.Metadata?.Capabilities);
        examplePrompts[0] = "Mutated prompt";
        capabilities[0] = "mutated-capability";
        Assert.Equal("Original prompt", Assert.Single(descriptor.Metadata?.ExamplePrompts ?? []));
        Assert.Equal("original-capability", Assert.Single(descriptor.Metadata?.Capabilities ?? []));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<string>)descriptor.Metadata!.Capabilities!)[0] = "descriptor-mutation");
        Assert.False(hiddenFound);
        Assert.Null(hiddenDescriptor);
    }

    [Fact]
    public void CompatibilityAndUnrestrictedCatalogsProjectTheirCompleteInputCatalog()
    {
        var mcpDefinition = CreateDefinition("mcp.discovery", allowMcp: true);
        var defaultDefinition = CreateDefinition("default.discovery");
        var catalog = CreateCatalog(mcpDefinition, defaultDefinition);
        IWorkSystemSession compatibilitySession = new CatalogOnlySession(catalog);
        var compatibility = compatibilitySession.Discovery;
        IWorkDiscoveryCatalog unrestricted = new AuthorizedWorkDiscoveryCatalog(catalog);

        Assert.Equal(2, compatibility.Definitions.Count);
        Assert.Equal(2, unrestricted.Definitions.Count);
        Assert.Equal(2, compatibility.ListByCategory("Discovery").Count);
        Assert.Equal(mcpDefinition.Name, Assert.Single(compatibility.ListInvocableBy(WorkInvocationChannel.Mcp)).Name);
        Assert.True(compatibility.TryGet(defaultDefinition.Name, out var found));
        Assert.Equal(defaultDefinition.Name, found.Name);
        Assert.False(compatibility.TryGet("missing.discovery", out var missing));
        Assert.Null(missing);
    }

    [Fact]
    public async Task CompatibilitySessionReconfiguresThroughItsReadableCatalog()
    {
        var definition = CreateDefinition("compatibility.reconfigure");
        var catalog = CreateCatalog(definition);
        IWorkSystemSession session = new CatalogOnlySession(catalog);

        var accepted = await session.ReconfigureDefinition(
            definition.Name,
            definition.Revision,
            new WorkDefinitionReconfiguration(
                DefaultOptions: new WorkerOptions(ProfilingEnabled: true)));
        var missing = await session.ReconfigureDefinition(
            "missing.reconfigure",
            revision: 0,
            new WorkDefinitionReconfiguration(
                DefaultOptions: new WorkerOptions(ProfilingEnabled: true)));

        Assert.True(accepted.IsAccepted);
        Assert.Equal(1, accepted.Revision);
        Assert.Equal(WorkDefinitionReconfigurationStatus.NotFound, missing.Status);
        await Assert.ThrowsAsync<ArgumentException>(() => session.ReconfigureDefinition(
            " ",
            revision: 0,
            new WorkDefinitionReconfiguration()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => session.ReconfigureDefinition(
            definition.Name,
            revision: 0,
            null!));
    }

    [Fact]
    public void RuntimeCatalogAdditionSnapshotsMetadataOnceForDiscovery()
    {
        var examplePrompts = new List<string> { "Original prompt" };
        var capabilities = new List<string> { "original-capability" };
        var definition = CreateDefinition(
            "runtime.discovery",
            metadata: new WorkDefinitionMetadata(
                "Exercise runtime discovery projection.",
                ExamplePrompts: examplePrompts,
                Capabilities: capabilities));
        var catalog = CreateCatalog();
        catalog.AddWork(new RegisteredWork(
            definition,
            _ => new NoopExecutor(),
            []));
        var discovery = new AuthorizedWorkDiscoveryCatalog(catalog);

        Assert.True(discovery.TryGet(definition.Name, out var first));
        Assert.True(discovery.TryGet(definition.Name, out var second));

        examplePrompts[0] = "Mutated prompt";
        capabilities[0] = "mutated-capability";

        Assert.NotSame(definition.Metadata, first.Metadata);
        Assert.Same(first.Metadata, second.Metadata);
        Assert.Same(first.Metadata?.ExamplePrompts, second.Metadata?.ExamplePrompts);
        Assert.Same(first.Metadata?.Capabilities, second.Metadata?.Capabilities);
        Assert.Equal("Original prompt", Assert.Single(second.Metadata?.ExamplePrompts ?? []));
        Assert.Equal("original-capability", Assert.Single(second.Metadata?.Capabilities ?? []));
    }

    private static WorkDefinition CreateDefinition(
        string name,
        IEnumerable<string>? discoverGroups = null,
        IEnumerable<string>? readGroups = null,
        IEnumerable<string>? operateGroups = null,
        WorkDefinitionMetadata? metadata = null,
        bool allowMcp = false)
        => WorkDefinition.Create(
            name,
            category: "Discovery",
            inputSchema: WorkSchema.FromType<string>(),
            metadata: metadata,
            authorization: WorkDefinitionAuthorization.Create(
                readGroups: readGroups,
                operateGroups: operateGroups,
                discoverGroups: discoverGroups),
            configuration: allowMcp
                ? WorkConfiguration.Default with
                {
                    Invocation = WorkInvocationConfiguration.Allow(WorkInvocationChannel.Mcp),
                }
                : WorkConfiguration.Default);

    private static WorkSystemCatalog CreateCatalog(params WorkDefinition[] definitions)
        => new(
            definitions.Select(definition => new RegisteredWork(
                definition,
                _ => new NoopExecutor(),
                [])).ToArray(),
            persistenceStoreAvailable: false);

    private static IReadOnlySet<string> Groups(params string[] groups)
        => groups.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private sealed class NoopExecutor : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }

    private sealed class CatalogOnlySession(IWorkCatalog catalog) : IWorkSystemSession
    {
        public string? SystemName => null;

        public WorkSystemState SystemState => WorkSystemState.Started;

        public WorkSystemCapabilities Capabilities => WorkSystemCapabilities.None;

        public IWorkSystemDiagnostics Diagnostics => throw new NotSupportedException();

        public IWorkCatalog Catalog { get; } = catalog;

        public IWorkQueueService Queue => throw new NotSupportedException();

        public IWorkerOperations Workers => throw new NotSupportedException();

        public IWorkQueryService Query => throw new NotSupportedException();

        public IWorkEventStream Events => throw new NotSupportedException();

        public IWorkChangeStream Changes => throw new NotSupportedException();
    }
}
