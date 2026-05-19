# Work Observability

## Intent

Workable events provide a live, low-cost view of worker activity. They are intended for notification, filtering, timelines, operational tools, and realtime UI refresh triggers. They are not intended to replace detail queries.

Each `IWorkSystem` exposes an event stream:

```csharp
IWorkEventStream events = workSystem.Events;
```

The stream is subscription based. A subscription receives events published after the subscription is created. Events are not replayed to later subscriptions.

## Event Shape

Each `WorkEvent` identifies where the event came from and what happened.

```csharp
public sealed record WorkEvent(
    DateTimeOffset OccurredAt,
    WorkSystemId WorkSystemId,
    WorkerId? WorkerId,
    WorkDefinitionId? DefinitionId,
    WorkSubjectId? SubjectId,
    WorkConcurrencyKey? ConcurrencyKey,
    IReadOnlySet<WorkIdentifier> Identifiers,
    WorkOrigin? Origin,
    string EventType,
    JsonElement? Data,
    IReadOnlyList<WorkMessage> Messages);
```

The envelope is stable and filterable. It carries the system, worker, definition, subject, concurrency key, identifiers, origin, event type, thin event data, and event messages.

Event data is intentionally thin. Worker lifecycle events include a worker summary and lightweight `keys` when keys are available. They do not include worker input, output, messages, logs, full iteration history, or full worker detail. Query worker detail when a UI needs those heavier fields.

`worker.purge` is even smaller. It carries only the purge timestamp and purged worker ids; it does not include a worker summary or keys.

## Event Types

Common event types include:

- `worker.queued`
- `worker.started`
- `worker.completed`
- `worker.failed`
- `worker.interrupted`
- `worker.canceled`
- `worker.waiting`
- `worker.retrying`
- `worker.iteration.completed`
- `worker.iteration.failed`
- `worker.recurrence.circuit_opened`
- `worker.log`
- `worker.start`
- `worker.pause`
- `worker.cancel`
- `worker.push`
- `worker.purge`
- `worker.reconfigured`

Action events such as `worker.cancel` describe the immediate action outcome. Completion events such as `worker.completed`, `worker.failed`, `worker.interrupted`, and `worker.canceled` describe the lifecycle result. Shutdown interruption publishes `worker.interrupted`; explicit API cancellation publishes the cancel action event and then the canceled completion event.

## Payloads

`Data` uses camel-case JSON property names. Enum values are strings. Null properties are omitted.

Most worker events include this shape:

```json
{
  "worker": {
    "id": { "value": "00000000-0000-0000-0000-000000000000" },
    "revision": 1,
    "stateSequence": 1,
    "definitionId": { "value": "00000000-0000-0000-0000-000000000000" },
    "definitionName": "email.welcome.send",
    "definitionCategory": "Email",
    "subjectId": { "type": "user", "value": "user-123" },
    "concurrencyKey": { "type": "tenant", "value": "tenant-456" },
    "identifiers": [
      { "type": "order", "value": "order-789" }
    ],
    "state": "Queued",
    "createdAt": "2026-05-17T12:00:00Z",
    "updatedAt": "2026-05-17T12:00:00Z",
    "version": {
      "workerId": { "value": "00000000-0000-0000-0000-000000000000" },
      "revision": 1
    }
  },
  "keys": [
    { "kind": "Subject", "type": "user", "value": "user-123" },
    { "kind": "ConcurrencyKey", "type": "tenant", "value": "tenant-456" },
    { "kind": "Identifier", "type": "order", "value": "order-789" }
  ]
}
```

Additional event-specific fields are added when useful:

- Completion events include `completionStatus`.
- Action events include `action` and `actionStatus`.
- Reconfiguration events include `reconfigurationStatus` and the requested `reconfiguration`.
- Waiting events include `recurrenceInterval`.
- Retrying events include `retryDelay`.
- Iteration events include a thin `iteration` object with sequence, timestamps, execution duration, and status.

Example completed event:

```json
{
  "worker": {},
  "keys": [],
  "completionStatus": "Completed"
}
```

Example iteration event:

```json
{
  "worker": {},
  "keys": [],
  "completionStatus": "Completed",
  "iteration": {
    "sequence": 1,
    "startedAt": "2026-05-17T12:00:00Z",
    "completedAt": "2026-05-17T12:00:01Z",
    "executionDuration": "00:00:01",
    "status": "Completed"
  }
}
```

Example purge event:

```json
{
  "purgedAt": "2026-05-17T12:00:00Z",
  "workerIds": [
    { "value": "00000000-0000-0000-0000-000000000000" }
  ]
}
```

`worker.log` events are thin notifications that a worker log was captured. Log text is retained on `WorkerSnapshot.Logs` up to the worker's configured logging buffer size, not embedded in the event payload.

## Filtering

Use `WorkEventFilter` to subscribe only to events the caller can use.

```csharp
await using var subscription = workSystem.Events.Subscribe(
    new WorkEventFilter(
        DefinitionIds: new HashSet<WorkDefinitionId> { sendWelcomeEmail.Id },
        Keys: new HashSet<WorkEventKeyFilter>
        {
            new(WorkKeyKind.Identifier, "order", "order-789")
        },
        EventTypes: new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "worker.completed",
            "worker.failed"
        }));
```

Filters can match:

- one worker id
- one definition id or a set of definition ids
- one subject id
- one concurrency key
- one identifier
- a set of key filters
- one event type or a set of event types

Key filters can target a specific key kind (`Subject`, `ConcurrencyKey`, or `Identifier`) or omit the kind to match any key with the same type and value.

Filters are applied before events enter the subscription buffer. For lazy-published events, Workable checks cheap metadata before constructing the event body, so filtered-out events do not pay the JSON payload cost.

## Buffering

Each subscription has a bounded buffer. The default capacity is `256`, and the default overflow behavior is `DropOldest`.

```csharp
await using var subscription = workSystem.Events.Subscribe(
    options: new WorkEventSubscriptionOptions(
        Capacity: 512,
        OverflowBehavior: WorkEventOverflowBehavior.DropOldest));
```

Overflow behavior options are:

- `DropOldest`: keep newer events when the reader falls behind.
- `DropNewest`: preserve older buffered events when the reader falls behind.
- `DropWrite`: reject the incoming event when the buffer is full.

For lazy-published events, a full `DropWrite` subscription can be skipped before the event body is constructed. This is useful for lossy diagnostic subscribers that should not add materialization cost to the worker lifecycle when they fall behind.

One slow subscriber does not block other subscribers.

## Reading Events

Create the subscription before the event you want to observe.

```csharp
await using var subscription = workSystem.Events.Subscribe(
    new WorkEventFilter(
        DefinitionId: sendWelcomeEmail.Id,
        EventType: "worker.completed"));

await using var reader = subscription.Read(cancellationToken).GetAsyncEnumerator();

await workSystem.Queue.Enqueue("email.welcome.send", cancellationToken: cancellationToken);

if (await reader.MoveNextAsync())
{
    WorkEvent completed = reader.Current;
}
```

Dispose the subscription, cancel the read, or dispose the system to remove the subscription.

## Ownership Rules

- Events are delivered only to subscriptions active at publish time.
- Subscriptions are independent.
- A subscription belongs to the system that created it.
- Disposing a subscription completes its reader.
- Canceling a read removes the subscription.
- Detail UIs should treat events as change notifications and query detail when they need full input, output, messages, logs, action history, iterations, or profile data.
