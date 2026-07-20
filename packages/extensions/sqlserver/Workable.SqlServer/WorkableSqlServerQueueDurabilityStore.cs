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
    private readonly string queueTable = $"{WorkableSqlServerSchema.QuoteIdentifier(options.SchemaName)}.[WorkQueueEntries]";
    private readonly string workflowRunsTable = $"{WorkableSqlServerSchema.QuoteIdentifier(options.SchemaName)}.[WorkflowRuns]";
    private readonly object enqueueBatchSync = new();
    private readonly List<PendingEnqueue> pendingEnqueues = [];
    private readonly int enqueueBatchSize = options.EnqueueBatchSize;
    private readonly TimeSpan enqueueBatchWindow = options.EnqueueBatchWindow;
    private int scheduledEnqueueBatchFlushes;

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

    public async IAsyncEnumerable<WorkflowRunPersistenceRecord> ListWorkflowRuns(
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
SELECT WorkSystemName,
       RunId,
       DefinitionId,
       DefinitionRevision,
       DefinitionName,
       DefinitionFingerprint,
       RequestContextJson,
       WorkflowInputJson,
       Status,
       StepsJson,
       ChildReceiptsJson,
       PendingControlAction,
       CreatedAt,
       StartedAt,
       CompletedAt,
       MessagesJson
FROM {this.workflowRunsTable}
WHERE PersistenceScope = @PersistenceScope
ORDER BY CreatedAt, RunId;
""";
                Add(command, "@PersistenceScope", request.PersistenceScope);

                var runs = new List<WorkflowRunPersistenceRecord>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    runs.Add(new WorkflowRunPersistenceRecord(
                        reader.IsDBNull(0) ? null : reader.GetString(0),
                        new WorkflowRunId(reader.GetGuid(1)),
                        new WorkflowDefinitionVersion(
                            new WorkflowDefinitionId(reader.GetGuid(2)),
                            reader.GetInt64(3)),
                        reader.GetString(4),
                        Deserialize<WorkInput>(reader, 7),
                        DeserializeRequestContext(reader, 6),
                        Enum.Parse<WorkflowRunStatus>(reader.GetString(8), ignoreCase: false),
                        Deserialize<WorkflowStepPersistenceRecord[]>(reader, 9) ?? [],
                        reader.GetFieldValue<DateTimeOffset>(12),
                        reader.IsDBNull(13) ? null : reader.GetFieldValue<DateTimeOffset>(13),
                        reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14),
                        Deserialize<WorkMessage[]>(reader, 15) ?? [],
                        Deserialize<WorkflowChildReceipt[]>(reader, 10) ?? [],
                        reader.GetString(5),
                        reader.IsDBNull(11) ? null : reader.GetString(11)));
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

    public Task<IReadOnlySet<WorkerId>> DurableWorkersExist(
        IReadOnlyCollection<WorkerId> workerIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workerIds);
        if (workerIds.Count == 0)
        {
            return Task.FromResult<IReadOnlySet<WorkerId>>(new HashSet<WorkerId>());
        }

        return ExecuteWithStoreUnavailableHandling<IReadOnlySet<WorkerId>>(
            "checking durable worker existence in batch",
            async () =>
            {
                await using var connection = new SqlConnection(options.ConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = RequiredDmlSetOptions + $"""
SELECT queue.WorkerId
FROM {this.entriesTable} queue
INNER JOIN OPENJSON(@WorkerIdsJson)
WITH (WorkerId uniqueidentifier '$') requested
    ON requested.WorkerId = queue.WorkerId;
""";
                Add(command, "@WorkerIdsJson", Serialize(workerIds.Select(static workerId => workerId.Value)));

                var existing = new HashSet<WorkerId>();
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    existing.Add(new WorkerId(reader.GetGuid(0)));
                }

                return existing;
            });
    }

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

        await this.EnqueueBatched(request, cancellationToken);
    }

    private async Task EnqueueBatched(
        WorkQueueDurabilityEnqueueRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var pending = new PendingEnqueue(request);
        var shouldScheduleDelayedFlush = false;
        var shouldFlushNow = false;

        lock (this.enqueueBatchSync)
        {
            this.pendingEnqueues.Add(pending);
            if (this.scheduledEnqueueBatchFlushes == 0)
            {
                this.scheduledEnqueueBatchFlushes++;
                shouldScheduleDelayedFlush = true;
            }

            if (this.pendingEnqueues.Count >= this.enqueueBatchSize &&
                this.scheduledEnqueueBatchFlushes < 2)
            {
                this.scheduledEnqueueBatchFlushes++;
                shouldFlushNow = true;
            }
        }

        if (shouldFlushNow)
        {
            this.StartScheduledEnqueueBatchFlush(TimeSpan.Zero);
        }
        else if (shouldScheduleDelayedFlush)
        {
            this.StartScheduledEnqueueBatchFlush(this.enqueueBatchWindow);
        }

        using var registration = cancellationToken.UnsafeRegister(
            static state =>
            {
                var (enqueue, token) = ((PendingEnqueue Pending, CancellationToken Token))state!;
                enqueue.TrySetCanceled(token);
            },
            (pending, cancellationToken));
        await pending.Completion.Task;
    }

    private void StartScheduledEnqueueBatchFlush(TimeSpan delay)
        => _ = Task.Run(async () =>
        {
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay);
            }

            await this.FlushEnqueueBatch();
        });

    private async Task FlushEnqueueBatch()
    {
        var batch = this.TakePendingEnqueueBatch();
        if (batch.Count == 0)
        {
            return;
        }

        try
        {
            await this.ExecutePendingEnqueueBatch(batch);
        }
        catch (Exception exception)
        {
            foreach (var pending in batch)
            {
                pending.TrySetException(exception);
            }
        }
    }

    private List<PendingEnqueue> TakePendingEnqueueBatch()
    {
        var shouldScheduleNextFlush = false;
        List<PendingEnqueue> batch;
        lock (this.enqueueBatchSync)
        {
            if (this.scheduledEnqueueBatchFlushes > 0)
            {
                this.scheduledEnqueueBatchFlushes--;
            }

            if (this.pendingEnqueues.Count == 0)
            {
                return [];
            }

            var count = Math.Min(this.enqueueBatchSize, this.pendingEnqueues.Count);
            batch = this.pendingEnqueues.GetRange(0, count);
            this.pendingEnqueues.RemoveRange(0, count);
            if (this.pendingEnqueues.Count > 0)
            {
                this.scheduledEnqueueBatchFlushes++;
                shouldScheduleNextFlush = true;
            }
        }

        if (shouldScheduleNextFlush)
        {
            this.StartScheduledEnqueueBatchFlush(TimeSpan.Zero);
        }

        return batch;
    }

    private async Task ExecutePendingEnqueueBatch(IReadOnlyList<PendingEnqueue> batch)
    {
        var active = batch.Where(static pending => !pending.Completion.Task.IsCompleted).ToArray();
        if (active.Length == 0)
        {
            return;
        }

        await ExecuteWithStoreUnavailableHandling(
            "enqueueing durable work",
            async () =>
            {
                await using var connection = new SqlConnection(options.ConnectionString);
                await connection.OpenAsync(CancellationToken.None);

                if (active.Length == 1)
                {
                    await Insert(active[0].Request, connection, transaction: null, CancellationToken.None);
                    active[0].TrySetResult();
                    return;
                }

                try
                {
                    await this.InsertBatch(active, connection, CancellationToken.None);
                    foreach (var pending in active)
                    {
                        pending.TrySetResult();
                    }
                }
                catch (SqlException exception) when (exception.Number is 2601 or 2627)
                {
                    await this.InsertBatchIndividually(active, connection, CancellationToken.None);
                }
            });
    }

    private async Task InsertBatchIndividually(
        IReadOnlyList<PendingEnqueue> batch,
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        foreach (var pending in batch)
        {
            if (pending.Completion.Task.IsCompleted)
            {
                continue;
            }

            try
            {
                await Insert(pending.Request, connection, transaction: null, cancellationToken);
                pending.TrySetResult();
            }
            catch (WorkQueueDurabilityDuplicateException exception)
            {
                pending.TrySetException(exception);
            }
        }
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
    WorkerId uniqueidentifier NOT NULL PRIMARY KEY
);

DECLARE @LockResult int;
DECLARE @Now datetimeoffset = SYSDATETIMEOFFSET();
DECLARE @RequiresClaimLock bit = 0;

SELECT @RequiresClaimLock = CASE
	    WHEN EXISTS
	    (
	        SELECT 1
	        FROM {this.queueTable} queue WITH (READPAST)
	        WHERE queue.WorkSystemName = @WorkSystemName
	          AND queue.HasPersistentConcurrency = 1
	          AND (queue.LeaseExpiresAt IS NULL OR queue.LeaseExpiresAt <= @Now)
	    )
	    THEN 1
	    ELSE 0
	END;

IF @RequiresClaimLock = 0
	BEGIN
	    ;WITH ready AS
	        (
	            SELECT TOP (@BatchSize) queue.WorkerId
	            FROM {this.queueTable} queue WITH (UPDLOCK, READPAST, ROWLOCK)
	            WHERE queue.WorkSystemName = @WorkSystemName
	              AND queue.HasPersistentConcurrency = 0
	              AND (queue.LeaseExpiresAt IS NULL OR queue.LeaseExpiresAt <= @Now)
	            ORDER BY queue.DefinitionName, queue.CreatedAt, queue.WorkerId
	        )
	    UPDATE queue
	    SET ClaimedBy = @OwnerId,
	        ClaimedAt = @Now,
	        LeaseId = @LeaseId,
	        LeaseExpiresAt = @LeaseExpiresAt
	    OUTPUT inserted.WorkerId
	    INTO @Claimed
	    FROM {this.queueTable} queue
	    INNER JOIN ready
	        ON ready.WorkerId = queue.WorkerId;
	END
	ELSE
	BEGIN
    BEGIN TRY
        BEGIN TRANSACTION;

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
	        SELECT queue.*
	        FROM {this.queueTable} queue WITH (UPDLOCK, READPAST, ROWLOCK)
	        WHERE queue.WorkSystemName = @WorkSystemName
	          AND (queue.LeaseExpiresAt IS NULL OR queue.LeaseExpiresAt <= @Now)
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
	                FROM {this.queueTable} active WITH (UPDLOCK, HOLDLOCK)
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
	            ranked.HasPersistentConcurrency
	        FROM ranked
	        WHERE ranked.HasPersistentConcurrency = 0
	           OR
	           (
	               ranked.ConcurrencyMaximumCapacity > 0
	               AND ranked.ActiveConcurrencyCount + ranked.ConcurrencyRank <= ranked.ConcurrencyMaximumCapacity
	           )
	        ORDER BY ranked.DefinitionName, ranked.CreatedAt, ranked.WorkerId
	)
	UPDATE queue
	SET ClaimedBy = @OwnerId,
	    ClaimedAt = @Now,
	    LeaseId = @LeaseId,
	    LeaseExpiresAt = @LeaseExpiresAt,
	    ConcurrencyBucket = CASE
	        WHEN ready.HasPersistentConcurrency = 1 THEN N'Executing'
	        ELSE ConcurrencyBucket
	    END
	OUTPUT inserted.WorkerId
	INTO @Claimed
	FROM {this.queueTable} queue
	INNER JOIN ready
	    ON ready.WorkerId = queue.WorkerId;

        COMMIT TRANSACTION;
    END TRY
    BEGIN CATCH
        IF @@TRANCOUNT > 0
        BEGIN
            ROLLBACK TRANSACTION;
        END;

        THROW;
    END CATCH;
END;

	SELECT queue.WorkerId,
	       queue.DefinitionName,
	       entries.InputJson,
	       entries.OptionsJson,
	       entries.ConfigurationJson,
	       entries.OriginJson,
	       queue.CreatedAt
	FROM @Claimed claimed
	INNER JOIN {this.queueTable} queue
	    ON queue.WorkerId = claimed.WorkerId
	INNER JOIN {this.entriesTable} entries
	    ON entries.WorkerId = claimed.WorkerId;
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
                    while (true)
                    {
                        var hasRow = await reader.ReadAsync(cancellationToken);
                        if (!hasRow)
                        {
                            break;
                        }

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

                    entries.Sort(static (left, right) =>
                    {
                        var definitionComparison = string.Compare(
                            left.DefinitionName,
                            right.DefinitionName,
                            StringComparison.Ordinal);
                        if (definitionComparison != 0)
                        {
                            return definitionComparison;
                        }

                        var createdAtComparison = left.CreatedAt.CompareTo(right.CreatedAt);
                        return createdAtComparison != 0
                            ? createdAtComparison
                            : left.Lease.WorkerId.Value.CompareTo(right.Lease.WorkerId.Value);
                    });
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

	UPDATE queue
	SET LeaseExpiresAt = @LeaseExpiresAt
	OUTPUT inserted.WorkerId, inserted.LeaseId INTO @RenewedLeases
	FROM {this.queueTable} queue
	INNER JOIN @SubmittedLeases leases
	    ON leases.WorkerId = queue.WorkerId
	   AND leases.LeaseId = queue.LeaseId;

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

	DELETE queue
	OUTPUT deleted.WorkerId, deleted.LeaseId INTO @RetainedWorkers
	FROM {this.queueTable} queue
	INNER JOIN @CleanupWorkers workers
	    ON workers.WorkerId = queue.WorkerId
	   AND (workers.LeaseId IS NULL OR workers.LeaseId = queue.LeaseId);

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

	DECLARE @DeletedQueueWorkers TABLE
	(
	    WorkerId uniqueidentifier NOT NULL,
	    LeaseId nvarchar(64) NULL
	);

	BEGIN TRY
	    BEGIN TRANSACTION;

	    DELETE queue
	    OUTPUT deleted.WorkerId, deleted.LeaseId INTO @DeletedQueueWorkers
	    FROM {this.queueTable} queue
	    INNER JOIN @CleanupWorkers workers
	        ON workers.WorkerId = queue.WorkerId
	       AND (workers.LeaseId IS NULL OR workers.LeaseId = queue.LeaseId);

	    DELETE entries
	    FROM {this.entriesTable} entries
	    INNER JOIN @CleanupWorkers workers
	        ON workers.WorkerId = entries.WorkerId
	    LEFT JOIN @DeletedQueueWorkers deletedQueue
	        ON deletedQueue.WorkerId = workers.WorkerId
	       AND (workers.LeaseId IS NULL OR deletedQueue.LeaseId = workers.LeaseId)
	    WHERE workers.LeaseId IS NULL
	       OR deletedQueue.WorkerId IS NOT NULL;

	    COMMIT TRANSACTION;
	END TRY
	BEGIN CATCH
	    IF @@TRANCOUNT > 0
	    BEGIN
	        ROLLBACK TRANSACTION;
	    END;

	    THROW;
	END CATCH;

	SELECT submitted.WorkerId, submitted.LeaseId
	FROM @CleanupWorkers submitted
	LEFT JOIN @DeletedQueueWorkers deletedQueue
	    ON deletedQueue.WorkerId = submitted.WorkerId
	   AND deletedQueue.LeaseId = submitted.LeaseId
	WHERE submitted.LeaseId IS NOT NULL
	  AND deletedQueue.WorkerId IS NULL;
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

	DECLARE @DeletedQueueWorkers TABLE
	(
	    WorkerId uniqueidentifier NOT NULL,
	    LeaseId nvarchar(64) NULL
	);

	DELETE queue
	OUTPUT deleted.WorkerId, deleted.LeaseId INTO @DeletedQueueWorkers
	FROM {this.queueTable} queue
	INNER JOIN @CleanupWorkers workers
	    ON workers.WorkerId = queue.WorkerId
	   AND (workers.LeaseId IS NULL OR workers.LeaseId = queue.LeaseId);

	DELETE entries
	FROM {this.entriesTable} entries
	INNER JOIN @CleanupWorkers workers
	    ON workers.WorkerId = entries.WorkerId
	LEFT JOIN @DeletedQueueWorkers deletedQueue
	    ON deletedQueue.WorkerId = workers.WorkerId
	   AND (workers.LeaseId IS NULL OR deletedQueue.LeaseId = workers.LeaseId)
	WHERE workers.LeaseId IS NULL
	   OR deletedQueue.WorkerId IS NOT NULL;

	SELECT submitted.WorkerId, submitted.LeaseId
	FROM @CleanupWorkers submitted
	LEFT JOIN @DeletedQueueWorkers deletedQueue
	    ON deletedQueue.WorkerId = submitted.WorkerId
	   AND deletedQueue.LeaseId = submitted.LeaseId
		WHERE submitted.LeaseId IS NOT NULL
		  AND deletedQueue.WorkerId IS NULL;
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
        @WorkSystemName AS WorkSystemName,
        @DefinitionId AS DefinitionId,
        @DefinitionRevision AS DefinitionRevision,
        @DefinitionName AS DefinitionName,
        @DefinitionFingerprint AS DefinitionFingerprint,
        @RequestContextJson AS RequestContextJson,
        @WorkflowInputJson AS WorkflowInputJson,
        @Status AS Status,
        @StepsJson AS StepsJson,
        @MessagesJson AS MessagesJson,
        @ChildReceiptsJson AS ChildReceiptsJson,
        @PendingControlAction AS PendingControlAction,
        @CreatedAt AS CreatedAt,
        @StartedAt AS StartedAt,
        @CompletedAt AS CompletedAt,
        @UpdatedAt AS UpdatedAt
) AS source
ON target.RunId = source.RunId
WHEN MATCHED THEN
    UPDATE SET
        PersistenceScope = source.PersistenceScope,
        WorkSystemName = source.WorkSystemName,
        DefinitionId = source.DefinitionId,
        DefinitionRevision = source.DefinitionRevision,
        DefinitionName = source.DefinitionName,
        DefinitionFingerprint = source.DefinitionFingerprint,
        RequestContextJson = source.RequestContextJson,
        WorkflowInputJson = source.WorkflowInputJson,
        Status = source.Status,
        StepsJson = source.StepsJson,
        MessagesJson = source.MessagesJson,
        ChildReceiptsJson = source.ChildReceiptsJson,
        PendingControlAction = source.PendingControlAction,
        CreatedAt = source.CreatedAt,
        StartedAt = source.StartedAt,
        CompletedAt = source.CompletedAt,
        UpdatedAt = source.UpdatedAt
WHEN NOT MATCHED THEN
    INSERT
    (
        RunId,
        PersistenceScope,
        WorkSystemName,
        DefinitionId,
        DefinitionRevision,
        DefinitionName,
        DefinitionFingerprint,
        RequestContextJson,
        WorkflowInputJson,
        Status,
        StepsJson,
        MessagesJson,
        ChildReceiptsJson,
        PendingControlAction,
        CreatedAt,
        StartedAt,
        CompletedAt,
        UpdatedAt
    )
    VALUES
    (
        source.RunId,
        source.PersistenceScope,
        source.WorkSystemName,
        source.DefinitionId,
        source.DefinitionRevision,
        source.DefinitionName,
        source.DefinitionFingerprint,
        source.RequestContextJson,
        source.WorkflowInputJson,
        source.Status,
        source.StepsJson,
        source.MessagesJson,
        source.ChildReceiptsJson,
        source.PendingControlAction,
        source.CreatedAt,
        source.StartedAt,
        source.CompletedAt,
        source.UpdatedAt
    );
""";
        Add(command, "@RunId", run.RunId.Value);
        Add(command, "@PersistenceScope", run.PersistenceScope);
        Add(command, "@WorkSystemName", run.WorkSystemName);
        Add(command, "@DefinitionId", run.DefinitionVersion.DefinitionId.Value);
        Add(command, "@DefinitionRevision", run.DefinitionVersion.Revision);
        Add(command, "@DefinitionName", run.DefinitionName);
        Add(command, "@DefinitionFingerprint", run.DefinitionFingerprint);
        Add(command, "@RequestContextJson", Serialize(run.RequestContext));
        Add(command, "@WorkflowInputJson", Serialize(run.Input));
        Add(command, "@Status", run.Status.ToString());
        Add(command, "@StepsJson", Serialize(run.Steps));
        Add(command, "@MessagesJson", Serialize(run.Messages));
        Add(command, "@ChildReceiptsJson", Serialize(run.ChildReceipts));
        Add(command, "@PendingControlAction", run.PendingControlAction);
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
        DbTransaction? transaction,
        CancellationToken cancellationToken)
    {
        if (transaction is null)
        {
            await using var ownedTransaction = await connection.BeginTransactionAsync(cancellationToken);
            try
            {
                await this.Insert(request, connection, ownedTransaction, cancellationToken);
                await ownedTransaction.CommitAsync(cancellationToken);
            }
            catch
            {
                await ownedTransaction.RollbackAsync(CancellationToken.None);
                throw;
            }

            return;
        }

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
    HasPersistentConcurrency,
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
    @HasPersistentConcurrency,
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

	INSERT INTO {this.queueTable}
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
	    CreatedAt
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
	    @CreatedAt
	);
""";
        var hasPersistentConcurrency = request.Configuration.Coordination.IsPersistentConcurrencyEnabled;
        Add(command, "@WorkerId", request.WorkerId.Value);
        Add(command, "@WorkSystemName", NormalizeWorkSystemName(request.WorkSystemName));
        Add(command, "@DefinitionName", request.Definition.Name);
        Add(command, "@IsDurableQueued", false);
        Add(command, "@HasIdempotencyReservation", request.Idempotency is not null);
        Add(command, "@HasPersistentConcurrency", hasPersistentConcurrency);
        Add(command, "@ConcurrencyScope", GetPersistentConcurrencyScope(request, hasPersistentConcurrency));
        Add(command, "@ConcurrencyMaximumCapacity", GetPersistentConcurrencyMaximumCapacity(request, hasPersistentConcurrency));
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

    private async Task InsertBatch(
        IReadOnlyList<PendingEnqueue> batch,
        DbConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = RequiredDmlSetOptions + $"""
	DECLARE @Entries TABLE
	(
	    WorkerId uniqueidentifier NOT NULL,
	    WorkSystemName nvarchar(256) NOT NULL,
	    DefinitionName nvarchar(450) NOT NULL,
	    HasIdempotencyReservation bit NOT NULL,
	    HasPersistentConcurrency bit NOT NULL,
	    ConcurrencyScope nvarchar(64) NULL,
	    ConcurrencyMaximumCapacity int NULL,
	    SubjectType nvarchar(256) NULL,
	    SubjectValue nvarchar(450) NULL,
	    ConcurrencyType nvarchar(256) NULL,
	    ConcurrencyValue nvarchar(450) NULL,
	    InputJson nvarchar(max) NULL,
	    OptionsJson nvarchar(max) NULL,
	    ConfigurationJson nvarchar(max) NULL,
	    OriginJson nvarchar(max) NOT NULL,
	    CreatedAt datetimeoffset NOT NULL
	);

	INSERT INTO @Entries
	(
	    WorkerId,
	    WorkSystemName,
	    DefinitionName,
	    HasIdempotencyReservation,
	    HasPersistentConcurrency,
	    ConcurrencyScope,
	    ConcurrencyMaximumCapacity,
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
	SELECT
	    WorkerId,
	    WorkSystemName,
	    DefinitionName,
	    HasIdempotencyReservation,
	    HasPersistentConcurrency,
	    ConcurrencyScope,
	    ConcurrencyMaximumCapacity,
	    SubjectType,
	    SubjectValue,
	    ConcurrencyType,
	    ConcurrencyValue,
	    InputJson,
	    OptionsJson,
	    ConfigurationJson,
	    OriginJson,
	    CreatedAt
	FROM OPENJSON(@EntriesJson)
	WITH
	(
	    WorkerId uniqueidentifier '$.workerId',
	    WorkSystemName nvarchar(256) '$.workSystemName',
	    DefinitionName nvarchar(450) '$.definitionName',
	    HasIdempotencyReservation bit '$.hasIdempotencyReservation',
	    HasPersistentConcurrency bit '$.hasPersistentConcurrency',
	    ConcurrencyScope nvarchar(64) '$.concurrencyScope',
	    ConcurrencyMaximumCapacity int '$.concurrencyMaximumCapacity',
	    SubjectType nvarchar(256) '$.subjectType',
	    SubjectValue nvarchar(450) '$.subjectValue',
	    ConcurrencyType nvarchar(256) '$.concurrencyType',
	    ConcurrencyValue nvarchar(450) '$.concurrencyValue',
	    InputJson nvarchar(max) '$.inputJson',
	    OptionsJson nvarchar(max) '$.optionsJson',
	    ConfigurationJson nvarchar(max) '$.configurationJson',
	    OriginJson nvarchar(max) '$.originJson',
	    CreatedAt datetimeoffset '$.createdAt'
	);

	INSERT INTO {this.entriesTable}
	(
	    WorkerId,
    WorkSystemName,
    DefinitionName,
    IsDurableQueued,
    HasIdempotencyReservation,
    HasPersistentConcurrency,
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
	SELECT
	    WorkerId,
	    WorkSystemName,
	    DefinitionName,
	    CAST(0 AS bit),
	    HasIdempotencyReservation,
	    HasPersistentConcurrency,
	    SubjectType,
    SubjectValue,
    ConcurrencyType,
    ConcurrencyValue,
    InputJson,
    OptionsJson,
    ConfigurationJson,
	    OriginJson,
	    CreatedAt
	FROM @Entries;

	INSERT INTO {this.queueTable}
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
	    CreatedAt
	)
	SELECT
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
	    CreatedAt
	FROM @Entries;
""";
        Add(command, "@EntriesJson", SerializeEnqueueRequests(batch));
        try
        {
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
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

    private static string SerializeEnqueueRequests(IReadOnlyList<PendingEnqueue> batch)
        => JsonSerializer.Serialize(
            batch.Select(static pending => CreateEnqueuePayload(pending.Request)),
            JsonOptions);

    private static EnqueuePayload CreateEnqueuePayload(WorkQueueDurabilityEnqueueRequest request)
    {
        var subjectId = request.Idempotency?.SubjectId ?? request.Input?.SubjectId;
        var hasPersistentConcurrency = request.Configuration.Coordination.IsPersistentConcurrencyEnabled;
        return new EnqueuePayload(
            request.WorkerId.Value,
            NormalizeWorkSystemName(request.WorkSystemName),
            request.Definition.Name,
            request.Idempotency is not null,
            hasPersistentConcurrency,
            GetPersistentConcurrencyScope(request, hasPersistentConcurrency),
            GetPersistentConcurrencyMaximumCapacity(request, hasPersistentConcurrency),
            subjectId?.Type,
            subjectId?.Value,
            request.Input?.ConcurrencyKey?.Type,
            request.Input?.ConcurrencyKey?.Value,
            Serialize(request.Input),
            SerializeWorkerOptions(request.Options with { QueueDurabilityTransaction = null }),
            Serialize(request.Configuration),
            Serialize(request.RequestContext) ?? throw new InvalidOperationException("Durable enqueue origin payload cannot be null."),
            request.CreatedAt);
    }

    private static string? GetPersistentConcurrencyScope(
        WorkQueueDurabilityEnqueueRequest request,
        bool hasPersistentConcurrency)
        => hasPersistentConcurrency
            ? request.Configuration.Coordination.Concurrency.Scope.ToString()
            : null;

    private static int? GetPersistentConcurrencyMaximumCapacity(
        WorkQueueDurabilityEnqueueRequest request,
        bool hasPersistentConcurrency)
        => hasPersistentConcurrency
            ? request.Configuration.Coordination.Concurrency.MaximumCapacity
            : null;

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

    private sealed record EnqueuePayload(
        Guid WorkerId,
        string WorkSystemName,
        string DefinitionName,
        bool HasIdempotencyReservation,
        bool HasPersistentConcurrency,
        string? ConcurrencyScope,
        int? ConcurrencyMaximumCapacity,
        string? SubjectType,
        string? SubjectValue,
        string? ConcurrencyType,
        string? ConcurrencyValue,
        string? InputJson,
        string? OptionsJson,
        string? ConfigurationJson,
        string OriginJson,
        DateTimeOffset CreatedAt);

    private sealed class PendingEnqueue(WorkQueueDurabilityEnqueueRequest request)
    {
        public WorkQueueDurabilityEnqueueRequest Request { get; } = request;

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void TrySetResult()
            => this.Completion.TrySetResult();

        public void TrySetException(Exception exception)
            => this.Completion.TrySetException(exception);

        public void TrySetCanceled(CancellationToken cancellationToken)
            => this.Completion.TrySetCanceled(cancellationToken);
    }

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
