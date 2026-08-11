using System.Linq;
using Microsoft.Data.SqlClient;

namespace Workable.SqlServer;

public static class WorkableSqlServerSchema
{
    private const int SchemaVersion = 4;
    private const int WorkflowSchemaVersion = 4;
    private const int ExecutionDiagnosticsSchemaVersion = 7;
    private const string QueueDurabilityComponent = "QueueDurability";
    private const string WorkflowPersistenceComponent = "WorkflowPersistence";
    private const string ExecutionDiagnosticsComponent = "ExecutionDiagnostics";
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
        var diagnosticsTable = $"{schema}.[WorkIterationDiagnostics]";
        var diagnosticLogsTable = $"{schema}.[WorkIterationDiagnosticLogs]";
        var instrumentationTable = $"{schema}.[WorkIterationInstrumentation]";
        var captureRulesTable = $"{schema}.[WorkDiagnosticCaptureRules]";
        var versionTable = $"{schema}.[SchemaVersion]";
        var escapedSchemaName = EscapeLiteral(schemaName);
        var dynamicSchema = EscapeLiteral(schema);
        var dynamicEntriesTable = EscapeLiteral(entriesTable);
        var dynamicQueueTable = EscapeLiteral(queueTable);
        var dynamicWorkflowRunsTable = EscapeLiteral(workflowRunsTable);
        var dynamicDiagnosticsTable = EscapeLiteral(diagnosticsTable);
        var dynamicCaptureRulesTable = EscapeLiteral(captureRulesTable);

        return
        [
            RequiredSetOptions,
            $"IF SCHEMA_ID(N'{escapedSchemaName}') IS NULL EXEC(N'CREATE SCHEMA {dynamicSchema}')",
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
        HasIdempotencyReservation bit NOT NULL CONSTRAINT DF_WorkableWorkEntries_HasIdempotencyReservation DEFAULT (0),
        SubjectType nvarchar(256) NULL,
        SubjectValue nvarchar(450) NULL,
        InputJson nvarchar(max) NULL,
        OptionsJson nvarchar(max) NULL,
        ConfigurationJson nvarchar(max) NULL,
        OriginJson nvarchar(max) NOT NULL,
        WorkflowProvenanceJson nvarchar(max) NULL,
        CreatedAt datetimeoffset NOT NULL,
        FailedAt datetimeoffset NULL,
        FailureMessagesJson nvarchar(max) NULL
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
        Disposition nvarchar(32) NOT NULL CONSTRAINT DF_WorkableWorkQueueEntries_Disposition DEFAULT (N'Ready'),
        HasPersistentConcurrency bit NOT NULL CONSTRAINT DF_WorkableWorkQueueEntries_HasPersistentConcurrency DEFAULT (0),
        ConcurrencyScope nvarchar(64) NULL,
        ConcurrencyMaximumCapacity int NULL,
        SubjectType nvarchar(256) NULL,
        SubjectValue nvarchar(450) NULL,
        ConcurrencyType nvarchar(256) NULL,
        ConcurrencyValue nvarchar(450) NULL,
        CreatedAt datetimeoffset NOT NULL,
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
        WorkflowInputJson nvarchar(max) NULL,
        Status nvarchar(64) NOT NULL,
        StepsJson nvarchar(max) NOT NULL,
        MessagesJson nvarchar(max) NOT NULL,
        ChildReceiptsJson nvarchar(max) NOT NULL CONSTRAINT DF_WorkableWorkflowRuns_ChildReceiptsJson DEFAULT (N'[]'),
        PendingControlAction nvarchar(32) NULL,
        PendingControlRequestContextJson nvarchar(max) NULL,
        CreatedAt datetimeoffset NOT NULL,
        StartedAt datetimeoffset NULL,
        CompletedAt datetimeoffset NULL
    );
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationDiagnostics', N'U') IS NULL
BEGIN
    CREATE TABLE {diagnosticsTable}
    (
        DiagnosticId uniqueidentifier NOT NULL CONSTRAINT PK_WorkableWorkIterationDiagnostics PRIMARY KEY,
        PersistenceScope nvarchar(450) NOT NULL,
        WorkSystemId uniqueidentifier NOT NULL,
        WorkSystemName nvarchar(256) NOT NULL,
        WorkerId uniqueidentifier NOT NULL,
        IterationSequence bigint NOT NULL,
        DefinitionId uniqueidentifier NOT NULL,
        DefinitionName nvarchar(450) NOT NULL,
        Status nvarchar(64) NULL,
        AttemptCount int NULL,
        StartedAt datetimeoffset NOT NULL,
        CompletedAt datetimeoffset NULL,
        DurationMilliseconds bigint NULL,
        CaptureSource nvarchar(64) NOT NULL,
        ProfileCaptureMode nvarchar(32) NULL,
        SqlClientProfilingAvailable bit NOT NULL,
        HttpClientProfilingAvailable bit NOT NULL,
        ProfileJson nvarchar(max) NULL,
        PayloadSchemaVersion int NOT NULL CONSTRAINT DF_WorkableWorkIterationDiagnostics_PayloadSchemaVersion DEFAULT (1),
        ProfileNodeCount int NOT NULL CONSTRAINT DF_WorkableWorkIterationDiagnostics_ProfileNodeCount DEFAULT (0),
        ProfileDropped bit NOT NULL CONSTRAINT DF_WorkableWorkIterationDiagnostics_ProfileDropped DEFAULT (0),
        PersistedLogCount bigint NOT NULL CONSTRAINT DF_WorkableWorkIterationDiagnostics_PersistedLogCount DEFAULT (0),
        DroppedLogCount bigint NOT NULL CONSTRAINT DF_WorkableWorkIterationDiagnostics_DroppedLogCount DEFAULT (0),
        RetentionSeconds int NOT NULL,
        CapturedAt datetimeoffset NOT NULL,
        UpdatedAt datetimeoffset NOT NULL,
        ExpiresAt datetimeoffset NULL,
        CONSTRAINT UX_WorkableWorkIterationDiagnostics_Iteration UNIQUE
            (PersistenceScope, WorkSystemName, WorkerId, IterationSequence)
    );
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationDiagnosticLogs', N'U') IS NULL
BEGIN
    CREATE TABLE {diagnosticLogsTable}
    (
        DiagnosticId uniqueidentifier NOT NULL,
        Ordinal bigint NOT NULL,
        OccurredAt datetimeoffset NOT NULL,
        Level nvarchar(16) NOT NULL,
        Category nvarchar(512) NOT NULL,
        EventId int NOT NULL,
        EventName nvarchar(256) NULL,
        Message nvarchar(max) NOT NULL,
        PropertiesJson nvarchar(max) NULL,
        ExceptionType nvarchar(512) NULL,
        ExceptionMessage nvarchar(max) NULL,
        ExceptionStack nvarchar(max) NULL,
        TraceId nvarchar(32) NULL,
        SpanId nvarchar(16) NULL,
        CONSTRAINT PK_WorkableWorkIterationDiagnosticLogs PRIMARY KEY (DiagnosticId, Ordinal),
        CONSTRAINT FK_WorkableWorkIterationDiagnosticLogs_Diagnostics FOREIGN KEY (DiagnosticId)
            REFERENCES {diagnosticsTable}(DiagnosticId) ON DELETE CASCADE
    );
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationInstrumentation', N'U') IS NULL
BEGIN
    CREATE TABLE {instrumentationTable}
    (
        DiagnosticId uniqueidentifier NOT NULL,
        Instrumentation nvarchar(128) NOT NULL,
        NodeCount int NOT NULL,
        TimingCount int NOT NULL,
        TotalTimingMilliseconds bigint NOT NULL,
        MaximumTimingMilliseconds bigint NOT NULL,
        OmittedNodeCount int NOT NULL,
        CONSTRAINT PK_WorkableWorkIterationInstrumentation PRIMARY KEY (DiagnosticId, Instrumentation),
        CONSTRAINT FK_WorkableWorkIterationInstrumentation_Diagnostics FOREIGN KEY (DiagnosticId)
            REFERENCES {diagnosticsTable}(DiagnosticId) ON DELETE CASCADE
    );
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkDiagnosticCaptureRules', N'U') IS NULL
BEGIN
    CREATE TABLE {captureRulesTable}
    (
        RuleId uniqueidentifier NOT NULL CONSTRAINT PK_WorkableWorkDiagnosticCaptureRules PRIMARY KEY,
        PersistenceScope nvarchar(450) NOT NULL,
        WorkSystemName nvarchar(256) NOT NULL,
        DefinitionName nvarchar(450) NULL,
        MinimumLogLevel nvarchar(16) NOT NULL,
        ProfileCaptureMode nvarchar(32) NULL,
        ArtifactRetentionSeconds int NOT NULL,
        CreatedAt datetimeoffset NOT NULL,
        ActiveUntil datetimeoffset NOT NULL,
        CreatedByJson nvarchar(max) NOT NULL
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
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkIterationDiagnostics', N'SqlClientProfilingAvailable') IS NULL
BEGIN
    ALTER TABLE {diagnosticsTable}
        ADD SqlClientProfilingAvailable bit NOT NULL
            CONSTRAINT DF_WorkableWorkIterationDiagnostics_SqlClientProfilingAvailable DEFAULT (0);
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND EXISTS (
       SELECT 1 FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       INNER JOIN sys.index_columns index_columns
           ON index_columns.object_id = indexes.object_id
          AND index_columns.index_id = indexes.index_id
          AND index_columns.key_ordinal = 1
       INNER JOIN sys.columns columns
           ON columns.object_id = index_columns.object_id
          AND columns.column_id = index_columns.column_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkIterationDiagnostics'
         AND indexes.name = N'IX_WorkableWorkIterationDiagnostics_RecentWork'
         AND columns.name = N'PersistenceScope')
BEGIN
    DROP INDEX IX_WorkableWorkIterationDiagnostics_RecentWork ON {diagnosticsTable};
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkIterationDiagnostics'
         AND indexes.name = N'IX_WorkableWorkIterationDiagnostics_RecentSystem')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkIterationDiagnostics_RecentSystem ON {dynamicDiagnosticsTable} (PersistenceScope, WorkSystemName, CompletedAt DESC, DiagnosticId) INCLUDE (DefinitionName, WorkerId, IterationSequence, Status, ExpiresAt);');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkDiagnosticCaptureRules', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkDiagnosticCaptureRules'
         AND indexes.name = N'IX_WorkableWorkDiagnosticCaptureRules_System')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkDiagnosticCaptureRules_System ON {dynamicCaptureRulesTable} (PersistenceScope, WorkSystemName, ActiveUntil, RuleId) INCLUDE (DefinitionName, CreatedAt);');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkIterationDiagnostics', N'HttpClientProfilingAvailable') IS NULL
BEGIN
    ALTER TABLE {diagnosticsTable}
        ADD HttpClientProfilingAvailable bit NOT NULL
            CONSTRAINT DF_WorkableWorkIterationDiagnostics_HttpClientProfilingAvailable DEFAULT (0);
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkIterationDiagnostics', N'ProfileDropped') IS NULL
BEGIN
    ALTER TABLE {diagnosticsTable}
        ADD ProfileDropped bit NOT NULL
            CONSTRAINT DF_WorkableWorkIterationDiagnostics_ProfileDropped DEFAULT (0);
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkIterationDiagnostics'
         AND indexes.name = N'IX_WorkableWorkIterationDiagnostics_RecentWork')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkIterationDiagnostics_RecentWork ON {dynamicDiagnosticsTable} (WorkSystemName, DefinitionName, CompletedAt DESC, DiagnosticId) INCLUDE (PersistenceScope, WorkerId, IterationSequence, Status, ExpiresAt);');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkIterationDiagnostics'
         AND indexes.name = N'IX_WorkableWorkIterationDiagnostics_Worker')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkIterationDiagnostics_Worker ON {dynamicDiagnosticsTable} (PersistenceScope, WorkSystemName, WorkerId, IterationSequence) INCLUDE (CompletedAt, ExpiresAt);');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkIterationDiagnostics'
         AND indexes.name = N'IX_WorkableWorkIterationDiagnostics_ExpirationByScope')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkIterationDiagnostics_ExpirationByScope ON {dynamicDiagnosticsTable} (PersistenceScope, ExpiresAt, DiagnosticId) WHERE ExpiresAt IS NOT NULL;');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkIterationDiagnostics'
         AND indexes.name = N'IX_WorkableWorkIterationDiagnostics_IncompleteByScope')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkIterationDiagnostics_IncompleteByScope ON {dynamicDiagnosticsTable} (PersistenceScope, UpdatedAt, DiagnosticId) INCLUDE (RetentionSeconds) WHERE CompletedAt IS NULL;');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkflowRuns', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkflowRuns', N'WorkflowInputJson') IS NULL
BEGIN
    ALTER TABLE {workflowRunsTable}
        ADD WorkflowInputJson nvarchar(max) NULL;
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
   AND COL_LENGTH(N'{escapedSchemaName}.WorkflowRuns', N'PendingControlRequestContextJson') IS NULL
BEGIN
    ALTER TABLE {workflowRunsTable}
        ADD PendingControlRequestContextJson nvarchar(max) NULL;
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkflowRuns', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkflowRuns', N'ChildReceiptsJson') IS NULL
BEGIN
    ALTER TABLE {workflowRunsTable}
        ADD ChildReceiptsJson nvarchar(max) NOT NULL
            CONSTRAINT DF_WorkableWorkflowRuns_ChildReceiptsJson DEFAULT (N'[]');
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
            ..CreateQueueDurabilityVersion4Batches(
                escapedSchemaName,
                entriesTable,
                queueTable,
                workflowRunsTable),
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
    EXEC(N'CREATE UNIQUE INDEX UX_WorkableWorkEntries_Idempotency ON {dynamicEntriesTable} (WorkSystemName, DefinitionName, SubjectType, SubjectValue) WHERE HasIdempotencyReservation = 1 AND SubjectType IS NOT NULL AND SubjectValue IS NOT NULL;');
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
    EXEC(N'CREATE INDEX IX_WorkableWorkQueueEntries_Ready ON {dynamicQueueTable} (WorkSystemName, DefinitionName, CreatedAt, WorkerId) INCLUDE (LeaseExpiresAt, HasPersistentConcurrency) WHERE Disposition = N''Ready'';');
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
    EXEC(N'CREATE INDEX IX_WorkableWorkQueueEntries_PersistentConcurrencyReady ON {dynamicQueueTable} (WorkSystemName, DefinitionName, CreatedAt, WorkerId) INCLUDE (LeaseExpiresAt, HasPersistentConcurrency, ConcurrencyScope, ConcurrencyMaximumCapacity, SubjectType, SubjectValue, ConcurrencyType, ConcurrencyValue) WHERE Disposition = N''Ready'' AND HasPersistentConcurrency = 1;');
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
    EXEC(N'CREATE INDEX IX_WorkableWorkQueueEntries_Concurrency ON {dynamicQueueTable} (WorkSystemName, DefinitionName, ConcurrencyBucket, LeaseExpiresAt, SubjectType, SubjectValue, ConcurrencyType, ConcurrencyValue) WHERE ConcurrencyBucket IS NOT NULL;');
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
    EXEC(N'CREATE INDEX IX_WorkableWorkflowRuns_Recovery ON {dynamicWorkflowRunsTable} (PersistenceScope, Status, CreatedAt, RunId);');
END
""",
            CreateVersionUpsertBatch(versionTable, "QueueDurability", SchemaVersion),
            CreateVersionUpsertBatch(versionTable, "WorkflowPersistence", WorkflowSchemaVersion),
            CreateVersionUpsertBatch(versionTable, "ExecutionDiagnostics", ExecutionDiagnosticsSchemaVersion),
        ];
    }

    private static IReadOnlyList<string> CreateQueueDurabilityVersion4Batches(
        string escapedSchemaName,
        string entriesTable,
        string queueTable,
        string workflowRunsTable)
        =>
        [
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkEntries', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkEntries', N'FailedAt') IS NULL
BEGIN
    ALTER TABLE {entriesTable} ADD FailedAt datetimeoffset NULL;
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkEntries', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkEntries', N'FailureMessagesJson') IS NULL
BEGIN
    ALTER TABLE {entriesTable} ADD FailureMessagesJson nvarchar(max) NULL;
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkEntries', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkEntries', N'WorkflowProvenanceJson') IS NULL
BEGIN
    ALTER TABLE {entriesTable} ADD WorkflowProvenanceJson nvarchar(max) NULL;
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkQueueEntries', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkQueueEntries', N'Disposition') IS NULL
BEGIN
    ALTER TABLE {queueTable}
        ADD Disposition nvarchar(32) NOT NULL
            CONSTRAINT DF_WorkableWorkQueueEntries_Disposition DEFAULT (N'Ready');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkQueueEntries', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkQueueEntries', N'Disposition') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkQueueEntries'
         AND indexes.name = N'IX_WorkableWorkQueueEntries_Failed')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkQueueEntries_Failed ON {EscapeLiteral(queueTable)} (WorkSystemName, LeaseExpiresAt, CreatedAt, WorkerId) WHERE Disposition = N''Failed'';');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkQueueEntries', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkQueueEntries', N'Disposition') IS NOT NULL
BEGIN
    DROP INDEX IF EXISTS IX_WorkableWorkQueueEntries_Ready ON {queueTable};
    EXEC(N'CREATE INDEX IX_WorkableWorkQueueEntries_Ready ON {EscapeLiteral(queueTable)} (WorkSystemName, DefinitionName, CreatedAt, WorkerId) INCLUDE (LeaseExpiresAt, HasPersistentConcurrency) WHERE Disposition = N''Ready'';');

    DROP INDEX IF EXISTS IX_WorkableWorkQueueEntries_PersistentConcurrencyReady ON {queueTable};
    EXEC(N'CREATE INDEX IX_WorkableWorkQueueEntries_PersistentConcurrencyReady ON {EscapeLiteral(queueTable)} (WorkSystemName, DefinitionName, CreatedAt, WorkerId) INCLUDE (LeaseExpiresAt, HasPersistentConcurrency, ConcurrencyScope, ConcurrencyMaximumCapacity, SubjectType, SubjectValue, ConcurrencyType, ConcurrencyValue) WHERE Disposition = N''Ready'' AND HasPersistentConcurrency = 1;');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkEntries', N'U') IS NOT NULL
BEGIN
    DROP INDEX IF EXISTS IX_WorkableWorkEntries_Ready ON {entriesTable};
    DROP INDEX IF EXISTS IX_WorkableWorkEntries_PersistentConcurrencyReady ON {entriesTable};
    DROP INDEX IF EXISTS IX_WorkableWorkEntries_Concurrency ON {entriesTable};

    DECLARE @DefaultConstraints nvarchar(max);
    SELECT @DefaultConstraints = STRING_AGG(QUOTENAME(defaults.name), N', ')
    FROM sys.default_constraints defaults
    INNER JOIN sys.columns columns
        ON columns.object_id = defaults.parent_object_id
       AND columns.column_id = defaults.parent_column_id
    WHERE defaults.parent_object_id = OBJECT_ID(N'{escapedSchemaName}.WorkEntries')
      AND columns.name IN (N'IsDurableQueued', N'HasPersistentConcurrency');

    IF @DefaultConstraints IS NOT NULL
        EXEC(N'ALTER TABLE {EscapeLiteral(entriesTable)} DROP CONSTRAINT ' + @DefaultConstraints + N';');

    DECLARE @DeadColumns nvarchar(max);
    SELECT @DeadColumns = STRING_AGG(QUOTENAME(columns.name), N', ')
    FROM sys.columns columns
    WHERE columns.object_id = OBJECT_ID(N'{escapedSchemaName}.WorkEntries')
      AND columns.name IN
      (
          N'IsDurableQueued',
          N'HasPersistentConcurrency',
          N'ConcurrencyType',
          N'ConcurrencyValue',
          N'ClaimedBy',
          N'ClaimedAt',
          N'LeaseId',
          N'LeaseExpiresAt',
          N'ConcurrencyBucket'
      );

    IF @DeadColumns IS NOT NULL
        EXEC(N'ALTER TABLE {EscapeLiteral(entriesTable)} DROP COLUMN ' + @DeadColumns + N';');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkQueueEntries', N'U') IS NOT NULL
BEGIN
    DECLARE @DeadQueueColumns nvarchar(max);
    SELECT @DeadQueueColumns = STRING_AGG(QUOTENAME(columns.name), N', ')
    FROM sys.columns columns
    WHERE columns.object_id = OBJECT_ID(N'{escapedSchemaName}.WorkQueueEntries')
      AND columns.name IN (N'ClaimedBy', N'ClaimedAt');

    IF @DeadQueueColumns IS NOT NULL
        EXEC(N'ALTER TABLE {EscapeLiteral(queueTable)} DROP COLUMN ' + @DeadQueueColumns + N';');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkflowRuns', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkflowRuns', N'UpdatedAt') IS NOT NULL
BEGIN
    ALTER TABLE {workflowRunsTable} DROP COLUMN UpdatedAt;
END
""",
        ];

    private static IReadOnlyList<string> CreateExecutionDiagnosticsVersion7Batches(
        string escapedSchemaName,
        string diagnosticsTable,
        string captureRulesTable)
        =>
        [
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkIterationDiagnostics', N'SqlClientProfilingAvailable') IS NULL
BEGIN
    ALTER TABLE {diagnosticsTable}
        ADD SqlClientProfilingAvailable bit NOT NULL
            CONSTRAINT DF_WorkableWorkIterationDiagnostics_SqlClientProfilingAvailable DEFAULT (0);
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND EXISTS (
       SELECT 1 FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       INNER JOIN sys.index_columns index_columns
           ON index_columns.object_id = indexes.object_id
          AND index_columns.index_id = indexes.index_id
          AND index_columns.key_ordinal = 1
       INNER JOIN sys.columns columns
           ON columns.object_id = index_columns.object_id
          AND columns.column_id = index_columns.column_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkIterationDiagnostics'
         AND indexes.name = N'IX_WorkableWorkIterationDiagnostics_RecentWork'
         AND columns.name = N'PersistenceScope')
BEGIN
    DROP INDEX IX_WorkableWorkIterationDiagnostics_RecentWork ON {diagnosticsTable};
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkIterationDiagnostics'
         AND indexes.name = N'IX_WorkableWorkIterationDiagnostics_RecentSystem')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkIterationDiagnostics_RecentSystem ON {EscapeLiteral(diagnosticsTable)} (PersistenceScope, WorkSystemName, CompletedAt DESC, DiagnosticId) INCLUDE (DefinitionName, WorkerId, IterationSequence, Status, ExpiresAt);');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkDiagnosticCaptureRules', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkDiagnosticCaptureRules'
         AND indexes.name = N'IX_WorkableWorkDiagnosticCaptureRules_System')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkDiagnosticCaptureRules_System ON {EscapeLiteral(captureRulesTable)} (PersistenceScope, WorkSystemName, ActiveUntil, RuleId) INCLUDE (DefinitionName, CreatedAt);');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkIterationDiagnostics', N'HttpClientProfilingAvailable') IS NULL
BEGIN
    ALTER TABLE {diagnosticsTable}
        ADD HttpClientProfilingAvailable bit NOT NULL
            CONSTRAINT DF_WorkableWorkIterationDiagnostics_HttpClientProfilingAvailable DEFAULT (0);
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND COL_LENGTH(N'{escapedSchemaName}.WorkIterationDiagnostics', N'ProfileDropped') IS NULL
BEGIN
    ALTER TABLE {diagnosticsTable}
        ADD ProfileDropped bit NOT NULL
            CONSTRAINT DF_WorkableWorkIterationDiagnostics_ProfileDropped DEFAULT (0);
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkIterationDiagnostics'
         AND indexes.name = N'IX_WorkableWorkIterationDiagnostics_RecentWork')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkIterationDiagnostics_RecentWork ON {EscapeLiteral(diagnosticsTable)} (WorkSystemName, DefinitionName, CompletedAt DESC, DiagnosticId) INCLUDE (PersistenceScope, WorkerId, IterationSequence, Status, ExpiresAt);');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkIterationDiagnostics'
         AND indexes.name = N'IX_WorkableWorkIterationDiagnostics_Worker')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkIterationDiagnostics_Worker ON {EscapeLiteral(diagnosticsTable)} (PersistenceScope, WorkSystemName, WorkerId, IterationSequence) INCLUDE (CompletedAt, ExpiresAt);');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkIterationDiagnostics'
         AND indexes.name = N'IX_WorkableWorkIterationDiagnostics_ExpirationByScope')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkIterationDiagnostics_ExpirationByScope ON {EscapeLiteral(diagnosticsTable)} (PersistenceScope, ExpiresAt, DiagnosticId) WHERE ExpiresAt IS NOT NULL;');
END
""",
            $"""
IF OBJECT_ID(N'{escapedSchemaName}.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'{escapedSchemaName}'
         AND tables.name = N'WorkIterationDiagnostics'
         AND indexes.name = N'IX_WorkableWorkIterationDiagnostics_IncompleteByScope')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkIterationDiagnostics_IncompleteByScope ON {EscapeLiteral(diagnosticsTable)} (PersistenceScope, UpdatedAt, DiagnosticId) INCLUDE (RetentionSeconds) WHERE CompletedAt IS NULL;');
END
""",
        ];

    private static string CreateVersionUpsertBatch(string versionTable, string component, int version)
        => $"""
MERGE {versionTable} WITH (HOLDLOCK) AS target
USING (SELECT N'{component}' AS Component, {version} AS Version) AS source
ON target.Component = source.Component
WHEN MATCHED THEN UPDATE SET Version = source.Version, UpdatedAt = SYSDATETIMEOFFSET()
WHEN NOT MATCHED THEN INSERT (Component, Version, UpdatedAt) VALUES (source.Component, source.Version, SYSDATETIMEOFFSET());
""";

    public static async Task Apply(
        string connectionString,
        string schemaName = "workable",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);

        var escapedSchemaName = EscapeLiteral(schemaName);
        var schemaState = await ReadSchemaState(
            connection,
            transaction: null,
            schemaName,
            escapedSchemaName,
            cancellationToken);
        if (IsCurrent(schemaState.Versions))
        {
            return;
        }

        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await AcquireSchemaDeploymentLock(connection, transaction, schemaName, cancellationToken);
        schemaState = await ReadSchemaState(
            connection,
            transaction,
            schemaName,
            escapedSchemaName,
            cancellationToken);
        if (IsCurrent(schemaState.Versions))
        {
            await transaction.CommitAsync(cancellationToken);
            return;
        }

        if (schemaState.Versions.Count == 0)
        {
            if (schemaState.KnownObjectCount != 0)
            {
                throw new InvalidOperationException(
                    $"Workable SQL Server schema '{schemaName}' contains Workable tables but has no schema version metadata. " +
                    "Refusing to treat an unversioned or partially deployed schema as a fresh installation.");
            }

            foreach (var batch in CreateBatches(schemaName))
            {
                await ExecuteBatch(connection, transaction, batch, cancellationToken);
            }
        }
        else
        {
            var migrations = CreateMigrationPlan(schemaState.Versions, schemaName, escapedSchemaName);
            await ExecuteBatch(connection, transaction, RequiredSetOptions, cancellationToken);
            var versionTable = $"{QuoteIdentifier(schemaName)}.[SchemaVersion]";
            foreach (var migration in migrations)
            {
                foreach (var batch in migration.Batches)
                {
                    await ExecuteBatch(connection, transaction, batch, cancellationToken);
                }

                await ExecuteBatch(
                    connection,
                    transaction,
                    CreateVersionUpsertBatch(versionTable, migration.Component, migration.ToVersion),
                    cancellationToken);
            }
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static bool IsCurrent(IReadOnlyDictionary<string, int> installedVersions)
        => InstalledVersion(installedVersions, QueueDurabilityComponent) >= SchemaVersion &&
            InstalledVersion(installedVersions, WorkflowPersistenceComponent) >= WorkflowSchemaVersion &&
            InstalledVersion(installedVersions, ExecutionDiagnosticsComponent) >= ExecutionDiagnosticsSchemaVersion;

    private static int InstalledVersion(IReadOnlyDictionary<string, int> installedVersions, string component)
        => installedVersions.TryGetValue(component, out var version) ? version : 0;

    private static IReadOnlyList<SchemaMigration> CreateMigrationPlan(
        IReadOnlyDictionary<string, int> installedVersions,
        string schemaName,
        string escapedSchemaName)
    {
        var schema = QuoteIdentifier(schemaName);
        var availableMigrations = new[]
        {
            new SchemaMigration(
                QueueDurabilityComponent,
                FromVersion: 3,
                ToVersion: 4,
                CreateQueueDurabilityVersion4Batches(
                    escapedSchemaName,
                    $"{schema}.[WorkEntries]",
                    $"{schema}.[WorkQueueEntries]",
                    $"{schema}.[WorkflowRuns]")),
            new SchemaMigration(
                ExecutionDiagnosticsComponent,
                FromVersion: 6,
                ToVersion: 7,
                CreateExecutionDiagnosticsVersion7Batches(
                    escapedSchemaName,
                    $"{schema}.[WorkIterationDiagnostics]",
                    $"{schema}.[WorkDiagnosticCaptureRules]")),
        };
        var currentVersions = new[]
        {
            new KeyValuePair<string, int>(QueueDurabilityComponent, SchemaVersion),
            new KeyValuePair<string, int>(WorkflowPersistenceComponent, WorkflowSchemaVersion),
            new KeyValuePair<string, int>(ExecutionDiagnosticsComponent, ExecutionDiagnosticsSchemaVersion),
        };
        var plan = new List<SchemaMigration>();
        foreach (var (component, currentVersion) in currentVersions)
        {
            if (!installedVersions.TryGetValue(component, out var installedVersion))
            {
                throw new InvalidOperationException(
                    $"Workable SQL Server schema '{schemaName}' is versioned but has no '{component}' version row.");
            }

            while (installedVersion < currentVersion)
            {
                var migration = availableMigrations.SingleOrDefault(candidate =>
                    candidate.Component == component &&
                    candidate.FromVersion == installedVersion);
                if (migration is null)
                {
                    throw new InvalidOperationException(
                        $"Workable SQL Server schema '{schemaName}' cannot upgrade component '{component}' " +
                        $"from version {installedVersion} to version {currentVersion} because no ordered migration is available.");
                }

                plan.Add(migration);
                installedVersion = migration.ToVersion;
            }
        }

        return plan;
    }

    private static async Task<InstalledSchemaState> ReadSchemaState(
        SqlConnection connection,
        SqlTransaction? transaction,
        string schemaName,
        string escapedSchemaName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
IF OBJECT_ID(N'{escapedSchemaName}.SchemaVersion', N'U') IS NULL
BEGIN
    SELECT CAST(NULL AS nvarchar(128)) AS Component, CAST(NULL AS int) AS Version WHERE 1 = 0;
END
ELSE
BEGIN
    EXEC(N'SELECT Component, Version FROM {EscapeLiteral(QuoteIdentifier(schemaName))}.[SchemaVersion];');
END
""";
        command.CommandText += """

SELECT COUNT(*)
FROM sys.objects objects
INNER JOIN sys.schemas schemas ON schemas.schema_id = objects.schema_id
WHERE schemas.name = @SchemaName
  AND objects.name IN
  (
      N'SchemaVersion',
      N'WorkEntries',
      N'WorkQueueEntries',
      N'WorkflowRuns',
      N'WorkIterationDiagnostics',
      N'WorkIterationDiagnosticLogs',
      N'WorkIterationInstrumentation',
      N'WorkDiagnosticCaptureRules'
  );
""";
        command.Parameters.AddWithValue("@SchemaName", schemaName);

        var versions = new Dictionary<string, int>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            versions[reader.GetString(0)] = reader.GetInt32(1);
        }

        if (!await reader.NextResultAsync(cancellationToken) ||
            !await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException("Could not inspect the installed Workable SQL Server schema state.");
        }

        return new InstalledSchemaState(versions, reader.GetInt32(0));
    }

    private static async Task AcquireSchemaDeploymentLock(
        SqlConnection connection,
        SqlTransaction transaction,
        string schemaName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
DECLARE @Result int;
EXEC @Result = sys.sp_getapplock
    @Resource = @Resource,
    @LockMode = N'Exclusive',
    @LockOwner = N'Transaction',
    @LockTimeout = 60000;
IF @Result < 0
    THROW 51000, N'Could not acquire the Workable schema deployment lock.', 1;
""";
        command.Parameters.AddWithValue("@Resource", $"Workable.Schema.{schemaName}");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task ExecuteBatch(
        SqlConnection connection,
        SqlTransaction transaction,
        string batch,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = batch;
        await command.ExecuteNonQueryAsync(cancellationToken);
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
            "HasIdempotencyReservation",
            "SubjectType",
            "SubjectValue",
            "InputJson",
            "OptionsJson",
            "ConfigurationJson",
            "OriginJson",
            "WorkflowProvenanceJson",
            "CreatedAt",
            "FailedAt",
            "FailureMessagesJson",
        };
        var requiredQueueColumns = new[]
        {
            "WorkerId",
            "WorkSystemName",
            "DefinitionName",
            "Disposition",
            "HasPersistentConcurrency",
            "ConcurrencyScope",
            "ConcurrencyMaximumCapacity",
            "SubjectType",
            "SubjectValue",
            "ConcurrencyType",
            "ConcurrencyValue",
            "CreatedAt",
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
  AND indexes.name = N'IX_WorkableWorkQueueEntries_Failed';
""", schemaName, cancellationToken) == 0)
        {
            missing.Add("IX_WorkableWorkQueueEntries_Failed");
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
            "WorkflowInputJson",
            "Status",
            "StepsJson",
            "MessagesJson",
            "ChildReceiptsJson",
            "PendingControlAction",
            "PendingControlRequestContextJson",
            "CreatedAt",
            "StartedAt",
            "CompletedAt",
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

    public static async Task ValidateExecutionDiagnosticsInstalled(
        string connectionString,
        string schemaName = "workable",
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(schemaName);

        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        var requiredColumns = new Dictionary<string, string[]>
        {
            ["SchemaVersion"] = ["Component", "Version", "UpdatedAt"],
            ["WorkIterationDiagnostics"] =
            [
                "DiagnosticId", "PersistenceScope", "WorkSystemId", "WorkSystemName", "WorkerId",
                "IterationSequence", "DefinitionId", "DefinitionName", "Status", "AttemptCount",
                "StartedAt", "CompletedAt", "DurationMilliseconds", "CaptureSource", "ProfileCaptureMode",
                "SqlClientProfilingAvailable", "HttpClientProfilingAvailable", "ProfileJson",
                "PayloadSchemaVersion", "ProfileNodeCount", "ProfileDropped", "PersistedLogCount",
                "DroppedLogCount", "RetentionSeconds", "CapturedAt", "UpdatedAt", "ExpiresAt",
            ],
            ["WorkIterationDiagnosticLogs"] =
            [
                "DiagnosticId", "Ordinal", "OccurredAt", "Level", "Category", "EventId", "EventName",
                "Message", "PropertiesJson", "ExceptionType", "ExceptionMessage", "ExceptionStack",
                "TraceId", "SpanId",
            ],
            ["WorkIterationInstrumentation"] =
            [
                "DiagnosticId", "Instrumentation", "NodeCount", "TimingCount", "TotalTimingMilliseconds",
                "MaximumTimingMilliseconds", "OmittedNodeCount",
            ],
            ["WorkDiagnosticCaptureRules"] =
            [
                "RuleId", "PersistenceScope", "WorkSystemName", "DefinitionName", "MinimumLogLevel",
                "ProfileCaptureMode", "ArtifactRetentionSeconds", "CreatedAt", "ActiveUntil", "CreatedByJson",
            ],
        };
        var missing = new List<string>();
        foreach (var (table, columns) in requiredColumns)
        {
            if (await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.tables tables
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = @SchemaName AND tables.name = @Name;
""", schemaName, cancellationToken, table) == 0)
            {
                missing.Add($"{schemaName}.{table}");
            }

            var existingColumns = await ReadExistingColumns(connection, schemaName, table, cancellationToken);
            foreach (var column in columns.Where(column => !existingColumns.Contains(column)))
            {
                missing.Add($"{schemaName}.{table}.{column}");
            }
        }

        var requiredIndexes = new Dictionary<string, string[]>
        {
            ["WorkIterationDiagnostics"] =
            [
                "IX_WorkableWorkIterationDiagnostics_RecentSystem",
                "IX_WorkableWorkIterationDiagnostics_RecentWork",
                "IX_WorkableWorkIterationDiagnostics_Worker",
                "IX_WorkableWorkIterationDiagnostics_ExpirationByScope",
                "IX_WorkableWorkIterationDiagnostics_IncompleteByScope",
            ],
            ["WorkDiagnosticCaptureRules"] = ["IX_WorkableWorkDiagnosticCaptureRules_System"],
        };
        foreach (var (table, indexes) in requiredIndexes)
        {
            var missingIndexes = new List<string>();
            foreach (var index in indexes)
            {
                if (await Scalar<int>(connection, """
SELECT COUNT(*)
FROM sys.indexes indexes
INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
WHERE schemas.name = @SchemaName AND tables.name = @Name AND indexes.name = @IndexName;
""", schemaName, cancellationToken, table, index) == 0)
                {
                    missingIndexes.Add(index);
                }
            }

            missing.AddRange(missingIndexes);
        }

        if (!missing.Any(item => item.StartsWith($"{schemaName}.SchemaVersion", StringComparison.Ordinal)))
        {
            var installedVersion = await Scalar<int>(connection, $"""
SELECT COALESCE(MAX(Version), 0)
FROM {QuoteIdentifier(schemaName)}.[SchemaVersion]
WHERE Component = @Name;
""", schemaName, cancellationToken, "ExecutionDiagnostics");
            if (installedVersion < ExecutionDiagnosticsSchemaVersion)
            {
                missing.Add($"ExecutionDiagnostics schema version {ExecutionDiagnosticsSchemaVersion} (installed: {installedVersion})");
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Workable SQL Server execution diagnostics schema '{schemaName}' is not installed or is incomplete. Missing: {string.Join(", ", missing)}.");
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
        string? name = null,
        string? indexName = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        command.Parameters.AddWithValue("@SchemaName", schemaName);
        if (name is not null)
        {
            command.Parameters.AddWithValue("@Name", name);
        }

        if (indexName is not null)
        {
            command.Parameters.AddWithValue("@IndexName", indexName);
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

    private sealed record InstalledSchemaState(
        IReadOnlyDictionary<string, int> Versions,
        int KnownObjectCount);

    private sealed record SchemaMigration(
        string Component,
        int FromVersion,
        int ToVersion,
        IReadOnlyList<string> Batches);
}

public sealed class WorkableSqlServerSchemaDeploymentException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
