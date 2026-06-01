using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "HttpApi")]
public sealed class WorkableHttpCatalogAdapterShould
{
    [Fact]
    public void RejectNullCatalogWhenReadingDefinitionsDirectly()
    {
        var exception = Assert.Throws<ArgumentNullException>(
            () => WorkableHttpCatalogAdapter.GetDefinitionsForCatalog(null!));

        Assert.Equal("catalog", exception.ParamName);
    }

    [Fact]
    public void SortDefinitionsByCategoryThenNameIgnoringCase()
    {
        var catalog = Catalog(
            WorkDefinition.Create("beta", category: "Operations"),
            WorkDefinition.Create("alpha", category: "operations"),
            WorkDefinition.Create("invoice", category: "Finance"));

        var definitions = WorkableHttpCatalogAdapter.GetDefinitionsForCatalog(catalog);

        Assert.Equal(
            ["invoice", "alpha", "beta"],
            definitions.Select(definition => definition.Name).ToArray());
    }

    [Fact]
    public void ReturnRootCatalogLevelWithChildCategories()
    {
        var general = WorkDefinition.Create("general.quick", category: null);
        var billing = WorkDefinition.Create("billing.invoice", category: "Operations:Billing");
        var shipping = WorkDefinition.Create("shipping.pack", category: "Operations:Shipping");
        var catalog = Catalog(general, shipping, billing);

        var level = WorkableHttpCatalogAdapter.GetDefinitionCatalogLevel(catalog, category: null);

        Assert.Equal(["General", "Operations"], level.Categories.Select(category => category.Label).ToArray());
        Assert.Equal([1, 2], level.Categories.Select(category => category.Count).ToArray());
        Assert.Empty(level.Definitions);
    }

    [Fact]
    public void ReturnDefaultCategoryDefinitionsForGeneralPath()
    {
        var general = WorkDefinition.Create("general.quick", category: null);
        var catalog = Catalog(
            WorkDefinition.Create("operations.root", category: "Operations"),
            general);

        var level = WorkableHttpCatalogAdapter.GetDefinitionCatalogLevel(catalog, WorkDefinitionMetadataDefaults.Category);

        Assert.Empty(level.Categories);
        Assert.Equal(
            [general.Id],
            level.Definitions.Select(definition => definition.Id).ToArray());
        Assert.Equal(
            [WorkDefinitionMetadataDefaults.Category],
            level.Definitions.Select(definition => definition.Category).ToArray());
    }

    [Fact]
    public void ReturnNestedCatalogLevelForCaseInsensitiveCategoryPath()
    {
        var operations = WorkDefinition.Create("operations.root", category: "Operations");
        var invoice = WorkDefinition.Create("invoice.send", category: "Operations:Billing");
        var reconcile = WorkDefinition.Create("billing.reconcile", category: "operations:billing");
        var shipping = WorkDefinition.Create("shipping.pack", category: "Operations:Shipping");
        var catalog = Catalog(operations, shipping, reconcile, invoice);

        var level = WorkableHttpCatalogAdapter.GetDefinitionCatalogLevel(catalog, "OPERATIONS:billing");

        Assert.Empty(level.Categories);
        Assert.Equal(
            ["billing.reconcile", "invoice.send"],
            level.Definitions.Select(definition => definition.Name).ToArray());
        Assert.All(
            level.Definitions,
            definition => Assert.Equal(
                0,
                string.Compare(definition.Category, "Operations:Billing", StringComparison.OrdinalIgnoreCase)));
    }

    private static IWorkCatalog Catalog(params WorkDefinition[] definitions)
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            foreach (var definition in definitions)
            {
                builder.AddWork(definition, SuccessfulWork);
            }
        });

        return services
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default
            .Catalog;
    }

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());
}
