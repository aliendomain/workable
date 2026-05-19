namespace Workable;

public sealed record WorkSystemCapacityConfiguration
{
    public static WorkSystemCapacityConfiguration Default { get; } = new();

    public int MaximumWorkers { get; init; } = 1_000_000;
}
