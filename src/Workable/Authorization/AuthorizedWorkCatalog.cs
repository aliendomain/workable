using System.Diagnostics.CodeAnalysis;

namespace Workable;

internal sealed class AuthorizedWorkCatalog(IWorkCatalog inner, WorkAuthorizationEvaluator authorization) : IWorkCatalog
{
    public bool IsFrozen => inner.IsFrozen;

    public IReadOnlyCollection<WorkDefinition> Definitions
        => [.. inner.Definitions.Where(authorization.CanRead)];

    public IReadOnlyList<WorkDefinition> ListByCategory(string category, bool includeSubcategories = true)
        => [.. inner.ListByCategory(category, includeSubcategories)
            .Where(authorization.CanRead)];

    public bool TryGet(WorkDefinitionId id, [NotNullWhen(true)] out WorkDefinition? definition)
    {
        if (inner.TryGet(id, out definition) && authorization.CanRead(definition))
        {
            return true;
        }

        definition = null;
        return false;
    }

    public bool TryGet(string name, [NotNullWhen(true)] out WorkDefinition? definition)
    {
        if (inner.TryGet(name, out var found) && authorization.CanRead(found))
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
        => authorization.CanOperate(definition.DefinitionId)
            ? inner.Reconfigure(definition, changes, cancellationToken)
            : Task.FromResult(WorkDefinitionReconfigurationOutcome.Unauthorized(definition.DefinitionId));
}
