# Concurrency Configuration

Concurrency configuration controls how many workers can occupy capacity at the same time.

Capacity is checked per configured scope, so unrelated work groups do not block each other. Workers without concurrency enabled do not participate in concurrency coordination.

| Setting | Default | Behavior |
| --- | --- | --- |
| `IsEnabled` | `false` | Enables concurrency coordination for the work definition. |
| `MaximumCapacity` | `0` | Maximum number of workers allowed by the configured scope. Required and greater than zero when concurrency is enabled. |
| `Scope` | `WorkConcurrencyScope.PerDefinition` | Capacity grouping. `PerDefinition` groups by work definition. `PerSubject` groups by `WorkSubjectId`. `PerConcurrencyKey` groups by `WorkConcurrencyKey`. |
| `BlockingMode` | `WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed` | Worker states that count against capacity. The default counts running, pausing, canceling, paused, and failed workers. |
| `LimitReachedBehavior` | `WorkConcurrencyLimitReachedBehavior.Ignore` | Behavior when a queue request reaches capacity. `Ignore` rejects the queue request. `DeferStart` accepts the worker and leaves it queued until capacity is available. |
| `OverrideBehavior` | `WorkConcurrencyOverrideBehavior.Flexible` | Behavior for manual start actions. `Flexible` allows a manual start to override capacity. `Strict` requires capacity even for manual start. |

When `Scope` is `PerSubject`, queue input must include a `WorkSubjectId`. When `Scope` is `PerConcurrencyKey`, queue input must include a `WorkConcurrencyKey`.

## Attribute

```
[WorkConcurrency(
    isEnabled: true,
    maximumCapacity: 1,
    scope: WorkConcurrencyScope.PerDefinition,
    blockingMode: WorkConcurrencyBlockingMode.WhileExecutingPausedOrFailed,
    limitReachedBehavior: WorkConcurrencyLimitReachedBehavior.DeferStart,
    overrideBehavior: WorkConcurrencyOverrideBehavior.Strict)]
public sealed class RefreshCacheWork : IWorkExecutor
{
    public Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(WorkExecutionResult.Success());
    }
}
```

## Bootstrap

```
services.AddWorkableSystem(builder =>
{
    builder.AddWork<RefreshCacheWork>(
        WorkDefinition.Create(
            name: "cache.refresh",
            description: "Refreshes cached data.",
            category: "Cache"),
        configuration => configuration.LimitConcurrency(
            maximumCapacity: 1,
            limitReachedBehavior: WorkConcurrencyLimitReachedBehavior.DeferStart,
            overrideBehavior: WorkConcurrencyOverrideBehavior.Strict));
});
```

## Queue Override

```
var handle = await system.Queue.Enqueue(
    "cache.refresh",
    options: new WorkerOptions(
        Configuration: WorkConfiguration.Default with
        {
            Concurrency = WorkConcurrencyConfiguration.Default with
            {
                IsEnabled = true,
                MaximumCapacity = 2,
                LimitReachedBehavior = WorkConcurrencyLimitReachedBehavior.DeferStart,
            },
        }));
```

## Queue Input

```
var input = WorkInput
    .FromValue(new RefreshCustomerCache("customer-123"))
    .WithSubject(new WorkSubjectId("customer", "customer-123"))
    .WithConcurrencyKey(new WorkConcurrencyKey("tenant", "tenant-456"));

var handle = await system.Queue.Enqueue("cache.refresh", input);
```

## Reconfiguration

```
var worker = await system.Query.GetWorker(workerId)
    ?? throw new InvalidOperationException("Worker was not found.");

var outcome = await system.Workers.Reconfigure(
    worker.Version,
    new WorkerReconfiguration(
        Concurrency: WorkConcurrencyConfiguration.Default with
        {
            IsEnabled = true,
            MaximumCapacity = 1,
            LimitReachedBehavior = WorkConcurrencyLimitReachedBehavior.DeferStart,
            OverrideBehavior = WorkConcurrencyOverrideBehavior.Strict,
        }));
```

## Related Interactions

- [Recurrence And Concurrency](work-configuration-interactions.md#recurrence-and-concurrency): waiting recurring workers hold concurrency capacity.
- [Start And Concurrency](work-configuration-interactions.md#start-and-concurrency): concurrency can delay start-policy queue waits.
