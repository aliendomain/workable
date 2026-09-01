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

SQL persistence registration is host-level service configuration, not a setting on one Workable system. One SQL connection/schema pair is registered per service collection, and every Workable system in that host uses that repository. `PersistenceScope` plus the logical Workable system name keeps each system's diagnostic rows isolated and stable across process restarts.

## Schema deployment

`AutoDeploySchema` defaults to `true`. One host-scoped initializer coordinates the shared SQL connection and schema: one completed deployment result is reused per application host, and each schema component is validated at most once after success even when the host contains multiple Workable systems. A component failure is shared across the other systems encountering that same startup failure, while a repeated initialization attempt for the same system retries it. A canceled deployment is not cached and can be retried immediately. If durable-queue and explicit persistence registrations specify different values, automatic deployment is enabled when either registration enables it. Ordered migrations newer than the installed diagnostics component version use the same `SchemaVersion` table as durable queue and workflow persistence. A database with no Workable objects receives the complete current schema directly. Existing Workable tables without version metadata fail deployment rather than being assumed current. Execution diagnostics has its own component version; it does not share the queue component's version number.

For environments where applications cannot change schemas, disable automatic deployment and apply the generated script before startup:

```csharp
services.AddWorkableSqlServerPersistence(
    new WorkableSqlServerPersistenceOptions
    {
        ConnectionString = connectionString,
        SchemaName = "workable",
        PersistenceScope = "my-application",
        AutoDeploySchema = false,
    });
```

Use the [SQL Server schema CLI](../../../packages/extensions/sqlserver/README.md#schema-cli) to generate or apply that script. With automatic deployment disabled, Workable validates the installed tables, columns, indexes, and component version. A missing or incomplete diagnostics schema leaves execution-diagnostics persistence unhealthy while the Workable system and application host continue starting; durable queue and workflow persistence retain their existing failure behavior.

If the diagnostics provider fails to initialize, Workable emits an `Error`-level `ExecutionDiagnosticsInitializationFailed` log with the provider exception, disables persisted execution diagnostics for that system until the process restarts, and continues system and host startup. This includes an unavailable SQL Server or database, configuration and permission errors reported by the provider, and a missing or incomplete Workable diagnostics schema. `IWorkSystem.Diagnostics.ExecutionDiagnosticsPersistence` and the authorized HTTP diagnostics response report `Status` as `Unhealthy`, expose `InitializationFailedAt`, and report `PersistenceAvailable` and `IsHealthy` as false. Provider exception details remain server-side because they can contain sensitive connection information. The system capability also reports diagnostics persistence as unavailable, evidence reads return no persisted results, and capture-rule mutations are unavailable. Cancellation still interrupts startup. A failed host-scoped SQL diagnostics deployment attempt is shared with later systems so they do not repeatedly reconnect to or redeploy against the same failing database during that startup. It does not suppress the first durable queue or workflow deployment attempt, and a repeated durable initialization for the same system retries after recovery.

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
        retentionPeriod: TimeSpan.FromDays(7),
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

`WorkProfileCaptureMode` controls automatic instrumentation nodes; it does not change the persistent log level or artifact limits:

| Value | Meaning |
| --- | --- |
| `Bounded` | Captures automatic SQL, HTTP, and extension nodes up to `WorkSystemProfilingConfiguration.MaximumAutomaticInstrumentationNodes`. Omitted automatic nodes are counted in the profile and instrumentation summary. |
| `Full` | Bypasses the automatic-instrumentation node-count limit for the selected iteration. Persistent profile node and serialized-size limits still apply. |

There is no `None` enum value. A temporary rule uses a null profile capture mode for logs-only capture. Outside a temporary rule, profiling is disabled with the normal `ProfilingEnabled` option rather than a capture-mode value.

Dependency counts require their profiling integrations to be registered in addition to persistence:

```csharp
services.AddWorkableSqlServerProfiling();
services.AddWorkableHttpClientProfiling();
```

Without those registrations, profiles can still contain application-authored nodes, but they cannot contain automatic `sql.client` or `http.client` timings. Each artifact records whether those integrations were available at iteration start so an agent can distinguish “zero operations” from “instrumentation was unavailable.”

A temporary capture rule can explicitly request `Bounded` or `Full` profiling in production. This is intended for a short emergency investigation and always has both an active lifetime and an artifact retention period. `Full` bypasses the live automatic-instrumentation node limit but does not bypass the persistent artifact limits, SQL redaction, or other payload limits.

A temporary rule with no profile capture mode is logs-only. It does not persist a profile even when the matching worker independently has profiling enabled; that worker's ordinary in-memory profile remains available through the normal diagnostics surface.

The admin UI exposes temporary system-level and work-definition-level controls only when `ExecutionDiagnosticsPersistenceAvailable` is true. Viewing rules and evidence requires diagnostics access; creating or deleting persistent capture rules requires `ControlSystem`. Deleting a rule stops future matching capture; it does not delete artifacts already captured.

The control at the top of the system catalog creates a rule for all work; the catalog definition control creates a rule for one work type. Both require an active lifetime and an artifact retention between one minute and 30 days. Creating or updating either scope replaces the prior active rule for that same scope, so repeatedly saving a scope does not accumulate duplicate rules. The equivalent HTTP request is:

```http
POST /workable/execution-diagnostics/capture-rules
Content-Type: application/json

{
  "definitionName": "reports.generate",
  "minimumLogLevel": "Information",
  "profileCaptureMode": "Bounded",
  "activeForMinutes": 30,
  "artifactRetentionMinutes": 1440,
  "description": "Compare SQL activity after the query change"
}
```

Omit `definitionName` for a system-wide rule. Set `profileCaptureMode` to `null` for logs-only capture.

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

Named systems expose the same routes below `/workable/systems/{systemName}`. Queries accept definition, worker, completion-time, minimum-log-level, and result-count filters; `take` defaults to `100` and must be between `1` and `1000`. The core diagnostics boundary enforces that range before calling either the built-in SQL Server repository or a custom provider. Use the query route before requesting one complete artifact.

Capture-rule validation failures return actionable Workable-owned messages. If a custom repository fails while persisting an otherwise valid capture rule, the HTTP route returns a stable `workable.execution_diagnostics.repository_failed` response without copying the provider exception message across the transport boundary; the underlying exception is recorded through the host logger.

The MCP adapter exposes `workable_query_execution_diagnostics` for compact iteration and instrumentation summaries and `workable_get_execution_diagnostic` for logs and profile JSON. Both surfaces require system diagnostics access. A single artifact response returns at most 10,000 logs and reports `LogsTruncated` when more persisted logs exist.

Instrumentation summaries group profile nodes by their stable instrumentation key, such as `sql.client` or `http.client`. They include timing totals, maximum duration, node counts, and omitted-node counts. An agent should report omitted counts when interpreting a bounded profile rather than presenting an incomplete operation count as exact. It should also check `ProfileDropped` before interpreting a missing profile as evidence that profiling was disabled.

Every artifact also snapshots `SqlClientProfilingAvailable` and `HttpClientProfilingAvailable` when the iteration starts. This lets an agent distinguish zero captured operations from instrumentation that was not registered for that execution. The snapshot is historical evidence; it does not change if the host registers or removes an instrumentation feature later.

### Agent interpretation checklist

For a question such as “how many SQL executes did the work just perform?” an agent should:

1. Call `workable_query_execution_diagnostics`, filtered by worker id or definition and a narrow completion window.
2. Select the intended worker iteration and inspect its capture source, profile capture mode, expiry, and SQL/HTTP instrumentation availability.
3. Read `TimingCount` from the `sql.client` instrumentation summary for the captured SQL command count. Use `http.client` for outbound HTTP requests.
4. Report `OmittedNodeCount` when it is nonzero; the timing count is then a captured lower bound rather than an exact total.
5. Check `ProfileDropped` before interpreting a missing profile or summary, and report `DroppedLogCount` when answering questions based on logs.
6. Call `workable_get_execution_diagnostic` only when the complete log stream, individual timing nodes, or profile context is needed. Check `LogsTruncated` before treating returned logs as complete.

If instrumentation availability is false, the correct conclusion is that the provider-specific profiling feature was not registered—not that the iteration performed zero operations. If availability is true, the profile was retained, and no nodes were omitted, a missing instrumentation summary means zero captured operations for that source.

The SQL Server provider stores normalized iteration, log, instrumentation-summary, and temporary-rule rows. The complete profile tree is retained as JSON because its context payload and recursive structure are intentionally open-ended.
