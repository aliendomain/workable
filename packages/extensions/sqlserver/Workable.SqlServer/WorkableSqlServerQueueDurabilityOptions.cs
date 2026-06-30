namespace Workable.SqlServer;

public sealed record WorkableSqlServerQueueDurabilityOptions
{
    public const int DefaultEnqueueBatchSize = 64;

    public const int DefaultClaimBatchSize = global::Workable.WorkQueueDurabilityRuntimeOptions.DefaultClaimBatchSize;

    public static readonly TimeSpan DefaultEnqueueBatchWindow = TimeSpan.FromMilliseconds(1);

    public required string ConnectionString { get; init; }

    public string SchemaName { get; init; } = "workable";

    public bool AutoDeploySchema { get; init; } = true;

    public int EnqueueBatchSize { get; init; } = DefaultEnqueueBatchSize;

    public TimeSpan EnqueueBatchWindow { get; init; } = DefaultEnqueueBatchWindow;

    public int ClaimBatchSize { get; init; } = DefaultClaimBatchSize;

    public int RecentClaimSampleCapacity { get; init; }
}
