# Transient Retry Configuration

Transient retry configuration controls how Workable retries unhandled execution exceptions that are classified as transient.

For configuration source order, precedence, and override rules that apply to every configuration facet, see [Work Configuration](README.md).

When `Count` is greater than zero, Workable uses the transient retry execution strategy. A transient exception is retried until execution succeeds, cancellation is requested, a non-transient exception occurs, or the configured retry count is exhausted. Declarative `WorkExecutionResult.Failure` results are not retried.

Each transient retry is a new worker iteration with a fresh execution scope. During retry backoff the worker state is `Retrying`, and `NextRunAt` indicates when the next retry iteration is scheduled.

Transient retry delay uses an initial delay and a backoff strategy. With exponential backoff, each retry waits longer than the previous retry up to `MaximumDelay`. Jitter adds a random delay between zero and the configured jitter value.

## Settings

| Setting | Default | Description |
| --- | --- | --- |
| `Count` | `3` | Number of transient retry attempts after execution fails transiently. |
| `InitialDelay` | `TimeSpan.FromMilliseconds(800)` | Delay before the first transient retry. Must be greater than zero when retries are enabled. |
| `Jitter` | `TimeSpan.FromMilliseconds(500)` | Maximum random delay added to retry delay. Must not be negative. |
| `MaximumDelay` | `TimeSpan.FromSeconds(30)` | Maximum delay produced by backoff. Must be greater than zero. |
| `Backoff` | `WorkRetryBackoff.Exponential` | Retry delay strategy. `Exponential` increases delay per attempt; `None` keeps the initial delay. |

## Attribute Configuration

`WorkTransientRetryAttribute` declares default transient retry behavior on the executor type.

```csharp
[WorkTransientRetry(
    count: 3,
    initialDelayMilliseconds: 800,
    jitterMilliseconds: 500,
    maximumDelayMilliseconds: 30_000,
    backoff: WorkRetryBackoff.Exponential)]
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

At startup, the same behavior can also be configured with the convenience method `RetryTransientFailures` or the full `UseTransientRetry` setter.

```csharp
services.AddWorkableSystem(builder =>
{
    builder.AddWork<RefreshCacheWork>(
        WorkDefinition.Create(
            name: "cache.refresh",
            description: "Refreshes cached data.",
            category: "Cache"),
        configuration => configuration.UseTransientRetry(
            new WorkTransientRetryConfiguration
            {
                Count = 3,
                InitialDelay = TimeSpan.FromMilliseconds(800),
                Jitter = TimeSpan.FromMilliseconds(500),
                MaximumDelay = TimeSpan.FromSeconds(30),
                Backoff = WorkRetryBackoff.Exponential,
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
            TransientRetry = WorkTransientRetryConfiguration.Default with
            {
                Count = 1,
                InitialDelay = TimeSpan.FromSeconds(1),
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
        TransientRetry: WorkTransientRetryConfiguration.Disabled));
```

Use `WorkTransientRetryConfiguration.Disabled` when a work definition or worker should not retry transient exceptions.

## Exception Classification

Unhandled execution exceptions are logged by Workable. Exception classifiers can mark an exception as `Transient`, `NonTransient`, or `Unknown`. Workable evaluates classifiers in this order:

1. Work registration classifiers
2. Workable system classifiers
3. App-wide Workable classifiers

The first `Transient` or `NonTransient` result stops the chain. `Unknown` lets Workable continue to the next classifier. If every classifier returns `Unknown`, the exception is treated as non-transient.

App-wide classifiers apply to every Workable system in the service provider.

```
services.AddWorkable(builder =>
{
    builder.ClassifyExceptions(exception =>
        exception is TimeoutException
            ? WorkExceptionClassification.Transient
            : WorkExceptionClassification.Unknown);
});
```

System classifiers apply to one Workable system.

```
services.AddWorkableSystem(builder =>
{
    builder.ClassifyExceptions(exception =>
        exception is TimeoutException
            ? WorkExceptionClassification.Transient
            : WorkExceptionClassification.Unknown);
});
```

Work classifiers apply only to that work registration and run before system-level and app-wide classifiers.

```
services.AddWorkableSystem(builder =>
{
    builder.AddWork<RefreshCacheWork>(
        WorkDefinition.Create(
            name: "cache.refresh",
            description: "Refreshes cached data.",
            category: "Cache"),
        configuration => configuration
            .RetryTransientFailures(
                count: 3,
                initialDelay: TimeSpan.FromMilliseconds(800))
            .ClassifyExceptions(exception =>
                exception is HttpRequestException
                    ? WorkExceptionClassification.Transient
                    : WorkExceptionClassification.Unknown));
});
```

## Related Interactions

- [Recurrence And Transient Retry](interactions.md#recurrence-and-transient-retry): retry attempts are resolved before recurrence records the iteration outcome.
