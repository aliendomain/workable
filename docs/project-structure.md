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
- `IWorkQueryService`
- `IWorkEventStream`
- worker snapshots, query criteria, query results, action outcomes, queue outcomes, completions, and event records

Libraries reference `Workable.Abstractions` when they need to queue, query, observe, or control work but do not host Workable themselves.

`src/Workable` contains the host/runtime surface:

- in-memory system implementation
- worker lifecycle, queueing, dispatch, retention, concurrency, and event-stream behavior
- `AddWorkableSystem`

Host applications reference `Workable` when they need to create systems and run the in-process runtime. `Workable` also references the abstractions package, so host applications can inject and use `IWorkSystem` directly.

`src/Workable.HttpApi` contains the HTTP API adapter surface:

- host setup and route entry points under `Hosting`
- system discovery and lifecycle routes under `Systems`
- catalog browsing and definition reconfiguration routes under `Catalog`
- queue request DTOs and queue routes under `Queue`
- HTTP query criteria, query adapter, and query routes under `Query`
- worker action and reconfiguration routes under `Workers`
- invocation channel enforcement for `WorkInvocationChannel.HttpApi`

Applications reference `Workable.HttpApi` when they want to expose Workable systems through HTTP endpoints.

`src/Workable.AspNetCore` contains ASP.NET Core integration that does not expose routes:

- HTTP-context origin provider for direct .NET queueing and worker operations
- `AddWorkableAspNetCoreOrigins`

ASP.NET Core applications reference `Workable.AspNetCore` when their own controllers or minimal API routes queue work and those direct .NET calls should record actor information from `HttpContext.User`.

`src/Workable.Mcp` contains the MCP adapter surface:

- MCP-style tool descriptors for work definitions
- work invocation through `IWorkQueue`
- query tools for worker status, worker snapshots, work definitions, work info, work keys, and status summaries
- action tools for start, pause, cancel, push, and purge
- schema compatibility handling for MCP tool input
- invocation channel enforcement for `WorkInvocationChannel.Mcp`

Applications reference `Workable.Mcp` when they want to expose Workable systems through an MCP transport.

`src/Workable.SignalR` contains the realtime adapter surface:

- SignalR hub mapping for worker event and dashboard subscriptions
- one Workable event-stream subscription per hosted system
- worker detail event broadcasting
- coalesced dashboard summary broadcasting
- HTTP realtime capability provider registration

Applications reference `Workable.SignalR` when they want ASP.NET Core clients to receive realtime Workable updates.

`src/workable-admin-ui` contains the Next.js admin UI:

- `src/app` contains App Router pages, layouts, and route handlers
- `src/components/ui` contains reusable UI primitives
- `src/components/workable` contains Workable-specific product components
- `src/hooks` contains reusable React hooks
- `src/lib` contains client-side helpers, API clients, and shared utilities
- `public` contains static image and icon assets

`tests/Workable.Tests` contains the contract, configuration, lifecycle, queueing, event stream, and in-memory runtime tests.

## Namespace

All packages expose the public namespace `Workable`. The assembly split is a packaging boundary, not a namespace fork.

Source folders organize implementation concerns. Folder names do not define public namespace segments.

## Folder Conventions

Folder names are vocabulary, not decoration. A folder with the same name in two projects should hold the same kind of concern at that project's layer.

`Catalog` contains work definition catalog concerns. In abstractions this is catalog contracts. In the runtime this is registration and lookup storage. In adapters this is catalog browsing, catalog route handling, and definition reconfiguration DTOs.

`Queue` contains queue submission concerns. In abstractions this is queue contracts and worker handles. In the runtime this is queue execution entry points. In adapters this is queue request/response DTOs, queue adapters, and queue routes.

`Workers` contains worker control and worker state concerns: worker snapshots, worker actions, worker indexes, worker state transitions, worker action history, worker routes, and worker reconfiguration DTOs.

`Systems` contains whole-system lifecycle and discovery concerns: `IWorkSystem`, registries, in-memory system implementations, HTTP system resolution, system capability metadata, and system start/stop routes.

`Query` contains read-only query concerns. Public query inputs live in `Query/Criteria`, and returned query models live in `Query/Results`. Runtime query execution belongs in `src/Workable/Query`: `WorkQueryService` is the discoverable facade exposed through `IWorkSystem.Query` and owns the read-model projection for each supported query method. Adapter-specific query criteria, adapters, and routes belong in that adapter's `Query` folder. Do not use `Query` for mutable operations.

`Events` contains event-stream contracts, event payloads, publishers, and subscription behavior.

`Metrics` contains metrics sinks and throughput/activity aggregation. Metrics files should describe captured system activity, not UI presentation.

`Hosting` contains integration and entry-point code: service-collection extensions, endpoint mapping, hosted services, builders, registration records, registries, route binding, and host-origin helpers. It should not become a general service folder.

`Execution` contains executor invocation, initialization, retry, recurrence, exception classification, and execution strategy code.

`Configuration` contains SDK configuration objects, attributes, builders, validators, and policy enums.

`Contributions` contains extension points that let feature assemblies contribute work definitions or startup work.

`Data` contains serializable work data containers and schema helpers.

`Definitions` contains work definition identity, metadata, and definition-level value objects.

`Identifiers` contains IDs, versions, subjects, identifiers, concurrency keys, and shared key contracts.

`Messages` contains structured work messages and message severity.

`Origins` contains actor/origin contracts and providers that describe where an invocation came from.

`Outcomes` contains structured outcomes for queueing, actions, completions, and definition reconfiguration.

`Profiling` contains profiling contracts, snapshots, runtime profile collection, and profiler facades.

`Logging` contains runtime log-capture integration. SDK logging configuration remains in `Configuration`.

`Realtime` contains realtime capability contracts. SignalR transport implementation stays in `Workable.SignalR`.

HTTP adapter folders use the same domain words as the core surface. A route file, adapter file, and DTOs for the same operation family live together in `Catalog`, `Query`, `Queue`, `Systems`, or `Workers`; do not create a nested `Routes` folder unless all adapter operation folders adopt the same split.

MCP currently uses a compact tool-oriented surface at the project root because its tool descriptors and router cross queue, query, catalog, and worker action concerns. If the MCP surface grows enough to split, use the same adapter folder vocabulary as HTTP: `Hosting` for registration/transport entry points, `Catalog` for catalog tools, `Query` for read-only tools, `Queue` for invocation tools, `Systems` for system selection/lifecycle tools, and `Workers` for worker action tools.

Tests mirror the production concern folder when a feature has enough tests to group: `Catalog`, `Configuration`, `Execution`, `Hosting`, `HttpApi`, `Mcp`, `Query`, `Queue`, `SignalR`, `Systems`, and `Workers`. Cross-cutting helpers belong in `TestSupport`. Generated test output such as `TestResults` is not part of the source structure.

The admin UI follows Next.js conventions rather than the .NET domain folder vocabulary. Keep route handlers in `src/app`, generic UI primitives in `src/components/ui`, Workable-specific UI in `src/components/workable`, hooks in `src/hooks`, and client helpers in `src/lib`.

## Dependency Direction

`Workable.Abstractions` references `Workable.Sdk`.

`Workable` references `Workable.Abstractions`.

`Workable.AspNetCore` references `Workable.Abstractions`.

`Workable.HttpApi` references `Workable` and `Workable.AspNetCore`.

`Workable.Mcp` references `Workable`.

`Workable.SignalR` references `Workable.Abstractions`.

Feature libraries reference `Workable.Sdk`.

Non-host libraries that use Workable reference `Workable.Abstractions`.

Host applications reference `Workable`.

Feature libraries do not need to know which runtime the host will use.
