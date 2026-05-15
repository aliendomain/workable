namespace Workable;

public sealed record WorkableHttpDefinitionCatalogLevel(
    IReadOnlyList<WorkOverviewCatalogCategoryItem> Categories,
    IReadOnlyList<WorkDefinition> Definitions);
