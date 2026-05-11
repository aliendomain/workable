namespace Workable;

public readonly record struct WorkOriginId(Guid Value)
{
    public static WorkOriginId New() => new(Guid.NewGuid());

    public override string ToString() => this.Value.ToString("D");
}
