# Iteration Status Streams

## Intent

An iteration status stream carries ordered, application-defined progress from executing work to live consumers.

Use it for data such as:

- assistant text deltas
- tool-call progress
- import stages and item counts
- compiler or deployment phases
- any other transient state that is useful before the iteration produces its final output

Status items are not worker lifecycle transitions, logs, raw operational events, or retained `WorkOutput`. They form a separate stream because interactive deltas need per-iteration ordering, replay cursors, and explicit gap handling rather than event batching or silent overflow.

## Publish From An Executor

Every executing `IWorkExecutionContext` exposes `Status`.

```csharp
public sealed record AssistantTextDelta(string MessageId, string Text);

public async Task<WorkExecutionResult> Execute(
    IWorkExecutionContext context,
    WorkInput? input,
    CancellationToken cancellationToken)
{
    var messageId = Guid.NewGuid().ToString("N");

    context.Status.Publish("assistant.message.started", new
    {
        messageId,
        role = "assistant"
    });

    await foreach (var chunk in assistant.Generate(cancellationToken))
    {
        context.Status.Publish(
            "assistant.text.delta",
            new AssistantTextDelta(messageId, chunk));
    }

    context.Status.Publish("assistant.message.completed", new
    {
        messageId,
        finishReason = "stop"
    });

    return WorkExecutionResult.Success();
}
```

`Publish(type)` emits an item without data. `Publish(type, value)` serializes `value` as JSON. `Publish(WorkIterationStatusUpdate)` accepts a prebuilt update containing a `JsonElement`.

Types are application-defined strings. Prefer namespaced, stable values such as `assistant.text.delta` or `deployment.stage.started`. Use payload data for changing values instead of creating a new type per message or stage.

Publish token-sized or word-sized chunks for text. Individual characters work, but they create significantly more allocation and transport overhead.

## Ordering And Iteration Identity

Every published item becomes a `WorkIterationStatusItem` with:

- `Iteration`: worker id plus iteration sequence
- `Sequence`: a one-based, monotonic sequence within that iteration
- `OccurredAt`
- work system and definition identity
- application-defined `Type`
- optional JSON `Data`

Calls from concurrent producers are serialized into one iteration sequence. The assigned sequence is the authoritative delivery order.

Retry and recurrence create different iteration sequences. A consumer must subscribe to the exact `WorkerIterationReference` it wants to follow; a worker id by itself is not enough.

## Subscribe In Process

Open systems expose `IWorkSystem.IterationStatuses`. Authorization-enabled systems expose the same surface through `IWorkSystemSession.IterationStatuses`.

```csharp
var iteration = new WorkerIterationReference(workerId, iterationSequence);
var lastReceivedSequence = 0L;

await using var subscription = session.IterationStatuses.Subscribe(
    iteration,
    afterSequence: lastReceivedSequence);

await foreach (var item in subscription.Read(cancellationToken))
{
    lastReceivedSequence = item.Sequence;

    if (item.Type == "assistant.text.delta")
    {
        var delta = item.DeserializeData<AssistantTextDelta>();
        Console.Write(delta?.Text);
    }
}
```

`afterSequence` is exclusive. Passing `42` requests item `43` and later, then continues with live items when that complete range is still retained. Passing zero requests the stream from sequence one; it reports a replay gap if the beginning has already been evicted.

The read completes after the iteration reaches a final status and all retained items have been delivered. Disposing the subscription or canceling `Read(...)` stops the live reader.

After a normal read completes, `subscription.Completion` contains the authoritative final iteration snapshot, worker revision/state, and accepted cancellation origin. Workable attaches this value atomically while closing the stream; clients do not need a separate query that could race retention or purge. On an authorized session, the retained iteration profile is included only when the caller has diagnostics permission; ordinary Read access still receives the status, output, messages, timing, and attempt count. Completion is null while the iteration is live and for manually completed custom stream implementations that do not supply terminal state.

## Stream Through SignalR

`Workable.SignalR` exposes a streaming hub invocation rather than a batched callback:

```csharp
var stream = connection.StreamAsyncCore<WorkableRealtimeIterationStatusMessage>(
    "StreamMyIterationStatus",
    [workerId.ToString("D"), iterationSequence, lastReceivedSequence, systemName],
    cancellationToken);

await foreach (var message in stream.WithCancellation(cancellationToken))
{
    if (message.Gap is { } gap)
    {
        // Reload a durable application snapshot or show an incomplete-stream state.
        var available = gap.FirstAvailableSequence is { } first
            ? $"available range: {first}-{gap.LastAvailableSequence}"
            : "no statuses are currently retained";
        Console.WriteLine($"Missing statuses after {gap.RequestedAfterSequence}; {available}.");
        break;
    }

    if (message.Completed is { } completed)
    {
        // Generic terminal Workable data, including completed.Output and
        // completed.Messages, arrives exactly once after the status items.
        break;
    }

    var item = message.Status!;
    lastReceivedSequence = item.Sequence;
    // Apply item.Type and item.Data to the live UI.
}
```

The JavaScript SignalR client uses the same arguments:

```javascript
const stream = connection.stream(
  "StreamMyIterationStatus",
  workerId,
  iterationSequence,
  lastReceivedSequence,
  systemName ?? null);

const subscription = stream.subscribe({
  next: message => {
    if (message.kind === "gap") {
      const gap = message.gap;
      showIncompleteStream(gap.requestedAfterSequence,
        gap.firstAvailableSequence, gap.lastAvailableSequence);
      return;
    }

    if (message.kind === "completed") {
      applyTerminalResult(message.completed);
      return;
    }

    const item = message.status;
    lastReceivedSequence = item.sequence;
    if (item.type === "assistant.text.delta") {
      output.append(item.data.text);
    }
  },
  complete: () => console.log("iteration status stream completed"),
  error: error => console.error("iteration status stream failed", error)
});

// Stop watching without canceling the underlying Workable iteration.
subscription.dispose();
```

Clients normally obtain `iterationSequence` from the current or retained worker snapshot. Do not assume it is always one: recurring and retry-capable workers can have many iterations.

## Resume And Replay Gaps

Keep the largest successfully applied `Sequence` as the resume cursor. After reconnecting, call `Subscribe(...)`, `StreamMyIterationStatus`, or `StreamIterationStatus` with that value as `afterSequence`.

The in-memory implementation retains at most the latest 4,096 items and 4 MiB of combined UTF-8 type and JSON payload data per iteration by default. System-wide defaults additionally retain at most 65,536 items and 64 MiB of accounted type and payload data. Crossing a per-iteration limit evicts that iteration's oldest items. Crossing a system limit deterministically evicts the oldest item among all iteration replay heads. If the requested cursor is older than the retained window:

- direct .NET subscriptions throw `WorkIterationStatusGapException`, including `AfterSequence`, `FirstAvailableSequence`, and `LastAvailableSequence`; the available values are null when the system budget evicted the iteration's complete replay window
- SignalR emits one terminal `WorkableRealtimeIterationStatusMessage` with `Kind == "gap"`, a structured `Gap` value containing the same range, and no `Status` or `Completed`, then completes normally

Do not silently append from `FirstAvailableSequence`; the missing data may make accumulated text or progress incorrect. Restart the visible stream from a durable application snapshot, show an explicit incomplete-stream state, or wait for the final output and reload it. Subscribing can also throw `WorkIterationStatusSubscriptionLimitException` when the per-iteration or system-wide live subscription limit is full; dispose subscriptions promptly and retry with backoff rather than opening parallel replacements.

## Authorization

Iteration status visibility follows work-definition read authorization.

- A readable definition can expose its iteration status items.
- An unreadable or unknown iteration produces an empty authorized stream so callers cannot use the status surface to probe hidden worker identities.
- SignalR captures the caller's authorization snapshot when the streaming invocation starts.
- `StreamMyIterationStatus` also requires an exact match between the authenticated actor id and the worker's originating actor id; unknown and other-actor workers both produce an empty stream.
- `StreamIterationStatus` is the operator form when definition-level read authorization is the intended boundary.

Status payloads should still contain only data appropriate for every caller allowed to read that work definition. Workable does not apply field-level redaction inside application-defined JSON.

## Lifetime And Current Limits

The status-stream implementation is replay-first and deliberately transient:

- items are retained in memory, not in the SQL durable queue store
- a host process restart loses the replay buffer
- publication stores an item even when no consumer is currently subscribed, so a client can connect after execution begins
- the default replay capacity is 4,096 items per iteration
- the default aggregate replay type-and-payload capacity is 4 MiB per iteration
- system replay is capped at 65,536 items and 64 MiB of accounted type-and-payload data
- each JSON payload is limited to 32,768 serialized UTF-8 bytes by default
- each application-defined type is limited to 256 UTF-8 bytes
- active subscriptions are capped at 64 per iteration and 4,096 per system
- existing worker and iteration retention owns the buffer lifetime; purging a worker or forgetting an iteration also removes its replay buffer

The terminal SignalR message carries the retained `WorkOutput`, but the status replay buffer itself remains in memory. If a reconnect crosses a process restart, a replay gap, malformed or missing terminal payload, or worker purge, reload the durable application or conversation state. That read is a recovery path, not the normal completion path. If a client must rebuild after a process restart, persist that application state separately until durable status-stream storage is explicitly added.

An oversized publish throws `WorkIterationStatusPayloadTooLargeException` or `WorkIterationStatusTypeTooLargeException` before assigning a sequence. Workable never truncates or silently drops it. Keep status payloads small, publish a reference when progress data is already stored elsewhere, and split large text into sensible chunks. Never rely on the status stream as a document or file transport.

Configure the limits once at system startup:

```csharp
services.AddWorkableSystem(builder =>
{
    builder.ConfigureIterationStatuses(
        replayItemCapacity: 4_096,
        replayPayloadByteCapacity: 4 * 1_024 * 1_024,
        systemReplayItemCapacity: 65_536,
        systemReplayByteCapacity: 64 * 1_024 * 1_024,
        maximumPayloadBytes: 32 * 1_024,
        maximumTypeBytes: 256,
        maximumSubscriptions: 4_096,
        maximumSubscriptionsPerIteration: 64);

    builder.AddWork<AssistantWork>();
});
```

Use `UseIterationStatuses(new WorkSystemIterationStatusConfiguration { ... })` when replacing the complete configuration object is clearer. These are system bootstrap limits, not worker retention settings, and they cannot be changed through queue options or worker reconfiguration.

## Runnable Sample

The SampleHost definition `sample.demo.assistant-stream` simulates an assistant response. It publishes:

1. `assistant.message.started`
2. a series of `assistant.text.delta` word chunks
3. `assistant.message.completed`

Its input is:

```json
{
  "prompt": "Explain status streams in one sentence.",
  "chunkDelayMilliseconds": 75
}
```

Run SampleHost, queue that definition, read the worker's current iteration sequence, and stream it from `/workable/realtime`. The implementation is in `apps/samples/Workable.SampleHost/WorkSystems/Demo/DemoAssistantStreamWork.cs`.

## V1 Contract

Iteration status replay is in-memory and non-durable. It records publications before any subscriber arrives, uses system-configured replay and payload limits, follows existing worker retention for cleanup, reports SignalR replay gaps as typed terminal messages, and attaches retained terminal state atomically to normal completion. SQL persistence is intentionally outside this contract.
