# Workable MCP

Workable can expose authored work definitions, work-system query tools, and worker action tools through the `Workable.Mcp` adapter package.

The adapter does not change how work is authored or executed. It projects an `IWorkSystem` catalog into tool descriptors and invokes tools by queueing work through Workable.

MCP exposure is opt-in. A work definition must allow `WorkInvocationChannel.Mcp` to appear as an MCP tool or be invoked through the MCP adapter.

`Workable.Mcp` is an authenticated transport. Anonymous callers are rejected before the MCP request handler runs, and mapped systems must have `RequireAuthorization(true)`.

Each MCP request creates a `WorkRequestContext` and an `IWorkSystemSession` for the selected system. Work-definition read access filters tool discovery and query results. Work-definition operate access controls work tools and worker action tools.

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

Work queued through the ASP.NET Core MCP server records a `WorkOrigin` with `WorkInvocationChannel.Mcp`. When an HTTP context is available, the origin uses `HttpContext.User` for actor identity and records the MCP request path as the origin URL.

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

Worker action tool calls record a `WorkOrigin` with `WorkInvocationChannel.Mcp`. The origin is retained in the worker's action history and is published on the action event. Definition default reconfiguration requires the current definition id and revision from `workable_query_work_definitions` or `workable_get_work_info`.

## Tool Descriptors

Use `GetMcpToolDescriptors` to describe the work available from one Workable system.

```csharp
IReadOnlyList<WorkableMcpToolDescriptor> tools =
    workSystem.GetMcpToolDescriptors();
```

Each descriptor includes the work name, description, category, definition id, input schema, output schema, and definition metadata. Definitions that do not allow `WorkInvocationChannel.Mcp` are omitted.

Definitions the caller cannot read are also omitted from the descriptor list.

```csharp
foreach (var tool in tools)
{
    string name = tool.Name;
    string inputSchemaJson = tool.InputSchemaJson;
    WorkDefinitionMetadata? metadata = tool.Metadata;
}
```

`WorkDefinition.Name` is the Workable name. The MCP server maps it to a protocol-safe tool name when the HTTP MCP endpoint is used.

## Schema Behavior

`WorkSchema` belongs to Workable first. Hosts can use it for custom UI generation, serialization, admin screens, and adapters. Generated schemas include a `$schema` value and expose the dialect on `WorkSchema.SchemaDialect`. Workable currently emits JSON Schema 2020-12 for typed input and output schemas.

Typed executors and typed delegate registrations generate schemas from their input and output CLR types using the same System.Text.Json web defaults Workable uses for input and output payloads. Explicit schemas can still be supplied on `WorkDefinition` when a host needs full control.

The MCP adapter uses a work schema directly when it has JSON content and a schema document. If a work definition does not provide a compatible input schema, the adapter can expose a permissive object schema.

```csharp
var tools = workSystem.GetMcpToolDescriptors(
    new WorkableMcpToolCatalogOptions
    {
        IncludeDefinitionsWithoutJsonSchema = true,
        FallbackInputSchemaJson = """{"type":"object","additionalProperties":true}""",
    });
```

Set `IncludeDefinitionsWithoutJsonSchema` to `false` when only explicitly described input schemas should become MCP tools.

## Invocation

Use `InvokeMcpTool` to queue work by tool name.

```csharp
using var input = JsonDocument.Parse("""{"userId":"user-123"}""");

WorkableMcpInvocationResult result =
    await workSystem.InvokeMcpTool(
        "email.welcome.send",
        input.RootElement,
        cancellationToken: cancellationToken);
```

By default, invocation waits for completion and returns the completed `WorkOutput`.

```csharp
if (result.IsCompletedSuccessfully)
{
    string? json = result.Output?.Json;
}
```

Long-running work can return after queue acceptance.

```csharp
var result = await workSystem.InvokeMcpTool(
    "report.generate",
    input.RootElement,
    new WorkableMcpInvocationOptions
    {
        Completion = WorkableMcpInvocationCompletion.ReturnAfterAccepted,
    },
    cancellationToken);

WorkerId? workerId = result.WorkerId;
```

The accepted worker remains owned by Workable and can be queried, observed, or controlled through the normal Workable APIs.

## Server Options

The server can include or exclude work tools, query tools, and action tools.

```csharp
builder.Services.AddWorkableMcpServer(options =>
{
    options.IncludeWorkTools = true;
    options.IncludeQueryTools = true;
    options.IncludeActionTools = true;
    options.Invocation = new WorkableMcpInvocationOptions
    {
        Completion = WorkableMcpInvocationCompletion.ReturnAfterAccepted,
    };
});
```

`Invocation` controls how MCP work tools return after queueing. Query tools always return the query result for the current request. Action tools return the `WorkActionOutcome` for the requested worker action.

## Package Boundary

`Workable.Mcp` depends on `Workable` and the official .NET MCP ASP.NET Core package. The core `Workable`, `Workable.Abstractions`, and `Workable.Sdk` packages do not depend on an MCP SDK.

The descriptor and invocation APIs remain available for hosts that need a custom MCP transport, but the standard ASP.NET Core server is available through `AddWorkableMcpServer` and `MapWorkableMcp`.
