using System.Linq;
using Microsoft.Data.SqlClient;

namespace Workable.SqlServer;

public static class WorkableSqlServerSchema
{
    private const int SchemaVersion = 3;
    private const int WorkflowSchemaVersion = 3;
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
        var queueTable = $"{schema}.[WorkQueueEntries]";
        var workflowRunsTable = $"{schema}.[WorkflowRuns]";
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
        HasPersistentConcurrency bit NOT NULL CONSTRAINT DF_WorkableWorkEntries_HasPersistentConcurrency DEFAULT (0),
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
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkQueueEntries', N'U') IS NULL
BEGIN
    CREATE TABLE {queueTable}
    (
        WorkerId uniqueidentifier NOT NULL CONSTRAINT PK_WorkableWorkQueueEntries PRIMARY KEY,
        WorkSystemName nvarchar(256) NOT NULL,
        DefinitionName nvarchar(450) NOT NULL,
        HasPersistentConcurrency bit NOT NULL CONSTRAINT DF_WorkableWorkQueueEntries_HasPersistentConcurrency DEFAULT (0),
        ConcurrencyScope nvarchar(64) NULL,
        ConcurrencyMaximumCapacity int NULL,
        SubjectType nvarchar(256) NULL,
        SubjectValue nvarchar(450) NULL,
        ConcurrencyType nvarchar(256) NULL,
        ConcurrencyValue nvarchar(450) NULL,
        CreatedAt datetimeoffset NOT NULL,
        ClaimedBy nvarchar(450) NULL,
        ClaimedAt datetimeoffset NULL,
        LeaseId nvarchar(64) NULL,
        LeaseExpiresAt datetimeoffset NULL,
        ConcurrencyBucket nvarchar(32) NULL
    );
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkflowRuns', N'U') IS NULL
BEGIN
    CREATE TABLE {workflowRunsTable}
    (
        RunId uniqueidentifier NOT NULL CONSTRAINT PK_WorkableWorkflowRuns PRIMARY KEY,
        PersistenceScope nvarchar(450) NOT NULL,
        WorkSystemName nvarchar(256) NULL,
        DefinitionId uniqueidentifier NOT NULL,
        DefinitionRevision bigint NOT NULL,
        DefinitionName nvarchar(450) NOT NULL,
        DefinitionFingerprint nvarchar(64) NOT NULL CONSTRAINT DF_WorkableWorkflowRuns_DefinitionFingerprint DEFAULT (N''),
        RequestContextJson nvarchar(max) NOT NULL,
        Status nvarchar(64) NOT NULL,
        StepsJson nvarchar(max) NOT NULL,
        MessagesJson nvarchar(max) NOT NULL,
        PendingControlAction nvarchar(32) NULL,
        CreatedAt datetimeoffset NOT NULL,
        StartedAt datetimeoffset NULL,
        CompletedAt datetimeoffset NULL,
        UpdatedAt datetimeoffset NOT NULL
    );
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkflowRuns', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkflowRuns', N'DefinitionFingerprint') IS NULL
BEGIN
    ALTER TABLE {workflowRunsTable}
        ADD DefinitionFingerprint nvarchar(64) NOT NULL
            CONSTRAINT DF_WorkableWorkflowRuns_DefinitionFingerprint DEFAULT (N'');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkflowRuns', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkflowRuns', N'PendingControlAction') IS NULL
BEGIN
    ALTER TABLE {workflowRunsTable}
        ADD PendingControlAction nvarchar(32) NULL;
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkflowRuns', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkflowRuns', N'WorkSystemId') IS NOT NULL
BEGIN
    ALTER TABLE {workflowRunsTable}
        DROP COLUMN WorkSystemId;
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkEntries', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkEntries', N'HasPersistentConcurrency') IS NULL
BEGIN
    ALTER TABLE {entriesTable}
        ADD HasPersistentConcurrency bit NOT NULL
            CONSTRAINT DF_WorkableWorkEntries_HasPersistentConcurrency DEFAULT (0);

    IF COL_LENGTH(N'{escapedSchemaName}.WorkEntries', N'IsDurableQueued') IS NOT NULL
       AND COL_LENGTH(N'{escapedSchemaName}.WorkEntries', N'ConfigurationJson') IS NOT NULL
    BEGIN
        EXEC(N'UPDATE {entriesTable}
        SET HasPersistentConcurrency = CASE
            WHEN JSON_VALUE(ConfigurationJson, ''$.coordination.concurrency.isEnabled'') = ''true''
             AND JSON_VALUE(ConfigurationJson, ''$.coordination.storage'') = ''Persistent''
            THEN 1
            ELSE 0
        END
        WHERE IsDurableQueued = 1;');
    END
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkQueueEntries', N'U') IS NOT NULL
   AND OBJECT_ID(N'{escapedSchemaName}.WorkEntries', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkEntries', N'IsDurableQueued') IS NOT NULL
BEGIN
    EXEC(N'
    INSERT INTO {queueTable}
    (
        WorkerId,
        WorkSystemName,
        DefinitionName,
        HasPersistentConcurrency,
        ConcurrencyScope,
        ConcurrencyMaximumCapacity,
        SubjectType,
        SubjectValue,
        ConcurrencyType,
        ConcurrencyValue,
        CreatedAt,
        ClaimedBy,
        ClaimedAt,
        LeaseId,
        LeaseExpiresAt,
        ConcurrencyBucket
    )
    SELECT entries.WorkerId,
           entries.WorkSystemName,
           entries.DefinitionName,
           entries.HasPersistentConcurrency,
           CASE
               WHEN entries.HasPersistentConcurrency = 1
               THEN JSON_VALUE(entries.ConfigurationJson, ''$.coordination.concurrency.scope'')
               ELSE NULL
           END,
           CASE
               WHEN entries.HasPersistentConcurrency = 1
               THEN TRY_CONVERT(int, JSON_VALUE(entries.ConfigurationJson, ''$.coordination.concurrency.maximumCapacity''))
               ELSE NULL
           END,
           entries.SubjectType,
           entries.SubjectValue,
           entries.ConcurrencyType,
           entries.ConcurrencyValue,
           entries.CreatedAt,
           entries.ClaimedBy,
           entries.ClaimedAt,
           entries.LeaseId,
           entries.LeaseExpiresAt,
           entries.ConcurrencyBucket
    FROM {entriesTable} entries
    WHERE entries.IsDurableQueued = 1
      AND NOT EXISTS
      (
          SELECT 1
          FROM {queueTable} queue
          WHERE queue.WorkerId = entries.WorkerId
      );

    UPDATE {entriesTable}
    SET IsDurableQueued = 0,
        ClaimedBy = NULL,
        ClaimedAt = NULL,
        LeaseId = NULL,
        LeaseExpiresAt = NULL,
        ConcurrencyBucket = NULL
    WHERE IsDurableQueued = 1;');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkEntries', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkEntries', N'IsDurableQueued') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkEntries'
         AND indexes.name = N'IX_WorkableWorkEntries_Ready')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkEntries_Ready ON {entriesTable} (WorkSystemName, LeaseExpiresAt, CreatedAt, WorkerId) WHERE IsDurableQueued = 1;');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkEntries', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkEntries', N'HasPersistentConcurrency') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkEntries', N'IsDurableQueued') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkEntries'
         AND indexes.name = N'IX_WorkableWorkEntries_PersistentConcurrencyReady')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkEntries_PersistentConcurrencyReady ON {entriesTable} (WorkSystemName, LeaseExpiresAt, CreatedAt, WorkerId) WHERE IsDurableQueued = 1 AND HasPersistentConcurrency = 1;');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkEntries', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkEntries', N'ConcurrencyBucket') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkEntries'
         AND indexes.name = N'IX_WorkableWorkEntries_Concurrency')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkEntries_Concurrency ON {entriesTable} (WorkSystemName, DefinitionName, ConcurrencyBucket, LeaseExpiresAt, SubjectType, SubjectValue, ConcurrencyType, ConcurrencyValue) WHERE ConcurrencyBucket IS NOT NULL;');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkEntries', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkEntries', N'HasIdempotencyReservation') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkEntries'
         AND indexes.name = N'UX_WorkableWorkEntries_Idempotency')
BEGIN
    EXEC(N'CREATE UNIQUE INDEX UX_WorkableWorkEntries_Idempotency ON {entriesTable} (WorkSystemName, DefinitionName, SubjectType, SubjectValue) WHERE HasIdempotencyReservation = 1 AND SubjectType IS NOT NULL AND SubjectValue IS NOT NULL;');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkQueueEntries', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkQueueEntries'
         AND indexes.name = N'IX_WorkableWorkQueueEntries_Ready')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkQueueEntries_Ready ON {queueTable} (WorkSystemName, DefinitionName, CreatedAt, WorkerId) INCLUDE (LeaseExpiresAt, HasPersistentConcurrency);');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkQueueEntries', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkQueueEntries'
         AND indexes.name = N'IX_WorkableWorkQueueEntries_PersistentConcurrencyReady')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkQueueEntries_PersistentConcurrencyReady ON {queueTable} (WorkSystemName, DefinitionName, CreatedAt, WorkerId) INCLUDE (LeaseExpiresAt, HasPersistentConcurrency, ConcurrencyScope, ConcurrencyMaximumCapacity, SubjectType, SubjectValue, ConcurrencyType, ConcurrencyValue) WHERE HasPersistentConcurrency = 1;');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkQueueEntries', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkQueueEntries'
         AND indexes.name = N'IX_WorkableWorkQueueEntries_Concurrency')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkQueueEntries_Concurrency ON {queueTable} (WorkSystemName, DefinitionName, ConcurrencyBucket, LeaseExpiresAt, SubjectType, SubjectValue, ConcurrencyType, ConcurrencyValue) WHERE ConcurrencyBucket IS NOT NULL;');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkflowRuns', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkflowRuns'
         AND indexes.name = N'IX_WorkableWorkflowRuns_Recovery')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkflowRuns_Recovery ON {workflowRunsTable} (PersistenceScope, Status, CreatedAt, RunId);');
END
""",
            $"""
MERGE {versionTable} WITH (HOLDLOCK) AS target
USING (SELECT N'QueueDurability' AS Component, {SchemaVersion} AS Version) AS source
ON target.Component = source.Component
WHEN MATCHED THEN UPDATE SET Version = source.Version, UpdatedAt = SYSDATETIMEOFFSET()
WHEN NOT MATCHED THEN INSERT (Component, Version, UpdatedAt) VALUES (source.Component, source.Version, SYSDATETIMEOFFSET());
""",
            $"""
MERGE {versionTable} WITH (HOLDLOCK) AS target
USING (SELECT N'WorkflowPersistence' AS Component, {WorkflowSchemaVersion} AS Version) AS source
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
            "HasPersistentConcurrency",
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
        var requiredQueueColumns = new[]
        {
            "WorkerId",
            "WorkSystemName",
            "DefinitionName",
            "HasPersistentConcurrency",
            "ConcurrencyScope",
            "ConcurrencyMaximumCapacity",
            "SubjectType",
            "SubjectValue",
            "ConcurrencyType",
            "ConcurrencyValue",
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
WHERE schemas.name = @SchemaName AND tables.name = N'WorkQueueEntries';
""", schemaName, cancellationToken) == 0)
        {
            missing.Add($"{schemaName}.WorkQueueEntries");
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

        var existingQueueColumns = await ReadExistingColumns(connection, schemaName, "WorkQueueEntries", cancellationToken);
        foreach (var column in requiredQueueColumns.Where(column => !existingQueueColumns.Contains(column)))
        {
            missing.Add($"{schemaName}.WorkQueueEntries.{column}");
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
  AND indexes.name = N'IX_WorkableWorkEntries_PersistentConcurrencyReady';
""", schemaName, cancellationToken) == 0)
        {
            missing.Add("IX_WorkableWorkEntries_PersistentConcurrencyReady");
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

        if (await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = @SchemaName
  AND tables.name = N'WorkQueueEntries'
  AND indexes.name = N'IX_WorkableWorkQueueEntries_Ready';
""", schemaName, cancellationToken) == 0)
        {
            missing.Add("IX_WorkableWorkQueueEntries_Ready");
        }

        if (await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = @SchemaName
  AND tables.name = N'WorkQueueEntries'
  AND indexes.name = N'IX_WorkableWorkQueueEntries_PersistentConcurrencyReady';
""", schemaName, cancellationToken) == 0)
        {
            missing.Add("IX_WorkableWorkQueueEntries_PersistentConcurrencyReady");
        }

        if (await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = @SchemaName
  AND tables.name = N'WorkQueueEntries'
  AND indexes.name = N'IX_WorkableWorkQueueEntries_Concurrency';
""", schemaName, cancellationToken) == 0)
        {
            missing.Add("IX_WorkableWorkQueueEntries_Concurrency");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Workable SQL Server schema '{schemaName}' is not installed or is incomplete. Missing: {string.Join(", ", missing)}.");
        }
    }

    public static async Task ValidateWorkflowPersistenceInstalled(
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
            "RunId",
            "PersistenceScope",
            "WorkSystemName",
            "DefinitionId",
            "DefinitionRevision",
            "DefinitionName",
            "DefinitionFingerprint",
            "RequestContextJson",
            "Status",
            "StepsJson",
            "MessagesJson",
            "PendingControlAction",
            "CreatedAt",
            "StartedAt",
            "CompletedAt",
            "UpdatedAt",
        };

        if (await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.tables tables
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = @SchemaName AND tables.name = N'WorkflowRuns';
""", schemaName, cancellationToken) == 0)
        {
            missing.Add($"{schemaName}.WorkflowRuns");
        }

        var existingColumns = await ReadExistingColumns(connection, schemaName, "WorkflowRuns", cancellationToken);
        foreach (var column in requiredColumns.Where(column => !existingColumns.Contains(column)))
        {
            missing.Add($"{schemaName}.WorkflowRuns.{column}");
        }

        if (await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = @SchemaName
  AND tables.name = N'WorkflowRuns'
  AND indexes.name = N'IX_WorkableWorkflowRuns_Recovery';
""", schemaName, cancellationToken) == 0)
        {
            missing.Add("IX_WorkableWorkflowRuns_Recovery");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Workable SQL Server workflow schema '{schemaName}' is not installed or is incomplete. Missing: {string.Join(", ", missing)}.");
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
        => await ReadExistingColumns(connection, schemaName, "WorkEntries", cancellationToken);

    private static async Task<HashSet<string>> ReadExistingColumns(
        SqlConnection connection,
        string schemaName,
        string tableName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT columns.name
FROM sys.columns columns
INNER JOIN sys.tables tables ON tables.object_id = columns.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = @SchemaName
  AND tables.name = @TableName;
""";
        command.Parameters.AddWithValue("@SchemaName", schemaName);
        command.Parameters.AddWithValue("@TableName", tableName);

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
