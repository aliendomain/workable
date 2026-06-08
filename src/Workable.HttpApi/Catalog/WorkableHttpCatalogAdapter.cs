namespace Workable;

/// <summary>
/// Adapts catalog and definition-reconfiguration operations to the HTTP API surface.
/// </summary>
public sealed class WorkableHttpCatalogAdapter
{
    /// <summary>
    /// Gets the visible definitions for the current session in stable catalog order.
    /// </summary>
    /// <param name="session">The authorized session whose catalog should be read.</param>
    /// <returns>The definitions visible to the caller.</returns>
    public IReadOnlyList<WorkDefinition> GetDefinitions(IWorkSystemSession session)
        => GetDefinitionsForCatalog(session.Catalog);

    /// <summary>
    /// Gets one visible definition by name.
    /// </summary>
    /// <param name="session">The authorized session whose catalog should be read.</param>
    /// <param name="name">The definition name to resolve.</param>
    /// <returns>The matching definition, or <see langword="null"/> when it is not visible to the caller.</returns>
    public WorkDefinition? GetDefinition(
        IWorkSystemSession session,
        string name)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return session.Catalog.TryGet(name, out var definition)
            ? definition
            : null;
    }

    /// <summary>
    /// Applies definition-default reconfiguration through the selected session.
    /// </summary>
    /// <param name="session">The authorized session that owns the target catalog.</param>
    /// <param name="name">The definition name to reconfigure.</param>
    /// <param name="request">The HTTP reconfiguration request payload.</param>
    /// <param name="cancellationToken">A token that cancels the reconfiguration operation.</param>
    /// <returns>The outcome of the definition reconfiguration request.</returns>
    public Task<WorkDefinitionReconfigurationOutcome> ReconfigureDefinition(
        IWorkSystemSession session,
        string name,
        WorkableHttpDefinitionReconfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!session.Catalog.TryGet(name, out var definition))
        {
            return Task.FromResult(WorkDefinitionReconfigurationOutcome.NotFound(name));
        }

        return session.Catalog.Reconfigure(
            new WorkDefinitionVersion(definition.Id, request.Revision),
            request.Changes,
            cancellationToken);
    }

    internal static IReadOnlyList<WorkDefinition> GetDefinitionsForCatalog(IWorkCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        return [.. catalog.Definitions
            .OrderBy(definition => definition.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)];
    }

    internal static WorkableHttpDefinitionCatalogLevel GetDefinitionCatalogLevel(
        IWorkCatalog catalog,
        string? category)
    {
        ArgumentNullException.ThrowIfNull(catalog);

        string[] pathSegments = string.IsNullOrWhiteSpace(category)
            ? []
            : (string.IsNullOrWhiteSpace(category)
                ? WorkDefinitionMetadataDefaults.Category
                : category)
                .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var categories = new Dictionary<string, WorkSystemCatalogCategoryItem>(StringComparer.OrdinalIgnoreCase);
        var directDefinitions = new List<WorkableHttpDefinitionCatalogItem>();

        foreach (var definition in catalog.Definitions)
        {
            var definitionSegments = (string.IsNullOrWhiteSpace(definition.Category)
                    ? WorkDefinitionMetadataDefaults.Category
                    : definition.Category)
                .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var matchesPath = pathSegments.Length == 0 ||
                pathSegments.Length <= definitionSegments.Length &&
                pathSegments
                    .Select((segment, index) => string.Equals(
                        definitionSegments[index],
                        segment,
                        StringComparison.OrdinalIgnoreCase))
                    .All(matches => matches);
            if (!matchesPath)
            {
                continue;
            }

            var remainingSegments = definitionSegments.Skip(pathSegments.Length).ToArray();
            if (remainingSegments.Length == 0)
            {
                directDefinitions.Add(CreateDefinitionCatalogItem(definition));
                continue;
            }

            var childSegments = pathSegments.Append(remainingSegments[0]).ToArray();
            var childPath = string.Join(':', childSegments);
            categories[childPath] = categories.TryGetValue(childPath, out var existing)
                ? existing with { Count = existing.Count + 1 }
                : new WorkSystemCatalogCategoryItem(
                    remainingSegments[0],
                    childPath,
                    1);
        }

        return new WorkableHttpDefinitionCatalogLevel(
            [.. categories.Values.OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)],
            [.. directDefinitions
                .OrderBy(definition => definition.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    private static WorkableHttpDefinitionCatalogItem CreateDefinitionCatalogItem(WorkDefinition definition)
        => new(
            definition.Name,
            string.IsNullOrWhiteSpace(definition.Category)
                ? WorkDefinitionMetadataDefaults.Category
                : definition.Category);
}
