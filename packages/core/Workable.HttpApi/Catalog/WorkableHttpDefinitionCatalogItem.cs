namespace Workable;

/// <summary>
/// Represents the lightweight definition row returned by HTTP catalog-level responses.
/// </summary>
/// <param name="Name">The definition name.</param>
/// <param name="Category">The definition category path.</param>
public sealed record WorkableHttpDefinitionCatalogItem(
    string Name,
    string Category);
