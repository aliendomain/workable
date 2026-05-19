namespace Workable;

public interface IWorkSystemDiagnostics
{
    WorkSystemQueueDiagnostics Queue { get; }

    WorkSystemReadModelDiagnostics ReadModel { get; }

    WorkSystemRetentionDiagnostics Retention { get; }

    WorkSystemConcurrencyDiagnostics Concurrency { get; }

    WorkSystemDurabilityDiagnostics Durability { get; }

    WorkSystemIdempotencyDiagnostics Idempotency { get; }
}
