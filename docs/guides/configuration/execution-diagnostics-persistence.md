# Persistent Execution Diagnostics

Persistent execution diagnostics retain iteration logs and profiles so a developer or agent can analyze work that just ran, including questions such as how many SQL commands or HTTP requests were executed. They are execution evidence, not a general production log archive.

Persistence must be registered before it can be enabled. The SQL Server provider can be registered independently of durable queueing:

```csharp
services.AddWorkableSqlServerPersistence(
    connectionString,
    schemaName: "workable",
    persistenceScope: "my-application");
```

`AddWorkableSqlServerDurableQueue(...)` also registers the diagnostics repository. Custom providers implement `IWorkExecutionDiagnosticsRepository`.

When durable queue and explicit diagnostics persistence are both registered, they must use the same SQL Server connection and schema. The explicit persistence options determine the diagnostics persistence scope regardless of registration order; conflicting stores or conflicting explicit persistence configurations fail during service registration.

## Work and system configuration

Enable persistence for one work definition:

```csharp
builder.AddWork<GenerateReportWork>(
    WorkDefinition.Create("reports.generate"),
    configuration => configuration.PersistExecutionDiagnostics(
        retention: TimeSpan.FromDays(7),
        minimumLogLevel: LogLevel.Debug,
        profileCaptureMode: WorkProfileCaptureMode.Bounded));
```

Enable it for every work definition that does not explicitly override the system policy:

```csharp
services.AddWorkableSystem(builder =>
{
    builder.PersistExecutionDiagnostics(
        retention: TimeSpan.FromDays(7),
        minimumLogLevel: LogLevel.Information,
        profileCaptureMode: WorkProfileCaptureMode.Bounded);
});
```

`DisableExecutionDiagnosticsPersistence()` explicitly opts one work definition out of an inherited system policy. The full configuration records are `WorkExecutionDiagnosticsPersistenceConfiguration` and `WorkSystemExecutionDiagnosticsPersistenceConfiguration`.

Persistence is definition-scoped: queue-time and worker-level runtime configuration cannot enable or disable it for an individual worker. Changing a definition's persistent-diagnostics policy through runtime reconfiguration requires `ControlSystem`; trusted startup configuration is unaffected.

Retention is mandatory and must be between one minute and 30 days. Completed artifacts become invisible when they expire, and the background cleanup service physically removes expired rows in bounded batches. It also removes abandoned incomplete artifacts and expired temporary rules.

## Logging and memory limits

Persistent log capture has its own `MinimumLogLevel`. It is intentionally independent of `WorkLoggingConfiguration.Level` and `MaximumBufferedEntries`:

- the retained worker snapshot continues to obey its hard in-memory entry limit;
- every eligible persistent log can be queued even after that in-memory buffer is full;
- structured logging properties, exception details, and current activity trace/span ids are retained with the persistent entry.

Persistent evidence has independent hard bounds. By default, the writer retains at most 10,000 logs and approximately 16 MiB of log payload per iteration, 64 MiB of queued log payload across the system, 32 completed live profiles awaiting asynchronous materialization, 100,000 profile nodes, and 4 MiB of serialized profile JSON. Messages, properties, and exception text are individually truncated. `DroppedLogCount`, `ProfileDropped`, and the `workablePropertiesTruncated` structured property make that loss discoverable. These defaults can be tightened through `WorkSystemExecutionDiagnosticsPersistenceConfiguration`.

Log messages, structured properties, exception data, and application-authored profile context can contain sensitive values. Workable preserves them as diagnostic evidence and does not apply general-purpose redaction. Keep retention short, restrict diagnostics permission, and redact sensitive application values before logging them. Built-in SQL profiling retains its existing parameter redaction and payload limits.

Execution does not call the repository. It uses a bounded, non-blocking in-process channel and a single background batch writer. Repository latency therefore does not delay work execution. The channel has separate bounded evidence and reserved control-operation budgets so a saturated evidence lane does not normally prevent iteration completion from being recorded. Profile snapshot materialization, profile size validation, structured-property JSON serialization, and instrumentation-summary construction run on the writer.

Persistent-only log messages and structured property values are copied into stable, bounded representations during the logging call. This small amount of synchronous work is necessary: retaining arbitrary logger state or formatter closures for deferred processing would make the memory limit unreliable and could observe pooled or subsequently mutated state.

If the channel is saturated, Workable drops diagnostic evidence rather than blocking the work. `DroppedLogCount` reports lost log entries. When profiling was requested but its live profile could not be queued or materialized, `ProfileDropped` is `true`; this distinguishes diagnostic loss from profiling not being requested. A failed repository log batch is also counted as dropped rather than reported as persisted. This is process isolation from the execution path, not a separate operating-system process.

Because persisted profile finalization is asynchronous, the `WorkCompletion` returned to the caller can precede the profile appearing on the retained worker iteration. The completed diagnostic artifact is the stable handoff for agent analysis; the writer also attaches a successfully materialized profile back to the retained iteration when it is still available.

## Profiling policy

In a non-production `IHostEnvironment`, enabling persistent diagnostics automatically enables the configured profile capture mode for matching work. In production, static persistence configuration records logs but does not automatically turn profiling on.

A temporary capture rule can explicitly request `Bounded` or `Full` profiling in production. This is intended for a short emergency investigation and always has both an active lifetime and an artifact retention period. `Full` bypasses the live automatic-instrumentation node limit but does not bypass the persistent artifact limits, SQL redaction, or other payload limits.

A temporary rule with no profile capture mode is logs-only. It does not persist a profile even when the matching worker independently has profiling enabled; that worker's ordinary in-memory profile remains available through the normal diagnostics surface.

The admin UI exposes temporary system-level and work-definition-level controls only when `ExecutionDiagnosticsPersistenceAvailable` is true. Viewing rules and evidence requires diagnostics access; creating or deleting persistent capture rules requires `ControlSystem`. Deleting a rule stops future matching capture; it does not delete artifacts already captured.

Active rules are loaded into an immutable, definition-indexed in-memory snapshot. Each iteration start performs an atomic cache read and exact-definition lookup; it does not query the repository or sort the complete rule collection. Profiling is enabled from that same effective iteration policy, so a rule applies to work that was queued before the rule was created and stops applying to queued work when the rule is deleted or expires. Creates and deletes update the snapshot immediately, while the cleanup refresh reconciles changes made by another process. Rule count and selector length are bounded.

The HTTP execution-diagnostics routes are mapped only when an execution diagnostics repository is registered. The admin UI uses the system capability to avoid requesting those routes when persistence is unavailable.

## Querying evidence

The HTTP API exposes:

```text
GET    /workable/execution-diagnostics
GET    /workable/execution-diagnostics/workers/{workerId}/iterations/{sequence}
GET    /workable/execution-diagnostics/capture-rules
POST   /workable/execution-diagnostics/capture-rules
DELETE /workable/execution-diagnostics/capture-rules/{ruleId}
```

The MCP adapter exposes `workable_query_execution_diagnostics` for compact iteration and instrumentation summaries and `workable_get_execution_diagnostic` for logs and profile JSON. Both surfaces require system diagnostics access. A single artifact response returns at most 10,000 logs and reports `LogsTruncated` when more persisted logs exist.

Instrumentation summaries group profile nodes by their stable instrumentation key, such as `sql.client` or `http.client`. They include timing totals, maximum duration, node counts, and omitted-node counts. An agent should report omitted counts when interpreting a bounded profile rather than presenting an incomplete operation count as exact. It should also check `ProfileDropped` before interpreting a missing profile as evidence that profiling was disabled.

Every artifact also snapshots `SqlClientProfilingAvailable` and `HttpClientProfilingAvailable` when the iteration starts. This lets an agent distinguish zero captured operations from instrumentation that was not registered for that execution. The snapshot is historical evidence; it does not change if the host registers or removes an instrumentation feature later.

The SQL Server provider stores normalized iteration, log, instrumentation-summary, and temporary-rule rows. The complete profile tree is retained as JSON because its context payload and recursive structure are intentionally open-ended.
