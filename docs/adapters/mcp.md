# Workable MCP

Workable can expose authored work definitions, work-system and workflow query tools, and worker or workflow action tools through the `Workable.Mcp` adapter package.

The adapter does not change how work is authored or executed. It projects an `IWorkSystem` catalog into tool descriptors and invokes tools by queueing work through Workable.

MCP exposure is opt-in. A work definition must allow `WorkInvocationChannel.Mcp` to appear as an MCP tool or be invoked through the MCP adapter.

The ASP.NET Core MCP endpoint is an authenticated transport. Anonymous callers are rejected before the MCP request handler runs, and mapped systems must be authorization-enabled.

Each ASP.NET Core MCP request creates a `WorkRequestContext` and an `IWorkSystemSession` for the selected system. Work-definition discovery access filters work-tool descriptors, read access filters query results, and operate access controls work invocation and worker action tools. Read and operate each imply discovery, but discovery alone permits neither queries nor invocation.

## Server Setup

`Workable.Mcp` includes an ASP.NET Core MCP server integration. Add it to the same host application that registers Workable systems.

```csharp
var builder = WebApplication.CreateBuilder(args);

// Register the host's real authentication handler, validation, and scheme defaults here.
// For example, use the host's existing JWT bearer, Microsoft Identity Web,
// cookie, or custom authentication setup.

builder.Services.AddWorkableSystem(workable =>
{
    workable.StartWithHost();
});

builder.Services.AddWorkableMcpServer();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.MapWorkableMcp();

await app.RunAsync();
```

The default MCP endpoint is `/workable/mcp`.

The mapped endpoint uses the same Workable ASP.NET Core authentication behavior as the other HTTP-based adapters. If the host configures `WorkableAspNetCoreAuthorizationOptions.TransportAuthenticationScheme`, the MCP endpoint authenticates against that explicit scheme instead of the ambient default. An authentication failure invokes the selected host handler's challenge. Once invoked, that handler owns the complete status, headers, events, redirect, and body; Workable writes a plain 401 only when no challenge scheme is available.

The authentication and authorization calls are required host-integration steps, not setup supplied by Workable. A real host must add its authentication handlers, validation settings, and authorization policies; `Workable.Mcp` does not provide them.

By default, `MapWorkableMcp()` adds ordinary ASP.NET Core authorization metadata, so the host's `DefaultPolicy` owns endpoint authorization and its challenge or forbid response. A named `authorizationPolicy` or `useHostFallbackPolicy: true` selects the corresponding host-owned behavior:

| Mapping | Endpoint metadata | Host policy |
| --- | --- | --- |
| `MapWorkableMcp()` | Ordinary authorization metadata | `DefaultPolicy` |
| `MapWorkableMcp(authorizationPolicy: "HostPolicy")` | Named authorization metadata | `HostPolicy` only; the default policy is not implicitly added |
| `MapWorkableMcp(useHostFallbackPolicy: true)` | No Workable authorization metadata | `FallbackPolicy`, when the host configured one and runs authorization middleware |

Workable only references policies already registered by the host; it does not create requirements, select policy authentication schemes, or configure authentication. When an explicitly selected Workable transport scheme differs from the scheme authenticated by the default policy, register a host policy for the intended scheme and pass its name through `authorizationPolicy`. Selecting both a named policy and fallback-policy mode is rejected.

MCP clients must tolerate the host handler's challenge contract. Depending on the selected scheme, an authentication failure need not be a plain `401` and Workable does not append a second response after the host handler runs.

`MapWorkableMcp` targets the default system unless a system name is supplied. Map a separate MCP endpoint for each named system that should be exposed.

```csharp
app.MapWorkableMcp();                                      // default system at /workable/mcp
app.MapWorkableMcp("/workable/systems/email/mcp", "email"); // named system
```

An MCP client connected to `/workable/systems/email/mcp` only sees tools for the `email` Workable system.

Named-system selection is non-enumerable at runtime. Tool discovery returns an empty list for both an unknown named system and a named system the caller cannot access; direct tool calls return the same `workable.mcp.system_not_found` result in both cases. Once the caller has some access to the selected system, ordinary definition, worker, workflow, and operation authorization still determines the visible tools and call outcomes.

The MCP server exposes three kinds of tools:

- Work tools queue work definitions that allow `WorkInvocationChannel.Mcp`.
- Query tools inspect worker status, worker snapshots, work definitions, work info, work keys, status summaries, and workflow runs.
- Action tools start, pause, cancel, push, and purge existing workers; start and operate workflow runs; and reconfigure work definition defaults.

The tool list is caller-scoped. Discover permission controls authored work descriptors, Read permission controls the corresponding work or workflow query tools, and the exact fine-grained operation grant controls each action tool. For example, queue-only access can expose an authored work tool or `workable_start_workflow` without exposing worker controls, while definition-reconfiguration access exposes only `workable_reconfigure_work_definition`. A disabled or unauthorized tool is also rejected when called directly, even if the client learned its protocol name elsewhere.

Work tools use MCP-safe names. For example, a Workable work definition named `email.welcome.send` is exposed as `workable_work_email_welcome_send`.

If multiple work definitions normalize to the same MCP-safe name, the server disambiguates them by appending a short definition-id suffix.

Work queued through the ASP.NET Core MCP server records a `WorkOrigin` with `WorkInvocationChannel.Mcp`. When an HTTP context is available, the origin uses the principal selected for Workable actor identity and records only the MCP request path as the origin URL; query strings are excluded. This is normally `HttpContext.User`; an explicitly selected Workable transport scheme remains private to Workable request state. Protocol-facing work tools, action tools, and reconfiguration tools can also carry an optional caller-supplied `description`, which Workable copies into the origin.

## Work Tool Inputs

Protocol-facing MCP work tools accept either the raw work input directly or a wrapped object when the caller wants to attach an origin description.

Raw input:

```json
{
  "userId": "user-123"
}
```

Wrapped input with origin description:

```json
{
  "input": {
    "userId": "user-123"
  },
  "description": "Send the delayed welcome email after support verified the account."
}
```

For inputless work, use `null` for `input` when you still want to attach a description.

```json
{
  "input": null,
  "description": "Run the daily cache refresh now."
}
```

## Query Tools

Query tools are enabled by default, but each caller sees work query tools only with work Read access and workflow query tools only with workflow Read access. Persistent execution-diagnostics tools instead require diagnostics permission and an available repository.

- `workable_query_workers`
- `workable_get_worker`
- `workable_get_worker_iteration`
- `workable_query_worker_iterations`
- `workable_get_work_info`
- `workable_query_work_definitions`
- `workable_query_worker_keys`
- `workable_query_worker_key_types`
- `workable_query_work_iteration_keys`
- `workable_query_work_iteration_key_types`
- `workable_get_worker_status_summary`
- `workable_query_workflow_runs`
- `workable_get_workflow_run`
- `workable_query_execution_diagnostics`
- `workable_get_execution_diagnostic`

These tools use the same query engine as the .NET API. They do not mutate workers. Worker queries can filter by selected configuration flags with `recurrenceEnabled`, `concurrencyEnabled`, and `profilingEnabled`.

Use `workable_query_worker_key_types` when the user asks broadly for workers tied to a relationship type, such as claim work or customer work. It groups by key type across subjects, concurrency keys, and identifiers, and supports pagination. Use `workable_query_worker_keys` when the user gives a specific relationship phrase, such as claim id CLM-123. Both tools can filter the returned workers by state and return matching `WorkerOverviewItem` rows, so the MCP client can inspect worker ids, states, definitions, revisions, and categories directly from the key search result.

Use `workable_query_work_iteration_key_types` and `workable_query_work_iteration_keys` when the user asks about actual executions tied to a relationship, such as failed claim work or completed customer work. These tools filter by iteration completion status and return `WorkerIterationOverviewItem` rows.

Use `workable_query_worker_iterations` when the user asks about execution history, recent failures, retry attempts, or recurring activity. It can filter by worker id, work name, category, completion status, subject, concurrency key, identifier, and time range. Use `workable_get_worker_iteration` when the client already has a worker id and iteration sequence and needs the full iteration output, messages, logs, or profile.

Use `workable_query_execution_diagnostics` when an agent needs persisted execution evidence or compact counts grouped by instrumentation source. Its optional `take` argument defaults to `100` and must be between `1` and `1000`; the runtime enforces that range even when a client bypasses descriptor-schema validation. Use `workable_get_execution_diagnostic` after selecting a worker iteration when the agent needs its complete persistent log stream and profile tree. These tools are advertised only while the selected system reports initialized execution-diagnostics persistence as available, and invoking them requires system diagnostics permission. They are not advertised after repository initialization fails.

Malformed protocol arguments return `workable.mcp.arguments_invalid` with Workable-owned validation guidance. An unexpected failure from a host-supplied system, session, query implementation, or diagnostics repository instead returns the stable `workable.mcp.tool_failed` result; provider exception text is logged by the host when logging is available and is not copied into the MCP response.

For operation-count questions, read `timingCount` under the relevant instrumentation key (`sql.client` or `http.client`) and report `omittedNodeCount` when it is nonzero. Before treating a missing profile as “profiling was off,” check `profileDropped`; a true value means profiling was requested but the best-effort writer could not retain it. Also check the per-iteration SQL and HTTP profiling availability flags: false means the corresponding instrumentation was not registered, not that the work executed zero operations. See [Persistent Execution Diagnostics](../guides/configuration/execution-diagnostics-persistence.md#agent-interpretation-checklist).

Use `workable_query_workflow_runs` for operator-style workflow monitoring. It can include final runs, filter by workflow definition name, control the compact child-worker sample size, and page with `skip` plus `take`. Pages default to `0`/`50`, `skip` accepts `0` through `10000`, `take` accepts `1` through `100`, and responses include `totalCount`, `skip`, and `take`. Use `workable_get_workflow_run` for one run's step graph and child-worker summaries. Compact run projections retain at most 256 distinct child-worker ids and matching receipts so one high-fan-out run cannot make a tool call proportional to its complete child history.

## Action Tools

Action tools are enabled by default, but each caller sees an action tool only when at least one registered definition grants that specific operation. `AllowOperate...` grants the complete operation set; `AllowQueue...`, `AllowWorkerActions...`, and `AllowOperations...(mask)` expose only their corresponding tools. Each worker action requires a `workerId` and the current `revision` from `workable_get_worker` or `workable_query_workers`.

- `workable_start_worker`: start or retry a queued, paused, or failed worker.
- `workable_pause_worker`: pause a queued, running, waiting, or retrying worker.
- `workable_cancel_worker`: permanently stop a non-final worker.
- `workable_push_worker`: skip the current recurrence wait and begin the next iteration immediately.
- `workable_purge_worker`: permanently remove a completed or canceled worker from memory.
- `workable_reconfigure_work_definition`: change a work definition's default worker options and/or default runtime configuration for future queued workers.
- `workable_start_workflow`: create a new run for a registered workflow name, optionally with workflow input and wait-for-completion behavior.
- `workable_start_workflow_run`: resume an existing paused or blocked workflow run by run id.
- `workable_pause_workflow_run`: pause a running workflow and its pausable outstanding child workers.
- `workable_cancel_workflow`: cancel a workflow and request cancellation of its outstanding cancellable child workers.

`workable_stop_workflow` remains a compatibility alias for `workable_pause_workflow_run`.

Worker action tool calls record a `WorkOrigin` with `WorkInvocationChannel.Mcp`. The origin is retained in the worker's action history and is published on the action event. Action tools accept an optional top-level `description` that is copied into that origin. Definition default reconfiguration requires the definition name and current revision, and `workable_reconfigure_work_definition` also accepts an optional top-level `description`. The reconfiguration operation does not require Read permission; callers that also have Read can obtain the revision from `workable_query_work_definitions` or `workable_get_work_info`, while operate-only callers must receive or retain the current revision through their host workflow.

Definition reconfiguration must contain at least one actual change. Supply either a `changes` object containing `defaultOptions` and/or `configuration`, or those properties at the top level—never both forms together. Unknown top-level arguments, non-object values, case-insensitive duplicates at any nesting depth, unknown nested configuration/option members, and empty change sets return `workable.mcp.arguments_invalid` without mutating the definition.

Syntactically valid reconfiguration objects still pass through Workable's configuration-domain validation. Undefined numeric enum values return an invalid reconfiguration outcome with the corresponding configuration message and do not advance the definition revision.

Read remains independent from these action tools. An operate-only worker action returns its status and worker id with `worker: null`; an operate-only definition reconfiguration returns its status and authoritative `revision` with `definition: null`; and an operate-only workflow start or action returns its status and run id with `run: null`. Grant Read when the client must receive the retained snapshot.

The workflow-operation mapping is explicit: starting a new run requires `Queue`, resuming a paused or blocked run requires `Start`, pausing requires `Pause`, and canceling requires `Cancel`. A caller with only one of those grants does not receive the other workflow action tools.

For state-changing calls, a hidden target and a nonexistent target both return the same not-found shape. A discoverable target that exists but rejects the requested operation can still return an unauthorized or invalid outcome. This prevents generic MCP action tools from becoming definition-name or worker/run-id enumeration oracles.

```json
{
  "workerId": "22222222-2222-2222-2222-222222222222",
  "revision": 3,
  "description": "Cancel the duplicate worker after operator review."
}
```

```json
{
  "name": "email.welcome.send",
  "revision": 7,
  "description": "Enable profiling for future workers during the incident.",
  "changes": {
    "defaultOptions": {
      "profilingEnabled": true
    }
  }
}
```

## Tool Descriptors

Use `GetMcpToolDescriptors` on an `IWorkSystemSession` to describe the work available to the current caller in one Workable system.

```csharp
IWorkSystemSession session = await workSystem.CreateSession(
    requestContext,
    cancellationToken);

IReadOnlyList<WorkableMcpToolDescriptor> tools =
    session.GetMcpToolDescriptors();
```

Each descriptor includes the work name, description, category, input schema, output schema, schema content types, fallback-schema usage, and definition metadata. Definitions that do not allow `WorkInvocationChannel.Mcp` are omitted.

Definitions the caller cannot discover are also omitted from the descriptor list. `AllowDiscoverToGroups(...)` and `AllowDiscoverToKnownAuthenticatedUsers()` can grant schema-only tool discovery. Read or operate permission also makes the descriptor discoverable automatically.

Descriptor visibility and invocation are intentionally separate. A discovery-only caller can build a tool catalog, but `InvokeMcpTool(...)` and protocol-facing work-tool calls still return an unauthorized result unless the caller independently satisfies the applicable queue permission.

```csharp
foreach (var tool in tools)
{
    string name = tool.Name;
    string inputSchemaJson = tool.InputSchemaJson;
    bool usesFallbackInputSchema = tool.UsesFallbackInputSchema;
    WorkDefinitionMetadata? metadata = tool.Metadata;
}
```

`WorkDefinition.Name` is the Workable name. The MCP server maps it to a protocol-safe tool name when the HTTP MCP endpoint is used.

## Schema Behavior

`WorkSchema` belongs to Workable first. Hosts can use it for custom UI generation, serialization, admin screens, and adapters. Generated schemas include a `$schema` value and expose the dialect on `WorkSchema.SchemaDialect`. Workable currently emits JSON Schema 2020-12 for typed input and output schemas.

Typed executors and typed delegate registrations generate schemas from their input and output CLR types using the same System.Text.Json web defaults Workable uses for input and output payloads. Explicit schemas can still be supplied on `WorkDefinition` when a host needs full control.

The MCP adapter uses a work schema directly when it has JSON content and a schema document. If a work definition does not provide a compatible input schema, the adapter can expose a permissive object schema instead. Output schemas are only exposed when the work definition provides a compatible JSON schema.

On the protocol-facing server surface, work-tool schemas are wrapped so clients can either send the raw work input directly or send `{ input, description }` when they want to attach an origin description. This wrapper behavior applies to `MapWorkableMcp()` and other server transport surfaces, not to the direct `IWorkSystemSession.InvokeMcpTool(...)` API.

```csharp
var tools = session.GetMcpToolDescriptors(
    new WorkableMcpToolCatalogOptions
    {
        IncludeDefinitionsWithoutJsonSchema = true,
        FallbackInputSchemaJson = """{"type":"object","additionalProperties":true}""",
    });
```

Set `IncludeDefinitionsWithoutJsonSchema` to `false` when only explicitly described input schemas should become MCP tools.
Set `FallbackInputSchemaJson` when definitions without a compatible JSON input schema should still become MCP tools, but with a host-chosen fallback schema instead of the default permissive object schema.

## Invocation

Use `InvokeMcpTool` on an `IWorkSystemSession` to queue work by Workable work name.

```csharp
IWorkSystemSession session = await workSystem.CreateSession(
    requestContext,
    cancellationToken);
using var input = JsonDocument.Parse("""{"userId":"user-123"}""");

WorkableMcpInvocationResult result =
    await session.InvokeMcpTool(
        "email.welcome.send",
        input.RootElement,
        cancellationToken: cancellationToken);
```

The direct session API uses the original Workable work name such as `email.welcome.send`. MCP-safe tool names like `workable_work_email_welcome_send` are only for the protocol-facing server surface.

By default, invocation waits for completion. The completed `WorkOutput` is returned when the caller also has Read permission for that definition; queue-only invocation preserves terminal status and safe messages but returns no output.

```csharp
if (result.IsCompletedSuccessfully)
{
    string? json = result.Output?.Json;
}
```

Long-running work can return after queue acceptance.

```csharp
var result = await session.InvokeMcpTool(
    "report.generate",
    input.RootElement,
    new WorkableMcpInvocationOptions
    {
        Completion = WorkableMcpInvocationCompletion.ReturnAfterAccepted,
    },
    cancellationToken);

WorkerId? workerId = result.WorkerId;
```

`WorkableMcpInvocationOptions` can also stamp queue-time `WorkerOptions` onto the invocation and set a bound on how long completion waits.

```csharp
var result = await session.InvokeMcpTool(
    "report.generate",
    input.RootElement,
    new WorkableMcpInvocationOptions
    {
        Completion = WorkableMcpInvocationCompletion.WaitForCompletion,
        WorkerOptions = new WorkerOptions(ProfilingEnabled: true),
        CompletionTimeout = TimeSpan.FromSeconds(30),
    },
    cancellationToken);
```

`WorkerOptions` behaves the same way it does for ordinary queue calls. Use it when MCP work tools should apply queue-time profiling or queue-time configuration overrides. `CompletionTimeout` is useful when the caller wants completion semantics but does not want to wait indefinitely for a long-running worker.

The accepted worker remains owned by Workable and can be queried, observed, or controlled through the normal Workable APIs.

## Server Options

The server can include or exclude work tools, query tools, and action tools. By default all three categories are enabled, but these host switches are upper bounds: they never advertise or authorize a tool the current caller lacks permission to use.

```csharp
builder.Services.AddWorkableMcpServer(options =>
{
    options.IncludeWorkTools = true;
    options.IncludeQueryTools = true;
    options.IncludeActionTools = true;
    options.ToolCatalog = new WorkableMcpToolCatalogOptions
    {
        IncludeDefinitionsWithoutJsonSchema = true,
        FallbackInputSchemaJson = """{"type":"object","additionalProperties":true}""",
    };
    options.Invocation = new WorkableMcpInvocationOptions
    {
        Completion = WorkableMcpInvocationCompletion.ReturnAfterAccepted,
        WorkerOptions = new WorkerOptions(ProfilingEnabled: true),
        CompletionTimeout = TimeSpan.FromSeconds(30),
    };
});
```

`ToolCatalog` controls how work definitions are projected into MCP work tools, including whether definitions without compatible JSON input schemas are included and what fallback input schema JSON they receive when they are. `Invocation` controls how MCP work tools return after queueing, the default `WorkerOptions` applied to those queued workers, and any completion timeout used when the server waits for completion. Visible query tools return the query result for the current request. Visible action tools return the worker action outcome, definition reconfiguration outcome, or workflow command/run payload for the requested operation. Direct calls to tools outside the caller-scoped list return the same not-found result as an unknown tool name.

The built-in system reports exact action categories to MCP. A custom `IWorkSystem` that wants the same fine-grained action-tool advertisement can also implement `IWorkOperationAccessSource`. Without that optional interface, MCP conservatively advertises actions only from system-wide Operate access; per-definition coarse Operate counts are not expanded into unrelated tools.

## Package Boundary

`Workable.Mcp` depends on `Workable` and the official .NET MCP ASP.NET Core package. The core `Workable`, `Workable.Abstractions`, and `Workable.Sdk` packages do not depend on an MCP SDK.

The descriptor and invocation APIs remain available for hosts that need a custom MCP transport, but the standard ASP.NET Core server is available through `AddWorkableMcpServer` and `MapWorkableMcp`.
