using System.Diagnostics.CodeAnalysis;

namespace Workable;

internal sealed class SessionWorkCatalog(
    WorkSystemCatalog inner,
    WorkRequestContext requestContext) : IWorkCatalog
{
    public WorkRequestContext RequestContext { get; } = requestContext;

    public bool IsFrozen => inner.IsFrozen;

    public IReadOnlyCollection<WorkDefinition> Definitions => inner.Definitions;

    public IReadOnlyList<WorkDefinition> ListByCategory(string category, bool includeSubcategories = true)
        => inner.ListByCategory(category, includeSubcategories);

    public bool TryGet(WorkDefinitionId id, [NotNullWhen(true)] out WorkDefinition? definition)
        => inner.TryGet(id, out definition);

    public bool TryGet(string name, [NotNullWhen(true)] out WorkDefinition? definition)
        => inner.TryGet(name, out definition);

    public Task<WorkDefinitionReconfigurationOutcome> Reconfigure(
        WorkDefinitionVersion definition,
        WorkDefinitionReconfiguration changes,
        CancellationToken cancellationToken = default)
        => inner.Reconfigure(definition, changes, cancellationToken);
}
