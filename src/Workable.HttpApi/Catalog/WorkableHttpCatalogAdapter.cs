namespace Workable;

public sealed class WorkableHttpCatalogAdapter
{
    public IReadOnlyList<WorkDefinition> GetDefinitions(IWorkSystemSession session)
        => GetDefinitionsForCatalog(session.Catalog);

    public WorkDefinition? GetDefinition(
        IWorkSystemSession session,
        WorkDefinitionId definitionId)
    {
        ArgumentNullException.ThrowIfNull(session);

        return session.Catalog.TryGet(definitionId, out var definition)
            ? definition
            : null;
    }

    public Task<WorkDefinitionReconfigurationOutcome> ReconfigureDefinition(
        IWorkSystemSession session,
        WorkDefinitionId definitionId,
        WorkableHttpDefinitionReconfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(request);

        return session.Catalog.Reconfigure(
            new WorkDefinitionVersion(definitionId, request.Revision),
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
            definition.Id,
            definition.Name,
            string.IsNullOrWhiteSpace(definition.Category)
                ? WorkDefinitionMetadataDefaults.Category
                : definition.Category);
}
