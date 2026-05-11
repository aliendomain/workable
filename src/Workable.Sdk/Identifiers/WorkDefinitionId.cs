namespace Workable;
public readonly record struct WorkDefinitionId(Guid Value)
{
    public static WorkDefinitionId New() => new(Guid.NewGuid());

    public override string ToString() => this.Value.ToString("D");
}
