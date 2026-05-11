# Workable

Workable helps .NET applications turn background jobs, operational tasks, recurring work, and user-triggered actions into first-class work the host can queue, observe, control, and configure.

Most applications eventually grow work that does not fit cleanly inside the immediate request or command that started it. Sending email, refreshing caches, synchronizing data, running maintenance tasks, retrying transient failures, and coordinating long-running operations all need more structure than "start a task and hope it finishes." Workable gives that work explicit identity, state, configuration, outcomes, and events.

Feature assemblies can declare their own work with `Workable.Sdk`. Libraries that need to use a hosted system can depend on `Workable.Abstractions` and accept `IWorkSystem` from the host. The host application owns the actual Workable systems, decides which work belongs in each system, and controls how workers start, retry, recur, respect concurrency, and stay available for inspection.

That split lets teams add work near the feature that needs it without forcing every feature library to know how the application hosts work. At runtime, the host gets a consistent surface for queueing work, awaiting completion, pausing, canceling, reconfiguring workers, and subscribing to work events.

## Documentation

### Using Workable

- [Getting Started](docs/getting-started.md): package split, work author setup, host setup, and queueing work.
- [Work Registration](docs/work-registration.md): define work in feature assemblies, generate definitions from sources, queue startup work, target named systems, and isolate catalogs.
- [Work Queueing](docs/work-queueing.md): queue work by name or id, pass input, set queue options, and await completion.
- [Work Configuration](docs/work-configuration.md): configuration sources, override order, and runtime reconfiguration rules.
  - [Start](docs/work-configuration-start.md): automatic start behavior and when queue calls return.
  - [Idempotency](docs/work-configuration-idempotency.md): duplicate prevention by `WorkSubjectId`.
  - [Recurrence](docs/work-configuration-recurrence.md): repeated execution and recurrence circuit behavior.
  - [Transient Retry](docs/work-configuration-transient-retry.md): transient exception classification and retry behavior.
  - [Logging](docs/work-configuration-logging.md): worker-scoped logging behavior.
  - [Retention](docs/work-configuration-retention.md): automatic purge timing for completed and canceled workers.
  - [Concurrency](docs/work-configuration-concurrency.md): capacity limits by definition, subject, or concurrency key.
  - [Invocation](docs/work-configuration-invocation.md): channels allowed to start a work definition.
  - [Interactions](docs/work-configuration-interactions.md): non-obvious behavior when configuration types are combined.
- [Work Querying](docs/work-querying.md): build admin views, status summaries, and definition browsers.
- [Work Observability](docs/work-observability.md): subscribe to work events.
- [Work Profiling](docs/work-profiling.md): capture per-worker execution profile trees.
- [Workable HTTP API](docs/work-http-api.md): expose registered work through HTTP endpoints.
- [Workable MCP](docs/work-mcp.md): expose registered work and read-only query tools through an MCP server.
- [Sample Host](samples/Workable.SampleHost/README.md): run HTTP and MCP adapters together in one ASP.NET Core app.

### Under The Hood

These docs explain what happens inside Workable after work is registered or queued.

- [Core API Surface](docs/core-api-surface.md): understand systems, queues, workers, actions, queries, events, and public contracts.
- [Project Structure](docs/project-structure.md): source layout, package boundary, and namespace convention.
- [Work Lifecycle](docs/work-lifecycle.md): queue acceptance, execution, worker handles, and lifecycle diagrams.
- [Execution Engine](docs/execution-engine.md): dispatcher, execution strategies, concurrency coordination, event stream, and retention behavior.
