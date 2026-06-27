# Project Structure

Workable is split across shipping packages, runnable apps, and tests so feature assemblies can define work without depending on the in-process host runtime.

## Repo Roots

`packages` contains the shipping .NET packages. `packages/core` holds the main Workable product surface, and `packages/extensions` holds optional provider-specific packages such as SQL Server.

`apps` contains runnable surfaces. `apps/web` holds browser applications, `apps/tools` holds operational and benchmarking tools, and `apps/samples` holds runnable reference hosts.

`tests` contains the primary automated test suite. `tests/extensions` groups provider-specific verification beside the shared core test suite.

`docs` contains product and architecture documentation.

`.github` contains repository automation and workflow configuration.

Generated or local-run folders such as `bin`, `obj`, `TestResults`, `artifacts`, and `logs` are not part of the source structure and should not drive folder conventions.

## Projects

`packages/core/Workable.Sdk` contains the work-authoring contracts and primitives:

- work definitions and metadata
- work input, output, schema, and identifiers
- work configuration attributes and builders
- `IWorkExecutor`
- `IWorkExecutionContext`
- `WorkExecutionResult`
- `AddWorkableWork`

Feature libraries reference `Workable.Sdk` when they need to declare work, configure work defaults, or implement executors.

`packages/core/Workable.Abstractions` contains the consumer-facing contracts for using an already-hosted system:

- `IWorkSystem`
- `IWorkSystemRegistry`
- `IWorkCatalog`
- `IWorkQueueService`
- `IWorkerHandle`
- `IWorkerOperations`
- `IWorkQueryService`
- `IWorkEventStream`
- worker snapshots, query criteria, query results, action outcomes, queue outcomes, completions, and event records

Libraries reference `Workable.Abstractions` when they need to queue, query, observe, or control work but do not host Workable themselves.

`packages/core/Workable` contains the host/runtime surface:

- in-memory system implementation
- worker lifecycle, queueing, dispatch, retention, coordination, durability, concurrency, and event-stream behavior
- `AddWorkableSystem`

Host applications reference `Workable` when they need to create systems and run the in-process runtime. `Workable` also references the abstractions package, so host applications can inject and use `IWorkSystem` directly.

`packages/core/Workable.HttpApi` contains the HTTP API adapter surface:

- host setup and route entry points under `Hosting`
- system discovery and lifecycle routes under `Systems`
- catalog browsing and definition reconfiguration routes under `Catalog`
- queue request DTOs and queue routes under `Queue`
- HTTP query criteria, query adapter, and query routes under `Query`
- worker action and reconfiguration routes under `Workers`
- invocation channel enforcement for `WorkInvocationChannel.HttpApi`

Applications reference `Workable.HttpApi` when they want to expose Workable systems through HTTP endpoints.

`packages/core/Workable.Views` contains reusable component-view composition:

- component and view request DTOs
- component result envelopes
- overview, worker-grid, iteration-grid, catalog, and throughput component projections
- the typed worker-overview landing and realtime contracts
- shared view and worker-overview normalization used by HTTP and realtime transports

Adapter packages reference `Workable.Views` when they need to expose the component-view contract over a transport.

`packages/core/Workable.AspNetCore` contains ASP.NET Core integration that does not expose routes:

- `IWorkActorFactory` and `IWorkRequestContextFactory`
- default claims-based `IWorkAuthorizationGroupProvider`
- `AddWorkableAspNetCoreAuthorization`

ASP.NET Core applications reference `Workable.AspNetCore` when their own controllers, minimal API routes, or custom transports need authenticated `WorkRequestContext` values and default claims-based group resolution from `HttpContext`.

`packages/core/Workable.Entra` contains Microsoft Entra ID target-app integration:

- JWT bearer setup for Microsoft Entra ID
- Entra `scp`, `roles`, and `groups` claim mapping into Workable authorization groups
- SignalR browser token handling for the Workable realtime hub

ASP.NET Core applications reference `Workable.Entra` when Workable adapter requests should validate Entra bearer tokens and map target-token claims into Workable authorization groups.

`packages/core/Workable.Mcp` contains the MCP adapter surface:

- MCP-style tool descriptors for work definitions
- work invocation through `IWorkQueueService`
- query tools for worker status, worker snapshots, work definitions, work info, work keys, and status summaries
- action tools for start, pause, cancel, push, and purge
- schema compatibility handling for MCP tool input
- invocation channel enforcement for `WorkInvocationChannel.Mcp`

Applications reference `Workable.Mcp` when they want to expose Workable systems through an MCP transport.

`packages/core/Workable.SignalR` contains the realtime adapter surface:

- SignalR hub mapping for raw event, named view, and worker-overview subscriptions
- shared subscription registries keyed by normalized request shape and read visibility
- raw event broadcasting
- worker-overview state caching, delta generation, and coalesced worker-overview broadcasting
- coalesced named-view broadcasting using shared view subscriptions
- HTTP realtime capability provider registration

Applications reference `Workable.SignalR` when they want ASP.NET Core clients to receive realtime Workable updates.

`packages/extensions/sqlserver/Workable.SqlServer` contains the SQL Server durability integration:

- SQL Server-backed `IWorkPersistenceStore` implementation
- SQL Server queue durability option records and worker option helpers
- SQL schema helpers used by the package and CLI
- service registration extensions for SQL Server durability

Applications reference `Workable.SqlServer` when they want durable queueing, persistence-backed idempotency, or persistence-backed coordination backed by SQL Server.

`apps/tools/Workable.SqlServer.Cli` contains the SQL Server support CLI:

- schema discovery
- command-line entry point for SQL Server schema or setup workflows

This is an operational tool project, not a runtime dependency of hosted systems.

`apps/tools/Workable.PerformanceHarness` contains benchmark and load-harness code:

- benchmark entry point and scale definitions
- benchmark scenarios for publish, query, and authorized bulk-action workloads
- helper system construction for repeatable benchmark runs

This project exists to measure runtime characteristics; it is not a host/runtime package consumed by applications.

`apps/web/workable-admin-ui` contains the Next.js admin UI:

- `src/app` contains App Router pages, layouts, and route handlers
- `src/components/ui` contains reusable UI primitives
- `src/components/workable` contains Workable-specific product components
- `src/hooks` contains reusable React hooks
- `src/lib` contains client-side helpers, API clients, and shared utilities
- `public` contains static image and icon assets

`tests/Workable.Tests` contains the contract, configuration, lifecycle, queueing, event stream, and in-memory runtime tests.

`tests/extensions/sqlserver/Workable.SqlServer.Tests` contains cross-platform SQL Server integration tests. The suite uses a provided SQL connection string when available and otherwise provisions a local SQL Server container through a Docker-compatible runtime.

`apps/samples/Workable.SampleHost` contains the sample ASP.NET Core host used to demonstrate Workable systems, adapters, auth stubs, and sample work definitions.

## Namespace

The public .NET packages expose the public namespace `Workable`. The assembly split is a packaging boundary, not a namespace fork.

Source folders organize implementation concerns. Folder names do not define public namespace segments. The admin UI and benchmark harness are exceptions because they follow their own platform conventions rather than the .NET package namespace pattern.

## Folder Conventions

Folder names are vocabulary, not decoration. A folder with the same name in two projects should hold the same kind of concern at that project's layer.

`Catalog` contains work definition catalog concerns. In abstractions this is catalog contracts. In the runtime this is registration and lookup storage. In adapters this is catalog browsing, catalog route handling, and definition reconfiguration DTOs.

`Authorization` contains authorization policy and evaluation concerns. In the SDK this is definition-level authorization metadata and attributes. In the runtime this is group resolution, policy evaluation, authorized wrappers, and session composition.

`Queue` contains queue submission concerns. In abstractions this is queue contracts and worker handles. In the runtime this is queue execution entry points. In adapters this is queue request/response DTOs, queue adapters, and queue routes.

`Workers` contains worker control and worker state concerns: worker snapshots, worker actions, worker indexes, worker state transitions, worker action history, worker routes, and worker reconfiguration DTOs.

`Systems` contains whole-system lifecycle and discovery concerns: `IWorkSystem`, registries, in-memory system implementations, HTTP system resolution, system capability metadata, and system start/stop routes.

`Query` contains read-only query concerns. Public query inputs live in `Query/Criteria`, and returned query models live in `Query/Results`. Runtime query execution belongs in `packages/core/Workable/Query`: `WorkSystemReadModel` projects worker lifecycle updates into immutable read snapshots, and `WorkSystemReadModelQueryService` is the discoverable facade exposed through `IWorkSystem.Query`. Shared component/view DTOs, typed worker-overview DTOs, and query-side composition belong in `packages/core/Workable.Views/Query`. Adapter route glue belongs in that adapter's `Query` folder. Do not use `Query` for mutable operations.

`Events` contains event-stream contracts, event payloads, publishers, and subscription behavior.

`Metrics` contains metrics sinks and throughput/activity aggregation. Metrics files should describe captured system activity, not UI presentation.

`Hosting` contains integration and entry-point code: service-collection extensions, endpoint mapping, hosted services, builders, registration records, registries, route binding, and host-origin helpers. It should not become a general service folder.

`Execution` contains executor invocation, initialization, retry, recurrence, exception classification, and execution strategy code.

`Configuration` contains SDK configuration objects, attributes, builders, validators, and policy enums.

`Options` contains option or reconfiguration objects that are caller-supplied but are not the full work-configuration tree. Examples include worker and definition reconfiguration records.

`Contributions` contains extension points that let feature assemblies contribute work definitions or startup work.

`Data` contains serializable work data containers and schema helpers.

`Definitions` contains work definition identity, metadata, and definition-level value objects.

`Identifiers` contains IDs, versions, subjects, identifiers, concurrency keys, and shared key contracts.

`Messages` contains structured work messages, including severity, text, timestamps, optional targets, and optional metadata.

`Origins` contains actor/origin contracts and providers that describe where an invocation came from.

`Outcomes` contains structured outcomes for queueing, actions, completions, and definition reconfiguration.

`Profiling` contains profiling contracts, snapshots, runtime profile collection, and profiler facades.

`Logging` contains runtime log-capture integration. SDK logging configuration remains in `Configuration`.

`Realtime` contains realtime capability contracts. SignalR transport implementation stays in `Workable.SignalR`.

Avoid generic `Services` folders as a catch-all. Prefer the domain folders above, or `Hosting` when the code is truly host-entry-point glue.

HTTP adapter folders use the same domain words as the core surface. A route file, adapter file, and DTOs for the same operation family live together in `Catalog`, `Query`, `Queue`, `Systems`, or `Workers`; do not create a nested `Routes` folder unless all adapter operation folders adopt the same split.

MCP currently uses a compact tool-oriented surface at the project root because its tool descriptors and router cross queue, query, catalog, and worker action concerns. If the MCP surface grows enough to split, use the same adapter folder vocabulary as HTTP: `Hosting` for registration/transport entry points, `Catalog` for catalog tools, `Query` for read-only tools, `Queue` for invocation tools, `Systems` for system selection/lifecycle tools, and `Workers` for worker action tools.

Tests mirror the production concern folder when a feature has enough tests to group: `Catalog`, `Configuration`, `Execution`, `Hosting`, `HttpApi`, `Mcp`, `Query`, `Queue`, `SignalR`, `Systems`, and `Workers`. Cross-cutting helpers belong in `TestSupport`. Generated test output such as `TestResults` is not part of the source structure.

The admin UI follows Next.js conventions rather than the .NET domain folder vocabulary. Keep route handlers in `src/app`, generic UI primitives in `src/components/ui`, Workable-specific UI in `src/components/workable`, hooks in `src/hooks`, and client helpers in `src/lib`.

The sample host follows normal ASP.NET Core application structure rather than the package folder vocabulary. `WorkSystems` is where sample work definitions and sample controllers are grouped by scenario.

## Dependency Direction

`Workable.Abstractions` references `Workable.Sdk`.

`Workable` references `Workable.Abstractions`.

`Workable.AspNetCore` references `Workable.Abstractions`.

`Workable.Entra` references `Workable.AspNetCore` and `Workable`.

`Workable.Views` references `Workable.Abstractions`.

`Workable.HttpApi` references `Workable`, `Workable.AspNetCore`, and `Workable.Views`.

`Workable.Mcp` references `Workable` and `Workable.AspNetCore`.

`Workable.SignalR` references `Workable.Abstractions`, `Workable.AspNetCore`, `Workable.Views`, and `Workable`.

`Workable.SqlServer` references `Workable.Abstractions`.

Feature libraries reference `Workable.Sdk`.

Non-host libraries that use Workable reference `Workable.Abstractions`.

Host applications reference `Workable`.

Feature libraries do not need to know which runtime the host will use.
