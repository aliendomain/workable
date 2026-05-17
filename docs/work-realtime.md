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
          "features": ["worker-events", "component-views"]
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

## Worker Details

Worker detail pages should load their initial snapshot through HTTP, then subscribe to realtime worker events.

```csharp
HubConnection connection = new HubConnectionBuilder()
    .WithUrl("/workable/realtime")
    .Build();

connection.On<WorkableRealtimeEvent>("workable.event", workEvent =>
{
    // Update the visible worker timeline, logs, action history, output, or state.
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

Worker messages are delivered through the `workable.event` client method with this shape:

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

The event data follows the payloads documented in [Work Observability](https://github.com/aliendomain/workable/blob/main/docs/work-observability.md).

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

Stop watching a view when the page no longer needs live updates.

```csharp
await connection.InvokeAsync("UnwatchView", "overview", (string?)null);
```

## Hub Methods

The hub exposes these observability methods:

```csharp
Task WatchWorker(string workerId, string? systemName = null);
Task UnwatchWorker(string workerId, string? systemName = null);
Task WatchView(string viewName, WorkViewCriteria? criteria = null, string? systemName = null);
Task UnwatchView(string viewName, string? systemName = null);
Task WatchSystem(string? systemName = null);
Task UnwatchSystem(string? systemName = null);
```

`WatchSystem` receives all realtime worker events for the selected system through the same `workable.event` client method.
