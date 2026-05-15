# Start Configuration

Start configuration controls whether queued work starts automatically and when `IWorkQueueService.Enqueue` returns control to the caller.

This is separate from `WithAutomaticStart`, which queues a new worker when the Workable system starts. Start configuration controls what happens after any worker is queued.

| Setting | Default | Behavior |
| --- | --- | --- |
| `Policy` | `StartAndReturnAfterAccepted` | Determines whether the worker starts automatically and which lifecycle point the queue call waits for. |

Start policy values are:

- `DoNotStart`: queue the worker and return after it is accepted. The worker remains queued until `Start` is executed through `IWorkerOperations`.
- `StartAndReturnAfterAccepted`: queue the worker, submit any scheduling request, and return after the worker is accepted.
- `StartAndReturnAfterStarted`: queue the worker and return after it starts running. If concurrency defers the worker, the queue call waits until capacity is available and the worker starts.
- `StartAndReturnAfterCompleted`: queue the worker and return after the worker completes, fails, pauses, or is canceled.

After a worker is accepted, canceling the queue call's cancellation token cancels only the caller's wait. It does not cancel the accepted worker.

## Attribute

```
[WorkStart(WorkStartPolicy.DoNotStart)]
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
        configuration => configuration.ReturnAfterStarted());
});
```

## Queue Override

```
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

```
var worker = await system.Query.Worker(workerId)
    ?? throw new InvalidOperationException("Worker was not found.");

var outcome = await system.Workers.Reconfigure(
    worker.Version,
    new WorkerReconfiguration(
        Start: WorkStartConfiguration.Default));
```

## Related Interactions

- [Start And Recurrence](work-configuration-interactions.md#start-and-recurrence): `StartAndReturnAfterCompleted` waits for the recurring worker lifecycle to finish, not for one iteration.
- [Start And Concurrency](work-configuration-interactions.md#start-and-concurrency): concurrency can delay the lifecycle point that queueing waits for.
