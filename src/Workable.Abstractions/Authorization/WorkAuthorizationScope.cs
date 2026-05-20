namespace Workable;

public sealed record WorkAuthorizationScope(
    IReadOnlySet<WorkDefinitionId> ReadDefinitionIds,
    IReadOnlySet<WorkDefinitionId> OperateDefinitionIds)
{
    public static WorkAuthorizationScope Empty { get; } = new(
        new HashSet<WorkDefinitionId>(),
        new HashSet<WorkDefinitionId>());

    public static WorkAuthorizationScope Create(
        IEnumerable<WorkDefinitionId>? readDefinitionIds = null,
        IEnumerable<WorkDefinitionId>? operateDefinitionIds = null)
        => new(
            ToSet(readDefinitionIds),
            ToSet(operateDefinitionIds));

    public bool CanRead(WorkDefinitionId definitionId)
        => this.ReadDefinitionIds.Contains(definitionId);

    public bool CanOperate(WorkDefinitionId definitionId)
        => this.OperateDefinitionIds.Contains(definitionId);

    private static HashSet<WorkDefinitionId> ToSet(IEnumerable<WorkDefinitionId>? definitionIds)
        => definitionIds is null
            ? []
            : [.. definitionIds];
}
