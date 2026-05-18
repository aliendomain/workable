# Workable Realtime

Workable can stream worker events and coalesced component-view updates to ASP.NET Core clients through the `Workable.SignalR` adapter package.

The realtime adapter is observability-only. Queueing work, querying snapshots, and sending worker actions remain in the .NET and HTTP API surfaces. SignalR clients subscribe to updates and receive messages when the underlying Workable event stream changes.

## Setup

Register and map the SignalR adapter from the host application.

```csharp
builder.Services.AddWorkableSignalR();

app.MapWorkableSignalR();
```

The default hub path is `/workable/realtime`.

```csharp
builder.Services.AddWorkableSignalR(options =>
{
    options.HubPath = "/internal/work/realtime";
    options.PublishInterval = TimeSpan.FromSeconds(2);
    options.DiagnosticsPublishInterval = TimeSpan.FromMilliseconds(250);
    options.EventBatchWindow = TimeSpan.FromSeconds(1);
    options.EventMinimumBatchWindow = TimeSpan.FromMilliseconds(100);
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

`AddWorkableSignalR` registers one background broadcaster that subscribes once to each hosted Workable system. Browser connections join SignalR groups; they do not create one Workable event-stream subscription per browser.

## Capability Discovery

`Workable.HttpApi` exposes capability information through the systems endpoint so clients can build a system picker and discover whether realtime is available for each system.

```http
GET /workable/systems
```

Each listed system includes its realtime capability.

When `Workable.SignalR` is registered:

```json
{
  "systems": [
    {
      "id": { "value": "11111111-1111-1111-1111-111111111111" },
      "name": null,
      "state": "Started",
      "isDefault": true,
      "capabilities": {
        "realtime": {
          "enabled": true,
          "transport": "signalr",
          "hubPath": "/workable/realtime",
          "features": ["worker-events", "component-views", "diagnostics-view"]
        }
      }
    }
  ]
}
```

When `Workable.SignalR` is not registered:

```json
{
  "systems": [
    {
      "id": { "value": "11111111-1111-1111-1111-111111111111" },
      "name": null,
      "state": "Started",
      "isDefault": true,
      "capabilities": {
        "realtime": {
          "enabled": false,
          "transport": null,
          "hubPath": null,
          "features": null
        }
      }
    }
  ]
}
```

## Worker Events

Worker detail pages should load their initial snapshot through HTTP, then subscribe to realtime worker events.

```csharp
HubConnection connection = new HubConnectionBuilder()
    .WithUrl("/workable/realtime")
    .Build();

connection.On<WorkableRealtimeEvent>("workable.event", workEvent =>
{
    // Update the visible worker timeline, logs, action history, output, or state.
});

connection.On<WorkableRealtimeEventBatch>("workable.events", batch =>
{
    foreach (var workEvent in batch.Events)
    {
        // Handle each event in its original stream order.
    }
});

await connection.StartAsync();
await connection.InvokeAsync(
    "WatchWorker",
    workerId.ToString("D"),
    (string?)null);
```

For a named system, pass the system name as the second argument.

```csharp
await connection.InvokeAsync(
    "WatchWorker",
    workerId.ToString("D"),
    "email");
```

Stop watching a worker when the page no longer needs live updates.

```csharp
await connection.InvokeAsync(
    "UnwatchWorker",
    workerId.ToString("D"),
    (string?)null);
```

Worker messages are delivered through `workable.event` for single events and `workable.events` for batches.

```csharp
public sealed record WorkableRealtimeEvent(
    DateTimeOffset OccurredAt,
    WorkSystemId WorkSystemId,
    WorkerId? WorkerId,
    WorkDefinitionId? DefinitionId,
    WorkSubjectId? SubjectId,
    WorkConcurrencyKey? ConcurrencyKey,
    IReadOnlyList<WorkIdentifier> Identifiers,
    WorkOrigin? Origin,
    string EventType,
    JsonElement? Data,
    IReadOnlyList<WorkMessage> Messages);
```

```csharp
public sealed record WorkableRealtimeEventBatch(
    DateTimeOffset SentAt,
    IReadOnlyList<WorkableRealtimeEvent> Events);
```

The event data follows the payloads documented in [Work Observability](https://github.com/aliendomain/workable/blob/main/docs/work-observability.md).

### Event Filters

Use `WatchEvents` to subscribe to filtered event streams for a system.

```csharp
await connection.InvokeAsync(
    "WatchEvents",
    new WorkableRealtimeEventCriteria(
        EventTypes: ["worker.completed", "worker.failed"],
        DefinitionIds: [sendWelcomeEmail.Id.Value.ToString("D")],
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

Stop watching with the same criteria:

```csharp
await connection.InvokeAsync(
    "UnwatchEvents",
    criteria,
    (string?)null);
```

`WatchWorker` is a convenience subscription for one worker id. `WatchSystem` subscribes to all worker events for the selected system.

### Event Batching

The realtime broadcaster coalesces bursts into batches. This reduces SignalR send overhead during high-volume event spikes.

- `EventBatchWindow` controls how long the broadcaster waits to collect additional events after receiving the first event in a burst.
- `EventMinimumBatchWindow` prevents accidentally configuring an overly chatty event stream; smaller positive windows are raised to this value.
- `EventMaxBatchSize` caps the number of events in one batch.
- `EventSubscriptionCapacity` caps the number of individual events buffered by each active event subscription group before the configured overflow behavior applies.
- `EventOverflowBehavior` controls what the per-subscription channel does when it reaches capacity.
- A single collected event is sent through `workable.event`.
- Multiple collected events are sent through `workable.events`.
- Event order is preserved inside the batch.

The defaults are a 1 second batch window, a 100ms minimum batch window, 512 events per batch, 16,384 buffered events per active event subscription group, and `DropWrite` overflow behavior.

The batch window is also the send pace during bursts. If the batch reaches `EventMaxBatchSize` before the window expires, the broadcaster waits out the remaining window before sending. That gives the bounded event subscription channel room to absorb overflow according to `EventOverflowBehavior` instead of turning a large burst into a tight loop of SignalR sends.

`DropWrite` is the default for SignalR because realtime event viewers are observational and bounded. When a SignalR event subscription is already full, lazy event payloads can be skipped before construction, which keeps high-throughput worker execution from paying to produce events the browser will never inspect. Use `DropOldest` only when keeping the newest event samples matters more than minimizing writer-path overhead.

Batching changes transport shape, not event semantics. Clients should handle both methods and process each event individually.

## Component View Updates

Overview-style clients can subscribe to the same component-view request shape used by the HTTP API.

```csharp
connection.On<WorkComponentQueryResult>("workable.view", view =>
{
    // Replace the visible component data with the pushed component map.
});

await connection.StartAsync();
await connection.InvokeAsync(
    "WatchView",
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

`WatchView` immediately sends the current `WorkComponentQueryResult` to the caller. After that, the server coalesces Workable events and publishes refreshed results on the publish interval.

View subscriptions are grouped by system id, view name, scope, component ids, component types, shapes, and options. Connections with the same normalized request share one server recomputation per publish tick. If a client hides a panel, it should call `WatchView` again with that component omitted; if it changes a panel between `compact`, `standard`, and `detailed`, it should call `WatchView` with the new shape. The same SignalR connection stays open while the server swaps the connection between normalized view groups.

SignalR view payloads use the same component efficiency contract as HTTP:

- hidden panels are omitted from the pushed component map
- `compact`, `standard`, and `detailed` shapes are normalized the same way as HTTP
- per-component errors are returned inside the component result
- unknown views return an error component rather than failing the hub connection

Most view groups publish only after the read-model sequence advances. View groups that include `throughput` publish on the normal view interval even when the read model is caught up, because zero-activity buckets are still meaningful chart data and need to advance the visible time window.

Stop watching a view when the page no longer needs live updates.

```csharp
await connection.InvokeAsync("UnwatchView", "overview", (string?)null);
```

## Diagnostics View Updates

System health chrome can subscribe to the `diagnostics` view without adding diagnostics data to the overview payload. The default diagnostics publish interval is 250ms and only active diagnostics view groups are published. See [Work Diagnostics](work-diagnostics.md) for field meanings and warning guidance.

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
                Options: JsonSerializer.SerializeToElement(new { warningThreshold = 100 }),
                Shape: WorkComponentShapes.Compact)
        ]),
    (string?)null);
```

Use the compact shape for alert indicators and the detailed shape for an expanded read-model diagnostics panel. The compact payload includes `pendingUpdateCount`, `isReadModelBehind`, `readModelLagWarningThreshold`, and projector failure fields. The detailed payload also includes the full `readModel` diagnostics object.

Diagnostics components can set `publishMode` in their options:

- `alertChanges` only pushes compact diagnostics when the alert state changes, such as normal-to-behind, behind-to-normal, severity band changes, or projector failure changes. This is intended for always-on notification indicators.
- `continuous` pushes on every diagnostics publish tick while the subscription is active. This is intended for visible diagnostics panels.

For example, an always-on alert indicator can stay quiet while healthy:

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

## Hub Methods

The hub exposes these observability methods:

```csharp
Task WatchWorker(string workerId, string? systemName = null);
Task UnwatchWorker(string workerId, string? systemName = null);
Task WatchView(string viewName, WorkViewCriteria? criteria = null, string? systemName = null);
Task UnwatchView(string viewName, string? systemName = null);
Task WatchEvents(WorkableRealtimeEventCriteria? criteria = null, string? systemName = null);
Task UnwatchEvents(WorkableRealtimeEventCriteria? criteria = null, string? systemName = null);
Task WatchSystem(string? systemName = null);
Task UnwatchSystem(string? systemName = null);
```

`WatchSystem` receives all realtime worker events for the selected system through the same `workable.event` and `workable.events` client methods.

## Admin Event Viewer

The Workable admin UI includes an event viewer in the system tools menu near the notification tray. It is intended for validating event shape, watching filtered event streams, and inspecting realtime traffic during performance work.

The event viewer can:

- enable or disable capture while the window is open
- keep a bounded local list of received SignalR batches
- clear captured batches
- filter by multiple event types
- filter by one or more definitions
- filter by subject, concurrency key, identifier, or any key with a type/value pair
- inspect colored JSON for one selected event
- show batched events as selectable rows while preserving batch metadata
- navigate the selected event with the keyboard
- resize the batch/event/JSON workspace, collapse the batch and filter panes, compact the window, or maximize it

The viewer only subscribes while it is open, capture is enabled, and at least one event type is selected. It starts with no event types selected so opening the tool does not accidentally subscribe to the full event stream. Closing the viewer or disabling capture removes the SignalR event subscription.

Filters are sent to the server as SignalR criteria, so filtered-out events do not need to be sent to the browser and can often be skipped before payload construction. The catalog filter is navigational: category rows move through the catalog tree, and definition rows must be checked explicitly. Key filters are matched against subject, concurrency key, identifiers, or any key when the key kind is omitted.

The left pane shows received SignalR messages. A `workable.event` message appears as a single-event batch, and a `workable.events` message appears as one batch row. Selecting a batch shows its events in a table above the JSON view. Selecting a row shows that individual event's JSON. Large arrays in the JSON viewer are capped in the expanded display so event inspection remains usable during large bursts.

## Admin Realtime Payload Viewer

The system tools menu also includes a realtime payload viewer for component-view traffic. It is separate from the worker event viewer.

The payload viewer can:

- capture only while its floating window is open and capture is enabled
- keep a bounded local list of received component-view messages
- clear captured messages
- filter the local message list by realtime subscription source
- inspect colored JSON for overview and diagnostics component-view payloads
- collapse the message list, collapse JSON to the component level, compact the window, or maximize it

This tool is for validating the `workable.view` payloads produced by overview and diagnostics subscriptions. It does not create worker event subscriptions and does not affect the event viewer's filters.
