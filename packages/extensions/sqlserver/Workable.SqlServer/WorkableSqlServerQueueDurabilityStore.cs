using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.SqlClient;

namespace Workable.SqlServer;

internal sealed class WorkableSqlServerQueueDurabilityStore(WorkableSqlServerQueueDurabilityOptions options) : IWorkPersistenceStore
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
    private readonly string workflowRunsTable = $"{WorkableSqlServerSchema.QuoteIdentifier(options.SchemaName)}.[WorkflowRuns]";

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
        catch (SqlException exception) when (IsStoreUnavailable(exception))
        {
            var action = options.AutoDeploySchema ? "deploying or validating" : "validating";
            throw new WorkPersistenceStoreUnavailableException(
                $"Workable.SqlServer could not reach SQL Server while {action} schema '{options.SchemaName}'.",
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

    public async Task InitializeWorkflows(
        WorkflowPersistenceInitializationContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (options.AutoDeploySchema)
            {
                await WorkableSqlServerSchema.Apply(options.ConnectionString, options.SchemaName, cancellationToken);
                await WorkableSqlServerSchema.ValidateWorkflowPersistenceInstalled(options.ConnectionString, options.SchemaName, cancellationToken);
                return;
            }

            await WorkableSqlServerSchema.ValidateWorkflowPersistenceInstalled(options.ConnectionString, options.SchemaName, cancellationToken);
        }
        catch (SqlException exception) when (IsStoreUnavailable(exception))
        {
            var action = options.AutoDeploySchema ? "deploying or validating" : "validating";
            throw new WorkPersistenceStoreUnavailableException(
                $"Workable.SqlServer could not reach SQL Server while {action} workflow schema '{options.SchemaName}'.",
                exception);
        }
        catch (Exception exception) when (exception is SqlException or InvalidOperationException)
        {
            var action = options.AutoDeploySchema ? "deploy" : "validate";
            throw new WorkableSqlServerSchemaDeploymentException(
                $"Workable.SqlServer could not {action} workflow schema '{options.SchemaName}'.",
                exception);
        }
    }

    public async Task<IWorkflowPersistenceTransaction> BeginWorkflowTransaction(
        WorkflowPersistenceTransactionRequest request,
        CancellationToken cancellationToken = default)
        => await ExecuteWithStoreUnavailableHandling(
            "beginning a workflow persistence transaction",
            async () =>
            {
                var connection = new SqlConnection(options.ConnectionString);
                try
                {
                    await connection.OpenAsync(cancellationToken);
                    var transaction = await connection.BeginTransactionAsync(cancellationToken);
                    try
                    {
                        await ApplyRequiredDmlSetOptions(connection, transaction, cancellationToken);
                        return (IWorkflowPersistenceTransaction)new WorkableSqlServerWorkflowPersistenceTransaction(connection, transaction);
                    }
                    catch
                    {
                        await transaction.DisposeAsync();
                        await connection.DisposeAsync();
                        throw;
                    }
                }
                catch
                {
                    await connection.DisposeAsync();
                    throw;
                }
            });

    public async IAsyncEnumerable<WorkflowRunPersistenceRecord> ListIncompleteWorkflowRuns(
        WorkflowPersistenceReadRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var runs = await ExecuteWithStoreUnavailableHandling(
            "reading durable workflow runs",
            async () =>
            {
                await using var connection = new SqlConnection(options.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = RequiredDmlSetOptions + $"""
SELECT WorkSystemId,
       WorkSystemName,
       RunId,
       DefinitionId,
       DefinitionRevision,
       DefinitionName,
       DefinitionFingerprint,
       RequestContextJson,
       Status,
       StepsJson,
       CreatedAt,
       StartedAt,
       CompletedAt,
       MessagesJson
FROM {this.workflowRunsTable}
WHERE PersistenceScope = @PersistenceScope
  AND Status = @RunningStatus
ORDER BY CreatedAt, RunId;
""";
                Add(command, "@PersistenceScope", request.PersistenceScope);
                Add(command, "@RunningStatus", WorkflowRunStatus.Running.ToString());

                var runs = new List<WorkflowRunPersistenceRecord>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    runs.Add(new WorkflowRunPersistenceRecord(
                        new WorkSystemId(reader.GetGuid(0)),
                        reader.IsDBNull(1) ? null : reader.GetString(1),
                        new WorkflowRunId(reader.GetGuid(2)),
                        new WorkflowDefinitionVersion(
                            new WorkflowDefinitionId(reader.GetGuid(3)),
                            reader.GetInt64(4)),
                        reader.GetString(5),
                        DeserializeRequestContext(reader, 7),
                        Enum.Parse<WorkflowRunStatus>(reader.GetString(8), ignoreCase: false),
                        Deserialize<WorkflowStepPersistenceRecord[]>(reader, 9) ?? [],
                        reader.GetFieldValue<DateTimeOffset>(10),
                        reader.IsDBNull(11) ? null : reader.GetFieldValue<DateTimeOffset>(11),
                        reader.IsDBNull(12) ? null : reader.GetFieldValue<DateTimeOffset>(12),
                        Deserialize<WorkMessage[]>(reader, 13) ?? [],
                        reader.GetString(6)));
                }

                return runs;
            });

        foreach (var run in runs)
        {
            yield return run;
        }
    }

    public Task UpsertWorkflowRun(
        WorkflowRunPersistenceRecord run,
        CancellationToken cancellationToken = default)
        => this.ExecuteOwnedTransaction(
            "persisting a workflow run",
            (connection, transaction, token) => this.UpsertWorkflowRunCore(run, connection, transaction, token),
            cancellationToken);

    public async Task UpsertWorkflowRun(
        WorkflowRunPersistenceRecord run,
        IWorkflowPersistenceTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        var (connection, sqlTransaction) = GetSqlServerTransaction(transaction);
        await ExecuteWithStoreUnavailableHandling(
            "persisting a workflow run",
            async () =>
            {
                await ApplyRequiredDmlSetOptions(connection, sqlTransaction, cancellationToken);
                await this.UpsertWorkflowRunCore(run, connection, sqlTransaction, cancellationToken);
            });
    }

    public Task DeleteWorkflowRun(
        WorkflowPersistenceDeleteRequest request,
        CancellationToken cancellationToken = default)
        => this.ExecuteOwnedTransaction(
            "deleting a workflow run",
            (connection, transaction, token) => this.DeleteWorkflowRunCore(request, connection, transaction, token),
            cancellationToken);

    public async Task DeleteWorkflowRun(
        WorkflowPersistenceDeleteRequest request,
        IWorkflowPersistenceTransaction transaction,
        CancellationToken cancellationToken = default)
    {
        var (connection, sqlTransaction) = GetSqlServerTransaction(transaction);
        await ExecuteWithStoreUnavailableHandling(
            "deleting a workflow run",
            async () =>
            {
                await ApplyRequiredDmlSetOptions(connection, sqlTransaction, cancellationToken);
                await this.DeleteWorkflowRunCore(request, connection, sqlTransaction, cancellationToken);
            });
    }

    public Task<bool> DurableWorkerExists(
        WorkerId workerId,
        CancellationToken cancellationToken = default)
        => ExecuteWithStoreUnavailableHandling(
            "checking durable worker existence",
            async () =>
            {
                await using var connection = new SqlConnection(options.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = RequiredDmlSetOptions + $"""
SELECT COUNT(*)
FROM {this.entriesTable}
WHERE WorkerId = @WorkerId;
""";
                Add(command, "@WorkerId", workerId.Value);
                return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken)) > 0;
            });

    public async Task Enqueue(WorkQueueDurabilityEnqueueRequest request, CancellationToken cancellationToken = default)
    {
        if (TryGetSqlServerTransaction(request.Transaction, out var existingConnection, out var existingTransaction))
        {
            await ExecuteWithStoreUnavailableHandling(
                "enqueueing durable work",
                async () =>
                {
                    await ApplyRequiredDmlSetOptions(existingConnection!, existingTransaction!, cancellationToken);
                    await Insert(request, existingConnection!, existingTransaction!, cancellationToken);
                });
            return;
        }

        await ExecuteWithStoreUnavailableHandling(
            "enqueueing durable work",
            async () =>
            {
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
            });
    }

    public async Task ReserveIdempotency(WorkIdempotencyPersistenceRequest request, CancellationToken cancellationToken = default)
    {
        if (TryGetSqlServerTransaction(request.Transaction, out var existingConnection, out var existingTransaction))
        {
            await ExecuteWithStoreUnavailableHandling(
                "reserving persistence-backed idempotency",
                async () =>
                {
                    await ApplyRequiredDmlSetOptions(existingConnection!, existingTransaction!, cancellationToken);
                    await InsertIdempotencyReservation(request, existingConnection!, existingTransaction!, cancellationToken);
                });
            return;
        }

        await ExecuteWithStoreUnavailableHandling(
            "reserving persistence-backed idempotency",
            async () =>
            {
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
            });
    }

    public async IAsyncEnumerable<WorkQueueDurabilityEntry> ClaimReady(
        WorkQueueDurabilityClaimRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var entries = await ExecuteWithStoreUnavailableHandling(
            "claiming durable work",
            async () =>
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
              AND JSON_VALUE(entries.ConfigurationJson, '$.coordination.concurrency.isEnabled') = 'true'
              AND JSON_VALUE(entries.ConfigurationJson, '$.coordination.storage') = 'Persistent'
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
                WHEN JSON_VALUE(entries.ConfigurationJson, '$.coordination.concurrency.isEnabled') = 'true'
                 AND JSON_VALUE(entries.ConfigurationJson, '$.coordination.storage') = 'Persistent'
                THEN 1
                ELSE 0
            END AS HasPersistenceConcurrency,
            TRY_CONVERT(int, JSON_VALUE(entries.ConfigurationJson, '$.coordination.concurrency.maximumCapacity')) AS ConcurrencyMaximumCapacity,
            JSON_VALUE(entries.ConfigurationJson, '$.coordination.concurrency.scope') AS ConcurrencyScope
        FROM {this.entriesTable} entries WITH (UPDLOCK, READPAST, ROWLOCK)
        WHERE entries.WorkSystemName = @WorkSystemName
          AND entries.IsDurableQueued = 1
          AND (entries.LeaseExpiresAt IS NULL OR entries.LeaseExpiresAt <= @Now)
          AND
          (
              @RequiresClaimLock = 1
              OR CASE
                  WHEN JSON_VALUE(entries.ConfigurationJson, '$.coordination.concurrency.isEnabled') = 'true'
                   AND JSON_VALUE(entries.ConfigurationJson, '$.coordination.storage') = 'Persistent'
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
                        var requestContext = DeserializeRequestContext(reader, 5);

                        entries.Add(new WorkQueueDurabilityEntry(
                            new WorkQueueDurabilityLease(workerId, request.OwnerId, leaseId),
                            reader.GetString(1),
                            Deserialize<WorkInput>(reader, 2),
                            DeserializeWorkerOptions(reader, 3) ?? WorkerOptions.Default,
                            Deserialize<WorkConfiguration>(reader, 4) ?? WorkConfiguration.Default,
                            requestContext,
                            reader.GetFieldValue<DateTimeOffset>(6)));
                    }
                }

                return entries;
            });

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

        if (!TryGetSqlServerTransaction(transaction, out var connection, out var sqlTransaction))
        {
            throw new InvalidOperationException(
                "Workable.SqlServer durable completion requires a SQL Server durability transaction.");
        }

        await ExecuteWithStoreUnavailableHandling(
            "completing durable work",
            async () =>
            {
                await using var command = connection!.CreateCommand();
                command.Transaction = sqlTransaction!;
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
            });
    }

    private async Task UpsertWorkflowRunCore(
        WorkflowRunPersistenceRecord run,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = RequiredDmlSetOptions + $"""
MERGE {this.workflowRunsTable} WITH (HOLDLOCK) AS target
USING
(
    SELECT
        @RunId AS RunId,
        @PersistenceScope AS PersistenceScope,
        @WorkSystemId AS WorkSystemId,
        @WorkSystemName AS WorkSystemName,
        @DefinitionId AS DefinitionId,
        @DefinitionRevision AS DefinitionRevision,
        @DefinitionName AS DefinitionName,
        @DefinitionFingerprint AS DefinitionFingerprint,
        @RequestContextJson AS RequestContextJson,
        @Status AS Status,
        @StepsJson AS StepsJson,
        @MessagesJson AS MessagesJson,
        @CreatedAt AS CreatedAt,
        @StartedAt AS StartedAt,
        @CompletedAt AS CompletedAt,
        @UpdatedAt AS UpdatedAt
) AS source
ON target.RunId = source.RunId
WHEN MATCHED THEN
    UPDATE SET
        PersistenceScope = source.PersistenceScope,
        WorkSystemId = source.WorkSystemId,
        WorkSystemName = source.WorkSystemName,
        DefinitionId = source.DefinitionId,
        DefinitionRevision = source.DefinitionRevision,
        DefinitionName = source.DefinitionName,
        DefinitionFingerprint = source.DefinitionFingerprint,
        RequestContextJson = source.RequestContextJson,
        Status = source.Status,
        StepsJson = source.StepsJson,
        MessagesJson = source.MessagesJson,
        CreatedAt = source.CreatedAt,
        StartedAt = source.StartedAt,
        CompletedAt = source.CompletedAt,
        UpdatedAt = source.UpdatedAt
WHEN NOT MATCHED THEN
    INSERT
    (
        RunId,
        PersistenceScope,
        WorkSystemId,
        WorkSystemName,
        DefinitionId,
        DefinitionRevision,
        DefinitionName,
        DefinitionFingerprint,
        RequestContextJson,
        Status,
        StepsJson,
        MessagesJson,
        CreatedAt,
        StartedAt,
        CompletedAt,
        UpdatedAt
    )
    VALUES
    (
        source.RunId,
        source.PersistenceScope,
        source.WorkSystemId,
        source.WorkSystemName,
        source.DefinitionId,
        source.DefinitionRevision,
        source.DefinitionName,
        source.DefinitionFingerprint,
        source.RequestContextJson,
        source.Status,
        source.StepsJson,
        source.MessagesJson,
        source.CreatedAt,
        source.StartedAt,
        source.CompletedAt,
        source.UpdatedAt
    );
""";
        Add(command, "@RunId", run.RunId.Value);
        Add(command, "@PersistenceScope", run.PersistenceScope);
        Add(command, "@WorkSystemId", run.WorkSystemId.Value);
        Add(command, "@WorkSystemName", run.WorkSystemName);
        Add(command, "@DefinitionId", run.DefinitionVersion.DefinitionId.Value);
        Add(command, "@DefinitionRevision", run.DefinitionVersion.Revision);
        Add(command, "@DefinitionName", run.DefinitionName);
        Add(command, "@DefinitionFingerprint", run.DefinitionFingerprint);
        Add(command, "@RequestContextJson", Serialize(run.RequestContext));
        Add(command, "@Status", run.Status.ToString());
        Add(command, "@StepsJson", Serialize(run.Steps));
        Add(command, "@MessagesJson", Serialize(run.Messages));
        Add(command, "@CreatedAt", run.CreatedAt);
        Add(command, "@StartedAt", run.StartedAt);
        Add(command, "@CompletedAt", run.CompletedAt);
        Add(command, "@UpdatedAt", DateTimeOffset.UtcNow);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeleteWorkflowRunCore(
        WorkflowPersistenceDeleteRequest request,
        DbConnection connection,
        DbTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = RequiredDmlSetOptions + $"""
DELETE FROM {this.workflowRunsTable}
WHERE RunId = @RunId;
""";
        Add(command, "@RunId", request.RunId.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
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
        Add(command, "@OptionsJson", SerializeWorkerOptions(request.Options with { QueueDurabilityTransaction = null }));
        Add(command, "@ConfigurationJson", Serialize(request.Configuration));
        Add(command, "@OriginJson", Serialize(request.RequestContext));
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
        Add(command, "@OriginJson", Serialize(request.RequestContext));
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
        await ExecuteWithStoreUnavailableHandling(
            "executing a persistence store command",
            async () =>
            {
                await using var connection = new SqlConnection(options.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = commandText;
                configure(command);
                await command.ExecuteNonQueryAsync(cancellationToken);
            });
    }

    private async Task ExecuteOwned(
        string commandText,
        Func<DbCommand, Task> execute,
        CancellationToken cancellationToken)
    {
        await ExecuteWithStoreUnavailableHandling(
            "executing a persistence store command",
            async () =>
            {
                await using var connection = new SqlConnection(options.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = commandText;
                await execute(command);
            });
    }

    private async Task ExecuteOwnedTransaction(
        string operation,
        Func<DbConnection, DbTransaction, CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        await ExecuteWithStoreUnavailableHandling(
            operation,
            async () =>
            {
                await using var connection = new SqlConnection(options.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                try
                {
                    await ApplyRequiredDmlSetOptions(connection, transaction, cancellationToken);
                    await action(connection, transaction, cancellationToken);
                    await transaction.CommitAsync(cancellationToken);
                }
                catch
                {
                    await transaction.RollbackAsync(CancellationToken.None);
                    throw;
                }
            });
    }

    private static async Task ExecuteWithStoreUnavailableHandling(
        string operation,
        Func<Task> action)
    {
        try
        {
            await action();
        }
        catch (SqlException exception) when (IsStoreUnavailable(exception))
        {
            throw new WorkPersistenceStoreUnavailableException(
                $"Workable.SqlServer could not reach SQL Server while {operation}.",
                exception);
        }
    }

    private static async Task<T> ExecuteWithStoreUnavailableHandling<T>(
        string operation,
        Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (SqlException exception) when (IsStoreUnavailable(exception))
        {
            throw new WorkPersistenceStoreUnavailableException(
                $"Workable.SqlServer could not reach SQL Server while {operation}.",
                exception);
        }
    }

    private static bool IsStoreUnavailable(SqlException exception)
        => exception.Number is -2 or 2 or 53 or 64 or 233 or 4060 or 18456 ||
            exception.Class >= 20;

    private static bool TryGetSqlServerTransaction(
        IWorkQueueDurabilityTransaction? transaction,
        out DbConnection? connection,
        out DbTransaction? dbTransaction)
    {
        switch (transaction)
        {
            case WorkableSqlServerQueueDurabilityTransaction queueTransaction:
                connection = queueTransaction.Connection;
                dbTransaction = queueTransaction.Transaction;
                return true;
            case WorkableSqlServerWorkflowPersistenceTransaction workflowTransaction:
                connection = workflowTransaction.Connection;
                dbTransaction = workflowTransaction.Transaction;
                return true;
            default:
                connection = null;
                dbTransaction = null;
                return false;
        }
    }

    private static (DbConnection Connection, DbTransaction Transaction) GetSqlServerTransaction(
        IWorkQueueDurabilityTransaction transaction)
        => TryGetSqlServerTransaction(transaction, out var connection, out var dbTransaction)
            ? (connection!, dbTransaction!)
            : throw new InvalidOperationException("Workable.SqlServer requires a SQL Server durability transaction.");

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

    private static string? SerializeWorkerOptions(WorkerOptions? options)
        => options is null
            ? null
            : Serialize(new PersistedWorkerOptions(
                options.HasExplicitProfilingEnabled ? options.ProfilingEnabled : null,
                options.Configuration));

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

    private static WorkerOptions? DeserializeWorkerOptions(DbDataReader reader, int ordinal)
        => Deserialize<PersistedWorkerOptions>(reader, ordinal)?.ToWorkerOptions();

    private static WorkRequestContext DeserializeRequestContext(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return new WorkRequestContext(WorkOrigin.Create(WorkInvocationChannel.InProcess));
        }

        var json = reader.GetString(ordinal);
        var payload = JsonSerializer.Deserialize<WorkRequestContextPayload>(json, JsonOptions);
        if (payload?.Origin is not null)
        {
            return new WorkRequestContext(
                payload.Origin,
                payload.Description,
                payload.Url,
                payload.Authorization,
                payload.IsAuthenticated);
        }

        return new WorkRequestContext(
            JsonSerializer.Deserialize<WorkOrigin>(json, JsonOptions) ??
            WorkOrigin.Create(WorkInvocationChannel.InProcess));
    }

    private static void Add(DbCommand command, string name, object? value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value ?? DBNull.Value;
        command.Parameters.Add(parameter);
    }

    private sealed record RenewalLeasePayload(Guid WorkerId, string LeaseId);

    private sealed record CleanupWorkerPayload(Guid WorkerId, string? LeaseId);

    private sealed record WorkRequestContextPayload(
        WorkOrigin? Origin,
        string? Description,
        string? Url,
        WorkAuthorizationSnapshot? Authorization,
        bool IsAuthenticated);

    private sealed record PersistedWorkerOptions(
        bool? ProfilingEnabled,
        WorkConfiguration? Configuration)
    {
        public WorkerOptions ToWorkerOptions()
            => this.ProfilingEnabled is { } profilingEnabled
                ? new WorkerOptions(profilingEnabled, this.Configuration)
                : new WorkerOptions(this.Configuration);
    }

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
