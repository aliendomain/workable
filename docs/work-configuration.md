# Work Configuration

Work configuration declares runtime behavior for a work definition. The catalog stores definition defaults, then copies those defaults into each worker as effective worker configuration when work is queued.

## Configuration Sources

Configuration can be supplied in six places. Later sources override earlier sources.

- Workable defaults
- Design-time attributes on `IWorkExecutor` classes
- Bootstrap configuration in `AddWorkableSystem` or `AddWorkableWork`
- Definition default reconfiguration through `IWorkCatalog.Reconfigure`
- Queue-time `WorkerOptions`, including `WorkerOptions.Configuration`
- Runtime `WorkerReconfiguration`

Invocation configuration is definition-level. It can be supplied by attributes, bootstrap configuration, or definition default reconfiguration. Queue-time options and runtime worker reconfiguration do not change which channels may start a work definition.

Queue-time `WorkerOptions` can override worker options and effective worker configuration for a single queued worker. `WorkerOptions.Configuration` is the queue-time configuration override surface.

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

Definition default reconfiguration updates a work definition's `DefaultOptions` and `Configuration`, advances the definition `Revision`, and affects only workers queued after the reconfiguration is accepted. It requires the current `WorkDefinitionVersion`. If another caller changes the definition defaults first, the operation returns a conflict outcome. Definition id, name, category, schemas, metadata, executor, initializers, and automatic start registrations are not changed by definition reconfiguration.

```csharp
WorkDefinition definition = workSystem.Catalog.Definitions
    .Single(definition => definition.Name == "email.welcome.send");

WorkDefinitionReconfigurationOutcome outcome =
    await workSystem.Catalog.Reconfigure(
        definition.Version,
        new WorkDefinitionReconfiguration(
            DefaultOptions: new WorkerOptions(ProfilingEnabled: true),
            Configuration: definition.Configuration with
            {
                Start = WorkStartConfiguration.DoNotStart,
            }),
        cancellationToken);
```

Runtime reconfiguration updates a worker's options and effective configuration, then advances the worker revision. Every reconfiguration call requires the current `WorkerVersion`. If another control operation changes the worker first, the reconfiguration returns a conflict outcome.

Invalid definition default reconfiguration returns `WorkDefinitionReconfigurationStatus.Invalid`. Invalid queue-time configuration returns `WorkQueueStatus.Invalid`. Invalid runtime reconfiguration returns `WorkActionStatus.Invalid`.
