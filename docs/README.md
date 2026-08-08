# Workable Docs

This folder groups Workable documentation by the kind of question you are trying to answer.

## Why Workable

Workable is for applications that have background jobs, recurring tasks, operational work, or user-triggered actions that need more than "start a task and hope for the best."

- Define work once and expose it consistently through direct in-process calls, HTTP, or MCP when those adapters are enabled.
- Let feature libraries author work near the feature while the host application stays in control of runtime policy.
- Keep queued work observable and controllable with worker history, status, diagnostics, events, and operator actions.
- Attach runtime behavior such as recurrence, retry, idempotency, concurrency, retention, logging, and profiling without reinventing those concerns per feature.

Start with [Getting Started](guides/getting-started.md) if you are evaluating whether Workable fits your application architecture.

## Guides

- [Getting Started](guides/getting-started.md): package split, work author setup, host setup, queueing work, and the preferred ASP.NET Core dispatcher path for custom HTTP endpoints.
- [Implementing Work](guides/implementing-work.md): write executor code, use `IWorkExecutionContext`, and understand failure, cancellation, pause, and interruption behavior.
- [Iteration Status Streams](guides/iteration-status-streams.md): publish ordered per-iteration progress, subscribe in process or through SignalR, and resume safely with replay cursors.
- [Microsoft Entra Authentication](guides/entra-authentication.md): configure Entra bearer-token authentication and claim mapping for Workable surfaces.
- [Registration](guides/registration.md): define work in feature assemblies, generate definitions from sources, queue startup work, target named systems, and isolate catalogs.
- [Workflows](guides/workflows.md): register multi-step orchestrations that dispatch existing work definitions, fan out from typed outputs, run named parallel branches, and join on completion.
- [Queueing](guides/queueing.md): queue work by name, pass input, set queue options, and await completion.
- [Configuration](guides/configuration/README.md): configuration sources, override order, and runtime reconfiguration rules.
  - [Start Configuration](guides/configuration/start.md): automatic start behavior and when queue calls return.
  - [Idempotency Configuration](guides/configuration/idempotency.md): duplicate prevention by `WorkSubjectId`.
  - [Recurrence Configuration](guides/configuration/recurrence.md): repeated execution, iteration waits, and recurrence circuit behavior.
  - [Transient Retry Configuration](guides/configuration/transient-retry.md): transient exception classification and retry behavior.
  - [Failed-Worker Handling Configuration](guides/configuration/failed-worker.md): opt into auto-cancel for failed non-recurring workers and control runtime overrides.
  - [Logging Configuration](guides/configuration/logging.md): worker-scoped logging behavior.
  - [Retention Configuration](guides/configuration/retention.md): automatic purge timing and final-worker count cleanup.
  - [System Settings](guides/configuration/system-settings.md): startup-only limits for admission capacity, retained final workers, iteration status replay, payload size, and profiling.
  - [Concurrency Configuration](guides/configuration/concurrency.md): capacity limits by definition, subject, or concurrency key.
  - [Queue Durability Configuration](guides/configuration/queue-durability.md): durable queueing, persistence-backed idempotency, and durable completion.
  - [Invocation Configuration](guides/configuration/invocation.md): channels allowed to start a work definition.
  - [Configuration Interactions](guides/configuration/interactions.md): non-obvious behavior when configuration types are combined.

## Adapters

- [HTTP API](adapters/http-api.md): expose registered work through HTTP endpoints.
- [MCP](adapters/mcp.md): expose registered work, query tools, worker action tools, and definition-default reconfiguration through an MCP server.
- [Realtime](adapters/realtime.md): stream worker events, component-view updates, diagnostics, and admin event-viewer traffic through SignalR.

## Concepts

- [Core API Surface](concepts/core-api-surface.md): understand systems, queues, workers, actions, queries, events, and public contracts.
- [Authorization](concepts/authorization.md): security model, request-context sessions, access introspection, and how the adapters apply authorization.
- [Abstractions Surface](concepts/abstractions-surface.md): understand the consumer-facing `Workable.Abstractions` contract for systems, sessions, queueing, querying, events, diagnostics, and lifecycle.
- [Abstractions Extension Points](concepts/abstractions-extension-points.md): advanced public extension points for persistence, metrics, lifecycle, realtime capability, and group resolution.
- [Outcomes And Control](concepts/outcomes-and-control.md): understand queue outcomes, action outcomes, completions, optimistic concurrency, and bulk control semantics.
- [ASP.NET Core Integration](concepts/aspnetcore-integration.md): how Workable maps `HttpContext` into actors, origins, and authorization groups for custom endpoints and transports.
- [Project Structure](concepts/project-structure.md): source layout, package boundary, and namespace convention.
- [Lifecycle](concepts/lifecycle.md): queue acceptance, execution, shutdown behavior, and lifecycle diagrams.
- [Execution Engine](concepts/execution-engine.md): dispatcher, execution strategies, concurrency coordination, event stream, and retention behavior.
- [Querying](concepts/querying.md): build admin views, status summaries, definition browsers, and read-model-backed dashboards.
- [Views](concepts/views.md): understand the shared component-view contract used by HTTP and SignalR, and how to use it directly for a custom UI.
- [Observability](concepts/observability.md): subscribe to work events, filters, payloads, and buffered delivery behavior.
- [Diagnostics](concepts/diagnostics.md): understand queue rejection, read-model lag, retention lag, and system warning signals.
- [Profiling](concepts/profiling.md): capture per-iteration profile trees with required instrumentation identities, automatically time SQL and outbound HTTP dependencies, filter those sources in the admin UI, bound automatic growth, and temporarily bypass that bound by work type or actor from the admin UI or HTTP API.

## Package Docs

- [SQL Server Integration](../packages/extensions/sqlserver/README.md): runtime setup and schema deployment for `Workable.SqlServer`.
- [Sample Host](../apps/samples/Workable.SampleHost/README.md): runnable host that exposes HTTP, MCP, and SignalR adapters.
- [Admin UI](../apps/web/workable-admin-ui/README.md): local setup and security model for the Next.js admin UI.
- [Performance Harness](../apps/tools/Workable.PerformanceHarness/README.md): benchmark and load-harness usage for runtime and adapter performance work.
