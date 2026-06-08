using System.Diagnostics.CodeAnalysis;

namespace Workable;

/// <summary>
/// Exposes the registered work definitions for a system.
/// </summary>
public interface IWorkCatalog
{
    /// <summary>
    /// Gets a value indicating whether the catalog has been frozen against further definition changes.
    /// </summary>
    bool IsFrozen { get; }

    /// <summary>
    /// Gets all registered definitions in the catalog.
    /// </summary>
    IReadOnlyCollection<WorkDefinition> Definitions { get; }

    /// <summary>
    /// Lists definitions in a category.
    /// </summary>
    /// <param name="category">The category path to inspect.</param>
    /// <param name="includeSubcategories"><see langword="true"/> to include descendant categories; otherwise, only exact matches.</param>
    /// <returns>The definitions that match the requested category scope.</returns>
    IReadOnlyList<WorkDefinition> ListByCategory(string category, bool includeSubcategories = true);

    /// <summary>
    /// Attempts to resolve a definition by name.
    /// </summary>
    /// <param name="name">The registered definition name to resolve.</param>
    /// <param name="definition">When this method returns <see langword="true"/>, receives the resolved definition.</param>
    /// <returns><see langword="true"/> when a definition with the requested name exists; otherwise <see langword="false"/>.</returns>
    bool TryGet(string name, [NotNullWhen(true)] out WorkDefinition? definition);

    /// <summary>
    /// Applies runtime reconfiguration to a registered definition.
    /// </summary>
    /// <param name="definition">The definition version to reconfigure.</param>
    /// <param name="changes">The requested configuration changes.</param>
    /// <param name="cancellationToken">A token that cancels the reconfiguration operation.</param>
    /// <returns>The outcome of the reconfiguration request.</returns>
    Task<WorkDefinitionReconfigurationOutcome> Reconfigure(
        WorkDefinitionVersion definition,
        WorkDefinitionReconfiguration changes,
        CancellationToken cancellationToken = default);
}
