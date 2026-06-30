namespace Workable;

/// <summary>
/// Persists durable queue state and idempotency reservations for a system.
/// </summary>
public interface IWorkPersistenceStore
{
    /// <summary>
    /// Initializes the persistence store for a system and its registered definitions.
    /// </summary>
    /// <param name="context">The system and definition context to initialize.</param>
    /// <param name="cancellationToken">A token that cancels the initialization operation.</param>
    Task Initialize(WorkQueueDurabilityInitializationContext context, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists a worker for later durable execution.
    /// </summary>
    /// <param name="request">The durable enqueue request.</param>
    /// <param name="cancellationToken">A token that cancels the enqueue operation.</param>
    Task Enqueue(WorkQueueDurabilityEnqueueRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Persists an idempotency reservation for a worker.
    /// </summary>
    /// <param name="request">The idempotency reservation request.</param>
    /// <param name="cancellationToken">A token that cancels the persistence operation.</param>
    Task ReserveIdempotency(WorkIdempotencyPersistenceRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    /// Claims a batch of ready durable queue entries for an owner.
    /// </summary>
    /// <param name="request">The claim request describing ownership and batch limits.</param>
    /// <param name="cancellationToken">A token that cancels the claim operation.</param>
    /// <returns>The durable queue entries whose leases were successfully claimed.</returns>
    IAsyncEnumerable<WorkQueueDurabilityEntry> ClaimReady(
        WorkQueueDurabilityClaimRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Renews ownership leases for previously claimed durable queue entries.
    /// </summary>
    /// <param name="leases">The claimed leases to renew.</param>
    /// <param name="leaseDuration">The new lease duration to apply.</param>
    /// <param name="cancellationToken">A token that cancels the renewal operation.</param>
    Task RenewLeases(
        IReadOnlyList<WorkQueueDurabilityLease> leases,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retains durable queue records for failed workers according to store policy.
    /// </summary>
    /// <param name="workers">The workers whose durable entries should be retained as failed.</param>
    /// <param name="cancellationToken">A token that cancels the retention operation.</param>
    Task RetainFailed(IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes durable queue records for final workers.
    /// </summary>
    /// <param name="workers">The workers whose durable entries should be deleted.</param>
    /// <param name="cancellationToken">A token that cancels the delete operation.</param>
    Task DeleteFinal(IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers, CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes durable queue records for final workers within an existing durability transaction.
    /// </summary>
    /// <param name="workers">The workers whose durable entries should be deleted.</param>
    /// <param name="transaction">The durability transaction to participate in.</param>
    /// <param name="cancellationToken">A token that cancels the delete operation.</param>
    Task DeleteFinal(
        IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
        IWorkQueueDurabilityTransaction transaction,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Determines whether a durable worker entry still exists in the persistence store.
    /// </summary>
    /// <param name="workerId">The worker identifier to check.</param>
    /// <param name="cancellationToken">A token that cancels the existence check.</param>
    /// <returns>A task that returns <see langword="true"/> when the durable worker entry still exists.</returns>
    Task<bool> DurableWorkerExists(
        WorkerId workerId,
        CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    /// <summary>
    /// Initializes workflow persistence support for a system and its registered workflow definitions.
    /// </summary>
    /// <param name="context">The system and workflow definition context to initialize.</param>
    /// <param name="cancellationToken">A token that cancels the initialization operation.</param>
    Task InitializeWorkflows(
        WorkflowPersistenceInitializationContext context,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Begins a workflow persistence transaction for one system.
    /// </summary>
    /// <param name="request">The transaction request describing the target system.</param>
    /// <param name="cancellationToken">A token that cancels the begin operation.</param>
    /// <returns>The workflow persistence transaction.</returns>
    Task<IWorkflowPersistenceTransaction> BeginWorkflowTransaction(
        WorkflowPersistenceTransactionRequest request,
        CancellationToken cancellationToken = default)
        => Task.FromException<IWorkflowPersistenceTransaction>(
            new InvalidOperationException("This persistence store does not support workflow persistence transactions."));

    /// <summary>
    /// Lists durable workflow runs that should be materialized for one system during startup.
    /// </summary>
    /// <param name="request">The read request describing the target system.</param>
    /// <param name="cancellationToken">A token that cancels the read operation.</param>
    /// <returns>The durable workflow runs that should be materialized.</returns>
    IAsyncEnumerable<WorkflowRunPersistenceRecord> ListWorkflowRuns(
        WorkflowPersistenceReadRequest request,
        CancellationToken cancellationToken = default)
        => EmptyWorkflowRuns();

    /// <summary>
    /// Persists the latest snapshot for one workflow run.
    /// </summary>
    /// <param name="run">The workflow run snapshot to persist.</param>
    /// <param name="cancellationToken">A token that cancels the write operation.</param>
    Task UpsertWorkflowRun(
        WorkflowRunPersistenceRecord run,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Persists the latest snapshot for one workflow run inside an existing workflow persistence transaction.
    /// </summary>
    /// <param name="run">The workflow run snapshot to persist.</param>
    /// <param name="transaction">The workflow persistence transaction to participate in.</param>
    /// <param name="cancellationToken">A token that cancels the write operation.</param>
    Task UpsertWorkflowRun(
        WorkflowRunPersistenceRecord run,
        IWorkflowPersistenceTransaction transaction,
        CancellationToken cancellationToken = default)
        => this.UpsertWorkflowRun(run, cancellationToken);

    /// <summary>
    /// Deletes a durable workflow run after it has reached a final state.
    /// </summary>
    /// <param name="request">The delete request for the run.</param>
    /// <param name="cancellationToken">A token that cancels the delete operation.</param>
    Task DeleteWorkflowRun(
        WorkflowPersistenceDeleteRequest request,
        CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>
    /// Deletes a durable workflow run inside an existing workflow persistence transaction.
    /// </summary>
    /// <param name="request">The delete request for the run.</param>
    /// <param name="transaction">The workflow persistence transaction to participate in.</param>
    /// <param name="cancellationToken">A token that cancels the delete operation.</param>
    Task DeleteWorkflowRun(
        WorkflowPersistenceDeleteRequest request,
        IWorkflowPersistenceTransaction transaction,
        CancellationToken cancellationToken = default)
        => this.DeleteWorkflowRun(request, cancellationToken);

    private static async IAsyncEnumerable<WorkflowRunPersistenceRecord> EmptyWorkflowRuns()
    {
        await Task.CompletedTask;
        yield break;
    }
}

/// <summary>
/// Provides system and definition context for persistence-store initialization.
/// </summary>
/// <param name="WorkSystemId">The identifier of the system being initialized.</param>
/// <param name="WorkSystemName">The configured system name, when one exists.</param>
/// <param name="Definitions">The registered definitions known at initialization time.</param>
public sealed record WorkQueueDurabilityInitializationContext(
    WorkSystemId WorkSystemId,
    string? WorkSystemName,
    IReadOnlyList<WorkDefinition> Definitions);

/// <summary>
/// Provides system and workflow-definition context for workflow-persistence initialization.
/// </summary>
/// <param name="WorkSystemName">The configured system name.</param>
/// <param name="Definitions">The registered workflow definitions known at initialization time.</param>
public sealed record WorkflowPersistenceInitializationContext(
    string WorkSystemName,
    IReadOnlyList<WorkflowDefinition> Definitions)
{
    /// <summary>
    /// Gets the workflow persistence scope for this system.
    /// </summary>
    public string PersistenceScope => this.WorkSystemName;
}

/// <summary>
/// Represents a request to begin a workflow persistence transaction for one system.
/// </summary>
/// <param name="WorkSystemName">The configured system name.</param>
public sealed record WorkflowPersistenceTransactionRequest(
    string WorkSystemName)
{
    /// <summary>
    /// Gets the workflow persistence scope for this system.
    /// </summary>
    public string PersistenceScope => this.WorkSystemName;
}

/// <summary>
/// Represents one durable enqueue request.
/// </summary>
/// <param name="WorkSystemId">The identifier of the target system.</param>
/// <param name="WorkSystemName">The configured system name, when one exists.</param>
/// <param name="WorkerId">The worker identifier being enqueued.</param>
/// <param name="Definition">The definition that produced the worker.</param>
/// <param name="Input">The retained input payload, when one exists.</param>
/// <param name="Options">The effective worker options for the worker.</param>
/// <param name="Configuration">The effective work configuration for the worker.</param>
/// <param name="RequestContext">The caller context recorded for the worker.</param>
/// <param name="CreatedAt">The time the worker was created.</param>
/// <param name="Idempotency">The durable idempotency reservation, when one exists.</param>
/// <param name="Transaction">The durability transaction to participate in, when one exists.</param>
public sealed record WorkQueueDurabilityEnqueueRequest(
    WorkSystemId WorkSystemId,
    string? WorkSystemName,
    WorkerId WorkerId,
    WorkDefinition Definition,
    WorkInput? Input,
    WorkerOptions Options,
    WorkConfiguration Configuration,
    WorkRequestContext RequestContext,
    DateTimeOffset CreatedAt,
    WorkQueueDurabilityIdempotency? Idempotency,
    IWorkQueueDurabilityTransaction? Transaction)
{
    /// <summary>
    /// Gets the origin metadata extracted from <see cref="RequestContext"/>.
    /// </summary>
    public WorkOrigin Origin => this.RequestContext.Origin;
}

/// <summary>
/// Represents the durable idempotency reservation associated with a worker.
/// </summary>
/// <param name="SubjectId">The subject identifier reserved for idempotent queueing.</param>
public sealed record WorkQueueDurabilityIdempotency(
    WorkSubjectId SubjectId);

/// <summary>
/// Represents one durable idempotency persistence request.
/// </summary>
/// <param name="WorkSystemId">The identifier of the target system.</param>
/// <param name="WorkSystemName">The configured system name, when one exists.</param>
/// <param name="WorkerId">The worker identifier reserving idempotency.</param>
/// <param name="Definition">The definition that produced the worker.</param>
/// <param name="SubjectId">The subject identifier reserved for idempotent queueing.</param>
/// <param name="RequestContext">The caller context recorded for the worker.</param>
/// <param name="CreatedAt">The time the worker was created.</param>
/// <param name="Transaction">The durability transaction to participate in, when one exists.</param>
public sealed record WorkIdempotencyPersistenceRequest(
    WorkSystemId WorkSystemId,
    string? WorkSystemName,
    WorkerId WorkerId,
    WorkDefinition Definition,
    WorkSubjectId SubjectId,
    WorkRequestContext RequestContext,
    DateTimeOffset CreatedAt,
    IWorkQueueDurabilityTransaction? Transaction)
{
    /// <summary>
    /// Gets the origin metadata extracted from <see cref="RequestContext"/>.
    /// </summary>
    public WorkOrigin Origin => this.RequestContext.Origin;
}

/// <summary>
/// Represents a request to claim ready durable queue entries.
/// </summary>
/// <param name="WorkSystemName">The configured system name, or <see langword="null"/> for the default unnamed system.</param>
/// <param name="OwnerId">The owner identity that will hold the leases.</param>
/// <param name="BatchSize">The maximum number of entries to claim.</param>
/// <param name="LeaseDuration">The lease duration to assign to claimed entries.</param>
public sealed record WorkQueueDurabilityClaimRequest(
    string? WorkSystemName,
    string OwnerId,
    int BatchSize,
    TimeSpan LeaseDuration);

/// <summary>
/// Represents one claimed durable queue entry ready for execution.
/// </summary>
/// <param name="Lease">The claimed lease associated with the entry.</param>
/// <param name="DefinitionName">The registered definition name to execute.</param>
/// <param name="Input">The retained input payload, when one exists.</param>
/// <param name="Options">The effective worker options for the worker.</param>
/// <param name="Configuration">The effective work configuration for the worker.</param>
/// <param name="RequestContext">The caller context recorded for the worker.</param>
/// <param name="CreatedAt">The time the worker was created.</param>
public sealed record WorkQueueDurabilityEntry(
    WorkQueueDurabilityLease Lease,
    string DefinitionName,
    WorkInput? Input,
    WorkerOptions Options,
    WorkConfiguration Configuration,
    WorkRequestContext RequestContext,
    DateTimeOffset CreatedAt)
{
    /// <summary>
    /// Gets the origin metadata extracted from <see cref="RequestContext"/>.
    /// </summary>
    public WorkOrigin Origin => this.RequestContext.Origin;
}

/// <summary>
/// Represents the ownership lease for a durable queue entry.
/// </summary>
/// <param name="WorkerId">The identifier of the leased worker.</param>
/// <param name="OwnerId">The owner that currently holds the lease.</param>
/// <param name="LeaseId">The store-defined lease identifier.</param>
public sealed record WorkQueueDurabilityLease(
    WorkerId WorkerId,
    string OwnerId,
    string LeaseId);

/// <summary>
/// Identifies a durable queue entry that should be cleaned up.
/// </summary>
/// <param name="WorkerId">The identifier of the worker to clean up.</param>
/// <param name="Lease">The lease that must still be owned to clean up the entry, when one exists.</param>
public sealed record WorkQueueDurabilityCleanupRequest(
    WorkerId WorkerId,
    WorkQueueDurabilityLease? Lease);

/// <summary>
/// Represents one durable workflow-run snapshot.
/// </summary>
/// <param name="WorkSystemName">The configured system name, when one exists.</param>
/// <param name="RunId">The workflow run identifier.</param>
/// <param name="DefinitionVersion">The workflow definition version used for the run.</param>
/// <param name="DefinitionName">The workflow definition name.</param>
/// <param name="RequestContext">The caller context recorded for the workflow run.</param>
/// <param name="Status">The current workflow run status.</param>
/// <param name="Steps">The persisted workflow step snapshots.</param>
/// <param name="CreatedAt">The time the workflow run was created.</param>
/// <param name="StartedAt">The time the workflow run started executing, when one exists.</param>
/// <param name="CompletedAt">The time the workflow run reached a final state, when one exists.</param>
/// <param name="Messages">The current workflow run messages.</param>
/// <param name="ChildReceipts">The retained child completion receipts captured for the workflow run.</param>
public sealed record WorkflowRunPersistenceRecord(
    string? WorkSystemName,
    WorkflowRunId RunId,
    WorkflowDefinitionVersion DefinitionVersion,
    string DefinitionName,
    WorkRequestContext RequestContext,
    WorkflowRunStatus Status,
    IReadOnlyList<WorkflowStepPersistenceRecord> Steps,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<WorkMessage> Messages,
    IReadOnlyList<WorkflowChildReceipt> ChildReceipts,
    string DefinitionFingerprint = "",
    string? PendingControlAction = null)
{
    /// <summary>
    /// Gets the origin metadata extracted from <see cref="RequestContext"/>.
    /// </summary>
    public WorkOrigin Origin => this.RequestContext.Origin;

    /// <summary>
    /// Gets the workflow persistence scope for this system.
    /// </summary>
    public string PersistenceScope => this.WorkSystemName
        ?? throw new InvalidOperationException("Durable workflow persistence requires a named Workable system.");
}

/// <summary>
/// Represents one persisted workflow-step snapshot.
/// </summary>
/// <param name="Name">The stable workflow-local step name.</param>
/// <param name="Kind">The workflow step kind.</param>
/// <param name="Status">The current workflow-step status.</param>
/// <param name="WorkerIds">The child worker ids associated with the step.</param>
/// <param name="StartedAt">The time the step started, when one exists.</param>
/// <param name="CompletedAt">The time the step completed, when one exists.</param>
/// <param name="Messages">The current step messages.</param>
public sealed record WorkflowStepPersistenceRecord(
    string Name,
    WorkflowStepKind Kind,
    WorkflowStepRunStatus Status,
    IReadOnlyList<WorkerId> WorkerIds,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<WorkMessage> Messages);

/// <summary>
/// Represents a request to read durable workflow runs for one system during startup materialization.
/// </summary>
/// <param name="WorkSystemName">The configured system name.</param>
public sealed record WorkflowPersistenceReadRequest(
    string WorkSystemName)
{
    /// <summary>
    /// Gets the workflow persistence scope for this system.
    /// </summary>
    public string PersistenceScope => this.WorkSystemName;
}

/// <summary>
/// Identifies one durable workflow run that should be deleted.
/// </summary>
/// <param name="RunId">The identifier of the workflow run to delete.</param>
public sealed record WorkflowPersistenceDeleteRequest(
    WorkflowRunId RunId);

/// <summary>
/// Thrown when the underlying persistence store cannot be reached or used.
/// </summary>
/// <param name="message">The exception message.</param>
/// <param name="innerException">The underlying store-specific failure.</param>
public sealed class WorkPersistenceStoreUnavailableException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);

/// <summary>
/// Thrown when a durable queue write conflicts with an existing unique or idempotent record.
/// </summary>
/// <param name="message">The exception message.</param>
public sealed class WorkQueueDurabilityDuplicateException(string message) : Exception(message);

/// <summary>
/// Thrown when one or more durable queue leases are no longer owned by the caller.
/// </summary>
public sealed class WorkQueueDurabilityLeaseLostException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="WorkQueueDurabilityLeaseLostException"/> class for a single lost lease.
    /// </summary>
    /// <param name="lease">The lease that was lost.</param>
    public WorkQueueDurabilityLeaseLostException(WorkQueueDurabilityLease lease)
        : this([lease])
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="WorkQueueDurabilityLeaseLostException"/> class for multiple lost leases.
    /// </summary>
    /// <param name="leases">The leases that were lost.</param>
    public WorkQueueDurabilityLeaseLostException(IReadOnlyList<WorkQueueDurabilityLease> leases)
        : base(CreateMessage(leases))
    {
        this.Leases = leases;
    }

    /// <summary>
    /// Gets the leases that were no longer owned by the caller.
    /// </summary>
    public IReadOnlyList<WorkQueueDurabilityLease> Leases { get; }

    private static string CreateMessage(IReadOnlyList<WorkQueueDurabilityLease> leases)
        => leases.Count switch
        {
            0 => "Durable queue lease ownership was lost.",
            1 => $"Durable queue lease ownership was lost for worker '{leases[0].WorkerId}'.",
            _ => $"Durable queue lease ownership was lost for {leases.Count} workers.",
        };
}
