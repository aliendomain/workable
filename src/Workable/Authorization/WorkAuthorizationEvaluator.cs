namespace Workable;

internal sealed class WorkAuthorizationEvaluator(
    IWorkCatalog catalog,
    IReadOnlySet<string> groups,
    WorkSystemAuthorizationEvaluator? systemAuthorization = null)
{
    public bool CanRead(WorkDefinition definition)
        => systemAuthorization?.HasReadAllWorkAccess() == true
            || definition.Authorization.CanRead(groups);

    public bool CanRead(WorkDefinitionId definitionId)
    {
        if (!this.TryGet(definitionId, out var definition))
        {
            return false;
        }

        return this.CanRead(definition);
    }

    public bool CanOperate(WorkDefinition definition)
        => systemAuthorization?.HasOperateAllWorkAccess() == true
            || definition.Authorization.CanOperate(groups);

    public bool CanOperate(WorkDefinitionId definitionId)
    {
        if (!this.TryGet(definitionId, out var definition))
        {
            return false;
        }

        return this.CanOperate(definition);
    }

    public IReadOnlySet<WorkDefinitionId> ReadableDefinitionIds()
        => catalog.Definitions
            .Where(this.CanRead)
            .Select(definition => definition.Id)
            .ToHashSet();

    private bool TryGet(WorkDefinitionId definitionId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorkDefinition? definition)
        => catalog.TryGet(definitionId, out definition);
}
