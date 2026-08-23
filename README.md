# Workable

Workable is a .NET work orchestration library for applications that need more than "run this in the background." It turns background jobs, operational tasks, recurring work, and user-triggered actions into first-class work the host can queue, observe, control, and configure.

Most applications eventually grow work that does not fit cleanly inside the request, controller action, message handler, or command that started it. Sending email, refreshing caches, synchronizing data, running maintenance tasks, retrying transient failures, and coordinating long-running operations all need identity, state, cancellation, status, events, and a way to be found later. Workable gives that work a consistent runtime model instead of leaving each feature to invent its own.

Workable is useful when you want feature code to define work near the feature that needs it, while the host application keeps control of execution. Feature assemblies can declare their own work with `Workable.Sdk`. Libraries that need to use a hosted system can depend on `Workable.Abstractions` and accept `IWorkSystem` from the host. The host application owns the actual Workable systems, decides which work belongs in each system, and controls how workers start, retry, recur, respect concurrency, and stay available for inspection.

That split lets teams add work near the feature that needs it without forcing every feature library to know how the application hosts work. At runtime, the host gets a consistent surface for queueing work, awaiting completion, pausing, canceling, reconfiguring workers, and subscribing to work events.

Workable also gives applications a path to expose the same authored work through more than one channel. Direct .NET callers, HTTP endpoints, MCP clients, and realtime SignalR clients can all work against the same catalog while preserving request context, structured outcomes, worker history, and invocation rules.

## Why Use Workable?

- Define work once and invoke it through .NET, HTTP, or MCP when those channels are enabled.
- Register workflow definitions that coordinate existing work through dispatch, typed fan-out, parallel branches, and join steps inside the host runtime.
- Keep feature libraries independent from the host runtime while still letting them contribute work.
- Queue fire-and-forget work without losing the ability to query, observe, cancel, pause, retry, or purge it.
- Give operators a real admin surface for work: live system and worker visibility, executable definitions, diagnostics, and control actions instead of one-off job screens and custom tooling.
- Attach runtime behavior such as recurrence, transient retry, failed-worker handling, idempotency, concurrency, durability, retention, logging, profiling, initialization, invocation policy, and start policy.
- Persist short-lived iteration logs and profiles so developers and agents can inspect the work they just ran and answer questions such as how many SQL commands or HTTP requests it executed.
- Use structured inputs, outputs, messages, worker snapshots, event payloads, and status summaries instead of ad hoc task tracking.
- Preserve who or what started work through request context and origin metadata for HTTP, MCP, SignalR, and direct .NET calls.

## Packages

### Core Packages

- `Workable.Sdk`: contracts and registration helpers for assemblies that author work.
- `Workable.Abstractions`: contracts for libraries that consume an already-hosted work system.
- `Workable`: in-process host and runtime for Workable systems.

### Optional Packages

- `Workable.SqlServer`: SQL Server persistence integration for durable queueing and completion, durable workflows, persistence-backed idempotency and concurrency, and expiring execution diagnostics.
- `Workable.AspNetCore`: ASP.NET Core request-context and authorization integration for custom endpoints and hosts.
- `Workable.Entra`: Workable actor and authorization-claim integration for ASP.NET Core hosts that already authenticate Microsoft Entra identities.
- `Workable.Views`: shared component-view contracts and projections used by HTTP and SignalR adapters; most applications receive it transitively through `Workable.HttpApi` or `Workable.SignalR` instead of referencing it directly.
- `Workable.HttpApi`: standard HTTP endpoints for queueing, querying, and controlling workers and workflow runs.
- `Workable.Mcp`: MCP server adapter for authored work, worker and workflow queries, and worker and workflow actions.
- `Workable.SignalR`: realtime worker collections, worker and workflow events, worker details, and component-view updates for ASP.NET Core clients.

### Apps And Tools

- `apps/samples/Workable.SampleHost`: runnable ASP.NET Core sample app with HTTP API, MCP, SignalR, fake-auth profiles, and SQL Server LocalDB durability scenarios.
- `apps/tools/Workable.PerformanceHarness`: opt-in scenario runner and BenchmarkDotNet harness for runtime, query, view, realtime, and SQL durability performance work.
- `apps/tools/Workable.SqlServer.Cli`: SQL Server schema generation and deployment CLI for Workable persistence.
- `apps/web/workable-admin-ui`: Next.js admin UI for inspecting and operating Workable systems through the HTTP API and SignalR realtime updates.

## Documentation

Start with the docs landing page: [Workable Docs](https://github.com/aliendomain/workable/blob/main/docs/README.md).

Recommended entry points:

- [Getting Started](https://github.com/aliendomain/workable/blob/main/docs/guides/getting-started.md) if you are evaluating or integrating Workable.
- [Registration](https://github.com/aliendomain/workable/blob/main/docs/guides/registration.md) if you are authoring work in feature assemblies.
- [Workflows](https://github.com/aliendomain/workable/blob/main/docs/guides/workflows.md) if you want to author multi-step orchestrations from existing work definitions.
- [Implementation](https://github.com/aliendomain/workable/blob/main/docs/guides/implementing-work.md) if you want to implement work classes and understand what executor code can do at runtime.
- [Queueing](https://github.com/aliendomain/workable/blob/main/docs/guides/queueing.md) if you already have work definitions and want to invoke them.
- [Configuration](https://github.com/aliendomain/workable/blob/main/docs/guides/configuration/README.md) if you are tuning start behavior, retry, recurrence, failed-worker handling, concurrency, durability, logging, retention, or invocation rules.
- [Persistent Execution Diagnostics](https://github.com/aliendomain/workable/blob/main/docs/guides/configuration/execution-diagnostics-persistence.md) if a developer or agent needs expiring iteration logs, profiles, or SQL/HTTP operation counts from recently executed work.
- [HTTP API](https://github.com/aliendomain/workable/blob/main/docs/adapters/http-api.md), [MCP](https://github.com/aliendomain/workable/blob/main/docs/adapters/mcp.md), and [Realtime](https://github.com/aliendomain/workable/blob/main/docs/adapters/realtime.md) if you are exposing Workable over transports.
- [Microsoft Entra Authentication](https://github.com/aliendomain/workable/blob/main/docs/guides/entra-authentication.md) if the host already authenticates Entra identities and Workable should interpret their actor and group claims without taking ownership of JWT configuration.
- [Abstractions Surface](https://github.com/aliendomain/workable/blob/main/docs/concepts/abstractions-surface.md) if you are consuming a hosted system from another library.
- [Workable SQL Server Integration](https://github.com/aliendomain/workable/blob/main/packages/extensions/sqlserver/README.md) if you need durable queueing, durable workflows, persistence-backed coordination, or execution-diagnostics storage.
- [Sample Host](https://github.com/aliendomain/workable/blob/main/apps/samples/Workable.SampleHost/README.md) if you want a runnable reference app.
- [Admin UI](https://github.com/aliendomain/workable/blob/main/apps/web/workable-admin-ui/README.md) if you want the browser-based operator surface.
- [Performance Harness](https://github.com/aliendomain/workable/blob/main/apps/tools/Workable.PerformanceHarness/README.md) if you are measuring runtime or adapter performance.
