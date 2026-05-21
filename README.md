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
- Attach runtime behavior such as recurrence, transient retry, idempotency, concurrency, retention, logging, profiling, initialization, and start policy.
- Use structured inputs, outputs, messages, worker snapshots, event payloads, and status summaries instead of ad hoc task tracking.
- Preserve who or what started work through request context and origin metadata for HTTP, MCP, SignalR, and direct .NET calls.

## Packages

- `Workable.Sdk`: contracts and registration helpers for assemblies that author work.
- `Workable.Abstractions`: contracts for libraries that consume an already-hosted work system.
- `Workable`: in-process host and runtime for Workable systems.
- `Workable.AspNetCore`: ASP.NET Core request-context and authorization integration for custom endpoints and hosts.
- `Workable.Views`: shared component-view contracts and projections used by HTTP and SignalR adapters.
- `Workable.HttpApi`: standard HTTP endpoints for queueing, querying, and controlling work.
- `Workable.Mcp`: MCP server adapter for authored work, query tools, and worker action tools.
- `Workable.SignalR`: realtime worker events and component-view updates for ASP.NET Core clients.

## Documentation

### Using Workable

- [Getting Started](https://github.com/aliendomain/workable/blob/main/docs/getting-started.md): package split, work author setup, host setup, and queueing work.
- [Work Registration](https://github.com/aliendomain/workable/blob/main/docs/work-registration.md): define work in feature assemblies, generate definitions from sources, queue startup work, target named systems, and isolate catalogs.
- [Work Queueing](https://github.com/aliendomain/workable/blob/main/docs/work-queueing.md): queue work by name or id, pass input, set queue options, and await completion.
- [Work Authorization](https://github.com/aliendomain/workable/blob/main/docs/work-authorization.md): security model, work and system authorization, ASP.NET Core integration, and adapter behavior.
- [Work Configuration](https://github.com/aliendomain/workable/blob/main/docs/work-configuration.md): configuration sources, override order, and runtime reconfiguration rules.
  - [Start](https://github.com/aliendomain/workable/blob/main/docs/work-configuration-start.md): automatic start behavior and when queue calls return.
  - [Idempotency](https://github.com/aliendomain/workable/blob/main/docs/work-configuration-idempotency.md): duplicate prevention by `WorkSubjectId`.
  - [Recurrence](https://github.com/aliendomain/workable/blob/main/docs/work-configuration-recurrence.md): repeated execution and recurrence circuit behavior.
  - [Transient Retry](https://github.com/aliendomain/workable/blob/main/docs/work-configuration-transient-retry.md): transient exception classification and retry behavior.
  - [Logging](https://github.com/aliendomain/workable/blob/main/docs/work-configuration-logging.md): worker-scoped logging behavior.
  - [Retention](https://github.com/aliendomain/workable/blob/main/docs/work-configuration-retention.md): automatic purge timing for completed and canceled workers.
  - [Concurrency](https://github.com/aliendomain/workable/blob/main/docs/work-configuration-concurrency.md): capacity limits by definition, subject, or concurrency key.
  - [Queue Durability](https://github.com/aliendomain/workable/blob/main/docs/work-configuration-queue-durability.md): persist accepted queue requests and replay interrupted durable work.
  - [Invocation](https://github.com/aliendomain/workable/blob/main/docs/work-configuration-invocation.md): channels allowed to start a work definition.
  - [Interactions](https://github.com/aliendomain/workable/blob/main/docs/work-configuration-interactions.md): non-obvious behavior when configuration types are combined.
- [Work Querying](https://github.com/aliendomain/workable/blob/main/docs/work-querying.md): build admin views, status summaries, and definition browsers.
- [Work Observability](https://github.com/aliendomain/workable/blob/main/docs/work-observability.md): subscribe to work events.
- [Work Diagnostics](https://github.com/aliendomain/workable/blob/main/docs/work-diagnostics.md): understand queue rejection, read-model lag, retention lag, and system warning signals.
- [Work Profiling](https://github.com/aliendomain/workable/blob/main/docs/work-profiling.md): capture per-worker execution profile trees.
- [Workable HTTP API](https://github.com/aliendomain/workable/blob/main/docs/work-http-api.md): expose registered work through HTTP endpoints.
- [Workable MCP](https://github.com/aliendomain/workable/blob/main/docs/work-mcp.md): expose registered work and read-only query tools through an MCP server.
- [Workable Realtime](https://github.com/aliendomain/workable/blob/main/docs/work-realtime.md): stream worker events, component-view updates, diagnostics, and admin event-viewer traffic through SignalR.
- [Sample Host](https://github.com/aliendomain/workable/blob/main/samples/Workable.SampleHost/README.md): run HTTP and MCP adapters together in one ASP.NET Core app.

### Under The Hood

These docs explain what happens inside Workable after work is registered or queued.

- [Core API Surface](https://github.com/aliendomain/workable/blob/main/docs/core-api-surface.md): understand systems, queues, workers, actions, queries, events, and public contracts.
- [Work Authorization](https://github.com/aliendomain/workable/blob/main/docs/work-authorization.md): security model, request-context sessions, and how the adapters apply authorization.
- [Project Structure](https://github.com/aliendomain/workable/blob/main/docs/project-structure.md): source layout, package boundary, and namespace convention.
- [Work Lifecycle](https://github.com/aliendomain/workable/blob/main/docs/work-lifecycle.md): queue acceptance, execution, worker handles, and lifecycle diagrams.
- [Execution Engine](https://github.com/aliendomain/workable/blob/main/docs/execution-engine.md): dispatcher, execution strategies, concurrency coordination, event stream, and retention behavior.
- [Work Diagnostics](https://github.com/aliendomain/workable/blob/main/docs/work-diagnostics.md): runtime diagnostics, warning thresholds, and admin UI notification behavior.
