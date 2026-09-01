using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Workable;
using Workable.SqlServer;
using Xunit.Abstractions;

namespace Workable.Tests;

[Collection(nameof(SqlServerTestHostCollection))]
[Trait("Category", "SqlServerIntegration")]
[Trait("Category", "PersistenceIntegration")]
public sealed class WorkableSqlServerPersistenceTests : IAsyncLifetime
{
    private const int DatabaseCreationAttempts = 3;
    private const int TransientFileInitializationErrorNumber = 17053;
    private const string SchemaName = "workable";
    private static readonly JsonSerializerOptions DurableJsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly ITestOutputHelper output;
    private readonly SqlServerTestHost sqlServer;
    private string databaseName = CreateDatabaseName();

    public WorkableSqlServerPersistenceTests(
        SqlServerTestHost sqlServer,
        ITestOutputHelper output)
    {
        this.sqlServer = sqlServer;
        this.output = output;
    }

    private string ConnectionString => this.sqlServer.BuildConnectionString(this.databaseName);

    public async Task InitializeAsync()
    {
        this.output.WriteLine($"SQL Server test host: {this.sqlServer.Description}");

        for (var attempt = 1; attempt <= DatabaseCreationAttempts; attempt++)
        {
            await using var connection = new SqlConnection(this.sqlServer.MasterConnectionString);
            await connection.OpenAsync();

            try
            {
                await Execute(connection, $"CREATE DATABASE {Quote(this.databaseName)};");
                return;
            }
            catch (SqlException exception) when (
                attempt < DatabaseCreationAttempts &&
                IsTransientFileInitializationFailure(exception))
            {
                this.output.WriteLine(
                    $"SQL Server transiently failed to initialize database files for '{this.databaseName}' " +
                    $"(attempt {attempt} of {DatabaseCreationAttempts}); retrying with a fresh database name.");
                await DropDatabaseIfExists(connection, this.databaseName);
                this.databaseName = CreateDatabaseName();
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt));
            }
        }

        throw new InvalidOperationException("SQL Server database creation retry loop completed unexpectedly.");
    }

    public async Task DisposeAsync()
    {
        await using var connection = new SqlConnection(this.sqlServer.MasterConnectionString);
        await connection.OpenAsync();
        await DropDatabaseIfExists(connection, this.databaseName);
    }

    private static string CreateDatabaseName()
        => "WorkableTests_" + Guid.NewGuid().ToString("N");

    private static bool IsTransientFileInitializationFailure(SqlException exception)
        => exception.Errors.Cast<SqlError>()
            .Any(error => error.Number == TransientFileInitializationErrorNumber);

    private static Task DropDatabaseIfExists(SqlConnection connection, string databaseName)
        => Execute(connection, $"""
IF DB_ID(N'{Escape(databaseName)}') IS NOT NULL
BEGIN
    ALTER DATABASE {Quote(databaseName)} SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
    DROP DATABASE {Quote(databaseName)};
END
""");

    [Fact]
    public async Task StartInitializesWorkEntriesSchema()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var system = this.CreateSystem(
            "sql-schema",
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration.QueueDurably().DoNotStart());

        await system.Start();
        await StopWithTimeout(system);

        await using var connection = await this.OpenConnection();
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.tables tables
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable' AND tables.name = N'WorkEntries';
"""));
        Assert.Equal(0, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.columns columns
INNER JOIN sys.tables tables ON tables.object_id = columns.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkEntries'
  AND columns.name IN
  (
      N'IsDurableQueued', N'HasPersistentConcurrency', N'ConcurrencyType', N'ConcurrencyValue',
      N'ClaimedBy', N'ClaimedAt', N'LeaseId', N'LeaseExpiresAt', N'ConcurrencyBucket'
  );
"""));
        Assert.Equal(2, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.columns columns
INNER JOIN sys.tables tables ON tables.object_id = columns.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkEntries'
	  AND columns.name IN (N'FailedAt', N'FailureMessagesJson');
"""));
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.columns columns
INNER JOIN sys.tables tables ON tables.object_id = columns.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkEntries'
  AND columns.name = N'WorkflowProvenanceJson';
"""));
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.tables tables
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable' AND tables.name = N'WorkQueueEntries';
"""));
        Assert.Equal(6, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.columns columns
INNER JOIN sys.tables tables ON tables.object_id = columns.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkQueueEntries'
  AND columns.name IN (N'WorkerId', N'HasPersistentConcurrency', N'ConcurrencyScope', N'ConcurrencyMaximumCapacity', N'LeaseExpiresAt', N'ConcurrencyBucket');
"""));
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.columns columns
INNER JOIN sys.tables tables ON tables.object_id = columns.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkQueueEntries'
  AND columns.name = N'Disposition';
"""));
        Assert.Equal(0, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkEntries'
  AND indexes.name IN
  (
      N'IX_WorkableWorkEntries_Ready',
      N'IX_WorkableWorkEntries_PersistentConcurrencyReady',
      N'IX_WorkableWorkEntries_Concurrency'
  );
"""));
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkQueueEntries'
  AND indexes.name = N'IX_WorkableWorkQueueEntries_Ready'
  AND indexes.filter_definition LIKE N'%Disposition%Ready%';
"""));
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkQueueEntries'
  AND indexes.name = N'IX_WorkableWorkQueueEntries_Failed';
"""));
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkQueueEntries'
  AND indexes.name = N'IX_WorkableWorkQueueEntries_PersistentConcurrencyReady'
  AND indexes.filter_definition LIKE N'%Disposition%Ready%'
  AND indexes.filter_definition LIKE N'%HasPersistentConcurrency%';
"""));
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkQueueEntries'
  AND indexes.name = N'IX_WorkableWorkQueueEntries_Concurrency';
"""));
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkEntries'
  AND indexes.name = N'UX_WorkableWorkEntries_Idempotency'
  AND indexes.filter_definition LIKE N'%HasIdempotencyReservation%';
"""));
        Assert.Equal(0, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.columns columns
INNER JOIN sys.tables tables ON tables.object_id = columns.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkEntries'
  AND columns.name IN (N'WorkSystemId', N'DefinitionId');
"""));
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.tables tables
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable' AND tables.name = N'WorkflowRuns';
"""));
        Assert.Equal(7, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.columns columns
INNER JOIN sys.tables tables ON tables.object_id = columns.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkflowRuns'
  AND columns.name IN (N'PersistenceScope', N'DefinitionFingerprint', N'RequestContextJson', N'WorkflowInputJson', N'StepsJson', N'PendingControlAction', N'PendingControlRequestContextJson');
"""));
        Assert.Equal(0, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.columns columns
INNER JOIN sys.tables tables ON tables.object_id = columns.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkflowRuns'
  AND columns.name IN (N'WorkSystemId', N'UpdatedAt');
"""));
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkflowRuns'
  AND indexes.name = N'IX_WorkableWorkflowRuns_Recovery';
"""));
    }

    [Fact]
    public async Task GeneratedSchemaScriptRunsWhenSessionStartsWithQuotedIdentifierOff()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await using var connection = await this.OpenConnection();
        await Execute(connection, "SET QUOTED_IDENTIFIER OFF;");
        foreach (var batch in WorkableSqlServerSchema.CreateBatches(SchemaName))
        {
            await Execute(connection, batch);
        }

        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkQueueEntries'
  AND indexes.name = N'IX_WorkableWorkQueueEntries_Ready';
"""));
    }

    [Fact]
    public async Task SchemaApplySupportsSchemaNamesContainingSqlLiteralCharacters()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string quotedSchemaName = "workable'quoted";
        await WorkableSqlServerSchema.Apply(this.ConnectionString, quotedSchemaName);
        await WorkableSqlServerSchema.ValidateInstalled(this.ConnectionString, quotedSchemaName);
        await WorkableSqlServerSchema.ValidateWorkflowPersistenceInstalled(this.ConnectionString, quotedSchemaName);
        await WorkableSqlServerSchema.ValidateExecutionDiagnosticsInstalled(this.ConnectionString, quotedSchemaName);
    }

    [Fact]
    public async Task SchemaApplyCreatesCurrentSchemaFromNoWorkableObjects()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string freshSchemaName = "workable_fresh_path";
        await using (var connection = await this.OpenConnection())
        {
            await Execute(connection, $"CREATE SCHEMA {WorkableSqlServerSchema.QuoteIdentifier(freshSchemaName)};");
            await Execute(connection, $"""
CREATE TABLE {WorkableSqlServerSchema.QuoteIdentifier(freshSchemaName)}.Unrelated
(
    Id int NOT NULL
);
""");
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, freshSchemaName);
        await WorkableSqlServerSchema.ValidateInstalled(this.ConnectionString, freshSchemaName);
        await WorkableSqlServerSchema.ValidateWorkflowPersistenceInstalled(this.ConnectionString, freshSchemaName);
        await WorkableSqlServerSchema.ValidateExecutionDiagnosticsInstalled(this.ConnectionString, freshSchemaName);

        await using var verification = await this.OpenConnection();
        Assert.Equal(3, await Scalar<int>(verification, $"""
SELECT COUNT(*)
FROM {WorkableSqlServerSchema.QuoteIdentifier(freshSchemaName)}.SchemaVersion;
"""));
    }

    [Fact]
    public async Task SchemaValidatorsDescribeEveryMissingFeatureOfAnEmptySchema()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string emptySchemaName = "workable_validation_empty";
        await using (var connection = await this.OpenConnection())
        {
            await Execute(connection, $"""
IF SCHEMA_ID(N'{emptySchemaName}') IS NULL
    EXEC(N'CREATE SCHEMA {WorkableSqlServerSchema.QuoteIdentifier(emptySchemaName)}');
""");
        }

        var queue = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkableSqlServerSchema.ValidateInstalled(this.ConnectionString, emptySchemaName));
        Assert.Contains($"{emptySchemaName}.WorkEntries", queue.Message, StringComparison.Ordinal);
        Assert.Contains("IX_WorkableWorkQueueEntries_Concurrency", queue.Message, StringComparison.Ordinal);

        var workflow = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkableSqlServerSchema.ValidateWorkflowPersistenceInstalled(this.ConnectionString, emptySchemaName));
        Assert.Contains($"{emptySchemaName}.WorkflowRuns", workflow.Message, StringComparison.Ordinal);
        Assert.Contains("IX_WorkableWorkflowRuns_Recovery", workflow.Message, StringComparison.Ordinal);

        var diagnostics = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkableSqlServerSchema.ValidateExecutionDiagnosticsInstalled(this.ConnectionString, emptySchemaName));
        Assert.Contains($"{emptySchemaName}.WorkIterationDiagnostics", diagnostics.Message, StringComparison.Ordinal);
        Assert.Contains("IX_WorkableWorkDiagnosticCaptureRules_System", diagnostics.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SchemaScalarRejectsMissingRowsAndDatabaseNullValues()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await using var connection = await this.OpenConnection();
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeSchemaScalar<int>(connection, "SELECT 1 WHERE 1 = 0;"));
        var databaseNull = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            InvokeSchemaScalar<int>(connection, "SELECT CAST(NULL AS int);"));

        Assert.Equal("Expected SQL scalar query to return a value.", missing.Message);
        Assert.Equal("Expected SQL scalar query to return a value.", databaseNull.Message);
    }

    [Fact]
    public async Task ConcurrentFreshSchemaApplySerializesCreation()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string concurrentSchemaName = "workable_concurrent_fresh";
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            WorkableSqlServerSchema.Apply(this.ConnectionString, concurrentSchemaName)));

        await WorkableSqlServerSchema.ValidateInstalled(this.ConnectionString, concurrentSchemaName);
        await WorkableSqlServerSchema.ValidateWorkflowPersistenceInstalled(this.ConnectionString, concurrentSchemaName);
        await WorkableSqlServerSchema.ValidateExecutionDiagnosticsInstalled(this.ConnectionString, concurrentSchemaName);
    }

    [Fact]
    public void GeneratedSchemaScriptDoesNotRewriteApplicationData()
    {
        var script = WorkableSqlServerSchema.GenerateScript(SchemaName);
        var applicationDataDml = new Regex(
            @"(?im)^\s*(?:UPDATE|INSERT\s+INTO|DELETE\s+FROM|MERGE)\s+(?<target>[^\s;]+)",
            RegexOptions.CultureInvariant);

        var dmlTargets = applicationDataDml.Matches(script)
            .Select(match => match.Groups["target"].Value)
            .ToArray();
        Assert.NotEmpty(dmlTargets);
        Assert.All(dmlTargets, target => Assert.Equal("[workable].[SchemaVersion]", target));
    }

    [Fact]
    public async Task SchemaApplyUpgradesExecutionDiagnosticMetadata()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await Execute(connection, """
ALTER TABLE workable.WorkIterationDiagnostics
DROP CONSTRAINT DF_WorkableWorkIterationDiagnostics_ProfileDropped;
ALTER TABLE workable.WorkIterationDiagnostics
DROP COLUMN SqlClientProfilingAvailable, HttpClientProfilingAvailable, ProfileDropped;
DROP INDEX IX_WorkableWorkIterationDiagnostics_RecentWork
ON workable.WorkIterationDiagnostics;
CREATE INDEX IX_WorkableWorkIterationDiagnostics_RecentWork
ON workable.WorkIterationDiagnostics
    (PersistenceScope, WorkSystemName, DefinitionName, CompletedAt DESC, DiagnosticId);
UPDATE workable.SchemaVersion
SET Version = 6
WHERE Component = N'ExecutionDiagnostics';
""");
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await WorkableSqlServerSchema.ValidateExecutionDiagnosticsInstalled(this.ConnectionString, SchemaName);

        await using var verification = await this.OpenConnection();
        Assert.Equal(3, await Scalar<int>(verification, """
SELECT COUNT(*)
FROM sys.columns columns
INNER JOIN sys.tables tables ON tables.object_id = columns.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkIterationDiagnostics'
  AND columns.name IN (N'SqlClientProfilingAvailable', N'HttpClientProfilingAvailable', N'ProfileDropped');
"""));
        Assert.Equal(2, await Scalar<int>(verification, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkIterationDiagnostics'
  AND indexes.name IN
  (
      N'IX_WorkableWorkIterationDiagnostics_ExpirationByScope',
      N'IX_WorkableWorkIterationDiagnostics_IncompleteByScope'
  );
"""));
        Assert.Equal("WorkSystemName", await Scalar<string>(verification, """
SELECT columns.name
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
INNER JOIN sys.index_columns index_columns
    ON index_columns.object_id = indexes.object_id
   AND index_columns.index_id = indexes.index_id
   AND index_columns.key_ordinal = 1
INNER JOIN sys.columns columns
    ON columns.object_id = index_columns.object_id
   AND columns.column_id = index_columns.column_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkIterationDiagnostics'
  AND indexes.name = N'IX_WorkableWorkIterationDiagnostics_RecentWork';
"""));
        Assert.Equal(1438, await Scalar<int>(verification, """
SELECT SUM(columns.max_length)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
INNER JOIN sys.index_columns index_columns
    ON index_columns.object_id = indexes.object_id
   AND index_columns.index_id = indexes.index_id
   AND index_columns.key_ordinal > 0
INNER JOIN sys.columns columns
    ON columns.object_id = index_columns.object_id
   AND columns.column_id = index_columns.column_id
WHERE schemas.name = N'workable'
  AND tables.name = N'WorkIterationDiagnostics'
  AND indexes.name = N'IX_WorkableWorkIterationDiagnostics_RecentWork';
"""));
        Assert.Equal(7, await Scalar<int>(verification, """
SELECT Version
FROM workable.SchemaVersion
WHERE Component = N'ExecutionDiagnostics';
"""));
    }

    [Fact]
    public async Task SchemaApplyUsesTheQueueVersionForTheCollapsedVersionFourUpgrade()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await Execute(connection, """
DROP INDEX IX_WorkableWorkQueueEntries_Failed ON workable.WorkQueueEntries;
DROP INDEX IX_WorkableWorkQueueEntries_Ready ON workable.WorkQueueEntries;
DROP INDEX IX_WorkableWorkQueueEntries_PersistentConcurrencyReady ON workable.WorkQueueEntries;
ALTER TABLE workable.WorkEntries DROP COLUMN WorkflowProvenanceJson, FailedAt, FailureMessagesJson;
ALTER TABLE workable.WorkQueueEntries
    DROP CONSTRAINT DF_WorkableWorkQueueEntries_Disposition;
ALTER TABLE workable.WorkQueueEntries DROP COLUMN Disposition;
ALTER TABLE workable.WorkEntries ADD
    IsDurableQueued bit NOT NULL CONSTRAINT DF_WorkableWorkEntries_IsDurableQueued DEFAULT (0),
    HasPersistentConcurrency bit NOT NULL CONSTRAINT DF_WorkableWorkEntries_HasPersistentConcurrency DEFAULT (0),
    ConcurrencyType nvarchar(256) NULL,
    ConcurrencyValue nvarchar(450) NULL,
    ClaimedBy nvarchar(450) NULL,
    ClaimedAt datetimeoffset NULL,
    LeaseId nvarchar(64) NULL,
    LeaseExpiresAt datetimeoffset NULL,
    ConcurrencyBucket nvarchar(32) NULL;
EXEC(N'CREATE INDEX IX_WorkableWorkEntries_Ready
    ON workable.WorkEntries (WorkSystemName, LeaseExpiresAt, CreatedAt, WorkerId)
    WHERE IsDurableQueued = 1;');
EXEC(N'CREATE INDEX IX_WorkableWorkEntries_PersistentConcurrencyReady
    ON workable.WorkEntries (WorkSystemName, LeaseExpiresAt, CreatedAt, WorkerId)
    WHERE IsDurableQueued = 1 AND HasPersistentConcurrency = 1;');
EXEC(N'CREATE INDEX IX_WorkableWorkEntries_Concurrency
    ON workable.WorkEntries
        (WorkSystemName, DefinitionName, ConcurrencyBucket, LeaseExpiresAt, SubjectType, SubjectValue, ConcurrencyType, ConcurrencyValue)
    WHERE ConcurrencyBucket IS NOT NULL;');
ALTER TABLE workable.WorkQueueEntries ADD ClaimedBy nvarchar(450) NULL, ClaimedAt datetimeoffset NULL;
ALTER TABLE workable.WorkflowRuns ADD UpdatedAt datetimeoffset NULL;
UPDATE workable.SchemaVersion
SET Version = 3
WHERE Component = N'QueueDurability';
""");
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await WorkableSqlServerSchema.ValidateInstalled(this.ConnectionString, SchemaName);

        await using var verification = await this.OpenConnection();
        Assert.Equal(4, await Scalar<int>(verification, """
SELECT Version
FROM workable.SchemaVersion
WHERE Component = N'QueueDurability';
"""));
        Assert.Equal(0, await Scalar<int>(verification, """
SELECT COUNT(*)
FROM sys.columns columns
INNER JOIN sys.tables tables ON tables.object_id = columns.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = N'workable'
  AND
  (
      tables.name = N'WorkEntries'
      AND columns.name IN
      (
          N'IsDurableQueued', N'HasPersistentConcurrency', N'ConcurrencyType', N'ConcurrencyValue',
          N'ClaimedBy', N'ClaimedAt', N'LeaseId', N'LeaseExpiresAt', N'ConcurrencyBucket'
      )
      OR tables.name = N'WorkQueueEntries' AND columns.name IN (N'ClaimedBy', N'ClaimedAt')
      OR tables.name = N'WorkflowRuns' AND columns.name = N'UpdatedAt'
  );
"""));
    }

    [Fact]
    public async Task SchemaApplyDoesNotRewriteCurrentVersionMetadata()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var connection = await this.OpenConnection();
        var before = await Scalar<DateTimeOffset>(connection, """
SELECT UpdatedAt
FROM workable.SchemaVersion
WHERE Component = N'QueueDurability';
""");

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);

        var after = await Scalar<DateTimeOffset>(connection, """
SELECT UpdatedAt
FROM workable.SchemaVersion
WHERE Component = N'QueueDurability';
""");
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task SchemaApplyRunsEveryRequiredMigrationAndPreservesForwardComponents()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var connection = await this.OpenConnection();
        await Execute(connection, """
UPDATE workable.SchemaVersion
SET Version = CASE Component
    WHEN N'QueueDurability' THEN 3
    WHEN N'WorkflowPersistence' THEN 5
    WHEN N'ExecutionDiagnostics' THEN 6
END,
UpdatedAt = '2000-01-01T00:00:00+00:00'
WHERE Component IN (N'QueueDurability', N'WorkflowPersistence', N'ExecutionDiagnostics');
""");

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);

        Assert.Equal(4, await Scalar<int>(connection, """
SELECT Version FROM workable.SchemaVersion WHERE Component = N'QueueDurability';
"""));
        Assert.Equal(5, await Scalar<int>(connection, """
SELECT Version FROM workable.SchemaVersion WHERE Component = N'WorkflowPersistence';
"""));
        Assert.Equal(7, await Scalar<int>(connection, """
SELECT Version FROM workable.SchemaVersion WHERE Component = N'ExecutionDiagnostics';
"""));
        Assert.Equal(
            DateTimeOffset.Parse("2000-01-01T00:00:00+00:00"),
            await Scalar<DateTimeOffset>(connection, """
SELECT UpdatedAt FROM workable.SchemaVersion WHERE Component = N'WorkflowPersistence';
"""));
    }

    [Fact]
    public async Task SchemaApplyRejectsUnsupportedVersionBeforeApplyingSupportedMigrations()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var connection = await this.OpenConnection();
        await Execute(connection, """
UPDATE workable.SchemaVersion
SET Version = CASE Component
    WHEN N'QueueDurability' THEN 3
    WHEN N'WorkflowPersistence' THEN 3
    ELSE Version
END
WHERE Component IN (N'QueueDurability', N'WorkflowPersistence');
""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName));

        Assert.Contains("WorkflowPersistence", exception.Message);
        Assert.Contains("no ordered migration", exception.Message);
        Assert.Equal(3, await Scalar<int>(connection, """
SELECT Version FROM workable.SchemaVersion WHERE Component = N'QueueDurability';
"""));
    }

    [Fact]
    public async Task SchemaApplyRejectsVersionedSchemaWithMissingComponentVersion()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var connection = await this.OpenConnection();
        await Execute(connection, """
DELETE FROM workable.SchemaVersion
WHERE Component = N'WorkflowPersistence';
""");

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName));

        Assert.Contains("no 'WorkflowPersistence' version row", exception.Message);
        Assert.Equal(2, await Scalar<int>(connection, "SELECT COUNT(*) FROM workable.SchemaVersion;"));
    }

    [Fact]
    public async Task AutoDeploySchemaCanBeDisabledWhenSchemaAlreadyExists()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);

        var system = this.CreateSystem(
            "sql-schema-preinstalled",
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration.QueueDurably().DoNotStart(),
            autoDeploySchema: false);

        await system.Start();
        await system.Stop();
    }

    [Fact]
    public async Task AutoDeploySchemaDisabledFailsStartupWhenSchemaIsMissing()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var system = this.CreateSystem(
            "sql-schema-missing",
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration.QueueDurably().DoNotStart(),
            autoDeploySchema: false);

        var exception = await Assert.ThrowsAsync<WorkableSqlServerSchemaDeploymentException>(() => system.Start());

        Assert.Contains("could not validate schema", exception.Message);
        Assert.IsType<InvalidOperationException>(exception.InnerException);
    }

    [Fact]
    public async Task AutoDeploySchemaRejectsExistingWorkableTablesWithoutVersionMetadata()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await using var connection = await this.OpenConnection();
        await Execute(connection, "CREATE SCHEMA workable;");
        await Execute(connection, """
CREATE TABLE workable.WorkEntries
(
    WorkerId uniqueidentifier NOT NULL CONSTRAINT PK_WorkableWorkEntries PRIMARY KEY,
    WorkSystemName nvarchar(256) NOT NULL,
    DefinitionName nvarchar(450) NOT NULL
);
""");

        var system = this.CreateSystem(
            "sql-schema-incomplete",
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration.QueueDurably().DoNotStart());

        var exception = await Assert.ThrowsAsync<WorkableSqlServerSchemaDeploymentException>(() => system.Start());

        Assert.Contains("could not deploy schema", exception.Message);
        var validation = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("no schema version metadata", validation.Message);
    }

    [Fact]
    public async Task DurableWorkflowCanStartAndCompleteWithSqlServerPersistence()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(
                this.ConnectionString,
                SchemaName)
            .AddWorkableSystem("workflow-tests", builder =>
            {
                builder.RequireAuthorization(false);
                builder.AddWork(
                    WorkDefinition.Create("sample.dispatch"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        "workflow.durable.dispatch",
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("sample.dispatch")));
            })
            .BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(registry.TryGet("workflow-tests", out var namedSystem));
        var system = namedSystem!;
        await system.Start();

        var handle = await StartWorkflow(system, "workflow.durable.dispatch");
        var completion = await WaitForWorkflowCompletion(handle);

        Assert.True(IsWorkflowAccepted(handle));
        Assert.Equal(WorkflowRunStatus.Completed.ToString(), WorkflowCompletionStatus(completion));

        await using var connection = await this.OpenConnection();
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkflowRuns;
"""));
        await TestEventually.Until(
            async () => await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkEntries;
""") == 0,
            "Expected durable workflow child work entries to be cleaned up.");
        await system.Stop();
    }

    [Fact]
    public async Task DurableWorkflowCanceledByShutdownRecoversAfterRestart()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workflowName = "workflow.durable.parallel.recover";
        var alphaStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var betaStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var firstProvider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .AddWorkableSystem("workflow-tests", builder =>
            {
                builder.RequireAuthorization(false);
                builder.AddWork(
                    WorkDefinition.Create("sample.alpha"),
                    async (_, _, cancellationToken) =>
                    {
                        alphaStarted.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return WorkExecutionResult.Success();
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWork(
                    WorkDefinition.Create("sample.beta"),
                    async (_, _, cancellationToken) =>
                    {
                        betaStarted.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return WorkExecutionResult.Success();
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        workflowName,
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow
                        .RunParallel("dispatch", parallel => parallel
                            .DispatchWork("alpha", WorkDefinition.Create("sample.alpha"))
                            .DispatchWork("beta", WorkDefinition.Create("sample.beta")))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var firstRegistry = firstProvider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(firstRegistry.TryGet("workflow-tests", out var firstNamedSystem));
        var firstSystem = firstNamedSystem!;
        await firstSystem.Start();

        var handle = await StartWorkflow(firstSystem, workflowName);
        var runId = RequiredWorkflowRunId(handle);
        await WaitWithTimeout(alphaStarted.Task);
        await WaitWithTimeout(betaStarted.Task);

        await using (var beforeStop = await this.OpenConnection())
        {
            await TestEventually.Until(
                async () => await Scalar<int>(beforeStop, """
SELECT COUNT(*)
FROM workable.WorkflowRuns;
""") == 1,
                "Expected the durable workflow run to be persisted before shutdown.");
            await TestEventually.Until(
                async () => await Scalar<int>(beforeStop, """
SELECT COUNT(*)
FROM workable.WorkEntries
WHERE WorkSystemName = N'workflow-tests'
  AND DefinitionName IN (N'sample.alpha', N'sample.beta');
""") == 2,
                "Expected both durable child workers to be persisted before shutdown.");
        }

        await StopWithTimeout(firstSystem);
        var canceled = await WaitForWorkflowCompletion(handle);
        Assert.Equal(WorkflowRunStatus.Canceled.ToString(), WorkflowCompletionStatus(canceled));
        await using (var afterStop = await this.OpenConnection())
        {
            Assert.Equal(1, await Scalar<int>(afterStop, $"""
SELECT COUNT(*)
FROM workable.WorkflowRuns
WHERE RunId = '{runId.Value:D}';
"""));
        }

        await using (var expired = await this.OpenConnection())
        {
            await Execute(expired, """
UPDATE workable.WorkQueueEntries
SET LeaseExpiresAt = DATEADD(second, -1, SYSDATETIMEOFFSET())
WHERE WorkSystemName = N'workflow-tests'
  AND DefinitionName IN (N'sample.alpha', N'sample.beta');
""");
        }

        var resumedChildren = 0;
        var replayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var secondProvider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .AddWorkableSystem("workflow-tests", builder =>
            {
                builder.RequireAuthorization(false);
                builder.AddWork(
                    WorkDefinition.Create("sample.alpha"),
                    (_, _, _) =>
                    {
                        if (Interlocked.Increment(ref resumedChildren) == 2)
                        {
                            replayed.TrySetResult();
                        }

                        return Task.FromResult(WorkExecutionResult.Success());
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWork(
                    WorkDefinition.Create("sample.beta"),
                    (_, _, _) =>
                    {
                        if (Interlocked.Increment(ref resumedChildren) == 2)
                        {
                            replayed.TrySetResult();
                        }

                        return Task.FromResult(WorkExecutionResult.Success());
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        workflowName,
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow
                        .RunParallel("dispatch", parallel => parallel
                            .DispatchWork("alpha", WorkDefinition.Create("sample.alpha"))
                            .DispatchWork("beta", WorkDefinition.Create("sample.beta")))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var secondRegistry = secondProvider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(secondRegistry.TryGet("workflow-tests", out var secondNamedSystem));
        var secondSystem = secondNamedSystem!;
        await secondSystem.Start();
        await WaitWithTimeout(replayed.Task);

        await TestEventually.Until(
            () => WorkflowStatus(secondSystem, runId) == WorkflowRunStatus.Completed.ToString(),
            "Expected the durable workflow to recover and complete after restart.",
            timeout: TimeSpan.FromSeconds(15));

        await using (var verification = await this.OpenConnection())
        {
            await TestEventually.Until(
                async () => await Scalar<int>(verification, """
SELECT COUNT(*)
FROM workable.WorkflowRuns;
""") == 1,
                "Expected the recovered durable workflow run to remain persisted while its final child workers are still retained.");
            await TestEventually.Until(
                async () => await Scalar<int>(verification, """
SELECT COUNT(*)
FROM workable.WorkEntries
WHERE WorkSystemName = N'workflow-tests';
""") == 0,
                "Expected recovered durable workflow child workers to be cleaned up after completion.");
        }

        await StopWithTimeout(secondSystem);
        Assert.Equal(2, Volatile.Read(ref resumedChildren));
    }

    [Fact]
    public async Task DurableWorkflowPauseRequestPersistsWhileOutstandingChildRuns()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workflowName = "workflow.durable.stop.persist.sql";
        var slowStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fastRuns = 0;
        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .AddWorkableSystem("workflow-tests", builder =>
            {
                builder.RequireAuthorization(false);
                builder.AddWork(
                    WorkDefinition.Create("sample.stop.slow"),
                    async (_, _, cancellationToken) =>
                    {
                        slowStarted.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return WorkExecutionResult.Success();
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWork(
                    WorkDefinition.Create("sample.stop.fast"),
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref fastRuns);
                        return Task.FromResult(WorkExecutionResult.Success());
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        workflowName,
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow
                        .DispatchWork("slow", WorkDefinition.Create("sample.stop.slow"))
                        .Join("join")
                        .DispatchWork("fast", WorkDefinition.Create("sample.stop.fast")));
            })
            .BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(registry.TryGet("workflow-tests", out var namedSystem));
        var system = namedSystem!;
        await system.Start();

        var handle = await StartWorkflow(system, workflowName);
        var runId = RequiredWorkflowRunId(handle);
        await WaitWithTimeout(slowStarted.Task);

        var pause = await ExecuteWorkflowAction(system, runId, "Pause");
        Assert.True(WorkflowActionAccepted(pause));

        await using (var connection = await this.OpenConnection())
        {
            await TestEventually.Until(
                async () =>
                {
                    var pendingAction = await Scalar<string?>(
                        connection,
                        """
SELECT TOP (1) PendingControlAction
FROM workable.WorkflowRuns
WHERE RunId = @RunId;
""",
                        new SqlParameter("@RunId", runId.Value));
                    if (string.Equals(pendingAction, "Pause", StringComparison.Ordinal))
                    {
                        return true;
                    }

                    var status = await Scalar<string?>(
                        connection,
                        """
SELECT TOP (1) Status
FROM workable.WorkflowRuns
WHERE RunId = @RunId;
""",
                        new SqlParameter("@RunId", runId.Value));
                    return string.Equals(status, nameof(WorkflowRunStatus.Paused), StringComparison.Ordinal);
                },
                "Expected the durable workflow pause request to be persisted or applied while the outstanding child is still running.",
                timeout: TimeSpan.FromSeconds(10));
        }

        await system.Stop();
        var canceled = await WaitForWorkflowCompletion(handle);
        Assert.Equal(WorkflowRunStatus.Canceled.ToString(), WorkflowCompletionStatus(canceled));
        Assert.Equal(0, Volatile.Read(ref fastRuns));
    }

    [Fact]
    public async Task DurableWorkflowRecoveryOnlyReplaysIncompleteParallelChildren()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workflowName = "workflow.durable.parallel.partial-recover";
        await using var firstProvider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .AddWorkableSystem("workflow-tests", builder =>
            {
                builder.RequireAuthorization(false);
                builder.AddWork(
                    WorkDefinition.Create("sample.alpha"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWork(
                    WorkDefinition.Create("sample.beta"),
                    async (_, _, cancellationToken) =>
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return WorkExecutionResult.Success();
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        workflowName,
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow
                        .RunParallel("dispatch", parallel => parallel
                            .DispatchWork("alpha", WorkDefinition.Create("sample.alpha"))
                            .DispatchWork("beta", WorkDefinition.Create("sample.beta")))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var firstRegistry = firstProvider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(firstRegistry.TryGet("workflow-tests", out var firstNamedSystem));
        var firstSystem = firstNamedSystem!;
        await firstSystem.Start();

        var handle = await StartWorkflow(firstSystem, workflowName);
        var runId = RequiredWorkflowRunId(handle);
        await TestEventually.Until(
            () => WorkflowStepWorkerIds(firstSystem, runId, "join").Count == 1,
            "Expected the durable join step to retain only the unfinished child before shutdown.",
            timeout: TimeSpan.FromSeconds(15));

        var remainingWorkerId = WorkflowStepWorkerIds(firstSystem, runId, "join").Single();
        await StopWithTimeout(firstSystem);
        var canceled = await WaitForWorkflowCompletion(handle);
        Assert.Equal(WorkflowRunStatus.Canceled.ToString(), WorkflowCompletionStatus(canceled));
        await using (var afterStop = await this.OpenConnection())
        {
            Assert.Equal(1, await Scalar<int>(afterStop, $"""
SELECT COUNT(*)
FROM workable.WorkflowRuns
WHERE RunId = '{runId.Value:D}';
"""));
        }

        await using (var expired = await this.OpenConnection())
        {
            await Execute(expired, $"""
UPDATE workable.WorkQueueEntries
SET LeaseExpiresAt = DATEADD(second, -1, SYSDATETIMEOFFSET())
WHERE WorkerId = '{remainingWorkerId.Value:D}';
""");
        }

        var replayedAlpha = 0;
        var replayedBeta = 0;
        await using var secondProvider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .AddWorkableSystem("workflow-tests", builder =>
            {
                builder.RequireAuthorization(false);
                builder.AddWork(
                    WorkDefinition.Create("sample.alpha"),
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref replayedAlpha);
                        return Task.FromResult(WorkExecutionResult.Success());
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWork(
                    WorkDefinition.Create("sample.beta"),
                    (_, _, _) =>
                    {
                        Interlocked.Increment(ref replayedBeta);
                        return Task.FromResult(WorkExecutionResult.Success());
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        workflowName,
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow
                        .RunParallel("dispatch", parallel => parallel
                            .DispatchWork("alpha", WorkDefinition.Create("sample.alpha"))
                            .DispatchWork("beta", WorkDefinition.Create("sample.beta")))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var secondRegistry = secondProvider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(secondRegistry.TryGet("workflow-tests", out var secondNamedSystem));
        var secondSystem = secondNamedSystem!;
        await secondSystem.Start();

        await TestEventually.Until(
            () => WorkflowStatus(secondSystem, runId) == WorkflowRunStatus.Completed.ToString(),
            "Expected the durable workflow to recover and complete after replaying only the unfinished child.",
            timeout: TimeSpan.FromSeconds(15));

        await StopWithTimeout(secondSystem);
        Assert.NotEqual(Guid.Empty, remainingWorkerId.Value);
        Assert.Equal(0, Volatile.Read(ref replayedAlpha));
        Assert.Equal(1, Volatile.Read(ref replayedBeta));
    }

    [Fact]
    public async Task WorkflowRunViewShowsRecoveredSqlDurableWorkflowState()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workflowName = "workflow.durable.parallel.status-recover";
        await using var firstProvider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .AddWorkableSystem("workflow-tests", builder =>
            {
                builder.RequireAuthorization(false);
                builder.AddWork(
                    WorkDefinition.Create("sample.alpha"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWork(
                    WorkDefinition.Create("sample.beta"),
                    async (_, _, cancellationToken) =>
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return WorkExecutionResult.Success();
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        workflowName,
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow
                        .RunParallel("dispatch", parallel => parallel
                            .DispatchWork("alpha", WorkDefinition.Create("sample.alpha"))
                            .DispatchWork("beta", WorkDefinition.Create("sample.beta")))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var firstRegistry = firstProvider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(firstRegistry.TryGet("workflow-tests", out var firstNamedSystem));
        var firstSystem = firstNamedSystem!;
        await firstSystem.Start();

        var handle = await StartWorkflow(firstSystem, workflowName);
        var runId = RequiredWorkflowRunId(handle);
        await TestEventually.Until(
            () => WorkflowStepWorkerIds(firstSystem, runId, "join").Count == 1,
            "Expected the durable join step to retain only the unfinished child before shutdown.",
            timeout: TimeSpan.FromSeconds(15));

        var remainingWorkerId = WorkflowStepWorkerIds(firstSystem, runId, "join").Single();
        await StopWithTimeout(firstSystem);
        await WaitForWorkflowCompletion(handle);

        await using (var expired = await this.OpenConnection())
        {
            await Execute(expired, $"""
UPDATE workable.WorkQueueEntries
SET LeaseExpiresAt = DATEADD(second, -1, SYSDATETIMEOFFSET())
WHERE WorkerId = '{remainingWorkerId.Value:D}';
""");
        }

        var resumedBetaStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumedBetaRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var secondProvider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .AddWorkableSystem("workflow-tests", builder =>
            {
                builder.RequireAuthorization(false);
                builder.AddWork(
                    WorkDefinition.Create("sample.alpha"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWork(
                    WorkDefinition.Create("sample.beta"),
                    async (_, _, cancellationToken) =>
                    {
                        resumedBetaStarted.TrySetResult();
                        await resumedBetaRelease.Task.WaitAsync(cancellationToken);
                        return WorkExecutionResult.Success();
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        workflowName,
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow
                        .RunParallel("dispatch", parallel => parallel
                            .DispatchWork("alpha", WorkDefinition.Create("sample.alpha"))
                            .DispatchWork("beta", WorkDefinition.Create("sample.beta")))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var secondRegistry = secondProvider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(secondRegistry.TryGet("workflow-tests", out var secondNamedSystem));
        var secondSystem = secondNamedSystem!;
        await secondSystem.Start();
        await resumedBetaStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var detail = await new WorkflowRunViewAdapter().Run(
            secondSystem,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            runId);

        resumedBetaRelease.TrySetResult();
        await TestEventually.Until(
            () => WorkflowStatus(secondSystem, runId) == WorkflowRunStatus.Completed.ToString(),
            "Expected the recovered durable workflow to complete after inspection.",
            timeout: TimeSpan.FromSeconds(15));

        await StopWithTimeout(secondSystem);

        Assert.NotNull(detail);
        Assert.Equal("dispatch", detail!.CurrentStepName);
        Assert.Equal(1, detail.OutstandingChildren.Active);
        Assert.Equal(0, detail.OutstandingChildren.Unavailable);
        Assert.Equal(2, Assert.Single(detail.Steps, step => step.Name == "dispatch").Children.Total);
        Assert.Equal(1, Assert.Single(detail.Steps, step => step.Name == "join").Children.Total);
    }

    [Fact]
    public async Task WorkflowRunsRoundTripThroughTheSqlPersistenceStore()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        var run = CreateWorkflowRun("workflow-tests", "workflow.sql.roundtrip");

        await store.UpsertWorkflowRun(run);
        var loaded = new List<WorkflowRunPersistenceRecord>();
        await foreach (var item in store.ListWorkflowRuns(
            new WorkflowPersistenceReadRequest("workflow-tests")))
        {
            loaded.Add(item);
        }

        await store.DeleteWorkflowRun(new WorkflowPersistenceDeleteRequest(run.RunId));

        Assert.Single(loaded);
        Assert.Equal(run.RunId, loaded[0].RunId);
        Assert.Equal(run.DefinitionName, loaded[0].DefinitionName);
        Assert.Equal(run.DefinitionFingerprint, loaded[0].DefinitionFingerprint);
        Assert.Equal(run.PendingControlAction, loaded[0].PendingControlAction);
        Assert.Equal(run.PendingControlRequestContext, loaded[0].PendingControlRequestContext);
        Assert.Equal(run.Input?.Json, loaded[0].Input?.Json);
        Assert.Equal(run.RequestContext.Actor.Id, loaded[0].RequestContext.Actor.Id);
        Assert.Equal(run.Steps.Single().WorkerIds, loaded[0].Steps.Single().WorkerIds);
        Assert.Equal(run.ChildReceipts.Single().WorkerId, loaded[0].ChildReceipts.Single().WorkerId);
        Assert.Equal(run.ChildReceipts.Single().StepName, loaded[0].ChildReceipts.Single().StepName);
        Assert.Equal(run.ChildReceipts.Single().DefinitionName, loaded[0].ChildReceipts.Single().DefinitionName);
        Assert.Equal(run.ChildReceipts.Single().CompletionStatus, loaded[0].ChildReceipts.Single().CompletionStatus);

        await using var connection = await this.OpenConnection();
        Assert.Equal(0, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkflowRuns;
"""));
    }

    [Fact]
    public async Task WorkflowRunReaderRestoresLegacyNullOptionalValues()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        var runId = Guid.NewGuid();
        var definitionId = Guid.NewGuid();
        const string persistenceScope = "workflow-null-coverage";
        await using (var connection = await this.OpenConnection())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
DELETE FROM workable.WorkflowRuns WHERE PersistenceScope = @PersistenceScope;

INSERT INTO workable.WorkflowRuns
(
    RunId, PersistenceScope, WorkSystemName, DefinitionId, DefinitionRevision, DefinitionName,
    DefinitionFingerprint, RequestContextJson, WorkflowInputJson, Status, StepsJson, MessagesJson,
    ChildReceiptsJson, PendingControlAction, PendingControlRequestContextJson, CreatedAt, StartedAt, CompletedAt
)
VALUES
(
    @RunId, @PersistenceScope, NULL, @DefinitionId, 1, N'workflow.legacy.nulls',
    N'legacy-fingerprint', @RequestContextJson, NULL, N'Running', N'null', N'null',
    N'null', NULL, NULL, @CreatedAt, NULL, NULL
);
""";
            command.Parameters.AddWithValue("@RunId", runId);
            command.Parameters.AddWithValue("@PersistenceScope", persistenceScope);
            command.Parameters.AddWithValue("@DefinitionId", definitionId);
            command.Parameters.AddWithValue(
                "@RequestContextJson",
                JsonSerializer.Serialize(
                    WorkOrigin.Create(WorkInvocationChannel.InProcess),
                    DurableJsonOptions));
            command.Parameters.AddWithValue("@CreatedAt", DateTimeOffset.UtcNow);
            await command.ExecuteNonQueryAsync();
        }

        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        var loaded = new List<WorkflowRunPersistenceRecord>();
        await foreach (var item in store.ListWorkflowRuns(new WorkflowPersistenceReadRequest(persistenceScope)))
        {
            loaded.Add(item);
        }

        var restored = Assert.Single(loaded);
        Assert.Null(restored.WorkSystemName);
        Assert.Null(restored.Input);
        Assert.Null(restored.StartedAt);
        Assert.Null(restored.CompletedAt);
        Assert.Empty(restored.Steps);
        Assert.Empty(restored.Messages);
        Assert.Empty(restored.ChildReceipts);
        Assert.Null(restored.PendingControlAction);
        Assert.Null(restored.PendingControlRequestContext);
        Assert.Equal(WorkInvocationChannel.InProcess, restored.RequestContext.Origin.Channel);

        await store.DeleteWorkflowRun(new WorkflowPersistenceDeleteRequest(new WorkflowRunId(runId)));
    }

    [Fact]
    public async Task WorkflowTransactionCommitsWorkflowRunsAndDurableWorkersAtomically()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        var run = CreateWorkflowRun("workflow-tests", "workflow.sql.transaction.commit");
        var workerId = WorkerId.New();

        await using (var transaction = await store.BeginWorkflowTransaction(
            new WorkflowPersistenceTransactionRequest("workflow-tests")))
        {
            await store.UpsertWorkflowRun(run, transaction);
            await store.Enqueue(CreateDurableEnqueueRequest(
                WorkSystemId.New(),
                "workflow-tests",
                workerId,
                "sample.dispatch",
                "workflow-transaction-commit",
                transaction));

            await transaction.Commit();
        }

        await using var connection = await this.OpenConnection();
        Assert.Equal(1, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkflowRuns;
"""));
        Assert.Equal(1, await CountRowsForSubject(connection, "workflow-transaction-commit"));

        await using (var transaction = await store.BeginWorkflowTransaction(
            new WorkflowPersistenceTransactionRequest("workflow-tests")))
        {
            await store.DeleteWorkflowRun(
                new WorkflowPersistenceDeleteRequest(run.RunId),
                transaction);
            await transaction.Commit();
        }

        Assert.Equal(0, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkflowRuns;
"""));
    }

    [Fact]
    public async Task BatchChecksDurableWorkerExistence()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        var systemId = WorkSystemId.New();
        var alpha = WorkerId.New();
        var beta = WorkerId.New();
        var missing = WorkerId.New();
        await store.Enqueue(CreateDurableEnqueueRequest(
            systemId,
            "workflow-tests",
            alpha,
            "sample.dispatch",
            "batch-existence-alpha",
            transaction: null));
        await store.Enqueue(CreateDurableEnqueueRequest(
            systemId,
            "workflow-tests",
            beta,
            "sample.dispatch",
            "batch-existence-beta",
            transaction: null));

        var existing = await store.DurableWorkersExist([alpha, missing, beta]);

        Assert.True(existing.SetEquals([alpha, beta]));
        Assert.True(await store.DurableWorkerExists(alpha));
        Assert.False(await store.DurableWorkerExists(missing));
    }

    [Fact]
    public async Task EmptyDurabilityBatchesAreNoOpsAndTransactionsRemainProviderBound()
    {
        var store = new WorkableSqlServerQueueDurabilityStore(new WorkableSqlServerQueueDurabilityOptions
        {
            ConnectionString = this.ConnectionString,
        });
        var foreignTransaction = new ForeignDurabilityTransaction();

        Assert.Empty(await store.DurableWorkersExist([]));
        await store.RenewLeases([], TimeSpan.FromMinutes(1));
        await store.RetainFailed(Array.Empty<WorkQueueDurabilityCleanupRequest>());
        await store.RetainFailed(Array.Empty<WorkQueueDurabilityFailureRequest>());
        await store.DeleteFinal([]);
        await store.DeleteFinal([], foreignTransaction);

        var workerId = WorkerId.New();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteFinal(
            [new WorkQueueDurabilityCleanupRequest(workerId, Lease: null)],
            foreignTransaction));
        Assert.Contains("requires a SQL Server durability transaction", exception.Message);
    }

    [Fact]
    public async Task IdempotencyReservationsCommitRollbackAndHonorAnExistingSqlTransaction()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        var store = new WorkableSqlServerQueueDurabilityStore(new WorkableSqlServerQueueDurabilityOptions
        {
            ConnectionString = this.ConnectionString,
        });
        var systemId = WorkSystemId.New();
        var definition = WorkDefinition.Create("sql.idempotency.reservation");
        var requestContext = WorkRequestContext.Create(WorkInvocationChannel.InProcess);
        var committedSubject = new WorkSubjectId("coverage", $"committed-{Guid.NewGuid():N}");
        var committed = new WorkIdempotencyPersistenceRequest(
            systemId,
            "reservation-tests",
            WorkerId.New(),
            definition,
            committedSubject,
            requestContext,
            DateTimeOffset.UtcNow,
            Transaction: null);

        await store.ReserveIdempotency(committed);
        var duplicate = await Assert.ThrowsAsync<WorkQueueDurabilityDuplicateException>(() =>
            store.ReserveIdempotency(committed with { WorkerId = WorkerId.New() }));
        Assert.Contains(committedSubject.ToString(), duplicate.Message, StringComparison.Ordinal);

        var rolledBackSubject = new WorkSubjectId("coverage", $"rolled-back-{Guid.NewGuid():N}");
        await using (var connection = await this.OpenConnection())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await store.ReserveIdempotency(committed with
            {
                WorkerId = WorkerId.New(),
                SubjectId = rolledBackSubject,
                Transaction = new WorkableSqlServerQueueDurabilityTransaction(connection, transaction),
            });
            await transaction.RollbackAsync();
        }

        await store.ReserveIdempotency(committed with
        {
            WorkerId = WorkerId.New(),
            SubjectId = rolledBackSubject,
        });
    }

    [Fact]
    public void DurabilitySerializationHandlesNullsDefaultsAndReadOnlySets()
    {
        Assert.Equal("default", InvokeDurabilityHelper<string>("NormalizeWorkSystemName", (object?)null));
        Assert.Equal("default", InvokeDurabilityHelper<string>("NormalizeWorkSystemName", "   "));
        Assert.Equal("orders", InvokeDurabilityHelper<string>("NormalizeWorkSystemName", "orders"));
        Assert.Null(InvokeGenericDurabilityHelper("Serialize", typeof(object), null));
        Assert.Contains("value", InvokeGenericDurabilityHelper("Serialize", typeof(object), new { value = 1 }) as string);
        Assert.Null(InvokeDurabilityHelper<object?>("SerializeWorkerOptions", (object?)null));
        Assert.NotNull(InvokeDurabilityHelper<object?>("SerializeWorkerOptions", new WorkerOptions()));

        var factoryType = typeof(WorkableSqlServerQueueDurabilityStore).GetNestedType(
            "IReadOnlySetJsonConverterFactory",
            BindingFlags.NonPublic)!;
        var factory = Assert.IsAssignableFrom<JsonConverterFactory>(Activator.CreateInstance(factoryType));
        Assert.True(factory.CanConvert(typeof(IReadOnlySet<string>)));
        Assert.False(factory.CanConvert(typeof(List<string>)));

        var options = new JsonSerializerOptions();
        options.Converters.Add(factory);
        var serialized = JsonSerializer.Serialize<IReadOnlySet<string>>(new HashSet<string> { "a", "b" }, options);
        var roundTrip = JsonSerializer.Deserialize<IReadOnlySet<string>>(serialized, options);
        var fromNull = JsonSerializer.Deserialize<IReadOnlySet<string>>("null", options);

        Assert.NotNull(roundTrip);
        Assert.True(roundTrip.SetEquals(["a", "b"]));
        Assert.Null(fromNull);
    }

    [Fact]
    public void DurabilityPayloadSerializationCoversOptionalAndPersistentConcurrencyShapes()
    {
        var systemId = WorkSystemId.New();
        var explicitOptions = new WorkerOptions(ProfilingEnabled: true)
        {
            ProfilingCaptureMode = WorkProfileCaptureMode.Full,
        };
        var persistent = CreateDurableEnqueueRequest(
            systemId,
            "orders",
            WorkerId.New(),
            "orders.persistent",
            "100",
            transaction: null,
            enableIdempotency: true,
            options: explicitOptions);
        var nonPersistent = CreateNonConcurrentDurableEnqueueRequest(
            systemId,
            WorkerId.New(),
            "orders.non-persistent",
            "200");
        var withoutInput = new WorkQueueDurabilityEnqueueRequest(
            systemId,
            " ",
            WorkerId.New(),
            WorkDefinition.Create("orders.no-input"),
            null,
            WorkerOptions.Default,
            WorkConfiguration.Default,
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            DateTimeOffset.UtcNow,
            Idempotency: null,
            Transaction: null);
        var withConcurrencyAndProvenance = withoutInput with
        {
            Input = WorkInput.Empty.WithConcurrencyKey(new WorkConcurrencyKey("tenant", "west")),
            WorkflowProvenance = new WorkflowProvenance(
                WorkflowRunId.New(),
                "orders.workflow",
                "dispatch"),
        };

        var persistentJson = SerializePrivatePayload("CreateEnqueuePayload", persistent);
        var nonPersistentJson = SerializePrivatePayload("CreateEnqueuePayload", nonPersistent);
        var withoutInputJson = SerializePrivatePayload("CreateEnqueuePayload", withoutInput);
        var concurrencyJson = SerializePrivatePayload("CreateEnqueuePayload", withConcurrencyAndProvenance);

        Assert.Contains("\"hasPersistentConcurrency\":true", persistentJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"hasIdempotencyReservation\":true", persistentJson, StringComparison.OrdinalIgnoreCase);
        using var persistentDocument = JsonDocument.Parse(persistentJson);
        var optionsJson = persistentDocument.RootElement.GetProperty("optionsJson").GetString();
        Assert.Contains("\"profilingEnabled\":true", optionsJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"profilingCaptureMode\":\"Full\"", optionsJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"hasPersistentConcurrency\":false", nonPersistentJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"concurrencyScope\":null", nonPersistentJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"workSystemName\":\"default\"", withoutInputJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"subjectType\":null", withoutInputJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"inputJson\":null", withoutInputJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"concurrencyType\":\"tenant\"", concurrencyJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"concurrencyValue\":\"west\"", concurrencyJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("orders.workflow", concurrencyJson, StringComparison.OrdinalIgnoreCase);

        var lease = new WorkQueueDurabilityLease(WorkerId.New(), "owner", "lease");
        var cleanupJson = InvokeDurabilityHelper<string>(
            "SerializeCleanupRequests",
            new WorkQueueDurabilityCleanupRequest[]
            {
                new(lease.WorkerId, lease),
                new(WorkerId.New(), null),
            });
        var failureJson = InvokeDurabilityHelper<string>(
            "SerializeFailureRequests",
            new WorkQueueDurabilityFailureRequest[]
            {
                new(lease.WorkerId, lease, DateTimeOffset.UtcNow, [WorkMessage.Error("failed", "Failed")]),
                new(WorkerId.New(), null, DateTimeOffset.UtcNow, []),
            });
        var renewalJson = InvokeDurabilityHelper<string>(
            "SerializeRenewalLeases",
            new WorkQueueDurabilityLease[] { lease });
        Assert.Contains("lease", cleanupJson, StringComparison.Ordinal);
        Assert.Contains("\"leaseId\":null", cleanupJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("failed", failureJson, StringComparison.Ordinal);
        Assert.Contains("lease", renewalJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DurabilityDeserializationSupportsNullPayloadEnvelopeAndLegacyOriginRows()
    {
        using var nullReader = CreateStringReader(DBNull.Value);
        Assert.True(nullReader.Read());
        Assert.Null(InvokeGenericDurabilityReader("Deserialize", typeof(WorkInput), nullReader, 0));
        Assert.Null(InvokeDurabilityReader("DeserializeWorkerOptions", nullReader, 0));
        Assert.Null(InvokeDurabilityReader("DeserializeOptionalRequestContext", nullReader, 0));
        var fallback = Assert.IsType<WorkRequestContext>(
            InvokeDurabilityReader("DeserializeRequestContext", nullReader, 0));
        Assert.Equal(WorkInvocationChannel.InProcess, fallback.Origin.Channel);

        var origin = WorkOrigin.Create(WorkInvocationChannel.HttpApi);
        var envelopeJson = JsonSerializer.Serialize(new
        {
            origin,
            description = "persisted request",
            url = "/orders",
            authorization = (WorkAuthorizationSnapshot?)null,
            isAuthenticated = true,
        }, DurableJsonOptions);
        using var envelopeReader = CreateStringReader(envelopeJson);
        Assert.True(envelopeReader.Read());
        var envelope = Assert.IsType<WorkRequestContext>(
            InvokeDurabilityReader("DeserializeRequestContext", envelopeReader, 0));
        Assert.Equal("persisted request", envelope.Description);
        Assert.Equal("/orders", envelope.Url);
        Assert.True(envelope.IsAuthenticated);

        using var legacyReader = CreateStringReader(JsonSerializer.Serialize(origin, DurableJsonOptions));
        Assert.True(legacyReader.Read());
        var legacy = Assert.IsType<WorkRequestContext>(
            InvokeDurabilityReader("DeserializeRequestContext", legacyReader, 0));
        Assert.Equal(WorkInvocationChannel.HttpApi, legacy.Origin.Channel);

        var persistedOptionsType = typeof(WorkableSqlServerQueueDurabilityStore).GetNestedType(
            "PersistedWorkerOptions",
            BindingFlags.NonPublic)!;
        var withFlags = Activator.CreateInstance(
            persistedOptionsType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [true, WorkProfileCaptureMode.Full, WorkConfiguration.Default],
            culture: null)!;
        var defaults = Activator.CreateInstance(
            persistedOptionsType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            args: [null, null, WorkConfiguration.Default],
            culture: null)!;
        var explicitWorkerOptions = Assert.IsType<WorkerOptions>(
            persistedOptionsType.GetMethod("ToWorkerOptions")!.Invoke(withFlags, null));
        var defaultWorkerOptions = Assert.IsType<WorkerOptions>(
            persistedOptionsType.GetMethod("ToWorkerOptions")!.Invoke(defaults, null));
        Assert.True(explicitWorkerOptions.ProfilingEnabled);
        Assert.Equal(WorkProfileCaptureMode.Full, explicitWorkerOptions.ProfilingCaptureMode);
        Assert.False(defaultWorkerOptions.HasExplicitProfilingEnabled);
        Assert.False(defaultWorkerOptions.HasExplicitProfilingCaptureMode);
    }

    [Theory]
    [InlineData(-2, 0, true)]
    [InlineData(2, 0, true)]
    [InlineData(53, 0, true)]
    [InlineData(64, 0, true)]
    [InlineData(233, 0, true)]
    [InlineData(4060, 0, true)]
    [InlineData(18456, 0, true)]
    [InlineData(50000, 20, true)]
    [InlineData(50000, 16, false)]
    public void ClassifyEverySqlServerAvailabilityError(int number, byte errorClass, bool expected)
    {
        var exception = CreateSqlException(number, errorClass);

        Assert.Equal(expected, InvokeDurabilityHelper<bool>("IsStoreUnavailable", exception));
        Assert.Equal(
            expected,
            (bool)typeof(WorkableSqlServerExecutionDiagnosticsRepository)
                .GetMethod("IsStoreUnavailable", BindingFlags.Static | BindingFlags.NonPublic)!
                .Invoke(null, [exception])!);
    }

    [Fact]
    public void ReadOnlySetConverterFactoryRejectsUnrelatedShapesAndRoundTripsSets()
    {
        var factoryType = typeof(WorkableSqlServerQueueDurabilityStore)
            .GetNestedType("IReadOnlySetJsonConverterFactory", BindingFlags.NonPublic)!;
        var factory = Assert.IsAssignableFrom<JsonConverterFactory>(Activator.CreateInstance(factoryType, nonPublic: true));

        Assert.False(factory.CanConvert(typeof(string)));
        Assert.False(factory.CanConvert(typeof(List<string>)));
        Assert.True(factory.CanConvert(typeof(IReadOnlySet<string>)));

        var options = new JsonSerializerOptions();
        options.Converters.Add(factory);
        IReadOnlySet<string> source = new HashSet<string>(["alpha", "beta"], StringComparer.Ordinal);
        var json = JsonSerializer.Serialize(source, options);
        var restored = JsonSerializer.Deserialize<IReadOnlySet<string>>(json, options);

        Assert.NotNull(restored);
        Assert.True(restored.SetEquals(source));
    }

    [Theory]
    [InlineData("queue", true)]
    [InlineData("queue", false)]
    [InlineData("workflow", true)]
    [InlineData("workflow", false)]
    [InlineData("diagnostics", true)]
    [InlineData("diagnostics", false)]
    public async Task InitializationClassifiesAMissingDatabaseAsStoreUnavailable(
        string component,
        bool autoDeploySchema)
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var connectionString = new SqlConnectionStringBuilder(this.ConnectionString)
        {
            InitialCatalog = $"workable_missing_{Guid.NewGuid():N}",
            ConnectTimeout = 1,
        }.ConnectionString;
        var systemId = WorkSystemId.New();

        var exception = component switch
        {
            "queue" => await Assert.ThrowsAsync<WorkPersistenceStoreUnavailableException>(() =>
                new WorkableSqlServerQueueDurabilityStore(new WorkableSqlServerQueueDurabilityOptions
                {
                    ConnectionString = connectionString,
                    AutoDeploySchema = autoDeploySchema,
                }).Initialize(new WorkQueueDurabilityInitializationContext(systemId, "coverage", []))),
            "workflow" => await Assert.ThrowsAsync<WorkPersistenceStoreUnavailableException>(() =>
                new WorkableSqlServerQueueDurabilityStore(new WorkableSqlServerQueueDurabilityOptions
                {
                    ConnectionString = connectionString,
                    AutoDeploySchema = autoDeploySchema,
                }).InitializeWorkflows(new WorkflowPersistenceInitializationContext("coverage", []))),
            "diagnostics" => await Assert.ThrowsAsync<WorkPersistenceStoreUnavailableException>(() =>
                new WorkableSqlServerExecutionDiagnosticsRepository(new WorkableSqlServerPersistenceOptions
                {
                    ConnectionString = connectionString,
                    AutoDeploySchema = autoDeploySchema,
                }).Initialize(new WorkExecutionDiagnosticsInitializationContext(systemId, "coverage"))),
            _ => throw new InvalidOperationException($"Unknown persistence component '{component}'."),
        };

        Assert.Contains("could not reach SQL Server", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<SqlException>(exception.InnerException);
    }

    [Fact]
    public async Task UnavailableDiagnosticsDatabaseDoesNotFailMultiSystemHostStartup()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var connectionString = new SqlConnectionStringBuilder(this.ConnectionString)
        {
            InitialCatalog = $"workable_missing_host_{Guid.NewGuid():N}",
            ConnectTimeout = 1,
        }.ConnectionString;
        var services = new ServiceCollection()
            .AddWorkableSqlServerPersistence(connectionString)
            .AddWorkableSystem(builder => builder
                .StartWithHost()
                .RequireAuthorization(false))
            .AddWorkableSystem("second", builder => builder
                .StartWithHost()
                .RequireAuthorization(false));
        await using var provider = services.BuildServiceProvider();
        var hostedService = Assert.Single(provider.GetServices<IHostedService>());
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(registry.TryGet("second", out var second));

        await hostedService.StartAsync(CancellationToken.None);

        Assert.Equal(WorkSystemState.Started, registry.Default.State);
        Assert.Equal(WorkSystemState.Started, second.State);
        Assert.False((await registry.Default.CreateSession(
            WorkRequestContext.Create(WorkInvocationChannel.InProcess))).Capabilities.ExecutionDiagnosticsPersistenceAvailable);
        Assert.False((await second.CreateSession(
            WorkRequestContext.Create(WorkInvocationChannel.InProcess))).Capabilities.ExecutionDiagnosticsPersistenceAvailable);

        await hostedService.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("workflow-list")]
    [InlineData("claim-ready")]
    [InlineData("claim-failed")]
    [InlineData("renew")]
    [InlineData("retain-failed")]
    [InlineData("delete-final")]
    public async Task QueueOperationsClassifyAMissingDatabaseAsStoreUnavailable(string operation)
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var connectionString = new SqlConnectionStringBuilder(this.ConnectionString)
        {
            InitialCatalog = $"workable_missing_operation_{Guid.NewGuid():N}",
            ConnectTimeout = 1,
        }.ConnectionString;
        var store = new WorkableSqlServerQueueDurabilityStore(new WorkableSqlServerQueueDurabilityOptions
        {
            ConnectionString = connectionString,
        });
        var workerId = WorkerId.New();
        var lease = new WorkQueueDurabilityLease(workerId, "coverage-owner", "coverage-lease");

        var exception = await Assert.ThrowsAsync<WorkPersistenceStoreUnavailableException>(async () =>
        {
            switch (operation)
            {
                case "workflow-list":
                    await foreach (var _ in store.ListWorkflowRuns(new WorkflowPersistenceReadRequest("coverage")))
                    {
                    }
                    break;
                case "claim-ready":
                    await ClaimReady(store, "coverage-owner", 1, "coverage");
                    break;
                case "claim-failed":
                    await ClaimFailed(store, "coverage-owner", 1, "coverage");
                    break;
                case "renew":
                    await store.RenewLeases([lease], TimeSpan.FromMinutes(1));
                    break;
                case "retain-failed":
                    await store.RetainFailed([
                        new WorkQueueDurabilityFailureRequest(
                            workerId,
                            lease,
                            DateTimeOffset.UtcNow,
                            [WorkMessage.Error("coverage", "coverage")]),
                    ]);
                    break;
                case "delete-final":
                    await store.DeleteFinal([new WorkQueueDurabilityCleanupRequest(workerId, lease)]);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown operation '{operation}'.");
            }
        });

        Assert.Contains("could not reach SQL Server", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<SqlException>(exception.InnerException);
    }

    [Fact]
    public async Task DurableQueueRoundTripsSystemAssignedWorkflowProvenance()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var systemName = $"workflow-provenance-{Guid.NewGuid():N}";
        var subject = $"workflow-provenance-{Guid.NewGuid():N}";
        var workerId = WorkerId.New();
        var provenance = new WorkflowProvenance(
            WorkflowRunId.New(),
            "workflow.sql.provenance",
            "dispatch");
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        await store.Enqueue(CreateDurableEnqueueRequest(
            WorkSystemId.New(),
            systemName,
            workerId,
            "sample.dispatch",
            subject,
            transaction: null) with
        {
            WorkflowProvenance = provenance,
        });

        var ready = Assert.Single(await ClaimReady(
            store,
            "workflow-provenance-ready",
            batchSize: 10,
            workSystemName: systemName));
        Assert.Equal(provenance, ready.WorkflowProvenance);

        await store.RetainFailed([
            new WorkQueueDurabilityFailureRequest(
                workerId,
                ready.Lease,
                DateTimeOffset.UtcNow,
                [WorkMessage.Error("workflow.provenance.test", "Retained for provenance round-trip testing.")]),
        ]);
        await using (var connection = await this.OpenConnection())
        {
            await ExpireLease(connection, subject);
        }

        var failed = Assert.Single(await ClaimFailed(
            store,
            "workflow-provenance-failed",
            batchSize: 10,
            workSystemName: systemName));
        Assert.Equal(provenance, failed.WorkflowProvenance);
    }

    [Fact]
    public async Task FailedClaimRestoresLegacyNullPayloadColumnsWithSafeDefaults()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var systemName = $"failed-null-payload-{Guid.NewGuid():N}";
        var workerId = WorkerId.New();
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        await store.Enqueue(CreateDurableEnqueueRequest(
            WorkSystemId.New(),
            systemName,
            workerId,
            "sample.dispatch",
            $"failed-null-payload-{Guid.NewGuid():N}",
            transaction: null));
        var ready = Assert.Single(await ClaimReady(store, "failed-null-ready", 10, systemName));
        await store.RetainFailed([
            new WorkQueueDurabilityFailureRequest(
                workerId,
                ready.Lease,
                DateTimeOffset.UtcNow,
                [WorkMessage.Error("temporary", "This payload is cleared to simulate a legacy row.")]),
        ]);

        await using (var connection = await this.OpenConnection())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
UPDATE workable.WorkEntries
SET InputJson = NULL,
    OptionsJson = NULL,
    ConfigurationJson = NULL,
    OriginJson = N'null',
    WorkflowProvenanceJson = NULL,
    FailureMessagesJson = NULL
WHERE WorkerId = @WorkerId;
UPDATE workable.WorkQueueEntries
SET LeaseExpiresAt = NULL
WHERE WorkerId = @WorkerId;
""";
            command.Parameters.AddWithValue("@WorkerId", workerId.Value);
            await command.ExecuteNonQueryAsync();
        }

        var failed = Assert.Single(await ClaimFailed(store, "failed-null-claim", 10, systemName));
        Assert.Null(failed.Input);
        Assert.Equal(WorkerOptions.Default, failed.Options);
        Assert.Equal(WorkConfiguration.Default, failed.Configuration);
        Assert.Equal(WorkInvocationChannel.InProcess, failed.RequestContext.Origin.Channel);
        Assert.Null(failed.WorkflowProvenance);
        var warning = Assert.Single(failed.Messages);
        Assert.Equal("workable.queue_durability.legacy_failure_restored", warning.Code);
    }

    [Fact]
    public async Task ReadyClaimRestoresLegacyNullPayloadColumnsWithSafeDefaults()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var systemName = $"ready-null-payload-{Guid.NewGuid():N}";
        var workerId = WorkerId.New();
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        await store.Enqueue(CreateDurableEnqueueRequest(
            WorkSystemId.New(),
            systemName,
            workerId,
            "sample.dispatch",
            $"ready-null-payload-{Guid.NewGuid():N}",
            transaction: null));

        await using (var connection = await this.OpenConnection())
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
UPDATE workable.WorkEntries
SET InputJson = NULL,
    OptionsJson = NULL,
    ConfigurationJson = NULL,
    OriginJson = N'null',
    WorkflowProvenanceJson = NULL
WHERE WorkerId = @WorkerId;
""";
            command.Parameters.AddWithValue("@WorkerId", workerId.Value);
            await command.ExecuteNonQueryAsync();
        }

        var ready = Assert.Single(await ClaimReady(store, "ready-null-claim", 10, systemName));
        Assert.Null(ready.Input);
        Assert.Equal(WorkerOptions.Default, ready.Options);
        Assert.Equal(WorkConfiguration.Default, ready.Configuration);
        Assert.Equal(WorkInvocationChannel.InProcess, ready.RequestContext.Origin.Channel);
        Assert.Null(ready.WorkflowProvenance);
    }

    [Fact]
    public async Task DuplicateWorkerWithoutSubjectUsesTheSafeDiagnosticPlaceholder()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        var store = new WorkableSqlServerQueueDurabilityStore(new WorkableSqlServerQueueDurabilityOptions
        {
            ConnectionString = this.ConnectionString,
            EnqueueBatchSize = 1,
        });
        var workerId = WorkerId.New();
        var request = CreateDurableEnqueueRequest(
            WorkSystemId.New(),
            $"duplicate-worker-{Guid.NewGuid():N}",
            workerId,
            "sample.dispatch",
            $"unused-{Guid.NewGuid():N}",
            transaction: null) with
        {
            Input = null,
            Idempotency = null,
        };

        await store.Enqueue(request);
        var exception = await Assert.ThrowsAsync<WorkQueueDurabilityDuplicateException>(() => store.Enqueue(request));

        Assert.Contains("<none>", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DurableQueueRoundTripsExplicitProfilingCaptureModes()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var systemName = $"profiling-capture-mode-{Guid.NewGuid():N}";
        const string fullDefinitionName = "profiling-capture-mode-full";
        const string boundedDefinitionName = "profiling-capture-mode-bounded";
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        var systemId = WorkSystemId.New();
        var fullWorkerId = WorkerId.New();
        var boundedWorkerId = WorkerId.New();

        await store.Enqueue(CreateDurableEnqueueRequest(
            systemId,
            systemName,
            fullWorkerId,
            fullDefinitionName,
            "profiling-full",
            transaction: null,
            enableIdempotency: false,
            options: new WorkerOptions
            {
                ProfilingEnabled = true,
                ProfilingCaptureMode = WorkProfileCaptureMode.Full,
            }));
        await store.Enqueue(CreateDurableEnqueueRequest(
            systemId,
            systemName,
            boundedWorkerId,
            boundedDefinitionName,
            "profiling-bounded",
            transaction: null,
            enableIdempotency: false,
            options: new WorkerOptions
            {
                ProfilingEnabled = true,
                ProfilingCaptureMode = WorkProfileCaptureMode.Bounded,
            }));

        var claimed = await ClaimReady(
            store,
            "profiling-consumer",
            batchSize: 10,
            workSystemName: systemName);
        var byWorkerId = claimed.ToDictionary(entry => entry.Lease.WorkerId);

        Assert.Equal(WorkProfileCaptureMode.Full, byWorkerId[fullWorkerId].Options.ProfilingCaptureMode);
        Assert.True(byWorkerId[fullWorkerId].Options.HasExplicitProfilingCaptureMode);
        Assert.Equal(WorkProfileCaptureMode.Bounded, byWorkerId[boundedWorkerId].Options.ProfilingCaptureMode);
        Assert.True(byWorkerId[boundedWorkerId].Options.HasExplicitProfilingCaptureMode);
    }

    [Fact]
    public async Task WorkflowTransactionRollbackDiscardsWorkflowRunsAndDurableWorkersTogether()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        var run = CreateWorkflowRun("workflow-tests", "workflow.sql.transaction.rollback");
        var workerId = WorkerId.New();

        await using (var transaction = await store.BeginWorkflowTransaction(
            new WorkflowPersistenceTransactionRequest("workflow-tests")))
        {
            await store.UpsertWorkflowRun(run, transaction);
            await store.Enqueue(CreateDurableEnqueueRequest(
                WorkSystemId.New(),
                "workflow-tests",
                workerId,
                "sample.dispatch",
                "workflow-transaction-rollback",
                transaction));
        }

        await using var connection = await this.OpenConnection();
        Assert.Equal(0, await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkflowRuns;
"""));
        Assert.Equal(0, await CountRowsForSubject(connection, "workflow-transaction-rollback"));
    }

    [Fact]
    public async Task DurableWorkflowRecoveryFailsWhenTheRegisteredDefinitionShapeChanged()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var workflowDefinitionId = WorkflowDefinitionId.New();
        const string workflowName = "workflow.sql.recovery.definition-changed";
        await using var firstProvider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .AddWorkableSystem("workflow-tests", builder =>
            {
                builder.RequireAuthorization(false);
                builder.AddWork(
                    WorkDefinition.Create("sample.alpha"),
                    async (_, _, cancellationToken) =>
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return WorkExecutionResult.Success();
                    },
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        workflowName,
                        id: workflowDefinitionId,
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow
                        .DispatchWork("dispatch", WorkDefinition.Create("sample.alpha"))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var firstRegistry = firstProvider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(firstRegistry.TryGet("workflow-tests", out var firstNamedSystem));
        var firstSystem = firstNamedSystem!;
        await firstSystem.Start();

        var handle = await StartWorkflow(firstSystem, workflowName);
        var runId = RequiredWorkflowRunId(handle);
        await TestEventually.Until(
            () => WorkflowStatus(firstSystem, runId) == WorkflowRunStatus.Running.ToString(),
            "Expected the durable workflow to start before restart.",
            timeout: TimeSpan.FromSeconds(15));
        await using (var persistedRunConnection = await this.OpenConnection())
        {
            await TestEventually.Until(
                async () => await Scalar<int>(persistedRunConnection, $"""
SELECT COUNT(*)
FROM workable.WorkflowRuns
WHERE RunId = '{runId.Value:D}';
""") == 1,
                "Expected the durable workflow run to be persisted before shutdown.",
                timeout: TimeSpan.FromSeconds(15));
            await TestEventually.Until(
                async () => await Scalar<int>(persistedRunConnection, """
SELECT COUNT(*)
FROM workable.WorkEntries
WHERE WorkSystemName = N'workflow-tests'
  AND DefinitionName = N'sample.alpha';
""") == 1,
                "Expected the durable workflow child worker to be persisted before shutdown.",
                timeout: TimeSpan.FromSeconds(15));
        }

        await StopWithTimeout(firstSystem);
        var canceled = await WaitForWorkflowCompletion(handle);
        Assert.Equal(WorkflowRunStatus.Canceled.ToString(), WorkflowCompletionStatus(canceled));

        await using var secondProvider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .AddWorkableSystem("workflow-tests", builder =>
            {
                builder.RequireAuthorization(false);
                builder.AddWork(
                    WorkDefinition.Create("sample.alpha"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWork(
                    WorkDefinition.Create("sample.beta"),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configuration => configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(1)));
                builder.AddWorkflow(
                    WorkflowDefinition.Create(
                        workflowName,
                        id: workflowDefinitionId,
                        coordination: WorkflowCoordinationConfiguration.Durable),
                    workflow => workflow
                        .DispatchWork("dispatch", WorkDefinition.Create("sample.alpha"))
                        .DispatchWork("archive", WorkDefinition.Create("sample.beta"))
                        .Join("join"));
            })
            .BuildServiceProvider();
        var secondRegistry = secondProvider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(secondRegistry.TryGet("workflow-tests", out var secondNamedSystem));
        var secondSystem = secondNamedSystem!;
        await secondSystem.Start();

        await TestEventually.Until(
            () => WorkflowStatus(secondSystem, runId) == WorkflowRunStatus.Failed.ToString(),
            "Expected the changed durable workflow definition to reject recovery of the persisted run.",
            timeout: TimeSpan.FromSeconds(15));

        var snapshot = WorkflowSnapshot(secondSystem, runId)
            ?? throw new InvalidOperationException("Expected failed recovered workflow snapshot.");
        var messages = (System.Collections.IEnumerable)(snapshot.GetType().GetProperty("Messages")?.GetValue(snapshot)
            ?? throw new InvalidOperationException("Expected workflow messages."));
        Assert.Contains(
            messages.Cast<object>(),
            message => string.Equals(
                message.GetType().GetProperty("Code")?.GetValue(message)?.ToString(),
                "workable.workflow.definition_mismatch",
                StringComparison.Ordinal));

        await using var connection = await this.OpenConnection();
        Assert.Equal(1, await Scalar<int>(connection, $"""
SELECT COUNT(*)
FROM workable.WorkflowRuns
WHERE RunId = '{runId.Value:D}';
"""));

        await StopWithTimeout(secondSystem);
    }

    [Fact]
    public async Task DirectSqlClientExecutionDoesNotAppearInWorkerProfileWhenSqlServerProfilingIsNotConfigured()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-profiled-direct-command";
        using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(
                this.ConnectionString,
                SchemaName)
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Profiles Microsoft.Data.SqlClient command execution.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (context, input, cancellationToken) =>
                {
                    await using var connection = new SqlConnection(this.ConnectionString);
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT @Value;";
                    command.Parameters.AddWithValue("@Value", 42);

                    var value = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
                    return value == 42
                        ? WorkExecutionResult.Success()
                        : WorkExecutionResult.Failure([WorkMessage.Error("sql.value.unexpected", $"Expected 42 but received {value}.")]);
                }))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await WaitForCompletion(await system.Queue.Enqueue(workName));

        Assert.True(completion.IsCompletedSuccessfully);
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected worker snapshot.");
        var profile = worker.Profile ?? throw new InvalidOperationException("Expected worker profile.");
        Assert.DoesNotContain(
            Flatten(profile.Root),
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirectSqlClientExecutionAppearsInWorkerProfileWhenSqlServerProfilingIsConfigured()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-profiled-direct-command";
        using var provider = new ServiceCollection()
            .AddWorkableSqlServerProfiling()
            .AddWorkableSqlServerDurableQueue(
                this.ConnectionString,
                SchemaName)
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Profiles Microsoft.Data.SqlClient command execution.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (context, input, cancellationToken) =>
                {
                    await using var connection = new SqlConnection(this.ConnectionString);
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT @Value;";
                    command.Parameters.AddWithValue("@Value", 42);

                    var value = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
                    return value == 42
                        ? WorkExecutionResult.Success()
                        : WorkExecutionResult.Failure([WorkMessage.Error("sql.value.unexpected", $"Expected 42 but received {value}.")]);
                }))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await WaitForCompletion(await system.Queue.Enqueue(workName));

        Assert.True(completion.IsCompletedSuccessfully);
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected worker snapshot.");
        var profile = worker.Profile ?? throw new InvalidOperationException("Expected worker profile.");
        var sqlNode = Assert.Single(
            Flatten(profile.Root),
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
        var contextJson = JsonSerializer.Serialize(sqlNode.Context);

        Assert.Equal("sql.client", sqlNode.Instrumentation);
        Assert.Contains("Microsoft.Data.SqlClient", contextJson, StringComparison.Ordinal);
        Assert.Contains("ExecuteScalar", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"Statement\":\"SELECT @Value;\"", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"ParameterCount\":1", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"Name\":\"@Value\"", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"Value\":42", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"IsRedacted\":false", contextJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedSqlClientExecutionAddsFailureToItsSingleTimingNode()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-profiled-failed-command";
        using var provider = new ServiceCollection()
            .AddWorkableSqlServerProfiling()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Profiles a failed Microsoft.Data.SqlClient command once.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (context, input, cancellationToken) =>
                {
                    await using var connection = new SqlConnection(this.ConnectionString);
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT * FROM workable.__profiling_missing_table;";
                    try
                    {
                        await command.ExecuteScalarAsync(cancellationToken);
                    }
                    catch (SqlException)
                    {
                        return WorkExecutionResult.Success();
                    }

                    return WorkExecutionResult.Failure([
                        WorkMessage.Error("sql.failure.expected", "The deliberately invalid statement unexpectedly succeeded."),
                    ]);
                }))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await WaitForCompletion(await system.Queue.Enqueue(workName));

        Assert.True(completion.IsCompletedSuccessfully);
        var profile = completion.Worker?.Profile ?? throw new InvalidOperationException("Expected worker profile.");
        var nodes = Flatten(profile.Root).ToList();
        var sqlNode = Assert.Single(
            nodes,
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
        var contextJson = JsonSerializer.Serialize(sqlNode.Context);

        Assert.DoesNotContain(nodes, node => node.Label == "SQL Error");
        Assert.Contains("\"Outcome\":\"Faulted\"", contextJson, StringComparison.Ordinal);
        Assert.Contains("Microsoft.Data.SqlClient.SqlException", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"MessageTruncated\":false", contextJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SqlProfilingUsesSharedAutomaticInstrumentationLimitAndReportsOmissions()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-profiled-automatic-limit";
        using var provider = new ServiceCollection()
            .AddWorkableSqlServerProfiling()
            .AddWorkableSqlServerDurableQueue(
                this.ConnectionString,
                SchemaName)
            .AddWorkableSystem(builder =>
            {
                builder.ConfigureProfiling(maximumAutomaticInstrumentationNodes: 1);
                builder.AddWork(
                    WorkDefinition.Create(
                        workName,
                        "Bounds automatic SQL profile nodes.",
                        defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                    async (context, input, cancellationToken) =>
                    {
                        await using var connection = new SqlConnection(this.ConnectionString);
                        await connection.OpenAsync(cancellationToken);
                        for (var value = 1; value <= 2; value++)
                        {
                            await using var command = connection.CreateCommand();
                            command.CommandText = "SELECT @Value;";
                            command.Parameters.AddWithValue("@Value", value);
                            await command.ExecuteScalarAsync(cancellationToken);
                        }

                        return WorkExecutionResult.Success();
                    });
            })
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await WaitForCompletion(await system.Queue.Enqueue(workName));

        Assert.True(completion.IsCompletedSuccessfully);
        var profile = completion.Worker?.Profile ?? throw new InvalidOperationException("Expected worker profile.");
        Assert.Single(
            Flatten(profile.Root),
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
        var summary = Assert.Single(
            Flatten(profile.Root),
            node => node.Label == "Automatic instrumentation truncated");
        Assert.Contains("sql.client", JsonSerializer.Serialize(summary.Context), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SqlProfilingOmitsBinaryParameterValuesFromWorkerProfile()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-profiled-full-values";
        var description = new string('x', 320);
        var payload = Enumerable.Range(0, 64)
            .Select(static index => (byte)index)
            .ToArray();
        var statement = """
            SELECT
                @Description AS Description,
                DATALENGTH(@Payload) AS PayloadLength;
            """;

        using var provider = new ServiceCollection()
            .AddWorkableSqlServerProfiling()
            .AddWorkableSqlServerDurableQueue(
                this.ConnectionString,
                SchemaName)
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Profiles Microsoft.Data.SqlClient command execution without retaining binary parameter values.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (context, input, cancellationToken) =>
                {
                    await using var connection = new SqlConnection(this.ConnectionString);
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = statement;
                    command.Parameters.AddWithValue("@Description", description);
                    command.Parameters.Add("@Payload", SqlDbType.VarBinary, payload.Length).Value = payload;

                    await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                    await reader.ReadAsync(cancellationToken);
                    var payloadLength = reader.GetInt32(reader.GetOrdinal("PayloadLength"));

                    return payloadLength == payload.Length
                        ? WorkExecutionResult.Success()
                        : WorkExecutionResult.Failure([WorkMessage.Error("sql.payload.unexpected", $"Expected {payload.Length} but received {payloadLength}.")]);
                }))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await WaitForCompletion(await system.Queue.Enqueue(workName));

        Assert.True(completion.IsCompletedSuccessfully);
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected worker snapshot.");
        var profile = worker.Profile ?? throw new InvalidOperationException("Expected worker profile.");
        var sqlNode = Assert.Single(
            Flatten(profile.Root),
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
        var contextJson = JsonSerializer.Serialize(sqlNode.Context);

        Assert.Contains($"\"Statement\":{JsonSerializer.Serialize(statement)}", contextJson, StringComparison.Ordinal);
        Assert.Contains($"\"Value\":{JsonSerializer.Serialize(description)}", contextJson, StringComparison.Ordinal);
        Assert.Contains($"\"Value\":{JsonSerializer.Serialize("<binary omitted>")}", contextJson, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToHexString(payload), contextJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SqlProfilingBoundsLargeStatementsAndOmitsBinaryParameterValues()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-profiled-bounded-values";
        var text = new string('t', 8_000);
        var payload = Enumerable.Range(0, 2_048).Select(static index => (byte)index).ToArray();
        var statement = "SELECT LEN(@Text), DATALENGTH(@Payload); --" + new string('s', 40_000);
        using var provider = new ServiceCollection()
            .AddWorkableSqlServerProfiling()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Bounds large SQL profile context values.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (context, input, cancellationToken) =>
                {
                    await using var connection = new SqlConnection(this.ConnectionString);
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = statement;
                    command.Parameters.AddWithValue("@Text", text);
                    command.Parameters.Add("@Payload", SqlDbType.VarBinary, payload.Length).Value = payload;
                    await command.ExecuteScalarAsync(cancellationToken);
                    return WorkExecutionResult.Success();
                }))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await WaitForCompletion(await system.Queue.Enqueue(workName));

        Assert.True(completion.IsCompletedSuccessfully);
        var sqlNode = Assert.Single(
            Flatten(completion.Worker!.Profile!.Root),
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(sqlNode.Context));
        var root = json.RootElement;
        var parameters = root.GetProperty("Parameters").EnumerateArray().ToArray();
        var textParameter = Assert.Single(parameters, parameter => parameter.GetProperty("Name").GetString() == "@Text");
        var binaryParameter = Assert.Single(parameters, parameter => parameter.GetProperty("Name").GetString() == "@Payload");

        Assert.True(root.GetProperty("StatementTruncated").GetBoolean());
        Assert.Equal(8_192, root.GetProperty("Statement").GetString()!.Length);
        Assert.True(textParameter.GetProperty("IsTruncated").GetBoolean());
        Assert.Equal(1_024, textParameter.GetProperty("Value").GetString()!.Length);
        Assert.False(binaryParameter.GetProperty("IsTruncated").GetBoolean());
        Assert.Equal("<binary omitted>", binaryParameter.GetProperty("Value").GetString());
    }

    [Fact]
    public async Task SqlProfilingFinalizesOutstandingCommandsBeforePublishingProfileSnapshot()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-profiled-outstanding-command";
        await using var pendingConnection = new SqlConnection(this.ConnectionString);
        await using var pendingCommand = pendingConnection.CreateCommand();
        pendingCommand.CommandText = "WAITFOR DELAY '00:00:01'; SELECT 1;";
        Task<object?>? pendingExecution = null;
        using var provider = new ServiceCollection()
            .AddWorkableSqlServerProfiling()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Finalizes an outstanding SQL profile timing.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (context, input, cancellationToken) =>
                {
                    await pendingConnection.OpenAsync(cancellationToken);
                    pendingExecution = pendingCommand.ExecuteScalarAsync(CancellationToken.None);
                    return WorkExecutionResult.Success();
                }))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await WaitForCompletion(await system.Queue.Enqueue(workName));
        var sqlNode = Assert.Single(
            Flatten(completion.Worker!.Profile!.Root),
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
        var beforeCompletion = JsonSerializer.Serialize(sqlNode.Context);

        Assert.Contains("\"Outcome\":\"Incomplete\"", beforeCompletion, StringComparison.Ordinal);
        await (pendingExecution ?? throw new InvalidOperationException("Expected pending SQL execution."));
        Assert.Equal(beforeCompletion, JsonSerializer.Serialize(sqlNode.Context));
    }

    [Fact]
    public async Task ObviousSecretSqlParametersAreRedactedInWorkerProfile()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-profiled-secret-parameter";
        const string secretValue = "super-secret-value";
        using var provider = new ServiceCollection()
            .AddWorkableSqlServerProfiling()
            .AddWorkableSqlServerDurableQueue(
                this.ConnectionString,
                SchemaName)
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Profiles Microsoft.Data.SqlClient command execution with secret-like parameters redacted.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (context, input, cancellationToken) =>
                {
                    await using var connection = new SqlConnection(this.ConnectionString);
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT LEN(@Password);";
                    command.Parameters.AddWithValue("@Password", secretValue);

                    var length = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
                    return length == secretValue.Length
                        ? WorkExecutionResult.Success()
                        : WorkExecutionResult.Failure([WorkMessage.Error("sql.value.unexpected", $"Expected {secretValue.Length} but received {length}.")]);
                }))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await WaitForCompletion(await system.Queue.Enqueue(workName));

        Assert.True(completion.IsCompletedSuccessfully);
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected worker snapshot.");
        var profile = worker.Profile ?? throw new InvalidOperationException("Expected worker profile.");
        var sqlNode = Assert.Single(
            Flatten(profile.Root),
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
        var contextJson = JsonSerializer.Serialize(sqlNode.Context);

        Assert.Contains("\"Name\":\"@Password\"", contextJson, StringComparison.Ordinal);
        Assert.Contains("\\u003Credacted\\u003E", contextJson, StringComparison.Ordinal);
        Assert.Contains("\"IsRedacted\":true", contextJson, StringComparison.Ordinal);
        Assert.DoesNotContain(secretValue, contextJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DirectSqlClientExecutionAppearsInWorkerProfileWhenSqlServerProfilingStartsAfterSqlClientHasInitialized()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await using (var connection = await this.OpenConnection())
        {
            Assert.Equal(1, await Scalar<int>(connection, "SELECT 1;"));
        }

        const string workName = "sql-profiled-direct-command-existing-listener";
        using var provider = new ServiceCollection()
            .AddWorkableSqlServerProfiling()
            .AddWorkableSqlServerDurableQueue(
                this.ConnectionString,
                SchemaName)
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Profiles Microsoft.Data.SqlClient command execution after SqlClient already initialized.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (context, input, cancellationToken) =>
                {
                    await using var connection = new SqlConnection(this.ConnectionString);
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT @Value;";
                    command.Parameters.AddWithValue("@Value", 42);

                    var value = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
                    return value == 42
                        ? WorkExecutionResult.Success()
                        : WorkExecutionResult.Failure([WorkMessage.Error("sql.value.unexpected", $"Expected 42 but received {value}.")]);
                }))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await WaitForCompletion(await system.Queue.Enqueue(workName));

        Assert.True(completion.IsCompletedSuccessfully);
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected worker snapshot.");
        var profile = worker.Profile ?? throw new InvalidOperationException("Expected worker profile.");
        Assert.Contains(
            Flatten(profile.Root),
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SqlProfilingTracksOnlyTheOwningSystemWhenMultipleSystemsAreStarted()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-profiled-direct-command-multi-system";
        using var provider = new ServiceCollection()
            .AddWorkableSqlServerProfiling()
            .AddWorkableSystem("alpha", builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Profiles Microsoft.Data.SqlClient command execution for the owning system only.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (context, input, cancellationToken) =>
                {
                    await using var connection = new SqlConnection(this.ConnectionString);
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT @Value;";
                    command.Parameters.AddWithValue("@Value", 42);

                    var value = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
                    return value == 42
                        ? WorkExecutionResult.Success()
                        : WorkExecutionResult.Failure([WorkMessage.Error("sql.value.unexpected", $"Expected 42 but received {value}.")]);
                }))
            .AddWorkableSystem("beta", builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Profiles Microsoft.Data.SqlClient command execution for the owning system only.",
                    defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
                async (context, input, cancellationToken) =>
                {
                    await using var connection = new SqlConnection(this.ConnectionString);
                    await connection.OpenAsync(cancellationToken);
                    await using var command = connection.CreateCommand();
                    command.CommandText = "SELECT @Value;";
                    command.Parameters.AddWithValue("@Value", 7);

                    var value = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
                    return value == 7
                        ? WorkExecutionResult.Success()
                        : WorkExecutionResult.Failure([WorkMessage.Error("sql.value.unexpected", $"Expected 7 but received {value}.")]);
                }))
            .BuildServiceProvider();

        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        var systems = registry.Systems.ToDictionary(system => system.Name ?? string.Empty, StringComparer.OrdinalIgnoreCase);
        await using var alpha = systems["alpha"];
        await using var beta = systems["beta"];

        await alpha.Start();
        await beta.Start();

        var alphaCompletion = await WaitForCompletion(await alpha.Queue.Enqueue(workName));
        var betaCompletion = await WaitForCompletion(await beta.Queue.Enqueue(workName));

        Assert.True(alphaCompletion.IsCompletedSuccessfully);
        Assert.True(betaCompletion.IsCompletedSuccessfully);

        var alphaProfile = alphaCompletion.Worker?.Profile ?? throw new InvalidOperationException("Expected alpha worker profile.");
        var betaProfile = betaCompletion.Worker?.Profile ?? throw new InvalidOperationException("Expected beta worker profile.");

        Assert.Single(
            Flatten(alphaProfile.Root),
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
        Assert.Single(
            Flatten(betaProfile.Root),
            node => node.MetricType == WorkProfileMetricType.Timing &&
                node.Label.StartsWith("SQL ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DurableQueueRetainsImplicitNonProductionProfilingWhenQueueOptionsOnlyOverrideConfiguration()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-durable-dev-default-profiling";
        using var provider = new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Development))
            .AddWorkableSqlServerDurableQueue(
                this.ConnectionString,
                SchemaName)
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(
                    workName,
                    "Uses durable queueing with inherited non-production profiling."),
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                configuration => configuration.QueueDurably()))
            .BuildServiceProvider();
        await using var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        Assert.True(system.Catalog.TryGet(workName, out var definition));

        var completion = await WaitForCompletion(await system.Queue.Enqueue(
            workName,
            options: new WorkerOptions(definition.Configuration)));

        Assert.True(completion.IsCompletedSuccessfully);
        var worker = completion.Worker ?? throw new InvalidOperationException("Expected worker snapshot.");
        Assert.True(worker.Options.ProfilingEnabled);
        Assert.NotNull(worker.Profile);
    }

    [Fact]
    public async Task DurableIdempotentQueueWritesOneCombinedEntryAndRejectsDuplicate()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var system = this.CreateSystem(
            "sql-durable-idempotent",
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration
                .QueueDurably()
                .CoordinatePersistently().RejectDuplicateSubjects()
                .DoNotStart());
        await system.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "combined"));
        var first = await system.Queue.Enqueue("sql-durable-idempotent", input);
        var duplicate = await system.Queue.Enqueue("sql-durable-idempotent", input);

        await using var connection = await this.OpenConnection();
        var combinedRows = await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkEntries entries
INNER JOIN workable.WorkQueueEntries queue
    ON queue.WorkerId = entries.WorkerId
WHERE entries.SubjectType = N'order'
  AND entries.SubjectValue = N'combined'
  AND entries.HasIdempotencyReservation = 1;
""");

        await system.Stop();

        Assert.True(first.QueueOutcome.IsAccepted);
        Assert.False(duplicate.QueueOutcome.IsAccepted);
        Assert.Equal(1, combinedRows);
        Assert.Contains(duplicate.QueueOutcome.Messages, message => message.Code == "workable.queue_durability.duplicate");
    }

    [Fact]
    public async Task DurableQueueWithoutIdempotencyWritesDurableRowOnly()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var system = this.CreateSystem(
            "sql-durable-without-idempotency",
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration.QueueDurably().DoNotStart());
        await system.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "durable-no-idempotency"));
        var handle = await system.Queue.Enqueue("sql-durable-without-idempotency", input);

        await using var connection = await this.OpenConnection();
        var durableOnlyRows = await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkEntries entries
INNER JOIN workable.WorkQueueEntries queue
    ON queue.WorkerId = entries.WorkerId
WHERE entries.SubjectType = N'order'
  AND entries.SubjectValue = N'durable-no-idempotency'
  AND entries.HasIdempotencyReservation = 0
  AND queue.HasPersistentConcurrency = 0;
""");

        await system.Stop();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.Equal(1, durableOnlyRows);
    }

    [Fact]
    public async Task DurableQueueWithPersistentConcurrencyWritesClaimMetadata()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var system = this.CreateSystem(
            "sql-durable-persistent-concurrency-metadata",
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration
                .QueueDurably()
                .LimitConcurrency(
                    maximumCapacity: 1,
                    blockingMode: WorkConcurrencyBlockingMode.WhileExecuting,
                    limitReachedBehavior: WorkConcurrencyLimitReachedBehavior.DeferStart)
                .DoNotStart());
        await system.Start();

        var handle = await system.Queue.Enqueue("sql-durable-persistent-concurrency-metadata");

        await using var connection = await this.OpenConnection();
        var durableConcurrencyRows = await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkQueueEntries
WHERE DefinitionName = N'sql-durable-persistent-concurrency-metadata'
  AND HasPersistentConcurrency = 1
  AND ConcurrencyScope = N'PerDefinition'
  AND ConcurrencyMaximumCapacity = 1;
""");

        await system.Stop();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.Equal(1, durableConcurrencyRows);
    }

    [Fact]
    public async Task ConcurrentNonTransactionalStoreEnqueuesPersistDurableRows()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const int rowCount = 96;
        const string systemName = "sql-store-batched-enqueue-system";
        const string definitionName = "sql-store-batched-enqueue";
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        var systemId = WorkSystemId.New();
        var requests = Enumerable.Range(0, rowCount)
            .Select(index => CreateDurableEnqueueRequest(
                systemId,
                systemName,
                WorkerId.New(),
                definitionName,
                $"batched-store-{index}",
                transaction: null,
                enableIdempotency: false))
            .ToArray();

        await Task.WhenAll(requests.Select(request => store.Enqueue(request)));

        await using var connection = await this.OpenConnection();
        var persistedRows = await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkEntries entries
INNER JOIN workable.WorkQueueEntries queue
    ON queue.WorkerId = entries.WorkerId
WHERE entries.WorkSystemName = N'sql-store-batched-enqueue-system'
  AND entries.DefinitionName = N'sql-store-batched-enqueue'
  AND entries.HasIdempotencyReservation = 0;
""");

        Assert.Equal(rowCount, persistedRows);
    }

    [Fact]
    public async Task ConcurrentBatchedStoreEnqueuePreservesDuplicateSubjectRejection()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string systemName = "sql-store-batched-duplicate-system";
        const string definitionName = "sql-store-batched-duplicate";
        const string subjectValue = "batched-store-duplicate";
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        var systemId = WorkSystemId.New();
        var requests = Enumerable.Range(0, 2)
            .Select(_ => CreateDurableEnqueueRequest(
                systemId,
                systemName,
                WorkerId.New(),
                definitionName,
                subjectValue,
                transaction: null,
                enableIdempotency: true))
            .ToArray();

        var results = await Task.WhenAll(requests.Select(async request =>
        {
            try
            {
                await store.Enqueue(request);
                return (Exception?)null;
            }
            catch (Exception exception)
            {
                return exception;
            }
        }));

        await using var connection = await this.OpenConnection();
        var persistedRows = await CountRowsForSubject(connection, subjectValue);

        Assert.Equal(1, results.Count(static exception => exception is null));
        Assert.Single(results.OfType<WorkQueueDurabilityDuplicateException>());
        Assert.Equal(1, persistedRows);
    }

    [Fact]
    public async Task CanceledPendingBatchIsDiscardedWithoutOpeningSqlConnection()
    {
        var store = new WorkableSqlServerQueueDurabilityStore(new WorkableSqlServerQueueDurabilityOptions
        {
            ConnectionString = "Server=invalid.invalid;Initial Catalog=unused;User ID=unused;Password=unused;TrustServerCertificate=true",
            EnqueueBatchSize = 32,
            EnqueueBatchWindow = TimeSpan.FromMinutes(1),
        });
        using var cancellation = new CancellationTokenSource();
        var request = CreateDurableEnqueueRequest(
            WorkSystemId.New(),
            "canceled-batch",
            WorkerId.New(),
            "sample.dispatch",
            "canceled-batch",
            transaction: null);

        var enqueue = store.Enqueue(request, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => enqueue);

        var flush = (Task)typeof(WorkableSqlServerQueueDurabilityStore)
            .GetMethod("FlushEnqueueBatch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, null)!;
        await flush;
        var emptyFlush = (Task)typeof(WorkableSqlServerQueueDurabilityStore)
            .GetMethod("FlushEnqueueBatch", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(store, null)!;
        await emptyFlush;
    }

    [Fact]
    public async Task PersistenceBackedIdempotencyWithoutDurabilityWritesReservationOnly()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var system = this.CreateSystem(
            "sql-idempotency-only",
            async (_, _, cancellationToken) =>
            {
                await release.Task.WaitAsync(cancellationToken);
                return WorkExecutionResult.Success();
            },
            configuration => configuration.CoordinatePersistently().RejectDuplicateSubjects());
        await system.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "reservation-only"));
        var first = await system.Queue.Enqueue("sql-idempotency-only", input);
        await WaitForWorkerState(system, RequiredWorkerId(first), WorkerState.Running);
        var duplicate = await system.Queue.Enqueue("sql-idempotency-only", input);

        await using var connection = await this.OpenConnection();
        var reservationRows = await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkEntries
WHERE SubjectType = N'order'
  AND SubjectValue = N'reservation-only'
  AND HasIdempotencyReservation = 1;
""");

        release.SetResult();
        await first.WaitForCompletion();
        await WaitForEntryCount(connection, "reservation-only", 0);
        await system.Stop();

        Assert.True(first.QueueOutcome.IsAccepted);
        Assert.False(duplicate.QueueOutcome.IsAccepted);
        Assert.Equal(1, reservationRows);
        Assert.Contains(duplicate.QueueOutcome.Messages, message => message.Code == "workable.idempotency.duplicate_subject");
    }

    [Fact]
    public async Task PersistenceBackedIdempotencyWithoutDurabilityRejectsExistingTransaction()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var system = this.CreateSystem(
            "sql-idempotency-only-existing-transaction",
            (_, _, _) =>
            {
                ran.TrySetResult();
                return Task.FromResult(WorkExecutionResult.Success());
            },
            configuration => configuration.CoordinatePersistently().RejectDuplicateSubjects());
        await system.Start();

        await using var connection = await this.OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync();
        var options = WorkerOptions.Default.WithSqlServerQueueDurabilityTransaction(connection, transaction);
        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "idempotency-only-transaction"));

        var handle = await system.Queue.Enqueue("sql-idempotency-only-existing-transaction", input, options);
        await transaction.RollbackAsync();

        var reservationRows = await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkEntries
WHERE SubjectType = N'order'
  AND SubjectValue = N'idempotency-only-transaction';
""");

        await system.Stop();

        Assert.False(handle.QueueOutcome.IsAccepted);
        Assert.Null(handle.WorkerId);
        Assert.False(ran.Task.IsCompleted);
        Assert.Equal(0, reservationRows);
        Assert.Contains(handle.QueueOutcome.Messages, message => message.Code == "workable.idempotency.persistence_transaction_requires_durable_queue");
    }

    [Fact]
    public async Task DurableQueueUsingExistingTransactionStartsPromptlyWhenNotifiedAfterCommit()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var system = this.CreateSystem(
            "sql-existing-transaction",
            (_, _, _) =>
            {
                ran.TrySetResult();
                return Task.FromResult(WorkExecutionResult.Success());
            },
            configuration => configuration.QueueDurably());
        await system.Start();

        await using var connection = await this.OpenConnection();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        var options = WorkerOptions.Default.WithSqlServerQueueDurabilityTransaction(connection, transaction);
        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "transactional"));

        var handle = await system.Queue.Enqueue("sql-existing-transaction", input, options);
        await using (var visibilityConnection = await this.OpenConnection())
        {
            Assert.Equal(0, await CountReadableRowsForSubject(visibilityConnection, "transactional"));
        }

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.False(ran.Task.IsCompleted);
        Assert.Null(await system.Query.Worker(RequiredWorkerId(handle)));

        await transaction.CommitAsync();
        system.Queue.NotifyDurableWorkAvailable();

        await WaitWithTimeout(ran.Task);
        var completion = await WaitForCompletion(handle);
        await system.Stop();

        Assert.True(ran.Task.IsCompleted);
        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DurableQueueCommitsWithCallerBusinessDataInSameTransaction()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string orderId = "business-transaction";
        const string payload = "invoice-ready";
        await using (var setupConnection = await this.OpenConnection())
        {
            await Execute(setupConnection, """
CREATE TABLE dbo.BusinessOrders
(
    OrderId nvarchar(64) NOT NULL CONSTRAINT PK_BusinessOrders PRIMARY KEY,
    Payload nvarchar(64) NOT NULL
);
""");
        }

        var observedPayload = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var system = this.CreateSystem(
            "sql-business-transaction",
            async (_, _, cancellationToken) =>
            {
                await using var readConnection = new SqlConnection(this.ConnectionString);
                await readConnection.OpenAsync(cancellationToken);
                await using var readCommand = readConnection.CreateCommand();
                readCommand.CommandText = """
SELECT Payload
FROM dbo.BusinessOrders
WHERE OrderId = @OrderId;
""";
                readCommand.Parameters.AddWithValue("@OrderId", orderId);

                var value = await readCommand.ExecuteScalarAsync(cancellationToken);
                if (value is string businessPayload)
                {
                    observedPayload.TrySetResult(businessPayload);
                    return WorkExecutionResult.Success();
                }

                return WorkExecutionResult.Failure(
                    [WorkMessage.Error("business.missing", "Business data was not visible to durable work.")]);
            },
            configuration => configuration.QueueDurably());
        await system.Start();

        await using var connection = await this.OpenConnection();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
        await using (var businessCommand = connection.CreateCommand())
        {
            businessCommand.Transaction = transaction;
            businessCommand.CommandText = """
INSERT INTO dbo.BusinessOrders (OrderId, Payload)
VALUES (@OrderId, @Payload);
""";
            businessCommand.Parameters.AddWithValue("@OrderId", orderId);
            businessCommand.Parameters.AddWithValue("@Payload", payload);
            await businessCommand.ExecuteNonQueryAsync();
        }

        var options = WorkerOptions.Default.WithSqlServerQueueDurabilityTransaction(connection, transaction);
        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", orderId));

        var handle = await system.Queue.Enqueue("sql-business-transaction", input, options);
        await using (var visibilityConnection = await this.OpenConnection())
        {
            Assert.Equal(0, await CountReadableRowsForSubject(visibilityConnection, orderId));
        }

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.False(observedPayload.Task.IsCompleted);
        Assert.Null(await system.Query.Worker(RequiredWorkerId(handle)));

        await transaction.CommitAsync();

        var completion = await WaitForCompletion(handle);
        var seenPayload = await WaitWithTimeout(observedPayload.Task);
        await system.Stop();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(payload, seenPayload);
    }

    [Fact]
    public async Task DurableCompletionCommitsBusinessDataAndDeletesQueueRowInSameTransaction()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string orderId = "durable-complete-business";
        const string payload = "packed";
        await using (var setupConnection = await this.OpenConnection())
        {
            await Execute(setupConnection, """
CREATE TABLE dbo.DurableCompletionOrders
(
    OrderId nvarchar(64) NOT NULL CONSTRAINT PK_DurableCompletionOrders PRIMARY KEY,
    Payload nvarchar(64) NOT NULL
);
""");
        }

        var system = this.CreateSystem(
            "sql-durable-complete-business",
            async (context, _, cancellationToken) =>
            {
                await using var connection = await this.OpenConnection();
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
INSERT INTO dbo.DurableCompletionOrders (OrderId, Payload)
VALUES (@OrderId, @Payload);
""";
                AddParameter(command, "@OrderId", orderId);
                AddParameter(command, "@Payload", payload);
                await command.ExecuteNonQueryAsync(cancellationToken);
                await context.CompleteDurablyWithSqlServerTransaction(connection, transaction, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return WorkExecutionResult.Success();
            },
            configuration => configuration.QueueDurably().CompleteDurably());
        await system.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", orderId));
        var handle = await system.Queue.Enqueue("sql-durable-complete-business", input);
        var completion = await WaitForCompletion(handle);

        await using var verification = await this.OpenConnection();
        await WaitForEntryCount(verification, orderId, 0);
        var businessPayload = await Scalar<string>(verification, $"""
SELECT Payload
FROM dbo.DurableCompletionOrders
WHERE OrderId = N'{Escape(orderId)}';
""");
        await system.Stop();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Equal(payload, businessPayload);
    }

    [Fact]
    public async Task DurableCompletionRollsBackBusinessDataWhenExecutionFails()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string orderId = "durable-complete-rollback";
        await using (var setupConnection = await this.OpenConnection())
        {
            await Execute(setupConnection, """
CREATE TABLE dbo.DurableCompletionRollbackOrders
(
    OrderId nvarchar(64) NOT NULL CONSTRAINT PK_DurableCompletionRollbackOrders PRIMARY KEY
);
""");
        }

        var system = this.CreateSystem(
            "sql-durable-complete-rollback",
            async (context, _, cancellationToken) =>
            {
                await using var connection = await this.OpenConnection();
                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
INSERT INTO dbo.DurableCompletionRollbackOrders (OrderId)
VALUES (@OrderId);
""";
                AddParameter(command, "@OrderId", orderId);
                await command.ExecuteNonQueryAsync(cancellationToken);
                await transaction.RollbackAsync(CancellationToken.None);
                return WorkExecutionResult.Failure(
                    [WorkMessage.Error("business.failed", "Business work failed.")]);
            },
            configuration => configuration.QueueDurably().CompleteDurably());
        await system.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", orderId));
        var handle = await system.Queue.Enqueue("sql-durable-complete-rollback", input);
        var completion = await WaitForCompletion(handle);

        await using var verification = await this.OpenConnection();
        await WaitForFailedEntryRetained(verification, orderId);
        var businessRows = await Scalar<int>(verification, $"""
SELECT COUNT(*)
FROM dbo.DurableCompletionRollbackOrders
WHERE OrderId = N'{Escape(orderId)}';
""");
        await system.Stop();

        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        Assert.Equal(0, businessRows);
    }

    [Fact]
    public async Task DurableQueueUsingExistingTransactionWithQuotedIdentifierOffDequeuesAfterCommit()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var system = this.CreateSystem(
            "sql-quoted-identifier-off",
            (_, _, _) =>
            {
                ran.TrySetResult();
                return Task.FromResult(WorkExecutionResult.Success());
            },
            configuration => configuration.QueueDurably());
        await system.Start();

        await using var connection = await this.OpenConnection();
        await Execute(connection, """
SET QUOTED_IDENTIFIER OFF;
SET ANSI_NULLS OFF;
""");
        await using var transaction = await connection.BeginTransactionAsync();
        var options = WorkerOptions.Default.WithSqlServerQueueDurabilityTransaction(connection, transaction);
        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "quoted-identifier-off"));

        var handle = await system.Queue.Enqueue("sql-quoted-identifier-off", input, options);
        await transaction.CommitAsync();

        var completion = await WaitForCompletion(handle);
        await system.Stop();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.True(ran.Task.IsCompleted);
        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DurableQueueUsingExistingTransactionRollbackLeavesNoWorkerOrRow()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var system = this.CreateSystem(
            "sql-existing-transaction-rollback",
            (_, _, _) =>
            {
                ran.TrySetResult();
                return Task.FromResult(WorkExecutionResult.Success());
            },
            configuration => configuration.QueueDurably());
        await system.Start();

        await using var connection = await this.OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync();
        var options = WorkerOptions.Default.WithSqlServerQueueDurabilityTransaction(connection, transaction);
        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "rollback"));

        var handle = await system.Queue.Enqueue("sql-existing-transaction-rollback", input, options);
        var workerId = RequiredWorkerId(handle);
        await using (var visibilityConnection = await this.OpenConnection())
        {
            Assert.Equal(0, await CountReadableRowsForSubject(visibilityConnection, "rollback"));
        }

        await transaction.RollbackAsync();

        var claimAttempts = system.Diagnostics.Durability.ClaimAttemptCount;
        system.Queue.NotifyDurableWorkAvailable();
        await TestEventually.Until(
            () => system.Diagnostics.Durability.ClaimAttemptCount > claimAttempts,
            "Expected the explicit durable queue notification to trigger an empty claim after rollback.",
            timeout: TimeSpan.FromSeconds(2));

        var rows = await CountRowsForSubject(connection, "rollback");
        await system.Stop();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.False(ran.Task.IsCompleted);
        Assert.Null(await system.Query.Worker(workerId));
        Assert.Equal(0, rows);
    }

    [Fact]
    public async Task DurableQueueReplaysAcrossRestartUsingStableNames()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-restart-durable";
        var firstSystem = this.CreateSystem(
            workName,
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration.QueueDurably());
        await firstSystem.Start();

        await using var connection = await this.OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync();
        var options = WorkerOptions.Default.WithSqlServerQueueDurabilityTransaction(connection, transaction);
        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "restart"));

        var handle = await firstSystem.Queue.Enqueue(workName, input, options);
        var workerId = RequiredWorkerId(handle);
        await firstSystem.Stop();
        await transaction.CommitAsync();

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSystem = this.CreateSystem(
            workName,
            (_, _, _) =>
            {
                ran.TrySetResult();
                return Task.FromResult(WorkExecutionResult.Success());
            },
            configuration => configuration.QueueDurably());

        await secondSystem.Start();
        await WaitWithTimeout(ran.Task);
        var replayed = await secondSystem.Query.Worker(workerId);
        await secondSystem.Stop();

        Assert.NotNull(replayed);
        Assert.Equal(workName, replayed.DefinitionName);
    }

    [Fact]
    public async Task PersistenceBackedIdempotencyRejectsDuplicateAcrossRestart()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-idempotency-restart";
        var firstSystem = this.CreateSystem(
            workName,
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration
                .CoordinatePersistently().RejectDuplicateSubjects()
                .DoNotStart());
        await firstSystem.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "idempotency-restart"));
        var first = await firstSystem.Queue.Enqueue(workName, input);
        await firstSystem.Stop();

        var secondSystem = this.CreateSystem(
            workName,
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration
                .CoordinatePersistently().RejectDuplicateSubjects()
                .DoNotStart());
        await secondSystem.Start();

        var duplicate = await secondSystem.Queue.Enqueue(workName, input);
        await secondSystem.Stop();

        Assert.True(first.QueueOutcome.IsAccepted);
        Assert.False(duplicate.QueueOutcome.IsAccepted);
        Assert.Contains(duplicate.QueueOutcome.Messages, message => message.Code == "workable.idempotency.duplicate_subject");
    }

    [Fact]
    public async Task DurableIdempotencyIsIsolatedByWorkSystemName()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-shared-work-name";
        var registry = this.CreateRegistry(
            services => services
                .AddWorkableSystem(builder => builder.AddWork(
                    WorkDefinition.Create(workName, "Default system shared durable work."),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configuration => configuration
                        .QueueDurably()
                        .CoordinatePersistently().RejectDuplicateSubjects()
                        .DoNotStart()))
                .AddWorkableSystem("background", builder => builder.AddWork(
                    WorkDefinition.Create(workName, "Named system shared durable work."),
                    (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                    configuration => configuration
                        .QueueDurably()
                        .CoordinatePersistently().RejectDuplicateSubjects()
                        .DoNotStart())));
        var defaultSystem = registry.Default;
        Assert.True(registry.TryGet("background", out var backgroundSystem));

        await defaultSystem.Start();
        await backgroundSystem.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "shared"));
        var defaultHandle = await defaultSystem.Queue.Enqueue(workName, input);
        var backgroundHandle = await backgroundSystem.Queue.Enqueue(workName, input);
        var defaultDuplicate = await defaultSystem.Queue.Enqueue(workName, input);

        await using var connection = await this.OpenConnection();
        var rows = await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkEntries
WHERE SubjectValue = N'shared';
""");
        var defaultRows = await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkEntries
WHERE WorkSystemName = N'default'
  AND DefinitionName = N'sql-shared-work-name'
  AND SubjectValue = N'shared';
""");
        var backgroundRows = await Scalar<int>(connection, """
SELECT COUNT(*)
FROM workable.WorkEntries
WHERE WorkSystemName = N'background'
  AND DefinitionName = N'sql-shared-work-name'
  AND SubjectValue = N'shared';
""");

        await backgroundSystem.Stop();
        await defaultSystem.Stop();

        Assert.True(defaultHandle.QueueOutcome.IsAccepted);
        Assert.True(backgroundHandle.QueueOutcome.IsAccepted);
        Assert.False(defaultDuplicate.QueueOutcome.IsAccepted);
        Assert.Equal(2, rows);
        Assert.Equal(1, defaultRows);
        Assert.Equal(1, backgroundRows);
    }

    [Fact]
    public async Task DurableQueueDeletesRowAfterCompletion()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var system = this.CreateSystem(
            "sql-final-delete",
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configuration => configuration.QueueDurably());
        await system.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "final-delete"));
        var handle = await system.Queue.Enqueue("sql-final-delete", input);
        var completion = await WaitForCompletion(handle);

        await using var connection = await this.OpenConnection();
        await WaitForEntryCount(connection, "final-delete", 0);
        await system.Stop();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.True(completion.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task DurableQueueRetainsFailedRowUntilCancelOrComplete()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var system = this.CreateSystem(
            "sql-failed-retention",
            (_, _, _) => Task.FromResult(WorkExecutionResult.Failure(
                [WorkMessage.Error("sql.failed", "Failed durable work.")])),
            configuration => configuration.QueueDurably());
        await system.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", "failed-retention"));
        var handle = await system.Queue.Enqueue("sql-failed-retention", input);
        var workerId = RequiredWorkerId(handle);
        var completion = await WaitForCompletion(handle);

        await using var connection = await this.OpenConnection();
        await WaitForFailedEntryRetained(connection, "failed-retention");
        var failed = await system.Query.Worker(workerId);
        Assert.NotNull(failed);
        var cancel = await system.Workers.Execute(new WorkerVersion(workerId, failed.Revision), WorkAction.Cancel);
        await WaitForEntryCount(connection, "failed-retention", 0);
        await system.Stop();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        Assert.True(cancel.IsAccepted);
    }

    [Fact]
    public async Task DurableQueueRestoresRetainedFailureAfterRestartWithoutExecutingIt()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-failed-restart";
        const string subjectValue = "failed-restart";
        var firstSystem = this.CreateSystem(
            workName,
            (_, _, _) => Task.FromResult(WorkExecutionResult.Failure(
                [WorkMessage.Error("sql.failed.restart", "Failed before restart.")])),
            configuration => configuration.QueueDurably());
        await firstSystem.Start();

        var handle = await firstSystem.Queue.Enqueue(
            workName,
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", subjectValue)));
        var workerId = RequiredWorkerId(handle);
        await WaitForCompletion(handle);

        await using var connection = await this.OpenConnection();
        await WaitForFailedEntryRetained(connection, subjectValue);
        await firstSystem.Stop();
        await ExpireLease(connection, subjectValue);

        var unexpectedlyExecuted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSystem = this.CreateSystem(
            workName,
            (_, _, _) =>
            {
                unexpectedlyExecuted.TrySetResult();
                return Task.FromResult(WorkExecutionResult.Success());
            },
            configuration => configuration.QueueDurably());
        await secondSystem.Start();

        await TestEventually.Until(
            async () => (await secondSystem.Query.Worker(workerId))?.State == WorkerState.Failed,
            "Expected retained failed durable worker to be restored after restart.");
        var restored = Assert.IsType<WorkerSnapshot>(await secondSystem.Query.Worker(workerId));
        var cancel = await secondSystem.Workers.Execute(restored.Version, WorkAction.Cancel);
        await WaitForEntryCount(connection, subjectValue, 0);
        await secondSystem.Stop();

        Assert.False(unexpectedlyExecuted.Task.IsCompleted);
        Assert.Contains(restored.Messages, message => message.Code == "sql.failed.restart");
        Assert.True(cancel.IsAccepted);
    }

    [Fact]
    public async Task DurableQueueAutoCancelsOverdueRetainedFailureAfterRestart()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-failed-restart-auto-cancel";
        const string subjectValue = "failed-restart-auto-cancel";
        var firstSystem = this.CreateSystem(
            workName,
            (_, _, _) => Task.FromResult(WorkExecutionResult.Failure(
                [WorkMessage.Error("sql.failed.auto_cancel", "Failed before restart.")])),
            configuration => configuration
                .QueueDurably()
                .AutoCancelFailedWorkersAfter(TimeSpan.FromMinutes(5)));
        await firstSystem.Start();

        var handle = await firstSystem.Queue.Enqueue(
            workName,
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", subjectValue)));
        var workerId = RequiredWorkerId(handle);
        await WaitForCompletion(handle);

        await using var connection = await this.OpenConnection();
        await WaitForFailedEntryRetained(connection, subjectValue);
        await firstSystem.Stop();
        await ExpireLease(connection, subjectValue);
        await Execute(connection, $"""
UPDATE entries
SET FailedAt = DATEADD(minute, -10, SYSDATETIMEOFFSET())
FROM workable.WorkEntries entries
WHERE entries.SubjectValue = N'{subjectValue}';
""");

        var unexpectedlyExecuted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSystem = this.CreateSystem(
            workName,
            (_, _, _) =>
            {
                unexpectedlyExecuted.TrySetResult();
                return Task.FromResult(WorkExecutionResult.Success());
            },
            configuration => configuration
                .QueueDurably()
                .AutoCancelFailedWorkersAfter(TimeSpan.FromMinutes(5)));
        await secondSystem.Start();

        await WaitForEntryCount(connection, subjectValue, 0);
        await TestEventually.Until(
            async () => (await secondSystem.Query.Worker(workerId))?.State == WorkerState.Canceled,
            "Expected overdue retained failure to auto-cancel after restart.");
        var canceled = Assert.IsType<WorkerSnapshot>(await secondSystem.Query.Worker(workerId));
        await secondSystem.Stop();

        Assert.False(unexpectedlyExecuted.Task.IsCompleted);
        Assert.Contains(canceled.Messages, message => message.Code == "sql.failed.auto_cancel");
        Assert.Contains(
            canceled.ActionHistory,
            history =>
                history.Kind == WorkerActionHistoryKind.WorkerAction &&
                history.Action == WorkAction.Cancel &&
                history.Status == WorkActionStatus.Accepted &&
                history.RequestContext.Description ==
                    "Workable auto-canceled a failed worker after the configured failed-state delay.");
    }

    [Fact]
    public async Task DurableQueueInterruptedByShutdownReplaysAfterLeaseExpires()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-shutdown-interrupted-replay";
        const string subjectValue = "shutdown-interrupted";
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var interruptedSeen = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstSystem = this.CreateSystem(
            workName,
            async (context, _, cancellationToken) =>
            {
                started.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                    return WorkExecutionResult.Success();
                }
                catch (OperationCanceledException)
                {
                    if (context.IsInterrupted)
                    {
                        interruptedSeen.TrySetResult();
                    }

                    throw;
                }
            },
            configuration => configuration.QueueDurably());
        await firstSystem.Start();

        var input = WorkInput.Empty.WithSubject(new WorkSubjectId("order", subjectValue));
        var handle = await firstSystem.Queue.Enqueue(workName, input);
        await WaitWithTimeout(started.Task);

        var stop = await firstSystem.Stop();
        var interrupted = Assert.Single(stop.CancellationRequestedWorkers);
        await WaitWithTimeout(interruptedSeen.Task);

        await using (var connection = await this.OpenConnection())
        {
            await WaitForEntryCount(connection, subjectValue, 1);
            await ExpireLease(connection, subjectValue);
        }

        var replayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSystem = this.CreateSystem(
            workName,
            (_, _, _) =>
            {
                replayed.TrySetResult();
                return Task.FromResult(WorkExecutionResult.Success());
            },
            configuration => configuration.QueueDurably());
        await secondSystem.Start();
        await WaitWithTimeout(replayed.Task);

        await using (var verification = await this.OpenConnection())
        {
            await WaitForEntryCount(verification, subjectValue, 0);
        }

        await secondSystem.Stop();

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.Equal(WorkerState.Interrupted, interrupted.State);
    }

    [Fact]
    public async Task ExpiredDurableLeaseIsClaimedAgainAfterRestart()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-expired-lease";
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        var workerId = WorkerId.New();
        await using (var connection = await this.OpenConnection())
        {
            await InsertDurableRow(
                connection,
                workerId,
                workName,
                "expired-lease",
                DateTimeOffset.UtcNow.AddMinutes(-5),
                leaseId: "dead-lease",
                leaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var system = this.CreateSystem(
            workName,
            (_, _, _) =>
            {
                ran.TrySetResult();
                return Task.FromResult(WorkExecutionResult.Success());
            },
            configuration => configuration.QueueDurably());

        await system.Start();
        await WaitWithTimeout(ran.Task);
        var replayed = await system.Query.Worker(workerId);
        await system.Stop();

        Assert.NotNull(replayed);
        Assert.Equal(workName, replayed.DefinitionName);
    }

    [Fact]
    public async Task CompetingDurableConsumersClaimReadyRowsWithoutOverlap()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-competing-consumers";
        const int batchSize = 150;
        const int rowCount = batchSize * 2;
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            for (var index = 0; index < rowCount; index++)
            {
                await InsertDurableRow(
                    connection,
                    WorkerId.New(),
                    workName,
                    $"competing-{index}",
                    DateTimeOffset.UtcNow.AddMilliseconds(index));
            }
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        var first = ClaimReady(store, "consumer-one", batchSize);
        var second = ClaimReady(store, "consumer-two", batchSize);
        var claimed = await Task.WhenAll(first, second);
        var retryOwner = claimed[0].Count <= claimed[1].Count ? "consumer-one" : "consumer-two";
        var retry = await ClaimReady(store, retryOwner, batchSize);
        var firstWorkerIds = claimed[0]
            .Concat(retryOwner == "consumer-one" ? retry : [])
            .Select(entry => entry.Lease.WorkerId)
            .ToHashSet();
        var secondWorkerIds = claimed[1]
            .Concat(retryOwner == "consumer-two" ? retry : [])
            .Select(entry => entry.Lease.WorkerId)
            .ToHashSet();

        Assert.Equal(rowCount, firstWorkerIds.Count + secondWorkerIds.Count);
        Assert.Empty(firstWorkerIds.Intersect(secondWorkerIds));
        Assert.NotEmpty(firstWorkerIds);
        Assert.NotEmpty(secondWorkerIds);
    }

    [Fact]
    public async Task ConcurrentReadyClaimAndFinalCleanupDoNotDeadlock()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const int rowCount = 1_000;
        const int roundCount = 10;
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        var systemId = WorkSystemId.New();
        for (var round = 0; round < roundCount; round++)
        {
            var workName = $"sql-concurrent-claim-cleanup-{round}";
            var enqueue = Task.WhenAll(Enumerable.Range(0, rowCount)
                .Select(index => store.Enqueue(CreateNonConcurrentDurableEnqueueRequest(
                    systemId,
                    WorkerId.New(),
                    workName,
                    $"claim-cleanup-{round}-{index}"))));
            var claimAndCleanup = Task.Run(async () =>
            {
                var claimedWorkerIds = new HashSet<WorkerId>();
                var cleanupTasks = new List<Task>();
                while (claimedWorkerIds.Count < rowCount)
                {
                    var claimed = await ClaimReady(store, "claim-cleanup-consumer", rowCount);
                    if (claimed.Count == 0)
                    {
                        await Task.Delay(1);
                        continue;
                    }

                    claimedWorkerIds.UnionWith(claimed.Select(entry => entry.Lease.WorkerId));
                    cleanupTasks.Add(store.DeleteFinal(claimed
                        .Select(entry => new WorkQueueDurabilityCleanupRequest(entry.Lease.WorkerId, entry.Lease))
                        .ToArray()));
                }

                await Task.WhenAll(cleanupTasks);
                return claimedWorkerIds;
            });
            await Task.WhenAll(enqueue, claimAndCleanup).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.Equal(rowCount, (await claimAndCleanup).Count);
        }
    }

    [Fact]
    public async Task PersistenceBackedConcurrencyLimitsDurableClaimsAcrossConsumers()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-persistent-concurrency";
        var firstWorkerId = WorkerId.New();
        var secondWorkerId = WorkerId.New();
        var configuration = DurablePersistenceConcurrencyConfiguration();
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await InsertDurableRow(
                connection,
                firstWorkerId,
                workName,
                "persistent-concurrency-first",
                DateTimeOffset.UtcNow,
                input: DurableConcurrencyInput("persistent-concurrency-first"),
                configuration: configuration);
            await InsertDurableRow(
                connection,
                secondWorkerId,
                workName,
                "persistent-concurrency-second",
                DateTimeOffset.UtcNow.AddMilliseconds(1),
                input: DurableConcurrencyInput("persistent-concurrency-second"),
                configuration: configuration);
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        var firstClaim = await ClaimReady(store, "consumer-one", batchSize: 10);
        var secondClaim = await ClaimReady(store, "consumer-two", batchSize: 10);
        await store.DeleteFinal([new WorkQueueDurabilityCleanupRequest(firstClaim.Single().Lease.WorkerId, firstClaim.Single().Lease)]);
        var claimAfterCompletion = await ClaimReady(store, "consumer-two", batchSize: 10);

        await using var verification = await this.OpenConnection();
        var executingBuckets = await Scalar<int>(verification, """
SELECT COUNT(*)
FROM workable.WorkQueueEntries
WHERE ConcurrencyBucket = N'Executing';
""");

        Assert.Equal(firstWorkerId, firstClaim.Single().Lease.WorkerId);
        Assert.Empty(secondClaim);
        Assert.Equal(secondWorkerId, claimAfterCompletion.Single().Lease.WorkerId);
        Assert.Equal(1, executingBuckets);
    }

    [Fact]
    public async Task PersistenceBackedConcurrencyWithoutIdempotencyLimitsDurableClaims()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-persistent-concurrency-no-idempotency";
        var firstWorkerId = WorkerId.New();
        var secondWorkerId = WorkerId.New();
        var configuration = DurablePersistenceConcurrencyConfiguration(enableIdempotency: false);
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await InsertDurableRow(
                connection,
                firstWorkerId,
                workName,
                "persistent-concurrency-no-idempotency-first",
                DateTimeOffset.UtcNow,
                hasIdempotencyReservation: false,
                input: DurableConcurrencyInput("persistent-concurrency-no-idempotency-first"),
                configuration: configuration);
            await InsertDurableRow(
                connection,
                secondWorkerId,
                workName,
                "persistent-concurrency-no-idempotency-second",
                DateTimeOffset.UtcNow.AddMilliseconds(1),
                hasIdempotencyReservation: false,
                input: DurableConcurrencyInput("persistent-concurrency-no-idempotency-second"),
                configuration: configuration);
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        var firstClaim = await ClaimReady(store, "consumer-one", batchSize: 10);
        var secondClaim = await ClaimReady(store, "consumer-two", batchSize: 10);

        Assert.Equal(firstWorkerId, firstClaim.Single().Lease.WorkerId);
        Assert.Empty(secondClaim);
    }

    [Fact]
    public async Task PersistenceBackedConcurrencyAllowsConfiguredCapacity()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-persistent-concurrency-capacity";
        var firstWorkerId = WorkerId.New();
        var secondWorkerId = WorkerId.New();
        var thirdWorkerId = WorkerId.New();
        var configuration = DurablePersistenceConcurrencyConfiguration(maximumCapacity: 2, enableIdempotency: false);
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await InsertDurableRow(
                connection,
                firstWorkerId,
                workName,
                "persistent-concurrency-capacity-first",
                DateTimeOffset.UtcNow,
                hasIdempotencyReservation: false,
                input: DurableConcurrencyInput("persistent-concurrency-capacity-first"),
                configuration: configuration);
            await InsertDurableRow(
                connection,
                secondWorkerId,
                workName,
                "persistent-concurrency-capacity-second",
                DateTimeOffset.UtcNow.AddMilliseconds(1),
                hasIdempotencyReservation: false,
                input: DurableConcurrencyInput("persistent-concurrency-capacity-second"),
                configuration: configuration);
            await InsertDurableRow(
                connection,
                thirdWorkerId,
                workName,
                "persistent-concurrency-capacity-third",
                DateTimeOffset.UtcNow.AddMilliseconds(2),
                hasIdempotencyReservation: false,
                input: DurableConcurrencyInput("persistent-concurrency-capacity-third"),
                configuration: configuration);
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        var firstClaim = await ClaimReady(store, "consumer-one", batchSize: 10);
        var blockedClaim = await ClaimReady(store, "consumer-two", batchSize: 10);
        await store.DeleteFinal([new WorkQueueDurabilityCleanupRequest(firstClaim[0].Lease.WorkerId, firstClaim[0].Lease)]);
        var claimAfterCompletion = await ClaimReady(store, "consumer-two", batchSize: 10);

        Assert.Equal([firstWorkerId, secondWorkerId], firstClaim.Select(entry => entry.Lease.WorkerId));
        Assert.Empty(blockedClaim);
        Assert.Equal(thirdWorkerId, claimAfterCompletion.Single().Lease.WorkerId);
    }

    [Fact]
    public async Task PersistenceBackedConcurrencyPerDefinitionLimitsOnlyMatchingDefinition()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string firstWorkName = "sql-persistent-concurrency-definition-a";
        const string secondWorkName = "sql-persistent-concurrency-definition-b";
        var firstWorkerId = WorkerId.New();
        var blockedWorkerId = WorkerId.New();
        var otherDefinitionWorkerId = WorkerId.New();
        var configuration = DurablePersistenceConcurrencyConfiguration(
            scope: WorkConcurrencyScope.PerDefinition,
            enableIdempotency: false);
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await InsertDurableRow(
                connection,
                firstWorkerId,
                firstWorkName,
                "persistent-concurrency-definition-first",
                DateTimeOffset.UtcNow,
                hasIdempotencyReservation: false,
                configuration: configuration);
            await InsertDurableRow(
                connection,
                blockedWorkerId,
                firstWorkName,
                "persistent-concurrency-definition-blocked",
                DateTimeOffset.UtcNow.AddMilliseconds(1),
                hasIdempotencyReservation: false,
                configuration: configuration);
            await InsertDurableRow(
                connection,
                otherDefinitionWorkerId,
                secondWorkName,
                "persistent-concurrency-definition-other",
                DateTimeOffset.UtcNow.AddMilliseconds(2),
                hasIdempotencyReservation: false,
                configuration: configuration);
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        var claimed = await ClaimReady(store, "consumer", batchSize: 10);

        Assert.Equal([firstWorkerId, otherDefinitionWorkerId], claimed.Select(entry => entry.Lease.WorkerId));
        Assert.DoesNotContain(claimed, entry => entry.Lease.WorkerId == blockedWorkerId);
    }

    [Fact]
    public async Task PersistenceBackedConcurrencyPerSubjectLimitsOnlyMatchingSubject()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-persistent-concurrency-subject";
        var firstWorkerId = WorkerId.New();
        var blockedWorkerId = WorkerId.New();
        var otherSubjectWorkerId = WorkerId.New();
        var configuration = DurablePersistenceConcurrencyConfiguration(
            scope: WorkConcurrencyScope.PerSubject,
            enableIdempotency: false);
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await InsertDurableRow(
                connection,
                firstWorkerId,
                workName,
                "persistent-concurrency-subject-shared",
                DateTimeOffset.UtcNow,
                hasIdempotencyReservation: false,
                configuration: configuration);
            await InsertDurableRow(
                connection,
                blockedWorkerId,
                workName,
                "persistent-concurrency-subject-shared",
                DateTimeOffset.UtcNow.AddMilliseconds(1),
                hasIdempotencyReservation: false,
                configuration: configuration);
            await InsertDurableRow(
                connection,
                otherSubjectWorkerId,
                workName,
                "persistent-concurrency-subject-other",
                DateTimeOffset.UtcNow.AddMilliseconds(2),
                hasIdempotencyReservation: false,
                configuration: configuration);
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        var claimed = await ClaimReady(store, "consumer", batchSize: 10);

        Assert.Equal([firstWorkerId, otherSubjectWorkerId], claimed.Select(entry => entry.Lease.WorkerId));
        Assert.DoesNotContain(claimed, entry => entry.Lease.WorkerId == blockedWorkerId);
    }

    [Fact]
    public async Task PersistenceBackedConcurrencySerializesConcurrentClaimers()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-persistent-concurrency-race";
        const int rowCount = 12;
        var configuration = DurablePersistenceConcurrencyConfiguration(enableIdempotency: false);
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            for (var index = 0; index < rowCount; index++)
            {
                await InsertDurableRow(
                    connection,
                    WorkerId.New(),
                    workName,
                    $"persistent-concurrency-race-{index}",
                    DateTimeOffset.UtcNow.AddMilliseconds(index),
                    hasIdempotencyReservation: false,
                    input: DurableConcurrencyInput($"persistent-concurrency-race-{index}"),
                    configuration: configuration);
            }
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        var claims = await Task.WhenAll(
            ClaimReady(store, "consumer-one", batchSize: 10),
            ClaimReady(store, "consumer-two", batchSize: 10),
            ClaimReady(store, "consumer-three", batchSize: 10));
        var claimed = claims.SelectMany(entries => entries).ToList();

        Assert.Single(claimed);
    }

    [Fact]
    public async Task RetainingFailedPersistenceBackedConcurrencyRowReleasesCapacity()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-persistent-concurrency-retain-failed";
        var firstWorkerId = WorkerId.New();
        var secondWorkerId = WorkerId.New();
        var configuration = DurablePersistenceConcurrencyConfiguration(enableIdempotency: false);
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await InsertDurableRow(
                connection,
                firstWorkerId,
                workName,
                "persistent-concurrency-retain-first",
                DateTimeOffset.UtcNow,
                hasIdempotencyReservation: false,
                input: DurableConcurrencyInput("persistent-concurrency-retain-first"),
                configuration: configuration);
            await InsertDurableRow(
                connection,
                secondWorkerId,
                workName,
                "persistent-concurrency-retain-second",
                DateTimeOffset.UtcNow.AddMilliseconds(1),
                hasIdempotencyReservation: false,
                input: DurableConcurrencyInput("persistent-concurrency-retain-second"),
                configuration: configuration);
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        var firstClaim = await ClaimReady(store, "consumer-one", batchSize: 10);
        await store.RetainFailed([new WorkQueueDurabilityCleanupRequest(firstClaim.Single().Lease.WorkerId, firstClaim.Single().Lease)]);
        var claimAfterRetain = await ClaimReady(store, "consumer-two", batchSize: 10);

        await using var verification = await this.OpenConnection();
        var retainedRows = await Scalar<int>(verification, $"""
SELECT COUNT(*)
FROM workable.WorkEntries entries
INNER JOIN workable.WorkQueueEntries queue
    ON queue.WorkerId = entries.WorkerId
WHERE entries.WorkerId = '{firstWorkerId.Value}'
  AND entries.FailedAt IS NOT NULL
  AND queue.Disposition = N'Failed'
  AND queue.ConcurrencyBucket IS NULL;
""");

        Assert.Equal(firstWorkerId, firstClaim.Single().Lease.WorkerId);
        Assert.Equal(secondWorkerId, claimAfterRetain.Single().Lease.WorkerId);
        Assert.Equal(1, retainedRows);
    }

    [Fact]
    public async Task RowsWithoutQueueEntriesAreNotClaimed()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await InsertDurableRow(
                connection,
                WorkerId.New(),
                "sql-not-queue-payload",
                "not-queue-payload",
                DateTimeOffset.UtcNow.AddMinutes(-5),
                includeQueueEntry: false,
                leaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        var claimed = await ClaimReady(store, "consumer", batchSize: 10);

        Assert.Empty(claimed);
    }

    [Fact]
    public async Task DurableRowBeingCompletedInTransactionCannotBeClaimed()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string leaseId = "complete-lease";
        var workerId = WorkerId.New();
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await InsertDurableRow(
                connection,
                workerId,
                "sql-completing-transaction",
                "completing-transaction",
                DateTimeOffset.UtcNow.AddMinutes(-5),
                leaseId: leaseId,
                leaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        await using var completionConnection = await this.OpenConnection();
        await using var completionTransaction = await completionConnection.BeginTransactionAsync();
        await store.DeleteFinal(
            [new WorkQueueDurabilityCleanupRequest(
                workerId,
                new WorkQueueDurabilityLease(workerId, "completing-owner", leaseId))],
            new WorkableSqlServerQueueDurabilityTransaction(completionConnection, completionTransaction));

        var claimedWhileCompleting = await ClaimReady(store, "consumer", batchSize: 10);
        await completionTransaction.CommitAsync();

        await using var verification = await this.OpenConnection();
        await WaitForEntryCount(verification, "completing-transaction", 0);

        Assert.Empty(claimedWhileCompleting);
    }

    [Fact]
    public async Task DurableCompletionRollbackLeavesDurableRowClaimable()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string leaseId = "rollback-lease";
        var workerId = WorkerId.New();
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await InsertDurableRow(
                connection,
                workerId,
                "sql-completion-rollback",
                "completion-rollback",
                DateTimeOffset.UtcNow.AddMinutes(-5),
                leaseId: leaseId,
                leaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        await using var completionConnection = await this.OpenConnection();
        await using var completionTransaction = await completionConnection.BeginTransactionAsync();
        await store.DeleteFinal(
            [new WorkQueueDurabilityCleanupRequest(
                workerId,
                new WorkQueueDurabilityLease(workerId, "rollback-owner", leaseId))],
            new WorkableSqlServerQueueDurabilityTransaction(completionConnection, completionTransaction));
        await completionTransaction.RollbackAsync();

        var claimedAfterRollback = await ClaimReady(store, "consumer", batchSize: 10);

        Assert.Equal(workerId, claimedAfterRollback.Single().Lease.WorkerId);
    }

    [Fact]
    public async Task RenewingLostDurableLeaseThrows()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-lost-lease-renew";
        var workerId = WorkerId.New();
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var connection = await this.OpenConnection();
        await InsertDurableRow(
            connection,
            workerId,
            workName,
            "lost-lease-renew",
            DateTimeOffset.UtcNow,
            leaseId: "current-lease",
            leaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1));

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        var exception = await Assert.ThrowsAsync<WorkQueueDurabilityLeaseLostException>(() =>
            store.RenewLeases(
                [new WorkQueueDurabilityLease(workerId, "old-owner", "stale-lease")],
                TimeSpan.FromMinutes(1)));

        Assert.Equal(workerId, Assert.Single(exception.Leases).WorkerId);
    }

    [Fact]
    public async Task RenewingDurableLeasesUsesSetBasedPartialLossDetection()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-batch-lease-renew";
        var firstWorkerId = WorkerId.New();
        var secondWorkerId = WorkerId.New();
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var connection = await this.OpenConnection();
        var originalExpiry = DateTimeOffset.UtcNow.AddSeconds(5);
        await InsertDurableRow(
            connection,
            firstWorkerId,
            workName,
            "batch-renew-kept",
            DateTimeOffset.UtcNow,
            leaseId: "kept-lease",
            leaseExpiresAt: originalExpiry);
        await InsertDurableRow(
            connection,
            secondWorkerId,
            workName,
            "batch-renew-lost",
            DateTimeOffset.UtcNow.AddMilliseconds(1),
            leaseId: "current-lease",
            leaseExpiresAt: originalExpiry);

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();
        var keptLease = new WorkQueueDurabilityLease(firstWorkerId, "owner", "kept-lease");
        var staleLease = new WorkQueueDurabilityLease(secondWorkerId, "owner", "stale-lease");

        var exception = await Assert.ThrowsAsync<WorkQueueDurabilityLeaseLostException>(() =>
            store.RenewLeases([keptLease, staleLease], TimeSpan.FromMinutes(1)));
        var keptExpiry = await ReadLeaseExpiry(connection, firstWorkerId);
        var lostExpiry = await ReadLeaseExpiry(connection, secondWorkerId);

        Assert.Equal(secondWorkerId, Assert.Single(exception.Leases).WorkerId);
        Assert.True(keptExpiry > originalExpiry);
        Assert.Equal(originalExpiry.ToUnixTimeSeconds(), lostExpiry.ToUnixTimeSeconds());
    }

    [Fact]
    public async Task StaleDurableLeaseCannotDeleteCurrentRow()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-stale-delete";
        var workerId = WorkerId.New();
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var connection = await this.OpenConnection();
        await InsertDurableRow(
            connection,
            workerId,
            workName,
            "stale-delete",
            DateTimeOffset.UtcNow,
            leaseId: "current-lease",
            leaseExpiresAt: DateTimeOffset.UtcNow.AddMinutes(1));

        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(this.ConnectionString, SchemaName)
            .BuildServiceProvider();
        var store = provider.GetRequiredService<IWorkPersistenceStore>();

        await Assert.ThrowsAsync<WorkQueueDurabilityLeaseLostException>(() =>
            store.DeleteFinal([new WorkQueueDurabilityCleanupRequest(
                workerId,
                new WorkQueueDurabilityLease(workerId, "old-owner", "stale-lease"))]));

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = """
SELECT COUNT(*)
FROM workable.WorkEntries
        WHERE WorkerId = @WorkerId;
""";
        countCommand.Parameters.AddWithValue("@WorkerId", workerId.Value);
        var rows = Convert.ToInt32(await countCommand.ExecuteScalarAsync());

        Assert.Equal(1, rows);
    }

    [Fact]
    public async Task StartupReplayDequeuesDurableRowsInBatch()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-startup-batch";
        const int batchSize = 25;
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            for (var index = 0; index < batchSize; index++)
            {
                await InsertDurableRow(
                    connection,
                    WorkerId.New(),
                    workName,
                    $"startup-batch-{index}",
                    DateTimeOffset.UtcNow.AddMilliseconds(index));
            }
        }

        var remaining = batchSize;
        var allRan = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var system = this.CreateSystem(
            workName,
            (_, _, _) =>
            {
                if (Interlocked.Decrement(ref remaining) == 0)
                {
                    allRan.TrySetResult();
                }

                return Task.FromResult(WorkExecutionResult.Success());
            },
            configuration => configuration.QueueDurably());

        await StartWithTimeout(system);
        await WaitWithTimeout(allRan.Task);
        await system.Stop();

        Assert.Equal(0, remaining);
    }

    [Fact]
    public async Task StartupReplayQueuesDurableRowsInCreatedOrderBeforeStartingDeferredWork()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string workName = "sql-startup-order";
        var firstSystem = this.CreateSystem(
            workName,
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            DurableOrderedConfiguration);
        await firstSystem.Start();

        await using var connection = await this.OpenConnection();
        await using var transaction = await connection.BeginTransactionAsync();
        var options = WorkerOptions.Default.WithSqlServerQueueDurabilityTransaction(connection, transaction);

        await firstSystem.Queue.Enqueue(workName, OrderedInput("first"), options);
        await firstSystem.Queue.Enqueue(workName, OrderedInput("second"), options);
        await SetCreatedAt(connection, transaction, "first", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        await SetCreatedAt(connection, transaction, "second", new DateTimeOffset(2026, 1, 1, 0, 0, 1, TimeSpan.Zero));
        await firstSystem.Stop();
        await transaction.CommitAsync();

        var executionOrder = new List<string>();
        var orderSync = new Lock();
        var bothStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondSystem = this.CreateSystem(
            workName,
            (_, input, _) =>
            {
                lock (orderSync)
                {
                    var subjectId = input?.SubjectId;
                    executionOrder.Add(subjectId is null ? string.Empty : subjectId.Value.Value);
                    if (executionOrder.Count == 2)
                    {
                        bothStarted.TrySetResult();
                    }
                }

                return Task.FromResult(WorkExecutionResult.Success());
            },
            DurableOrderedConfiguration);

        await secondSystem.Start();
        await WaitWithTimeout(bothStarted.Task);
        await secondSystem.Stop();

        Assert.Equal(["first", "second"], executionOrder);
    }

    [Fact]
    public async Task PersistsQueriesAndExpiresExecutionDiagnostics()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        var services = new ServiceCollection()
            .AddSingleton<IHostEnvironment>(new TestHostEnvironment(Environments.Development))
            .AddWorkableSqlServerPersistence(this.ConnectionString, SchemaName, persistenceScope: "diagnostic-tests")
            .AddWorkableSqlServerProfiling()
            .AddWorkableHttpClientProfiling()
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create("sql-diagnostics", "Persists logs and profiles in SQL Server."),
                (context, _, _) =>
                {
                    var logger = context.Services.GetRequiredService<ILogger<WorkableSqlServerPersistenceTests>>();
                    logger.LogInformation("sql diagnostic {ExecuteCount}", 2);
                    logger.LogWarning("sql diagnostic complete");
                    context.Profile.AddInfo("SQL execute count", new { Count = 2 });
                    return Task.FromResult(WorkExecutionResult.Success());
                },
                configuration =>
                {
                    configuration.ConfigureLogging(level: LogLevel.Warning, maximumBufferedEntries: 1);
                    configuration.PersistExecutionDiagnostics(TimeSpan.FromHours(1), LogLevel.Information);
                }));
        await using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var repository = provider.GetRequiredService<IWorkExecutionDiagnosticsRepository>();
        await system.Start();

        var completion = await (await system.Queue.Enqueue("sql-diagnostics")).WaitForCompletion();
        await StopWithTimeout(system);
        var query = await repository.Query(new WorkExecutionDiagnosticCriteria(
            system.Id,
            MinimumLogLevel: LogLevel.Warning,
            Take: 10));
        var summary = Assert.Single(query.Items);
        var artifact = await repository.Get(new WorkExecutionDiagnosticGetRequest(
            system.Id,
            completion.Worker!.Id,
            completion.Worker.LastIteration!.Sequence));
        var limitedArtifact = await repository.Get(new WorkExecutionDiagnosticGetRequest(
            system.Id,
            completion.Worker.Id,
            completion.Worker.LastIteration.Sequence,
            MaximumLogCount: 1));

        Assert.Equal(2, summary.PersistedLogCount);
        Assert.Equal(0, summary.DroppedLogCount);
        Assert.False(summary.ProfileDropped);
        Assert.True(summary.InstrumentationAvailability.SqlClientProfilingAvailable);
        Assert.True(summary.InstrumentationAvailability.HttpClientProfilingAvailable);
        Assert.NotNull(artifact);
        Assert.Equal(2, artifact.Logs.Count);
        Assert.False(artifact.LogsTruncated);
        Assert.NotNull(limitedArtifact);
        Assert.Single(limitedArtifact.Logs);
        Assert.True(limitedArtifact.LogsTruncated);
        Assert.NotNull(artifact.Profile);
        Assert.Contains(summary.Instrumentation, item => item.Instrumentation == WorkProfileInstrumentation.Application);
        Assert.Contains("\"ExecuteCount\":2", artifact.Logs[0].PropertiesJson, StringComparison.Ordinal);
        Assert.Empty((await repository.Query(new WorkExecutionDiagnosticCriteria(
            system.Id,
            MinimumLogLevel: LogLevel.Critical))).Items);

        var activeDiagnosticId = Guid.NewGuid();
        await repository.BeginIteration(new WorkExecutionDiagnosticIterationStart(
            activeDiagnosticId,
            system.Id,
            system.Name,
            WorkerId.New(),
            99,
            WorkDefinitionId.New(),
            "sql-diagnostics-active",
            DateTimeOffset.UtcNow.AddDays(-2),
            TimeSpan.FromHours(1),
            LogLevel.Information,
            null,
            new WorkExecutionDiagnosticInstrumentationAvailability(false, false),
            WorkExecutionDiagnosticCaptureSource.SystemConfiguration));
        var protectedActive = await repository.DeleteExpired(new WorkExecutionDiagnosticsExpirationRequest(
            system.Id,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(1))
        {
            ActiveDiagnosticIds = new HashSet<Guid> { activeDiagnosticId },
        });
        var orphanedDeleted = await repository.DeleteExpired(new WorkExecutionDiagnosticsExpirationRequest(
            system.Id,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddDays(1)));

        Assert.Equal(0, protectedActive);
        Assert.Equal(1, orphanedDeleted);

        var deleted = await repository.DeleteExpired(new WorkExecutionDiagnosticsExpirationRequest(
            system.Id,
            DateTimeOffset.UtcNow.AddHours(2),
            DateTimeOffset.UtcNow.AddHours(-1)));

        Assert.Equal(1, deleted);
        Assert.Empty((await repository.Query(new WorkExecutionDiagnosticCriteria(system.Id))).Items);
    }

    [Fact]
    public async Task ExpirationCleansThePersistenceScopeAndProtectsActiveDiagnosticsAcrossSystems()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string persistenceScope = "diagnostic-global-expiry";
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        var retiredContext = new WorkExecutionDiagnosticsInitializationContext(
            WorkSystemId.New(),
            "retired-diagnostic-system");
        var expiredCompletedId = Guid.Empty;
        var abandonedId = Guid.NewGuid();
        var expiredRuleId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await using (var retiredProvider = this.CreateDiagnosticsProvider(persistenceScope, autoDeploySchema: false))
        {
            var retiredRepository = retiredProvider.GetRequiredService<IWorkExecutionDiagnosticsRepository>();
            await retiredRepository.Initialize(retiredContext);
            expiredCompletedId = (await PersistDiagnostic(
                retiredRepository,
                retiredContext,
                "retired.completed",
                now.AddHours(-2),
                LogLevel.Information)).DiagnosticId;
            await retiredRepository.BeginIteration(CreateDiagnosticStart(
                abandonedId,
                retiredContext,
                WorkerId.New(),
                2,
                "retired.abandoned",
                now.AddHours(-2)));
            await retiredRepository.UpsertCaptureRule(new WorkExecutionDiagnosticCaptureRule(
                expiredRuleId,
                retiredContext.WorkSystemId,
                retiredContext.WorkSystemName,
                null,
                LogLevel.Information,
                null,
                TimeSpan.FromHours(1),
                now.AddMinutes(-10),
                now.AddMinutes(-1),
                new WorkActor("expiry-test")), 5);
        }

        await using var provider = this.CreateDiagnosticsProvider(persistenceScope, autoDeploySchema: false);
        var repository = provider.GetRequiredService<IWorkExecutionDiagnosticsRepository>();
        var currentContext = new WorkExecutionDiagnosticsInitializationContext(
            WorkSystemId.New(),
            "current-diagnostic-system");
        var activeContext = new WorkExecutionDiagnosticsInitializationContext(
            WorkSystemId.New(),
            "active-diagnostic-system");
        await repository.Initialize(currentContext);
        await repository.Initialize(activeContext);
        var activeId = Guid.NewGuid();
        await repository.BeginIteration(CreateDiagnosticStart(
            activeId,
            activeContext,
            WorkerId.New(),
            1,
            "active.work",
            now.AddHours(-2)));

        await using (var connection = await this.OpenConnection())
        {
            await Execute(connection, $"""
UPDATE workable.WorkIterationDiagnostics
SET UpdatedAt = CASE
    WHEN DiagnosticId = '{abandonedId:D}' THEN DATEADD(day, -2, SYSDATETIMEOFFSET())
    ELSE DATEADD(hour, -2, SYSDATETIMEOFFSET())
END
WHERE DiagnosticId IN ('{abandonedId:D}', '{activeId:D}');
""");
        }

        var deleted = await repository.DeleteExpired(new WorkExecutionDiagnosticsExpirationRequest(
            currentContext.WorkSystemId,
            now,
            now.AddDays(-1)));
        Assert.Equal(2, deleted);
        Assert.Empty(await repository.ListCaptureRules(currentContext));

        await using var verification = await this.OpenConnection();
        Assert.Equal(0, await Scalar<int>(verification, $"""
SELECT COUNT(*) FROM workable.WorkIterationDiagnostics
WHERE DiagnosticId IN ('{expiredCompletedId:D}', '{abandonedId:D}');
"""));
        Assert.Equal(1, await Scalar<int>(verification, $"""
SELECT COUNT(*) FROM workable.WorkIterationDiagnostics WHERE DiagnosticId = '{activeId:D}';
"""));
        Assert.Equal(0, await Scalar<int>(verification, $"""
SELECT COUNT(*) FROM workable.WorkDiagnosticCaptureRules WHERE RuleId = '{expiredRuleId:D}';
"""));
    }

    [Fact]
    public async Task ExpirationDoesNotDeleteActiveDiagnosticsOwnedByAnotherRepositoryInstance()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        const string persistenceScope = "diagnostic-cross-instance-expiry";
        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var cleanupProvider = this.CreateDiagnosticsProvider(persistenceScope, autoDeploySchema: false);
        await using var activeProvider = this.CreateDiagnosticsProvider(persistenceScope, autoDeploySchema: false);
        var cleanupRepository = cleanupProvider.GetRequiredService<IWorkExecutionDiagnosticsRepository>();
        var activeRepository = activeProvider.GetRequiredService<IWorkExecutionDiagnosticsRepository>();
        var cleanupContext = new WorkExecutionDiagnosticsInitializationContext(
            WorkSystemId.New(),
            "cleanup-diagnostic-system");
        var activeContext = new WorkExecutionDiagnosticsInitializationContext(
            WorkSystemId.New(),
            "active-cross-instance-system");
        await cleanupRepository.Initialize(cleanupContext);
        await activeRepository.Initialize(activeContext);

        var now = DateTimeOffset.UtcNow;
        var diagnosticId = Guid.NewGuid();
        var workerId = WorkerId.New();
        await activeRepository.BeginIteration(CreateDiagnosticStart(
            diagnosticId,
            activeContext,
            workerId,
            1,
            "active.cross-instance",
            now.AddHours(-2)));
        await using (var connection = await this.OpenConnection())
        {
            await Execute(connection, $"""
UPDATE workable.WorkIterationDiagnostics
SET UpdatedAt = DATEADD(hour, -2, SYSDATETIMEOFFSET())
WHERE DiagnosticId = '{diagnosticId:D}';
""");
        }

        var deleted = await cleanupRepository.DeleteExpired(new WorkExecutionDiagnosticsExpirationRequest(
            cleanupContext.WorkSystemId,
            now,
            now.AddDays(-1)));
        await activeRepository.CompleteIteration(new WorkExecutionDiagnosticIterationCompletion(
            diagnosticId,
            WorkCompletionStatus.Completed,
            1,
            now,
            TimeSpan.FromHours(2),
            null,
            false,
            0,
            0,
            []));
        var artifact = await activeRepository.Get(new WorkExecutionDiagnosticGetRequest(
            activeContext.WorkSystemId,
            workerId,
            1));

        Assert.Equal(0, deleted);
        Assert.NotNull(artifact);
        Assert.Equal(WorkCompletionStatus.Completed, artifact.Summary.Status);
    }

    [Fact]
    public async Task PersistsUpdatesExpiresAndDeletesExecutionDiagnosticCaptureRules()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await using var provider = this.CreateDiagnosticsProvider("diagnostic-rule-crud");
        var repository = provider.GetRequiredService<IWorkExecutionDiagnosticsRepository>();
        var context = new WorkExecutionDiagnosticsInitializationContext(
            WorkSystemId.New(),
            "diagnostic-rule-system");
        await repository.Initialize(context);
        var now = DateTimeOffset.UtcNow;
        var ruleId = Guid.NewGuid();
        var actor = new WorkActor("diagnostic-user", "Diagnostic User", "diagnostic@example.test");

        await repository.UpsertCaptureRule(new WorkExecutionDiagnosticCaptureRule(
            ruleId,
            context.WorkSystemId,
            context.WorkSystemName,
            "orders.rebuild",
            LogLevel.Information,
            WorkProfileCaptureMode.Bounded,
            TimeSpan.FromHours(2),
            now,
            now.AddMinutes(15),
            actor), 1);
        await repository.UpsertCaptureRule(new WorkExecutionDiagnosticCaptureRule(
            ruleId,
            context.WorkSystemId,
            context.WorkSystemName,
            "orders.rebuild",
            LogLevel.Warning,
            WorkProfileCaptureMode.Full,
            TimeSpan.FromHours(3),
            now,
            now.AddMinutes(30),
            actor), 1);

        var persisted = Assert.Single(await repository.ListCaptureRules(context));
        Assert.Equal(ruleId, persisted.Id);
        Assert.Equal(LogLevel.Warning, persisted.MinimumLogLevel);
        Assert.Equal(WorkProfileCaptureMode.Full, persisted.ProfileCaptureMode);
        Assert.Equal(TimeSpan.FromHours(3), persisted.ArtifactRetention);
        Assert.Equal(actor, persisted.CreatedBy);
        var replacementId = Guid.NewGuid();
        await repository.UpsertCaptureRule(new WorkExecutionDiagnosticCaptureRule(
            replacementId,
            context.WorkSystemId,
            context.WorkSystemName,
            "ORDERS.REBUILD",
            LogLevel.Error,
            null,
            TimeSpan.FromHours(1),
            now,
            now.AddMinutes(20),
            actor), 1);
        persisted = Assert.Single(await repository.ListCaptureRules(context));
        Assert.Equal(replacementId, persisted.Id);
        Assert.False(await repository.DeleteCaptureRule(
            new WorkExecutionDiagnosticCaptureRuleDeleteRequest(context.WorkSystemId, ruleId)));
        Assert.True(await repository.DeleteCaptureRule(
            new WorkExecutionDiagnosticCaptureRuleDeleteRequest(context.WorkSystemId, replacementId)));
        Assert.False(await repository.DeleteCaptureRule(
            new WorkExecutionDiagnosticCaptureRuleDeleteRequest(context.WorkSystemId, ruleId)));

        var expiredRule = new WorkExecutionDiagnosticCaptureRule(
            Guid.NewGuid(),
            context.WorkSystemId,
            context.WorkSystemName,
            null,
            LogLevel.Debug,
            null,
            TimeSpan.FromHours(1),
            now.AddMinutes(-10),
            now.AddMinutes(-1),
            actor);
        await repository.UpsertCaptureRule(expiredRule, 1);

        Assert.Empty(await repository.ListCaptureRules(context));
        await using var connection = await this.OpenConnection();
        Assert.Equal(0, await Scalar<int>(connection, $"""
SELECT COUNT(*)
FROM workable.WorkDiagnosticCaptureRules
WHERE RuleId = '{expiredRule.Id:D}';
"""));
    }

    [Fact]
    public async Task CaptureRuleRepositoryRejectsANonPositiveActiveRuleLimitBeforeConnecting()
    {
        var repository = new WorkableSqlServerExecutionDiagnosticsRepository(
            new WorkableSqlServerPersistenceOptions
            {
                ConnectionString = "Server=unused.invalid;Initial Catalog=unused;Integrated Security=true;TrustServerCertificate=true",
            });
        var now = DateTimeOffset.UtcNow;
        var rule = new WorkExecutionDiagnosticCaptureRule(
            Guid.NewGuid(),
            WorkSystemId.New(),
            "validation",
            null,
            LogLevel.Information,
            null,
            TimeSpan.FromMinutes(5),
            now,
            now.AddMinutes(1),
            new WorkActor("validator"));

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            repository.UpsertCaptureRule(rule, maximumActiveRules: 0));
    }

    [Fact]
    public async Task ConcurrentCaptureRuleWritesEnforceTheDatabaseMaximum()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var firstProvider = this.CreateDiagnosticsProvider("diagnostic-rule-limit", autoDeploySchema: false);
        await using var secondProvider = this.CreateDiagnosticsProvider("diagnostic-rule-limit", autoDeploySchema: false);
        var firstRepository = firstProvider.GetRequiredService<IWorkExecutionDiagnosticsRepository>();
        var secondRepository = secondProvider.GetRequiredService<IWorkExecutionDiagnosticsRepository>();
        var context = new WorkExecutionDiagnosticsInitializationContext(
            WorkSystemId.New(),
            "diagnostic-rule-limit-system");
        await Task.WhenAll(firstRepository.Initialize(context), secondRepository.Initialize(context));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;
        var now = DateTimeOffset.UtcNow;

        async Task<Exception?> TryCreate(
            IWorkExecutionDiagnosticsRepository repository,
            string definitionName)
        {
            if (Interlocked.Increment(ref ready) == 2)
            {
                gate.TrySetResult();
            }

            await gate.Task;
            try
            {
                await repository.UpsertCaptureRule(new WorkExecutionDiagnosticCaptureRule(
                    Guid.NewGuid(),
                    context.WorkSystemId,
                    context.WorkSystemName,
                    definitionName,
                    LogLevel.Information,
                    null,
                    TimeSpan.FromHours(1),
                    now,
                    now.AddMinutes(10),
                    new WorkActor(definitionName)), 1);
                return null;
            }
            catch (SqlException exception)
            {
                return exception;
            }
        }

        var outcomes = await Task.WhenAll(
            Task.Run(() => TryCreate(firstRepository, "rule.one")),
            Task.Run(() => TryCreate(secondRepository, "rule.two")));

        Assert.Single(outcomes, outcome => outcome is null);
        var rejected = Assert.IsType<SqlException>(Assert.Single(outcomes, outcome => outcome is not null));
        Assert.Equal(50010, rejected.Number);
        Assert.Single(await firstRepository.ListCaptureRules(context));
    }

    [Fact]
    public async Task ConcurrentCaptureRuleWritesAtomicallyReplaceTheSameScope()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var firstProvider = this.CreateDiagnosticsProvider("diagnostic-rule-replace", autoDeploySchema: false);
        await using var secondProvider = this.CreateDiagnosticsProvider("diagnostic-rule-replace", autoDeploySchema: false);
        var firstRepository = firstProvider.GetRequiredService<IWorkExecutionDiagnosticsRepository>();
        var secondRepository = secondProvider.GetRequiredService<IWorkExecutionDiagnosticsRepository>();
        var context = new WorkExecutionDiagnosticsInitializationContext(
            WorkSystemId.New(),
            "diagnostic-rule-replace-system");
        await Task.WhenAll(firstRepository.Initialize(context), secondRepository.Initialize(context));
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = 0;

        async Task Replace(IWorkExecutionDiagnosticsRepository repository, string actorId)
        {
            if (Interlocked.Increment(ref ready) == 2)
            {
                gate.TrySetResult();
            }

            await gate.Task;
            var now = DateTimeOffset.UtcNow;
            await repository.UpsertCaptureRule(new WorkExecutionDiagnosticCaptureRule(
                Guid.NewGuid(),
                context.WorkSystemId,
                context.WorkSystemName,
                "Orders.Run",
                LogLevel.Information,
                null,
                TimeSpan.FromHours(1),
                now,
                now.AddMinutes(10),
                new WorkActor(actorId)), 1);
        }

        await Task.WhenAll(
            Task.Run(() => Replace(firstRepository, "first")),
            Task.Run(() => Replace(secondRepository, "second")));

        var persisted = Assert.Single(await firstRepository.ListCaptureRules(context));
        Assert.Equal("Orders.Run", persisted.DefinitionName);
        Assert.Contains(persisted.CreatedBy.Id, new[] { "first", "second" });
    }

    [Fact]
    public async Task ExecutionDiagnosticsAreIsolatedByPersistenceScopeAndWorkSystemName()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using var firstProvider = this.CreateDiagnosticsProvider("diagnostic-scope-a", autoDeploySchema: false);
        await using var secondProvider = this.CreateDiagnosticsProvider("diagnostic-scope-b", autoDeploySchema: false);
        var firstRepository = firstProvider.GetRequiredService<IWorkExecutionDiagnosticsRepository>();
        var secondRepository = secondProvider.GetRequiredService<IWorkExecutionDiagnosticsRepository>();
        var firstContext = new WorkExecutionDiagnosticsInitializationContext(
            WorkSystemId.New(),
            "shared-diagnostic-system");
        var secondContext = new WorkExecutionDiagnosticsInitializationContext(
            WorkSystemId.New(),
            "shared-diagnostic-system");
        var otherSystemContext = new WorkExecutionDiagnosticsInitializationContext(
            WorkSystemId.New(),
            "other-diagnostic-system");
        await firstRepository.Initialize(firstContext);
        await firstRepository.Initialize(otherSystemContext);
        await secondRepository.Initialize(secondContext);
        var persisted = await PersistDiagnostic(
            firstRepository,
            firstContext,
            "isolated.diagnostic",
            DateTimeOffset.UtcNow,
            LogLevel.Warning);
        var rule = new WorkExecutionDiagnosticCaptureRule(
            Guid.NewGuid(),
            firstContext.WorkSystemId,
            firstContext.WorkSystemName,
            null,
            LogLevel.Warning,
            null,
            TimeSpan.FromHours(1),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(10),
            new WorkActor("scope-user"));
        await firstRepository.UpsertCaptureRule(rule, 5);

        Assert.Empty((await secondRepository.Query(
            new WorkExecutionDiagnosticCriteria(secondContext.WorkSystemId))).Items);
        Assert.Null(await secondRepository.Get(new WorkExecutionDiagnosticGetRequest(
            secondContext.WorkSystemId,
            persisted.WorkerId,
            persisted.Sequence)));
        Assert.Empty(await secondRepository.ListCaptureRules(secondContext));
        Assert.Empty((await firstRepository.Query(
            new WorkExecutionDiagnosticCriteria(otherSystemContext.WorkSystemId))).Items);
        Assert.Empty(await firstRepository.ListCaptureRules(otherSystemContext));
        Assert.Single((await firstRepository.Query(
            new WorkExecutionDiagnosticCriteria(firstContext.WorkSystemId))).Items);
        Assert.Single(await firstRepository.ListCaptureRules(firstContext));
    }

    [Fact]
    public async Task ExecutionDiagnosticQueriesApplyAllFiltersAndStableOrdering()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await using var provider = this.CreateDiagnosticsProvider("diagnostic-query-filters");
        var repository = provider.GetRequiredService<IWorkExecutionDiagnosticsRepository>();
        var context = new WorkExecutionDiagnosticsInitializationContext(
            WorkSystemId.New(),
            "diagnostic-query-system");
        await repository.Initialize(context);
        var now = DateTimeOffset.UtcNow;
        var oldest = await PersistDiagnostic(
            repository,
            context,
            "alpha.work",
            now.AddMinutes(-3),
            LogLevel.Information,
            sequence: 1);
        var middle = await PersistDiagnostic(
            repository,
            context,
            "beta.work",
            now.AddMinutes(-2),
            LogLevel.Warning,
            sequence: 2);
        var newest = await PersistDiagnostic(
            repository,
            context,
            "alpha.work",
            now.AddMinutes(-1),
            LogLevel.Critical,
            sequence: 3);

        var ordered = (await repository.Query(new WorkExecutionDiagnosticCriteria(
            context.WorkSystemId,
            Take: 2))).Items;
        var definitions = (await repository.Query(new WorkExecutionDiagnosticCriteria(
            context.WorkSystemId,
            DefinitionName: "alpha.work"))).Items;
        var worker = (await repository.Query(new WorkExecutionDiagnosticCriteria(
            context.WorkSystemId,
            WorkerId: middle.WorkerId))).Items;
        var completedWindow = (await repository.Query(new WorkExecutionDiagnosticCriteria(
            context.WorkSystemId,
            CompletedAfter: now.AddMinutes(-2.5),
            CompletedBefore: now.AddMinutes(-0.5)))).Items;
        var severe = (await repository.Query(new WorkExecutionDiagnosticCriteria(
            context.WorkSystemId,
            MinimumLogLevel: LogLevel.Error))).Items;

        Assert.Equal([newest.DiagnosticId, middle.DiagnosticId], ordered.Select(item => item.DiagnosticId));
        Assert.Equal([newest.DiagnosticId, oldest.DiagnosticId], definitions.Select(item => item.DiagnosticId));
        Assert.Equal(middle.DiagnosticId, Assert.Single(worker).DiagnosticId);
        Assert.Equal([newest.DiagnosticId, middle.DiagnosticId], completedWindow.Select(item => item.DiagnosticId));
        Assert.Equal(newest.DiagnosticId, Assert.Single(severe).DiagnosticId);
    }

    [Fact]
    public async Task ExecutionDiagnosticWritesAreIdempotentAndExpirationCascades()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await using var provider = this.CreateDiagnosticsProvider("diagnostic-idempotency");
        var repository = provider.GetRequiredService<IWorkExecutionDiagnosticsRepository>();
        var context = new WorkExecutionDiagnosticsInitializationContext(
            WorkSystemId.New(),
            "diagnostic-idempotency-system");
        await repository.Initialize(context);
        var diagnosticId = Guid.NewGuid();
        var workerId = WorkerId.New();
        var completedAt = DateTimeOffset.UtcNow;
        var start = CreateDiagnosticStart(
            diagnosticId,
            context,
            workerId,
            7,
            "idempotent.work",
            completedAt.AddSeconds(-1));
        var log = new WorkExecutionDiagnosticLogRecord(
            diagnosticId,
            0,
            completedAt,
            LogLevel.Warning,
            "idempotent.category",
            new EventId(27, "IdempotentLog"),
            "write once",
            "{\"attempt\":1}",
            null,
            null,
            null,
            null,
            null);
        var completion = new WorkExecutionDiagnosticIterationCompletion(
            diagnosticId,
            WorkCompletionStatus.Completed,
            1,
            completedAt,
            TimeSpan.FromSeconds(1),
            null,
            false,
            1,
            0,
            [new WorkExecutionInstrumentationSummary("sql-client", 2, 2, 8, 5)]);

        await repository.BeginIteration(start);
        await repository.BeginIteration(start);
        await repository.AppendLogs([log]);
        await repository.AppendLogs([log]);
        await repository.CompleteIteration(completion);
        await repository.CompleteIteration(completion);

        var artifact = await repository.Get(new WorkExecutionDiagnosticGetRequest(
            context.WorkSystemId,
            workerId,
            7));
        Assert.NotNull(artifact);
        Assert.Single(artifact.Logs);
        Assert.Single(artifact.Summary.Instrumentation);

        Assert.Equal(1, await repository.DeleteExpired(new WorkExecutionDiagnosticsExpirationRequest(
            context.WorkSystemId,
            completedAt.AddHours(2),
            completedAt.AddHours(-1))));
        Assert.Null(await repository.Get(new WorkExecutionDiagnosticGetRequest(
            context.WorkSystemId,
            workerId,
            7)));
        await using var connection = await this.OpenConnection();
        Assert.Equal(0, await Scalar<int>(connection, $"""
SELECT
    (SELECT COUNT(*) FROM workable.WorkIterationDiagnostics WHERE DiagnosticId = '{diagnosticId:D}') +
    (SELECT COUNT(*) FROM workable.WorkIterationDiagnosticLogs WHERE DiagnosticId = '{diagnosticId:D}') +
    (SELECT COUNT(*) FROM workable.WorkIterationInstrumentation WHERE DiagnosticId = '{diagnosticId:D}');
"""));
    }

    [Fact]
    public async Task ExecutionDiagnosticsAutoDeployDisabledRejectsAnIncompleteSchema()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await Execute(connection, "DROP TABLE workable.WorkIterationInstrumentation;");
        }

        await using var provider = this.CreateDiagnosticsProvider(
            "diagnostic-incomplete-schema",
            autoDeploySchema: false);
        var repository = provider.GetRequiredService<IWorkExecutionDiagnosticsRepository>();

        var exception = await Assert.ThrowsAsync<WorkableSqlServerSchemaDeploymentException>(() =>
            repository.Initialize(new WorkExecutionDiagnosticsInitializationContext(
                WorkSystemId.New(),
                "diagnostic-incomplete-schema-system")));

        Assert.Contains("could not validate schema", exception.Message);
        var validation = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("WorkIterationInstrumentation", validation.Message);
    }

    [Fact]
    public async Task ExecutionDiagnosticsValidationRejectsMissingColumnsIndexesAndOldVersions()
    {
        if (this.SkipIfUnavailable())
        {
            return;
        }

        await WorkableSqlServerSchema.Apply(this.ConnectionString, SchemaName);
        await using (var connection = await this.OpenConnection())
        {
            await Execute(connection, """
ALTER TABLE workable.WorkDiagnosticCaptureRules DROP COLUMN CreatedByJson;
DROP INDEX IX_WorkableWorkIterationDiagnostics_ExpirationByScope
    ON workable.WorkIterationDiagnostics;
UPDATE workable.SchemaVersion
SET Version = 5
WHERE Component = N'ExecutionDiagnostics';
""");
        }

        await using var provider = this.CreateDiagnosticsProvider(
            "diagnostic-invalid-schema-shape",
            autoDeploySchema: false);
        var repository = provider.GetRequiredService<IWorkExecutionDiagnosticsRepository>();

        var exception = await Assert.ThrowsAsync<WorkableSqlServerSchemaDeploymentException>(() =>
            repository.Initialize(new WorkExecutionDiagnosticsInitializationContext(
                WorkSystemId.New(),
                "diagnostic-invalid-schema-shape-system")));

        var validation = Assert.IsType<InvalidOperationException>(exception.InnerException);
        Assert.Contains("WorkDiagnosticCaptureRules.CreatedByJson", validation.Message);
        Assert.Contains("IX_WorkableWorkIterationDiagnostics_ExpirationByScope", validation.Message);
        Assert.Contains("schema version 7 (installed: 5)", validation.Message);
    }

    private ServiceProvider CreateDiagnosticsProvider(string persistenceScope, bool autoDeploySchema = true)
        => new ServiceCollection()
            .AddWorkableSqlServerPersistence(
                this.ConnectionString,
                SchemaName,
                persistenceScope,
                autoDeploySchema)
            .BuildServiceProvider();

    private static WorkExecutionDiagnosticIterationStart CreateDiagnosticStart(
        Guid diagnosticId,
        WorkExecutionDiagnosticsInitializationContext context,
        WorkerId workerId,
        long sequence,
        string definitionName,
        DateTimeOffset startedAt)
        => new(
            diagnosticId,
            context.WorkSystemId,
            context.WorkSystemName,
            workerId,
            sequence,
            WorkDefinitionId.New(),
            definitionName,
            startedAt,
            TimeSpan.FromHours(1),
            LogLevel.Information,
            WorkProfileCaptureMode.Bounded,
            new WorkExecutionDiagnosticInstrumentationAvailability(true, true),
            WorkExecutionDiagnosticCaptureSource.WorkConfiguration);

    private static async Task<(Guid DiagnosticId, WorkerId WorkerId, long Sequence)> PersistDiagnostic(
        IWorkExecutionDiagnosticsRepository repository,
        WorkExecutionDiagnosticsInitializationContext context,
        string definitionName,
        DateTimeOffset completedAt,
        LogLevel logLevel,
        long sequence = 1)
    {
        var diagnosticId = Guid.NewGuid();
        var workerId = WorkerId.New();
        await repository.BeginIteration(CreateDiagnosticStart(
            diagnosticId,
            context,
            workerId,
            sequence,
            definitionName,
            completedAt.AddSeconds(-1)));
        await repository.AppendLogs(
        [
            new WorkExecutionDiagnosticLogRecord(
                diagnosticId,
                0,
                completedAt,
                logLevel,
                "integration.category",
                new EventId((int)sequence, "IntegrationLog"),
                $"{definitionName} completed",
                null,
                null,
                null,
                null,
                null,
                null),
        ]);
        await repository.CompleteIteration(new WorkExecutionDiagnosticIterationCompletion(
            diagnosticId,
            WorkCompletionStatus.Completed,
            1,
            completedAt,
            TimeSpan.FromSeconds(1),
            null,
            false,
            1,
            0,
            []));
        return (diagnosticId, workerId, sequence);
    }

    private IWorkSystem CreateSystem(
        string name,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder> configure,
        bool autoDeploySchema = true)
    {
        var provider = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(
                this.ConnectionString,
                SchemaName,
                autoDeploySchema)
            .AddWorkableSystem(builder => builder.AddWork(
                WorkDefinition.Create(name, $"SQL Server integration test for {name}."),
                execute,
                configure))
            .BuildServiceProvider();

        return provider.GetRequiredService<IWorkSystemRegistry>().Default;
    }

    private IWorkSystemRegistry CreateRegistry(Func<IServiceCollection, IServiceCollection> configure)
    {
        var services = new ServiceCollection()
            .AddWorkableSqlServerDurableQueue(
                this.ConnectionString,
                SchemaName);

        return configure(services)
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>();
    }

    private bool SkipIfUnavailable()
        => false;

    private async Task<SqlConnection> OpenConnection()
    {
        var connection = new SqlConnection(this.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task Execute(SqlConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task Execute(
        SqlConnection connection,
        string commandText,
        params SqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task SetCreatedAt(
        SqlConnection connection,
        DbTransaction transaction,
        string subjectValue,
        DateTimeOffset createdAt)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = (SqlTransaction)transaction;
        command.CommandText = """
UPDATE workable.WorkEntries
SET CreatedAt = @CreatedAt
WHERE SubjectValue = @SubjectValue;

UPDATE queue
SET CreatedAt = @CreatedAt
FROM workable.WorkQueueEntries queue
INNER JOIN workable.WorkEntries entries
    ON entries.WorkerId = queue.WorkerId
WHERE entries.SubjectValue = @SubjectValue;
""";
        AddParameter(command, "@CreatedAt", createdAt);
        AddParameter(command, "@SubjectValue", subjectValue);
        await command.ExecuteNonQueryAsync();
    }

    private static void AddParameter(DbCommand command, string name, object value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task<IReadOnlyList<WorkQueueDurabilityEntry>> ClaimReady(
        IWorkPersistenceStore store,
        string ownerId,
        int batchSize,
        string? workSystemName = null)
    {
        var entries = new List<WorkQueueDurabilityEntry>();
        await foreach (var entry in store.ClaimReady(
            new WorkQueueDurabilityClaimRequest(
                WorkSystemName: workSystemName,
                OwnerId: ownerId,
                BatchSize: batchSize,
                LeaseDuration: TimeSpan.FromMinutes(1))))
        {
            entries.Add(entry);
        }

        return entries;
    }

    private static async Task<IReadOnlyList<WorkQueueDurabilityFailedEntry>> ClaimFailed(
        IWorkPersistenceStore store,
        string ownerId,
        int batchSize,
        string? workSystemName = null)
    {
        var entries = new List<WorkQueueDurabilityFailedEntry>();
        await foreach (var entry in store.ClaimFailed(
            new WorkQueueDurabilityClaimRequest(
                WorkSystemName: workSystemName,
                OwnerId: ownerId,
                BatchSize: batchSize,
                LeaseDuration: TimeSpan.FromMinutes(1))))
        {
            entries.Add(entry);
        }

        return entries;
    }

    private static async Task InsertDurableRow(
        SqlConnection connection,
        WorkerId workerId,
        string definitionName,
        string subjectValue,
        DateTimeOffset createdAt,
        bool includeQueueEntry = true,
        bool hasIdempotencyReservation = true,
        string? leaseId = null,
        DateTimeOffset? leaseExpiresAt = null,
        WorkInput? input = null,
        WorkConfiguration? configuration = null)
    {
        var rowInput = input ?? WorkInput.Empty.WithSubject(new WorkSubjectId("order", subjectValue));
        var rowConfiguration = configuration ?? WorkConfiguration.Default with
        {
            Coordination = WorkCoordinationConfiguration.Default with
            {
                IsEnabled = true,
                Storage = WorkCoordinationStorage.Persistent,
                Durability = new WorkQueueDurabilityConfiguration
                {
                    IsEnabled = true,
                },
                Idempotency = WorkIdempotencyConfiguration.Default with
                {
                    IsEnabled = true,
                },
            },
        };

        await using var command = connection.CreateCommand();
        command.CommandText = """
INSERT INTO workable.WorkEntries
(
    WorkerId,
    WorkSystemName,
    DefinitionName,
    HasIdempotencyReservation,
    SubjectType,
    SubjectValue,
    InputJson,
    OptionsJson,
    ConfigurationJson,
    OriginJson,
    CreatedAt
)
VALUES
(
    @WorkerId,
    @WorkSystemName,
    @DefinitionName,
    @HasIdempotencyReservation,
    @SubjectType,
    @SubjectValue,
    @InputJson,
    @OptionsJson,
    @ConfigurationJson,
    @OriginJson,
    @CreatedAt
);

IF @IncludeQueueEntry = 1
BEGIN
    INSERT INTO workable.WorkQueueEntries
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
        LeaseId,
        LeaseExpiresAt,
        ConcurrencyBucket
    )
    VALUES
    (
        @WorkerId,
        @WorkSystemName,
        @DefinitionName,
        @HasPersistentConcurrency,
        @ConcurrencyScope,
        @ConcurrencyMaximumCapacity,
        @SubjectType,
        @SubjectValue,
        @ConcurrencyType,
        @ConcurrencyValue,
        @CreatedAt,
        @LeaseId,
        @LeaseExpiresAt,
        CASE
            WHEN @HasPersistentConcurrency = 1 AND @LeaseId IS NOT NULL THEN N'Executing'
            ELSE NULL
        END
    );
END;
""";
        var hasPersistentConcurrency = rowConfiguration.Coordination.IsPersistentConcurrencyEnabled;
        var subjectId = rowInput.SubjectId;
        command.Parameters.AddWithValue("@WorkerId", workerId.Value);
        command.Parameters.AddWithValue("@WorkSystemName", "default");
        command.Parameters.AddWithValue("@DefinitionName", definitionName);
        command.Parameters.AddWithValue("@IncludeQueueEntry", includeQueueEntry);
        command.Parameters.AddWithValue("@HasIdempotencyReservation", hasIdempotencyReservation);
        command.Parameters.AddWithValue("@HasPersistentConcurrency", hasPersistentConcurrency);
        command.Parameters.AddWithValue(
            "@ConcurrencyScope",
            hasPersistentConcurrency ? rowConfiguration.Coordination.Concurrency.Scope.ToString() : DBNull.Value);
        command.Parameters.AddWithValue(
            "@ConcurrencyMaximumCapacity",
            hasPersistentConcurrency ? rowConfiguration.Coordination.Concurrency.MaximumCapacity : DBNull.Value);
        command.Parameters.AddWithValue("@SubjectType", (object?)subjectId?.Type ?? DBNull.Value);
        command.Parameters.AddWithValue("@SubjectValue", (object?)subjectId?.Value ?? subjectValue);
        var concurrencyKey = rowInput.ConcurrencyKey;
        command.Parameters.AddWithValue("@ConcurrencyType", concurrencyKey is null ? DBNull.Value : concurrencyKey.Value.Type);
        command.Parameters.AddWithValue("@ConcurrencyValue", concurrencyKey is null ? DBNull.Value : concurrencyKey.Value.Value);
        command.Parameters.AddWithValue(
            "@InputJson",
            JsonSerializer.Serialize(
                rowInput,
                DurableJsonOptions));
        command.Parameters.AddWithValue("@OptionsJson", JsonSerializer.Serialize(WorkerOptions.Default, DurableJsonOptions));
        command.Parameters.AddWithValue(
            "@ConfigurationJson",
            JsonSerializer.Serialize(rowConfiguration, DurableJsonOptions));
        command.Parameters.AddWithValue(
            "@OriginJson",
            JsonSerializer.Serialize(
                WorkOrigin.Create(WorkInvocationChannel.InProcess),
                DurableJsonOptions));
        command.Parameters.AddWithValue("@CreatedAt", createdAt);
        command.Parameters.AddWithValue("@LeaseId", (object?)leaseId ?? DBNull.Value);
        command.Parameters.AddWithValue("@LeaseExpiresAt", (object?)leaseExpiresAt ?? DBNull.Value);

        await command.ExecuteNonQueryAsync();
    }

    private static void DurableOrderedConfiguration(IWorkConfigurationBuilder configuration)
        => configuration
            .QueueDurably()
            .LimitConcurrency(
                maximumCapacity: 1,
                scope: WorkConcurrencyScope.PerConcurrencyKey,
                blockingMode: WorkConcurrencyBlockingMode.WhileExecuting,
                limitReachedBehavior: WorkConcurrencyLimitReachedBehavior.DeferStart);

    private static WorkConfiguration DurablePersistenceConcurrencyConfiguration(
        int maximumCapacity = 1,
        WorkConcurrencyScope scope = WorkConcurrencyScope.PerConcurrencyKey,
        bool enableIdempotency = true)
        => WorkConfiguration.Default with
        {
            Coordination = WorkCoordinationConfiguration.Default with
            {
                IsEnabled = true,
                Storage = WorkCoordinationStorage.Persistent,
                Durability = new WorkQueueDurabilityConfiguration
                {
                    IsEnabled = true,
                },
                Idempotency = WorkIdempotencyConfiguration.Default with
                {
                    IsEnabled = enableIdempotency,
                },
                Concurrency = WorkConcurrencyConfiguration.Default with
                {
                    IsEnabled = true,
                    MaximumCapacity = maximumCapacity,
                    Scope = scope,
                    BlockingMode = WorkConcurrencyBlockingMode.WhileExecuting,
                    LimitReachedBehavior = WorkConcurrencyLimitReachedBehavior.DeferStart,
                },
            },
        };

    private static WorkInput DurableConcurrencyInput(string subjectValue)
        => WorkInput.Empty
            .WithSubject(new WorkSubjectId("order", subjectValue))
            .WithConcurrencyKey(new WorkConcurrencyKey("persistent-concurrency", "shared"));

    private static WorkInput OrderedInput(string value)
        => WorkInput.Empty
            .WithSubject(new WorkSubjectId("order", value))
            .WithConcurrencyKey(new WorkConcurrencyKey("startup-order", "shared"));

    private static async Task StartWithTimeout(IWorkSystem system)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await system.Start(timeout.Token);
    }

    private static async Task StopWithTimeout(IWorkSystem system)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await system.Stop(cancellationToken: timeout.Token);
    }

    private static async Task<WorkCompletion> WaitForCompletion(IWorkerHandle handle)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await handle.WaitForCompletion(timeout.Token);
    }

    private static async Task WaitWithTimeout(Task task)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await task.WaitAsync(timeout.Token);
    }

    private static async Task<T> WaitWithTimeout<T>(Task<T> task)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await task.WaitAsync(timeout.Token);
    }

    private static async Task<T> Scalar<T>(SqlConnection connection, string commandText)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        var value = await command.ExecuteScalarAsync();
        if (value is null or DBNull)
        {
            throw new InvalidOperationException("Expected SQL scalar query to return a value.");
        }

        return (T)Convert.ChangeType(value, typeof(T));
    }

    private static async Task<T?> Scalar<T>(
        SqlConnection connection,
        string commandText,
        params SqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync();
        if (value is null or DBNull)
        {
            return default;
        }

        return (T?)Convert.ChangeType(value, Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T));
    }

    private static Task<int> CountRowsForSubject(SqlConnection connection, string subjectValue)
        => Scalar<int>(connection, $"""
SELECT COUNT(*)
FROM workable.WorkEntries
WHERE SubjectValue = N'{Escape(subjectValue)}';
""");

    private static Task<int> CountReadableRowsForSubject(SqlConnection connection, string subjectValue)
        => Scalar<int>(connection, $"""
SELECT COUNT(*)
FROM workable.WorkEntries WITH (READPAST)
WHERE SubjectValue = N'{Escape(subjectValue)}';
""");

    private static async Task<DateTimeOffset> ReadLeaseExpiry(SqlConnection connection, WorkerId workerId)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
SELECT LeaseExpiresAt
FROM workable.WorkQueueEntries
WHERE WorkerId = @WorkerId;
""";
        command.Parameters.AddWithValue("@WorkerId", workerId.Value);
        var value = await command.ExecuteScalarAsync();
        return value is DateTimeOffset dateTimeOffset
            ? dateTimeOffset
            : throw new InvalidOperationException("Expected durable row to have a lease expiry.");
    }

    private static async Task WaitForEntryCount(SqlConnection connection, string subjectValue, int expected)
        => await TestEventually.Until(
            async () => await CountRowsForSubject(connection, subjectValue) == expected,
            $"Expected SQL Server work entry count for subject '{subjectValue}' to become {expected}.");

    private static async Task<object> StartWorkflow(IWorkSystem system, string workflowName)
    {
        var runtime = system.GetType()
            .GetProperty("WorkflowRuntime", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(system)
            ?? throw new InvalidOperationException("Expected workflow runtime property.");
        var startTask = (Task)(runtime.GetType()
            .GetMethod(
                "Start",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(string), typeof(WorkRequestContext), typeof(CancellationToken)],
                modifiers: null)?
            .Invoke(
                runtime,
                [workflowName, WorkRequestContext.Create(WorkInvocationChannel.InProcess), CancellationToken.None])
            ?? throw new InvalidOperationException("Expected workflow start task."));
        await startTask;
        return startTask.GetType().GetProperty("Result")?.GetValue(startTask)
            ?? throw new InvalidOperationException("Expected workflow start handle.");
    }

    private static async Task<object> ExecuteWorkflowAction(
        IWorkSystem system,
        WorkflowRunId runId,
        string actionName)
    {
        var runtime = system.GetType()
            .GetProperty("WorkflowRuntime", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(system)
            ?? throw new InvalidOperationException("Expected workflow runtime property.");
        var executeMethod = runtime.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Single(method =>
            {
                if (!string.Equals(method.Name, "Execute", StringComparison.Ordinal))
                {
                    return false;
                }

                var parameters = method.GetParameters();
                return parameters.Length == 4 &&
                    parameters[0].ParameterType == typeof(WorkflowRunId) &&
                    parameters[2].ParameterType == typeof(WorkRequestContext) &&
                    parameters[3].ParameterType == typeof(CancellationToken);
            });
        var actionParameterType = executeMethod.GetParameters()[1].ParameterType;
        var action = Enum.Parse(actionParameterType, actionName, ignoreCase: false);
        var task = (Task)executeMethod.Invoke(
            runtime,
            [runId, action, WorkRequestContext.Create(WorkInvocationChannel.InProcess), CancellationToken.None])!;
        await task.WaitAsync(CancellationToken.None);
        return task.GetType().GetProperty("Result")?.GetValue(task)
            ?? throw new InvalidOperationException("Expected workflow action outcome.");
    }

    private static async Task<object> WaitForWorkflowCompletion(object handle)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waitTask = (Task)handle.GetType()
            .GetMethod("WaitForCompletion", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
            .Invoke(handle, [timeout.Token])!;
        await waitTask.WaitAsync(timeout.Token);
        return waitTask.GetType().GetProperty("Result")?.GetValue(waitTask)
            ?? throw new InvalidOperationException("Expected workflow completion result.");
    }

    private static bool IsWorkflowAccepted(object handle)
    {
        var startOutcome = handle.GetType().GetProperty("StartOutcome")?.GetValue(handle)
            ?? throw new InvalidOperationException("Expected workflow start outcome.");
        return (bool)(startOutcome.GetType().GetProperty("IsAccepted")?.GetValue(startOutcome) ?? false);
    }

    private static bool WorkflowActionAccepted(object outcome)
        => (bool)(outcome.GetType().GetProperty("IsAccepted")?.GetValue(outcome) ?? false);

    private static WorkflowRunId RequiredWorkflowRunId(object handle)
        => handle.GetType().GetProperty("RunId")?.GetValue(handle) is WorkflowRunId runId
            ? runId
            : throw new InvalidOperationException("Expected workflow run id.");

    private static string? WorkflowCompletionStatus(object completion)
        => completion.GetType().GetProperty("Status")?.GetValue(completion)?.ToString();

    private static string? WorkflowStatus(IWorkSystem system, WorkflowRunId runId)
    {
        var snapshot = WorkflowSnapshot(system, runId);
        return snapshot?.GetType().GetProperty("Status")?.GetValue(snapshot)?.ToString();
    }

    private static IReadOnlyList<WorkerId> WorkflowStepWorkerIds(
        IWorkSystem system,
        WorkflowRunId runId,
        string stepName)
    {
        var snapshot = WorkflowSnapshot(system, runId)
            ?? throw new InvalidOperationException("Expected workflow snapshot.");
        var steps = (System.Collections.IEnumerable)(snapshot.GetType().GetProperty("Steps")?.GetValue(snapshot)
            ?? throw new InvalidOperationException("Expected workflow steps."));
        var step = steps.Cast<object>().Single(candidate => string.Equals(
            candidate.GetType().GetProperty("Name")?.GetValue(candidate)?.ToString(),
            stepName,
            StringComparison.Ordinal));
        var workerIds = (System.Collections.IEnumerable)(step.GetType().GetProperty("WorkerIds")?.GetValue(step)
            ?? throw new InvalidOperationException("Expected workflow step worker ids."));
        return [.. workerIds.Cast<WorkerId>()];
    }

    private static object? WorkflowSnapshot(IWorkSystem system, WorkflowRunId runId)
    {
        var runtime = system.GetType()
            .GetProperty("WorkflowRuntime", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(system)
            ?? throw new InvalidOperationException("Expected workflow runtime property.");
        return runtime.GetType()
            .GetMethod("Get", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?
            .Invoke(runtime, [runId]);
    }

    private static WorkflowRunPersistenceRecord CreateWorkflowRun(
        string systemName,
        string definitionName)
    {
        var workerId = WorkerId.New();
        var definition = WorkflowDefinition.Create(definitionName);
        return new WorkflowRunPersistenceRecord(
            systemName,
            WorkflowRunId.New(),
            definition.Version,
            definitionName,
            WorkInput.FromValue(new WorkflowSqlInput("sql-input")),
            WorkRequestContext.Create(
                WorkInvocationChannel.InProcess,
                new WorkActor("workflow-sql-user", "Workflow SQL User"),
                isAuthenticated: true),
            WorkflowRunStatus.Running,
            [
                new WorkflowStepPersistenceRecord(
                    "dispatch",
                    WorkflowStepKind.DispatchWork,
                    WorkflowStepRunStatus.Completed,
                    [workerId],
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow,
                    []),
            ],
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null,
            [],
            [
                new WorkflowChildReceipt(
                    workerId,
                    "dispatch",
                    definitionName,
                    WorkerState.Completed,
                    DateTimeOffset.UtcNow,
                    [WorkMessage.Info("workflow.child.completed", "Child completed.")],
                    WorkOutput.Empty),
            ],
            "sql-test-workflow-fingerprint",
            "Stop",
            WorkRequestContext.Create(
                WorkInvocationChannel.HttpApi,
                new WorkActor("workflow-cancel-user", "Workflow Cancel User"),
                description: "Cancel for deployment",
                isAuthenticated: true));
    }

    private sealed record WorkflowSqlInput(string Value);

    private sealed class ForeignDurabilityTransaction : IWorkQueueDurabilityTransaction;

    private static T InvokeDurabilityHelper<T>(string methodName, object? argument)
        => (T)typeof(WorkableSqlServerQueueDurabilityStore)
            .GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [argument])!;

    private static SqlException CreateSqlException(int number, byte errorClass)
    {
        var errorConstructor = typeof(SqlError)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .OrderByDescending(constructor => constructor.GetParameters().Length)
            .First();
        var errorArguments = errorConstructor.GetParameters()
            .Select(parameter => parameter.Name switch
            {
                "infoNumber" or "number" => (object?)number,
                "errorState" or "state" => (byte)1,
                "errorClass" or "class" => errorClass,
                "server" => "coverage-server",
                "errorMessage" or "message" => "coverage failure",
                "procedure" => "coverage-procedure",
                "lineNumber" => 1,
                "win32ErrorCode" => Activator.CreateInstance(parameter.ParameterType),
                "exception" => null,
                _ => parameter.HasDefaultValue
                    ? parameter.DefaultValue
                    : parameter.ParameterType.IsValueType
                        ? Activator.CreateInstance(parameter.ParameterType)
                        : null,
            })
            .ToArray();
        var error = (SqlError)errorConstructor.Invoke(errorArguments);
        var errors = (SqlErrorCollection)Activator.CreateInstance(typeof(SqlErrorCollection), nonPublic: true)!;
        typeof(SqlErrorCollection)
            .GetMethod("Add", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(errors, [error]);
        var create = typeof(SqlException)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.Name == "CreateException")
            .First(method => method.GetParameters() is var parameters &&
                parameters.Length >= 2 &&
                parameters[0].ParameterType == typeof(SqlErrorCollection) &&
                parameters[1].ParameterType == typeof(string));
        var createArguments = create.GetParameters()
            .Select((parameter, index) => index switch
            {
                0 => (object?)errors,
                1 => "coverage",
                _ => parameter.HasDefaultValue
                    ? parameter.DefaultValue
                    : parameter.ParameterType.IsValueType
                        ? Activator.CreateInstance(parameter.ParameterType)
                        : null,
            })
            .ToArray();
        return (SqlException)create.Invoke(null, createArguments)!;
    }

    private static Task<T> InvokeSchemaScalar<T>(SqlConnection connection, string commandText)
        => (Task<T>)typeof(WorkableSqlServerSchema)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == "Scalar" && method.IsGenericMethodDefinition)
            .MakeGenericMethod(typeof(T))
            .Invoke(
                null,
                [connection, commandText, SchemaName, CancellationToken.None, null, null])!;

    private static object? InvokeGenericDurabilityHelper(string methodName, Type typeArgument, object? argument)
        => typeof(WorkableSqlServerQueueDurabilityStore)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == methodName && method.IsGenericMethodDefinition)
            .MakeGenericMethod(typeArgument)
            .Invoke(null, [argument]);

    private static string SerializePrivatePayload(string methodName, object argument)
    {
        var payload = typeof(WorkableSqlServerQueueDurabilityStore)
            .GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [argument])!;
        return JsonSerializer.Serialize(payload, payload.GetType(), DurableJsonOptions);
    }

    private static object? InvokeGenericDurabilityReader(
        string methodName,
        Type typeArgument,
        DbDataReader reader,
        int ordinal)
        => typeof(WorkableSqlServerQueueDurabilityStore)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method => method.Name == methodName && method.IsGenericMethodDefinition)
            .MakeGenericMethod(typeArgument)
            .Invoke(null, [reader, ordinal]);

    private static object? InvokeDurabilityReader(string methodName, DbDataReader reader, int ordinal)
        => typeof(WorkableSqlServerQueueDurabilityStore)
            .GetMethod(methodName, BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [reader, ordinal]);

    private static DataTableReader CreateStringReader(object value)
    {
        using var table = new DataTable();
        table.Columns.Add("Payload", typeof(string));
        table.Rows.Add(value);
        return table.CreateDataReader();
    }

    private static WorkQueueDurabilityEnqueueRequest CreateDurableEnqueueRequest(
        WorkSystemId systemId,
        string systemName,
        WorkerId workerId,
        string definitionName,
        string subjectValue,
        IWorkQueueDurabilityTransaction? transaction,
        bool enableIdempotency = true,
        WorkerOptions? options = null)
        => new(
            systemId,
            systemName,
            workerId,
            WorkDefinition.Create(definitionName),
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", subjectValue)),
            options ?? WorkerOptions.Default,
            DurablePersistenceConcurrencyConfiguration(enableIdempotency: enableIdempotency),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            DateTimeOffset.UtcNow,
            enableIdempotency ? new WorkQueueDurabilityIdempotency(new WorkSubjectId("order", subjectValue)) : null,
            transaction);

    private static WorkQueueDurabilityEnqueueRequest CreateNonConcurrentDurableEnqueueRequest(
        WorkSystemId systemId,
        WorkerId workerId,
        string definitionName,
        string subjectValue)
        => new(
            systemId,
            "default",
            workerId,
            WorkDefinition.Create(definitionName),
            WorkInput.Empty.WithSubject(new WorkSubjectId("order", subjectValue)),
            WorkerOptions.Default,
            WorkConfiguration.Default with
            {
                Coordination = WorkCoordinationConfiguration.Default with
                {
                    IsEnabled = true,
                    Storage = WorkCoordinationStorage.Persistent,
                    Durability = new WorkQueueDurabilityConfiguration
                    {
                        IsEnabled = true,
                    },
                },
            },
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            DateTimeOffset.UtcNow,
            Idempotency: null,
            Transaction: null);

    private static async Task WaitForFailedEntryRetained(SqlConnection connection, string subjectValue)
        => await TestEventually.Until(
            async () => await CountFailedRetainedRowsForSubject(connection, subjectValue) == 1,
            $"Expected failed SQL Server work entry for subject '{subjectValue}' to be retained.");

    private static IEnumerable<WorkProfileSnapshotNode> Flatten(WorkProfileSnapshotNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    private static async Task ExpireLease(SqlConnection connection, string subjectValue)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
UPDATE queue
SET LeaseExpiresAt = DATEADD(second, -1, SYSDATETIMEOFFSET())
FROM workable.WorkQueueEntries queue
INNER JOIN workable.WorkEntries entries
    ON entries.WorkerId = queue.WorkerId
WHERE entries.SubjectValue = N'{Escape(subjectValue)}';
""";
        await command.ExecuteNonQueryAsync();
    }

    private static WorkerId RequiredWorkerId(IWorkerHandle handle)
        => handle.WorkerId ?? throw new InvalidOperationException("Expected worker id.");

    private static async Task WaitForWorkerState(IWorkSystem system, WorkerId workerId, WorkerState state)
        => await TestEventually.Until(
            async () => (await system.Query.Worker(workerId))?.State == state,
            $"Expected worker '{workerId.Value:D}' to reach state '{state}'.");

    private static Task<int> CountFailedRetainedRowsForSubject(SqlConnection connection, string subjectValue)
        => Scalar<int>(connection, $"""
SELECT COUNT(*)
FROM workable.WorkEntries entries
INNER JOIN workable.WorkQueueEntries queue
    ON queue.WorkerId = entries.WorkerId
WHERE entries.SubjectValue = N'{Escape(subjectValue)}'
  AND entries.FailedAt IS NOT NULL
  AND queue.Disposition = N'Failed';
""");

    private static string Quote(string identifier)
        => $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";

    private static string Escape(string value)
        => value.Replace("'", "''", StringComparison.Ordinal);

    private sealed class TestHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Workable.SqlServer.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
