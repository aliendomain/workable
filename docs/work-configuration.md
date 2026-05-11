# Work Configuration

Work configuration declares runtime behavior for a work definition. Configuration is immutable on the catalog definition, then copied into each worker as effective worker configuration when work is queued.

## Configuration Sources

Configuration can be supplied in five places. Later sources override earlier sources.

- Workable defaults
- Design-time attributes on `IWorkExecutor` classes
- Bootstrap configuration in `AddWorkableSystem` or `AddWorkableWork`
- Queue-time `WorkerOptions`
- Runtime `WorkerReconfiguration`

Invocation configuration is definition-level. It can be supplied by attributes or bootstrap configuration, but queue-time options and runtime reconfiguration do not change which channels may start a work definition.

Some registration-time behavior is attached with the same fluent builder but is not worker configuration. `WithAutomaticStart` declares startup queue requests for a definition. `WithInitialization` declares initializer services that run before executor invocation.

## Configuration Types

- [Start Configuration](work-configuration-start.md): automatic start behavior and when queue calls return.
- [Idempotency Configuration](work-configuration-idempotency.md): duplicate prevention by `WorkSubjectId`.
- [Recurrence Configuration](work-configuration-recurrence.md): repeated execution, iteration waits, and recurrence circuit behavior.
- [Transient Retry Configuration](work-configuration-transient-retry.md): transient exception classification and retry behavior.
- [Logging Configuration](work-configuration-logging.md): worker-scoped logging behavior.
- [Retention Configuration](work-configuration-retention.md): automatic purge timing for completed and canceled workers.
- [Concurrency Configuration](work-configuration-concurrency.md): capacity limits by definition, subject, or concurrency key.
- [Invocation Configuration](work-configuration-invocation.md): channels allowed to start a work definition.
- [Configuration Interactions](work-configuration-interactions.md): non-obvious behavior when configuration types are combined.

## Runtime Rules

Runtime reconfiguration updates a worker's effective configuration and advances the worker revision. Every reconfiguration call requires the current `WorkerVersion`. If another control operation changes the worker first, the reconfiguration returns a conflict outcome.

Invalid queue-time configuration returns `WorkQueueStatus.Invalid`. Invalid runtime reconfiguration returns `WorkActionStatus.Invalid`.
