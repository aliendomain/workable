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

Those six sources apply in two phases:

- Definition defaults are built from Workable defaults, then attributes, then bootstrap configuration, then definition default reconfiguration.
- Worker-specific state starts from the definition's current defaults when the worker is queued, then applies queue-time `WorkerOptions`, then later applies runtime `WorkerReconfiguration`.

Queue-time and runtime worker overrides merge at the top-level `WorkConfiguration` facet boundary. Supplying `Start`, `Coordination`, `Recurrence`, `TransientRetry`, `Logging`, or `Retention` replaces that whole facet for that worker. Supplying `null` leaves the existing facet unchanged.

Invocation configuration is definition-level. It can be supplied by attributes, bootstrap configuration, or definition default reconfiguration. Queue-time options and runtime worker reconfiguration do not change which channels may start a work definition.

Queue-time `WorkerOptions` can override worker options and effective worker configuration for a single queued worker. `WorkerOptions.Configuration` is the queue-time configuration override surface.

Some registration-time behavior is attached with the same fluent builder but is not worker configuration. `WithAutomaticStart` declares startup queue requests for a definition. `WithInitialization` declares initializer services that run before executor invocation. `ClassifyExceptions` contributes transient-exception classification but does not change the stored `WorkConfiguration` object.

## Configuration Surfaces

Workable exposes configuration in four related shapes:

- `WorkerOptions`: queue-time or definition-default worker options such as `ProfilingEnabled`, `Configuration`, and `QueueDurabilityTransaction`.
- `WorkConfiguration`: per-definition and per-worker runtime behavior with `Start`, `Coordination`, `Recurrence`, `TransientRetry`, `Logging`, `Retention`, and `Invocation`.
- Nested coordination facets inside `WorkCoordinationConfiguration`: `Idempotency`, `Concurrency`, and `Durability`.
- System bootstrap configuration on `IWorkSystemBuilder`: `WorkSystemRetentionConfiguration`, `WorkSystemCapacityConfiguration`, and shutdown grace period settings.

## Configuration Types

`WorkCoordinationConfiguration` controls where Workable keeps coordination state and which coordination protections are enabled for a worker. `IsEnabled` turns coordination on, `Storage` selects `Local` or `Persistent`, and the nested idempotency, concurrency, and durability settings decide whether duplicate protection, capacity limits, durable queueing, or durable completion participate in that mode. `Local` is the default and keeps coordination inside one process. `Persistent` uses a registered persistence store and is required for durable queueing, cross-process idempotency, cross-process concurrency, and durable completion without queue durability.

Worker retention and system retention are configured separately. `WorkRetentionConfiguration.MaximumFinalWorkers` targets retained final workers for one definition. `WorkSystemRetentionConfiguration.MaximumFinalWorkers` is a startup-only system cap across the whole Workable system. When the system cap is reached, a definition may retain fewer final workers than its own worker-level target. See [System Settings](system-settings.md#system-final-worker-cap) for the system-wide cap.

## Direct Fluent Setters

The fluent configuration builder exposes direct `Use*` setters when the caller wants to replace one whole configuration object instead of using convenience helpers such as `RecurEvery`, `RetryTransientFailures`, or `ConfigureLogging`.

`UseCoordination` is the broadest example because it replaces the whole coordination object, including nested idempotency, concurrency, and durability settings:

```csharp
builder.AddWork<ProcessOrderWork>(
    WorkDefinition.Create(
        name: "orders.process",
        description: "Processes an order.",
        category: "Orders"),
    configuration => configuration.UseCoordination(
        new WorkCoordinationConfiguration
        {
            IsEnabled = true,
            Storage = WorkCoordinationStorage.Persistent,
            Idempotency = new WorkIdempotencyConfiguration
            {
                IsEnabled = true,
                ConflictPolicy = WorkIdempotencyConflictPolicy.RejectDuplicates,
            },
            Concurrency = new WorkConcurrencyConfiguration
            {
                IsEnabled = true,
                MaximumCapacity = 1,
                Scope = WorkConcurrencyScope.PerConcurrencyKey,
                BlockingMode = WorkConcurrencyBlockingMode.WhileExecuting,
                LimitReachedBehavior = WorkConcurrencyLimitReachedBehavior.DeferStart,
                OverrideBehavior = WorkConcurrencyOverrideBehavior.Strict,
            },
            Durability = new WorkQueueDurabilityConfiguration
            {
                IsEnabled = true,
                CompleteDurably = true,
                FallbackPollingInterval = TimeSpan.FromSeconds(5),
            },
        }));
```

The rest of the direct setters follow the same pattern:

- `UseStart(new WorkStartConfiguration { ... })`
- `UseRecurrence(new WorkRecurrenceConfiguration { ... })`
- `UseTransientRetry(new WorkTransientRetryConfiguration { ... })`
- `UseLogging(new WorkLoggingConfiguration { ... })`
- `UseRetention(new WorkRetentionConfiguration { ... })`
- `UseInvocation(new WorkInvocationConfiguration { ... })`
- system `UseCapacity(new WorkSystemCapacityConfiguration { ... })`

- [Start Configuration](start.md): automatic start behavior and when queue calls return.
- [Idempotency Configuration](idempotency.md): duplicate prevention by `WorkSubjectId`.
- [Recurrence Configuration](recurrence.md): repeated execution, iteration waits, and recurrence circuit behavior.
- [Transient Retry Configuration](transient-retry.md): transient exception classification and retry behavior.
- [Logging Configuration](logging.md): worker-scoped logging behavior.
- [Retention Configuration](retention.md): automatic purge timing and background count-target cleanup for completed and canceled workers.
- [System Settings](system-settings.md): startup-only system-wide limits for admission capacity and retained final workers.
- [Concurrency Configuration](concurrency.md): capacity limits by definition, subject, or concurrency key.
- [Queue Durability Configuration](queue-durability.md): persist accepted queue requests, recover interrupted durable work, and opt into transaction-bound durable completion.
- [Invocation Configuration](invocation.md): channels allowed to start a work definition.
- [Configuration Interactions](interactions.md): non-obvious behavior when configuration types are combined.

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

`WorkerReconfiguration` can change `ProfilingEnabled` and any worker-level configuration facet except invocation. Invocation remains a definition-level contract even after the worker exists.

Runtime reconfiguration is allowed for any non-final worker. It does not require the worker to be paused or otherwise stopped first. The effect depends on the current worker state and on which facet changed. Some changes take effect immediately, such as concurrency changes that release or reserve capacity, or disabling recurrence for a worker that is currently waiting. Other changes affect the next relevant lifecycle point of the current worker, such as a future retry, recurrence interval, retention decision, or queued start behavior.

Invalid definition default reconfiguration returns `WorkDefinitionReconfigurationStatus.Invalid`. Invalid queue-time configuration returns `WorkQueueStatus.Invalid`. Invalid runtime reconfiguration returns `WorkActionStatus.Invalid`.

Persistent coordination is also validated against the host system. If a work definition, queue override, or worker reconfiguration asks for `WorkCoordinationStorage.Persistent` but the system has no registered persistence store, Workable rejects the operation instead of accepting work that cannot be coordinated safely.
