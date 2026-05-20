namespace Workable;

internal sealed class SessionWorkSystemDiagnostics(
    WorkSystemDiagnostics inner,
    WorkRequestContext requestContext) : IWorkSystemDiagnostics
{
    public WorkRequestContext RequestContext { get; } = requestContext;

    public WorkSystemQueueDiagnostics Queue => inner.Queue;

    public WorkSystemReadModelDiagnostics ReadModel => inner.ReadModel;

    public WorkSystemRetentionDiagnostics Retention => inner.Retention;

    public WorkSystemConcurrencyDiagnostics Concurrency => inner.Concurrency;

    public WorkSystemDurabilityDiagnostics Durability => inner.Durability;

    public WorkSystemIdempotencyDiagnostics Idempotency => inner.Idempotency;
}
