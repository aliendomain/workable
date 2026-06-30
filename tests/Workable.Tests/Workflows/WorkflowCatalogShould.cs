using Workable;

namespace Workable.Tests;

[Trait("Category", "Workflows")]
public sealed class WorkflowCatalogShould
{
    [Fact]
    public void ListByCategoryHonorsTheSubcategoryFlag()
    {
        var catalog = new WorkflowCatalog(
        [
            CreateWorkflow("workflow.finance.root", "Finance"),
            CreateWorkflow("workflow.finance.invoice", "Finance:Invoices"),
            CreateWorkflow("workflow.operations.cache", "Operations:Cache"),
        ]);

        var withSubcategories = catalog.ListByCategory("Finance");
        var exactOnly = catalog.ListByCategory("Finance", includeSubcategories: false);

        Assert.Equal(
            ["workflow.finance.root", "workflow.finance.invoice"],
            withSubcategories.Select(definition => definition.Name).ToArray());
        Assert.Equal(
            ["workflow.finance.root"],
            exactOnly.Select(definition => definition.Name).ToArray());
    }

    private static RegisteredWorkflow CreateWorkflow(string name, string category)
        => new(
            WorkflowDefinition.Create(name, category: category),
            [],
            WorkOperateAuthorizationConfiguration.None);
}
