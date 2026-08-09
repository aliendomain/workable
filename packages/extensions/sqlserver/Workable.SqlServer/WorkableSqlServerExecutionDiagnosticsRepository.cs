using System.Collections.Concurrent;
using System.Data;
using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace Workable.SqlServer;

internal sealed class WorkableSqlServerExecutionDiagnosticsRepository(
    WorkableSqlServerPersistenceOptions options) : IWorkExecutionDiagnosticsRepository
{
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
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string diagnosticsTable =
        $"{WorkableSqlServerSchema.QuoteIdentifier(options.SchemaName)}.[WorkIterationDiagnostics]";
    private readonly string logsTable =
        $"{WorkableSqlServerSchema.QuoteIdentifier(options.SchemaName)}.[WorkIterationDiagnosticLogs]";
    private readonly string instrumentationTable =
        $"{WorkableSqlServerSchema.QuoteIdentifier(options.SchemaName)}.[WorkIterationInstrumentation]";
    private readonly string captureRulesTable =
        $"{WorkableSqlServerSchema.QuoteIdentifier(options.SchemaName)}.[WorkDiagnosticCaptureRules]";
    private readonly ConcurrentDictionary<WorkSystemId, string> systemNames = [];
    private readonly ConcurrentDictionary<Guid, WorkSystemId> activeDiagnostics = [];

    public async Task Initialize(
        WorkExecutionDiagnosticsInitializationContext context,
        CancellationToken cancellationToken = default)
    {
        this.systemNames[context.WorkSystemId] = NormalizeSystemName(context.WorkSystemName);
        try
        {
            if (options.AutoDeploySchema)
            {
                await WorkableSqlServerSchema.Apply(options.ConnectionString, options.SchemaName, cancellationToken);
            }

            await WorkableSqlServerSchema.ValidateExecutionDiagnosticsInstalled(
                options.ConnectionString,
                options.SchemaName,
                cancellationToken);
        }
        catch (SqlException exception) when (IsStoreUnavailable(exception))
        {
            var action = options.AutoDeploySchema ? "deploying or validating" : "validating";
            throw new WorkPersistenceStoreUnavailableException(
                $"Workable.SqlServer could not reach SQL Server while {action} execution diagnostics schema '{options.SchemaName}'.",
                exception);
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            var action = options.AutoDeploySchema ? "deploy" : "validate";
            throw new WorkableSqlServerSchemaDeploymentException(
                $"Workable.SqlServer could not {action} schema '{options.SchemaName}'.",
                exception);
        }
    }

    public async Task BeginIteration(
        WorkExecutionDiagnosticIterationStart iteration,
        CancellationToken cancellationToken = default)
    {
        this.activeDiagnostics.TryAdd(iteration.DiagnosticId, iteration.WorkSystemId);
        try
        {
            await using var connection = await Open(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = RequiredDmlSetOptions + $"""
IF NOT EXISTS (SELECT 1 FROM {this.diagnosticsTable} WHERE DiagnosticId = @DiagnosticId)
BEGIN
    INSERT INTO {this.diagnosticsTable}
    (
        DiagnosticId, PersistenceScope, WorkSystemId, WorkSystemName, WorkerId, IterationSequence,
        DefinitionId, DefinitionName, StartedAt, CaptureSource, ProfileCaptureMode,
        SqlClientProfilingAvailable, HttpClientProfilingAvailable, PayloadSchemaVersion,
        RetentionSeconds, CapturedAt, UpdatedAt
    )
    VALUES
    (
        @DiagnosticId, @PersistenceScope, @WorkSystemId, @WorkSystemName, @WorkerId, @IterationSequence,
        @DefinitionId, @DefinitionName, @StartedAt, @CaptureSource, @ProfileCaptureMode,
        @SqlClientProfilingAvailable, @HttpClientProfilingAvailable, 3,
        @RetentionSeconds, @CapturedAt, @CapturedAt
    );
END;
""";
            Add(command, "@DiagnosticId", iteration.DiagnosticId);
            Add(command, "@PersistenceScope", options.PersistenceScope);
            Add(command, "@WorkSystemId", iteration.WorkSystemId.Value);
            Add(command, "@WorkSystemName", NormalizeSystemName(iteration.WorkSystemName));
            Add(command, "@WorkerId", iteration.WorkerId.Value);
            Add(command, "@IterationSequence", iteration.IterationSequence);
            Add(command, "@DefinitionId", iteration.DefinitionId.Value);
            Add(command, "@DefinitionName", iteration.DefinitionName);
            Add(command, "@StartedAt", iteration.StartedAt);
            Add(command, "@CaptureSource", iteration.CaptureSource.ToString());
            Add(command, "@ProfileCaptureMode", iteration.ProfileCaptureMode?.ToString());
            Add(command, "@SqlClientProfilingAvailable", iteration.InstrumentationAvailability.SqlClientProfilingAvailable);
            Add(command, "@HttpClientProfilingAvailable", iteration.InstrumentationAvailability.HttpClientProfilingAvailable);
            Add(command, "@RetentionSeconds", checked((int)iteration.Retention.TotalSeconds));
            Add(command, "@CapturedAt", DateTimeOffset.UtcNow);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch
        {
            this.activeDiagnostics.TryRemove(iteration.DiagnosticId, out _);
            throw;
        }
    }

    public async Task AppendLogs(
        IReadOnlyList<WorkExecutionDiagnosticLogRecord> logs,
        CancellationToken cancellationToken = default)
    {
        if (logs.Count == 0)
        {
            return;
        }

        await using var connection = await Open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + $"""
INSERT INTO {this.logsTable}
(
    DiagnosticId, Ordinal, OccurredAt, Level, Category, EventId, EventName, Message,
    PropertiesJson, ExceptionType, ExceptionMessage, ExceptionStack, TraceId, SpanId
)
SELECT source.DiagnosticId, source.Ordinal, source.OccurredAt, source.Level, source.Category,
       source.EventId, source.EventName, source.Message, source.PropertiesJson, source.ExceptionType,
       source.ExceptionMessage, source.ExceptionStack, source.TraceId, source.SpanId
FROM OPENJSON(@LogsJson)
WITH
(
    DiagnosticId uniqueidentifier '$.diagnosticId',
    Ordinal bigint '$.ordinal',
    OccurredAt datetimeoffset '$.occurredAt',
    Level nvarchar(16) '$.level',
    Category nvarchar(512) '$.category',
    EventId int '$.eventId',
    EventName nvarchar(256) '$.eventName',
    Message nvarchar(max) '$.message',
    PropertiesJson nvarchar(max) '$.propertiesJson',
    ExceptionType nvarchar(512) '$.exceptionType',
    ExceptionMessage nvarchar(max) '$.exceptionMessage',
    ExceptionStack nvarchar(max) '$.exceptionStack',
    TraceId nvarchar(32) '$.traceId',
    SpanId nvarchar(16) '$.spanId'
) source
WHERE EXISTS (SELECT 1 FROM {this.diagnosticsTable} diagnostics WHERE diagnostics.DiagnosticId = source.DiagnosticId)
  AND NOT EXISTS (
      SELECT 1 FROM {this.logsTable} existing
      WHERE existing.DiagnosticId = source.DiagnosticId AND existing.Ordinal = source.Ordinal);

UPDATE diagnostics
SET UpdatedAt = @UpdatedAt
FROM {this.diagnosticsTable} diagnostics
WHERE diagnostics.DiagnosticId IN
(
    SELECT DISTINCT source.DiagnosticId
    FROM OPENJSON(@LogsJson)
    WITH (DiagnosticId uniqueidentifier '$.diagnosticId') source
);
""";
        var payload = logs.Select(log => new
        {
            log.DiagnosticId,
            log.Ordinal,
            log.OccurredAt,
            Level = log.Level.ToString(),
            log.Category,
            EventId = log.EventId.Id,
            EventName = log.EventId.Name,
            log.Message,
            log.PropertiesJson,
            log.ExceptionType,
            log.ExceptionMessage,
            ExceptionStack = log.ExceptionStackTrace,
            log.TraceId,
            log.SpanId,
        });
        Add(command, "@LogsJson", JsonSerializer.Serialize(payload, JsonOptions));
        Add(command, "@UpdatedAt", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CompleteIteration(
        WorkExecutionDiagnosticIterationCompletion completion,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await using var connection = await Open(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.Transaction = (SqlTransaction)transaction;
            command.CommandText = RequiredDmlSetOptions + $"""
UPDATE {this.diagnosticsTable}
SET Status = @Status,
    AttemptCount = @AttemptCount,
    CompletedAt = @CompletedAt,
    DurationMilliseconds = @DurationMilliseconds,
    ProfileJson = @ProfileJson,
    ProfileNodeCount = @ProfileNodeCount,
    ProfileDropped = @ProfileDropped,
    PersistedLogCount = @PersistedLogCount,
    DroppedLogCount = @DroppedLogCount,
    UpdatedAt = @CompletedAt,
    ExpiresAt = DATEADD(second, RetentionSeconds, @CompletedAt)
WHERE DiagnosticId = @DiagnosticId;

DELETE FROM {this.instrumentationTable} WHERE DiagnosticId = @DiagnosticId;

INSERT INTO {this.instrumentationTable}
(
    DiagnosticId, Instrumentation, NodeCount, TimingCount, TotalTimingMilliseconds,
    MaximumTimingMilliseconds, OmittedNodeCount
)
SELECT @DiagnosticId, source.Instrumentation, source.NodeCount, source.TimingCount,
       source.TotalTimingMilliseconds, source.MaximumTimingMilliseconds, source.OmittedNodeCount
FROM OPENJSON(@InstrumentationJson)
WITH
(
    Instrumentation nvarchar(128) '$.instrumentation',
    NodeCount int '$.nodeCount',
    TimingCount int '$.timingCount',
    TotalTimingMilliseconds bigint '$.totalTimingMilliseconds',
    MaximumTimingMilliseconds bigint '$.maximumTimingMilliseconds',
    OmittedNodeCount int '$.omittedNodeCount'
) source;
""";
            Add(command, "@DiagnosticId", completion.DiagnosticId);
            Add(command, "@Status", completion.Status.ToString());
            Add(command, "@AttemptCount", completion.AttemptCount);
            Add(command, "@CompletedAt", completion.CompletedAt);
            Add(command, "@DurationMilliseconds", checked((long)completion.ExecutionDuration.TotalMilliseconds));
            Add(command, "@ProfileJson", completion.Profile is null ? null : JsonSerializer.Serialize(completion.Profile, JsonOptions));
            Add(command, "@ProfileNodeCount", completion.Instrumentation.Sum(summary => summary.NodeCount));
            Add(command, "@ProfileDropped", completion.ProfileDropped);
            Add(command, "@PersistedLogCount", completion.PersistedLogCount);
            Add(command, "@DroppedLogCount", completion.DroppedLogCount);
            Add(command, "@InstrumentationJson", JsonSerializer.Serialize(completion.Instrumentation, JsonOptions));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            this.activeDiagnostics.TryRemove(completion.DiagnosticId, out _);
        }
    }

    public async Task<int> DeleteExpired(
        WorkExecutionDiagnosticsExpirationRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = this.GetSystemName(request.WorkSystemId);
        foreach (var active in this.activeDiagnostics)
        {
            if (active.Value == request.WorkSystemId &&
                !request.ActiveDiagnosticIds.Contains(active.Key))
            {
                this.activeDiagnostics.TryRemove(active.Key, out _);
            }
        }

        var activeDiagnosticIds = request.ActiveDiagnosticIds
            .Concat(this.activeDiagnostics.Keys)
            .Distinct()
            .ToArray();
        await using var connection = await Open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + $"""
WITH active AS
(
    SELECT DiagnosticId
    FROM OPENJSON(@ActiveDiagnosticIds)
    WITH (DiagnosticId uniqueidentifier '$')
),
candidates AS
(
    SELECT DiagnosticId, ExpiresAt AS ExpiredAt
    FROM {this.diagnosticsTable} WITH (READPAST)
    WHERE PersistenceScope = @PersistenceScope
      AND ExpiresAt IS NOT NULL
      AND ExpiresAt <= @ExpiresBefore
      AND NOT EXISTS
      (
          SELECT 1 FROM active WHERE active.DiagnosticId = {this.diagnosticsTable}.DiagnosticId
      )

    UNION ALL

    SELECT DiagnosticId, UpdatedAt AS ExpiredAt
    FROM {this.diagnosticsTable} WITH (READPAST)
    WHERE PersistenceScope = @PersistenceScope
      AND CompletedAt IS NULL
      AND (DATEADD(second, RetentionSeconds, UpdatedAt) <= @ExpiresBefore
           OR UpdatedAt <= @AbandonedBefore)
      AND NOT EXISTS
      (
          SELECT 1 FROM active WHERE active.DiagnosticId = {this.diagnosticsTable}.DiagnosticId
      )
),
expired AS
(
    SELECT TOP (@MaximumCount) DiagnosticId
    FROM candidates
    ORDER BY ExpiredAt, DiagnosticId
)
DELETE diagnostics
FROM {this.diagnosticsTable} diagnostics
INNER JOIN expired ON expired.DiagnosticId = diagnostics.DiagnosticId;
""";
        Add(command, "@MaximumCount", request.MaximumCount);
        Add(command, "@PersistenceScope", options.PersistenceScope);
        Add(command, "@ExpiresBefore", request.ExpiresBefore);
        Add(command, "@AbandonedBefore", request.AbandonedBefore);
        Add(command, "@ActiveDiagnosticIds", JsonSerializer.Serialize(activeDiagnosticIds));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<WorkExecutionDiagnosticQueryResult> Query(
        WorkExecutionDiagnosticCriteria criteria,
        CancellationToken cancellationToken = default)
    {
        if (criteria.Take is <= 0 or > 1_000)
        {
            throw new ArgumentOutOfRangeException(nameof(criteria), "Execution diagnostic query take must be between 1 and 1,000.");
        }

        var systemName = this.GetSystemName(criteria.WorkSystemId);
        await using var connection = await Open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + SelectSummarySql + $"""
  AND (@DefinitionName IS NULL OR DefinitionName = @DefinitionName)
  AND (@WorkerId IS NULL OR WorkerId = @WorkerId)
  AND (@CompletedAfter IS NULL OR CompletedAt >= @CompletedAfter)
  AND (@CompletedBefore IS NULL OR CompletedAt <= @CompletedBefore)
  AND (@MinimumLogLevel IS NULL OR EXISTS
      (
          SELECT 1
          FROM {this.logsTable} query_logs
          WHERE query_logs.DiagnosticId = {this.diagnosticsTable}.DiagnosticId
            AND CASE query_logs.Level
                WHEN 'Trace' THEN 0
                WHEN 'Debug' THEN 1
                WHEN 'Information' THEN 2
                WHEN 'Warning' THEN 3
                WHEN 'Error' THEN 4
                WHEN 'Critical' THEN 5
                ELSE 6
            END >= @MinimumLogLevel
      ))
ORDER BY CompletedAt DESC, DiagnosticId
OFFSET 0 ROWS FETCH NEXT @Take ROWS ONLY;
""";
        AddSummaryScope(command, systemName);
        Add(command, "@DefinitionName", NormalizeOptional(criteria.DefinitionName));
        Add(command, "@WorkerId", criteria.WorkerId?.Value);
        Add(command, "@CompletedAfter", criteria.CompletedAfter);
        Add(command, "@CompletedBefore", criteria.CompletedBefore);
        Add(command, "@MinimumLogLevel", criteria.MinimumLogLevel is null
            ? null
            : (int)criteria.MinimumLogLevel.Value);
        Add(command, "@Take", criteria.Take);
        var summaries = await ReadSummaries(command, cancellationToken);
        return new WorkExecutionDiagnosticQueryResult(
            await this.AttachInstrumentation(connection, summaries, cancellationToken));
    }

    public async Task<WorkExecutionDiagnosticArtifact?> Get(
        WorkExecutionDiagnosticGetRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.MaximumLogCount is <= 0 or > 10_000)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                "Execution diagnostic maximum log count must be between 1 and 10,000.");
        }

        var systemName = this.GetSystemName(request.WorkSystemId);
        await using var connection = await Open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + SelectSummarySql + """
  AND WorkerId = @WorkerId
  AND IterationSequence = @IterationSequence;
""";
        AddSummaryScope(command, systemName);
        Add(command, "@WorkerId", request.WorkerId.Value);
        Add(command, "@IterationSequence", request.IterationSequence);
        var summaries = await ReadSummaries(command, cancellationToken);
        if (summaries.Count == 0)
        {
            return null;
        }

        var summary = (await this.AttachInstrumentation(connection, summaries, cancellationToken))[0];
        var logs = await this.ReadLogs(
            connection,
            summary.DiagnosticId,
            request.MaximumLogCount + 1,
            cancellationToken);
        var logsTruncated = logs.Count > request.MaximumLogCount;
        if (logsTruncated)
        {
            logs = [.. logs.Take(request.MaximumLogCount)];
        }
        var profile = await this.ReadProfile(connection, summary.DiagnosticId, cancellationToken);
        return new WorkExecutionDiagnosticArtifact(summary, logs, profile)
        {
            LogsTruncated = logsTruncated,
        };
    }

    public async Task<IReadOnlyList<WorkExecutionDiagnosticCaptureRule>> ListCaptureRules(
        WorkExecutionDiagnosticsInitializationContext context,
        CancellationToken cancellationToken = default)
    {
        var systemName = NormalizeSystemName(context.WorkSystemName);
        await using var connection = await Open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + $"""
DELETE FROM {this.captureRulesTable}
WHERE PersistenceScope = @PersistenceScope
  AND ActiveUntil <= @Now;

SELECT RuleId, DefinitionName, MinimumLogLevel, ProfileCaptureMode,
       ArtifactRetentionSeconds, CreatedAt, ActiveUntil, CreatedByJson
FROM {this.captureRulesTable}
WHERE PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName
  AND ActiveUntil > @Now
ORDER BY CreatedAt, RuleId;
""";
        Add(command, "@PersistenceScope", options.PersistenceScope);
        Add(command, "@WorkSystemName", systemName);
        Add(command, "@Now", DateTimeOffset.UtcNow);
        var rules = new List<WorkExecutionDiagnosticCaptureRule>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            WorkProfileCaptureMode? captureMode = reader.IsDBNull(3)
                ? null
                : Enum.Parse<WorkProfileCaptureMode>(reader.GetString(3));
            rules.Add(new WorkExecutionDiagnosticCaptureRule(
                reader.GetGuid(0),
                context.WorkSystemId,
                context.WorkSystemName,
                NullableString(reader, 1),
                Enum.Parse<Microsoft.Extensions.Logging.LogLevel>(reader.GetString(2)),
                captureMode,
                TimeSpan.FromSeconds(reader.GetInt32(4)),
                reader.GetFieldValue<DateTimeOffset>(5),
                reader.GetFieldValue<DateTimeOffset>(6),
                JsonSerializer.Deserialize<WorkActor>(reader.GetString(7), JsonOptions)
                    ?? throw new InvalidOperationException("A persisted execution diagnostic capture rule had no creator.")));
        }

        return rules;
    }

    public async Task UpsertCaptureRule(
        WorkExecutionDiagnosticCaptureRule rule,
        int maximumActiveRules,
        CancellationToken cancellationToken = default)
    {
        if (maximumActiveRules <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumActiveRules));
        }

        await using var connection = await Open(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqlTransaction)transaction;
        command.CommandText = RequiredDmlSetOptions + $"""
DELETE FROM {this.captureRulesTable}
WHERE PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName
  AND ActiveUntil <= @Now;

IF NOT EXISTS (SELECT 1 FROM {this.captureRulesTable} WHERE RuleId = @RuleId)
   AND (SELECT COUNT(*) FROM {this.captureRulesTable} WITH (UPDLOCK, HOLDLOCK)
        WHERE PersistenceScope = @PersistenceScope AND WorkSystemName = @WorkSystemName) >= @MaximumActiveRules
    THROW 50010, 'The maximum number of active execution diagnostic capture rules has been reached.', 1;

MERGE {this.captureRulesTable} WITH (HOLDLOCK) AS target
USING (SELECT @RuleId AS RuleId) AS source
ON target.RuleId = source.RuleId
WHEN MATCHED THEN UPDATE SET
    DefinitionName = @DefinitionName,
    MinimumLogLevel = @MinimumLogLevel,
    ProfileCaptureMode = @ProfileCaptureMode,
    ArtifactRetentionSeconds = @ArtifactRetentionSeconds,
    ActiveUntil = @ActiveUntil,
    CreatedByJson = @CreatedByJson
WHEN NOT MATCHED THEN INSERT
(
    RuleId, PersistenceScope, WorkSystemName, DefinitionName, MinimumLogLevel,
    ProfileCaptureMode, ArtifactRetentionSeconds, CreatedAt, ActiveUntil, CreatedByJson
)
VALUES
(
    @RuleId, @PersistenceScope, @WorkSystemName, @DefinitionName, @MinimumLogLevel,
    @ProfileCaptureMode, @ArtifactRetentionSeconds, @CreatedAt, @ActiveUntil, @CreatedByJson
);
""";
        Add(command, "@RuleId", rule.Id);
        Add(command, "@Now", DateTimeOffset.UtcNow);
        Add(command, "@MaximumActiveRules", maximumActiveRules);
        Add(command, "@PersistenceScope", options.PersistenceScope);
        Add(command, "@WorkSystemName", NormalizeSystemName(rule.WorkSystemName));
        Add(command, "@DefinitionName", NormalizeOptional(rule.DefinitionName));
        Add(command, "@MinimumLogLevel", rule.MinimumLogLevel.ToString());
        Add(command, "@ProfileCaptureMode", rule.ProfileCaptureMode?.ToString());
        Add(command, "@ArtifactRetentionSeconds", checked((int)rule.ArtifactRetention.TotalSeconds));
        Add(command, "@CreatedAt", rule.CreatedAt);
        Add(command, "@ActiveUntil", rule.ActiveUntil);
        Add(command, "@CreatedByJson", JsonSerializer.Serialize(rule.CreatedBy, JsonOptions));
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> DeleteCaptureRule(
        WorkExecutionDiagnosticCaptureRuleDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        var systemName = this.GetSystemName(request.WorkSystemId);
        await using var connection = await Open(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + $"""
DELETE FROM {this.captureRulesTable}
WHERE RuleId = @RuleId
  AND PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName;
""";
        Add(command, "@RuleId", request.RuleId);
        Add(command, "@PersistenceScope", options.PersistenceScope);
        Add(command, "@WorkSystemName", systemName);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private const string SelectSummarySql = """
SELECT DiagnosticId, WorkSystemId, WorkSystemName, WorkerId, IterationSequence,
       DefinitionId, DefinitionName, Status, AttemptCount, StartedAt, CompletedAt,
       DurationMilliseconds, CaptureSource, ProfileCaptureMode, SqlClientProfilingAvailable,
       HttpClientProfilingAvailable, ProfileDropped, PersistedLogCount, DroppedLogCount, ExpiresAt
FROM {0}
WHERE PersistenceScope = @PersistenceScope
  AND WorkSystemName = @WorkSystemName
  AND CompletedAt IS NOT NULL
  AND ExpiresAt > @Now
""";

    private void AddSummaryScope(DbCommand command, string systemName)
    {
        command.CommandText = string.Format(command.CommandText, this.diagnosticsTable);
        Add(command, "@PersistenceScope", options.PersistenceScope);
        Add(command, "@WorkSystemName", systemName);
        Add(command, "@Now", DateTimeOffset.UtcNow);
    }

    private static async Task<List<WorkExecutionDiagnosticSummary>> ReadSummaries(
        DbCommand command,
        CancellationToken cancellationToken)
    {
        var summaries = new List<WorkExecutionDiagnosticSummary>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            WorkProfileCaptureMode? captureMode = reader.IsDBNull(13)
                ? null
                : Enum.Parse<WorkProfileCaptureMode>(reader.GetString(13));
            summaries.Add(new WorkExecutionDiagnosticSummary(
                reader.GetGuid(0),
                new WorkSystemId(reader.GetGuid(1)),
                DenormalizeSystemName(reader.GetString(2)),
                new WorkerId(reader.GetGuid(3)),
                reader.GetInt64(4),
                new WorkDefinitionId(reader.GetGuid(5)),
                reader.GetString(6),
                Enum.Parse<WorkCompletionStatus>(reader.GetString(7)),
                reader.GetInt32(8),
                reader.GetFieldValue<DateTimeOffset>(9),
                reader.GetFieldValue<DateTimeOffset>(10),
                TimeSpan.FromMilliseconds(reader.GetInt64(11)),
                Enum.Parse<WorkExecutionDiagnosticCaptureSource>(reader.GetString(12)),
                captureMode,
                new WorkExecutionDiagnosticInstrumentationAvailability(
                    reader.GetBoolean(14),
                    reader.GetBoolean(15)),
                reader.GetBoolean(16),
                reader.GetInt64(17),
                reader.GetInt64(18),
                reader.GetFieldValue<DateTimeOffset>(19),
                []));
        }

        return summaries;
    }

    private async Task<IReadOnlyList<WorkExecutionDiagnosticSummary>> AttachInstrumentation(
        SqlConnection connection,
        IReadOnlyList<WorkExecutionDiagnosticSummary> summaries,
        CancellationToken cancellationToken)
    {
        if (summaries.Count == 0)
        {
            return [];
        }

        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + $"""
SELECT Instrumentation, NodeCount, TimingCount, TotalTimingMilliseconds,
       MaximumTimingMilliseconds, OmittedNodeCount, DiagnosticId
FROM {this.instrumentationTable}
WHERE DiagnosticId IN (SELECT value FROM OPENJSON(@DiagnosticIds) WITH (value uniqueidentifier '$'));
""";
        Add(command, "@DiagnosticIds", JsonSerializer.Serialize(summaries.Select(summary => summary.DiagnosticId)));
        var byId = new Dictionary<Guid, List<WorkExecutionInstrumentationSummary>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(6);
            if (!byId.TryGetValue(id, out var entries))
            {
                entries = [];
                byId.Add(id, entries);
            }

            entries.Add(new WorkExecutionInstrumentationSummary(
                reader.GetString(0),
                reader.GetInt32(1),
                reader.GetInt32(2),
                reader.GetInt64(3),
                reader.GetInt64(4),
                reader.GetInt32(5)));
        }

        return [.. summaries.Select(summary => summary with
        {
            Instrumentation = byId.GetValueOrDefault(summary.DiagnosticId) ?? [],
        })];
    }

    private async Task<IReadOnlyList<WorkExecutionDiagnosticLogRecord>> ReadLogs(
        SqlConnection connection,
        Guid diagnosticId,
        int maximumCount,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + $"""
SELECT TOP (@MaximumCount) Ordinal, OccurredAt, Level, Category, EventId, EventName, Message, PropertiesJson,
       ExceptionType, ExceptionMessage, ExceptionStack, TraceId, SpanId
FROM {this.logsTable}
WHERE DiagnosticId = @DiagnosticId
ORDER BY Ordinal;
""";
        Add(command, "@DiagnosticId", diagnosticId);
        Add(command, "@MaximumCount", maximumCount);
        var logs = new List<WorkExecutionDiagnosticLogRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            logs.Add(new WorkExecutionDiagnosticLogRecord(
                diagnosticId,
                reader.GetInt64(0),
                reader.GetFieldValue<DateTimeOffset>(1),
                Enum.Parse<Microsoft.Extensions.Logging.LogLevel>(reader.GetString(2)),
                reader.GetString(3),
                new Microsoft.Extensions.Logging.EventId(reader.GetInt32(4), NullableString(reader, 5)),
                reader.GetString(6),
                NullableString(reader, 7),
                NullableString(reader, 8),
                NullableString(reader, 9),
                NullableString(reader, 10),
                NullableString(reader, 11),
                NullableString(reader, 12)));
        }

        return logs;
    }

    private async Task<WorkProfileSnapshot?> ReadProfile(
        SqlConnection connection,
        Guid diagnosticId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT ProfileJson FROM {this.diagnosticsTable} WHERE DiagnosticId = @DiagnosticId";
        Add(command, "@DiagnosticId", diagnosticId);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull
            ? null
            : JsonSerializer.Deserialize<WorkProfileSnapshot>((string)value, JsonOptions);
    }

    private async Task<SqlConnection> Open(CancellationToken cancellationToken)
    {
        var connection = new SqlConnection(options.ConnectionString);
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

    private string GetSystemName(WorkSystemId systemId)
        => this.systemNames.TryGetValue(systemId, out var name)
            ? name
            : throw new InvalidOperationException($"Execution diagnostics repository was not initialized for system '{systemId}'.");

    private static string NormalizeSystemName(string? value) => value ?? string.Empty;

    private static string? DenormalizeSystemName(string value) => value.Length == 0 ? null : value;

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? NullableString(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static bool IsStoreUnavailable(SqlException exception)
        => exception.Number is -2 or 2 or 53 or 64 or 233 or 4060 or 18456 ||
            exception.Class >= 20;

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }
}
