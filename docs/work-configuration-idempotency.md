# Idempotency Configuration

Idempotency configuration controls whether Workable rejects duplicate work for the same `WorkSubjectId`.

`WorkSubjectId` is supplied on `WorkInput`. A subject can be used for correlation and lookup even when idempotency is disabled.

| Setting | Default | Behavior |
| --- | --- | --- |
| `IsEnabled` | `false` | Enables duplicate prevention by subject id. |
| `ConflictPolicy` | `RejectDuplicates` | Rejects queue requests when another worker for the same definition and subject is not reusable. |

Idempotency is part of coordination configuration. `WorkCoordinationConfiguration.Storage` decides where all enabled coordination features run. `Local` uses in-memory duplicate tracking. `Persistent` stores the active idempotency reservation in the configured persistence provider.

When idempotency is enabled:

- Queue requests without a subject are rejected.
- `Canceled` workers do not block a new worker for the same subject.
- `Queued`, `Running`, `Waiting`, `Pausing`, `Paused`, `Canceling`, `Completed`, and `Failed` workers block a new worker for the same subject.

## Attribute

```
[WorkIdempotency]
public sealed class SendWelcomeEmailWork : IWorkExecutor
{
    public Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
    {
        return Task.FromResult(WorkExecutionResult.Success());
    }
}
```

## Bootstrap

```
services.AddWorkableSystem(builder =>
{
    builder.AddWork<SendWelcomeEmailWork>(
        WorkDefinition.Create(
            name: "email.welcome.send",
            description: "Sends a welcome email to a new user.",
            category: "Email:Lifecycle"),
        configuration => configuration.RejectDuplicateSubjects());
});
```

## Queue Input

```
var input = WorkInput
    .FromValue(new SendWelcomeEmail("user-123"))
    .WithSubject(new WorkSubjectId("user", "user-123"));

var handle = await system.Queue.Enqueue("email.welcome.send", input);
```

## Queue Override

```
var handle = await system.Queue.Enqueue(
    "email.welcome.send",
    input,
    options: new WorkerOptions(
        Configuration: WorkConfiguration.Default with
        {
            Coordination = WorkCoordinationConfiguration.Default with
            {
                IsEnabled = true,
                Idempotency = new WorkIdempotencyConfiguration
                {
                    IsEnabled = true,
                },
            },
        }));
```

## Persistence-Backed Idempotency

Persistence-backed idempotency can be used without durable queueing when the host has registered a persistence provider:

```
configuration
    .CoordinatePersistently()
    .RejectDuplicateSubjects();
```

Persistent coordination is rejected at queue time, definition reconfiguration time, and worker reconfiguration time when the Workable system does not have a registered persistence store.

Without durable queueing, persistence-backed idempotency uses the provider's own transaction for the reservation and then materializes the worker in memory. A queue request that supplies a caller-owned persistence transaction is rejected in this mode, because Workable cannot safely wait for that transaction to commit before starting an in-memory worker. Use `QueueDurably()` when enqueue acceptance must participate in the caller's transaction.

Because persistence-backed idempotency creates a persisted Workable row, it can also be paired with durable completion:

```
configuration
    .CoordinatePersistently()
    .RejectDuplicateSubjects()
    .CompleteDurably();
```

In that mode, executor code must call `IWorkExecutionContext.CompleteDurably(...)` with the developer-owned transaction before returning success. See [Queue Durability Configuration](work-configuration-queue-durability.md#durable-completion) for the transaction pattern.

## Reconfiguration

```
var worker = await system.Query.Worker(workerId)
    ?? throw new InvalidOperationException("Worker was not found.");

var outcome = await system.Workers.Reconfigure(
    worker.Version,
    new WorkerReconfiguration(
        Coordination: WorkCoordinationConfiguration.Default));
```

## Related Interactions

- [Idempotency And Recurrence](work-configuration-interactions.md#idempotency-and-recurrence): recurring workers keep the same subject across iterations and can block duplicate work while waiting.
- [Durable Queue And Idempotency](work-configuration-interactions.md#durable-queue-and-idempotency): durable queueing and persistence-backed idempotency can commit duplicate detection and queue persistence together.
