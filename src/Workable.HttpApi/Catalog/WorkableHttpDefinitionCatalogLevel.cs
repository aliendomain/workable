namespace Workable;

public sealed record WorkableHttpDefinitionCatalogLevel(
    IReadOnlyList<WorkSystemCatalogCategoryItem> Categories,
    IReadOnlyList<WorkDefinition> Definitions);
