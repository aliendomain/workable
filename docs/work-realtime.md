# Workable Realtime

Workable can stream worker events and coalesced dashboard summaries to ASP.NET Core clients through the `Workable.SignalR` adapter package.

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
    options.DashboardPublishInterval = TimeSpan.FromSeconds(2);
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
          "features": ["worker-events", "system-dashboard"]
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

## Dashboard Updates

Dashboard pages should load their initial summaries through HTTP, then subscribe to coalesced realtime dashboard updates.

```csharp
connection.On<WorkableRealtimeDashboard>("workable.dashboard", dashboard =>
{
    // Update iteration counts, common key types, and recent iteration lists.
});

await connection.StartAsync();
await connection.InvokeAsync("WatchDashboard", (string?)null);
```

`WatchDashboard` immediately sends the current dashboard summary to the caller. After that, the server publishes another summary when Workable events occur and the dashboard publish interval elapses.

Dashboard messages use this shape:

```csharp
public sealed record WorkableRealtimeDashboard(
    WorkSystemId SystemId,
    string? SystemName,
    WorkSystemState SystemState,
    int DefinitionCount,
    int ActiveWorkerCount,
    int FinalWorkerCount,
    int FailedWorkerCount,
    IReadOnlyDictionary<WorkerState, int> WorkerCountByState,
    int CurrentIterationCount,
    int CompletedIterationCount,
    int FailedIterationCount,
    int CanceledIterationCount,
    IReadOnlyDictionary<WorkCompletionStatus, int> IterationCountByStatus,
    IReadOnlyList<WorkIterationKeyTypeFacet> CommonKeyTypes,
    IReadOnlyList<WorkerOverviewItem> FailedWorkers,
    IReadOnlyList<WorkerIterationOverviewItem> FailedIterations,
    IReadOnlyList<WorkerIterationOverviewItem> CompletedIterations,
    DateTimeOffset UpdatedAt);
```

`DefinitionCount` is the number of definitions that currently have queued or active workers. `CurrentIterationCount` is the number of iterations with `WorkCompletionStatus.Executing`.

Dashboard messages use the same worker-state and iteration-oriented activity shape as the default `POST /workable/views/overview` response, plus `SystemId` and `UpdatedAt`.

## Hub Methods

The hub exposes these observability methods:

```csharp
Task WatchWorker(string workerId, string? systemName = null);
Task UnwatchWorker(string workerId, string? systemName = null);
Task WatchDashboard(string? systemName = null);
Task UnwatchDashboard(string? systemName = null);
Task WatchSystem(string? systemName = null);
Task UnwatchSystem(string? systemName = null);
```

`WatchSystem` receives all realtime worker events for the selected system through the same `workable.event` client method.
