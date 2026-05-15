namespace Workable;

public sealed class WorkableHttpCatalogAdapter
{
    public IReadOnlyList<WorkDefinition> GetDefinitions(IWorkSystem system)
        => GetDefinitionsForSystem(system);

    public Task<WorkDefinitionReconfigurationOutcome> ReconfigureDefinition(
        IWorkSystem system,
        WorkDefinitionId definitionId,
        WorkableHttpDefinitionReconfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(request);

        return system.Catalog.Reconfigure(
            new WorkDefinitionVersion(definitionId, request.Revision),
            request.Changes,
            cancellationToken);
    }

    internal static Task<WorkDefinitionReconfigurationOutcome> ReconfigureDefinitionCore(
        IWorkSystem system,
        WorkDefinitionId definitionId,
        WorkableHttpDefinitionReconfigurationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentNullException.ThrowIfNull(request);

        return new WorkableHttpCatalogAdapter().ReconfigureDefinition(system, definitionId, request, cancellationToken);
    }

    internal static IReadOnlyList<WorkDefinition> GetDefinitionsForSystem(IWorkSystem system)
    {
        ArgumentNullException.ThrowIfNull(system);

        return [.. system.Catalog.Definitions
            .OrderBy(definition => definition.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)];
    }

    internal static WorkableHttpDefinitionCatalogLevel GetDefinitionCatalogLevel(
        IWorkSystem system,
        string? category)
    {
        ArgumentNullException.ThrowIfNull(system);

        string[] pathSegments = string.IsNullOrWhiteSpace(category)
            ? []
            : SplitCategoryPath(category);
        var categories = new Dictionary<string, WorkSystemCatalogCategoryItem>(StringComparer.OrdinalIgnoreCase);
        var directDefinitions = new List<WorkDefinition>();

        foreach (var definition in system.Catalog.Definitions)
        {
            var definitionSegments = SplitCategoryPath(definition.Category);
            if (!StartsWithCategoryPath(definitionSegments, pathSegments))
            {
                continue;
            }

            var remainingSegments = definitionSegments.Skip(pathSegments.Length).ToArray();
            if (remainingSegments.Length == 0)
            {
                directDefinitions.Add(definition);
                continue;
            }

            var childSegments = pathSegments.Append(remainingSegments[0]).ToArray();
            var childPath = string.Join(':', childSegments);
            if (categories.TryGetValue(childPath, out var existing))
            {
                categories[childPath] = existing with { Count = existing.Count + 1 };
            }
            else
            {
                categories[childPath] = new WorkSystemCatalogCategoryItem(
                    remainingSegments[0],
                    childPath,
                    1);
            }
        }

        return new WorkableHttpDefinitionCatalogLevel(
            [.. categories.Values.OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)],
            [.. directDefinitions
                .OrderBy(definition => definition.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)]);
    }

    private static string[] SplitCategoryPath(string? category)
        => (string.IsNullOrWhiteSpace(category)
                ? WorkDefinitionMetadataDefaults.Category
                : category)
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static bool StartsWithCategoryPath(
        string[] categorySegments,
        string[] pathSegments)
        => pathSegments.Length == 0 ||
            pathSegments.Length <= categorySegments.Length &&
            pathSegments
                .Select((segment, index) => string.Equals(
                    categorySegments[index],
                    segment,
                    StringComparison.OrdinalIgnoreCase))
                .All(matches => matches);
}
