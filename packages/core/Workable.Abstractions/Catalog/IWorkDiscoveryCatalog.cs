using System.Diagnostics.CodeAnalysis;

namespace Workable;

/// <summary>
/// Exposes redacted definition metadata that the current caller may discover.
/// </summary>
public interface IWorkDiscoveryCatalog
{
    /// <summary>
    /// Gets all work-definition descriptors discoverable by the current caller.
    /// </summary>
    IReadOnlyCollection<WorkDefinitionDescriptor> Definitions { get; }

    /// <summary>
    /// Lists discoverable definitions in a category.
    /// </summary>
    /// <param name="category">The category path to inspect.</param>
    /// <param name="includeSubcategories"><see langword="true"/> to include descendant categories; otherwise, only exact matches.</param>
    /// <returns>The descriptors that match the requested category scope.</returns>
    IReadOnlyList<WorkDefinitionDescriptor> ListByCategory(
        string category,
        bool includeSubcategories = true);

    /// <summary>
    /// Lists discoverable definitions that permit invocation through the supplied channel.
    /// </summary>
    /// <param name="channel">The invocation channel the consumer intends to use.</param>
    /// <returns>The discoverable descriptors enabled for that channel.</returns>
    IReadOnlyList<WorkDefinitionDescriptor> ListInvocableBy(WorkInvocationChannel channel);

    /// <summary>
    /// Attempts to resolve a discoverable definition descriptor by name.
    /// </summary>
    /// <param name="name">The registered definition name to resolve.</param>
    /// <param name="definition">When this method returns <see langword="true"/>, receives the redacted descriptor.</param>
    /// <returns><see langword="true"/> when the definition exists and is discoverable; otherwise, <see langword="false"/>.</returns>
    bool TryGet(string name, [NotNullWhen(true)] out WorkDefinitionDescriptor? definition);
}
