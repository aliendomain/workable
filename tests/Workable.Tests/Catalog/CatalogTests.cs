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

    private static Task<WorkExecutionResult> SuccessfulWork(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());
}
