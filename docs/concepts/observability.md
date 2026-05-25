# Work Observability

## Intent

Workable events provide a live, low-cost view of worker activity. They are intended for notification, filtering, timelines, operational tools, and realtime UI refresh triggers. They are not intended to replace detail queries.

Each `IWorkSystem` exposes an event stream:

```csharp
IWorkEventStream events = workSystem.Events;
```

The stream is subscription based. A subscription receives events published after the subscription is created. Events are not replayed to later subscriptions.

The intended usage pattern is:

1. subscribe
2. react to lightweight events
3. query detail when the consumer actually needs heavier state

That keeps the event stream cheap while still letting richer UIs stay accurate.

## Subscription Contract

`Subscribe(...)` returns an `IWorkEventSubscription`. The subscription itself is just the owner of the stream registration. `Read(...)` is what produces the async event sequence:

```csharp
await using var subscription = workSystem.Events.Subscribe();
await using var reader = subscription.Read(cancellationToken).GetAsyncEnumerator();
```

Important semantics:

- the subscription starts receiving only future events
- each subscription owns its own bounded buffer
- canceling the read loop ends event reads; disposing the reader or subscription removes the subscription
- disposing the subscription removes the subscription
- one slow subscriber does not block other subscribers

This is a live notification stream, not a replay log or durable event store.

## Event Shape

Each `WorkEvent` identifies where the event came from and what happened.

```csharp
public sealed record WorkEvent(
    DateTimeOffset OccurredAt,
    WorkSystemId WorkSystemId,
    string? WorkSystemName,
    WorkerId? WorkerId,
    WorkDefinitionId? WorkDefinitionId,
    string? WorkDefinitionName,
    WorkSubjectId? SubjectId,
    WorkConcurrencyKey? ConcurrencyKey,
    IReadOnlySet<WorkIdentifier> Identifiers,
    string EventType,
    JsonElement? Data);
```

The envelope is stable and filterable. It carries the system id, optional system name, worker, definition id and name, subject, concurrency key, identifiers, event type, and thin event data.

Event data is intentionally thin. Worker lifecycle events include a worker summary and lightweight `keys` when keys are available. They do not include worker input, output, worker messages, logs, full iteration history, or full worker detail. Query worker detail when a consumer needs those heavier fields.

`worker.purge` is even smaller. It carries only the purge timestamp and purged worker ids; it does not include a worker summary or keys.

## Event Types

The current worker event types are:

- `worker.queued`: a worker was accepted into the system.
- `worker.start`: a start action was applied to a worker.
- `worker.started`: execution for an iteration began.
- `worker.pause`: a pause action was applied to a worker.
- `worker.paused`: execution completion resolved into the paused state.
- `worker.cancel`: a cancel action was applied to a worker.
- `worker.canceled`: execution completion resolved into the canceled state.
- `worker.completed`: execution completion resolved successfully.
- `worker.failed`: execution completion resolved as failed.
- `worker.interrupted`: execution completion resolved as interrupted, such as shutdown or lease loss.
- `worker.waiting`: a recurring worker finished an iteration and is waiting for its next recurrence interval.
- `worker.retrying`: a transient exception failure moved the worker into retry delay.
- `worker.iteration.completed`: an iteration finished successfully.
- `worker.iteration.failed`: an iteration finished as failed.
- `worker.recurrence.circuit_opened`: recurring execution stopped because the recurrence circuit breaker opened.
- `worker.log`: a worker log entry was captured.
- `worker.push`: a push action was applied to a waiting or retrying worker.
- `worker.purge`: one or more final workers were removed from retention tracking and memory.
- `worker.reconfigured`: a worker reconfiguration request was accepted.

Action events such as `worker.cancel`, `worker.pause`, and `worker.push` describe the immediate action outcome. Completion events such as `worker.completed`, `worker.failed`, `worker.paused`, `worker.interrupted`, and `worker.canceled` describe the lifecycle result. Shutdown interruption publishes `worker.interrupted`; explicit API cancellation publishes the cancel action event and then the canceled completion event.

## Payloads

`Data` uses camel-case JSON property names. Enum values are strings. Null properties are omitted.

Most worker events share a common thin worker payload and then add a few event-specific fields when needed. The payload families below document the base shape once, then show only the JSON fields each event family adds or changes.

### Base Worker Payload

Used as-is by:

- `worker.started`

Shape:

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
    "stateChangedAt": "2026-05-17T12:00:00Z",
    "updatedAt": "2026-05-17T12:00:00Z",
    "version": {
      "workerId": { "value": "00000000-0000-0000-0000-000000000000" },
      "revision": 1
    },
    "totalExecutionDuration": "00:00:00"
  },
  "keys": [
    { "kind": "Subject", "type": "user", "value": "user-123" },
    { "kind": "ConcurrencyKey", "type": "tenant", "value": "tenant-456" },
    { "kind": "Identifier", "type": "order", "value": "order-789" }
  ]
}
```

Notes:

- `queueDuration` is included after the worker has started at least once. It is omitted for work that is still queued and has never begun execution.
- `nextRunAt` is included when the worker already has a scheduled future run. In practice that means recurring work in the `Waiting` state and transient-retry work in the `Retrying` state.
- `interruptionReason` is included when the worker is in the `Interrupted` state.
- Every payload family below includes this full base worker payload unless noted otherwise.

### Event Origin Payload

Used by events that represent a caller request rather than a passive state transition.

Shape:

```json
{
  "origin": {
    "channel": "HttpApi",
    "actor": {
      "id": "user-123",
      "name": "Greya",
      "email": "greya@example.test"
    },
    "description": "Queue work 'email.welcome.send' through the HTTP API.",
    "url": "/workable/work/email.welcome.send"
  }
}
```

Notes:

- `actor` is omitted when the origin does not carry caller identity.
- `description` and `url` are included only when the source request supplied them.

### Queue Payload

Used by:

- `worker.queued`

Includes the base worker payload plus:

```json
{
  "origin": { "...": "same as Event Origin Payload" }
}
```

Notes:

- `worker.queued` always includes `origin`.
- `origin.actor` is included only when the queue request carried caller identity.

### Log Payload

Used by:

- `worker.log`

Includes the base worker payload plus:

```json
{
  "log": {
    "category": "MyApp.Workers.EmailWelcomeSend",
    "level": "Error",
    "eventId": {
      "id": 42,
      "name": "email_sent"
    },
    "message": "SMTP connection failed while sending the welcome email.",
    "exceptionType": "System.InvalidOperationException",
    "exceptionMessage": "SMTP connection failed."
  }
}
```

Notes:

- `OccurredAt` on the event envelope is the captured log timestamp.
- The event payload includes the core captured log fields.
- `exceptionType` and `exceptionMessage` are present only when the captured log included an exception.
- Retained iteration logs on `WorkerSnapshot.Iterations[*].Logs` carry the same structured log entry fields as the event payload.

### Action Payload

Used by:

- `worker.start`
- `worker.pause`
- `worker.cancel`
- `worker.push`

Includes the base worker payload plus:

```json
{
  "origin": { "...": "same as Event Origin Payload" },
  "action": "Cancel",
  "actionStatus": "Accepted"
}
```

Notes:

- `action` is the action that was applied.
- `actionStatus` is the immediate action outcome, such as `Accepted`, `Conflict`, or `Invalid`.

### Completion Payload

Used by:

- `worker.completed`
- `worker.failed`
- `worker.paused`
- `worker.interrupted`
- `worker.canceled`

Includes the base worker payload plus:

```json
{
  "completionStatus": "Completed"
}
```

### Waiting Payload

Used by:

- `worker.waiting`

Includes the base worker payload plus:

```json
{
  "recurrenceInterval": "00:00:04"
}
```

### Retrying Payload

Used by:

- `worker.retrying`

Includes the base worker payload plus:

```json
{
  "retryDelay": "00:00:00.8000000"
}
```

### Iteration Completion Payload

Used by:

- `worker.iteration.completed`
- `worker.iteration.failed`

Includes the base worker payload plus:

```json
{
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

### Recurrence Circuit Payload

Used by:

- `worker.recurrence.circuit_opened`

Includes the base worker payload plus:

```json
{
  "iteration": {
    "sequence": 3,
    "startedAt": "2026-05-17T12:10:00Z",
    "completedAt": "2026-05-17T12:10:01Z",
    "executionDuration": "00:00:01",
    "status": "Failed"
  }
}
```

Notes:

- This event includes the latest retained iteration snapshot, but it does not add `completionStatus`.

### Reconfiguration Payload

Used by:

- `worker.reconfigured`

Includes the base worker payload plus:

```json
{
  "origin": { "...": "same as Event Origin Payload" },
  "reconfigurationStatus": "Accepted",
  "reconfiguration": {
    "recurrence": {
      "isEnabled": false
    }
  }
}
```

Notes:

- `reconfiguration` contains the accepted change request shape, with only the supplied fields present.

### Purge Payload

Used by:

- `worker.purge`

Shape:

```json
{
  "purgedAt": "2026-05-17T12:00:00Z",
  "workerIds": [
    { "value": "00000000-0000-0000-0000-000000000000" }
  ],
  "origin": { "...": "same as Event Origin Payload" }
}
```

Notes:

- `worker.purge` is the only current event type that does not use the base worker payload.
- `origin` is included only for explicit non-system-user purge requests. Retention and other system-driven purge events omit it.

When a consumer knows the event payload shape, `WorkEvent.DeserializeData<T>()` can deserialize the `Data` field directly:

```csharp
MyCompletedEventData? data = completed.DeserializeData<MyCompletedEventData>();
```

That is a convenience for event consumers that want typed payload access while still working against the stable `WorkEvent` envelope.

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

Choose overflow mode based on what the subscriber values:

- `DropOldest` for dashboards and live operator screens that care more about "current enough" than perfect history
- `DropNewest` for consumers that would rather preserve earlier context while they catch up
- `DropWrite` for lossy subscribers that should never push extra event materialization cost back onto worker execution

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

Dispose the reader, dispose the subscription, or dispose the system to remove the subscription. Canceling a read loop typically leads to reader disposal in normal `await foreach` or `await using` patterns.

## Common Consumer Pattern

Most realtime consumers work best with a two-step pattern:

- use events to know that something changed
- use queries to fetch the exact current detail the UI or tool wants to show

For example, a dashboard can subscribe to `worker.completed`, `worker.failed`, and `worker.reconfigured`, then refresh `SystemWorkerCounts`, `SystemFailedWorkers`, or one `WorkerSnapshot` on demand. That is usually better than trying to treat the event payload itself as the system of record.

## Ownership Rules

- Events are delivered only to subscriptions active at publish time.
- Subscriptions are independent.
- A subscription belongs to the system that created it.
- Disposing a subscription completes its reader.
- Disposing the reader or subscription removes the subscription.
- Detail UIs should treat events as change notifications and query detail when they need full input, output, messages, logs, action history, iterations, or profile data.
