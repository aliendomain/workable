# Retention Configuration

Retention configuration controls how long Workable keeps final workers available after completion or cancellation.

`PurgeInterval` is the retention interval for final workers. Final workers are `Completed` or `Canceled`. When a final worker has not been manually purged, Workable purges it after the configured interval. Failed workers are not final and are handled separately because they can be started again or canceled.

| Setting | Default | Behavior |
| --- | --- | --- |
| `PurgeInterval` | `TimeSpan.FromMinutes(5)` | Retention interval before a completed or canceled worker is automatically purged. Must be greater than zero. |

## Attribute

```
[WorkRetention(purgeIntervalSeconds: 300)]
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
        configuration => configuration.ConfigureRetention(TimeSpan.FromMinutes(5)));
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
            },
        }));
```

## Reconfiguration

```
var worker = await system.Query.GetWorker(workerId)
    ?? throw new InvalidOperationException("Worker was not found.");

var outcome = await system.Workers.Reconfigure(
    worker.Version,
    new WorkerReconfiguration(
        Retention: WorkRetentionConfiguration.Default with
        {
            PurgeInterval = TimeSpan.FromMinutes(10),
        }));
```

## Related Interactions

- [Retention And Failure](work-configuration-interactions.md#retention-and-failure): failed workers are not final and are not automatically purged by final-worker retention.
