# Start Configuration

Start configuration controls whether queued work starts automatically and when `IWorkQueueService.Enqueue` returns control to the caller.

For configuration source order, precedence, and override rules that apply to every configuration facet, see [Work Configuration](README.md).

This is separate from `WithAutomaticStart`, which queues a new worker when the Workable system starts. Start configuration controls what happens after any worker is queued.

## Settings

| Setting | Default | Description |
| --- | --- | --- |
| `Policy` | `StartAndReturnAfterAccepted` | Controls whether the worker starts automatically and which lifecycle point the queue call waits for before returning. `DoNotStart` leaves the worker queued. `StartAndReturnAfterAccepted` returns after acceptance. `StartAndReturnAfterStarted` returns after execution begins. `StartAndReturnAfterCompleted` waits until the worker reaches a completion outcome. |

## Attribute Configuration

`WorkStartAttribute` declares the default start policy on the executor type.

```csharp
[WorkStart(WorkStartPolicy.StartAndReturnAfterCompleted)]
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

At startup, the same behavior can also be configured with the convenience methods `DoNotStart`, `ReturnAfterStarted`, and `ReturnAfterCompleted`, or the full `UseStart` setter.

```csharp
services.AddWorkableSystem(builder =>
{
    builder.AddWork<SendWelcomeEmailWork>(
        WorkDefinition.Create(
            name: "email.welcome.send",
            description: "Sends a welcome email to a new user.",
            category: "Email:Lifecycle"),
        configuration => configuration.UseStart(
            new WorkStartConfiguration
            {
                Policy = WorkStartPolicy.StartAndReturnAfterCompleted,
            }));
});
```

After a worker is accepted, canceling the queue call's cancellation token cancels only the caller's wait. It does not cancel the accepted worker.

When recurrence is enabled, `StartAndReturnAfterCompleted` can wait indefinitely because the worker does not complete after a successful iteration. It returns only when the recurring worker actually leaves the recurring lifecycle.

## Queue-Time Configuration

```csharp
var handle = await system.Queue.Enqueue(
    "email.welcome.send",
    options: new WorkerOptions(
        Configuration: WorkConfiguration.Default with
        {
            Start = new WorkStartConfiguration
            {
                Policy = WorkStartPolicy.StartAndReturnAfterCompleted,
            },
        }));
```

## Reconfiguration

```csharp
var worker = await system.Query.Worker(workerId)
    ?? throw new InvalidOperationException("Worker was not found.");

var outcome = await system.Workers.Reconfigure(
    worker.Version,
    new WorkerReconfiguration(
        Start: WorkStartConfiguration.Default));
```

## Related Interactions

- [Start And Recurrence](interactions.md#start-and-recurrence): `StartAndReturnAfterCompleted` waits for the recurring worker lifecycle to finish, not for one iteration.
- [Start And Concurrency](interactions.md#start-and-concurrency): concurrency can delay the lifecycle point that queueing waits for.
