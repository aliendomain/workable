# Concurrency Configuration

Concurrency configuration controls how many workers can occupy capacity at the same time.

For configuration source order, precedence, and override rules that apply to every configuration facet, see [Work Configuration](README.md).

Capacity is checked per configured scope, so unrelated work groups do not block each other. Workers without concurrency enabled do not participate in concurrency coordination.

## Settings

| Setting | Default | Description |
| --- | --- | --- |
| `IsEnabled` | `false` | Enables concurrency coordination for the work definition. |
| `MaximumCapacity` | `0` | Maximum number of workers allowed by the configured scope. Required and greater than zero when concurrency is enabled. |
| `Scope` | `WorkConcurrencyScope.PerDefinition` | Capacity grouping. `PerDefinition` groups by work definition. `PerSubject` groups by `WorkSubjectId`. `PerConcurrencyKey` groups by `WorkConcurrencyKey`. |
| `BlockingMode` | `WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed` | Decides which worker states hold capacity. The default counts running, pausing, interrupting, canceling, paused, and failed workers. |
| `LimitReachedBehavior` | `WorkConcurrencyLimitReachedBehavior.Ignore` | Behavior when a queue request reaches capacity. `Ignore` rejects the queue request. `DeferStart` accepts the worker and leaves it queued until capacity is available. |
| `OverrideBehavior` | `WorkConcurrencyOverrideBehavior.Flexible` | Behavior for manual start actions. `Flexible` allows a manual start to override capacity. `Strict` requires capacity even for manual start. |

Concurrency is part of coordination configuration. `WorkCoordinationConfiguration.Storage` decides where all enabled coordination features run. `Local` coordinates inside one work system process. `Persistent` coordinates through the configured persistence integration.

When `Scope` is `PerSubject`, queue input must include a `WorkSubjectId`. When `Scope` is `PerConcurrencyKey`, queue input must include a `WorkConcurrencyKey`.

## Attribute Configuration

`WorkConcurrencyAttribute` declares default concurrency behavior on the executor type.

```csharp
[WorkConcurrency(
    isEnabled: true,
    maximumCapacity: 1,
    scope: WorkConcurrencyScope.PerConcurrencyKey,
    blockingMode: WorkConcurrencyBlockingMode.WhileExecuting,
    limitReachedBehavior: WorkConcurrencyLimitReachedBehavior.DeferStart,
    overrideBehavior: WorkConcurrencyOverrideBehavior.Strict)]
public sealed class RefreshCacheWork : IWorkExecutor
{
    public Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());
}
```

## Startup Configuration

At startup, the same behavior can also be configured with the convenience method `LimitConcurrency` or the full `UseCoordination` setter.

```csharp
services.AddWorkableSystem(builder =>
{
    builder.AddWork<RefreshCacheWork>(
        WorkDefinition.Create(
            name: "cache.refresh",
            description: "Refreshes cached data.",
            category: "Cache"),
        configuration => configuration.UseCoordination(
            new WorkCoordinationConfiguration
            {
                IsEnabled = true,
                Storage = WorkCoordinationStorage.Local,
                Concurrency = new WorkConcurrencyConfiguration
                {
                    IsEnabled = true,
                    MaximumCapacity = 1,
                    Scope = WorkConcurrencyScope.PerConcurrencyKey,
                    BlockingMode = WorkConcurrencyBlockingMode.WhileExecuting,
                    LimitReachedBehavior = WorkConcurrencyLimitReachedBehavior.DeferStart,
                    OverrideBehavior = WorkConcurrencyOverrideBehavior.Strict,
                },
            }));
});
```

Persistence-backed concurrency is intentionally narrower. When `Storage` is `Persistent`, Workable currently requires durable queueing, `BlockingMode = WhileExecuting`, and `LimitReachedBehavior = DeferStart`.

## Queue-Time Configuration

```csharp
var handle = await system.Queue.Enqueue(
    "cache.refresh",
    options: new WorkerOptions(
        Configuration: WorkConfiguration.Default with
        {
            Coordination = WorkCoordinationConfiguration.Default with
            {
                IsEnabled = true,
                Concurrency = WorkConcurrencyConfiguration.Default with
                {
                    IsEnabled = true,
                    MaximumCapacity = 2,
                    LimitReachedBehavior = WorkConcurrencyLimitReachedBehavior.DeferStart,
                },
            },
        }));
```

## Reconfiguration

```csharp
var worker = await system.Query.Worker(workerId)
    ?? throw new InvalidOperationException("Worker was not found.");

var outcome = await system.Workers.Reconfigure(
    worker.Version,
    new WorkerReconfiguration(
        Coordination: WorkCoordinationConfiguration.Default with
        {
            IsEnabled = true,
            Concurrency = WorkConcurrencyConfiguration.Default with
            {
                IsEnabled = true,
                MaximumCapacity = 1,
                LimitReachedBehavior = WorkConcurrencyLimitReachedBehavior.DeferStart,
                OverrideBehavior = WorkConcurrencyOverrideBehavior.Strict,
            },
        }));
```

## Related Interactions

- [Recurrence And Concurrency](interactions.md#recurrence-and-concurrency): waiting recurring workers hold concurrency capacity.
- [Start And Concurrency](interactions.md#start-and-concurrency): concurrency can delay start-policy queue waits.
- [Durable Queue And Concurrency](interactions.md#durable-queue-and-concurrency): persistence-backed concurrency is enforced when durable rows are claimed.
