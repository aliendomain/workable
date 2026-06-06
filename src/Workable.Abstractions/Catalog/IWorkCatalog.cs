using System.Diagnostics.CodeAnalysis;

namespace Workable;
public interface IWorkCatalog
{
    bool IsFrozen { get; }

    IReadOnlyCollection<WorkDefinition> Definitions { get; }

    IReadOnlyList<WorkDefinition> ListByCategory(string category, bool includeSubcategories = true);

    bool TryGet(string name, [NotNullWhen(true)] out WorkDefinition? definition);

    Task<WorkDefinitionReconfigurationOutcome> Reconfigure(
        WorkDefinitionVersion definition,
        WorkDefinitionReconfiguration changes,
        CancellationToken cancellationToken = default);
}
