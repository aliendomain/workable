# System Settings

System settings control the main startup-only limits that apply across the whole Workable system.

`MaximumWorkers` is an approximate admission guard for workers that are not final. Queued, running, waiting, retrying, paused, failed, and interrupted workers count against the guard. Completed and canceled workers are final, so they remain available for retention and query views without blocking new queue requests. Because the check is intentionally lightweight and concurrent, brief overages are possible under heavy parallel queueing.

## Settings

| Setting | Default | Description |
| --- | --- | --- |
| `MaximumWorkers` | `1_000_000` | Approximate non-final worker record limit for the system. When the system is at or above this count, new queue requests are rejected with `workable.system.capacity_reached`. Must be greater than zero. |

## Profiling Limit

Automatic SQL, HTTP, and extension instrumentation shares a hard per-profile node limit. Explicit application nodes created through `IWorkProfiler` are not counted against it.

| Setting | Default | Description |
| --- | --- | --- |
| `WorkSystemProfilingConfiguration.MaximumAutomaticInstrumentationNodes` | `500` | Maximum automatic instrumentation nodes admitted to one worker-iteration profile. Must be greater than zero. Temporary full-capture rules can intentionally bypass this limit for selected workers. |

The limit is shared across SQL client, HTTP client, and custom automatic instrumentation; it is not a separate allowance for each source. Admission, including HTTP sampling, is atomic under concurrent bursts. When the limit is reached, one truncation summary reports omitted counts by source; it retains at most 32 source keys of up to 128 characters and aggregates additional custom keys under `other`. Built-in instrumentation avoids constructing context for rejected nodes, and HTTP capture stops requesting full activity data for subsequent requests in that bounded profile. Explicit `IWorkProfiler` nodes are outside this budget.

The node limit is not a byte limit, so each integration must also bound its retained context. Built-in HTTP capture limits text fields and excludes headers, bodies, URI query contents, URI user information, and exception messages. Built-in SQL capture bounds statements, parameter previews, metadata, and exception messages; see [Work Profiling](../../concepts/profiling.md#automatic-sql-client-timing) for the exact limits.

A temporary `Full` capture rule bypasses this node-count limit only. It does not bypass queue authorization, security redaction, or retention. See [Work Profiling](../../concepts/profiling.md#temporary-full-capture) for the API and admin UI workflow.

## Iteration Status Replay Limits

Iteration status streams retain published application progress in memory even when no consumer is currently listening. That replay-first behavior lets clients connect after an iteration begins without losing its initial status items.

| Setting | Default | Description |
| --- | --- | --- |
| `WorkSystemIterationStatusConfiguration.ReplayItemCapacity` | `4_096` | Maximum recent status items retained for replay per iteration. Older items are evicted and an older cursor produces an explicit replay gap. Must be greater than zero. |
| `WorkSystemIterationStatusConfiguration.ReplayPayloadByteCapacity` | `4_194_304` | Maximum combined UTF-8 type and serialized JSON payload bytes retained per iteration. Oldest items are evicted until both per-iteration limits are satisfied. Must accommodate `MaximumTypeBytes + MaximumPayloadBytes`. |
| `WorkSystemIterationStatusConfiguration.SystemReplayItemCapacity` | `65_536` | Maximum status items retained across the system. When exceeded, Workable evicts the oldest status item across iteration heads and reports replay gaps to affected readers. Must be at least `ReplayItemCapacity`. |
| `WorkSystemIterationStatusConfiguration.SystemReplayByteCapacity` | `67_108_864` | Maximum combined UTF-8 type and JSON payload bytes retained across the system. Must be at least `ReplayPayloadByteCapacity`. This bounds accounted content, not exact CLR object overhead. |
| `WorkSystemIterationStatusConfiguration.MaximumPayloadBytes` | `32_768` | Maximum serialized UTF-8 JSON payload size for one status item. This default keeps payload measurement below the large-object-heap threshold. Oversized publications throw `WorkIterationStatusPayloadTooLargeException`. |
| `WorkSystemIterationStatusConfiguration.MaximumTypeBytes` | `256` | Maximum UTF-8 status type size. Oversized types throw `WorkIterationStatusTypeTooLargeException`, and accepted type bytes count toward both replay byte budgets. |
| `WorkSystemIterationStatusConfiguration.MaximumSubscriptions` | `4_096` | Maximum active iteration-status subscriptions across the system. |
| `WorkSystemIterationStatusConfiguration.MaximumSubscriptionsPerIteration` | `64` | Maximum active subscriptions to one iteration. Must not exceed `MaximumSubscriptions`. |

These are startup-only system memory and fanout guardrails, not worker retention settings. Existing worker and iteration retention controls lifetime: purging a worker or forgetting an iteration removes its status replay buffer. Status replay is transient and is not written to the configured persistence store.

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
    builder.ConfigureIterationStatuses(
        replayItemCapacity: 4_096,
        replayPayloadByteCapacity: 4 * 1_024 * 1_024,
        systemReplayItemCapacity: 65_536,
        systemReplayByteCapacity: 64 * 1_024 * 1_024,
        maximumPayloadBytes: 32 * 1_024,
        maximumTypeBytes: 256,
        maximumSubscriptions: 4_096,
        maximumSubscriptionsPerIteration: 64);
    builder.ConfigureProfiling(maximumAutomaticInstrumentationNodes: 500);
    builder.UseShutdownGracePeriodRatio(0.8);

    builder.AddWork<RefreshCacheWork>(
        WorkDefinition.Create(
            name: "cache.refresh",
            description: "Refreshes cached data.",
            category: "Cache"));
});
```
