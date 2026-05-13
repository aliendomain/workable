# Work Observability

## Intent

Workable events provide a live view of worker activity.

`IWorkEventStream` is exposed from each `IWorkSystem`:

```csharp
IWorkEventStream events = workSystem.Events;
```

The stream is subscription based. A subscription receives events published after the subscription is created. Each subscription owns its own bounded buffer and is removed when it is disposed or when its reader is canceled.

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

`Origin` describes the trusted boundary that caused the event when Workable knows it. HTTP API requests record an origin with `WorkInvocationChannel.HttpApi`, actor information from `HttpContext.User`, and the request path. ASP.NET Core MCP tool calls record an origin with `WorkInvocationChannel.Mcp` and the same actor extraction when an HTTP context is available. Direct .NET queue and worker operation calls record `WorkInvocationChannel.DotNet` through `IDotNetWorkOriginProvider`; the ASP.NET Core adapter provides an implementation that can read `HttpContext.User` for direct .NET calls made inside an ASP.NET Core request. Worker lifecycle events use the worker's queue origin. Worker action and reconfiguration events use the origin of the action or reconfiguration request.

`Data` is an event-time JSON payload. It always includes a `worker` summary with the worker id, revision, state sequence, definition name, category, current state, subject, concurrency key, identifiers, origin, and timestamps as they were when the event was published. Specific events add more fields so subscribers do not need to immediately call `GetWorker`.

Common event types include:

- `worker.queued`
- `worker.started`
- `worker.completed`
- `worker.failed`
- `worker.canceled`
- `worker.waiting`
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

`worker.log` events are published when worker logging captures a log entry from the executor or from services used during execution.

## Event Data Payloads

`Data` uses camel-case JSON property names. Enum values are strings. Properties with `null` values are omitted.

Every event `Data` payload includes this `worker` object:

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
    "origin": {
      "id": { "value": "00000000-0000-0000-0000-000000000000" },
      "createdAt": "2026-05-11T12:00:00Z",
      "channel": "HttpApi",
      "actor": {
        "id": "user-123",
        "name": "Greya",
        "email": "greya@example.test"
      },
      "description": "Queue work 'email.welcome.send' through HTTP API.",
      "url": "/workable/work/email.welcome.send"
    },
    "state": "Queued",
    "createdAt": "2026-05-11T12:00:00Z",
    "updatedAt": "2026-05-11T12:00:00Z",
    "version": {
      "workerId": { "value": "00000000-0000-0000-0000-000000000000" },
      "revision": 1
    }
  }
}
```

When the worker does not have a subject, concurrency key, origin identifiers, or actor fields, those nullable properties are omitted. `identifiers` is present as an array and may be empty.

The event-specific payloads below abbreviate the common worker object as `"worker": {}` to avoid repeating it. In emitted events, `worker` is the full object shown above.

### `worker.queued`

Published when work is accepted into memory. The payload includes the input used to create the worker.

```json
{
  "worker": {},
  "input": {
    "json": "{\"userId\":\"user-123\"}",
    "clrType": "Sample.SendWelcomeEmailInput, Sample",
    "contentType": "application/json",
    "subjectId": { "type": "user", "value": "user-123" },
    "concurrencyKey": { "type": "tenant", "value": "tenant-456" },
    "identifiers": [
      { "type": "order", "value": "order-789" }
    ]
  }
}
```

### `worker.started`

Published when a worker begins executing. The payload includes the input available to the executor.

```json
{
  "worker": {},
  "input": {
    "json": "{\"userId\":\"user-123\"}",
    "contentType": "application/json"
  }
}
```

### `worker.completed`

Published when a worker completes successfully.

```json
{
  "worker": {},
  "output": {
    "json": "{\"sent\":true}",
    "clrType": "Sample.SendWelcomeEmailOutput, Sample",
    "contentType": "application/json"
  },
  "completionStatus": "Completed"
}
```

### `worker.failed`

Published when a worker finishes in the failed state. Failure messages are carried by the event-level `Messages` collection. If the failure result supplied output, it appears in `output`.

```json
{
  "worker": {},
  "output": {
    "json": "{\"retryable\":false}",
    "contentType": "application/json"
  },
  "completionStatus": "Failed"
}
```

### `worker.canceled`

Published when execution finishes as canceled.

```json
{
  "worker": {},
  "completionStatus": "Canceled"
}
```

### `worker.start`, `worker.pause`, `worker.cancel`, `worker.push`, `worker.purge`

Published when an action is requested against a worker. `actionStatus` is the immediate validation outcome for that action. Not-found actions do not produce a worker event because there is no worker to attach it to.

```json
{
  "worker": {},
  "action": "Cancel",
  "actionStatus": "Accepted"
}
```

If the worker already has output when the action event is published, `output` is also included:

```json
{
  "worker": {},
  "output": {
    "json": "{\"sent\":true}",
    "contentType": "application/json"
  },
  "action": "Purge",
  "actionStatus": "Accepted"
}
```

### `worker.reconfigured`

Published when a worker reconfiguration is accepted. The payload contains the requested changes, not the full effective configuration.

```json
{
  "worker": {},
  "reconfigurationStatus": "Accepted",
  "reconfiguration": {
    "profilingEnabled": true,
    "recurrence": {
      "isEnabled": false,
      "interval": "00:00:00",
      "continueAfterFailure": true,
      "circuitBreakerFailureThreshold": 3,
      "retainedSuccessfulIterations": 25,
      "retainedFailedIterations": 5,
      "raiseCircuitBreakerOpenedEvent": true
    }
  }
}
```

### `worker.iteration.completed`

Published after a worker iteration completes successfully.

```json
{
  "worker": {},
  "completionStatus": "Completed",
  "iteration": {
    "sequence": 1,
    "startedAt": "2026-05-11T11:59:59Z",
    "completedAt": "2026-05-11T12:00:00Z",
    "executionDuration": "00:00:01",
    "occurredAt": "2026-05-11T12:00:00Z",
    "status": "Completed",
    "output": {
      "json": "{\"attempt\":1}",
      "contentType": "application/json"
    },
    "messages": [],
    "logs": []
  }
}
```

### `worker.iteration.failed`

Published after a worker iteration fails. If the failure is transient and retry is configured, this is followed by `worker.retrying`.

```json
{
  "worker": {},
  "completionStatus": "Failed",
  "iteration": {
    "sequence": 2,
    "startedAt": "2026-05-11T12:00:59Z",
    "completedAt": "2026-05-11T12:01:00Z",
    "executionDuration": "00:00:01",
    "occurredAt": "2026-05-11T12:01:00Z",
    "status": "Failed",
    "messages": [
      {
        "code": "sample.failure",
        "severity": "Error",
        "text": "The iteration failed."
      }
    ]
  }
}
```

### `worker.retrying`

Published when a worker is waiting for transient retry backoff. The worker state in the payload is `Retrying`.

```json
{
  "worker": {
    "state": "Retrying",
    "nextRunAt": "2026-05-11T12:01:30Z"
  },
  "retryDelay": "00:00:30"
}
```

### `worker.waiting`

Published when a recurring worker is waiting for the next iteration.

```json
{
  "worker": {},
  "recurrenceInterval": "00:05:00"
}
```

### `worker.recurrence.circuit_opened`

Published when recurrence stops because the circuit breaker threshold was reached. The payload includes the latest retained iteration when one is available.

```json
{
  "worker": {},
  "iteration": {
    "sequence": 3,
    "occurredAt": "2026-05-11T12:02:00Z",
    "status": "Failed",
    "messages": [
      {
        "code": "sample.failure",
        "severity": "Error",
        "text": "The iteration failed."
      }
    ]
  }
}
```

### `worker.log`

Published when worker logging captures a log entry.

```json
{
  "worker": {},
  "log": {
    "occurredAt": "2026-05-11T12:00:00Z",
    "category": "Sample.EmailSender",
    "level": "Warning",
    "eventId": 42,
    "eventName": "SendFailed",
    "message": "Email send failed.",
    "exceptionType": "System.TimeoutException",
    "exceptionMessage": "The operation timed out.",
    "metadata": {
      "smtpServer": "smtp.example.test"
    }
  }
}
```

```csharp
if (workEvent.EventType == "worker.completed")
{
    var data = workEvent.Data.GetValueOrDefault();
    string? state = data.GetProperty("worker").GetProperty("state").GetString();
    string? outputJson = data.GetProperty("output").GetProperty("json").GetString();
}
```

```csharp
await using var subscription = workSystem.Events.Subscribe(
    new WorkEventFilter(
        WorkerId: workerId,
        EventType: "worker.log"));
```

Captured worker logs are also retained on `WorkerSnapshot.Logs` up to the worker's configured logging buffer size.

## Subscribe Before Activity

Create the subscription before the event you want to observe. Events are not replayed to subscriptions created later.

```csharp
await using var subscription = workSystem.Events.Subscribe(
    new WorkEventFilter(
        DefinitionId: sendWelcomeEmail.Id,
        EventType: "worker.queued"));

await using var reader = subscription.Read().GetAsyncEnumerator();

await workSystem.Queue.Enqueue("email.welcome.send");

if (await reader.MoveNextAsync())
{
    WorkEvent queued = reader.Current;
}
```

## Filter By Worker

Use `WorkerId` in the filter when the worker id is already known.

```csharp
var handle = await workSystem.Queue.Enqueue(
    "email.welcome.send",
    options: new WorkerOptions(
        Configuration: WorkConfiguration.Default with
        {
            Start = WorkStartConfiguration.DoNotStart,
        }));

if (handle.WorkerId is not { } workerId)
{
    return;
}

var worker = await workSystem.Query.GetWorker(workerId, cancellationToken);

if (worker is null)
{
    return;
}

await using var subscription = workSystem.Events.Subscribe(
    new WorkEventFilter(
        WorkerId: workerId,
        EventType: "worker.started"));

await workSystem.Workers.Execute(worker.Version, WorkAction.Start, cancellationToken);

await using var reader = subscription.Read(cancellationToken).GetAsyncEnumerator();

if (await reader.MoveNextAsync())
{
    WorkEvent started = reader.Current;
}
```

If the worker id is not known yet, subscribe before queueing and filter by the information that is already known, such as definition id, subject id, concurrency key, work identifier, or event type.

## Filter Events

`WorkEventFilter` can filter by worker id, work definition id, subject id, concurrency key, work identifier, and event type.

```csharp
var filter = new WorkEventFilter(
    WorkerId: workerId,
    DefinitionId: sendWelcomeEmail.Id,
    SubjectId: new WorkSubjectId("user", "user-123"),
    ConcurrencyKey: new WorkConcurrencyKey("tenant", "tenant-456"),
    Identifier: new WorkIdentifier("order", "order-789"),
    EventType: "worker.completed");

await using var subscription = workSystem.Events.Subscribe(filter);
```

Filters are applied as events are published. The subscription buffer only receives matching events.

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

Capacity controls how many matching events can wait for the subscription reader.

## Ownership Rules

- Events are delivered only to subscriptions active at publish time.
- Subscriptions are independent; one slow subscriber does not block the others.
- Disposing a subscription completes its reader.
- Canceling a read removes the subscription.
- A subscription belongs to the system that created it.
