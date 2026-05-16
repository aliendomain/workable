# System Capacity Configuration

System capacity configuration controls how many worker records an in-memory Workable system will accept before rejecting new queue requests.

`MaximumWorkers` is an approximate admission guard across all worker states, including queued, running, waiting, failed, completed, and canceled workers. Workable checks the current worker dictionary count before accepting a new worker. Because the check is intentionally lightweight and concurrent, brief overages are possible under heavy parallel queueing.

| Setting | Default | Behavior |
| --- | --- | --- |
| `MaximumWorkers` | `1_000_000` | Approximate total worker record limit for the system. When the system is at or above this count, new queue requests are rejected with `workable.system.capacity_reached`. Must be greater than zero. |

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

- [Retention Configuration](work-configuration-retention.md): retention reduces final worker records after completion or cancellation, but it does not protect the system from unbounded queued or running workers.
- [Concurrency Configuration](work-configuration-concurrency.md): concurrency limits how many workers occupy execution capacity for a work definition, subject, or key. System capacity limits total accepted worker records.
