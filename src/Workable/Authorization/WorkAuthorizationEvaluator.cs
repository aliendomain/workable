namespace Workable;

internal sealed class WorkAuthorizationEvaluator(
    IWorkCatalog catalog,
    IReadOnlySet<string> groups,
    bool isKnownAuthenticatedUser,
    WorkSystemAuthorizationEvaluator? systemAuthorization = null)
{
    public bool CanRead(WorkDefinition definition)
        => systemAuthorization?.HasReadAllWorkAccess() == true
            || definition.Authorization.CanRead(groups, isKnownAuthenticatedUser);

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
            || definition.Authorization.CanOperate(groups, isKnownAuthenticatedUser);

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
        => this.ReadableDefinitions()
            .Select(definition => definition.Id)
            .ToHashSet();

    public IReadOnlySet<WorkDefinitionId> OperableDefinitionIds()
        => this.OperableDefinitions()
            .Select(definition => definition.Id)
            .ToHashSet();

    public IReadOnlyList<WorkDefinition> ReadableDefinitions()
        => [.. catalog.Definitions.Where(this.CanRead)];

    public IReadOnlyList<WorkDefinition> OperableDefinitions()
        => [.. catalog.Definitions.Where(this.CanOperate)];

    private bool TryGet(WorkDefinitionId definitionId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorkDefinition? definition)
    {
        definition = catalog.Definitions.SingleOrDefault(candidate => candidate.Id == definitionId);
        return definition is not null;
    }
}
