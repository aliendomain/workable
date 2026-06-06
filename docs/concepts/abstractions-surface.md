# Abstractions Surface

`Workable.Abstractions` is the main package for code that uses a hosted Workable system without hosting Workable itself.

That distinction is important. A feature library, background service, API layer, or custom transport can depend on `Workable.Abstractions`, accept `IWorkSystem` or `IWorkSystemSession`, and stay completely decoupled from the runtime implementation in `Workable`.

## When To Depend On It

Reach for `Workable.Abstractions` when your code needs to:

- queue work
- inspect work definitions
- query workers, iterations, or system state
- subscribe to work events
- apply worker actions or runtime reconfiguration
- participate in authorization-aware request handling

If the same code also needs to create systems, add work-definition sources, or configure hosting behavior, that is where `Workable` takes over.

## Root Types

The package revolves around three root contracts:

- `IWorkSystemRegistry`: discover one or more hosted systems
- `IWorkSystem`: one hosted system and its public facets
- `IWorkSystemSession`: the caller-scoped version of that same surface

Inject `IWorkSystem` when you just need the default hosted system. Inject `IWorkSystemRegistry` when the caller needs to choose by `WorkSystemId` or by name.

`IWorkSystemRegistry` is intentionally small:

- `Default`
- `Systems`
- `TryGet(...)` by id
- `TryGet(...)` by name

That keeps multi-system discovery on the public contract without forcing consumers to know anything about the host's internal registration model.

## Direct System Vs Session

`IWorkSystem` is the raw system root. It exposes:

- `Id`, `Name`, `State`, and `RequiresAuthorization`
- `Catalog`
- `Queue`
- `Workers`
- `Query`
- `Events`
- `Diagnostics`
- `DescribeAccess(...)`
- `CreateSession(...)`
- `Start(...)`
- `Stop(...)`

`IWorkSystemSession` exposes the same operational facets but binds them to one `WorkRequestContext` up front. It also carries `SystemName` and `SystemState` so a transport or UI can hold onto one caller-scoped view of the system.

Use the root system when the caller is trusted, in-process, and not user-scoped. Use a session when request identity, origin, or authorization matters.

```csharp
var actor = new WorkActor("user-123", "Taylor");

var requestContext = new WorkRequestContext(
    WorkOrigin.Create(
        WorkInvocationChannel.HttpApi,
        actor: actor),
    url: "/workable/workers",
    isAuthenticated: true);

IWorkSystemSession session = workSystem.CreateSession(requestContext);

WorkerQueryResult workers = await session.Query.Workers(cancellationToken: cancellationToken);
```

That session-bound model is the most important mental model in this package: the same catalog, queue, worker, query, event, and diagnostics contracts still exist, but they can now be filtered or rejected according to the bound caller.

For trusted in-process callers, `WorkRequestContext.IsAuthenticated` is also part of that bound caller state. Workable uses it together with a known actor to evaluate rules such as `AllowOperateToKnownAuthenticatedUsers()`.

## Access Introspection

`DescribeAccess(...)` lets a host or custom adapter reason about access before creating a broader session experience.

`DescribeAccess(...).HasAnyAccess()` is the simple yes-or-no check for "does this caller have enough real access for this system to be visible or selected by name through a transport-facing surface?"

`DescribeAccess(...)` returns a `WorkSystemAccessSummary`:

- `IsSystemAdministrator`
- `IsWorkAdministrator`
- `CanViewDiagnostics`
- `CanControlSystem`
- `CanReadAllWork`
- `CanOperateAllWork`
- `TotalDefinitionCount`
- `ReadableDefinitionCount`
- `OperableDefinitionCount`

That is the right contract for capability negotiation, custom UI feature gating, or system-list endpoints that need to describe more than a boolean.

## System Boundary Vs Work Boundary

It helps to separate two kinds of authorization behavior.

System-boundary authorization answers:

- may this caller discover the system?
- may this caller read diagnostics?
- may this caller start or stop the system?

Work-boundary authorization answers:

- may this caller read this definition and its workers?
- may this caller queue this definition?
- may this caller pause, cancel, push, purge, or reconfigure this worker?

System-boundary failures are where `WorkSystemAuthorizationRequiredException` and `WorkSystemAccessDeniedException` live. Work-boundary failures generally stay inside structured outcomes like `WorkQueueOutcome` and `WorkActionOutcome`.

## Catalog Surface

`IWorkCatalog` is the public definition surface:

- `Definitions`
- `ListByCategory(...)`
- `TryGet(...)` by id or name
- `Reconfigure(...)`
- `IsFrozen`

`IsFrozen` matters mostly to hosts and dynamic definition contributors. Definitions can still be contributed while startup is building the catalog. Once startup finishes, the catalog is frozen and new contributions stop.

`Reconfigure(...)` is optimistic-concurrency based through `WorkDefinitionVersion`. It changes the defaults used for future workers, not workers that already exist.

## Queue Surface

`IWorkQueueService` accepts work by definition id or name, with either raw `WorkInput` or typed CLR input.

Queueing always returns an `IWorkerHandle`, even when the request is rejected. The handle bridges two moments:

- immediate admission through `WorkQueueOutcome`
- eventual execution completion through `WaitForCompletion()`

That is why the same queue API works for fire-and-forget, request/response, and operator tooling.

See [Outcomes And Control](outcomes-and-control.md) for the full outcome model.

## Worker Control Surface

`IWorkerOperations` is the mutable worker surface:

- `Execute(...)` for `Start`, `Pause`, `Cancel`, `Push`, and `Purge`
- `ExecuteAll(...)` for bulk actions
- `Reconfigure(...)` for runtime worker reconfiguration

Single-worker operations are revision-aware through `WorkerVersion`. Bulk operations intentionally report one `WorkActionOutcome` per matched worker instead of collapsing the whole batch into one coarse result.

This surface is for existing workers. Changing defaults for future workers belongs on `IWorkCatalog.Reconfigure(...)`.

## Query Surface

`IWorkQueryService` is the display and inspection surface. It stays intentionally discoverable by giving each built-in query its own named method.

The main groups are:

- worker detail and worker lists
- iteration detail and iteration lists
- work definition browsing
- `WorkInfo` and definition rollups
- worker key and key-type search
- iteration key and key-type search
- status summaries
- whole-system and sliced aggregate queries

`Worker(...)` and `WorkerIteration(...)` return authoritative retained detail. The aggregate/list side of the query surface is eventual: each call reads one published projector snapshot, but separate aggregate calls are not guaranteed to line up against the same snapshot.

See [Querying](querying.md) for the read-model, key-search, and aggregate-query details.

## Event Surface

`IWorkEventStream` exposes live subscriptions. `Subscribe(...)` returns an `IWorkEventSubscription`, and `Read(...)` produces the async stream of `WorkEvent` envelopes.

The public contract is:

- subscriptions only observe future events
- each subscription owns its own bounded buffer
- filters apply before buffered delivery
- disposing the subscription or canceling the read removes it

This makes the event surface good for notification, correlation, and realtime refresh triggers. It is not a replay log.

See [Observability](observability.md) for payload, filtering, and buffering details.

## Diagnostics Surface

`IWorkSystemDiagnostics` groups the runtime health facets:

- queue
- read model
- retention
- concurrency
- durability
- idempotency

These are the same shapes used by HTTP, SignalR, and admin tooling. `Workable.Abstractions` is where those diagnostics stay public for any in-process consumer that needs them.

## Lifecycle Surface

`Start(...)` and `Stop(...)` are part of the abstractions package because some hosts and tools need explicit system lifecycle control.

`Stop(...)` returns `WorkSystemStopResult`, not just a boolean. The result includes:

- the shutdown grace period used
- workers for which interruption was requested
- compact shutdown summaries for those workers
- workers that outlasted the grace period and had to be force-completed as interrupted in Workable state

That keeps shutdown operationally inspectable from the public contract.

See [Lifecycle](lifecycle.md) for the detailed shutdown behavior.

## Typical Dependency Patterns

Most consumer code falls into one of these shapes:

- inject `IWorkSystem` and queue or query the default system
- inject `IWorkSystemRegistry` and choose a named system
- accept an `IWorkSystemSession` when the caller context is already known

That is the package's job: give downstream code a stable, host-independent surface for using Workable without dragging runtime internals across the package boundary.
