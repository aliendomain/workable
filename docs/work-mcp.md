# Workable MCP

Workable can expose authored work definitions, work-system query tools, and worker action tools through the `Workable.Mcp` adapter package.

The adapter does not change how work is authored or executed. It projects an `IWorkSystem` catalog into tool descriptors and invokes tools by queueing work through Workable.

MCP exposure is opt-in. A work definition must allow `WorkInvocationChannel.Mcp` to appear as an MCP tool or be invoked through the MCP adapter.

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

app.MapWorkableMcp("/mcp");

await app.RunAsync();
```

`MapWorkableMcp` targets the default system unless a system name is supplied. Map a separate MCP endpoint for each named system that should be exposed.

```csharp
app.MapWorkableMcp("/mcp");                  // default system
app.MapWorkableMcp("/mcp/email", "email");   // named system
```

An MCP client connected to `/mcp/email` only sees tools for the `email` Workable system.

The MCP server exposes three kinds of tools:

- Work tools queue work definitions that allow `WorkInvocationChannel.Mcp`.
- Query tools inspect worker status, worker snapshots, work definitions, work info, and status summaries.
- Action tools start, pause, cancel, push, and purge existing workers.

Work tools use MCP-safe names. For example, a Workable work definition named `email.welcome.send` is exposed as `workable_work_email_welcome_send`.

Work queued through the ASP.NET Core MCP server records a `WorkOrigin` with `WorkInvocationChannel.Mcp`. When an HTTP context is available, the origin uses `HttpContext.User` for actor identity and records the MCP request path as the origin URL.

## Query Tools

Query tools are exposed by default so an MCP client can inspect what is happening in the work system after it starts work.

- `workable_query_workers`
- `workable_get_worker`
- `workable_get_work_info`
- `workable_query_work_definitions`
- `workable_get_worker_status_summary`

These tools use the same query engine as the .NET API. They do not mutate workers.

## Action Tools

Action tools are exposed by default so an MCP client can control workers after it has inspected them. Each action requires a `workerId` and the current `revision` from `workable_get_worker` or `workable_query_workers`.

- `workable_start_worker`: start or retry a queued, paused, or failed worker.
- `workable_pause_worker`: request a cooperative pause for a running worker or a recurring worker that is waiting.
- `workable_cancel_worker`: permanently stop a non-final worker.
- `workable_push_worker`: skip the current recurrence wait and begin the next iteration immediately.
- `workable_purge_worker`: permanently remove a completed or canceled worker from memory.

Action tool calls record a `WorkOrigin` with `WorkInvocationChannel.Mcp`. The origin is retained in the worker's action history and is published on the action event.

## Tool Descriptors

Use `GetMcpToolDescriptors` to describe the work available from one Workable system.

```csharp
IReadOnlyList<WorkableMcpToolDescriptor> tools =
    workSystem.GetMcpToolDescriptors();
```

Each descriptor includes the work name, description, category, definition id, input schema, output schema, and definition metadata. Definitions that do not allow `WorkInvocationChannel.Mcp` are omitted.

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

`WorkSchema` belongs to Workable first. Hosts can use it for custom UI generation, serialization, admin screens, and adapters.

Typed executors and typed delegate registrations generate schemas from their input and output CLR types. Explicit schemas can still be supplied on `WorkDefinition` when a host needs full control.

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
