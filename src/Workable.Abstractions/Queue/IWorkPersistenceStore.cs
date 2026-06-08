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
