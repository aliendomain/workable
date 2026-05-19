# Work Lifecycle

## Overview

Work begins when a caller queues a known work definition. The queue resolves the definition, creates a worker, and returns a handle according to the worker's start policy. After acceptance, the worker belongs to the Workable system; the caller may keep the handle, await it later, or discard it.

The queue cancellation token applies only to accepting the queue request. Execution uses Workable's system lifetime and per-worker cancellation, so caller-scoped cancellation, for example from a controller action, does not cancel already accepted work.

These diagrams show the normal run-once path in smaller parts. The execution engine also includes dispatch, idempotency, concurrency, durability, and retention coordinators; those are described in [Execution Engine](execution-engine.md).

## Queue Acceptance

Queue acceptance resolves work, validates the effective runtime configuration, and applies coordination before returning to the caller. Non-durable acceptance creates a queued worker record, publishes the queued event, submits a scheduling request when the start policy starts work automatically, and returns according to the configured start policy. Durable acceptance persists the queue entry first; the durable reader publishes the queued event and schedules the worker when the persisted row materializes in memory.

```mermaid
sequenceDiagram
    participant Caller
    participant Queue as IWorkQueueService / WorkQueueService
    participant Catalog as WorkSystemCatalog
    participant Ops as WorkerOperations
    participant Persistence as WorkerPersistenceCoordinator
    participant Durability as WorkQueueDurabilityCoordinator
    participant Record as WorkerRecord
    participant Events as WorkEventStream
    participant Dispatcher as WorkerDispatcher
    participant Handle as IWorkerHandle / WorkerHandle

    Caller->>Queue: Enqueue(definitionId or name, WorkInput, queueToken)
    Queue->>Catalog: TryGetWork(...)
    Catalog-->>Queue: RegisteredWork
    Queue->>Ops: CreateWorker(RegisteredWork, input, options)
    Ops->>Persistence: AcceptQueuedWorker(...)

    alt Durable queueing
        Persistence->>Durability: Enqueue durable row
        Durability-->>Persistence: Durable handle
        Persistence-->>Ops: Durable handle
        Note over Durability,Dispatcher: Durable reader materializes the row later, then publishes queued and schedules
    else In-memory queueing
        Persistence-->>Ops: WorkerRecord and scheduling flags
        Ops->>Record: Track WorkerRecord(state: Queued, revision: 0)
        Ops->>Events: Publish worker.queued to active subscriptions
        opt Start policy starts automatically
            Ops->>Dispatcher: Schedule(worker)
        end
        opt Start policy waits past acceptance
            Ops->>Record: Wait for started or completed milestone
        end
    end
    Ops-->>Queue: WorkerHandle
    Queue-->>Caller: IWorkerHandle

    Note over Dispatcher,Handle: Acceptance ends after any schedule request is submitted
    Note over Caller,Dispatcher: Caller ambient context is not carried into the scheduler
```

## Execution

Execution begins after dispatch. The dispatcher asks `WorkerOperations` to start the queued worker, then the selected execution strategy invokes work through `WorkerExecutionInvoker`.

```mermaid
sequenceDiagram
    participant Dispatcher as WorkerDispatcher
    participant Ops as WorkerOperations
    participant SM as WorkerStateMachine
    participant Record as WorkerRecord
    participant Events as WorkEventStream
    participant Strategy as IWorkerExecutionStrategy
    participant Invoker as WorkerExecutionInvoker
    participant InitScope as Initialization DI Scope
    participant Init as IWorkInitializer
    participant ExecScope as Execution DI Scope
    participant Exec as IWorkExecutor

    Dispatcher->>Ops: Dispatch queued worker
    Ops->>SM: Apply(Queued, Start)
    SM-->>Ops: Running accepted

    Ops->>Record: Start(systemLifetimeToken), state sequence advances
    Ops->>Events: Publish worker.started to active subscriptions

    Note over Strategy,Exec: Workable creates execution context inside the execution scope
    Ops->>Strategy: Execute(worker, workerToken)
    Strategy->>Invoker: Execute(worker, workerToken)
    opt Work has initialization
        Invoker->>InitScope: CreateAsyncScope()
        InitScope->>Init: Resolve initializer
        Invoker->>Init: Initialize(context, input, workerToken)
        Init-->>Invoker: WorkExecutionResult
        Invoker->>InitScope: DisposeAsync()
    end
    Invoker->>ExecScope: CreateAsyncScope()
    ExecScope->>Exec: Resolve executor
    Invoker->>Exec: Execute(context, input, workerToken)
    Exec-->>Invoker: WorkExecutionResult or exception
    Invoker->>ExecScope: DisposeAsync()
    Invoker-->>Strategy: WorkExecutionResult or exception

    Strategy->>SM: Complete(Running, hasErrors)
    SM-->>Strategy: Completed or Failed

    Strategy->>Record: Complete(result), state sequence advances
    Strategy->>Events: Publish worker.completed / worker.failed to active subscriptions
    Strategy->>Record: Release per-worker cancellation resources
```

Executors receive a Workable-owned `CancellationToken`. Explicit API cancellation and interruption both cancel that token, but they produce different lifecycle outcomes. During interruption, `IWorkExecutionContext.IsInterrupted` returns `true` and `IWorkExecutionContext.InterruptionReason` reports why, such as `Shutdown` or `LeaseLost`, so executor code can distinguish recoverable interruption from an explicit cancel request.

## Recurring Execution

Recurring execution keeps one worker active across multiple iterations. Each iteration calls `WorkerExecutionInvoker`, so every iteration gets its own async DI scope and scoped service provider.

```mermaid
sequenceDiagram
    participant Strategy as RecurringWorkerExecutionStrategy
    participant Invoker as WorkerExecutionInvoker
    participant Record as WorkerRecord
    participant Events as WorkEventStream

    Strategy->>Invoker: Execute iteration with fresh scope
    Note over Strategy,Invoker: Initializers and executor use separate scopes inside the iteration
    Invoker-->>Strategy: WorkExecutionResult
    Strategy->>Record: CompleteRecurringIteration(result, continue)
    Strategy->>Events: Publish worker.iteration.completed or worker.iteration.failed
    Strategy->>Events: Publish worker.waiting
    Strategy->>Record: Wait for interval or Push/Pause/Cancel

    alt Push
        Record-->>Strategy: Wait is signaled
        Strategy->>Record: Begin next iteration
    else Interval elapses
        Strategy->>Record: Begin next iteration
    else Pause, Cancel, or shutdown interruption
        Record-->>Strategy: Wait is signaled
        Strategy->>Record: Complete as paused, canceled, or interrupted
    end
```

## Worker Handle

The handle exposes the immediate queue outcome and can await completion. The worker continues independently even if the caller discards the handle.

```mermaid
sequenceDiagram
    participant Caller
    participant Queue as IWorkQueueService
    participant Handle as IWorkerHandle / WorkerHandle
    participant Record as WorkerRecord

    Caller->>Queue: Enqueue(...)
    Queue-->>Caller: IWorkerHandle

    alt Caller keeps the handle
    Caller->>Handle: WaitForCompletion()
    Handle->>Record: WaitForCompletion()
    Record-->>Handle: WorkCompletion
    Handle-->>Caller: WorkCompletion with WorkerSnapshot and WorkOutput
    else Caller discards the handle
    Caller->>Handle: Discard reference
    Note over Handle,Record: Accepted worker remains owned by Workable
    end
```

## Shutdown

When a system stops, it stops accepting new queue requests, interrupts queued, running, waiting, and retrying workers, stops the dispatcher, and waits for the shutdown grace period. By default, hosted systems derive that grace period from the .NET generic host shutdown timeout; outside a host, the fallback is 15 seconds. If a worker does not complete after interruption within that allowance, Workable marks the worker as interrupted with a forced-interruption message and continues shutdown. Stop results include the grace period plus summaries and names for workers that had to be force-completed during shutdown. Once shutdown work is complete, Workable clears in-memory worker and iteration records for the system.

Shutdown interruption is not the same as API cancellation. `WorkAction.Cancel` is an explicit final state and publishes the normal cancel/canceled action and completion events. Shutdown interruption records `WorkerState.Interrupted`, publishes `worker.interrupted`, and leaves durable queue rows eligible for replay after lease expiry.

```mermaid
sequenceDiagram
    participant Host
    participant System as IWorkSystem
    participant Ops as WorkerOperations
    participant Dispatcher as WorkerDispatcher
    participant Record as WorkerRecord
    participant Events as WorkEventStream

    Host->>System: Stop()
    System->>Ops: StopDispatching()
    Ops->>Ops: Stop accepting new queue requests
    Ops->>Record: RequestInterruptForSystemStop()
    Record-->>Ops: Interruption requested
    Ops->>Dispatcher: Stop()
    Ops->>Record: WaitForCompletion(grace period)
    alt Worker completes before grace expires
        Record-->>Ops: Interrupted completion
        Ops->>Events: Publish worker.interrupted
    else Grace expires
        Ops->>Record: ForceInterruptForSystemStop()
        Ops->>Events: Publish worker.interrupted
    end
    Ops-->>System: Shutdown complete
```

## Classes

- `IWorkQueueService` accepts work by `WorkDefinitionId` or name.
- `WorkQueueService` resolves queued work and delegates worker creation.
- `WorkSystemCatalog` stores the system's immutable work definitions.
- `RegisteredWork` connects a `WorkDefinition` to an executor factory.
- `WorkerOperations` creates workers, owns in-memory dispatch, and applies worker actions.
- `WorkerPersistenceCoordinator` centralizes in-memory and persistence-backed coordination for queue acceptance, durable materialization, durable completion, idempotency lookup, and final-state cleanup.
- `WorkQueueAcceptanceCoordinator` prepares queue acceptance by applying idempotency, concurrency, and durability decisions to the resolved runtime plan.
- `WorkIdempotencyCoordinator` performs local duplicate-subject checks and active subject lookup.
- `WorkQueueDurabilityCoordinator` integrates with the persistence store for durable queue entries, lease renewal, replay, and cleanup.
- `WorkerDispatcher` schedules accepted workers for execution outside the caller's queue request.
- `WorkConcurrencyCoordinator` manages per-definition concurrency managers for work that opts into concurrency.
- `WorkerRetentionScheduler` schedules automatic purge for completed and canceled workers.
- `WorkerRecord` stores worker state, control revision, state sequence, input, output, options, and messages.
- `WorkerIterationSnapshot` reports retained iteration history on `WorkerSnapshot`, including run-once work and transient retry attempts.
- `IWorkerExecutionStrategy` executes a worker according to a runtime strategy.
- `ConfiguredWorkerExecutionStrategy` chooses run-once, transient retry, or recurring execution for each worker.
- `RunOnceWorkerExecutionStrategy` executes workers that run once and then complete.
- `TransientRetryWorkerExecutionStrategy` retries unhandled execution exceptions that are classified as transient.
- `RecurringWorkerExecutionStrategy` executes recurring workers across repeated iterations.
- `WorkerExecutionInvoker` creates initialization scopes for configured initializers, then creates a separate execution scope for the executor.
- `IWorkInitializer` runs setup or validation before executor invocation.
- `WorkerStateMachine` owns action and completion transition rules.
- `IWorkExecutor` runs the work implementation.
- `IWorkExecutionContext` provides execution metadata and scoped services from a Workable-owned execution scope.
- `WorkEventStream` manages active event subscriptions and publishes worker lifecycle events to matching subscribers.
- `WorkerHandle` exposes immediate queue outcome and awaitable completion.
- `WorkCompletion` reports final status, worker snapshot, messages, and output.

## Ownership Rules

- Accepted workers continue running even when the caller discards the `WorkerHandle`.
- The queue cancellation token only cancels queue acceptance before a worker is accepted.
- The dispatcher starts accepted workers outside the caller's execution context.
- Work execution uses Workable-owned cancellation, including pause, cancel, shutdown interruption, and runtime policy cancellation.
- Shutdown stops accepting new queue requests before interruption begins. New queue requests return `WorkQueueStatus.Invalid` with `workable.system.stopping`.
- Shutdown interruption is cooperative. Workable cancels execution tokens, exposes `IWorkExecutionContext.IsInterrupted`, and waits for the configured grace period. Remaining workers are force-completed as interrupted in Workable state.
- Initializers and executors do not share scoped service instances. Each initializer run creates and disposes its own DI scope before the executor scope is created.
- Recurring workers create new initializer and executor scopes for each iteration.
- `OnceLazy` initialization uses a per-definition gate. The first worker that reaches the initializer runs it while competing workers wait; after it succeeds, later workers skip it.
- Recurring workers that use concurrency hold their capacity while waiting between iterations.
- Per-worker execution resources are released when execution completes, fails, pauses, is canceled, or is interrupted.
- Event subscriptions are owned by callers and are removed when disposed or canceled.
