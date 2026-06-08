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
    {
        if (TryGetDefinition(definition.DefinitionId, out var target) &&
            authorization.CanOperate(target))
        {
            return inner.Reconfigure(definition, changes, cancellationToken);
        }

        return TryGetDefinition(definition.DefinitionId, out target)
            ? Task.FromResult(WorkDefinitionReconfigurationOutcome.Unauthorized(target.Name))
            : Task.FromResult(WorkDefinitionReconfigurationOutcome.Unauthorized(definition.DefinitionId.ToString()));
    }

    private bool TryGetDefinition(WorkDefinitionId id, [NotNullWhen(true)] out WorkDefinition? definition)
    {
        definition = inner.Definitions.SingleOrDefault(candidate => candidate.Id == id);
        return definition is not null;
    }
}
