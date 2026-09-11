using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace Workable.SqlServer;

internal sealed class WorkableSqlServerScheduleStore : IWorkScheduleStore
{
    private const int MaximumDefinitionNameLength = 450;
    private const string RequiredDmlSetOptions = """
SET ANSI_NULLS ON;
SET QUOTED_IDENTIFIER ON;
SET ANSI_PADDING ON;
SET ANSI_WARNINGS ON;
SET ARITHABORT ON;
SET CONCAT_NULL_YIELDS_NULL ON;
SET NUMERIC_ROUNDABORT OFF;

""";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter(),
            new IReadOnlySetJsonConverterFactory(),
        },
    };

    private readonly WorkableSqlServerPersistenceOptions options;
    private readonly WorkableSqlServerSchemaInitializer schemaInitializer;
    private readonly string schedulesTable;
    private readonly string hostsTable;
    private readonly string occurrenceUsageTable;
    private readonly string occurrencesTable;

    public WorkableSqlServerScheduleStore(
        WorkableSqlServerPersistenceOptions options,
        WorkableSqlServerSchemaInitializer? schemaInitializer = null)
    {
        this.options = options;
        this.schemaInitializer = schemaInitializer ?? new(
            options.ConnectionString,
            options.SchemaName,
            options.AutoDeploySchema);
        var schema = WorkableSqlServerSchema.QuoteIdentifier(options.SchemaName);
        this.schedulesTable = $"{schema}.[WorkSchedules]";
        this.hostsTable = $"{schema}.[WorkScheduleHosts]";
        this.occurrenceUsageTable = $"{schema}.[WorkScheduleOccurrenceUsage]";
        this.occurrencesTable = $"{schema}.[WorkScheduleOccurrences]";
    }

    public Task Initialize(
        WorkScheduleStoreInitializationContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return this.schemaInitializer.InitializeScheduling(
            $"{this.options.PersistenceScope}:{NormalizeSystemName(context.WorkSystemName)}",
            cancellationToken);
    }

    public async Task<WorkScheduleStoreCreationStatus> Create(
        WorkScheduleStoreCreateRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Record.Schedule.DefinitionName) ||
            request.Record.Schedule.DefinitionName.Length > MaximumDefinitionNameLength)
        {
            return WorkScheduleStoreCreationStatus.InvalidDefinitionName;
        }

        await using var connection = await this.Open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + $"""
SET XACT_ABORT ON;
SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
BEGIN TRANSACTION;

-- Multi-table scheduling transactions lock this per-system range before touching schedules or occurrences.
DECLARE @OccurrenceUsageId uniqueidentifier;
SELECT @OccurrenceUsageId = OccurrenceUsageId
FROM {this.occurrenceUsageTable} WITH (UPDLOCK, HOLDLOCK)
WHERE PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName;

DECLARE @CreationStatus int = 0;
IF @PayloadSizeBytes > @MaximumSchedulePayloadBytes
BEGIN
    SET @CreationStatus = 1;
END
ELSE IF (SELECT COUNT(1)
    FROM {this.schedulesTable} WITH (UPDLOCK, HOLDLOCK)
    WHERE PersistenceScope = @PersistenceScope
      AND WorkSystemName = @WorkSystemName) >= @MaximumRetainedSchedules
BEGIN
    SET @CreationStatus = 4;
END
ELSE IF (SELECT COUNT(1)
    FROM {this.schedulesTable} WITH (UPDLOCK, HOLDLOCK)
    WHERE PersistenceScope = @PersistenceScope
      AND WorkSystemName = @WorkSystemName
      AND CreatedByKey = @CreatedByKey) >= @MaximumRetainedSchedulesPerActor
BEGIN
    SET @CreationStatus = 5;
END
ELSE IF COALESCE((SELECT SUM(PayloadSizeBytes)
    FROM {this.schedulesTable} WITH (UPDLOCK, HOLDLOCK)
    WHERE PersistenceScope = @PersistenceScope
      AND WorkSystemName = @WorkSystemName), 0) + @PayloadSizeBytes > @MaximumRetainedPayloadBytes
BEGIN
    SET @CreationStatus = 6;
END
ELSE IF COALESCE((SELECT SUM(PayloadSizeBytes)
    FROM {this.schedulesTable} WITH (UPDLOCK, HOLDLOCK)
    WHERE PersistenceScope = @PersistenceScope
      AND WorkSystemName = @WorkSystemName
      AND CreatedByKey = @CreatedByKey), 0) + @PayloadSizeBytes > @MaximumRetainedPayloadBytesPerActor
BEGIN
    SET @CreationStatus = 7;
END
ELSE IF (SELECT COUNT(1)
    FROM {this.schedulesTable} WITH (UPDLOCK, HOLDLOCK)
    WHERE PersistenceScope = @PersistenceScope
      AND WorkSystemName = @WorkSystemName
      AND Status = N'Active') >= @MaximumActiveSchedules
BEGIN
    SET @CreationStatus = 2;
END
ELSE IF (SELECT COUNT(1)
    FROM {this.schedulesTable} WITH (UPDLOCK, HOLDLOCK)
    WHERE PersistenceScope = @PersistenceScope
      AND WorkSystemName = @WorkSystemName
      AND CreatedByKey = @CreatedByKey
      AND Status = N'Active') >= @MaximumActiveSchedulesPerActor
BEGIN
    SET @CreationStatus = 8;
END
ELSE IF (SELECT COUNT(1)
    FROM {this.schedulesTable} WITH (UPDLOCK, HOLDLOCK)
    WHERE PersistenceScope = @PersistenceScope
      AND WorkSystemName = @WorkSystemName
      AND DefinitionName = @DefinitionName
      AND Status = N'Active') >= @MaximumActiveSchedulesPerDefinition
BEGIN
    SET @CreationStatus = 3;
END
ELSE
BEGIN
    IF @OccurrenceUsageId IS NULL
    BEGIN
        SET @OccurrenceUsageId = NEWID();
        INSERT INTO {this.occurrenceUsageTable}
            (OccurrenceUsageId, PersistenceScope, WorkSystemName, OccurrenceCount, PayloadSizeBytes)
        VALUES
            (@OccurrenceUsageId, @PersistenceScope, @WorkSystemName, 0, 0);
    END;

    INSERT INTO {this.schedulesTable}
    (
        ScheduleId, PersistenceScope, WorkSystemName, DefinitionName, TimingJson, InputJson,
        WorkerOptionsJson, RequestContextJson, Status, CreatedAt, NextRunAt, LastRunAt,
        CanceledAt, CanceledByJson, LeaseId, LeaseExpiresAt, ExecutionGrantJson,
        CreatedByJson, CreatedByKey, PayloadSizeBytes, CancellationPayloadReserveBytes
    )
    VALUES
    (
        @ScheduleId, @PersistenceScope, @WorkSystemName, @DefinitionName, @TimingJson, @InputJson,
        @WorkerOptionsJson, @RequestContextJson, @Status, @CreatedAt, @NextRunAt, @LastRunAt,
        @CanceledAt, @CanceledByJson, NULL, NULL, @ExecutionGrantJson,
        @CreatedByJson, @CreatedByKey, @PayloadSizeBytes, @CancellationPayloadReserveBytes
    );
END;

COMMIT;
SELECT @CreationStatus;
""";
        AddScheduleParameters(command, this.options.PersistenceScope, request.Record);
        Add(command, "@PayloadSizeBytes", request.PayloadSizeBytes);
        Add(command, "@CancellationPayloadReserveBytes", request.CancellationPayloadReserveBytes);
        Add(command, "@MaximumSchedulePayloadBytes", request.MaximumSchedulePayloadBytes);
        Add(command, "@MaximumActiveSchedules", request.MaximumActiveSchedules);
        Add(command, "@MaximumActiveSchedulesPerDefinition", request.MaximumActiveSchedulesPerDefinition);
        Add(command, "@MaximumActiveSchedulesPerActor", request.MaximumActiveSchedulesPerActor);
        Add(command, "@MaximumRetainedSchedules", request.MaximumRetainedSchedules);
        Add(command, "@MaximumRetainedSchedulesPerActor", request.MaximumRetainedSchedulesPerActor);
        Add(command, "@MaximumRetainedPayloadBytes", request.MaximumRetainedPayloadBytes);
        Add(command, "@MaximumRetainedPayloadBytesPerActor", request.MaximumRetainedPayloadBytesPerActor);
        var result = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        return result switch
        {
            0 => WorkScheduleStoreCreationStatus.Accepted,
            1 => WorkScheduleStoreCreationStatus.PayloadTooLarge,
            2 => WorkScheduleStoreCreationStatus.SystemLimitReached,
            3 => WorkScheduleStoreCreationStatus.DefinitionLimitReached,
            4 => WorkScheduleStoreCreationStatus.RetainedSystemLimitReached,
            5 => WorkScheduleStoreCreationStatus.RetainedActorLimitReached,
            6 => WorkScheduleStoreCreationStatus.RetainedSystemBytesLimitReached,
            7 => WorkScheduleStoreCreationStatus.RetainedActorBytesLimitReached,
            8 => WorkScheduleStoreCreationStatus.ActiveActorLimitReached,
            _ => throw new InvalidOperationException($"Unsupported schedule creation status '{result}'."),
        };
    }

    public async Task EndHost(
        WorkScheduleHostEnd hostEnd,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hostEnd);
        await using var connection = await this.Open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + $"""
UPDATE {this.hostsTable}
SET ObservedAt = CASE WHEN ObservedAt < @EndedAt THEN @EndedAt ELSE ObservedAt END,
    AvailableThrough = CASE WHEN AvailableThrough > @EndedAt THEN @EndedAt ELSE AvailableThrough END
WHERE HostRunId = @HostRunId
  AND PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName;
""";
        AddScope(command, hostEnd.WorkSystemName);
        Add(command, "@HostRunId", hostEnd.HostRunId);
        Add(command, "@EndedAt", hostEnd.EndedAt);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlySet<DateTimeOffset>> FindAvailableTimes(
        WorkScheduleHostAvailabilityRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ScheduledTimes.Count == 0)
        {
            return new HashSet<DateTimeOffset>();
        }

        await using var connection = await this.Open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + $"""
SELECT DISTINCT requested.ScheduledAt
FROM OPENJSON(@ScheduledTimesJson)
WITH (ScheduledAt datetimeoffset '$') requested
WHERE EXISTS
(
    SELECT 1
    FROM {this.hostsTable} hosts
    WHERE hosts.PersistenceScope = @PersistenceScope
      AND hosts.WorkSystemName = @WorkSystemName
      AND hosts.StartedAt <= requested.ScheduledAt
      AND hosts.AvailableThrough >= requested.ScheduledAt
);
""";
        AddScope(command, request.WorkSystemName);
        Add(command, "@ScheduledTimesJson", Serialize(request.ScheduledTimes));
        var available = new HashSet<DateTimeOffset>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            available.Add(reader.GetFieldValue<DateTimeOffset>(0));
        }

        return available;
    }

    public async Task<WorkSchedulePersistenceRecord?> Get(
        WorkScheduleStoreReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await this.Open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + SelectScheduleColumns + Environment.NewLine + $"""
FROM {this.schedulesTable}
WHERE PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName
  AND ScheduleId = @ScheduleId;
""";
        AddScope(command, request.WorkSystemName);
        Add(command, "@ScheduleId", request.ScheduleId.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSchedule(reader) : null;
    }

    public async Task<IReadOnlyList<WorkScheduleSummary>> List(
        WorkScheduleStoreListRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await this.Open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + $"SELECT TOP (@Take) " + ScheduleSummaryColumnList + Environment.NewLine + $"""
FROM {this.schedulesTable}
WHERE PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName
  AND (@DefinitionName IS NULL OR DefinitionName = @DefinitionName)
  AND (@DefinitionNamesJson IS NULL OR DefinitionName IN (
      SELECT [value] FROM OPENJSON(@DefinitionNamesJson)))
  AND (@Status IS NULL OR Status = @Status)
  AND (@CursorCreatedAt IS NULL OR CreatedAt < @CursorCreatedAt OR
      (CreatedAt = @CursorCreatedAt AND ScheduleId > @CursorScheduleId))
ORDER BY CreatedAt DESC, ScheduleId;
""";
        AddScope(command, request.WorkSystemName);
        Add(command, "@Take", request.Take);
        Add(command, "@DefinitionName", request.DefinitionName);
        Add(command, "@DefinitionNamesJson", request.DefinitionNames is null
            ? null
            : Serialize(request.DefinitionNames));
        Add(command, "@Status", request.Status?.ToString());
        Add(command, "@CursorCreatedAt", request.Cursor?.CreatedAt);
        Add(command, "@CursorScheduleId", request.Cursor?.ScheduleId.Value);
        var schedules = new List<WorkScheduleSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            schedules.Add(ReadScheduleSummary(reader));
        }

        return schedules;
    }

    public async Task<WorkSchedulePersistenceRecord?> Cancel(
        WorkScheduleStoreCancelRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await this.Open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + $"""
UPDATE {this.schedulesTable}
SET Status = N'Canceled',
    NextRunAt = NULL,
    CanceledAt = @CanceledAt,
    CanceledByJson = @CanceledByJson,
    LeaseId = NULL,
    LeaseExpiresAt = NULL,
    PayloadSizeBytes = PayloadSizeBytes - CancellationPayloadReserveBytes + @AdditionalPayloadSizeBytes,
    CancellationPayloadReserveBytes = 0
OUTPUT {OutputScheduleColumnList}
WHERE PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName
  AND ScheduleId = @ScheduleId
  AND Status = N'Active'
  AND DispatchStarted = 0;
""";
        AddScope(command, request.WorkSystemName);
        Add(command, "@ScheduleId", request.ScheduleId.Value);
        Add(command, "@CanceledAt", request.CanceledAt);
        var serializedCanceler = Serialize(request.CanceledBy);
        Add(command, "@CanceledByJson", serializedCanceler);
        Add(command, "@AdditionalPayloadSizeBytes", Encoding.UTF8.GetByteCount(serializedCanceler));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadSchedule(reader) : null;
    }

    public async Task<IReadOnlyList<WorkScheduleClaim>> ClaimDue(
        WorkScheduleClaimRequest request,
        CancellationToken cancellationToken = default)
        => await this.ClaimDue(request, observation: null, cancellationToken);

    public async Task<IReadOnlyList<WorkScheduleClaim>> ClaimDueAndObserveHost(
        WorkScheduleClaimRequest request,
        WorkScheduleHostObservation observation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return await this.ClaimDue(request, observation, cancellationToken);
    }

    private async Task<IReadOnlyList<WorkScheduleClaim>> ClaimDue(
        WorkScheduleClaimRequest request,
        WorkScheduleHostObservation? observation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var leaseId = Guid.NewGuid();
        var leaseExpiresAt = request.DueAt + request.LeaseDuration;
        await using var connection = await this.Open(cancellationToken);
        await using var command = connection.CreateCommand();
        var observationCommand = observation is null
            ? string.Empty
            : $"""
UPDATE {this.hostsTable}
SET ObservedAt = @ObservedAt,
    AvailableThrough = @AvailableThrough
WHERE HostRunId = @HostRunId
  AND PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName;

IF @@ROWCOUNT = 0
BEGIN
    DELETE TOP (100) FROM {this.hostsTable}
    WHERE PersistenceScope = @PersistenceScope
      AND WorkSystemName = @WorkSystemName
      AND AvailableThrough < @DeleteEndedBefore;

    INSERT INTO {this.hostsTable}
        (HostRunId, PersistenceScope, WorkSystemName, StartedAt, ObservedAt, AvailableThrough)
    VALUES
        (@HostRunId, @PersistenceScope, @WorkSystemName, @StartedAt, @ObservedAt, @AvailableThrough);
END;

""";
        command.CommandText = RequiredDmlSetOptions + observationCommand + $"""
;WITH CandidateSchedules AS
(
    SELECT TOP (@MaximumCount)
           ScheduleId,
           ROW_NUMBER() OVER (ORDER BY NextRunAt, ScheduleId) AS ClaimSequence,
           SUM(PayloadSizeBytes) OVER (
               ORDER BY NextRunAt, ScheduleId
               ROWS UNBOUNDED PRECEDING) AS RunningPayloadBytes
    FROM {this.schedulesTable} WITH (UPDLOCK, READPAST, ROWLOCK)
    WHERE PersistenceScope = @PersistenceScope
      AND WorkSystemName = @WorkSystemName
      AND Status = N'Active'
      AND NextRunAt <= @DueAt
      AND (LeaseExpiresAt IS NULL OR LeaseExpiresAt <= @DueAt)
    ORDER BY NextRunAt, ScheduleId
),
DueSchedules AS
(
    SELECT schedules.*
    FROM {this.schedulesTable} schedules
    INNER JOIN CandidateSchedules candidates ON candidates.ScheduleId = schedules.ScheduleId
    WHERE candidates.RunningPayloadBytes <= @MaximumPayloadBytes
       OR candidates.ClaimSequence = 1
)
UPDATE DueSchedules
SET LeaseId = @LeaseId,
    LeaseExpiresAt = @LeaseExpiresAt,
    DispatchStarted = 0
OUTPUT {OutputScheduleColumnList};
""";
        AddScope(command, request.WorkSystemName);
        Add(command, "@MaximumCount", request.MaximumCount);
        Add(command, "@MaximumPayloadBytes", request.MaximumPayloadBytes);
        Add(command, "@DueAt", request.DueAt);
        Add(command, "@LeaseId", leaseId);
        Add(command, "@LeaseExpiresAt", leaseExpiresAt);
        if (observation is not null)
        {
            Add(command, "@HostRunId", observation.HostRunId);
            Add(command, "@StartedAt", observation.StartedAt);
            Add(command, "@ObservedAt", observation.ObservedAt);
            Add(command, "@AvailableThrough", observation.AvailableThrough);
            Add(command, "@DeleteEndedBefore", observation.DeleteEndedBefore);
        }
        var claims = new List<WorkScheduleClaim>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var schedule = ReadSchedule(reader);
            claims.Add(new(
                leaseId,
                leaseExpiresAt,
                schedule.Schedule.NextRunAt!.Value,
                schedule));
        }

        return claims;
    }

    public async Task<bool> BeginDispatch(
        WorkScheduleDispatchStart dispatch,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dispatch);
        await using var connection = await this.Open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + $"""
DECLARE @DispatchStarted bit = 0;
UPDATE {this.schedulesTable}
SET LeaseExpiresAt = @LeaseExpiresAt,
    DispatchStarted = 1
WHERE PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName
  AND ScheduleId = @ScheduleId
  AND Status = N'Active'
  AND LeaseId = @LeaseId
  AND LeaseExpiresAt > @StartedAt
  AND DispatchStarted = 0;

IF @@ROWCOUNT = 1 SET @DispatchStarted = 1;
SELECT @DispatchStarted;
""";
        AddScope(command, dispatch.WorkSystemName);
        Add(command, "@ScheduleId", dispatch.ScheduleId.Value);
        Add(command, "@LeaseId", dispatch.LeaseId);
        Add(command, "@StartedAt", dispatch.StartedAt);
        Add(command, "@LeaseExpiresAt", dispatch.LeaseExpiresAt);
        return Convert.ToBoolean(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task CompleteClaim(
        WorkScheduleClaimCompletion completion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(completion);
        var serializedMessages = Serialize(completion.Occurrence.Messages);
        var occurrencePayloadSizeBytes = Encoding.UTF8.GetByteCount(serializedMessages);
        if (completion.MaximumRetainedOccurrences <= 0 ||
            completion.MaximumRetainedOccurrencePayloadBytes < occurrencePayloadSizeBytes)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completion),
                "Occurrence limits must be positive and must accommodate the occurrence payload.");
        }

        await using var connection = await this.Open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + $"""
SET XACT_ABORT ON;
SET TRANSACTION ISOLATION LEVEL READ COMMITTED;
BEGIN TRANSACTION;

-- Keep the same usage-to-schedule lock order as creation and terminal cleanup.
DECLARE @OccurrenceUsageId uniqueidentifier;
DECLARE @RetainedOccurrenceCount bigint;
DECLARE @RetainedOccurrencePayloadBytes bigint;

SELECT @OccurrenceUsageId = OccurrenceUsageId,
       @RetainedOccurrenceCount = OccurrenceCount,
       @RetainedOccurrencePayloadBytes = PayloadSizeBytes
FROM {this.occurrenceUsageTable} WITH (UPDLOCK, HOLDLOCK)
WHERE PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName;

UPDATE {this.schedulesTable}
SET Status = @ScheduleStatus,
    NextRunAt = @NextRunAt,
    LastRunAt = @AttemptedAt,
    LeaseId = NULL,
    LeaseExpiresAt = NULL,
    DispatchStarted = 0,
    PayloadSizeBytes = PayloadSizeBytes - CASE
        WHEN @ScheduleStatus = N'Active' THEN 0
        ELSE CancellationPayloadReserveBytes
    END,
    CancellationPayloadReserveBytes = CASE
        WHEN @ScheduleStatus = N'Active' THEN CancellationPayloadReserveBytes
        ELSE 0
    END
WHERE PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName
  AND ScheduleId = @ScheduleId
  AND Status = N'Active'
  AND LeaseId = @LeaseId
  AND DispatchStarted = 1;

IF @@ROWCOUNT = 1
BEGIN
    IF @OccurrenceUsageId IS NULL
    BEGIN
        SET @OccurrenceUsageId = NEWID();
        SET @RetainedOccurrenceCount = 0;
        SET @RetainedOccurrencePayloadBytes = 0;
        INSERT INTO {this.occurrenceUsageTable}
            (OccurrenceUsageId, PersistenceScope, WorkSystemName, OccurrenceCount, PayloadSizeBytes)
        VALUES
            (@OccurrenceUsageId, @PersistenceScope, @WorkSystemName, 0, 0);
    END;

    DECLARE @OccurrencesToFree bigint = @RetainedOccurrenceCount - @MaximumRetainedOccurrences + 1;
    DECLARE @PayloadBytesToFree bigint =
        @RetainedOccurrencePayloadBytes + @OccurrencePayloadSizeBytes - @MaximumRetainedOccurrencePayloadBytes;
    DECLARE @DeletedOccurrences TABLE (PayloadSizeBytes bigint NOT NULL);

    IF @OccurrencesToFree > 0 OR @PayloadBytesToFree > 0
    BEGIN
        ;WITH OrderedOccurrences AS
        (
            SELECT occurrences.OccurrenceId,
                   occurrences.PayloadSizeBytes,
                   ROW_NUMBER() OVER (ORDER BY occurrences.AttemptedAt, occurrences.OccurrenceId) AS Sequence,
                   SUM(occurrences.PayloadSizeBytes) OVER (
                       ORDER BY occurrences.AttemptedAt, occurrences.OccurrenceId
                       ROWS UNBOUNDED PRECEDING) AS RunningPayloadBytes
            FROM {this.occurrencesTable} occurrences
            WHERE occurrences.OccurrenceUsageId = @OccurrenceUsageId
        )
        DELETE occurrences
        OUTPUT deleted.PayloadSizeBytes INTO @DeletedOccurrences
        FROM {this.occurrencesTable} occurrences
        INNER JOIN OrderedOccurrences ordered ON ordered.OccurrenceId = occurrences.OccurrenceId
        WHERE (@OccurrencesToFree > 0 AND ordered.Sequence <= @OccurrencesToFree)
           OR (@PayloadBytesToFree > 0 AND ordered.RunningPayloadBytes - ordered.PayloadSizeBytes < @PayloadBytesToFree);
    END;

    DECLARE @DeletedOccurrenceCount bigint = (SELECT COUNT_BIG(1) FROM @DeletedOccurrences);
    DECLARE @DeletedOccurrencePayloadBytes bigint = COALESCE((SELECT SUM(PayloadSizeBytes) FROM @DeletedOccurrences), 0);

    INSERT INTO {this.occurrencesTable}
    (
        OccurrenceId, OccurrenceUsageId, ScheduleId, ScheduledAt, AttemptedAt, Status, QueueStatus,
        WorkerId, MessagesJson, ExpiresAt, PayloadSizeBytes
    )
    VALUES
    (
        @OccurrenceId, @OccurrenceUsageId, @ScheduleId, @ScheduledAt, @AttemptedAt, @OccurrenceStatus, @QueueStatus,
        @WorkerId, @MessagesJson, @ExpiresAt, @OccurrencePayloadSizeBytes
    );

    UPDATE {this.occurrenceUsageTable}
    SET OccurrenceCount = @RetainedOccurrenceCount - @DeletedOccurrenceCount + 1,
        PayloadSizeBytes = @RetainedOccurrencePayloadBytes - @DeletedOccurrencePayloadBytes + @OccurrencePayloadSizeBytes
    WHERE OccurrenceUsageId = @OccurrenceUsageId;
END;

COMMIT;
""";
        AddScope(command, completion.WorkSystemName);
        Add(command, "@ScheduleId", completion.ScheduleId.Value);
        Add(command, "@LeaseId", completion.LeaseId);
        Add(command, "@ScheduleStatus", completion.ScheduleStatus.ToString());
        Add(command, "@NextRunAt", completion.NextRunAt);
        Add(command, "@OccurrencePayloadSizeBytes", occurrencePayloadSizeBytes);
        Add(command, "@MaximumRetainedOccurrences", completion.MaximumRetainedOccurrences);
        Add(command, "@MaximumRetainedOccurrencePayloadBytes", completion.MaximumRetainedOccurrencePayloadBytes);
        AddOccurrenceParameters(command, completion.Occurrence, serializedMessages);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task ReleaseClaim(
        WorkScheduleClaimRelease release,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(release);
        await using var connection = await this.Open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + $"""
UPDATE {this.schedulesTable}
SET LeaseId = NULL, LeaseExpiresAt = NULL, DispatchStarted = 0
WHERE PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName
  AND ScheduleId = @ScheduleId
  AND LeaseId = @LeaseId;
""";
        AddScope(command, release.WorkSystemName);
        Add(command, "@ScheduleId", release.ScheduleId.Value);
        Add(command, "@LeaseId", release.LeaseId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<WorkScheduleOccurrence>> ListOccurrences(
        WorkScheduleOccurrenceReadRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await this.Open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + $"""
;WITH OrderedOccurrences AS
(
    SELECT TOP (@Take)
           occurrences.OccurrenceId, occurrences.ScheduleId, occurrences.ScheduledAt,
           occurrences.AttemptedAt, occurrences.Status, occurrences.QueueStatus,
           occurrences.WorkerId, occurrences.MessagesJson, occurrences.ExpiresAt,
           ROW_NUMBER() OVER (
               ORDER BY occurrences.AttemptedAt DESC, occurrences.OccurrenceId) AS ResultSequence,
           SUM(occurrences.PayloadSizeBytes) OVER (
               ORDER BY occurrences.AttemptedAt DESC, occurrences.OccurrenceId
               ROWS UNBOUNDED PRECEDING) AS RunningPayloadBytes
    FROM {this.occurrencesTable} occurrences
    INNER JOIN {this.schedulesTable} schedules ON schedules.ScheduleId = occurrences.ScheduleId
    WHERE schedules.PersistenceScope = @PersistenceScope
      AND schedules.WorkSystemName = @WorkSystemName
      AND occurrences.ScheduleId = @ScheduleId
      AND occurrences.ExpiresAt > @Now
    ORDER BY occurrences.AttemptedAt DESC, occurrences.OccurrenceId
)
SELECT OccurrenceId, ScheduleId, ScheduledAt, AttemptedAt, Status, QueueStatus,
       WorkerId, MessagesJson, ExpiresAt
FROM OrderedOccurrences
WHERE RunningPayloadBytes <= @MaximumPayloadBytes
   OR ResultSequence = 1
ORDER BY AttemptedAt DESC, OccurrenceId;
""";
        AddScope(command, request.WorkSystemName);
        Add(command, "@ScheduleId", request.ScheduleId.Value);
        Add(command, "@Take", request.Take);
        Add(command, "@MaximumPayloadBytes", request.MaximumPayloadBytes);
        Add(command, "@Now", DateTimeOffset.UtcNow);
        var occurrences = new List<WorkScheduleOccurrence>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            occurrences.Add(ReadOccurrence(reader));
        }

        return occurrences;
    }

    public async Task<int> DeleteExpiredOccurrences(
        WorkScheduleOccurrenceExpirationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await this.Open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + $"""
SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- Lock the per-system usage range before mutating occurrence history.
DECLARE @OccurrenceUsageId uniqueidentifier;
SELECT @OccurrenceUsageId = OccurrenceUsageId
FROM {this.occurrenceUsageTable} WITH (UPDLOCK, HOLDLOCK)
WHERE PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName;

DECLARE @DeletedOccurrences TABLE (PayloadSizeBytes bigint NOT NULL);
;WITH Expired AS
(
    SELECT TOP (@MaximumCount) occurrences.OccurrenceId
    FROM {this.occurrencesTable} occurrences
    WHERE occurrences.OccurrenceUsageId = @OccurrenceUsageId
      AND occurrences.ExpiresAt <= @ExpiresBefore
    ORDER BY occurrences.ExpiresAt, occurrences.OccurrenceId
)
DELETE occurrences
OUTPUT deleted.PayloadSizeBytes INTO @DeletedOccurrences
FROM {this.occurrencesTable} occurrences
INNER JOIN Expired ON Expired.OccurrenceId = occurrences.OccurrenceId;

DECLARE @DeletedOccurrenceCount bigint = (SELECT COUNT_BIG(1) FROM @DeletedOccurrences);
DECLARE @DeletedOccurrencePayloadBytes bigint = COALESCE((SELECT SUM(PayloadSizeBytes) FROM @DeletedOccurrences), 0);
UPDATE {this.occurrenceUsageTable}
SET OccurrenceCount = OccurrenceCount - @DeletedOccurrenceCount,
    PayloadSizeBytes = PayloadSizeBytes - @DeletedOccurrencePayloadBytes
WHERE OccurrenceUsageId = @OccurrenceUsageId;

COMMIT;
SELECT @DeletedOccurrenceCount;
""";
        AddScope(command, request.WorkSystemName);
        Add(command, "@MaximumCount", request.MaximumCount);
        Add(command, "@ExpiresBefore", request.ExpiresBefore);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    public async Task<int> DeleteExpiredSchedules(
        WorkScheduleExpirationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        await using var connection = await this.Open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + $"""
SET XACT_ABORT ON;
BEGIN TRANSACTION;

-- Keep the same usage-to-schedule lock order as creation and occurrence completion.
DECLARE @OccurrenceUsageId uniqueidentifier;
SELECT @OccurrenceUsageId = OccurrenceUsageId
FROM {this.occurrenceUsageTable} WITH (UPDLOCK, HOLDLOCK)
WHERE PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName;

DECLARE @ExpiredSchedules TABLE (ScheduleId uniqueidentifier NOT NULL PRIMARY KEY);
INSERT INTO @ExpiredSchedules (ScheduleId)
SELECT TOP (@MaximumCount) ScheduleId
FROM {this.schedulesTable} WITH (UPDLOCK, HOLDLOCK)
WHERE PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName
  AND ((Status = N'Completed' AND LastRunAt <= @FinalizedBefore)
    OR (Status = N'Canceled' AND CanceledAt <= @FinalizedBefore))
ORDER BY COALESCE(CanceledAt, LastRunAt), ScheduleId;

DECLARE @DeletedOccurrenceCount bigint = 0;
DECLARE @DeletedOccurrencePayloadBytes bigint = 0;
SELECT @DeletedOccurrenceCount = COUNT_BIG(1),
       @DeletedOccurrencePayloadBytes = COALESCE(SUM(occurrences.PayloadSizeBytes), 0)
FROM {this.occurrencesTable} occurrences
INNER JOIN @ExpiredSchedules expired ON expired.ScheduleId = occurrences.ScheduleId
WHERE occurrences.OccurrenceUsageId = @OccurrenceUsageId;

DELETE schedules
FROM {this.schedulesTable} schedules
INNER JOIN @ExpiredSchedules expired ON expired.ScheduleId = schedules.ScheduleId;

UPDATE {this.occurrenceUsageTable}
SET OccurrenceCount = OccurrenceCount - @DeletedOccurrenceCount,
    PayloadSizeBytes = PayloadSizeBytes - @DeletedOccurrencePayloadBytes
WHERE OccurrenceUsageId = @OccurrenceUsageId;

DECLARE @DeletedScheduleCount int = (SELECT COUNT(1) FROM @ExpiredSchedules);
COMMIT;
SELECT @DeletedScheduleCount;
""";
        AddScope(command, request.WorkSystemName);
        Add(command, "@MaximumCount", request.MaximumCount);
        Add(command, "@FinalizedBefore", request.FinalizedBefore);
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<SqlConnection> Open(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(this.options.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private void AddScope(DbCommand command, string? workSystemName)
    {
        Add(command, "@PersistenceScope", this.options.PersistenceScope);
        Add(command, "@WorkSystemName", NormalizeSystemName(workSystemName));
    }

    private static void AddScheduleParameters(
        DbCommand command,
        string persistenceScope,
        WorkSchedulePersistenceRecord record)
    {
        var schedule = record.Schedule;
        Add(command, "@ScheduleId", schedule.Id.Value);
        Add(command, "@PersistenceScope", persistenceScope);
        Add(command, "@WorkSystemName", NormalizeSystemName(schedule.WorkSystemName));
        Add(command, "@DefinitionName", schedule.DefinitionName);
        Add(command, "@TimingJson", Serialize(schedule.Timing));
        Add(command, "@InputJson", SerializeOptional(schedule.Input));
        Add(command, "@WorkerOptionsJson", SerializeWorkerOptions(schedule.WorkerOptions));
        var requestContext = record.RequestContext.WithoutAuthorization();
        Add(command, "@RequestContextJson", Serialize(new PersistedRequestContext(
            requestContext.Origin,
            requestContext.Description,
            requestContext.Url,
            requestContext.IsAuthenticated)));
        Add(command, "@ExecutionGrantJson", Serialize(record.ExecutionGrant));
        Add(command, "@CreatedByJson", Serialize(schedule.CreatedBy));
        Add(command, "@CreatedByKey", SHA256.HashData(Encoding.UTF8.GetBytes(schedule.CreatedBy.Id ?? string.Empty)));
        Add(command, "@Status", schedule.Status.ToString());
        Add(command, "@CreatedAt", schedule.CreatedAt);
        Add(command, "@NextRunAt", schedule.NextRunAt);
        Add(command, "@LastRunAt", schedule.LastRunAt);
        Add(command, "@CanceledAt", schedule.CanceledAt);
        Add(command, "@CanceledByJson", SerializeOptional(schedule.CanceledBy));
    }

    private static void AddOccurrenceParameters(
        DbCommand command,
        WorkScheduleOccurrence occurrence,
        string? serializedMessages = null)
    {
        Add(command, "@OccurrenceId", occurrence.OccurrenceId);
        Add(command, "@ScheduledAt", occurrence.ScheduledAt);
        Add(command, "@AttemptedAt", occurrence.AttemptedAt);
        Add(command, "@OccurrenceStatus", occurrence.Status.ToString());
        Add(command, "@QueueStatus", occurrence.QueueStatus?.ToString());
        var workerId = occurrence.WorkerId is { } value ? value.Value : (Guid?)null;
        Add(command, "@WorkerId", workerId);
        Add(command, "@MessagesJson", serializedMessages ?? Serialize(occurrence.Messages));
        Add(command, "@ExpiresAt", occurrence.ExpiresAt);
    }

    private static WorkSchedulePersistenceRecord ReadSchedule(DbDataReader reader)
    {
        var timing = Deserialize<WorkScheduleTiming>(reader.GetString(4));
        var input = DeserializeOptional<WorkInput>(reader, 5);
        var workerOptions = DeserializeOptional<PersistedWorkerOptions>(reader, 6)?.ToWorkerOptions();
        var requestContext = Deserialize<PersistedRequestContext>(reader.GetString(7)).ToRequestContext();
        var snapshot = new WorkScheduleSnapshot(
            new WorkScheduleId(reader.GetGuid(0)),
            DenormalizeSystemName(reader.GetString(2)),
            reader.GetString(3),
            timing,
            input,
            workerOptions,
            Enum.Parse<WorkScheduleStatus>(reader.GetString(8), ignoreCase: false),
            reader.GetFieldValue<DateTimeOffset>(9),
            requestContext.Actor,
            reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
            reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
            reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
            DeserializeOptional<WorkActor>(reader, 13));
        return new(snapshot, requestContext, Deserialize<WorkScheduleExecutionGrant>(reader.GetString(14)))
        {
            SerializedPayloadBytes = reader.GetInt64(15),
        };
    }

    private static WorkScheduleSummary ReadScheduleSummary(DbDataReader reader)
    {
        return new(
            new WorkScheduleId(reader.GetGuid(0)),
            DenormalizeSystemName(reader.GetString(1)),
            reader.GetString(2),
            Deserialize<WorkScheduleTiming>(reader.GetString(3)),
            Enum.Parse<WorkScheduleStatus>(reader.GetString(4), ignoreCase: false),
            reader.GetFieldValue<DateTimeOffset>(5),
            Deserialize<WorkActor>(reader.GetString(9)),
            reader.IsDBNull(6) ? null : reader.GetFieldValue<DateTimeOffset>(6),
            reader.IsDBNull(7) ? null : reader.GetFieldValue<DateTimeOffset>(7),
            reader.IsDBNull(8) ? null : reader.GetFieldValue<DateTimeOffset>(8),
            DeserializeOptional<WorkActor>(reader, 10));
    }

    private static WorkScheduleOccurrence ReadOccurrence(DbDataReader reader)
        => new(
            reader.GetGuid(0),
            new WorkScheduleId(reader.GetGuid(1)),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetFieldValue<DateTimeOffset>(3),
            Enum.Parse<WorkScheduleOccurrenceStatus>(reader.GetString(4), ignoreCase: false),
            reader.IsDBNull(5) ? null : Enum.Parse<WorkQueueStatus>(reader.GetString(5), ignoreCase: false),
            reader.IsDBNull(6) ? null : new WorkerId(reader.GetGuid(6)),
            Deserialize<WorkMessage[]>(reader.GetString(7)),
            reader.GetFieldValue<DateTimeOffset>(8));

    private static string Serialize<T>(T value)
        => JsonSerializer.Serialize(value, JsonOptions);

    private static string? SerializeOptional<T>(T? value)
        => value is null ? null : Serialize(value);

    private static string? SerializeWorkerOptions(WorkerOptions? workerOptions)
        => workerOptions is null
            ? null
            : Serialize(new PersistedWorkerOptions(
                workerOptions.HasExplicitProfilingEnabled ? workerOptions.ProfilingEnabled : null,
                workerOptions.HasExplicitProfilingCaptureMode ? workerOptions.ProfilingCaptureMode : null,
                workerOptions.Configuration));

    private static T Deserialize<T>(string json)
        => JsonSerializer.Deserialize<T>(json, JsonOptions)
            ?? throw new InvalidOperationException($"Could not deserialize persisted {typeof(T).Name}.");

    private static T? DeserializeOptional<T>(DbDataReader reader, int ordinal)
        where T : class
        => reader.IsDBNull(ordinal) ? null : Deserialize<T>(reader.GetString(ordinal));

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private static string NormalizeSystemName(string? name) => name ?? string.Empty;

    private static string? DenormalizeSystemName(string name) => name.Length == 0 ? null : name;

    private const string ScheduleColumnList = """
ScheduleId, PersistenceScope, WorkSystemName, DefinitionName, TimingJson, InputJson,
WorkerOptionsJson, RequestContextJson, Status, CreatedAt, NextRunAt, LastRunAt,
CanceledAt, CanceledByJson, ExecutionGrantJson, PayloadSizeBytes
""";

    private const string SelectScheduleColumns = "SELECT " + ScheduleColumnList;

    private const string ScheduleSummaryColumnList = """
ScheduleId, WorkSystemName, DefinitionName, TimingJson, Status, CreatedAt, NextRunAt,
LastRunAt, CanceledAt, CreatedByJson, CanceledByJson
""";

    private const string OutputScheduleColumnList = """
inserted.ScheduleId, inserted.PersistenceScope, inserted.WorkSystemName, inserted.DefinitionName,
inserted.TimingJson, inserted.InputJson, inserted.WorkerOptionsJson, inserted.RequestContextJson,
inserted.Status, inserted.CreatedAt, inserted.NextRunAt, inserted.LastRunAt,
inserted.CanceledAt, inserted.CanceledByJson, inserted.ExecutionGrantJson, inserted.PayloadSizeBytes
""";

    private sealed record PersistedWorkerOptions(
        bool? ProfilingEnabled,
        WorkProfileCaptureMode? ProfilingCaptureMode,
        WorkConfiguration? Configuration)
    {
        public WorkerOptions ToWorkerOptions()
        {
            var options = this.ProfilingEnabled is { } profilingEnabled
                ? new WorkerOptions(profilingEnabled, this.Configuration)
                : new WorkerOptions(this.Configuration);
            return this.ProfilingCaptureMode is { } profilingCaptureMode
                ? options with { ProfilingCaptureMode = profilingCaptureMode }
                : options;
        }
    }

    private sealed record PersistedRequestContext(
        WorkOrigin Origin,
        string? Description,
        string? Url,
        bool IsAuthenticated)
    {
        public WorkRequestContext ToRequestContext()
            => new(
                this.Origin,
                this.Description,
                this.Url,
                Authorization: null,
                IsAuthenticated: this.IsAuthenticated);
    }

    private sealed class IReadOnlySetJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert)
            => typeToConvert.IsGenericType &&
                typeToConvert.GetGenericTypeDefinition() == typeof(IReadOnlySet<>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var elementType = typeToConvert.GetGenericArguments()[0];
            return (JsonConverter)Activator.CreateInstance(
                typeof(IReadOnlySetJsonConverter<>).MakeGenericType(elementType))!;
        }

        private sealed class IReadOnlySetJsonConverter<T> : JsonConverter<IReadOnlySet<T>>
        {
            public override IReadOnlySet<T>? Read(
                ref Utf8JsonReader reader,
                Type typeToConvert,
                JsonSerializerOptions options)
                => JsonSerializer.Deserialize<HashSet<T>>(ref reader, options);

            public override void Write(
                Utf8JsonWriter writer,
                IReadOnlySet<T> value,
                JsonSerializerOptions options)
                => JsonSerializer.Serialize(writer, value.ToArray(), options);
        }
    }
}
