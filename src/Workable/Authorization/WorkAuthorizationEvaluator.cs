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

    public bool HasReadAllWorkAccess()
        => systemAuthorization?.HasReadAllWorkAccess() == true ||
            this.ReadableDefinitionIds().Count == catalog.Definitions.Count;

    public bool HasOperateAllWorkAccess()
        => systemAuthorization?.HasOperateAllWorkAccess() == true ||
            this.OperableDefinitionIds().Count == catalog.Definitions.Count;

    public IReadOnlySet<WorkDefinitionId> ReadableDefinitionIds()
        => catalog.Definitions
            .Where(this.CanRead)
            .Select(definition => definition.Id)
            .ToHashSet();

    public IReadOnlySet<WorkDefinitionId> OperableDefinitionIds()
        => catalog.Definitions
            .Where(this.CanOperate)
            .Select(definition => definition.Id)
            .ToHashSet();

    private bool TryGet(WorkDefinitionId definitionId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorkDefinition? definition)
        => catalog.TryGet(definitionId, out definition);
}
