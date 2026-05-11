namespace Workable;
public readonly record struct WorkSystemId(Guid Value)
{
    public static WorkSystemId New() => new(Guid.NewGuid());

    public override string ToString() => this.Value.ToString("D");
}
