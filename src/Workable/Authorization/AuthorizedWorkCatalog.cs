using System.Diagnostics.CodeAnalysis;

namespace Workable;

internal sealed class AuthorizedWorkCatalog(IWorkCatalog inner, WorkAuthorizationScope scope) : IWorkCatalog
{
    public bool IsFrozen => inner.IsFrozen;

    public IReadOnlyCollection<WorkDefinition> Definitions
        => [.. inner.Definitions.Where(definition => scope.CanRead(definition.Id))];

    public IReadOnlyList<WorkDefinition> ListByCategory(string category, bool includeSubcategories = true)
        => [.. inner.ListByCategory(category, includeSubcategories)
            .Where(definition => scope.CanRead(definition.Id))];

    public bool TryGet(WorkDefinitionId id, [NotNullWhen(true)] out WorkDefinition? definition)
    {
        if (scope.CanRead(id) && inner.TryGet(id, out definition))
        {
            return true;
        }

        definition = null;
        return false;
    }

    public bool TryGet(string name, [NotNullWhen(true)] out WorkDefinition? definition)
    {
        if (inner.TryGet(name, out var found) && scope.CanRead(found.Id))
        {
            definition = found;
            return true;
        }

        definition = null;
        return false;
    }

    public Task<WorkDefinitionReconfigurationOutcome> Reconfigure(
        WorkDefinitionVersion definition,
        WorkDefinitionReconfiguration changes,
        CancellationToken cancellationToken = default)
        => scope.CanOperate(definition.DefinitionId)
            ? inner.Reconfigure(definition, changes, cancellationToken)
            : Task.FromResult(WorkDefinitionReconfigurationOutcome.NotFound(definition.DefinitionId));
}
