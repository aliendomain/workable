# Retention Configuration

Retention configuration controls how many final workers Workable keeps available after completion or cancellation, and how long those worker records may stay available.

For configuration source order, precedence, and override rules that apply to every configuration facet, see [Work Configuration](README.md).

`PurgeInterval` is the maximum retention age for final workers. Worker-level `MaximumFinalWorkers` is an asynchronously enforced retained final-worker target for a work definition. Final workers are `Completed` or `Canceled`. Failed and interrupted workers are not final and are handled separately because they can still require retry, replay, inspection, or explicit cancellation. When failed-worker auto-cancel is enabled, Workable changes that failed worker state into `Canceled` after the configured delay, and retention then treats it like any other final worker.

## Settings

### Worker Settings

| Setting | Default | Description |
| --- | --- | --- |
| `PurgeInterval` | `TimeSpan.FromMinutes(10)` | Retention interval before a completed or canceled worker is automatically purged. Must be greater than zero. |
| `MaximumFinalWorkers` | `1_000` | Target retained completed or canceled workers for this work definition. Count retention runs in the background, so brief overages are expected under load. Cleanup can purge any final workers from that definition. Actual retained count can still be lower when the Workable system-level final-worker cap is reached. See [System Settings](system-settings.md#system-final-worker-cap). Must be greater than zero. |

## Attribute Configuration

`WorkRetentionAttribute` declares default worker-level retention behavior on the executor type.

```csharp
[WorkRetention(purgeIntervalSeconds: 600, maximumFinalWorkers: 1_000)]
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

At startup, the same worker-level behavior can also be configured with the convenience method `ConfigureRetention` or the full `UseRetention` setter.

```csharp
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
});
```

## Queue-Time Configuration

```csharp
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

```csharp
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

- [Retention And Failure](interactions.md#retention-and-failure): failed and interrupted workers are not final and are not automatically purged by final-worker retention.
- Durable workflows retain child completion receipts on the workflow run, so completed child workers can be purged without breaking later joins or workflow status views.
- [System Settings](system-settings.md): system-wide limits include admission capacity for non-final workers and a retained final-worker cap.
