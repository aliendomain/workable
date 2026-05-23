# Work Profiling

Work profiling captures a per-worker execution tree for diagnostic timing and context. Profiling is controlled by `WorkerOptions.ProfilingEnabled`.

When profiling is disabled, the profile API is still available and behaves as a no-op. Work code can add profile information without checking whether profiling is active.

## Enable Profiling

Profiling can be enabled on the work definition's default worker options.

```csharp
var definition = WorkDefinition.Create(
    name: "cache.refresh",
    description: "Refreshes cached data.",
    category: "Cache",
    defaultOptions: new WorkerOptions(
        ProfilingEnabled: true));
```

It can also be enabled for a single queue request.

```csharp
var handle = await system.Queue.Enqueue(
    "cache.refresh",
    options: new WorkerOptions(
        ProfilingEnabled: true));
```

Runtime reconfiguration can update profiling for any non-final worker.

```csharp
var worker = await system.Query.Worker(workerId)
    ?? throw new InvalidOperationException("Worker was not found.");

var outcome = await system.Workers.Reconfigure(
    worker.Version,
    new WorkerReconfiguration(
        ProfilingEnabled: true));
```

## Profile From Work

Executors access profiling through `IWorkExecutionContext.Profile`.

```csharp
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

The lambda overload has the same access through its `context` parameter.

```csharp
services.AddWorkableSystem(builder =>
{
    builder.AddWork(
        WorkDefinition.Create(
            "cache.refresh.lambda",
            defaultOptions: new WorkerOptions(ProfilingEnabled: true)),
        async (context, input, cancellationToken) =>
        {
            context.Profile.AddInfo("cache key", "home-page");

            using var scope = context.Profile.CreateScope("Refresh cache");
            using var timing = context.Profile.StartTiming("Load source data");
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);

            return WorkExecutionResult.Success();
        });
});
```

Workable also registers `IWorkProfiler` with dependency injection. Scoped and transient services created during execution can inject `IWorkProfiler` and add entries to the same active profile tree.

```csharp
public sealed class CacheLoader(IWorkProfiler profile)
{
    public async Task Load(CancellationToken cancellationToken)
    {
        using var timing = profile.StartTiming("CacheLoader.Load");
        await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
    }
}
```

That works because `IWorkProfiler` is a facade over the current active worker profile. Services resolved inside the worker execution scope do not need the execution context passed through manually just to contribute profile entries.

Services can contribute in three common ways:

- `AddInfo(...)` to attach small labels or structured context objects.
- `StartTiming(...)` to measure a leaf operation.
- `CreateScope(...)` or `CreateMethodScope(...)` to group nested work and optionally attach input or result context.

When the context object is not a string, `WorkProfileSnapshot.ToAsciiTree()` renders it as JSON beneath that profile node.

## Built-In Scope

When profiling is enabled, Workable wraps the executor call in a method scope. Entries added by the executor or by services it uses appear beneath that execution scope.

Constructor-time profile entries from services resolved for the executor are captured on the worker profile root because the active worker profile is established before the executor is resolved.

Workable also records a small result object on that built-in executor method scope after execution returns. Today that result captures whether the execution had errors and how many messages it returned.

## Profile Results

The latest profile is exposed on `WorkerSnapshot.Profile`.

```csharp
var completion = await handle.WaitForCompletion();
var ascii = completion.Worker?.Profile?.ToAsciiTree();
```

Workers capture a profile per iteration. Each retained `WorkerIterationSnapshot` can include its own `Profile`, including run-once workers that produce multiple iterations because of transient retry.

```csharp
var worker = await system.Query.Worker(workerId);

foreach (var iteration in worker?.Iterations ?? [])
{
    var ascii = iteration.Profile?.ToAsciiTree();
}
```

`WorkerSnapshot.Profile` is the latest captured worker profile. `WorkerIterationSnapshot.Profile` is the profile captured for that specific retained iteration.

Profile retention follows the same iteration retention settings as iteration history.
