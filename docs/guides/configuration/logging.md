# Logging Configuration

Logging configuration controls worker-scoped log capture.

For configuration source order, precedence, and override rules that apply to every configuration facet, see [Work Configuration](README.md).

Workable decorates `ILogger<>` in the host service provider. During worker execution, logs written by the executor and by scoped or transient services created for that execution are still forwarded to the host logger. When worker logging is enabled, matching log entries are retained on the worker snapshot and a thin `worker.log` event is published.

## Settings

| Setting | Default | Description |
| --- | --- | --- |
| `IsEnabled` | `true` | Enables worker-scoped log capture. |
| `Level` | `LogLevel.Information` | Minimum log level captured by Workable. Host logging still uses the host's normal logging rules. |
| `MaximumBufferedEntries` | `100` | Maximum number of captured log entries retained on the worker snapshot. Older entries are removed first. |

## Attribute Configuration

`WorkLoggingAttribute` declares default worker log capture behavior on the executor type.

```csharp
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
        => Task.FromResult(WorkExecutionResult.Success());
}
```

Captured logs are exposed in two related places:

- `worker.log` events on `IWorkEventStream`, which notify subscribers that a log was captured.
- `WorkerSnapshot.Logs`, which contains the most recent captured entries for that worker.

The event payload is intentionally thin and does not include the log message or metadata. Query the worker snapshot when a UI needs the retained log details.

## Startup Configuration

At startup, the same behavior can also be configured with the convenience method `ConfigureLogging` or the full `UseLogging` setter.

```csharp
services.AddWorkableSystem(builder =>
{
    builder.AddWork<RefreshCacheWork>(
        WorkDefinition.Create(
            name: "cache.refresh",
            description: "Refreshes cached data.",
            category: "Cache"),
        configuration => configuration.UseLogging(
            new WorkLoggingConfiguration
            {
                IsEnabled = true,
                Level = LogLevel.Information,
                MaximumBufferedEntries = 100,
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
            Logging = WorkLoggingConfiguration.Default with
            {
                Level = LogLevel.Warning,
                MaximumBufferedEntries = 250,
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
        Logging: WorkLoggingConfiguration.Default with
        {
            IsEnabled = false,
            MaximumBufferedEntries = 100,
        }));
```

## Related Interactions

- [Logging And Service Lifetimes](interactions.md#logging-and-service-lifetimes): worker log capture follows the logger instances used during execution.
