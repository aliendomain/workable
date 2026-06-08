namespace Workable;

/// <summary>
/// Represents one lightweight HTTP catalog level containing child categories and direct definitions.
/// </summary>
/// <param name="Categories">The immediate child categories beneath the requested category path.</param>
/// <param name="Definitions">The direct definitions that belong to the requested category path.</param>
public sealed record WorkableHttpDefinitionCatalogLevel(
    IReadOnlyList<WorkSystemCatalogCategoryItem> Categories,
    IReadOnlyList<WorkableHttpDefinitionCatalogItem> Definitions);
