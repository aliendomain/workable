# Workable MCP

Workable can expose authored work definitions, work-system query tools, and worker action tools through the `Workable.Mcp` adapter package.

The adapter does not change how work is authored or executed. It projects an `IWorkSystem` catalog into tool descriptors and invokes tools by queueing work through Workable.

MCP exposure is opt-in. A work definition must allow `WorkInvocationChannel.Mcp` to appear as an MCP tool or be invoked through the MCP adapter.

The ASP.NET Core MCP endpoint is an authenticated transport. Anonymous callers are rejected before the MCP request handler runs, and mapped systems must be authorization-enabled.

Each ASP.NET Core MCP request creates a `WorkRequestContext` and an `IWorkSystemSession` for the selected system. Work-definition read access filters tool discovery and query results. Work-definition operate access controls work tools and worker action tools.

## Server Setup

`Workable.Mcp` includes an ASP.NET Core MCP server integration. Add it to the same host application that registers Workable systems.

```csharp
var builder = WebApplication.CreateBuilder(args);

builder.Services.AddWorkableSystem(workable =>
{
    workable.StartWithHost();
});

builder.Services.AddWorkableMcpServer();

var app = builder.Build();

app.MapWorkableMcp();

await app.RunAsync();
```

The default MCP endpoint is `/workable/mcp`.

The mapped endpoint uses the same Workable ASP.NET Core authentication behavior as the other HTTP-based adapters. If the host configures `WorkableAspNetCoreAuthorizationOptions.TransportAuthenticationScheme`, the MCP endpoint authenticates against that explicit scheme instead of the ambient default.

Unlike the HTTP API and SignalR adapters, `MapWorkableMcp()` does not add ASP.NET Core authorization metadata to the mapped endpoint. It does not need `app.UseAuthorization()` just to make the Workable MCP endpoint function.

`MapWorkableMcp` targets the default system unless a system name is supplied. Map a separate MCP endpoint for each named system that should be exposed.

```csharp
app.MapWorkableMcp();                                      // default system at /workable/mcp
app.MapWorkableMcp("/workable/systems/email/mcp", "email"); // named system
```

An MCP client connected to `/workable/systems/email/mcp` only sees tools for the `email` Workable system.

The MCP server exposes three kinds of tools:

- Work tools queue work definitions that allow `WorkInvocationChannel.Mcp`.
- Query tools inspect worker status, worker snapshots, work definitions, work info, work keys, and status summaries.
- Action tools start, pause, cancel, push, and purge existing workers, and can reconfigure work definition defaults.

Work tools use MCP-safe names. For example, a Workable work definition named `email.welcome.send` is exposed as `workable_work_email_welcome_send`.

If multiple work definitions normalize to the same MCP-safe name, the server disambiguates them by appending a short definition-id suffix.

Work queued through the ASP.NET Core MCP server records a `WorkOrigin` with `WorkInvocationChannel.Mcp`. When an HTTP context is available, the origin uses `HttpContext.User` for actor identity and records the MCP request path as the origin URL. Protocol-facing work tools, action tools, and reconfiguration tools can also carry an optional caller-supplied `description`, which Workable copies into the origin.

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

Query tools are exposed by default so an MCP client can inspect what is happening in the work system after it starts work.

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

These tools use the same query engine as the .NET API. They do not mutate workers. Worker queries can filter by selected configuration flags with `recurrenceEnabled`, `concurrencyEnabled`, and `profilingEnabled`.

Use `workable_query_worker_key_types` when the user asks broadly for workers tied to a relationship type, such as claim work or customer work. It groups by key type across subjects, concurrency keys, and identifiers, and supports pagination. Use `workable_query_worker_keys` when the user gives a specific relationship phrase, such as claim id CLM-123. Both tools can filter the returned workers by state and return matching `WorkerOverviewItem` rows, so the MCP client can inspect worker ids, states, definitions, revisions, and categories directly from the key search result.

Use `workable_query_work_iteration_key_types` and `workable_query_work_iteration_keys` when the user asks about actual executions tied to a relationship, such as failed claim work or completed customer work. These tools filter by iteration completion status and return `WorkerIterationOverviewItem` rows.

Use `workable_query_worker_iterations` when the user asks about execution history, recent failures, retry attempts, or recurring activity. It can filter by worker id, work name, category, completion status, subject, concurrency key, identifier, and time range. Use `workable_get_worker_iteration` when the client already has a worker id and iteration sequence and needs the full iteration output, messages, logs, or profile.

## Action Tools

Action tools are exposed by default so an MCP client can control workers after it has inspected them. Each action requires a `workerId` and the current `revision` from `workable_get_worker` or `workable_query_workers`.

- `workable_start_worker`: start or retry a queued, paused, or failed worker.
- `workable_pause_worker`: request a cooperative pause for a running worker or a recurring worker that is waiting.
- `workable_cancel_worker`: permanently stop a non-final worker.
- `workable_push_worker`: skip the current recurrence wait and begin the next iteration immediately.
- `workable_purge_worker`: permanently remove a completed or canceled worker from memory.
- `workable_reconfigure_work_definition`: change a work definition's default worker options and/or default runtime configuration for future queued workers.

Worker action tool calls record a `WorkOrigin` with `WorkInvocationChannel.Mcp`. The origin is retained in the worker's action history and is published on the action event. Action tools accept an optional top-level `description` that is copied into that origin. Definition default reconfiguration requires the current definition name and revision from `workable_query_work_definitions` or `workable_get_work_info`, and `workable_reconfigure_work_definition` also accepts an optional top-level `description`.

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
IWorkSystemSession session = workSystem.CreateSession(requestContext);

IReadOnlyList<WorkableMcpToolDescriptor> tools =
    session.GetMcpToolDescriptors();
```

Each descriptor includes the work name, description, category, input schema, output schema, schema content types, fallback-schema usage, and definition metadata. Definitions that do not allow `WorkInvocationChannel.Mcp` are omitted.

Definitions the caller cannot read are also omitted from the descriptor list.

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
IWorkSystemSession session = workSystem.CreateSession(requestContext);
using var input = JsonDocument.Parse("""{"userId":"user-123"}""");

WorkableMcpInvocationResult result =
    await session.InvokeMcpTool(
        "email.welcome.send",
        input.RootElement,
        cancellationToken: cancellationToken);
```

The direct session API uses the original Workable work name such as `email.welcome.send`. MCP-safe tool names like `workable_work_email_welcome_send` are only for the protocol-facing server surface.

By default, invocation waits for completion and returns the completed `WorkOutput`.

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

The server can include or exclude work tools, query tools, and action tools. By default all three are enabled.

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

`ToolCatalog` controls how work definitions are projected into MCP work tools, including whether definitions without compatible JSON input schemas are included and what fallback input schema JSON they receive when they are. `Invocation` controls how MCP work tools return after queueing, the default `WorkerOptions` applied to those queued workers, and any completion timeout used when the server waits for completion. Query tools always return the query result for the current request. Action tools return the `WorkActionOutcome` for the requested worker action, or the definition reconfiguration outcome for `workable_reconfigure_work_definition`.

## Package Boundary

`Workable.Mcp` depends on `Workable` and the official .NET MCP ASP.NET Core package. The core `Workable`, `Workable.Abstractions`, and `Workable.Sdk` packages do not depend on an MCP SDK.

The descriptor and invocation APIs remain available for hosts that need a custom MCP transport, but the standard ASP.NET Core server is available through `AddWorkableMcpServer` and `MapWorkableMcp`.
