# Recurrence Configuration

Recurrence controls whether a worker runs again after an execution completes. When recurrence is enabled, `Interval` is required and must be greater than zero.

Recurring workers keep the same worker identity across iterations. Each iteration invokes the work through a fresh execution scope, so scoped services are created and disposed once per iteration. After an iteration continues, the worker records the iteration, enters `Waiting`, and waits until the recurrence interval elapses. `Push` skips the current wait and starts the next iteration. `Pause` can pause a running iteration or a waiting worker; `Start` resumes a paused worker. `Cancel` permanently stops the worker.

If an iteration returns `WorkExecutionResult.Failure`, recurrence uses `ContinueAfterFailure` and `CircuitBreakerFailureThreshold` to decide whether to keep running. Transient retry, when configured, creates additional iterations and puts the worker in `Retrying` during retry backoff.

| Setting | Default | Behavior |
| --- | --- | --- |
| `IsEnabled` | `false` | Enables repeated execution. |
| `Interval` | `TimeSpan.Zero` | Wait time between executions. |
| `ContinueAfterFailure` | `true` | Continues the recurring loop after a failed execution while the circuit remains closed. The next attempt uses the normal recurrence interval. |
| `CircuitBreakerFailureThreshold` | `3` | Maximum consecutive failed iterations before recurrence opens the circuit and stops the recurring loop. |
| `RetainedSuccessfulIterations` | `25` | Number of successful iteration records retained on the worker snapshot. |
| `RetainedFailedIterations` | `5` | Number of failed or interrupted iteration records retained on the worker snapshot. |
| `RaiseCircuitBreakerOpenedEvent` | `true` | Publishes an event when recurrence stops because the circuit breaker opens. |

## Attribute

```
[WorkRecurrence(
    intervalMilliseconds: 60_000,
    continueAfterFailure: true,
    circuitBreakerFailureThreshold: 3,
    retainedSuccessfulIterations: 25,
    retainedFailedIterations: 5,
    raiseCircuitBreakerOpenedEvent: true)]
public sealed class RefreshCacheWork : IWorkExecutor
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
    builder.AddWork<RefreshCacheWork>(
        WorkDefinition.Create(
            name: "cache.refresh",
            description: "Refreshes cached data.",
            category: "Cache"),
        configuration => configuration.RecurEvery(TimeSpan.FromMinutes(5)));
});
```

Contributed work can use the same configuration builder.

```
services.AddWorkableWork<RefreshCacheWork>(
    WorkDefinition.Create(
        name: "cache.refresh",
        description: "Refreshes cached data.",
        category: "Cache"),
    configuration => configuration.RecurEvery(TimeSpan.FromMinutes(5)));
```

## Queue Override

```
var handle = await system.Queue.Enqueue(
    "cache.refresh",
    options: new WorkerOptions(
        Configuration: WorkConfiguration.Default with
        {
            Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMinutes(1)),
        }));
```

## Reconfiguration

```
var worker = await system.Query.Worker(workerId)
    ?? throw new InvalidOperationException("Worker was not found.");

var outcome = await system.Workers.Reconfigure(
    worker.Version,
    new WorkerReconfiguration(
        Recurrence: WorkRecurrenceConfiguration.Disabled));
```

## Related Interactions

- [Start And Recurrence](work-configuration-interactions.md#start-and-recurrence): start policies wait for worker lifecycle points, not recurrence iteration points.
- [Recurrence And Concurrency](work-configuration-interactions.md#recurrence-and-concurrency): waiting recurring workers hold concurrency capacity.
- [Recurrence And Transient Retry](work-configuration-interactions.md#recurrence-and-transient-retry): transient retry creates additional iterations and exposes retry backoff through `Retrying`.
- [Idempotency And Recurrence](work-configuration-interactions.md#idempotency-and-recurrence): recurring workers keep the same subject across iterations.
