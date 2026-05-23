# Workable

Workable is a .NET work orchestration library for applications that need more than "run this in the background." It turns background jobs, operational tasks, recurring work, and user-triggered actions into first-class work the host can queue, observe, control, and configure.

Most applications eventually grow work that does not fit cleanly inside the request, controller action, message handler, or command that started it. Sending email, refreshing caches, synchronizing data, running maintenance tasks, retrying transient failures, and coordinating long-running operations all need identity, state, cancellation, status, events, and a way to be found later. Workable gives that work a consistent runtime model instead of leaving each feature to invent its own.

Workable is useful when you want feature code to define work near the feature that needs it, while the host application keeps control of execution. Feature assemblies can declare their own work with `Workable.Sdk`. Libraries that need to use a hosted system can depend on `Workable.Abstractions` and accept `IWorkSystem` from the host. The host application owns the actual Workable systems, decides which work belongs in each system, and controls how workers start, retry, recur, respect concurrency, and stay available for inspection.

That split lets teams add work near the feature that needs it without forcing every feature library to know how the application hosts work. At runtime, the host gets a consistent surface for queueing work, awaiting completion, pausing, canceling, reconfiguring workers, and subscribing to work events.

Workable also gives applications a path to expose the same authored work through more than one channel. Direct .NET callers, HTTP endpoints, MCP clients, and realtime SignalR clients can all work against the same catalog while preserving request context, structured outcomes, worker history, and invocation rules.

## Why Use Workable?

- Define work once and invoke it through .NET, HTTP, or MCP when those channels are enabled.
- Keep feature libraries independent from the host runtime while still letting them contribute work.
- Queue fire-and-forget work without losing the ability to query, observe, cancel, pause, retry, or purge it.
- Give operators a real admin surface for work: live system and worker visibility, executable definitions, diagnostics, and control actions instead of one-off job screens and custom tooling.
- Attach runtime behavior such as recurrence, transient retry, idempotency, concurrency, retention, logging, profiling, initialization, and start policy.
- Use structured inputs, outputs, messages, worker snapshots, event payloads, and status summaries instead of ad hoc task tracking.
- Preserve who or what started work through request context and origin metadata for HTTP, MCP, SignalR, and direct .NET calls.

## Packages

### Core Packages

- `Workable.Sdk`: contracts and registration helpers for assemblies that author work.
- `Workable.Abstractions`: contracts for libraries that consume an already-hosted work system.
- `Workable`: in-process host and runtime for Workable systems.

### Optional Packages

- `Workable.SqlServer`: SQL Server persistence integration for durable queueing, persistence-backed idempotency, and persistence-backed concurrency.
- `Workable.AspNetCore`: ASP.NET Core request-context and authorization integration for custom endpoints and hosts.
- `Workable.Entra`: Microsoft Entra ID bearer-token validation and Workable authorization claim mapping for ASP.NET Core target apps.
- `Workable.Views`: shared component-view contracts and projections used by HTTP and SignalR adapters; most applications receive it transitively through `Workable.HttpApi` or `Workable.SignalR` instead of referencing it directly.
- `Workable.HttpApi`: standard HTTP endpoints for queueing, querying, and controlling work.
- `Workable.Mcp`: MCP server adapter for authored work, query tools, and worker action tools.
- `Workable.SignalR`: realtime worker events and component-view updates for ASP.NET Core clients.

### Apps And Tools

- `samples/Workable.SampleHost`: runnable ASP.NET Core sample app with HTTP API, MCP, SignalR, fake-auth profiles, and SQL Server LocalDB durability scenarios.
- `src/Workable.PerformanceHarness`: opt-in scenario runner and BenchmarkDotNet harness for runtime, query, view, realtime, and SQL durability performance work.
- `integrations/sqlserver/tools/Workable.SqlServer.Cli`: SQL Server schema generation and deployment CLI for Workable persistence.
- `src/workable-admin-ui`: Next.js admin UI for inspecting and operating Workable systems through the HTTP API and SignalR realtime updates.

## Documentation

Start with the docs landing page: [Workable Docs](https://github.com/aliendomain/workable/blob/main/docs/README.md).

Recommended entry points:

- [Getting Started](https://github.com/aliendomain/workable/blob/main/docs/guides/getting-started.md) if you are evaluating or integrating Workable.
- [Registration](https://github.com/aliendomain/workable/blob/main/docs/guides/registration.md) if you are authoring work in feature assemblies.
- [Queueing](https://github.com/aliendomain/workable/blob/main/docs/guides/queueing.md) if you already have work definitions and want to invoke them.
- [Configuration](https://github.com/aliendomain/workable/blob/main/docs/guides/configuration/README.md) if you are tuning start behavior, retry, recurrence, concurrency, durability, logging, retention, or invocation rules.
- [HTTP API](https://github.com/aliendomain/workable/blob/main/docs/adapters/http-api.md), [MCP](https://github.com/aliendomain/workable/blob/main/docs/adapters/mcp.md), and [Realtime](https://github.com/aliendomain/workable/blob/main/docs/adapters/realtime.md) if you are exposing Workable over transports.
- [Abstractions Surface](https://github.com/aliendomain/workable/blob/main/docs/concepts/abstractions-surface.md) if you are consuming a hosted system from another library.
- [Workable SQL Server Integration](https://github.com/aliendomain/workable/blob/main/integrations/sqlserver/README.md) if you need durable queueing or persistence-backed coordination.
- [Sample Host](https://github.com/aliendomain/workable/blob/main/samples/Workable.SampleHost/README.md) if you want a runnable reference app.
- [Admin UI](https://github.com/aliendomain/workable/blob/main/src/workable-admin-ui/README.md) if you want the browser-based operator surface.
- [Performance Harness](https://github.com/aliendomain/workable/blob/main/src/Workable.PerformanceHarness/README.md) if you are measuring runtime or adapter performance.
