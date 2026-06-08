namespace Workable;

internal sealed class UnauthorizedWorkSystemDiagnostics(
    WorkSystemId systemId,
    string? systemName) : IWorkSystemDiagnostics
{
    public WorkSystemQueueDiagnostics Queue => throw this.CreateException();

    public WorkSystemReadModelDiagnostics ReadModel => throw this.CreateException();

    public WorkSystemRetentionDiagnostics Retention => throw this.CreateException();

    public WorkSystemConcurrencyDiagnostics Concurrency => throw this.CreateException();

    public WorkSystemDurabilityDiagnostics Durability => throw this.CreateException();

    public WorkSystemIdempotencyDiagnostics Idempotency => throw this.CreateException();

    private WorkSystemAccessDeniedException CreateException()
        => new(WorkSystemPermission.ViewDiagnostics, systemId, systemName);
}
