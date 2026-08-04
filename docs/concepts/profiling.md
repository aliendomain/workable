# Work Profiling

Work profiling captures a per-worker execution tree for diagnostic timing and context. Profiling is controlled by `WorkerOptions.ProfilingEnabled`.

Automatic instrumentation is bounded separately from explicit application profiling. By default, one profile admits up to `500` automatic nodes shared across SQL, HTTP, and extension instrumentation. Calls made directly through `IWorkProfiler` do not consume that automatic budget.

When Workable is hosted in a non-production `IHostEnvironment`, work definitions that do not explicitly set `defaultOptions` inherit profiling enabled by default. Set `defaultOptions: new WorkerOptions(ProfilingEnabled: false)` when a specific work definition should opt out of that non-production default.

When profiling is disabled, the profile API is still available and behaves as a no-op. Work code can add profile information without checking whether profiling is active.

## Feature Summary

| Feature | Behavior |
| --- | --- |
| Profile shape | One execution tree per worker iteration, with the latest iteration also exposed on `WorkerSnapshot.Profile`. |
| Development default | Profiling is enabled by default for definitions without explicit default options when the host environment is not Production. |
| Explicit application profiling | Work code and injected services can add scopes, timings, method scopes, information, and results through `IWorkProfiler`. |
| SQL client timing | Optional `Microsoft.Data.SqlClient` instrumentation records command timing and diagnostic context without changing application query code. |
| HTTP client timing | Optional outbound `HttpClient` instrumentation records the request and its returned response outcome without a wrapper or delegating handler. |
| Automatic growth control | SQL, HTTP, and custom automatic instrumentation share a hard per-iteration node limit of `500` by default. |
| Truncation visibility | A profile that reaches the limit contains one `Automatic instrumentation truncated` summary grouped by instrumentation source. |
| Targeted bypass | Temporary rules can select future workers by work type, actor/user id, or both and resolve them to `Full` capture. |
| Operator surfaces | Capture rules are available in the Workable admin UI and through the built-in HTTP API. |
| Retention | Profiles follow worker-iteration retention and are stored in the same worker and iteration snapshots as explicit profile entries. |

On an authorization-enabled system, retained profile telemetry requires system-level diagnostics permission. A caller who can read or operate work but cannot view diagnostics still receives the authorized worker, iteration, completion, or action result, but `WorkerSnapshot.Profile` and every `WorkerIterationSnapshot.Profile` are removed from that result. This also applies to worker snapshots returned while stopping a system. The authoritative retained snapshots are not modified, so a diagnostics-authorized caller can retrieve the same profiles later.

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

## Automatic Instrumentation Limit

Configure the shared per-profile automatic instrumentation limit at system startup:

```csharp
services.AddWorkableSystem(builder =>
{
    builder.ConfigureProfiling(maximumAutomaticInstrumentationNodes: 500);
});
```

The limit is enforced synchronously with lock-free admission, so a concurrent burst cannot grow one profile beyond the configured automatic-node count. HTTP activity sampling reserves that admission atomically, so concurrent requests cannot all request capture and then race for the last slot. SQL and HTTP consume the same budget rather than receiving independent limits. Built-in instrumentation creates captured context only after admission succeeds. Once a bounded profile is full, HTTP profiling also stops requesting full activity data for later requests in that worker. When operations are omitted, the snapshot includes one `Automatic instrumentation truncated` node with omission counts grouped by instrumentation key. To keep custom instrumentation names from creating unbounded summary state, the first 32 distinct keys are retained, keys are limited to 128 characters, and additional keys are counted as `other`.

Explicit nodes created through `AddInfo`, `StartTiming`, `CreateScope`, and `CreateMethodScope` remain developer-controlled and are not silently discarded.

### Temporary Full Capture

The Workable HTTP API and admin UI can create temporary full-capture rules for future workers. A rule can match a work definition, the stable `WorkRequestContext.Actor.Id`, or both. A matching rule:

- enables profiling for the worker even if its inherited option was disabled;
- sets `WorkerOptions.ProfilingCaptureMode` to `Full`;
- bypasses the shared automatic SQL, HTTP, and extension node limit for that worker;
- is consumed only after queue acceptance succeeds;
- disappears after its configured match count is consumed or its expiration is reached.

Rules are intentionally in-memory operational state. They do not survive a host restart. The resolved `Full` capture mode is stored on an accepted worker's effective options, including durable queue entries, so delayed execution still honors the capture decision.

Rules default to one match and a 30-minute lifetime. The supported ranges are 1–1,000 matches and 1–1,440 minutes, each work-definition or actor-id selector is limited to 512 characters, and a system can have at most 1,000 active rules. When a worker matches more than one rule, a combined work-type-and-actor rule is selected before a broader single-selector rule; equally specific rules are selected oldest first. Steady-state queue matching uses immutable indexes for the exact work-type and actor selectors, then reserves and completes a match atomically without taking the rule-administration lock or scanning unrelated rules. Administrative reads and creates reclaim exhausted or expired rules in one batch and rebuild the index once under the administration lock. Stopping and restarting a system in the same host does not clear its rules, but restarting the host does.

Use short expirations and small match counts. Full capture is deliberately unbounded because it represents an explicit diagnostic choice.

On an authorization-enabled system, explicitly selecting `WorkerOptions.ProfilingCaptureMode.Full` at queue time requires both the work definition's normal queue permission and system-level diagnostics permission. Persisting `Full` in reconfigured definition defaults likewise requires both definition-reconfiguration permission and diagnostics permission. Trusted host startup configuration is not caller-authorized and is unaffected.

`Full` bypasses only the automatic instrumentation node-count limit. It does not bypass:

- work-definition queue or invocation authorization;
- HTTP capture privacy rules, so headers, bodies, URI query contents, URI user information, and exception messages remain excluded;
- SQL parameter-name redaction;
- worker or iteration retention;
- rule match count or expiration.

A system administrator can create a capture rule because system administration includes diagnostics access. That does not grant permission to queue the matching work. A caller with the work definition's queue permission must still submit the worker through a permitted invocation channel.

### Bypass the Limit in the Admin UI

To capture a work type:

1. Select the system.
2. Open **Catalog** and select the work definition.
3. In **Full profile capture**, set **Matching workers** and **Expires after (minutes)**.
4. Select **Capture by work type**.

To capture work for a user or actor:

1. Select the system and open **Workers**.
2. Open a retained worker created by that actor and expand **Worker controls**.
3. In **Full profile capture**, choose **Capture by user** or **Capture this user + work type**.

The worker detail page uses that worker's stable actor id only as a selector. It does not recapture or change the existing worker. The rule applies to future accepted workers. Definition names match case-insensitively; actor ids match exactly.

The UI needs access to the built-in Workable HTTP surface and diagnostics access to list, create, or delete rules. A system administrator has diagnostics permission by default. Rule creation is separate from queueing, and the UI does not elevate queue rights.

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

Only logical scopes created by `CreateScope(...)` and `CreateMethodScope(...)` retain `SetResult(...)` payloads. The handle returned by `StartTiming(...)` still exposes `SetResult(...)` through the shared scope interface, but timing scopes currently ignore that call.

When the context object is not a string, `WorkProfileSnapshot.ToAsciiTree()` renders it as JSON beneath that profile node.

In the ASCII output, `Tree` is the node's inclusive time. `Node` is the node's retained node time. For scope nodes, that retained node time is the inclusive scope time minus nested scope time. Timings listed directly under the scope are still shown as child rows, but they do not reduce the parent scope's `Node` value.

## Built-In Scope

When profiling is enabled, Workable wraps the executor call in a method scope. Entries added by the executor or by services it uses appear beneath that execution scope.

Constructor-time profile entries from services resolved for the executor are captured on the worker profile root because the active worker profile is established before the executor is resolved.

Workable also records a small result object on that built-in executor method scope after execution returns. Today that result captures whether the execution had errors and how many messages it returned.

## Automatic HTTP Client Timing

Hosts can capture outbound `HttpClient` requests as profile timing nodes:

```csharp
services.AddWorkableHttpClientProfiling();
```

The registration makes the HTTP instrumentation available to every Workable system in the host. Workable still emits HTTP timing nodes only while a worker profile is active. Because workers that do not explicitly configure `defaultOptions` inherit profiling enabled in a non-production `IHostEnvironment`, registered HTTP instrumentation is active for those workers by default during development. Production workers must still opt into profiling through their definition, queue options, or runtime reconfiguration.

HTTP timing uses the built-in `System.Net.Http` activity source, so application code does not need to wrap `HttpClient`, add a delegating handler, or use `IHttpClientFactory`. One timing starts with the outbound request and is completed with the response returned by that request. Each captured timing contains the HTTP method, a sanitized request URI, protocol version, response status, outcome, and transport error type when available.

This instruments outbound dependency calls made through `HttpClient`; it does not profile inbound ASP.NET Core server requests. The returned response for an outbound call is part of the same outbound timing rather than a separate inbound-request profile.

Workable installs one HTTP activity listener for all started Workable systems in the host and routes each request through the ambient profiling context. The listener requests local activity data without setting the distributed-tracing recorded flag, so enabling Workable HTTP profiling does not force downstream parent-based tracing systems to sample the request.

After a bounded profile reaches its automatic-node limit, the listener returns `None` for later HTTP requests in that worker. Workable therefore does not force creation of full request activities or their tags after the cap. Full-capture workers continue sampling because bypassing that cap is their explicit purpose.

Requests still running when worker execution ends are finalized with an `Incomplete` outcome before the profile snapshot is created. Their timing and context remain stable after publication, and a stale ambient context cannot append HTTP nodes after finalization.

HTTP capture is deliberately conservative:

- Request and response headers are not retained. That includes `Authorization`, cookies, API keys, authentication challenges, and custom security headers.
- Request and response bodies are not retained.
- URI query strings, fragments, and user information are not retained. `HasQueryString` is `true` or `false` when the bounded inspection can determine that safely, and `null` when the separator would be beyond the inspected prefix; names and values are never retained.
- URI parsing and query detection inspect at most the first 4,096 characters before retaining the 2,048-character sanitized value. `UriInspectionTruncated` reports that the source exceeded the inspection window. If an absolute URI is malformed, an authority-form reference is relative, or an oversized absolute URI does not expose a complete authority within that window, Workable omits the URI instead of falling back to raw text and risking retention of user information. Runtime cost therefore does not grow with an arbitrarily long source URI.
- Error messages are not retained because transport exceptions can repeat sensitive URI or endpoint data. Only the runtime's error type is captured.

Registration is idempotent and separate from worker profiling, matching SQL profiling configuration:

```csharp
services.AddWorkableHttpClientProfiling();
services.AddWorkableSqlServerProfiling();
```

Each registration advertises its own system capability and contributes automatic timing only to workers whose effective `ProfilingEnabled` option is `true`.

## Automatic SQL Client Timing

Hosts using the SQL Server extension can capture `Microsoft.Data.SqlClient` command timing:

```csharp
services.AddWorkableSqlServerProfiling();
```

The integration observes SqlClient diagnostics, so application code does not need to wrap commands. One shared diagnostic observer serves all started Workable systems in the host and routes commands through the ambient system-owned profiling context. Command-start diagnostics are disabled when no registered Workable profile is active; completion diagnostics remain enabled only while a profiled command is outstanding or an eligible profile is active. This prevents process-wide SQL payload construction for unrelated commands. A SQL timing can include the operation, command type, statement kind and text, parameter metadata and values, database, and transaction presence. SQL failures set `Outcome` to `Faulted` and add the bounded exception type and message to that same timing node; they do not duplicate the statement and parameters in a second error node. Commands still running when the worker snapshot is published are finalized with an `Incomplete` outcome, and late provider events cannot mutate the published snapshot.

SQL payloads are bounded independently of the automatic-node count: statement inspection and retained text are limited to 8,192 characters; at most 32 parameters are retained; parameter context has an approximately 4,096-character aggregate budget; individual text values are limited to 1,024 characters; binary previews are limited to 256 bytes; exception messages are limited to 1,024 characters; and individual metadata fields are limited to 512 characters. The captured context reports statement, parameter, value, and exception-message truncation. Parameter names that look like passwords, secrets, tokens, API keys, access keys, private keys, or shared-access signatures have their values replaced with `<redacted>`. Values of unsupported application-defined parameter types are represented by a type placeholder such as `<CustomValue>`; profiling never invokes arbitrary application `ToString()` implementations.

SQL statement text and parameter values that do not match the redaction rules can contain sensitive application data. Enable SQL profiling only where retaining that data is acceptable. `Full` capture bypasses the shared node-count limit but does not disable SQL parameter redaction.

## Profiling Instrumentation Extensions

Provider integrations can add other automatic instrumentation through `IWorkProfilingInstrumentationFactory`. Workable creates each registered factory's instrumentation once per started system, passes the system id and ambient `IWorkProfilingContextAccessor`, and disposes the returned handle when that system stops.

Instrumentation must check that the ambient profiling context belongs to the system supplied to the factory before contributing nodes. This preserves isolation when one dependency-injection container hosts multiple Workable systems. The built-in HTTP client capture and the SQL Server integration both use this shared lifecycle.

Automatic extensions should contribute through `WorkProfilingContext.TryStartAutomaticTiming(...)` and `TryAddAutomaticInfo(...)`. Prefer their context-factory overloads so expensive context is created only after Workable admits the node. Those methods participate in the shared automatic-node budget and continue to work with custom `IWorkProfiler` implementations that predate automatic budgeting.

Asynchronous instrumentation that can outlive worker execution can register an `IWorkProfilePendingInstrumentation` with `IWorkProfilePendingInstrumentationRegistry`. The profile finalizes registered operations before constructing its immutable snapshot; built-in HTTP and SQL instrumentation use this contract to publish stable `Incomplete` timings.

The registration entry/exit window must remain short and the exit call must be placed in a `finally` block. Profile publication waits on a drain signal rather than actively spinning, but it still cannot safely publish while an integration is in the middle of registering a timing.

## Profile Results

The latest profile is exposed on `WorkerSnapshot.Profile`.

```csharp
var completion = await handle.WaitForCompletion();
var ascii = completion.Worker?.Profile?.ToAsciiTree();
```

The parameterless renderer is bounded to 10,000 nodes and 256 levels. It appends `profile rendering truncated` when either limit is reached, preventing an intentionally full or unusually deep profile from producing unbounded diagnostic text. Use `ToAsciiTree(maximumNodes, maximumDepth)` to request smaller limits.

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
