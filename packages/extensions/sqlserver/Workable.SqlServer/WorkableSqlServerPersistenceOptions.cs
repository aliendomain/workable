namespace Workable.SqlServer;

/// <summary>
/// Configures shared SQL Server persistence features that do not require durable queueing.
/// </summary>
public sealed record WorkableSqlServerPersistenceOptions
{
    public required string ConnectionString { get; init; }

    public string SchemaName { get; init; } = "workable";

    public string PersistenceScope { get; init; } = "default";

    public bool AutoDeploySchema { get; init; } = true;
}
