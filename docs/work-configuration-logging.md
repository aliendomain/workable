# Logging Configuration

Logging configuration controls worker-scoped log capture.

Workable decorates `ILogger<>` in the host service provider. During worker execution, logs written by the executor and by scoped or transient services created for that execution are still forwarded to the host logger. When worker logging is enabled, matching log entries are also captured as `worker.log` events and retained on the worker snapshot.

| Setting | Default | Behavior |
| --- | --- | --- |
| `IsEnabled` | `true` | Enables worker-scoped log capture. |
| `Level` | `LogLevel.Information` | Minimum log level captured by Workable. Host logging still uses the host's normal logging rules. |
| `MaximumBufferedEntries` | `100` | Maximum number of captured log entries retained on the worker snapshot. Older entries are removed first. |

Captured logs are exposed in two places:

- `worker.log` events on `IWorkEventStream`.
- `WorkerSnapshot.Logs`, which contains the most recent captured entries for that worker.

## Attribute

```
[WorkLogging(
    isEnabled: true,
    level: LogLevel.Information,
    maximumBufferedEntries: 100)]
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
        configuration => configuration.ConfigureLogging(
            isEnabled: true,
            level: LogLevel.Information,
            maximumBufferedEntries: 100));
});
```

## Queue Override

```
var handle = await system.Queue.Enqueue(
    "cache.refresh",
    options: new WorkerOptions(
        Configuration: WorkConfiguration.Default with
        {
            Logging = WorkLoggingConfiguration.Default with
            {
                Level = LogLevel.Warning,
                MaximumBufferedEntries = 250,
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
        Logging: WorkLoggingConfiguration.Default with
        {
            IsEnabled = false,
            MaximumBufferedEntries = 100,
        }));
```

## Related Interactions

- [Logging And Service Lifetimes](work-configuration-interactions.md#logging-and-service-lifetimes): worker log capture follows the logger instances used during execution.
