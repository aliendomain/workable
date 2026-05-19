namespace Workable;

public interface IWorkPersistenceStore
{
    Task Initialize(WorkQueueDurabilityInitializationContext context, CancellationToken cancellationToken = default);

    Task Enqueue(WorkQueueDurabilityEnqueueRequest request, CancellationToken cancellationToken = default);

    Task ReserveIdempotency(WorkIdempotencyPersistenceRequest request, CancellationToken cancellationToken = default);

    IAsyncEnumerable<WorkQueueDurabilityEntry> ClaimReady(
        WorkQueueDurabilityClaimRequest request,
        CancellationToken cancellationToken = default);

    Task RenewLeases(
        IReadOnlyList<WorkQueueDurabilityLease> leases,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task RetainFailed(IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers, CancellationToken cancellationToken = default);

    Task DeleteFinal(IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers, CancellationToken cancellationToken = default);

    Task DeleteFinal(
        IReadOnlyList<WorkQueueDurabilityCleanupRequest> workers,
        IWorkQueueDurabilityTransaction transaction,
        CancellationToken cancellationToken = default);
}

public interface IWorkQueueDurabilityStore : IWorkPersistenceStore;

public sealed record WorkQueueDurabilityInitializationContext(
    WorkSystemId WorkSystemId,
    string? WorkSystemName,
    IReadOnlyList<WorkDefinition> Definitions);

public sealed record WorkQueueDurabilityEnqueueRequest(
    WorkSystemId WorkSystemId,
    string? WorkSystemName,
    WorkerId WorkerId,
    WorkDefinition Definition,
    WorkInput? Input,
    WorkerOptions Options,
    WorkConfiguration Configuration,
    WorkOrigin Origin,
    DateTimeOffset CreatedAt,
    WorkQueueDurabilityIdempotency? Idempotency,
    IWorkQueueDurabilityTransaction? Transaction);

public sealed record WorkQueueDurabilityIdempotency(
    WorkSubjectId SubjectId);

public sealed record WorkIdempotencyPersistenceRequest(
    WorkSystemId WorkSystemId,
    string? WorkSystemName,
    WorkerId WorkerId,
    WorkDefinition Definition,
    WorkSubjectId SubjectId,
    WorkOrigin Origin,
    DateTimeOffset CreatedAt,
    IWorkQueueDurabilityTransaction? Transaction);

public sealed record WorkQueueDurabilityClaimRequest(
    string? WorkSystemName,
    string OwnerId,
    int BatchSize,
    TimeSpan LeaseDuration);

public sealed record WorkQueueDurabilityEntry(
    WorkQueueDurabilityLease Lease,
    string DefinitionName,
    WorkInput? Input,
    WorkerOptions Options,
    WorkConfiguration Configuration,
    WorkOrigin Origin,
    DateTimeOffset CreatedAt);

public sealed record WorkQueueDurabilityLease(
    WorkerId WorkerId,
    string OwnerId,
    string LeaseId);

public sealed record WorkQueueDurabilityCleanupRequest(
    WorkerId WorkerId,
    WorkQueueDurabilityLease? Lease);

public sealed class WorkPersistenceStoreUnavailableException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);

public sealed class WorkQueueDurabilityDuplicateException(string message) : Exception(message);

public sealed class WorkQueueDurabilityLeaseLostException : Exception
{
    public WorkQueueDurabilityLeaseLostException(WorkQueueDurabilityLease lease)
        : this([lease])
    {
    }

    public WorkQueueDurabilityLeaseLostException(IReadOnlyList<WorkQueueDurabilityLease> leases)
        : base(CreateMessage(leases))
    {
        this.Leases = leases;
    }

    public IReadOnlyList<WorkQueueDurabilityLease> Leases { get; }

    private static string CreateMessage(IReadOnlyList<WorkQueueDurabilityLease> leases)
        => leases.Count switch
        {
            0 => "Durable queue lease ownership was lost.",
            1 => $"Durable queue lease ownership was lost for worker '{leases[0].WorkerId}'.",
            _ => $"Durable queue lease ownership was lost for {leases.Count} workers.",
        };
}
