# Abstractions Extension Points

Most applications use `Workable.Abstractions` only as a consumer surface. A smaller set use it to extend Workable itself.

This document focuses on that smaller set: the public seams where advanced hosts and integrations can plug in without depending on runtime internals.

## When To Reach For These Contracts

Reach for these APIs only when one of these is true:

- you are implementing a persistence-backed durability provider
- you want Workable iterations translated into your metrics pipeline
- you need a host lifecycle callback during shutdown
- you are advertising or replacing a realtime transport
- you need custom authorization-group resolution

If you are only queueing, querying, or controlling work, these are not the APIs you want.

## Persistence Providers

`IWorkPersistenceStore` is the most substantial extension point in the package. It is the public contract behind durable queueing, persistence-backed idempotency, and workflow-run persistence.

The worker-oriented durability protocol has a clear shape:

1. `Initialize(...)`
2. `Enqueue(...)`
3. `ReserveIdempotency(...)`
4. `ClaimReady(...)`
5. `RenewLeases(...)`
6. `RetainFailed(...)` or `DeleteFinal(...)`

The same interface also carries the workflow-oriented persistence protocol:

1. `InitializeWorkflows(...)`
2. `BeginWorkflowTransaction(...)`
3. `ListIncompleteWorkflowRuns(...)`
4. `UpsertWorkflowRun(...)`
5. `DeleteWorkflowRun(...)`

That protocol tells you a lot about Workable's durability model:

- accepted queue requests can be persisted before local execution starts
- queue replay is lease based, not "everyone can poll the same row forever"
- durable cleanup is separate from failed-work retention
- persistence-backed idempotency can participate in the same provider
- workflow orchestration state can live beside worker durability in the same store abstraction
- workflow persistence can share one store-defined transaction with durable worker enqueue

### What The Provider Owns

At a practical level, a provider is responsible for:

- durable queue initialization for one system
- storing accepted queue entries
- optionally reserving idempotency records in persistence
- claiming ready entries for one owner
- renewing those leases while work is in flight
- retaining failed durable entries when the host chooses to keep them
- deleting final durable entries, with or without a caller-owned durability transaction
- initializing workflow persistence for one system
- beginning workflow persistence transactions that can also be passed through worker queue durability options
- listing incomplete durable workflow runs for resume
- storing the latest workflow-run snapshot
- deleting persisted workflow runs after final completion

`WorkQueueDurabilityInitializationContext` is worth noticing because Workable hands the provider the system id, optional name, and current definitions. That is the provider's chance to prepare tables, validate schema, or align definition metadata before queue traffic begins.

`WorkflowPersistenceInitializationContext` is the workflow-side equivalent. It gives the provider the system id, optional name, and registered workflow definitions before durable workflow state is read or written.

### Important Durable Types

The durability-related record types all describe one coherent protocol:

- `WorkQueueDurabilityEnqueueRequest`
- `WorkIdempotencyPersistenceRequest`
- `WorkQueueDurabilityClaimRequest`
- `WorkQueueDurabilityEntry`
- `WorkQueueDurabilityLease`
- `WorkQueueDurabilityCleanupRequest`
- `WorkflowPersistenceInitializationContext`
- `WorkflowPersistenceTransactionRequest`
- `WorkflowPersistenceReadRequest`
- `WorkflowRunPersistenceRecord`
- `WorkflowStepPersistenceRecord`
- `WorkflowPersistenceDeleteRequest`
- `IWorkflowPersistenceTransaction`

`WorkQueueDurabilityEntry` is the payload that comes back from `ClaimReady(...)`. It carries the lease plus the definition name, input, options, configuration, origin, and creation time needed to materialize work back into memory.

`WorkflowRunPersistenceRecord` is the workflow-side snapshot payload. It carries the run id, workflow definition version and name, request context, workflow status, persisted step snapshots, timestamps, and workflow messages.

`IWorkflowPersistenceTransaction` extends `IWorkQueueDurabilityTransaction`, which lets one store-defined transaction span workflow-run persistence and durable child-worker enqueue.

### Error Semantics

The public exceptions here matter because they communicate expected provider-level failure modes:

- `WorkPersistenceStoreUnavailableException`: the provider could not be used at all
- `WorkQueueDurabilityDuplicateException`: persistence-backed idempotency rejected a duplicate
- `WorkQueueDurabilityLeaseLostException`: the current owner lost one or more leases

That last one is especially important. Lease loss is not a generic error; it is part of the durable replay model and can lead to worker interruption and later replay.

If you are implementing a provider, study [Workable SQL Server Integration](../../packages/extensions/sqlserver/README.md) as the concrete example.

## Metrics Sink

`IWorkMetricsSink` is intentionally small:

```csharp
public interface IWorkMetricsSink
{
    void IterationRecorded(WorkDefinitionId definitionId, WorkerIterationSnapshot iteration);
}
```

Workable calls it when an iteration result is recorded. That makes it the clean public seam for translating Workable execution into application metrics such as:

- duration histograms
- success, failure, and cancellation counters
- per-definition throughput measurements
- recurring-iteration activity dashboards

The important detail is that the hook is iteration based, not queue-request based. For recurring work and transient retry, one worker can produce multiple iteration records over time.

## Lifecycle Observer

`IWorkSystemLifecycleObserver` is the host-facing shutdown hook:

```csharp
Task SystemStopping(
    IWorkSystem system,
    WorkOrigin origin,
    CancellationToken cancellationToken = default);
```

Workable invokes it when a system begins stopping. The observer receives both the system and the `WorkOrigin` of the stop request.

Use it when the host needs to:

- flush or detach related resources before shutdown completes
- mirror Workable shutdown into another subsystem
- perform app-specific coordination as system stop begins

This is not a worker event subscription replacement. It is the lifecycle seam for host coordination.

## Realtime Capability Provider

`IWorkRealtimeCapabilityProvider` is the public way to advertise whether a host has a realtime surface:

```csharp
public interface IWorkRealtimeCapabilityProvider
{
    WorkRealtimeCapability GetCapability();
}
```

`WorkRealtimeCapability` reports:

- whether realtime is enabled
- transport name
- hub path

That matters when another surface, usually HTTP or a custom admin UI, wants to ask "does this host expose realtime and how should I connect to it?" without hard-coding SignalR assumptions.

Most applications will never implement this directly. It matters when you are building or replacing a realtime transport and want the host to advertise that capability coherently.

## Authorization Group Provider

`IWorkAuthorizationGroupProvider` lives in `Workable.Abstractions` because authorization-group resolution is part of the host contract, not just an ASP.NET Core convenience.

```csharp
public interface IWorkAuthorizationGroupProvider
{
    IReadOnlySet<string> GetGroups(WorkActor actor, string? systemName);
}
```

ASP.NET Core integration supplies a default implementation based on claims. Hosts can replace it when Workable groups should come from:

- database-backed permission resolution
- tenant-aware group expansion
- application-specific policy projection
- a non-claims identity system

This is the seam between "the host knows who the caller is" and "Workable needs normalized group names to evaluate."

## Choosing The Right Level

These contracts matter because they keep advanced integrations on the supported public side of the package boundary.

Use `Workable.Abstractions` as a pure consumer surface by default. Drop into these extension points only when you are deliberately extending one of Workable's hosting responsibilities: persistence, metrics, lifecycle, realtime capability, or authorization-group resolution.
