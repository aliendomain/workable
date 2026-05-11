# Project Structure

Workable is split into source projects so feature assemblies can define work without depending on the in-process host runtime.

## Projects

`src/Workable.Sdk` contains the work-authoring contracts and primitives:

- work definitions and metadata
- work input, output, schema, and identifiers
- work configuration attributes and builders
- `IWorkExecutor`
- `IWorkExecutionContext`
- `WorkExecutionResult`
- `AddWorkableWork`

Feature libraries reference `Workable.Sdk` when they need to declare work, configure work defaults, or implement executors.

`src/Workable.Abstractions` contains the consumer-facing contracts for using an already-hosted system:

- `IWorkSystem`
- `IWorkSystemRegistry`
- `IWorkCatalog`
- `IWorkQueue`
- `IWorkerHandle`
- `IWorkerOperations`
- `IWorkQuery`
- `IWorkEventStream`
- worker snapshots, query results, action outcomes, queue outcomes, completions, and event records

Libraries reference `Workable.Abstractions` when they need to queue, query, observe, or control work but do not host Workable themselves.

`src/Workable` contains the host/runtime surface:

- in-memory system implementation
- worker lifecycle, queueing, dispatch, retention, concurrency, and event-stream behavior
- `AddWorkableSystem`

Host applications reference `Workable` when they need to create systems and run the in-process runtime. `Workable` also references the abstractions package, so host applications can inject and use `IWorkSystem` directly.

`src/Workable.HttpApi` contains the HTTP API adapter surface:

- endpoint mapping for Workable definitions and work invocation
- HTTP invocation through `IWorkQueue`
- invocation channel enforcement for `WorkInvocationChannel.HttpApi`

Applications reference `Workable.HttpApi` when they want to expose Workable systems through HTTP endpoints.

`src/Workable.AspNetCore` contains ASP.NET Core integration that does not expose routes:

- HTTP-context origin provider for direct .NET queueing and worker operations
- `AddWorkableAspNetCoreOrigins`

ASP.NET Core applications reference `Workable.AspNetCore` when their own controllers or minimal API routes queue work and those direct .NET calls should record actor information from `HttpContext.User`.

`src/Workable.Mcp` contains the MCP adapter surface:

- MCP-style tool descriptors for work definitions
- work invocation through `IWorkQueue`
- query tools for worker status, worker snapshots, work definitions, work info, and status summaries
- action tools for start, pause, cancel, push, and purge
- schema compatibility handling for MCP tool input
- invocation channel enforcement for `WorkInvocationChannel.Mcp`

Applications reference `Workable.Mcp` when they want to expose Workable systems through an MCP transport.

`tests/Workable.Tests` contains the contract, configuration, lifecycle, queueing, event stream, and in-memory runtime tests.

## Namespace

All packages expose the public namespace `Workable`. The assembly split is a packaging boundary, not a namespace fork.

Source folders organize implementation concerns. Folder names do not define public namespace segments.

## Dependency Direction

`Workable.Abstractions` references `Workable.Sdk`.

`Workable` references `Workable.Abstractions`.

`Workable.AspNetCore` references `Workable.Abstractions`.

`Workable.HttpApi` references `Workable` and `Workable.AspNetCore`.

`Workable.Mcp` references `Workable`.

Feature libraries reference `Workable.Sdk`.

Non-host libraries that use Workable reference `Workable.Abstractions`.

Host applications reference `Workable`.

Feature libraries do not need to know which runtime the host will use.
