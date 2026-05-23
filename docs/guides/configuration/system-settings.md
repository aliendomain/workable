# System Settings

System settings control the main startup-only limits that apply across the whole Workable system.

`MaximumWorkers` is an approximate admission guard for workers that are not final. Queued, running, waiting, retrying, paused, failed, and interrupted workers count against the guard. Completed and canceled workers are final, so they remain available for retention and query views without blocking new queue requests. Because the check is intentionally lightweight and concurrent, brief overages are possible under heavy parallel queueing.

## Settings

| Setting | Default | Description |
| --- | --- | --- |
| `MaximumWorkers` | `1_000_000` | Approximate non-final worker record limit for the system. When the system is at or above this count, new queue requests are rejected with `workable.system.capacity_reached`. Must be greater than zero. |

## System Final Worker Cap

Workable also has a separate system-wide retained final-worker cap. This is not admission capacity for non-final workers, but it is the other main system-level limit that affects how much worker history the system keeps.

| Setting | Default | Description |
| --- | --- | --- |
| `WorkSystemRetentionConfiguration.MaximumFinalWorkers` | `10_000` | Target retained completed or canceled workers across the whole system. Count retention runs in the background, so brief overages are expected under load. Cleanup can purge any final workers in the system. This cap can reduce how many final workers one definition retains even when that definition's own retention target is higher. Must be greater than zero. |

This setting is startup-only. It does not participate in queue-time overrides or worker reconfiguration.

## Shutdown Grace Period

Workable also lets the host control how long shutdown waits for interrupted workers to stop cooperatively before they are force-interrupted.

| Setting | Default | Description |
| --- | --- | --- |
| `UseShutdownGracePeriod(TimeSpan gracePeriod)` | not set | Uses one explicit grace period for this system, regardless of the host shutdown timeout. |
| `UseShutdownGracePeriodRatio(double hostShutdownTimeoutRatio)` | `0.8` | Uses a percentage of the .NET host shutdown timeout for this system. The ratio must be greater than `0` and less than or equal to `0.9`. |
| Fallback grace period | `15 seconds` | Used when Workable is running outside a host or when the host shutdown timeout is unavailable or non-positive. |

By default, Workable uses `UseShutdownGracePeriodRatio(0.8)`. That means hosted systems get 80% of the host shutdown timeout unless an explicit grace period is configured.

## Startup Configuration

System-wide limits are configured only at startup on `IWorkSystemBuilder`.

```csharp
services.AddWorkableSystem(builder =>
{
    builder.ConfigureCapacity(maximumWorkers: 1_000_000);
    builder.ConfigureRetention(maximumFinalWorkers: 10_000);
    builder.UseShutdownGracePeriodRatio(0.8);

    builder.AddWork<RefreshCacheWork>(
        WorkDefinition.Create(
            name: "cache.refresh",
            description: "Refreshes cached data.",
            category: "Cache"));
});
```
