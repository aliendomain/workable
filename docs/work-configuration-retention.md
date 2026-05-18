# Retention Configuration

Retention configuration controls how many final workers Workable keeps available after completion or cancellation, and how long those final workers may stay available.

`PurgeInterval` is the maximum retention age for final workers. Worker-level `MaximumFinalWorkers` is an asynchronously enforced retained final-worker target for a work definition. System-level `MaximumFinalWorkers` is an asynchronously enforced retained final-worker target across the whole system. Final workers are `Completed` or `Canceled`. When a final worker has not been manually purged, Workable purges it after the configured interval, when the work definition is above its retained final-worker target, or when the system is above its retained final-worker target. Failed and interrupted workers are not final and are handled separately because they can still require retry, replay, inspection, or explicit cancellation.

| Worker setting | Default | Behavior |
| --- | --- | --- |
| `PurgeInterval` | `TimeSpan.FromMinutes(10)` | Retention interval before a completed or canceled worker is automatically purged. Must be greater than zero. |
| `MaximumFinalWorkers` | `1_000` | Target retained completed or canceled workers per work definition. Count retention runs in the background, so brief overages are expected under load. Cleanup can purge any final workers from that definition. Must be greater than zero. |

| System setting | Default | Behavior |
| --- | --- | --- |
| `MaximumFinalWorkers` | `10_000` | Target retained completed or canceled workers across the whole system. Count retention runs in the background, so brief overages are expected under load. Cleanup can purge any final workers in the system. Must be greater than zero. |

## Attribute

```
[WorkRetention(purgeIntervalSeconds: 600, maximumFinalWorkers: 1_000)]
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
        configuration => configuration.ConfigureRetention(
            purgeInterval: TimeSpan.FromMinutes(10),
            maximumFinalWorkers: 1_000));

    builder.ConfigureRetention(maximumFinalWorkers: 10_000);
});
```

## Queue Override

```
var handle = await system.Queue.Enqueue(
    "cache.refresh",
    options: new WorkerOptions(
        Configuration: WorkConfiguration.Default with
        {
            Retention = WorkRetentionConfiguration.Default with
            {
                PurgeInterval = TimeSpan.FromMinutes(2),
                MaximumFinalWorkers = 500,
            },
        }));
```

## Reconfiguration

```
var worker = await system.Query.Worker(workerId)
    ?? throw new InvalidOperationException("Worker was not found.");

var outcome = await system.Workers.Reconfigure(
    worker.Version,
    new WorkerReconfiguration(
        Retention: WorkRetentionConfiguration.Default with
        {
            PurgeInterval = TimeSpan.FromMinutes(10),
            MaximumFinalWorkers = 1_000,
        }));
```

## Related Interactions

- [Retention And Failure](work-configuration-interactions.md#retention-and-failure): failed and interrupted workers are not final and are not automatically purged by final-worker retention.
- [System Capacity Configuration](work-system-capacity.md): system capacity rejects new queue requests when the approximate non-final worker record count is at capacity. Retained completed and canceled workers do not block admission, but still consume memory while retained.
