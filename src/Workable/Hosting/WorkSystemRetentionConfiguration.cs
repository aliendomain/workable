namespace Workable;

public sealed record WorkSystemRetentionConfiguration
{
    public static WorkSystemRetentionConfiguration Default { get; } = new();

    public int MaximumFinalWorkers { get; init; } = 10_000;
}
