# System Capacity Configuration

System capacity configuration controls how many non-final worker records an in-memory Workable system will accept before rejecting new queue requests.

`MaximumWorkers` is an approximate admission guard for workers that are not final. Queued, running, waiting, retrying, paused, and failed workers count against the guard. Completed and canceled workers are final, so they remain available for retention and query views without blocking new queue requests. Because the check is intentionally lightweight and concurrent, brief overages are possible under heavy parallel queueing.

| Setting | Default | Behavior |
| --- | --- | --- |
| `MaximumWorkers` | `1_000_000` | Approximate non-final worker record limit for the system. When the system is at or above this count, new queue requests are rejected with `workable.system.capacity_reached`. Must be greater than zero. |

## Bootstrap

```
services.AddWorkableSystem(builder =>
{
    builder.ConfigureCapacity(maximumWorkers: 1_000_000);

    builder.AddWork<RefreshCacheWork>(
        WorkDefinition.Create(
            name: "cache.refresh",
            description: "Refreshes cached data.",
            category: "Cache"));
});
```

## Related Interactions

- [Retention Configuration](work-configuration-retention.md): retention reduces final worker records after completion or cancellation. Final workers do not count against system admission capacity, but they still consume memory while retained.
- [Concurrency Configuration](work-configuration-concurrency.md): concurrency limits how many workers occupy execution capacity for a work definition, subject, or key. System capacity limits accepted non-final worker records.
