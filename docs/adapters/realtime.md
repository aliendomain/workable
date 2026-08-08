# Workable Realtime

Workable can stream worker events and coalesced component-view updates to ASP.NET Core clients through the `Workable.SignalR` adapter package.

The realtime adapter is observability-only. Queueing work, querying snapshots, and sending worker actions remain in the .NET and HTTP API surfaces. SignalR clients subscribe to updates and receive messages when the underlying Workable state or event stream changes.

`Workable.SignalR` is an authenticated transport. Anonymous negotiate and connect requests are rejected, and mapped systems must be authorization-enabled.

Each hub subscription captures a `WorkRequestContext` and an authorization snapshot when the client subscribes. Realtime reads are filtered by the caller's read access, and shared subscription groups are keyed by effective read visibility so callers only share broadcasts when they can see the same work.

## Setup

Register and map the SignalR adapter from the host application.

```csharp
builder.Services.AddWorkableSignalR();

app.MapWorkableSignalR();
```

The default hub path is `/workable/realtime`.

`MapWorkableSignalR` always requires authenticated callers. When `WorkableAspNetCoreAuthorizationOptions.TransportAuthenticationScheme` is also set, `MapWorkableSignalR` adds matching authorization metadata to the hub endpoint so ASP.NET Core evaluates that specific scheme.

That transport scheme is not automatic. `AddWorkableSignalR()` by itself does not choose one. It is commonly set by [Workable.Entra](../guides/entra-authentication.md), or by host code that wants Workable SignalR requests to authenticate with one specific ASP.NET Core scheme instead of inheriting the ambient default.

When a transport scheme is configured, the host pipeline must run authentication and authorization middleware before the hub endpoint executes. If your host already runs `app.UseAuthentication()` and `app.UseAuthorization()`, no extra step is needed.

```csharp
app.UseAuthentication();
app.UseAuthorization();
app.MapWorkableSignalR("/internal/work/realtime");
```

When a browser connects to the Workable hub from a different origin, such as a local admin UI at `http://localhost:3000` calling `https://localhost:7058/workable/realtime`, the host must also configure CORS for the hub endpoint. Workable does not add CORS automatically.

```csharp
builder.Services.AddCors(options =>
{
    options.AddPolicy("WorkableRealtime", policy =>
    {
        policy
            .WithOrigins("http://localhost:3000")
            .WithMethods("GET", "POST")
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

app.MapWorkableSignalR()
    .RequireCors("WorkableRealtime");
```

`GET` and `POST` are the methods used by the SignalR connect and negotiate flow. `AllowAnyHeader()` is usually the pragmatic choice because browser SignalR clients commonly send `Authorization` on negotiate requests and may include transport-specific headers over time. For tightly controlled environments, you can replace it with an explicit header list once you have confirmed the exact requests your client emits.

When the browser client connects with credentials enabled, such as the Workable admin UI SignalR client, the CORS policy must also call `AllowCredentials()`. In that case, the allowed origins must stay explicit; ASP.NET Core will not allow combining credentials with wildcard origins.

```csharp
builder.Services.AddWorkableSignalR(options =>
{
    options.HubPath = "/internal/work/realtime";
    options.PublishInterval = TimeSpan.FromSeconds(2);
    options.DiagnosticsPublishInterval = TimeSpan.FromMilliseconds(250);
    options.BatchTimeWindow = TimeSpan.FromSeconds(1);
    options.LiveTimeWindow = TimeSpan.FromMilliseconds(100);
    options.MinimumTimeWindow = TimeSpan.FromMilliseconds(100);
    options.EventMaxBatchSize = 512;
    options.EventSubscriptionCapacity = 16_384;
    options.EventOverflowBehavior = WorkEventOverflowBehavior.DropWrite;
});

app.MapWorkableSignalR();
```

The mapped path can also be supplied directly.

```csharp
app.MapWorkableSignalR("/internal/work/realtime");
```

`AddWorkableSignalR` accepts these options:

- `HubPath`: the default path used by `MapWorkableSignalR()` when the map call does not supply one explicitly.
- `PublishInterval`: how often interval-required named view components, such as throughput, are recomputed and pushed while they are active. State-based named views are pushed from Workable change notifications.
- `DiagnosticsPublishInterval`: how often diagnostics named view subscriptions are recomputed and pushed while they are active.
- `BatchTimeWindow`: how long the broadcaster waits to accumulate more events after the first event in a raw event-stream burst before sending.
- `LiveTimeWindow`: how long the broadcaster waits to accumulate more worker-overview changes before sending one coalesced latest-state update.
- `MinimumTimeWindow`: the minimum positive time window the broadcaster will honor for either mode, to avoid overly chatty sends from tiny configured values.
- `EventMaxBatchSize`: the maximum number of events included in one `workable.events` batch.
- `EventSubscriptionCapacity`: the number of events each active event subscription group can buffer before overflow handling applies.
- `EventOverflowBehavior`: what happens when an event subscription group reaches its buffer limit, such as `DropWrite` or `DropOldest`.

`AddWorkableSignalR` registers one background broadcaster per host. Each hosted Workable system gets four coordinated realtime lanes:

- raw event streaming
- worker-overview streaming
- named view streaming
- diagnostics view streaming

Browser connections join SignalR groups and share server-side recomputation or event readers when their normalized subscription request and effective read access match. One browser does not get its own private Workable event-stream subscription unless its request shape differs from the other active subscribers.

State-based named views are wake-on-change: the in-memory runtime publishes coalesced change notifications after its read model snapshot advances, and the SignalR broadcaster recomputes the latest view for each matching group. Worker, definition, subject, concurrency-key, identifier, and originating-actor changes are used to avoid recomputing named-view groups whose normalized criteria cannot be affected. Views that depend on time passing still use `PublishInterval`.

## Capability Discovery

`Workable.HttpApi` exposes capability information through the host endpoint so clients can discover whether the host exposes realtime transport and which systems are visible to the caller.

```http
GET /workable/host
```

Realtime capability is host-level in the HTTP discovery surface. System visibility still matters because callers only see systems they can connect to, and system access still determines which system-specific views and diagnostics a client should attempt to use.

When `Workable.SignalR` is registered:

```json
{
  "capabilities": {
    "realtime": {
      "enabled": true,
      "transport": "signalr",
      "hubPath": "/workable/realtime"
    }
  }
}
```

When `Workable.SignalR` is not registered:

```json
{
  "capabilities": {
    "realtime": {
      "enabled": false,
      "transport": null,
      "hubPath": null
    }
  }
}
```

## Actor-Scoped Worker Updates

Use `WatchWorkers` to keep a browser synchronized with all workers originated by one actor. It is a discoverable convenience method over the existing `workers` named view, so it retains the same initial-snapshot and change-stream guarantees without introducing a separate subscription implementation. Set `actorId` on the `workerGrid` component options; actor ids use exact ordinal matching after surrounding whitespace is removed.

```javascript
const subscriptionId = `user-work:${userId}`;

connection.on("workable.view", envelope => {
  if (envelope.subscriptionId !== subscriptionId) {
    return;
  }

  const workerGrid = envelope.result.components.workerGrid;
  renderWorkers(workerGrid.data);
});

await connection.start();
await connection.invoke(
  "WatchWorkers",
  subscriptionId,
  {
    components: [
      {
        id: "workerGrid",
        type: "workerGrid",
        shape: "detailed",
        options: {
          actorId: userId,
          take: 50
        }
      }
    ]
  },
  null
);
```

Subscription establishment always sends the current worker-grid snapshot, including an empty grid when the actor has not originated any work yet. The subscription is registered first, then waits for the shared view change stream to be live before querying that snapshot. Group broadcasts wait behind the direct seed, so a newer group update cannot be overtaken by an older initial snapshot. A worker change that races subscription establishment is therefore either already represented by the initial query or causes a later actor-keyed view update; the browser does not need to subscribe to individual workers or raw events.

If the shared view change stream stops and restarts, the broadcaster re-queries every active state-based view group before consuming new changes. This closes changes that may have occurred while the stream was unavailable without requiring clients to recreate their watches.

The scope follows the actor stored on the worker's original request context. Actions later performed by that actor on somebody else's worker do not move that worker into this view. Normal read authorization still applies, so the grid contains only definitions visible to the SignalR caller.

Stop the subscription with `UnwatchWorkers(subscriptionId, systemName)`.

## Worker Overview Updates

Worker detail pages should load their initial worker-overview snapshot through HTTP, then subscribe to the dedicated realtime worker-overview stream.

```csharp
HubConnection connection = new HubConnectionBuilder()
    .WithUrl("/workable/realtime")
    .Build();

const string WorkerOverviewSubscriptionId = "worker-overview-main";

connection.On<WorkableRealtimeViewEnvelope<WorkWorkerOverviewRealtimeUpdate>>(
    "workable.workerOverview",
    envelope =>
{
    if (!string.Equals(envelope.SubscriptionId, WorkerOverviewSubscriptionId, StringComparison.Ordinal))
    {
        return;
    }

    // Apply the synchronized sections to the visible worker overview state.
});

await connection.StartAsync();
await connection.InvokeAsync(
    "WatchWorkerOverview",
    WorkerOverviewSubscriptionId,
    workerId.ToString("D"),
    new WorkWorkerOverviewRealtimeCriteria(
        WorkerControls: WorkComponentShapes.Standard,
        WorkerLogs: WorkComponentShapes.Detailed,
        WorkerDuration: WorkComponentShapes.Standard,
        WorkerTimeline: WorkComponentShapes.Standard,
        LogSortDirection: WorkWorkerOverviewSortDirection.Desc,
        TimelineSortDirection: WorkWorkerOverviewSortDirection.Desc),
    (string?)null);
```

For a named system, pass the system name as the final argument.

```csharp
await connection.InvokeAsync(
    "WatchWorkerOverview",
    WorkerOverviewSubscriptionId,
    workerId.ToString("D"),
    new WorkWorkerOverviewRealtimeCriteria(
        WorkerControls: WorkComponentShapes.Standard,
        WorkerLogs: WorkComponentShapes.Compact,
        WorkerDuration: WorkComponentShapes.Compact,
        WorkerTimeline: WorkComponentShapes.Standard),
    "email");
```

`WatchWorkerOverview` immediately sends the current worker-overview state to the caller through `workable.workerOverview`. The payload uses the same `WorkableRealtimeViewEnvelope<T>` wrapper as named views, but the `Result` is a `WorkWorkerOverviewRealtimeUpdate` and `ViewName` is always `"worker-overview"`.

The initial message is a full synchronized snapshot serialized as a worker-overview update. Later messages are also latest-state updates: the server coalesces worker changes over `LiveTimeWindow`, re-queries the current worker-overview state, and pushes the synchronized result.

When the worker-overview lane cannot safely continue from its current state, the server can send a `WorkWorkerOverviewRealtimeUpdate` with `RequiresRefresh = true` and an optional `RefreshReason`. Clients should treat that as a resync instruction: reload fresh worker-overview state instead of continuing to apply stale data. Clients may keep the existing watch alive if they can safely resynchronize in place, or recreate it if that is simpler for their UI architecture.

`WorkWorkerOverviewRealtimeCriteria` lets the caller describe the live screen state:

- `WorkerControls`: `compact` or `standard`
- `WorkerLogs`: `compact`, `standard`, or `detailed`
- `WorkerDuration`: `compact`, `standard`, or `detailed`
- `WorkerTimeline`: `compact`, `standard`, or `detailed`
- `LogSortDirection`, `LogLevels`, and optional `LogIterationSequence`
- `TimelineSortDirection` and optional `TimelineCategories`

Those panel modes materially change what the server pushes:

- controls are always live, but latest output is only pushed when controls are `standard`
- logs in `compact` push only summary counts
- logs in `standard` or `detailed` push summary counts plus matching log rows
- recent iterations in `compact` push nothing
- recent iterations in `standard` or `detailed` push matching iteration lifecycle rows
- timeline in `compact` pushes nothing
- timeline in `standard` or `detailed` pushes matching timeline rows

Worker-overview updates are grouped by:

- system
- worker id
- normalized panel modes
- normalized filters and sort directions
- effective read visibility

Matching subscribers share one server-side worker-overview pump. The pump listens for that worker's change key, coalesces bursts over `LiveTimeWindow`, re-queries the latest worker-overview state, and fans the synchronized update out to each active subscriber in the group with that subscriber's own current `subscriptionId`.

Stop watching a worker overview when the page no longer needs live updates.

```csharp
await connection.InvokeAsync(
    "UnwatchWorkerOverview",
    WorkerOverviewSubscriptionId,
    (string?)null);
```

## Raw Event Streams

Raw event subscriptions are for event viewers, diagnostics, and consumers that want low-level `WorkEvent` envelopes rather than worker-overview state updates.

Worker-list pages should prefer `WatchWorkers`, and worker detail pages should generally prefer `WatchWorkerOverview`, over raw event handling. Dashboards and other state-oriented UI should prefer these methods, `WatchView`, or another change-stream-backed surface. Use `WatchEvents` only when the client needs raw event payloads, event types, or event-by-event diagnostic output.

Worker messages are delivered through `workable.event` for single events and `workable.events` for batches.

```csharp
public sealed record WorkableRealtimeEvent(
    DateTimeOffset OccurredAt,
    string? WorkSystemName,
    WorkerId? WorkerId,
    string? WorkDefinitionName,
    WorkSubjectId? SubjectId,
    WorkConcurrencyKey? ConcurrencyKey,
    IReadOnlyList<WorkIdentifier> Identifiers,
    string EventType,
    JsonElement? Data);
```

```csharp
public sealed record WorkableRealtimeEventBatch(
    DateTimeOffset SentAt,
    IReadOnlyList<WorkableRealtimeEvent> Events);
```

The event data follows the payloads documented in [Work Observability](../concepts/observability.md).

Workflow lifecycle events use the same transport envelope and can be filtered by workflow definition name and workflow identifiers:

- `workflow.started`
- `workflow.resume`
- `workflow.pause`
- `workflow.cancel`
- `workflow.step.updated`
- `workflow.paused`
- `workflow.blocked`
- `workflow.completed`
- `workflow.failed`
- `workflow.canceled`

The event envelope keeps `WorkDefinitionName` equal to the workflow definition name and includes identifiers such as `workflow-run`, `workflow-definition`, and, when relevant, `workflow-step`.

### Event Filters

Use `WatchEvents` to subscribe to filtered event streams for a system.

```csharp
await connection.InvokeAsync(
    "WatchEvents",
    new WorkableRealtimeEventCriteria(
        EventTypes: ["worker.completed", "worker.failed"],
        DefinitionNames: [sendWelcomeEmail.Name],
        Keys:
        [
            new WorkableRealtimeEventKeyCriteria(
                WorkKeyKind.Identifier,
                "order",
                "order-789")
        ]),
    (string?)null);
```

The server applies definition, key, and event-type filters before constructing lazy event payloads when possible. This keeps filtered event viewers cheap during bursts.

Workflow event filters use the same shape:

```csharp
await connection.InvokeAsync(
    "WatchEvents",
    new WorkableRealtimeEventCriteria(
        EventTypes: ["workflow.completed"],
        DefinitionNames: ["orders.fulfillment"],
        Keys:
        [
            new WorkableRealtimeEventKeyCriteria(
                WorkKeyKind.Identifier,
                "workflow-run",
                runId.ToString("D"))
        ]),
    (string?)null);
```

Stop watching with the same criteria:

```csharp
await connection.InvokeAsync(
    "UnwatchEvents",
    criteria,
    (string?)null);
```

### Event Batching

The realtime broadcaster coalesces bursts into batches. This reduces SignalR send overhead during high-volume event spikes.

- `BatchTimeWindow` controls how long the broadcaster waits to collect additional events after receiving the first event in a normal burst.
- `LiveTimeWindow` controls worker-overview change coalescing so detail screens can feel live without sending one SignalR message per worker state change.
- `MinimumTimeWindow` prevents accidentally configuring an overly chatty event stream; smaller positive windows are raised to this value.
- `EventMaxBatchSize` caps the number of events in one batch.
- `EventSubscriptionCapacity` caps the number of individual events buffered by each active event subscription group before the configured overflow behavior applies.
- `EventOverflowBehavior` controls what the per-subscription channel does when it reaches capacity.
- A single collected event is sent through `workable.event`.
- Multiple collected events are sent through `workable.events`.
- Event order is preserved inside the batch.

The defaults are a 1 second batch window, a 100ms live window, a 100ms minimum time window, 512 events per batch, 16,384 buffered events per active event subscription group, and `DropWrite` for raw event subscriptions.

The chosen time window is also the send pace during bursts. If the batch reaches `EventMaxBatchSize` before the window expires, the broadcaster waits out the remaining window before sending. That gives the bounded event subscription channel room to absorb overflow according to `EventOverflowBehavior` instead of turning a large burst into a tight loop of SignalR sends.

`DropWrite` remains the default for raw SignalR event viewers because those streams are observational and bounded. When a raw event subscription is already full, lazy event payloads can be skipped before construction, which keeps high-throughput worker execution from paying to produce events the browser will never inspect.

Batching changes transport shape, not event semantics. Clients should handle both methods and process each event individually.

## Local Debug Routes

When the HTTP adapter is running in `Development`, or when the configured listener URLs are all loopback-only, Workable also exposes local realtime debug routes:

```http
GET /workable/debug/realtime
GET /workable/debug/realtime?connectionId=abc123
GET /workable/systems/fulfillment/debug/realtime
```

These endpoints are intentionally for local troubleshooting. In non-development environments, Workable registers them only for loopback-only listener configurations, and each request must also come from a loopback address. Other callers receive `404 Not Found`.

They expose:

- active raw event, named-view, and worker-overview subscriptions
- current group membership and normalized worker-overview criteria
- worker-overview lifecycle state such as `isStreaming`, `streamingStartedAt`, `streamingStoppedAt`, `lastActivityAt`, and `lastError`
- worker-overview change-stream queue diagnostics, such as `capacity`, `queuedCount`, `peakQueuedCount`, `acceptedChangeCount`, `deliveredChangeCount`, `coalescedChangeCount`, and `droppedChangeCount`

Use the optional `connectionId` filter when you need to match one browser tab or one SignalR connection precisely instead of inspecting the whole system snapshot.

## Component View Updates

Overview-style clients can subscribe to the same component-view request shape used by the HTTP API. Use `WatchWorkers` for the common live worker-list case; use the generic `WatchView` method for overview dashboards, workflow views, diagnostics, and custom named views.

```csharp
const string OverviewSubscriptionId = "overview-main";

connection.On<WorkableRealtimeViewEnvelope<WorkComponentQueryResult>>("workable.view", envelope =>
{
    if (!string.Equals(envelope.SubscriptionId, OverviewSubscriptionId, StringComparison.Ordinal))
    {
        return;
    }

    // Replace the visible component data with envelope.Result.
});

await connection.StartAsync();
await connection.InvokeAsync(
    "WatchView",
    OverviewSubscriptionId,
    "overview",
    new WorkViewCriteria(
        Components:
        [
            new("system", "system"),
            new("workers", "workers", Shape: WorkComponentShapes.Compact),
            new("throughput", "throughput", Shape: WorkComponentShapes.Standard)
        ]),
    (string?)null);
```

`WatchView` immediately sends the current `WorkableRealtimeViewEnvelope<WorkComponentQueryResult>` to the caller. The envelope includes the caller-supplied `subscriptionId`, the normalized `viewName`, and the `result`. After that, the server coalesces Workable events and publishes refreshed results on the publish interval.

View subscriptions are grouped by system id, view name, scope, component ids, component types, shapes, options, and effective read visibility. Connections with the same normalized request and the same readable work set share one server recomputation per publish tick. The client-supplied `subscriptionId` is the logical handle for one live view stream on a SignalR connection. A single SignalR connection can keep multiple view subscriptions active at once as long as each one has its own `subscriptionId`. Reusing the same `subscriptionId` replaces that logical view watch with the new normalized request.

SignalR view payloads use the same component efficiency contract as HTTP:

- hidden panels are omitted from the pushed component map
- `compact`, `standard`, and `detailed` shapes are normalized the same way as HTTP
- per-component errors are returned inside the component result
- unknown views return an error component rather than failing the hub connection

Most view groups publish only after the read-model sequence advances. View groups that include `throughput` publish on the normal view interval even when the read model is caught up, because zero-activity buckets are still meaningful chart data and need to advance the visible time window.

Workflow operator views also use `WatchView`. These views refresh when either the workflow runtime changes or the child-worker read model changes, so list and detail screens stay current while a workflow is dispatching, waiting, stopping, failing, or watching child workers settle.

```csharp
const string WorkflowRunsSubscriptionId = "workflow-runs-main";

await connection.InvokeAsync(
    "WatchView",
    WorkflowRunsSubscriptionId,
    "workflow-runs",
    new WorkViewCriteria(
        Components:
        [
            new(
                "workflowRuns",
                "workflowRuns",
                Options: JsonSerializer.SerializeToElement(new
                {
                    includeFinal = true,
                    definitionName = "orders.fulfillment",
                    childSampleSize = 5
                }),
                Shape: WorkComponentShapes.Detailed)
        ]),
    (string?)null);
```

```csharp
const string WorkflowRunSubscriptionId = "workflow-run-detail";

await connection.InvokeAsync(
    "WatchView",
    WorkflowRunSubscriptionId,
    "workflow-run",
    new WorkViewCriteria(
        Components:
        [
            new(
                "workflowRun",
                "workflowRun",
                Options: JsonSerializer.SerializeToElement(new
                {
                    runId = runId.ToString("D"),
                    childSampleSize = 5
                }),
                Shape: WorkComponentShapes.Detailed)
        ]),
    (string?)null);
```

`workflow-runs` uses one `workflowRuns` component with these options:

- `includeFinal`
- `definitionName`
- `childSampleSize`

`workflow-run` uses one `workflowRun` component with these options:

- `runId`
- `childSampleSize`

Stop watching a view when the page no longer needs live updates.

```csharp
await connection.InvokeAsync("UnwatchView", OverviewSubscriptionId, (string?)null);
```

## Diagnostics View Updates

System health chrome can subscribe to the `diagnostics` view without adding diagnostics data to the overview payload. The default diagnostics publish interval is 750ms and only active diagnostics view groups are published. See [Work Diagnostics](../concepts/diagnostics.md) for field meanings and warning guidance.

```csharp
const string DiagnosticsSubscriptionId = "diagnostics-alerts";

await connection.InvokeAsync(
    "WatchView",
    DiagnosticsSubscriptionId,
    "diagnostics",
    new WorkViewCriteria(
        Components:
        [
            new(
                "readModelDiagnostics",
                "readModelDiagnostics",
                Options: JsonSerializer.SerializeToElement(new { warningThreshold = 100 }),
                Shape: WorkComponentShapes.Compact)
        ]),
    (string?)null);
```

Use the compact shape for alert indicators and the detailed shape for expanded diagnostics panels. Supported diagnostics components are `systemDiagnostics`, `queueDiagnostics`, `readModelDiagnostics`, `retentionDiagnostics`, `concurrencyDiagnostics`, `durabilityDiagnostics`, and `idempotencyDiagnostics`.

Compact component payloads include the values needed for the notification tray:

- `queueDiagnostics`: rejected-work counts, alertable rejected-work counts, and last rejection details.
- `readModelDiagnostics`: pending update count, read-model lag state, threshold, and projector failure fields.
- `retentionDiagnostics`: tracked final worker count, scheduled purge count, oldest due purge age, threshold, and scheduler failure fields.
- `concurrencyDiagnostics`: deferred start count, oldest deferred start age, last released count, and threshold.
- `durabilityDiagnostics`: accepted waiter count, pending cleanup count, their oldest ages, thresholds, and durable reader, lease renewal, and cleanup failure fields.
- `idempotencyDiagnostics`: duplicate rejection count and the storage mode that rejected the latest duplicate.

Diagnostics components can set `publishMode` in their options:

- `alertChanges` only pushes compact diagnostics when the alert state changes, such as normal-to-behind, behind-to-normal, severity band changes, queue rejection changes, or background-loop failure changes. This is intended for always-on notification indicators.
- `continuous` pushes on every diagnostics publish tick while the subscription is active. This is intended for visible diagnostics panels.

Threshold options are component-specific: `warningThreshold` controls read-model pending updates, `warningSeconds` controls retention lag and concurrency lag, and durability accepts `acceptedWorkerWarningSeconds` and `cleanupWarningSeconds`.

For example, an always-on alert indicator can stay quiet while healthy:

```csharp
const string DiagnosticsAlertSubscriptionId = "diagnostics-alert-tray";

await connection.InvokeAsync(
    "WatchView",
    DiagnosticsAlertSubscriptionId,
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

## Hub Methods

The hub exposes these observability methods:

```csharp
Task WatchView(string subscriptionId, string viewName, WorkViewCriteria? criteria = null, string? systemName = null);
Task UnwatchView(string subscriptionId, string? systemName = null);
Task WatchWorkers(string subscriptionId, WorkViewCriteria? criteria = null, string? systemName = null);
Task UnwatchWorkers(string subscriptionId, string? systemName = null);
Task WatchWorkerOverview(string subscriptionId, string workerId, WorkWorkerOverviewRealtimeCriteria? criteria = null, string? systemName = null);
Task UnwatchWorkerOverview(string subscriptionId, string? systemName = null);
Task WatchEvents(WorkableRealtimeEventCriteria? criteria = null, string? systemName = null);
Task UnwatchEvents(WorkableRealtimeEventCriteria? criteria = null, string? systemName = null);
```
