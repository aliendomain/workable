# Idempotency Configuration

Idempotency configuration controls whether Workable rejects duplicate work for the same `WorkSubjectId`.

`WorkSubjectId` is supplied on `WorkInput`. A subject can be used for correlation and lookup even when idempotency is disabled.

| Setting | Default | Behavior |
| --- | --- | --- |
| `IsEnabled` | `false` | Enables duplicate prevention by subject id. |
| `ConflictPolicy` | `RejectDuplicates` | Rejects queue requests when another worker for the same definition and subject is not reusable. |

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
            Idempotency = new WorkIdempotencyConfiguration
            {
                IsEnabled = true,
            },
        }));
```

## Reconfiguration

```
var worker = await system.Query.GetWorker(workerId)
    ?? throw new InvalidOperationException("Worker was not found.");

var outcome = await system.Workers.Reconfigure(
    worker.Version,
    new WorkerReconfiguration(
        Idempotency: WorkIdempotencyConfiguration.Default));
```

## Related Interactions

- [Idempotency And Recurrence](work-configuration-interactions.md#idempotency-and-recurrence): recurring workers keep the same subject across iterations and can block duplicate work while waiting.
