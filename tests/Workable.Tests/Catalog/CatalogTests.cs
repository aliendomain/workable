using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "WorkCatalog")]
public sealed class CatalogTests
{
    [Fact]
    public void MultipleSystemsHaveIsolatedCatalogs()
    {
        var first = WorkDefinition.Create("first", "Runs first.");
        var second = WorkDefinition.Create("second", "Runs second.");

        var services = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(first, SuccessfulWork))
            .AddWorkableSystem("named", builder => builder.AddWork(second, SuccessfulWork));

        var registry = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>();

        Assert.True(registry.Default.Catalog.TryGet("first", out _));
        Assert.False(registry.Default.Catalog.TryGet("second", out _));

        Assert.True(registry.TryGet("named", out var named));
        Assert.True(named.Catalog.TryGet("second", out _));
        Assert.False(named.Catalog.TryGet("first", out _));
    }

    [Fact]
    public void DuplicateNamesAreRejectedWithinOneCatalog()
    {
        var first = WorkDefinition.Create("same", "Runs first.");
        var second = WorkDefinition.Create("same", "Runs second.");

        var services = new ServiceCollection()
            .AddWorkableSystem(builder => builder
                .AddWork(first, SuccessfulWork)
                .AddWork(second, SuccessfulWork));

        Assert.Throws<InvalidOperationException>(() => services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>());
    }

    [Fact]
    public void SameInputSchemaCanBeUsedByMultipleDefinitions()
    {
        var schema = new WorkSchema("""{"type":"object","properties":{"id":{"type":"string"}}}""");
        var first = WorkDefinition.Create("first", "Runs first.", inputSchema: schema);
        var second = WorkDefinition.Create("second", "Runs second.", inputSchema: schema);

        var services = new ServiceCollection()
            .AddWorkableSystem(builder => builder
                .AddWork(first, SuccessfulWork)
                .AddWork(second, SuccessfulWork));

        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;

        Assert.Equal(2, system.Catalog.Definitions.Count);
        Assert.All(system.Catalog.Definitions, definition => Assert.Equal(schema, definition.InputSchema));
    }

    [Fact]
    public void CatalogListsDefinitionsByCategoryPath()
    {
        var finance = WorkDefinition.Create("finance.root", category: "Finance");
        var invoice = WorkDefinition.Create("invoice.send", category: "Finance:Invoices");
        var payroll = WorkDefinition.Create("payroll.run", category: "Finance:Payroll");
        var operations = WorkDefinition.Create("operations.finance", category: "FinanceOperations");

        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder
                .AddWork(finance, SuccessfulWork)
                .AddWork(invoice, SuccessfulWork)
                .AddWork(payroll, SuccessfulWork)
                .AddWork(operations, SuccessfulWork))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        var descendants = system.Catalog.ListByCategory("finance");
        var exact = system.Catalog.ListByCategory("Finance", includeSubcategories: false);

        Assert.Equal(["finance.root", "invoice.send", "payroll.run"], descendants.Select(definition => definition.Name));
        Assert.Equal(["finance.root"], exact.Select(definition => definition.Name));
    }

    [Fact]
    public async Task CatalogIsFrozenAfterSystemStart()
    {
        var definition = WorkDefinition.Create("work", "Runs.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, SuccessfulWork))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

        Assert.False(system.Catalog.IsFrozen);

        await system.Start();

        Assert.True(system.Catalog.IsFrozen);
    }

    [Fact]
    public void DefinitionMetadataIsOptionalAndSeparateFromRuntimeOptions()
    {
        var metadata = new WorkDefinitionMetadata(
            Purpose: "Let automation decide whether this work is appropriate.",
            WhenToUse: "Use when a policy sync should be retried.",
            WhenNotToUse: "Do not use for destructive cleanup.",
            Risk: WorkRisk.Medium,
            RequiresApproval: true,
            RequiresJustification: true,
            ExamplePrompts: ["Retry the failed policy sync for P-123."],
            Capabilities: ["writes-database"]);

        var definition = WorkDefinition.Create("policy.sync.retry", "Retries one failed policy sync.",
            metadata: metadata);

        Assert.Equal("policy.sync.retry", definition.Name);
        Assert.Equal(WorkerOptions.Default, definition.DefaultOptions);
        var definitionMetadata = Assert.IsType<WorkDefinitionMetadata>(definition.Metadata);
        Assert.Same(metadata, definitionMetadata);
        Assert.True(definitionMetadata.RequiresApproval);
        Assert.Contains("writes-database", definitionMetadata.Capabilities ?? []);
    }

    [Fact]
    public void WorkDefinitionExposesRevisionVersion()
    {
        var definition = WorkDefinition.Create("versioned.definition");

        Assert.Equal(0, definition.Revision);
        Assert.Equal(new WorkDefinitionVersion(definition.Id, definition.Revision), definition.Version);
    }

    [Fact]
    public async Task DefinitionReconfigurationUpdatesOnlyDefaultsAndAdvancesRevision()
    {
        var inputSchema = new WorkSchema("""{"type":"object","properties":{"id":{"type":"string"}}}""");
        var outputSchema = new WorkSchema("""{"type":"object","properties":{"ok":{"type":"boolean"}}}""");
        var metadata = new WorkDefinitionMetadata(Purpose: "Preserved metadata.");
        var definition = WorkDefinition.Create(
            "definition.defaults",
            "Preserves non-default metadata.",
            category: "Catalog:Definitions",
            inputSchema: inputSchema,
            outputSchema: outputSchema,
            metadata: metadata);
        var system = CreateSystem(definition);

        var outcome = await system.Catalog.Reconfigure(
            definition.Version,
            new WorkDefinitionReconfiguration(
                DefaultOptions: new WorkerOptions(ProfilingEnabled: true),
                Configuration: WorkConfiguration.Default with
                {
                    Start = WorkStartConfiguration.DoNotStart,
                }));

        Assert.True(outcome.IsAccepted);
        Assert.NotNull(outcome.Definition);
        Assert.Equal(1, outcome.Definition.Revision);
        Assert.Equal(definition.Id, outcome.Definition.Id);
        Assert.Equal(definition.Name, outcome.Definition.Name);
        Assert.Equal(definition.Category, outcome.Definition.Category);
        Assert.Equal(definition.Description, outcome.Definition.Description);
        Assert.Equal(inputSchema, outcome.Definition.InputSchema);
        Assert.Equal(outputSchema, outcome.Definition.OutputSchema);
        Assert.Same(metadata, outcome.Definition.Metadata);
        Assert.True(outcome.Definition.DefaultOptions.ProfilingEnabled);
        Assert.Equal(WorkStartPolicy.DoNotStart, outcome.Definition.Configuration.Start.Policy);

        Assert.True(system.Catalog.TryGet(definition.Id, out var catalogDefinition));
        Assert.Equal(outcome.Definition, catalogDefinition);
        Assert.Equal(0, definition.Revision);
        Assert.False(definition.DefaultOptions.ProfilingEnabled);
    }

    [Fact]
    public async Task DefinitionReconfigurationRefreshesCatalogAndQueryViews()
    {
        var definition = WorkDefinition.Create("definition.query.refresh", category: "Catalog:Refresh");
        var system = CreateSystem(definition);

        var outcome = await system.Catalog.Reconfigure(
            definition.Version,
            new WorkDefinitionReconfiguration(DefaultOptions: new WorkerOptions(ProfilingEnabled: true)));

        var byCategory = Assert.Single(system.Catalog.ListByCategory("Catalog:Refresh"));
        var byQuery = Assert.Single(await system.Query.QueryWorkDefinitions(new WorkDefinitionQuery(Name: "definition.query.refresh")));
        var info = await system.Query.GetWorkInfo(definition.Id);

        Assert.True(outcome.IsAccepted);
        Assert.Equal(1, byCategory.Revision);
        Assert.True(byCategory.DefaultOptions.ProfilingEnabled);
        Assert.Equal(byCategory, byQuery);
        Assert.Equal(byCategory, info?.Definition);
    }

    [Fact]
    public async Task DefinitionReconfigurationRejectsStaleRevision()
    {
        var definition = WorkDefinition.Create("definition.conflict");
        var system = CreateSystem(definition);

        var accepted = await system.Catalog.Reconfigure(
            definition.Version,
            new WorkDefinitionReconfiguration(DefaultOptions: new WorkerOptions(ProfilingEnabled: true)));
        var conflict = await system.Catalog.Reconfigure(
            definition.Version,
            new WorkDefinitionReconfiguration(DefaultOptions: new WorkerOptions(ProfilingEnabled: false)));

        Assert.True(accepted.IsAccepted);
        Assert.Equal(WorkDefinitionReconfigurationStatus.Conflict, conflict.Status);
        Assert.Equal(1, conflict.Definition?.Revision);
        Assert.Contains(conflict.Messages, message => message.Code == "workable.definition.revision_conflict");
    }

    [Fact]
    public async Task DefinitionReconfigurationRejectsUnknownDefinition()
    {
        var system = CreateSystem(WorkDefinition.Create("definition.known"));

        var outcome = await system.Catalog.Reconfigure(
            new WorkDefinitionVersion(WorkDefinitionId.New(), 0),
            new WorkDefinitionReconfiguration(DefaultOptions: new WorkerOptions(ProfilingEnabled: true)));

        Assert.Equal(WorkDefinitionReconfigurationStatus.NotFound, outcome.Status);
        Assert.Null(outcome.Definition);
    }

    [Fact]
    public async Task DefinitionReconfigurationRejectsInvalidConfigurationWithoutChangingRevision()
    {
        var definition = WorkDefinition.Create("definition.invalid");
        var system = CreateSystem(definition);

        var outcome = await system.Catalog.Reconfigure(
            definition.Version,
            new WorkDefinitionReconfiguration(
                Configuration: WorkConfiguration.Default with
                {
                    Recurrence = new WorkRecurrenceConfiguration
                    {
                        IsEnabled = true,
                        Interval = TimeSpan.Zero,
                    },
                }));

        Assert.Equal(WorkDefinitionReconfigurationStatus.Invalid, outcome.Status);
        Assert.Equal(0, outcome.Definition?.Revision);
        Assert.Contains(outcome.Messages, message => message.Code == "workable.configuration.recurrence.interval_required");
        Assert.True(system.Catalog.TryGet(definition.Id, out var current));
        Assert.Equal(0, current.Revision);
        Assert.False(current.Configuration.Recurrence.IsEnabled);
    }

    [Fact]
    public async Task DefinitionReconfigurationRejectsInvalidDefaultOptionsConfiguration()
    {
        var definition = WorkDefinition.Create("definition.invalid.options");
        var system = CreateSystem(definition);

        var outcome = await system.Catalog.Reconfigure(
            definition.Version,
            new WorkDefinitionReconfiguration(
                DefaultOptions: new WorkerOptions(
                    Configuration: WorkConfiguration.Default with
                    {
                        Retention = WorkRetentionConfiguration.Default with
                        {
                            PurgeInterval = TimeSpan.Zero,
                        },
                    })));

        Assert.Equal(WorkDefinitionReconfigurationStatus.Invalid, outcome.Status);
        Assert.Equal(0, outcome.Definition?.Revision);
        Assert.Contains(outcome.Messages, message => message.Code == "workable.configuration.retention.purge_interval_required");
    }

    [Fact]
    public async Task DefinitionReconfigurationAcceptsOnlyOneConcurrentChangeForSameRevision()
    {
        var definition = WorkDefinition.Create("definition.concurrent");
        var system = CreateSystem(definition);

        var attempts = await Task.WhenAll(
            Task.Run(async () => await system.Catalog.Reconfigure(
                definition.Version,
                new WorkDefinitionReconfiguration(DefaultOptions: new WorkerOptions(ProfilingEnabled: true)))),
            Task.Run(async () => await system.Catalog.Reconfigure(
                definition.Version,
                new WorkDefinitionReconfiguration(DefaultOptions: new WorkerOptions(ProfilingEnabled: false)))));

        Assert.Equal(1, attempts.Count(outcome => outcome.Status == WorkDefinitionReconfigurationStatus.Accepted));
        Assert.Equal(1, attempts.Count(outcome => outcome.Status == WorkDefinitionReconfigurationStatus.Conflict));
        Assert.True(system.Catalog.TryGet(definition.Id, out var current));
        Assert.Equal(1, current.Revision);
    }

    [Fact]
    public async Task DefinitionReconfigurationAffectsOnlyFutureWorkers()
    {
        var definition = WorkDefinition.Create("definition.future-workers");
        var system = CreateSystem(definition);

        await system.Start();

        var first = await system.Queue.Enqueue(definition.Id);
        await first.WaitForCompletion();
        var firstWorker = await system.Query.GetWorker(RequiredWorkerId(first));

        var reconfigured = await system.Catalog.Reconfigure(
            definition.Version,
            new WorkDefinitionReconfiguration(
                DefaultOptions: new WorkerOptions(ProfilingEnabled: true),
                Configuration: WorkConfiguration.Default with
                {
                    Start = WorkStartConfiguration.DoNotStart,
                }));
        var second = await system.Queue.Enqueue(definition.Id);
        var secondWorker = await system.Query.GetWorker(RequiredWorkerId(second));

        Assert.True(reconfigured.IsAccepted);
        Assert.NotNull(firstWorker);
        Assert.NotNull(secondWorker);
        Assert.False(firstWorker.Options.ProfilingEnabled);
        Assert.Equal(WorkStartPolicy.StartAndReturnAfterAccepted, firstWorker.Configuration.Start.Policy);
        Assert.True(secondWorker.Options.ProfilingEnabled);
        Assert.Equal(WorkStartPolicy.DoNotStart, secondWorker.Configuration.Start.Policy);
        Assert.Equal(WorkerState.Queued, secondWorker.State);
    }

    private static Task<WorkExecutionResult> SuccessfulWork(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private static IWorkSystem CreateSystem(WorkDefinition definition)
        => new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, SuccessfulWork))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;

    private static WorkerId RequiredWorkerId(IWorkerHandle handle)
        => handle.WorkerId ?? throw new InvalidOperationException("Expected worker id.");
}
