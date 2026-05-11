# Transient Retry Configuration

Transient retry configuration controls how Workable retries unhandled execution exceptions that are classified as transient.

When `Count` is greater than zero, Workable uses the transient retry execution strategy. A transient exception is retried until execution succeeds, cancellation is requested, a non-transient exception occurs, or the configured retry count is exhausted. Declarative `WorkExecutionResult.Failure` results are not retried.

Transient retry delay uses an initial delay and a backoff strategy. With exponential backoff, each retry waits longer than the previous retry up to `MaximumDelay`. Jitter adds a random delay between zero and the configured jitter value.

| Setting | Default | Behavior |
| --- | --- | --- |
| `Count` | `0` | Number of transient retry attempts after execution fails transiently. |
| `InitialDelay` | `TimeSpan.FromMilliseconds(800)` | Delay before the first transient retry. Must be greater than zero when retries are enabled. |
| `Jitter` | `TimeSpan.FromMilliseconds(500)` | Maximum random delay added to retry delay. Must not be negative. |
| `MaximumDelay` | `TimeSpan.FromSeconds(30)` | Maximum delay produced by backoff. Must be greater than zero. |
| `Backoff` | `WorkRetryBackoff.Exponential` | Retry delay strategy. `Exponential` increases delay per attempt; `None` keeps the initial delay. |

## Attribute

```
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
        configuration => configuration.RetryTransientFailures(
            count: 3,
            initialDelay: TimeSpan.FromMilliseconds(800),
            jitter: TimeSpan.FromMilliseconds(500),
            maximumDelay: TimeSpan.FromSeconds(30)));
});
```

## Queue Override

```
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

```
var worker = await system.Query.GetWorker(workerId)
    ?? throw new InvalidOperationException("Worker was not found.");

var outcome = await system.Workers.Reconfigure(
    worker.Version,
    new WorkerReconfiguration(
        TransientRetry: WorkTransientRetryConfiguration.Default with
        {
            Count = 0,
        }));
```

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

- [Recurrence And Transient Retry](work-configuration-interactions.md#recurrence-and-transient-retry): retry attempts are resolved before recurrence records the iteration outcome.
