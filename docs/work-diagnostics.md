# Work Diagnostics

## Intent

Workable diagnostics describe the health of the work system itself. They are meant for operators, admin UI chrome, performance testing, and alerting. They answer questions such as:

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
- In the admin UI, acknowledged rejection alerts stay quiet until `AlertableRejectedWorkCount` increases again.

Alertable queue rejection codes are:

- `workable.system.capacity_reached`
- `workable.system.not_started`
- `workable.system.stopping`
- `workable.queue_durability.store_required`
- `workable.queue_durability.store_unreachable`
- `workable.idempotency.persistence_store_required`
- `workable.idempotency.persistence_store_unreachable`

Expected caller or configuration outcomes, such as missing definitions, invalid input, duplicate idempotency rejections, and concurrency group capacity rejections, still increment `RejectedWorkCount` but do not light the admin UI tray by themselves.

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

The default warning threshold used by diagnostics components is `100` pending updates. SignalR alert severity treats lag at 10x the threshold as critical.

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

The default retention warning threshold is `30` seconds of oldest due purge age. SignalR alert severity treats age at 10x the threshold as critical.

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

A nonzero `DeferredStartCount` can be normal during bursts. A warning means deferred workers have been waiting longer than the configured threshold. The default concurrency warning threshold used by diagnostics components is `30` seconds. SignalR alert severity treats age at 10x the threshold as critical.

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

The default warning thresholds used by diagnostics components are `30` seconds for accepted worker materialization and `30` seconds for cleanup age. SignalR alert severity treats either age at 10x its threshold as critical. Reader, lease renewal, and cleanup failures are critical because durable coordination may stop progressing until the loop recovers.

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

Duplicate rejections can be healthy when callers intentionally retry the same subject. Unexpected growth can indicate client retries, duplicate events, or replay traffic. Idempotency diagnostics appear in the admin UI popover, but duplicate rejections do not create a notification bell warning by themselves.

## Realtime Diagnostics Views

The SignalR diagnostics view is separate from overview component subscriptions. This keeps always-on alert indicators small and keeps detailed diagnostics traffic off the overview path.

Diagnostics components are:

- `systemDiagnostics`
- `queueDiagnostics`
- `readModelDiagnostics`
- `retentionDiagnostics`
- `concurrencyDiagnostics`
- `durabilityDiagnostics`
- `idempotencyDiagnostics`

Each supports `compact` and `detailed` shapes. Compact is for notification indicators and small trays. Detailed includes the full diagnostics object.

Diagnostics component options:

- `publishMode: "alertChanges"` pushes only when the alert state changes. This is useful for always-on warning indicators.
- `publishMode: "continuous"` pushes every diagnostics publish interval while the panel is open.
- `warningThreshold` sets the read-model pending update warning threshold.
- `warningSeconds` sets the retention overdue-age or concurrency deferred-start-age warning threshold.
- `acceptedWorkerWarningSeconds` sets the durability accepted-worker materialization warning threshold.
- `cleanupWarningSeconds` sets the durability cleanup-age warning threshold.

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

## Admin UI Warnings

The admin UI notification tray uses compact diagnostics to show system warnings without polling.

- Alertable queue rejections are shown as critical because system state, capacity, or persistence infrastructure denied work.
- Read-model lag is shown when `PendingUpdateCount` crosses the configured threshold.
- Retention lag is shown when `OldestDuePurgeAge` crosses the configured threshold.
- Concurrency backlog is shown when `OldestDeferredStartAge` crosses the configured threshold.
- Durable materialization lag is shown when `OldestAcceptedWaiterAge` crosses the configured threshold.
- Durable cleanup lag is shown when `OldestPendingCleanupAge` crosses the configured threshold.
- Projector, scheduler, durable reader, lease renewal, and cleanup failures are shown as critical because an internal background component failed.
- Idempotency duplicate rejections are visible in the popover but do not create notification warnings by themselves.

The always-on bell indicator subscribes to compact `alertChanges` diagnostics for every realtime-enabled system configured in the admin UI, across all configured hosts. Each subscription is still scoped to one Workable system because the SignalR hub resolves diagnostics by system name. The client aggregates those compact alert states into one tray indicator and labels warnings with the system and host that produced them.

That aggregate alert layer is intentionally lightweight:

- Healthy systems stay quiet because `alertChanges` does not push repeated "still healthy" payloads.
- Alert subscriptions do not capture realtime payload messages for the payload viewer.
- Queue rejection acknowledgements are tracked per system, so acknowledging one system does not hide rejection alerts from another system.

Opening the tray does not subscribe to full diagnostics for every configured system. The visible detail panels remain scoped to the currently selected system. Opening the tray subscribes to compact `continuous` diagnostics for the active system so visible counts stay fresh at the diagnostics publish interval. Expanding a diagnostics section subscribes to the detailed component for that active system and section. Closing the tray or collapsing a detail section removes the extra subscription.

To inspect detailed diagnostics for a different host or system, switch the admin UI to that system and open or expand the tray there. The bell can alert across systems; the detailed diagnostics panels inspect the active system.

Healthy systems should be quiet. The always-on alert subscription uses `alertChanges`, so it does not continually push "everything is fine" messages.

Acknowledging queue rejection warnings stores the current `AlertableRejectedWorkCount` for that system in the admin UI. The critical warning stays quiet until that same system reports a larger alertable rejection count.

The system tools menu near the tray opens the realtime payload viewer and event viewer. The payload viewer can show diagnostics `workable.view` messages when diagnostics subscriptions are active, which is useful for verifying compact alert payloads, tray payloads, and detailed diagnostics payloads separately.
