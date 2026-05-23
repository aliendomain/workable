# Work Diagnostics

## Intent

Workable diagnostics describe the health of the work system itself. They are meant for operators, performance testing, and alerting. They answer questions such as:

- Is the query read model keeping up with lifecycle updates?
- Is work being rejected before it is accepted?
- Is retention falling behind final-worker cleanup?
- Are concurrency-deferred workers waiting too long?
- Are durable workers failing to materialize or clean up?
- Are duplicate idempotency rejections happening?
- Did an internal projector, scheduler, or durability loop fail?

Diagnostics are available from `IWorkSystem.Diagnostics`, the HTTP diagnostics endpoint, and realtime diagnostics component views.

```csharp
WorkSystemQueueDiagnostics queue = workSystem.Diagnostics.Queue;
WorkSystemReadModelDiagnostics readModel = workSystem.Diagnostics.ReadModel;
WorkSystemRetentionDiagnostics retention = workSystem.Diagnostics.Retention;
WorkSystemConcurrencyDiagnostics concurrency = workSystem.Diagnostics.Concurrency;
WorkSystemDurabilityDiagnostics durability = workSystem.Diagnostics.Durability;
WorkSystemIdempotencyDiagnostics idempotency = workSystem.Diagnostics.Idempotency;
```

```http
GET /workable/diagnostics
GET /workable/systems/email/diagnostics
```

## Reading Diagnostics

Diagnostics work without SignalR.

- In-process callers can read `IWorkSystem.Diagnostics` directly.
- HTTP callers can read the diagnostics endpoint on demand.
- Realtime diagnostics views are optional and exist for callers that want pushed updates instead of polling.

## Queue Diagnostics

Queue diagnostics describe rejected queue requests.

```csharp
public sealed record WorkSystemQueueDiagnostics(
    long RejectedWorkCount,
    DateTimeOffset? LastRejectedAt,
    WorkQueueStatus? LastRejectedStatus,
    WorkDefinitionId? LastRejectedDefinitionId,
    string? LastRejectedCode,
    string? LastRejectedMessage,
    long AlertableRejectedWorkCount,
    string? LastAlertableRejectedCode,
    string? LastAlertableRejectedMessage);
```

`RejectedWorkCount` increments when work is not accepted. Rejections can happen because a definition is missing, invocation is not allowed, the system is stopping, validation fails, idempotency rejects a duplicate, or system capacity rejects a request.

Think of queue rejection as an error signal for the caller, not read-model lag. If this count moves unexpectedly:

- Check `LastRejectedCode` and `LastRejectedMessage` first.
- If the status is capacity-related, the system is protecting itself from accepting more in-memory work.
- If the status is invalid or not found, a caller may be targeting the wrong definition, system, or channel.
- `AlertableRejectedWorkCount` only tracks rejection codes that usually indicate infrastructure or system-capacity problems.

Alertable queue rejection codes are:

- `workable.system.capacity_reached`
- `workable.system.not_started`
- `workable.system.stopping`
- `workable.queue_durability.store_required`
- `workable.queue_durability.store_unreachable`
- `workable.idempotency.persistence_store_required`
- `workable.idempotency.persistence_store_unreachable`

Expected caller or configuration outcomes, such as missing definitions, invalid input, duplicate idempotency rejections, and concurrency group capacity rejections, still increment `RejectedWorkCount` but do not count as alertable rejections.

## Read-Model Diagnostics

The read model serves query APIs and component views from immutable snapshots. Lifecycle updates are projected asynchronously into that model.

```csharp
public sealed record WorkSystemReadModelDiagnostics(
    long EnqueuedSequence,
    long AppliedSequence,
    long AppliedUpdateCount,
    long PublishedSnapshotCount,
    int LastBatchSize,
    TimeSpan LastProjectionDuration,
    DateTimeOffset? LastProjectedAt,
    string? ProjectorFailureType,
    string? ProjectorFailureMessage)
{
    public long PendingUpdateCount => Math.Max(0, EnqueuedSequence - AppliedSequence);
    public bool HasProjectorFailure => ProjectorFailureType is not null;
}
```

Important fields:

- `EnqueuedSequence`: latest lifecycle update accepted by the projector.
- `AppliedSequence`: latest update included in the currently published read snapshot.
- `PendingUpdateCount`: current lag between accepted updates and the published snapshot.
- `AppliedUpdateCount`: total updates applied by the projector.
- `PublishedSnapshotCount`: number of immutable read snapshots published.
- `LastBatchSize`: number of updates applied in the last projection batch.
- `LastProjectionDuration`: how long the last projection batch took.
- `LastProjectedAt`: when projection last completed.
- `HasProjectorFailure`: whether the projector saw an internal exception.

`PendingUpdateCount` can be nonzero during bursts. That is normal. A warning means the query surface is falling behind writes by more than the configured threshold, not that work execution is stopped.

The default warning threshold used by diagnostics components is `100` pending updates. The realtime diagnostics alert broadcaster treats lag at 10x the threshold as critical.

When read-model lag is high:

- Overview and query pages may show an older, internally consistent snapshot.
- Worker lifecycle execution can still be moving normally.
- If `PendingUpdateCount` rises and does not return toward zero, projection is not draining fast enough.
- If `LastProjectionDuration` grows, the projector may be spending too much time per batch.
- If `HasProjectorFailure` is true, treat it as an internal failure and inspect the exception fields.

## Retention Diagnostics

Retention diagnostics describe automatic cleanup for final workers.

```csharp
public sealed record WorkSystemRetentionDiagnostics(
    int TrackedFinalWorkerCount,
    int ScheduledPurgeCount,
    int ScheduledPurgeHighWaterMark,
    DateTimeOffset? OldestScheduledPurgeDueAt,
    TimeSpan OldestDuePurgeAge,
    int PendingCountRetentionDefinitionCount,
    bool SystemCountRetentionPending,
    DateTimeOffset? LastRunAt,
    TimeSpan LastRunDuration,
    int LastPurgedCount,
    long TotalPurgedCount,
    string? SchedulerFailureType,
    string? SchedulerFailureMessage)
{
    public bool HasSchedulerFailure => SchedulerFailureType is not null;
}
```

Important fields:

- `TrackedFinalWorkerCount`: final workers currently tracked for retention decisions.
- `ScheduledPurgeCount`: workers with a time-based purge scheduled.
- `ScheduledPurgeHighWaterMark`: highest scheduled purge queue size since the last trim/reset.
- `OldestScheduledPurgeDueAt`: due time of the oldest scheduled purge, if any.
- `OldestDuePurgeAge`: how long the oldest due purge has been waiting.
- `PendingCountRetentionDefinitionCount`: number of definitions that need count-based cleanup.
- `SystemCountRetentionPending`: whether system-level count cleanup is needed.
- `LastRunAt`: last retention scheduler run.
- `LastRunDuration`: time spent in the last retention run.
- `LastPurgedCount`: number of workers purged in the last run.
- `TotalPurgedCount`: total workers purged by retention.
- `HasSchedulerFailure`: whether the scheduler saw an internal exception.

`TrackedFinalWorkerCount` is not the same as `ScheduledPurgeCount`. A final worker can be tracked for count-based retention even if its time-based purge is not due. `ScheduledPurgeCount` is the time-ordered purge queue. Both can be high after a burst.

The default retention warning threshold is `30` seconds of oldest due purge age. The realtime diagnostics alert broadcaster treats age at 10x the threshold as critical.

When retention is behind:

- Final workers may remain queryable longer than configured.
- Retention cleanup may be doing large purge batches.
- Work acceptance is not necessarily blocked unless system capacity or memory pressure is also involved.
- If `HasSchedulerFailure` is true, inspect the scheduler exception fields.

## Concurrency Diagnostics

Concurrency diagnostics describe workers accepted with `DeferStart` that are waiting for capacity.

```csharp
public sealed record WorkSystemConcurrencyDiagnostics(
    int DeferredStartCount,
    TimeSpan OldestDeferredStartAge,
    int LastDrainReleasedCount);
```

Important fields:

- `DeferredStartCount`: workers currently blocked by concurrency and waiting to start.
- `OldestDeferredStartAge`: how long the longest-waiting deferred worker has been blocked.
- `LastDrainReleasedCount`: workers released by the most recent concurrency drain.

A nonzero `DeferredStartCount` can be normal during bursts. A warning means deferred workers have been waiting longer than the configured threshold. The default concurrency warning threshold used by diagnostics components is `30` seconds. The realtime diagnostics alert broadcaster treats age at 10x the threshold as critical.

When concurrency is behind:

- Work has been accepted but is not starting because configured capacity is full.
- If `LastDrainReleasedCount` remains low or zero while age grows, capacity may not be freeing.
- Check work duration, failed or paused workers that hold capacity, and whether `MaximumCapacity` is too low for the traffic shape.

## Durability Diagnostics

Durability diagnostics describe the durable queue reader, lease renewal loop, and cleanup loop.

```csharp
public sealed record WorkSystemDurabilityDiagnostics(
    int AcceptedWaiterCount,
    TimeSpan OldestAcceptedWaiterAge,
    int PendingCleanupCount,
    TimeSpan OldestPendingCleanupAge,
    string? ReaderFailureType,
    string? ReaderFailureMessage,
    string? LeaseRenewalFailureType,
    string? LeaseRenewalFailureMessage,
    string? CleanupFailureType,
    string? CleanupFailureMessage)
{
    public bool HasReaderFailure => this.ReaderFailureType is not null;
    public bool HasLeaseRenewalFailure => this.LeaseRenewalFailureType is not null;
    public bool HasCleanupFailure => this.CleanupFailureType is not null;
}
```

Important fields:

- `AcceptedWaiterCount`: accepted durable queue requests waiting to materialize into in-memory workers.
- `OldestAcceptedWaiterAge`: how long the oldest accepted durable request has been waiting to materialize.
- `PendingCleanupCount`: durable cleanup actions queued for completed, canceled, purged, or lost durable rows.
- `OldestPendingCleanupAge`: how long the oldest cleanup item has been waiting.
- `HasReaderFailure`: whether the durable reader loop saw an internal or persistence exception.
- `HasLeaseRenewalFailure`: whether active durable lease renewal failed.
- `HasCleanupFailure`: whether durable cleanup failed.

The default warning thresholds used by diagnostics components are `30` seconds for accepted worker materialization and `30` seconds for cleanup age. The realtime diagnostics alert broadcaster treats either age at 10x its threshold as critical. Reader, lease renewal, and cleanup failures are critical because durable coordination may stop progressing until the loop recovers.

When durability is behind:

- Accepted durable queue calls may be waiting for Workable to materialize workers.
- Durable rows for completed or canceled work may remain in the persistence store longer than expected.
- Lease renewal failures can allow another runtime to replay durable work after the lease expires.
- Persistence connectivity errors should be treated as infrastructure issues, especially if they repeat.

## Idempotency Diagnostics

Idempotency diagnostics describe duplicate-subject queue requests rejected by local or persistent coordination.

```csharp
public sealed record WorkSystemIdempotencyDiagnostics(
    long DuplicateRejectionCount,
    WorkCoordinationStorage? LastDuplicateRejectedStorage);
```

Important fields:

- `DuplicateRejectionCount`: queue requests rejected because an idempotency reservation already existed for the same definition and subject.
- `LastDuplicateRejectedStorage`: whether the most recent duplicate was rejected by `Local` or `Persistent` coordination.

Duplicate rejections can be healthy when callers intentionally retry the same subject. Unexpected growth can indicate client retries, duplicate events, or replay traffic. Duplicate rejections do not create warning alerts by themselves.

## Pushed Diagnostics

If you want diagnostics to be pushed, Workable exposes them through the realtime diagnostics view. This is optional. The underlying diagnostics data is the same data available from `IWorkSystem.Diagnostics` and the HTTP diagnostics endpoint.

The SignalR diagnostics view is separate from overview component subscriptions. This keeps always-on alert indicators small and keeps detailed diagnostics traffic off the overview path.

Diagnostics components are:

- `systemDiagnostics`
- `queueDiagnostics`
- `readModelDiagnostics`
- `retentionDiagnostics`
- `concurrencyDiagnostics`
- `durabilityDiagnostics`
- `idempotencyDiagnostics`

`systemDiagnostics` is compact-only. The other diagnostics components support `compact` and `detailed` shapes. Detailed responses wrap the full diagnostics object plus any derived threshold or boolean fields for that component.

Diagnostics component options:

- `publishMode: "alertChanges"` is a SignalR-only compact diagnostics mode. It pushes only when alert state changes, and is used for `systemDiagnostics`, `queueDiagnostics`, `readModelDiagnostics`, `retentionDiagnostics`, `concurrencyDiagnostics`, and `durabilityDiagnostics`.
- `publishMode: "continuous"` pushes every diagnostics publish interval while the subscription is active.
- `warningThreshold` sets the read-model pending update warning threshold for `readModelDiagnostics`.
- `warningSeconds` sets the retention overdue-age or concurrency deferred-start-age warning threshold for `retentionDiagnostics` and `concurrencyDiagnostics`.
- `acceptedWorkerWarningSeconds` sets the durability accepted-worker materialization warning threshold for `durabilityDiagnostics`.
- `cleanupWarningSeconds` sets the durability cleanup-age warning threshold for `durabilityDiagnostics`.

Example:

```csharp
await connection.InvokeAsync(
    "WatchView",
    "diagnostics",
    new WorkViewCriteria(
        Components:
        [
            new(
                "readModelDiagnostics",
                "readModelDiagnostics",
                Options: JsonSerializer.SerializeToElement(new
                {
                    publishMode = "alertChanges",
                    warningThreshold = 100
                }),
                Shape: WorkComponentShapes.Compact)
        ]),
    (string?)null);
```

## Alert Subscriptions

Compact diagnostics are designed for lightweight alerting without polling.

- Alertable queue rejections can be surfaced when system state, capacity, or persistence infrastructure denied work.
- Read-model lag can be surfaced when `PendingUpdateCount` crosses the configured threshold.
- Retention lag can be surfaced when `OldestDuePurgeAge` crosses the configured threshold.
- Concurrency backlog can be surfaced when `OldestDeferredStartAge` crosses the configured threshold.
- Durable materialization lag can be surfaced when `OldestAcceptedWaiterAge` crosses the configured threshold.
- Durable cleanup lag can be surfaced when `OldestPendingCleanupAge` crosses the configured threshold.
- Projector, scheduler, durable reader, lease renewal, and cleanup failures can be surfaced as critical because an internal background component failed.
- Idempotency duplicate rejections are visible in diagnostics data, but do not create warning alerts by themselves.

`alertChanges` is intentionally quiet. Healthy systems do not repeatedly emit "still healthy" payloads.

For user interfaces or monitoring clients, a common pattern is:

- keep an always-on compact subscription for alert state
- subscribe to compact `continuous` diagnostics only for the system the user is actively viewing
- subscribe to detailed components only when the caller is actively inspecting that section

That keeps alerting responsive while avoiding a constant stream of detailed diagnostics payloads for every system.
