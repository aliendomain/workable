namespace Workable.SqlServer;

public sealed record WorkableSqlServerQueueDurabilityOptions
{
    public required string ConnectionString { get; init; }

    public string SchemaName { get; init; } = "workable";

    public bool AutoDeploySchema { get; init; } = true;
}
