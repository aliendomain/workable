# Idempotency Configuration

Idempotency configuration controls whether Workable rejects duplicate work for the same `WorkSubjectId`.

For configuration source order, precedence, and override rules that apply to every configuration facet, see [Work Configuration](README.md).

`WorkSubjectId` is supplied on `WorkInput`. A subject can be used for correlation and lookup even when idempotency is disabled.

Idempotency is part of coordination configuration. `WorkCoordinationConfiguration.Storage` decides where enabled idempotency runs. `Local` uses in-memory duplicate tracking. `Persistent` stores the active reservation in the configured persistence provider.

## Settings

| Setting | Default | Description |
| --- | --- | --- |
| `IsEnabled` | `false` | Enables duplicate prevention by subject id. When enabled, queue requests without a subject are rejected. |
| `ConflictPolicy` | `RejectDuplicates` | Rejects a queue request when another worker for the same definition and subject is still blocking reuse. This is currently the only supported policy. |

## Attribute Configuration

`WorkIdempotencyAttribute` declares default idempotency behavior on the executor type.

```csharp
[WorkIdempotency(
    isEnabled: true,
    conflictPolicy: WorkIdempotencyConflictPolicy.RejectDuplicates)]
public sealed class SendWelcomeEmailWork : IWorkExecutor
{
    public Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());
}
```

## Startup Configuration

At startup, the same behavior can also be configured with the convenience method `RejectDuplicateSubjects` or the full `UseCoordination` setter.

```csharp
services.AddWorkableSystem(builder =>
{
    builder.AddWork<SendWelcomeEmailWork>(
        WorkDefinition.Create(
            name: "email.welcome.send",
            description: "Sends a welcome email to a new user.",
            category: "Email:Lifecycle"),
        configuration => configuration.UseCoordination(
            new WorkCoordinationConfiguration
            {
                IsEnabled = true,
                Storage = WorkCoordinationStorage.Local,
                Idempotency = new WorkIdempotencyConfiguration
                {
                    IsEnabled = true,
                    ConflictPolicy = WorkIdempotencyConflictPolicy.RejectDuplicates,
                },
            }));
});
```

When idempotency is enabled, `Canceled` workers do not block a new worker for the same subject. `Queued`, `Running`, `Waiting`, `Pausing`, `Paused`, `Canceling`, `Completed`, and `Failed` workers do.

## Queue-Time Configuration

```csharp
var input = WorkInput
    .FromValue(new SendWelcomeEmail("user-123"))
    .WithSubject(new WorkSubjectId("user", "user-123"));

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

Persistent coordination is rejected at queue time, definition reconfiguration time, and worker reconfiguration time when the Workable system does not have a registered persistence store.

## Reconfiguration

```csharp
var worker = await system.Query.Worker(workerId)
    ?? throw new InvalidOperationException("Worker was not found.");

var outcome = await system.Workers.Reconfigure(
    worker.Version,
    new WorkerReconfiguration(
        Coordination: WorkCoordinationConfiguration.Default));
```

Without durable queueing, persistence-backed idempotency uses the provider's own transaction for the reservation and then materializes the worker in memory. A queue request that supplies a caller-owned persistence transaction is rejected in this mode, because Workable cannot safely wait for that transaction to commit before starting an in-memory worker. Use `QueueDurably()` when enqueue acceptance must participate in the caller's transaction.
