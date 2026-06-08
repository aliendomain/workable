# Failed-Worker Handling Configuration

Failed-worker handling configuration controls what Workable should do when a non-recurring worker settles into the `Failed` worker state.

For configuration source order, precedence, and override rules that apply to every configuration facet, see [Work Configuration](README.md).

By default, failed workers stay in `Failed` until someone explicitly starts or cancels them. Opting into failed-worker auto-cancel changes only the worker disposition. Workable still retains the failed iteration, messages, and failure details that explain what happened.

## Settings

| Setting | Default | Description |
| --- | --- | --- |
| `Handling` | `WorkFailedWorkerHandling.Manual` | Keeps failed workers for manual handling, or auto-cancels them after the configured delay. |
| `AutoCancelAfter` | `TimeSpan.FromMinutes(10)` | Failed-state delay before Workable auto-cancels the worker when auto-cancel handling is enabled or executor code opts into auto-cancel for the current execution. Must be greater than zero. |

Failed-worker auto-cancel is not supported for recurring work. Workable rejects configurations that combine recurrence with `WorkFailedWorkerHandling.AutoCancel`.

## Attribute Configuration

`WorkFailedWorkerAttribute` declares default failed-worker handling on the executor type.

```csharp
[WorkFailedWorker(WorkFailedWorkerHandling.AutoCancel, autoCancelAfterSeconds: 300)]
public sealed class EnableSurveyWork : IWorkExecutor
{
    public Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());
}
```

## Startup Configuration

At startup, the same behavior can be configured with `ConfigureFailedWorker`, `AutoCancelFailedWorkersAfter`, or the full `UseFailedWorker` setter.

```csharp
services.AddWorkableSystem(builder =>
{
    builder.AddWork<EnableSurveyWork>(
        WorkDefinition.Create(
            name: "survey.enable",
            description: "Enables a survey.",
            category: "Surveys"),
        configuration => configuration.AutoCancelFailedWorkersAfter(TimeSpan.FromMinutes(5)));
});
```

## Queue-Time Configuration

```csharp
var handle = await system.Queue.Enqueue(
    "survey.enable",
    options: new WorkerOptions(
        Configuration: WorkConfiguration.Default with
        {
            FailedWorker = new WorkFailedWorkerConfiguration
            {
                Handling = WorkFailedWorkerHandling.AutoCancel,
                AutoCancelAfter = TimeSpan.FromMinutes(2),
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
        FailedWorker: new WorkFailedWorkerConfiguration
        {
            Handling = WorkFailedWorkerHandling.AutoCancel,
            AutoCancelAfter = TimeSpan.FromMinutes(10),
        }));
```

## Runtime Override From Executor Code

Executor code can override the configured failed-worker handling for the current worker execution:

- `context.RequireManualFailedWorkerHandling()` forces manual handling if the execution fails.
- `context.AllowFailedWorkerAutoCancel()` opts into auto-cancel using the worker's configured `AutoCancelAfter` delay.
- `context.AllowFailedWorkerAutoCancel(TimeSpan.FromMinutes(1))` opts into auto-cancel with an execution-specific delay override.

These runtime overrides win over static configuration for the current worker.

## Related Interactions

- [Retention And Failure](interactions.md#retention-and-failure): auto-cancel transitions the worker through the normal cancel path so final-worker retention can take over afterward.
- [Failed-Worker Handling And Recurrence](interactions.md#failed-worker-handling-and-recurrence): recurring workers cannot opt into failed-worker auto-cancel.
