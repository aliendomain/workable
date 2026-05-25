# Recurrence Configuration

Recurrence controls whether a worker runs again after an execution completes. When recurrence is enabled, `Interval` is required and must be greater than zero.

For configuration source order, precedence, and override rules that apply to every configuration facet, see [Work Configuration](README.md).

Recurring workers keep the same worker identity across iterations. Each iteration invokes the work through a fresh execution scope, so scoped services are created and disposed once per iteration. After an iteration continues, the worker records the iteration, enters `Waiting`, and waits until the recurrence interval elapses. `Push` skips the current wait and starts the next iteration. `Pause` can pause a running iteration or a waiting worker; `Start` resumes a paused worker. `Cancel` permanently stops the worker.

If an iteration returns `WorkExecutionResult.Failure`, recurrence uses `ContinueAfterFailure` and `CircuitBreakerFailureThreshold` to decide whether to keep running. Transient retry, when configured, creates additional iterations and puts the worker in `Retrying` during retry backoff.

Because a recurring worker remains one active worker across iterations, queue and handle waits can remain pending indefinitely. `Enqueue(...)` can wait indefinitely when start policy is `StartAndReturnAfterCompleted`, and `IWorkerHandle.WaitForCompletion(...)` can also wait indefinitely until recurrence stops.

That includes failure paths. A failed iteration does not necessarily complete the worker or release the handle wait. If recurrence continues after failure, or if transient retry schedules another attempt first, the handle remains pending until the worker actually reaches a terminal completion state.

## Settings

| Setting | Default | Description |
| --- | --- | --- |
| `IsEnabled` | `false` | Enables repeated execution. |
| `Interval` | `TimeSpan.Zero` | Wait time between completed iterations. Must be greater than zero when recurrence is enabled. |
| `ContinueAfterFailure` | `true` | Continues the recurring loop after a failed execution while the circuit remains closed. |
| `CircuitBreakerFailureThreshold` | `3` | Maximum consecutive failed iterations before recurrence opens the circuit and stops the recurring loop. |
| `RetainedIterations` | `25` | Number of iteration records retained on the worker snapshot, regardless of status. |
| `RaiseCircuitBreakerOpenedEvent` | `true` | Publishes an event when recurrence stops because the circuit breaker opens. |

## Attribute Configuration

`WorkRecurrenceAttribute` declares default recurrence behavior on the executor type.

```csharp
[WorkRecurrence(
    intervalMilliseconds: 300_000,
    continueAfterFailure: true,
    circuitBreakerFailureThreshold: 3,
    retainedIterations: 25,
    raiseCircuitBreakerOpenedEvent: true)]
public sealed class RefreshCacheWork : IWorkExecutor
{
    public Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());
}
```

## Startup Configuration

At startup, the same behavior can also be configured with the convenience methods `RecurEvery` and `DisableRecurrence`, or the full `UseRecurrence` setter.

```csharp
services.AddWorkableSystem(builder =>
{
    builder.AddWork<RefreshCacheWork>(
        WorkDefinition.Create(
            name: "cache.refresh",
            description: "Refreshes cached data.",
            category: "Cache"),
        configuration => configuration.UseRecurrence(
            new WorkRecurrenceConfiguration
            {
                IsEnabled = true,
                Interval = TimeSpan.FromMinutes(5),
                ContinueAfterFailure = true,
                CircuitBreakerFailureThreshold = 3,
                RetainedIterations = 25,
                RaiseCircuitBreakerOpenedEvent = true,
            }));
});
```

## Queue-Time Configuration

```csharp
var handle = await system.Queue.Enqueue(
    "cache.refresh",
    options: new WorkerOptions(
        Configuration: WorkConfiguration.Default with
        {
            Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(1)),
        }));
```

## Reconfiguration

```csharp
var worker = await system.Query.Worker(workerId)
    ?? throw new InvalidOperationException("Worker was not found.");

var outcome = await system.Workers.Reconfigure(
    worker.Version,
    new WorkerReconfiguration(
        Recurrence: WorkRecurrenceConfiguration.Disabled));
```

## Related Interactions

- [Start And Recurrence](interactions.md#start-and-recurrence): start policies wait for worker lifecycle points, not recurrence iteration points.
- [Recurrence And Concurrency](interactions.md#recurrence-and-concurrency): waiting recurring workers hold concurrency capacity.
- [Recurrence And Transient Retry](interactions.md#recurrence-and-transient-retry): transient retry creates additional iterations and exposes retry backoff through `Retrying`.
- [Idempotency And Recurrence](interactions.md#idempotency-and-recurrence): recurring workers keep the same subject across iterations.
