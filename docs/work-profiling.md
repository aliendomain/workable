# Work Profiling

Work profiling captures a per-worker execution tree for diagnostic timing and context. Profiling is controlled by `WorkerOptions.ProfilingEnabled`.

When profiling is disabled, the profile API is still available and behaves as a no-op. Work code can add profile information without checking whether profiling is active.

## Enable Profiling

Profiling can be enabled on the work definition's default worker options.

```
var definition = WorkDefinition.Create(
    name: "cache.refresh",
    description: "Refreshes cached data.",
    category: "Cache",
    defaultOptions: new WorkerOptions(
        ProfilingEnabled: true));
```

It can also be enabled for a single queue request.

```
var handle = await system.Queue.Enqueue(
    "cache.refresh",
    options: new WorkerOptions(
        ProfilingEnabled: true));
```

Runtime reconfiguration can update profiling for workers that can be reconfigured.

```
var worker = await system.Query.Worker(workerId)
    ?? throw new InvalidOperationException("Worker was not found.");

var outcome = await system.Workers.Reconfigure(
    worker.Version,
    new WorkerReconfiguration(
        ProfilingEnabled: true));
```

## Profile From Work

Executors access profiling through `IWorkExecutionContext.Profile`.

```
public sealed class RefreshCacheWork : IWorkExecutor
{
    public async Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
    {
        context.Profile.AddInfo("cache key", "home-page");

        using (context.Profile.CreateScope("Refresh cache"))
        {
            using var query = context.Profile.StartTiming("Load source data");
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }

        return WorkExecutionResult.Success();
    }
}
```

Workable also registers `IWorkProfiler` with dependency injection. Scoped and transient services created during execution can inject `IWorkProfiler` and add entries to the same active profile tree.

```
public sealed class CacheLoader(IWorkProfiler profile)
{
    public async Task Load(CancellationToken cancellationToken)
    {
        using var timing = profile.StartTiming("CacheLoader.Load");
        await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
    }
}
```

## Built-In Scope

When profiling is enabled, Workable wraps the executor call in a method scope. Entries added by the executor or by services it uses appear beneath that execution scope.

Constructor-time profile entries from services resolved for the executor are captured on the worker profile root because the active worker profile is established before the executor is resolved.

## Profile Results

The latest profile is exposed on `WorkerSnapshot.Profile`.

```
var completion = await handle.WaitForCompletion();
var ascii = completion.Worker?.Profile?.ToAsciiTree();
```

Workers capture a profile per iteration. Each retained `WorkerIterationSnapshot` can include its own `Profile`, including run-once workers that produce multiple iterations because of transient retry.

```
var worker = await system.Query.Worker(workerId);

foreach (var iteration in worker?.Iterations ?? [])
{
    var ascii = iteration.Profile?.ToAsciiTree();
}
```

Profile retention follows the same iteration retention settings as iteration history.
