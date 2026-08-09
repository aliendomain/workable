# Logging Configuration

Logging configuration controls worker-scoped log capture.

For configuration source order, precedence, and override rules that apply to every configuration facet, see [Work Configuration](README.md).

Workable decorates `ILogger<>` in the host service provider. During worker execution, logs written by the executor and by scoped or transient services created for that execution are still forwarded to the host logger. When worker logging is enabled, matching log entries are retained on the worker snapshot and a `worker.log` event is published with the captured log message details.

This retained in-memory buffer is separate from [Persistent Execution Diagnostics](execution-diagnostics-persistence.md). Persistent capture has its own minimum level and can retain eligible entries after the snapshot buffer reaches `MaximumBufferedEntries`.

## Settings

| Setting | Default | Description |
| --- | --- | --- |
| `IsEnabled` | `true` | Enables worker-scoped log capture. |
| `Level` | `LogLevel.Information` | Minimum log level captured by Workable. Host logging still uses the host's normal logging rules. |
| `MaximumBufferedEntries` | `100` | Maximum number of captured log entries retained per worker iteration. Older entries are removed first within that iteration. |

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

- `worker.log` events on `IWorkEventStream`, which include the captured log message details, a stable log entry id, the in-flight iteration identity, and worker context.
- `WorkerSnapshot.Iterations[*].Logs`, which contains the retained log entries for each retained iteration.

The event payload includes the captured log entry id, message, category, level, event id, and exception fields when an exception was logged. It also includes the current iteration snapshot so a consumer can correlate the log with the iteration that emitted it, plus the worker-level retained `logSummary` and `timelineSummary` aggregates used by overview-style realtime consumers. Retained iteration snapshots expose the same log entry fields for each retained iteration.

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
