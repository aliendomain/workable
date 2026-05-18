# Work Diagnostics

## Intent

Workable diagnostics describe the health of the in-memory work system itself. They are meant for operators, admin UI chrome, performance testing, and alerting. They answer questions such as:

- Is the query read model keeping up with lifecycle updates?
- Is work being rejected before it is accepted?
- Is retention falling behind final-worker cleanup?
- Did an internal projector or scheduler fail?

Diagnostics are available from `IWorkSystem.Diagnostics`, the HTTP diagnostics endpoint, and realtime diagnostics component views.

```csharp
WorkSystemQueueDiagnostics queue = workSystem.Diagnostics.Queue;
WorkSystemReadModelDiagnostics readModel = workSystem.Diagnostics.ReadModel;
WorkSystemRetentionDiagnostics retention = workSystem.Diagnostics.Retention;
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
    string? LastRejectedMessage);
```

`RejectedWorkCount` increments when work is not accepted. Rejections can happen because a definition is missing, invocation is not allowed, the system is stopping, validation fails, idempotency rejects a duplicate, or system capacity rejects a request.

Think of queue rejection as an error signal for the caller, not read-model lag. If this count moves unexpectedly:

- Check `LastRejectedCode` and `LastRejectedMessage` first.
- If the status is capacity-related, the system is protecting itself from accepting more in-memory work.
- If the status is invalid or not found, a caller may be targeting the wrong definition, system, or channel.
- In the admin UI, acknowledged rejection alerts stay quiet until `RejectedWorkCount` increases again.

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

## Realtime Diagnostics Views

The SignalR diagnostics view is separate from overview component subscriptions. This keeps always-on alert indicators small and keeps detailed diagnostics traffic off the overview path.

Diagnostics components are:

- `queueDiagnostics`
- `readModelDiagnostics`
- `retentionDiagnostics`

Each supports `compact` and `detailed` shapes. Compact is for notification indicators and small trays. Detailed includes the full diagnostics object.

Diagnostics component options:

- `publishMode: "alertChanges"` pushes only when the alert state changes. This is useful for always-on warning indicators.
- `publishMode: "continuous"` pushes every diagnostics publish interval while the panel is open.
- `warningThreshold` sets the read-model pending update warning threshold.
- `warningSeconds` sets the retention overdue-age warning threshold.

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

- Queue rejections are shown as critical because accepted work was denied.
- Read-model lag is shown when `PendingUpdateCount` crosses the configured threshold.
- Retention lag is shown when `OldestDuePurgeAge` crosses the configured threshold.
- Projector and scheduler failures are shown as errors because an internal background component failed.

The always-on bell indicator subscribes to compact `alertChanges` diagnostics. That subscription stays quiet while the alert state is unchanged, so healthy systems do not receive a continuous stream of "still healthy" messages. Opening the tray subscribes to compact `continuous` diagnostics so the visible counts stay fresh at the diagnostics publish interval. Expanding a diagnostics section subscribes to the detailed component for that section. Closing the tray or collapsing a detail section removes the extra subscription.

Healthy systems should be quiet. The always-on alert subscription uses `alertChanges`, so it does not continually push "everything is fine" messages.

Acknowledging queue rejection warnings stores the current `RejectedWorkCount` in the admin UI. The critical warning stays quiet until the server reports a larger rejection count.

The system tools menu near the tray opens the realtime payload viewer and event viewer. The payload viewer can show diagnostics `workable.view` messages when diagnostics subscriptions are active, which is useful for verifying compact alert payloads, tray payloads, and detailed diagnostics payloads separately.
