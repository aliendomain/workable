using System.Diagnostics.CodeAnalysis;

namespace Workable;
public interface IWorkCatalog
{
    bool IsFrozen { get; }

    IReadOnlyCollection<WorkDefinition> Definitions { get; }

    IReadOnlyList<WorkDefinition> ListByCategory(string category, bool includeSubcategories = true);

    bool TryGet(WorkDefinitionId id, [NotNullWhen(true)] out WorkDefinition? definition);

    bool TryGet(string name, [NotNullWhen(true)] out WorkDefinition? definition);
}
