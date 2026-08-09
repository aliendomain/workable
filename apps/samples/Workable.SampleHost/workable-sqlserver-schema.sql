SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;
GO

IF SCHEMA_ID(N'workable') IS NULL EXEC(N'CREATE SCHEMA [workable]')
GO

IF OBJECT_ID(N'workable.SchemaVersion', N'U') IS NULL
BEGIN
    CREATE TABLE [workable].[SchemaVersion]
    (
        Component nvarchar(128) NOT NULL CONSTRAINT PK_WorkableSchemaVersion PRIMARY KEY,
        Version int NOT NULL,
        UpdatedAt datetimeoffset NOT NULL
    );
END
GO

IF OBJECT_ID(N'workable.WorkEntries', N'U') IS NULL
BEGIN
    CREATE TABLE [workable].[WorkEntries]
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
GO

IF OBJECT_ID(N'workable.WorkQueueEntries', N'U') IS NULL
BEGIN
    CREATE TABLE [workable].[WorkQueueEntries]
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
GO

IF OBJECT_ID(N'workable.WorkflowRuns', N'U') IS NULL
BEGIN
    CREATE TABLE [workable].[WorkflowRuns]
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
        CompletedAt datetimeoffset NULL,
        UpdatedAt datetimeoffset NOT NULL
    );
END
GO

IF OBJECT_ID(N'workable.WorkIterationDiagnostics', N'U') IS NULL
BEGIN
    CREATE TABLE [workable].[WorkIterationDiagnostics]
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
GO

IF OBJECT_ID(N'workable.WorkIterationDiagnosticLogs', N'U') IS NULL
BEGIN
    CREATE TABLE [workable].[WorkIterationDiagnosticLogs]
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
            REFERENCES [workable].[WorkIterationDiagnostics](DiagnosticId) ON DELETE CASCADE
    );
END
GO

IF OBJECT_ID(N'workable.WorkIterationInstrumentation', N'U') IS NULL
BEGIN
    CREATE TABLE [workable].[WorkIterationInstrumentation]
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
            REFERENCES [workable].[WorkIterationDiagnostics](DiagnosticId) ON DELETE CASCADE
    );
END
GO

IF OBJECT_ID(N'workable.WorkDiagnosticCaptureRules', N'U') IS NULL
BEGIN
    CREATE TABLE [workable].[WorkDiagnosticCaptureRules]
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
GO

IF OBJECT_ID(N'workable.WorkflowRuns', N'U') IS NOT NULL
   AND COL_LENGTH(N'workable.WorkflowRuns', N'DefinitionFingerprint') IS NULL
BEGIN
    ALTER TABLE [workable].[WorkflowRuns]
        ADD DefinitionFingerprint nvarchar(64) NOT NULL
            CONSTRAINT DF_WorkableWorkflowRuns_DefinitionFingerprint DEFAULT (N'');
END
GO

IF OBJECT_ID(N'workable.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND COL_LENGTH(N'workable.WorkIterationDiagnostics', N'SqlClientProfilingAvailable') IS NULL
BEGIN
    ALTER TABLE [workable].[WorkIterationDiagnostics]
        ADD SqlClientProfilingAvailable bit NOT NULL
            CONSTRAINT DF_WorkableWorkIterationDiagnostics_SqlClientProfilingAvailable DEFAULT (0);
END
GO

IF OBJECT_ID(N'workable.WorkIterationDiagnostics', N'U') IS NOT NULL
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
       WHERE schemas.name = N'workable'
         AND tables.name = N'WorkIterationDiagnostics'
         AND indexes.name = N'IX_WorkableWorkIterationDiagnostics_RecentWork'
         AND columns.name = N'PersistenceScope')
BEGIN
    DROP INDEX IX_WorkableWorkIterationDiagnostics_RecentWork ON [workable].[WorkIterationDiagnostics];
END
GO

IF OBJECT_ID(N'workable.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'workable'
         AND tables.name = N'WorkIterationDiagnostics'
         AND indexes.name = N'IX_WorkableWorkIterationDiagnostics_RecentSystem')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkIterationDiagnostics_RecentSystem ON [workable].[WorkIterationDiagnostics] (PersistenceScope, WorkSystemName, CompletedAt DESC, DiagnosticId) INCLUDE (DefinitionName, WorkerId, IterationSequence, Status, ExpiresAt);');
END
GO

IF OBJECT_ID(N'workable.WorkDiagnosticCaptureRules', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'workable'
         AND tables.name = N'WorkDiagnosticCaptureRules'
         AND indexes.name = N'IX_WorkableWorkDiagnosticCaptureRules_System')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkDiagnosticCaptureRules_System ON [workable].[WorkDiagnosticCaptureRules] (PersistenceScope, WorkSystemName, ActiveUntil, RuleId) INCLUDE (DefinitionName, CreatedAt);');
END
GO

IF OBJECT_ID(N'workable.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND COL_LENGTH(N'workable.WorkIterationDiagnostics', N'HttpClientProfilingAvailable') IS NULL
BEGIN
    ALTER TABLE [workable].[WorkIterationDiagnostics]
        ADD HttpClientProfilingAvailable bit NOT NULL
            CONSTRAINT DF_WorkableWorkIterationDiagnostics_HttpClientProfilingAvailable DEFAULT (0);
END
GO

IF OBJECT_ID(N'workable.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND COL_LENGTH(N'workable.WorkIterationDiagnostics', N'ProfileDropped') IS NULL
BEGIN
    ALTER TABLE [workable].[WorkIterationDiagnostics]
        ADD ProfileDropped bit NOT NULL
            CONSTRAINT DF_WorkableWorkIterationDiagnostics_ProfileDropped DEFAULT (0);
END
GO

IF OBJECT_ID(N'workable.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'workable'
         AND tables.name = N'WorkIterationDiagnostics'
         AND indexes.name = N'IX_WorkableWorkIterationDiagnostics_RecentWork')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkIterationDiagnostics_RecentWork ON [workable].[WorkIterationDiagnostics] (WorkSystemName, DefinitionName, CompletedAt DESC, DiagnosticId) INCLUDE (PersistenceScope, WorkerId, IterationSequence, Status, ExpiresAt);');
END
GO

IF OBJECT_ID(N'workable.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'workable'
         AND tables.name = N'WorkIterationDiagnostics'
         AND indexes.name = N'IX_WorkableWorkIterationDiagnostics_Worker')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkIterationDiagnostics_Worker ON [workable].[WorkIterationDiagnostics] (PersistenceScope, WorkSystemName, WorkerId, IterationSequence) INCLUDE (CompletedAt, ExpiresAt);');
END
GO

IF OBJECT_ID(N'workable.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'workable'
         AND tables.name = N'WorkIterationDiagnostics'
         AND indexes.name = N'IX_WorkableWorkIterationDiagnostics_ExpirationByScope')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkIterationDiagnostics_ExpirationByScope ON [workable].[WorkIterationDiagnostics] (PersistenceScope, ExpiresAt, DiagnosticId) WHERE ExpiresAt IS NOT NULL;');
END
GO

IF OBJECT_ID(N'workable.WorkIterationDiagnostics', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1 FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'workable'
         AND tables.name = N'WorkIterationDiagnostics'
         AND indexes.name = N'IX_WorkableWorkIterationDiagnostics_IncompleteByScope')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkIterationDiagnostics_IncompleteByScope ON [workable].[WorkIterationDiagnostics] (PersistenceScope, UpdatedAt, DiagnosticId) INCLUDE (RetentionSeconds) WHERE CompletedAt IS NULL;');
END
GO

IF OBJECT_ID(N'workable.WorkflowRuns', N'U') IS NOT NULL
   AND COL_LENGTH(N'workable.WorkflowRuns', N'WorkflowInputJson') IS NULL
BEGIN
    ALTER TABLE [workable].[WorkflowRuns]
        ADD WorkflowInputJson nvarchar(max) NULL;
END
GO

IF OBJECT_ID(N'workable.WorkflowRuns', N'U') IS NOT NULL
   AND COL_LENGTH(N'workable.WorkflowRuns', N'PendingControlAction') IS NULL
BEGIN
    ALTER TABLE [workable].[WorkflowRuns]
        ADD PendingControlAction nvarchar(32) NULL;
END
GO

IF OBJECT_ID(N'workable.WorkflowRuns', N'U') IS NOT NULL
   AND COL_LENGTH(N'workable.WorkflowRuns', N'PendingControlRequestContextJson') IS NULL
BEGIN
    ALTER TABLE [workable].[WorkflowRuns]
        ADD PendingControlRequestContextJson nvarchar(max) NULL;
END
GO

IF OBJECT_ID(N'workable.WorkflowRuns', N'U') IS NOT NULL
   AND COL_LENGTH(N'workable.WorkflowRuns', N'ChildReceiptsJson') IS NULL
BEGIN
    ALTER TABLE [workable].[WorkflowRuns]
        ADD ChildReceiptsJson nvarchar(max) NOT NULL
            CONSTRAINT DF_WorkableWorkflowRuns_ChildReceiptsJson DEFAULT (N'[]');
END
GO

IF OBJECT_ID(N'workable.WorkflowRuns', N'U') IS NOT NULL
   AND COL_LENGTH(N'workable.WorkflowRuns', N'WorkSystemId') IS NOT NULL
BEGIN
    ALTER TABLE [workable].[WorkflowRuns]
        DROP COLUMN WorkSystemId;
END
GO

IF OBJECT_ID(N'workable.WorkEntries', N'U') IS NOT NULL
   AND COL_LENGTH(N'workable.WorkEntries', N'HasPersistentConcurrency') IS NULL
BEGIN
    ALTER TABLE [workable].[WorkEntries]
        ADD HasPersistentConcurrency bit NOT NULL
            CONSTRAINT DF_WorkableWorkEntries_HasPersistentConcurrency DEFAULT (0);

    IF COL_LENGTH(N'workable.WorkEntries', N'IsDurableQueued') IS NOT NULL
       AND COL_LENGTH(N'workable.WorkEntries', N'ConfigurationJson') IS NOT NULL
    BEGIN
        EXEC(N'UPDATE [workable].[WorkEntries]
        SET HasPersistentConcurrency = CASE
            WHEN JSON_VALUE(ConfigurationJson, ''$.coordination.concurrency.isEnabled'') = ''true''
             AND JSON_VALUE(ConfigurationJson, ''$.coordination.storage'') = ''Persistent''
            THEN 1
            ELSE 0
        END
        WHERE IsDurableQueued = 1;');
    END
END
GO

IF OBJECT_ID(N'workable.WorkQueueEntries', N'U') IS NOT NULL
   AND OBJECT_ID(N'workable.WorkEntries', N'U') IS NOT NULL
   AND COL_LENGTH(N'workable.WorkEntries', N'IsDurableQueued') IS NOT NULL
BEGIN
    EXEC(N'
    INSERT INTO [workable].[WorkQueueEntries]
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
    FROM [workable].[WorkEntries] entries
    WHERE entries.IsDurableQueued = 1
      AND NOT EXISTS
      (
          SELECT 1
          FROM [workable].[WorkQueueEntries] queue
          WHERE queue.WorkerId = entries.WorkerId
      );

    UPDATE [workable].[WorkEntries]
    SET IsDurableQueued = 0,
        ClaimedBy = NULL,
        ClaimedAt = NULL,
        LeaseId = NULL,
        LeaseExpiresAt = NULL,
        ConcurrencyBucket = NULL
    WHERE IsDurableQueued = 1;');
END
GO

IF OBJECT_ID(N'workable.WorkEntries', N'U') IS NOT NULL
   AND COL_LENGTH(N'workable.WorkEntries', N'IsDurableQueued') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'workable'
         AND tables.name = N'WorkEntries'
         AND indexes.name = N'IX_WorkableWorkEntries_Ready')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkEntries_Ready ON [workable].[WorkEntries] (WorkSystemName, LeaseExpiresAt, CreatedAt, WorkerId) WHERE IsDurableQueued = 1;');
END
GO

IF OBJECT_ID(N'workable.WorkEntries', N'U') IS NOT NULL
   AND COL_LENGTH(N'workable.WorkEntries', N'HasPersistentConcurrency') IS NOT NULL
   AND COL_LENGTH(N'workable.WorkEntries', N'IsDurableQueued') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'workable'
         AND tables.name = N'WorkEntries'
         AND indexes.name = N'IX_WorkableWorkEntries_PersistentConcurrencyReady')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkEntries_PersistentConcurrencyReady ON [workable].[WorkEntries] (WorkSystemName, LeaseExpiresAt, CreatedAt, WorkerId) WHERE IsDurableQueued = 1 AND HasPersistentConcurrency = 1;');
END
GO

IF OBJECT_ID(N'workable.WorkEntries', N'U') IS NOT NULL
   AND COL_LENGTH(N'workable.WorkEntries', N'ConcurrencyBucket') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'workable'
         AND tables.name = N'WorkEntries'
         AND indexes.name = N'IX_WorkableWorkEntries_Concurrency')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkEntries_Concurrency ON [workable].[WorkEntries] (WorkSystemName, DefinitionName, ConcurrencyBucket, LeaseExpiresAt, SubjectType, SubjectValue, ConcurrencyType, ConcurrencyValue) WHERE ConcurrencyBucket IS NOT NULL;');
END
GO

IF OBJECT_ID(N'workable.WorkEntries', N'U') IS NOT NULL
   AND COL_LENGTH(N'workable.WorkEntries', N'HasIdempotencyReservation') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'workable'
         AND tables.name = N'WorkEntries'
         AND indexes.name = N'UX_WorkableWorkEntries_Idempotency')
BEGIN
    EXEC(N'CREATE UNIQUE INDEX UX_WorkableWorkEntries_Idempotency ON [workable].[WorkEntries] (WorkSystemName, DefinitionName, SubjectType, SubjectValue) WHERE HasIdempotencyReservation = 1 AND SubjectType IS NOT NULL AND SubjectValue IS NOT NULL;');
END
GO

IF OBJECT_ID(N'workable.WorkQueueEntries', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'workable'
         AND tables.name = N'WorkQueueEntries'
         AND indexes.name = N'IX_WorkableWorkQueueEntries_Ready')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkQueueEntries_Ready ON [workable].[WorkQueueEntries] (WorkSystemName, DefinitionName, CreatedAt, WorkerId) INCLUDE (LeaseExpiresAt, HasPersistentConcurrency);');
END
GO

IF OBJECT_ID(N'workable.WorkQueueEntries', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'workable'
         AND tables.name = N'WorkQueueEntries'
         AND indexes.name = N'IX_WorkableWorkQueueEntries_PersistentConcurrencyReady')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkQueueEntries_PersistentConcurrencyReady ON [workable].[WorkQueueEntries] (WorkSystemName, DefinitionName, CreatedAt, WorkerId) INCLUDE (LeaseExpiresAt, HasPersistentConcurrency, ConcurrencyScope, ConcurrencyMaximumCapacity, SubjectType, SubjectValue, ConcurrencyType, ConcurrencyValue) WHERE HasPersistentConcurrency = 1;');
END
GO

IF OBJECT_ID(N'workable.WorkQueueEntries', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'workable'
         AND tables.name = N'WorkQueueEntries'
         AND indexes.name = N'IX_WorkableWorkQueueEntries_Concurrency')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkQueueEntries_Concurrency ON [workable].[WorkQueueEntries] (WorkSystemName, DefinitionName, ConcurrencyBucket, LeaseExpiresAt, SubjectType, SubjectValue, ConcurrencyType, ConcurrencyValue) WHERE ConcurrencyBucket IS NOT NULL;');
END
GO

IF OBJECT_ID(N'workable.WorkflowRuns', N'U') IS NOT NULL
   AND NOT EXISTS (
       SELECT 1
       FROM sys.indexes indexes
       INNER JOIN sys.tables tables ON tables.object_id = indexes.object_id
       INNER JOIN sys.schemas schemas ON schemas.schema_id = tables.schema_id
       WHERE schemas.name = N'workable'
         AND tables.name = N'WorkflowRuns'
         AND indexes.name = N'IX_WorkableWorkflowRuns_Recovery')
BEGIN
    EXEC(N'CREATE INDEX IX_WorkableWorkflowRuns_Recovery ON [workable].[WorkflowRuns] (PersistenceScope, Status, CreatedAt, RunId);');
END
GO

MERGE [workable].[SchemaVersion] WITH (HOLDLOCK) AS target
USING (SELECT N'QueueDurability' AS Component, 3 AS Version) AS source
ON target.Component = source.Component
WHEN MATCHED THEN UPDATE SET Version = source.Version, UpdatedAt = SYSDATETIMEOFFSET()
WHEN NOT MATCHED THEN INSERT (Component, Version, UpdatedAt) VALUES (source.Component, source.Version, SYSDATETIMEOFFSET());
GO

MERGE [workable].[SchemaVersion] WITH (HOLDLOCK) AS target
USING (SELECT N'WorkflowPersistence' AS Component, 4 AS Version) AS source
ON target.Component = source.Component
WHEN MATCHED THEN UPDATE SET Version = source.Version, UpdatedAt = SYSDATETIMEOFFSET()
WHEN NOT MATCHED THEN INSERT (Component, Version, UpdatedAt) VALUES (source.Component, source.Version, SYSDATETIMEOFFSET());
GO

MERGE [workable].[SchemaVersion] WITH (HOLDLOCK) AS target
USING (SELECT N'ExecutionDiagnostics' AS Component, 7 AS Version) AS source
ON target.Component = source.Component
WHEN MATCHED THEN UPDATE SET Version = source.Version, UpdatedAt = SYSDATETIMEOFFSET()
WHEN NOT MATCHED THEN INSERT (Component, Version, UpdatedAt) VALUES (source.Component, source.Version, SYSDATETIMEOFFSET());
