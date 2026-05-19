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
        ON [workable].[WorkEntries] (WorkSystemName, LeaseExpiresAt, CreatedAt, WorkerId)
        WHERE IsDurableQueued = 1;

    CREATE INDEX IX_WorkableWorkEntries_Concurrency
        ON [workable].[WorkEntries] (WorkSystemName, DefinitionName, ConcurrencyBucket, LeaseExpiresAt, SubjectType, SubjectValue, ConcurrencyType, ConcurrencyValue)
        WHERE ConcurrencyBucket IS NOT NULL;

    CREATE UNIQUE INDEX UX_WorkableWorkEntries_Idempotency
        ON [workable].[WorkEntries] (WorkSystemName, DefinitionName, SubjectType, SubjectValue)
        WHERE HasIdempotencyReservation = 1 AND SubjectType IS NOT NULL AND SubjectValue IS NOT NULL;
END
GO

MERGE [workable].[SchemaVersion] WITH (HOLDLOCK) AS target
USING (SELECT N'QueueDurability' AS Component, 1 AS Version) AS source
ON target.Component = source.Component
WHEN MATCHED THEN UPDATE SET Version = source.Version, UpdatedAt = SYSDATETIMEOFFSET()
WHEN NOT MATCHED THEN INSERT (Component, Version, UpdatedAt) VALUES (source.Component, source.Version, SYSDATETIMEOFFSET());
