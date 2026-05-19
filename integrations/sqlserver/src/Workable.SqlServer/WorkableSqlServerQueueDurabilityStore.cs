using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace Workable.SqlServer;

internal sealed class WorkableSqlServerQueueDurabilityStore(WorkableSqlServerQueueDurabilityOptions options) : IWorkQueueDurabilityStore
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
        Converters =
        {
            new JsonStringEnumConverter(),
            new IReadOnlySetJsonConverterFactory(),
        },
    };

    private readonly string entriesTable = $"{WorkableSqlServerSchema.QuoteIdentifier(options.SchemaName)}.[WorkEntries]";

    public async Task Initialize(WorkQueueDurabilityInitializationContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            if (options.AutoDeploySchema)
            {
                await WorkableSqlServerSchema.Apply(options.ConnectionString, options.SchemaName, cancellationToken);
                await WorkableSqlServerSchema.ValidateInstalled(options.ConnectionString, options.SchemaName, cancellationToken);
                return;
            }

            await WorkableSqlServerSchema.ValidateInstalled(options.ConnectionString, options.SchemaName, cancellationToken);
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            var action = options.AutoDeploySchema ? "deploy" : "validate";
            throw new WorkableSqlServerSchemaDeploymentException(
                $"Workable.SqlServer could not {action} schema '{options.SchemaName}'.",
                exception);
        }
    }

    public async Task Enqueue(WorkQueueDurabilityEnqueueRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Transaction is WorkableSqlServerQueueDurabilityTransaction existing)
        {
            await ApplyRequiredDmlSetOptions(existing.Connection, existing.Transaction, cancellationToken);
            await Insert(request, existing.Connection, existing.Transaction, cancellationToken);
            return;
        }

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await Insert(request, connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task ReserveIdempotency(WorkIdempotencyPersistenceRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Transaction is WorkableSqlServerQueueDurabilityTransaction existing)
        {
            await ApplyRequiredDmlSetOptions(existing.Connection, existing.Transaction, cancellationToken);
            await InsertIdempotencyReservation(request, existing.Connection, existing.Transaction, cancellationToken);
            return;
        }

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await InsertIdempotencyReservation(request, connection, transaction, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async IAsyncEnumerable<WorkQueueDurabilityEntry> ClaimReady(
        WorkQueueDurabilityClaimRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        var leaseId = Guid.NewGuid().ToString("N");
        var expiresAt = DateTimeOffset.UtcNow.Add(request.LeaseDuration);
        await using var command = connection.CreateCommand();
        command.CommandText = RequiredDmlSetOptions + $"""
DECLARE @Claimed TABLE
(
    WorkerId uniqueidentifier NOT NULL,
    DefinitionName nvarchar(450) NOT NULL,
    InputJson nvarchar(max) NULL,
    OptionsJson nvarchar(max) NULL,
    ConfigurationJson nvarchar(max) NULL,
    OriginJson nvarchar(max) NOT NULL,
    CreatedAt datetimeoffset NOT NULL
);

DECLARE @LockResult int;
DECLARE @Now datetimeoffset = SYSDATETIMEOFFSET();
DECLARE @RequiresClaimLock bit = 0;

BEGIN TRY
    BEGIN TRANSACTION;

    SELECT @RequiresClaimLock = CASE
        WHEN EXISTS
        (
            SELECT 1
            FROM {this.entriesTable} entries WITH (READPAST)
            WHERE entries.WorkSystemName = @WorkSystemName
              AND entries.IsDurableQueued = 1
              AND (entries.LeaseExpiresAt IS NULL OR entries.LeaseExpiresAt <= @Now)
              AND JSON_VALUE(entries.ConfigurationJson, '$.concurrency.isEnabled') = 'true'
              AND JSON_VALUE(entries.ConfigurationJson, '$.concurrency.storage') = 'Persistence'
        )
        THEN 1
        ELSE 0
    END;

    IF @RequiresClaimLock = 0
    BEGIN
        ;WITH ready AS
            (
                SELECT TOP (@BatchSize) *
                FROM {this.entriesTable} WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE WorkSystemName = @WorkSystemName
                  AND IsDurableQueued = 1
                  AND (LeaseExpiresAt IS NULL OR LeaseExpiresAt <= @Now)
                ORDER BY CreatedAt, WorkerId
            )
        UPDATE ready
        SET ClaimedBy = @OwnerId,
            ClaimedAt = @Now,
            LeaseId = @LeaseId,
            LeaseExpiresAt = @LeaseExpiresAt
        OUTPUT inserted.WorkerId,
               inserted.DefinitionName,
               inserted.InputJson,
               inserted.OptionsJson,
               inserted.ConfigurationJson,
               inserted.OriginJson,
               inserted.CreatedAt
        INTO @Claimed;

        COMMIT TRANSACTION;
    END;
    ELSE
    BEGIN
        EXEC @LockResult = sp_getapplock
            @Resource = @ClaimLockResource,
            @LockMode = 'Exclusive',
            @LockOwner = 'Transaction',
            @LockTimeout = 10000;

        IF @LockResult < 0
        BEGIN
            THROW 51000, 'Workable.SqlServer could not acquire the durable queue claim lock.', 1;
        END;

;WITH candidates AS
    (
        SELECT entries.*,
            CASE
                WHEN JSON_VALUE(entries.ConfigurationJson, '$.concurrency.isEnabled') = 'true'
                 AND JSON_VALUE(entries.ConfigurationJson, '$.concurrency.storage') = 'Persistence'
                THEN 1
                ELSE 0
            END AS HasPersistenceConcurrency,
            TRY_CONVERT(int, JSON_VALUE(entries.ConfigurationJson, '$.concurrency.maximumCapacity')) AS ConcurrencyMaximumCapacity,
            JSON_VALUE(entries.ConfigurationJson, '$.concurrency.scope') AS ConcurrencyScope
        FROM {this.entriesTable} entries WITH (UPDLOCK, READPAST, ROWLOCK)
        WHERE entries.WorkSystemName = @WorkSystemName
          AND entries.IsDurableQueued = 1
          AND (entries.LeaseExpiresAt IS NULL OR entries.LeaseExpiresAt <= @Now)
          AND
          (
              @RequiresClaimLock = 1
              OR CASE
                  WHEN JSON_VALUE(entries.ConfigurationJson, '$.concurrency.isEnabled') = 'true'
                   AND JSON_VALUE(entries.ConfigurationJson, '$.concurrency.storage') = 'Persistence'
                  THEN 1
                  ELSE 0
              END = 0
          )
    ),
ranked AS
    (
        SELECT candidates.*,
            ROW_NUMBER() OVER
            (
                PARTITION BY
                    candidates.WorkSystemName,
                    candidates.DefinitionName,
                    candidates.ConcurrencyScope,
                    CASE WHEN candidates.ConcurrencyScope = 'PerSubject' THEN candidates.SubjectType ELSE NULL END,
                    CASE WHEN candidates.ConcurrencyScope = 'PerSubject' THEN candidates.SubjectValue ELSE NULL END,
                    CASE WHEN candidates.ConcurrencyScope = 'PerConcurrencyKey' THEN candidates.ConcurrencyType ELSE NULL END,
                    CASE WHEN candidates.ConcurrencyScope = 'PerConcurrencyKey' THEN candidates.ConcurrencyValue ELSE NULL END
                ORDER BY candidates.CreatedAt, candidates.WorkerId
            ) AS ConcurrencyRank,
            (
                SELECT COUNT(*)
                FROM {this.entriesTable} active WITH (UPDLOCK, HOLDLOCK)
                WHERE active.WorkSystemName = candidates.WorkSystemName
                  AND active.DefinitionName = candidates.DefinitionName
                  AND active.ConcurrencyBucket = N'Executing'
                  AND active.LeaseExpiresAt > @Now
                  AND
                  (
                      candidates.ConcurrencyScope = 'PerDefinition'
                      OR
                      (
                          candidates.ConcurrencyScope = 'PerSubject'
                          AND active.SubjectType = candidates.SubjectType
                          AND active.SubjectValue = candidates.SubjectValue
                      )
                      OR
                      (
                          candidates.ConcurrencyScope = 'PerConcurrencyKey'
                          AND active.ConcurrencyType = candidates.ConcurrencyType
                          AND active.ConcurrencyValue = candidates.ConcurrencyValue
                      )
                  )
            ) AS ActiveConcurrencyCount
        FROM candidates
    ),
ready AS
    (
        SELECT TOP (@BatchSize)
            ranked.WorkerId,
            ranked.HasPersistenceConcurrency
        FROM ranked
        WHERE ranked.HasPersistenceConcurrency = 0
           OR
           (
               ranked.ConcurrencyMaximumCapacity > 0
               AND ranked.ActiveConcurrencyCount + ranked.ConcurrencyRank <= ranked.ConcurrencyMaximumCapacity
           )
        ORDER BY ranked.CreatedAt, ranked.WorkerId
)
UPDATE entries
SET ClaimedBy = @OwnerId,
    ClaimedAt = @Now,
    LeaseId = @LeaseId,
    LeaseExpiresAt = @LeaseExpiresAt,
    ConcurrencyBucket = CASE
        WHEN ready.HasPersistenceConcurrency = 1 THEN N'Executing'
        ELSE ConcurrencyBucket
    END
OUTPUT inserted.WorkerId,
       inserted.DefinitionName,
       inserted.InputJson,
       inserted.OptionsJson,
       inserted.ConfigurationJson,
       inserted.OriginJson,
       inserted.CreatedAt
INTO @Claimed
FROM {this.entriesTable} entries
INNER JOIN ready
    ON ready.WorkerId = entries.WorkerId;

        COMMIT TRANSACTION;
    END;
END TRY
BEGIN CATCH
    IF @@TRANCOUNT > 0
    BEGIN
        ROLLBACK TRANSACTION;
    END;

    THROW;
END CATCH;

SELECT WorkerId,
       DefinitionName,
       InputJson,
       OptionsJson,
       ConfigurationJson,
       OriginJson,
       CreatedAt
FROM @Claimed
ORDER BY CreatedAt, WorkerId;
""";
        Add(command, "@BatchSize", request.BatchSize);
        Add(command, "@WorkSystemName", NormalizeWorkSystemName(request.WorkSystemName));
        Add(command, "@OwnerId", request.OwnerId);
        Add(command, "@LeaseId", leaseId);
        Add(command, "@LeaseExpiresAt", expiresAt);
        Add(command, "@ClaimLockResource", $"WorkableQueueClaim:{NormalizeWorkSystemName(request.WorkSystemName)}");

        var entries = new List<WorkQueueDurabilityEntry>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var workerId = new WorkerId(reader.GetGuid(0));
                entries.Add(new WorkQueueDurabilityEntry(
                    new WorkQueueDurabilityLease(workerId, request.OwnerId, leaseId),
                    reader.GetString(1),
                    Deserialize<WorkInput>(reader, 2),
                    Deserialize<WorkerOptions>(reader, 3) ?? WorkerOptions.Default,
                    Deserialize<WorkConfiguration>(reader, 4) ?? WorkConfiguration.Default,
                    Deserialize<WorkOrigin>(reader, 5) ?? WorkOrigin.Create(WorkInvocationChannel.DotNet, description: "Durable queue replay."),
                    reader.GetFieldValue<DateTimeOffset>(6)));
            }
        }

        foreach (var entry in entries)
        {
            yield return entry;
        }
    }

    public Task RenewLeases(
        IReadOnlyList<WorkQueueDurabilityLease> leases,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        if (leases.Count == 0)
        {
            return Task.CompletedTask;
        }

        return ExecuteOwned(RequiredDmlSetOptions + $"""
DECLARE @SubmittedLeases TABLE
(
    WorkerId uniqueidentifier NOT NULL,
    LeaseId nvarchar(64) NOT NULL
);

INSERT INTO @SubmittedLeases (WorkerId, LeaseId)
SELECT WorkerId, LeaseId
FROM OPENJSON(@LeasesJson)
WITH
(
    WorkerId uniqueidentifier '$.workerId',
    LeaseId nvarchar(64) '$.leaseId'
);

DECLARE @RenewedLeases TABLE
(
    WorkerId uniqueidentifier NOT NULL,
    LeaseId nvarchar(64) NOT NULL
);

UPDATE entries
SET LeaseExpiresAt = @LeaseExpiresAt
OUTPUT inserted.WorkerId, inserted.LeaseId INTO @RenewedLeases
FROM {this.entriesTable} entries
INNER JOIN @SubmittedLeases leases
    ON leases.WorkerId = entries.WorkerId
   AND leases.LeaseId = entries.LeaseId;

SELECT submitted.WorkerId, submitted.LeaseId
FROM @SubmittedLeases submitted
LEFT JOIN @RenewedLeases renewed
    ON renewed.WorkerId = submitted.WorkerId
   AND renewed.LeaseId = submitted.LeaseId
WHERE renewed.WorkerId IS NULL;
""", async command =>
        {
            var expiresAt = DateTimeOffset.UtcNow.Add(leaseDuration);
            var leasesByKey = leases.ToDictionary(lease => (lease.WorkerId, lease.LeaseId));
            Add(command, "@LeaseExpiresAt", expiresAt);
            Add(command, "@LeasesJson", SerializeRenewalLeases(leases));

            var lostLeases = new List<WorkQueueDurabilityLease>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var workerId = new WorkerId(reader.GetGuid(0));
                var leaseId = reader.GetString(1);
                if (leasesByKey.TryGetValue((workerId, leaseId), out var lease))
                {
                    lostLeases.Add(lease);
                }
            }

            if (lostLeases.Count > 0)
            {
                throw new WorkQueueDurabilityLeaseLostException(lostLeases);
            }
        }, cancellationToken);
    }

    public Task RetainFailed(
        IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
        CancellationToken cancellationToken = default)
    {
        if (workers.Count == 0)
        {
            return Task.CompletedTask;
        }

        return ExecuteOwned(RequiredDmlSetOptions + $"""
DECLARE @CleanupWorkers TABLE
(
    WorkerId uniqueidentifier NOT NULL,
    LeaseId nvarchar(64) NULL
);

INSERT INTO @CleanupWorkers (WorkerId, LeaseId)
SELECT WorkerId, LeaseId
FROM OPENJSON(@WorkersJson)
WITH
(
    WorkerId uniqueidentifier '$.workerId',
    LeaseId nvarchar(64) '$.leaseId'
);

DECLARE @RetainedWorkers TABLE
(
    WorkerId uniqueidentifier NOT NULL,
    LeaseId nvarchar(64) NULL
);

UPDATE entries
SET IsDurableQueued = 0,
    ClaimedBy = NULL,
    ClaimedAt = NULL,
    LeaseId = NULL,
    LeaseExpiresAt = NULL,
    ConcurrencyBucket = NULL
OUTPUT inserted.WorkerId, deleted.LeaseId INTO @RetainedWorkers
FROM {this.entriesTable} entries
INNER JOIN @CleanupWorkers workers
    ON workers.WorkerId = entries.WorkerId
   AND (workers.LeaseId IS NULL OR workers.LeaseId = entries.LeaseId);

SELECT submitted.WorkerId, submitted.LeaseId
FROM @CleanupWorkers submitted
LEFT JOIN @RetainedWorkers retained
    ON retained.WorkerId = submitted.WorkerId
   AND (submitted.LeaseId IS NULL OR retained.LeaseId = submitted.LeaseId)
WHERE submitted.LeaseId IS NOT NULL
  AND retained.WorkerId IS NULL;
""", async command =>
        {
            await ExecuteCleanup(command, workers, cancellationToken);
        }, cancellationToken);
    }

    public Task DeleteFinal(
        IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
        CancellationToken cancellationToken = default)
    {
        if (workers.Count == 0)
        {
            return Task.CompletedTask;
        }

        return ExecuteOwned(RequiredDmlSetOptions + $"""
DECLARE @CleanupWorkers TABLE
(
    WorkerId uniqueidentifier NOT NULL,
    LeaseId nvarchar(64) NULL
);

INSERT INTO @CleanupWorkers (WorkerId, LeaseId)
SELECT WorkerId, LeaseId
FROM OPENJSON(@WorkersJson)
WITH
(
    WorkerId uniqueidentifier '$.workerId',
    LeaseId nvarchar(64) '$.leaseId'
);

DECLARE @DeletedWorkers TABLE
(
    WorkerId uniqueidentifier NOT NULL,
    LeaseId nvarchar(64) NULL
);

DELETE entries
OUTPUT deleted.WorkerId, deleted.LeaseId INTO @DeletedWorkers
FROM {this.entriesTable} entries
INNER JOIN @CleanupWorkers workers
    ON workers.WorkerId = entries.WorkerId
   AND (workers.LeaseId IS NULL OR workers.LeaseId = entries.LeaseId);

SELECT submitted.WorkerId, submitted.LeaseId
FROM @CleanupWorkers submitted
LEFT JOIN @DeletedWorkers deleted
    ON deleted.WorkerId = submitted.WorkerId
   AND (submitted.LeaseId IS NULL OR deleted.LeaseId = submitted.LeaseId)
WHERE submitted.LeaseId IS NOT NULL
  AND deleted.WorkerId IS NULL;
""", async command =>
        {
            await ExecuteCleanup(command, workers, cancellationToken);
        }, cancellationToken);
    }

    public async Task DeleteFinal(
        IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
        IWorkQueueDurabilityTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        if (workers.Count == 0)
        {
            return;
        }

        if (transaction is not WorkableSqlServerQueueDurabilityTransaction sqlServerTransaction)
        {
            throw new InvalidOperationException(
                $"Workable.SqlServer durable completion requires a {nameof(WorkableSqlServerQueueDurabilityTransaction)}.");
        }

        await using var command = sqlServerTransaction.Connection.CreateCommand();
        command.Transaction = sqlServerTransaction.Transaction;
        command.CommandText = RequiredDmlSetOptions + $"""
DECLARE @CleanupWorkers TABLE
(
    WorkerId uniqueidentifier NOT NULL,
    LeaseId nvarchar(64) NULL
);

INSERT INTO @CleanupWorkers (WorkerId, LeaseId)
SELECT WorkerId, LeaseId
FROM OPENJSON(@WorkersJson)
WITH
(
    WorkerId uniqueidentifier '$.workerId',
    LeaseId nvarchar(64) '$.leaseId'
);

DECLARE @DeletedWorkers TABLE
(
    WorkerId uniqueidentifier NOT NULL,
    LeaseId nvarchar(64) NULL
);

DELETE entries
OUTPUT deleted.WorkerId, deleted.LeaseId INTO @DeletedWorkers
FROM {this.entriesTable} entries
INNER JOIN @CleanupWorkers workers
    ON workers.WorkerId = entries.WorkerId
   AND (workers.LeaseId IS NULL OR workers.LeaseId = entries.LeaseId);

SELECT submitted.WorkerId, submitted.LeaseId
FROM @CleanupWorkers submitted
LEFT JOIN @DeletedWorkers deleted
    ON deleted.WorkerId = submitted.WorkerId
   AND (submitted.LeaseId IS NULL OR deleted.LeaseId = submitted.LeaseId)
WHERE submitted.LeaseId IS NOT NULL
  AND deleted.WorkerId IS NULL;
""";
        await ExecuteCleanup(command, workers, cancellationToken);
    }

    private async Task Insert(
        WorkQueueDurabilityEnqueueRequest request,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = RequiredDmlSetOptions + $"""
INSERT INTO {this.entriesTable}
(
    WorkerId,
    WorkSystemName,
    DefinitionName,
    IsDurableQueued,
    HasIdempotencyReservation,
    SubjectType,
    SubjectValue,
    ConcurrencyType,
    ConcurrencyValue,
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
    @IsDurableQueued,
    @HasIdempotencyReservation,
    @SubjectType,
    @SubjectValue,
    @ConcurrencyType,
    @ConcurrencyValue,
    @InputJson,
    @OptionsJson,
    @ConfigurationJson,
    @OriginJson,
    @CreatedAt
);
""";
        Add(command, "@WorkerId", request.WorkerId.Value);
        Add(command, "@WorkSystemName", NormalizeWorkSystemName(request.WorkSystemName));
        Add(command, "@DefinitionName", request.Definition.Name);
        Add(command, "@IsDurableQueued", true);
        Add(command, "@HasIdempotencyReservation", request.Idempotency is not null);
        var subjectId = request.Idempotency?.SubjectId ?? request.Input?.SubjectId;
        Add(command, "@SubjectType", subjectId?.Type);
        Add(command, "@SubjectValue", subjectId?.Value);
        Add(command, "@ConcurrencyType", request.Input?.ConcurrencyKey?.Type);
        Add(command, "@ConcurrencyValue", request.Input?.ConcurrencyKey?.Value);
        Add(command, "@InputJson", Serialize(request.Input));
        Add(command, "@OptionsJson", Serialize(request.Options with { QueueDurabilityTransaction = null }));
        Add(command, "@ConfigurationJson", Serialize(request.Configuration));
        Add(command, "@OriginJson", Serialize(request.Origin));
        Add(command, "@CreatedAt", request.CreatedAt);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            var duplicateSubject = subjectId?.ToString() ?? "<none>";
            throw new WorkQueueDurabilityDuplicateException(
                $"A durable worker for subject '{duplicateSubject}' already exists.");
        }
    }

    private async Task InsertIdempotencyReservation(
        WorkIdempotencyPersistenceRequest request,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = RequiredDmlSetOptions + $"""
INSERT INTO {this.entriesTable}
(
    WorkerId,
    WorkSystemName,
    DefinitionName,
    IsDurableQueued,
    HasIdempotencyReservation,
    SubjectType,
    SubjectValue,
    OriginJson,
    CreatedAt
)
VALUES
(
    @WorkerId,
    @WorkSystemName,
    @DefinitionName,
    @IsDurableQueued,
    @HasIdempotencyReservation,
    @SubjectType,
    @SubjectValue,
    @OriginJson,
    @CreatedAt
);
""";
        Add(command, "@WorkerId", request.WorkerId.Value);
        Add(command, "@WorkSystemName", NormalizeWorkSystemName(request.WorkSystemName));
        Add(command, "@DefinitionName", request.Definition.Name);
        Add(command, "@IsDurableQueued", false);
        Add(command, "@HasIdempotencyReservation", true);
        Add(command, "@SubjectType", request.SubjectId.Type);
        Add(command, "@SubjectValue", request.SubjectId.Value);
        Add(command, "@OriginJson", Serialize(request.Origin));
        Add(command, "@CreatedAt", request.CreatedAt);

        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqlException exception) when (exception.Number is 2601 or 2627)
        {
            throw new WorkQueueDurabilityDuplicateException(
                $"A worker for subject '{request.SubjectId}' already exists.");
        }
    }

    private async Task ExecuteOwned(
        string commandText,
        Action<DbCommand> configure,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        configure(command);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task ExecuteOwned(
        string commandText,
        Func<DbCommand, Task> execute,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await execute(command);
    }

    private static async Task ApplyRequiredDmlSetOptions(
        DbConnection connection,
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = RequiredDmlSetOptions;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string? Serialize<T>(T value)
        => value is null ? null : JsonSerializer.Serialize(value, JsonOptions);

    private static string SerializeRenewalLeases(IReadOnlyList<WorkQueueDurabilityLease> leases)
        => JsonSerializer.Serialize(
            leases.Select(lease => new RenewalLeasePayload(
                lease.WorkerId.Value,
                lease.LeaseId)),
            JsonOptions);

    private static string SerializeCleanupRequests(IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers)
        => JsonSerializer.Serialize(
            workers.Select(worker => new CleanupWorkerPayload(
                worker.WorkerId.Value,
                worker.Lease?.LeaseId)),
            JsonOptions);

    private static async Task ExecuteCleanup(
        DbCommand command,
        IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
        CancellationToken cancellationToken)
    {
        var workersByLease = workers
            .Where(worker => worker.Lease is not null)
            .ToDictionary(worker => (worker.WorkerId, worker.Lease!.LeaseId));
        Add(command, "@WorkersJson", SerializeCleanupRequests(workers));

        var lostLeases = new List<WorkQueueDurabilityLease>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var workerId = new WorkerId(reader.GetGuid(0));
            var leaseId = reader.GetString(1);
            if (workersByLease.TryGetValue((workerId, leaseId), out var worker) &&
                worker.Lease is { } lease)
            {
                lostLeases.Add(lease);
            }
        }

        if (lostLeases.Count > 0)
        {
            throw new WorkQueueDurabilityLeaseLostException(lostLeases);
        }
    }

    private static string NormalizeWorkSystemName(string? workSystemName)
        => string.IsNullOrWhiteSpace(workSystemName) ? "default" : workSystemName;

    private static T? Deserialize<T>(DbDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal)
            ? default
            : JsonSerializer.Deserialize<T>(reader.GetString(ordinal), JsonOptions);

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record RenewalLeasePayload(Guid WorkerId, string LeaseId);

    private sealed record CleanupWorkerPayload(Guid WorkerId, string? LeaseId);

    private sealed class IReadOnlySetJsonConverterFactory : JsonConverterFactory
    {
        public override bool CanConvert(Type typeToConvert)
            => typeToConvert.IsGenericType &&
                typeToConvert.GetGenericTypeDefinition() == typeof(IReadOnlySet<>);

        public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
        {
            var itemType = typeToConvert.GetGenericArguments()[0];
            var converterType = typeof(IReadOnlySetJsonConverter<>).MakeGenericType(itemType);
            return Activator.CreateInstance(converterType) is JsonConverter converter
                ? converter
                : throw new InvalidOperationException($"Could not create converter for {typeToConvert}.");
        }

        private sealed class IReadOnlySetJsonConverter<T> : JsonConverter<IReadOnlySet<T>>
            where T : notnull
        {
            public override IReadOnlySet<T> Read(
                ref Utf8JsonReader reader,
                Type typeToConvert,
                JsonSerializerOptions options)
                => JsonSerializer.Deserialize<HashSet<T>>(ref reader, options) ?? [];

            public override void Write(
                Utf8JsonWriter writer,
                IReadOnlySet<T> value,
                JsonSerializerOptions options)
                => JsonSerializer.Serialize(writer, value.ToArray(), options);
        }
    }
}
