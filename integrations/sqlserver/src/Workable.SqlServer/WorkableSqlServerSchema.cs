using System.Linq;
using Microsoft.Data.SqlClient;

namespace Workable.SqlServer;

public static class WorkableSqlServerSchema
{
    private const int SchemaVersion = 1;
    private const string RequiredSetOptions = """
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
""";

    public static string GenerateScript(string schemaName = "workable")
        => string.Join(
            $"{Environment.NewLine}GO{Environment.NewLine}{Environment.NewLine}",
            CreateBatches(schemaName));

    public static IReadOnlyList<string> CreateBatches(string schemaName = "workable")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);

        var schema = QuoteIdentifier(schemaName);
        var entriesTable = $"{schema}.[WorkEntries]";
        var versionTable = $"{schema}.[SchemaVersion]";
        var escapedSchemaName = EscapeLiteral(schemaName);

        return
        [
            RequiredSetOptions,
            $"IF SCHEMA_ID(N'{escapedSchemaName}') IS NULL EXEC(N'CREATE SCHEMA {schema}')",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.SchemaVersion', N'U') IS NULL
BEGIN
    CREATE TABLE {versionTable}
    (
        Component nvarchar(128) NOT NULL CONSTRAINT PK_WorkableSchemaVersion PRIMARY KEY,
        Version int NOT NULL,
        UpdatedAt datetimeoffset NOT NULL
    );
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkEntries', N'U') IS NULL
BEGIN
    CREATE TABLE {entriesTable}
    (
        WorkerId uniqueidentifier NOT NULL CONSTRAINT PK_WorkableWorkEntries PRIMARY KEY,
        WorkSystemName nvarchar(256) NOT NULL,
        DefinitionName nvarchar(450) NOT NULL,
        IsDurableQueued bit NOT NULL CONSTRAINT DF_WorkableWorkEntries_IsDurableQueued DEFAULT (0),
        HasIdempotencyReservation bit NOT NULL CONSTRAINT DF_WorkableWorkEntries_HasIdempotencyReservation DEFAULT (0),
        SubjectType nvarchar(256) NULL,
        SubjectValue nvarchar(450) NULL,
        ConcurrencyType nvarchar(256) NULL,
        ConcurrencyValue nvarchar(450) NULL,
        InputJson nvarchar(max) NULL,
        OptionsJson nvarchar(max) NULL,
        ConfigurationJson nvarchar(max) NULL,
        OriginJson nvarchar(max) NOT NULL,
        CreatedAt datetimeoffset NOT NULL,
        ClaimedBy nvarchar(450) NULL,
        ClaimedAt datetimeoffset NULL,
        LeaseId nvarchar(64) NULL,
        LeaseExpiresAt datetimeoffset NULL,
        ConcurrencyBucket nvarchar(32) NULL
    );

    CREATE INDEX IX_WorkableWorkEntries_Ready
        ON {entriesTable} (WorkSystemName, LeaseExpiresAt, CreatedAt, WorkerId)
        WHERE IsDurableQueued = 1;

    CREATE INDEX IX_WorkableWorkEntries_Concurrency
        ON {entriesTable} (WorkSystemName, DefinitionName, ConcurrencyBucket, LeaseExpiresAt, SubjectType, SubjectValue, ConcurrencyType, ConcurrencyValue)
        WHERE ConcurrencyBucket IS NOT NULL;

    CREATE UNIQUE INDEX UX_WorkableWorkEntries_Idempotency
        ON {entriesTable} (WorkSystemName, DefinitionName, SubjectType, SubjectValue)
        WHERE HasIdempotencyReservation = 1 AND SubjectType IS NOT NULL AND SubjectValue IS NOT NULL;
END
""",
            $"""
MERGE {versionTable} WITH (HOLDLOCK) AS target
USING (SELECT N'QueueDurability' AS Component, {SchemaVersion} AS Version) AS source
ON target.Component = source.Component
WHEN MATCHED THEN UPDATE SET Version = source.Version, UpdatedAt = SYSDATETIMEOFFSET()
WHEN NOT MATCHED THEN INSERT (Component, Version, UpdatedAt) VALUES (source.Component, source.Version, SYSDATETIMEOFFSET());
""",
        ];
    }

    public static async Task Apply(
        string connectionString,
        string schemaName = "workable",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        foreach (var batch in CreateBatches(schemaName))
        {
            await using var command = connection.CreateCommand();
            command.CommandText = batch;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public static async Task ValidateInstalled(
        string connectionString,
        string schemaName = "workable",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var missing = new List<string>();
        var requiredColumns = new[]
        {
            "WorkerId",
            "WorkSystemName",
            "DefinitionName",
            "IsDurableQueued",
            "HasIdempotencyReservation",
            "SubjectType",
            "SubjectValue",
            "ConcurrencyType",
            "ConcurrencyValue",
            "InputJson",
            "OptionsJson",
            "ConfigurationJson",
            "OriginJson",
            "CreatedAt",
            "ClaimedBy",
            "ClaimedAt",
            "LeaseId",
            "LeaseExpiresAt",
            "ConcurrencyBucket",
        };

        if (await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.tables tables
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = @SchemaName AND tables.name = N'WorkEntries';
""", schemaName, cancellationToken) == 0)
        {
            missing.Add($"{schemaName}.WorkEntries");
        }

        if (await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.tables tables
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = @SchemaName AND tables.name = N'SchemaVersion';
""", schemaName, cancellationToken) == 0)
        {
            missing.Add($"{schemaName}.SchemaVersion");
        }

        var existingColumns = await ReadExistingColumns(connection, schemaName, cancellationToken);
        foreach (var column in requiredColumns.Where(column => !existingColumns.Contains(column)))
        {
            missing.Add($"{schemaName}.WorkEntries.{column}");
        }

        if (await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = @SchemaName
  AND tables.name = N'WorkEntries'
  AND indexes.name = N'IX_WorkableWorkEntries_Ready';
""", schemaName, cancellationToken) == 0)
        {
            missing.Add("IX_WorkableWorkEntries_Ready");
        }

        if (await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = @SchemaName
  AND tables.name = N'WorkEntries'
  AND indexes.name = N'IX_WorkableWorkEntries_Concurrency';
""", schemaName, cancellationToken) == 0)
        {
            missing.Add("IX_WorkableWorkEntries_Concurrency");
        }

        if (await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = @SchemaName
  AND tables.name = N'WorkEntries'
  AND indexes.name = N'UX_WorkableWorkEntries_Idempotency'
  AND indexes.filter_definition LIKE N'%HasIdempotencyReservation%';
""", schemaName, cancellationToken) == 0)
        {
            missing.Add("UX_WorkableWorkEntries_Idempotency");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Workable SQL Server schema '{schemaName}' is not installed or is incomplete. Missing: {string.Join(", ", missing)}.");
        }
    }

    internal static string QuoteIdentifier(string identifier)
        => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string EscapeLiteral(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private static async Task<T> Scalar<T>(
        SqlConnection connection,
        string commandText,
        string schemaName,
        CancellationToken cancellationToken,
        string? name = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("@SchemaName", schemaName);
        if (name is not null)
        {
            command.Parameters.AddWithValue("@Name", name);
        }

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null or DBNull)
        {
            throw new InvalidOperationException("Expected SQL scalar query to return a value.");
        }

        return (T)Convert.ChangeType(value, typeof(T));
    }

    private static async Task<HashSet<string>> ReadExistingColumns(
        SqlConnection connection,
        string schemaName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT columns.name
FROM sys.columns columns
INNER JOIN sys.tables tables ON tables.object_id = columns.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = @SchemaName
  AND tables.name = N'WorkEntries';
""";
        command.Parameters.AddWithValue("@SchemaName", schemaName);

        var columns = new HashSet<string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }
}

public sealed class WorkableSqlServerSchemaDeploymentException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
