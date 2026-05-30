namespace Workable;

public sealed record WorkableHttpDefinitionCatalogLevel(
    IReadOnlyList<WorkSystemCatalogCategoryItem> Categories,
    IReadOnlyList<WorkableHttpDefinitionCatalogItem> Definitions);
