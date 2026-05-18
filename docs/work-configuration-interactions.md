# Configuration Interactions

Most configuration options are easy to reason about in isolation. A few become more important when they are combined because one option changes the lifecycle point that another option observes.

## Recurrence And Concurrency

A recurring worker is one worker that executes multiple iterations. Between iterations, the worker enters `Waiting` until the recurrence interval elapses or `Push` starts the next iteration.

`Waiting` recurring workers count against concurrency capacity. This is true for every `WorkConcurrencyBlockingMode` because the recurring worker is still active and has reserved the right to continue its next iteration.

With `WorkConcurrencyScope.PerDefinition` and `MaximumCapacity` set to `1`, one recurring worker can keep later workers for the same definition queued or rejected while it waits between iterations. The result depends on `LimitReachedBehavior`.

- `Ignore` rejects later queue requests when the recurring worker holds capacity.
- `DeferStart` accepts later queue requests and leaves them queued until capacity becomes available.

Use `WorkConcurrencyScope.PerSubject` or `WorkConcurrencyScope.PerConcurrencyKey` when separate subjects or keys should have separate capacity. Increase `MaximumCapacity` when multiple workers in the same concurrency group should run or wait at the same time.

## Recurrence And Transient Retry

Transient retry creates additional worker iterations. If an execution attempt throws a transient exception, Workable records the failed iteration, moves the worker to `Retrying` for the retry backoff, and starts another iteration when the backoff completes.

When a retry succeeds, recurrence records a successful iteration. When retries are exhausted, recurrence records a failed iteration and then applies `ContinueAfterFailure` and `CircuitBreakerFailureThreshold`.

## Start And Recurrence

Start policies wait for worker lifecycle points, not recurrence iteration points.

With recurring work, `StartAndReturnAfterCompleted` waits until the recurring worker reaches a completion state. It does not return after the first successful iteration. A recurring worker can continue indefinitely, so a queue call using `StartAndReturnAfterCompleted` can also wait indefinitely unless recurrence stops through cancellation, shutdown interruption, pause, reconfiguration, circuit breaker behavior, or another terminal outcome.

For recurring work, `StartAndReturnAfterAccepted` is usually the most natural fire-and-observe policy. Use `StartAndReturnAfterStarted` when the caller needs to know that the first iteration actually began.

## Start And Concurrency

Start configuration controls when queueing returns to the caller. Concurrency can delay the lifecycle point the start policy is waiting for.

With `StartAndReturnAfterStarted`, a queue call waits until the worker actually starts. If concurrency defers the worker, the queue call waits while the worker remains queued. With `StartAndReturnAfterCompleted`, the queue call waits through deferred start and execution.

With `DoNotStart`, queueing accepts the worker but leaves it queued. Concurrency capacity is reserved when the worker is started, not when the worker is accepted.

Manual start honors `OverrideBehavior`.

- `Strict` requires capacity before a manual start can move the worker to execution.
- `Flexible` allows a manual start to override capacity.

## Idempotency And Recurrence

Idempotency uses `WorkSubjectId` to prevent duplicate work for the same definition and subject. A recurring worker keeps the same worker identity and subject across iterations, including while it is `Waiting`.

When idempotency is enabled, a recurring worker blocks another worker with the same definition and subject while it is queued, running, waiting, pausing, paused, canceling, completed, or failed. A canceled worker does not block a new worker for the same subject. In memory, an interrupted worker also does not block a new worker after restart; with durable queueing, the retained durable row still represents replayable work until that row is completed or explicitly canceled.

## Durable Queue And Idempotency

Durable queueing persists accepted work before returning from enqueue. It does not imply duplicate-subject protection. Idempotency is controlled only by idempotency configuration.

When durable queueing and persistence-backed idempotency are both enabled, the persistence provider owns the duplicate check. SQL Server stores durable queue data and the idempotency reservation in the same `workable.WorkEntries` row, protected by a filtered unique index on work system, definition, and subject.

SQL Server persistence-backed idempotency is implemented and usable with or without durable queueing. Without durable queueing, it writes an idempotency-only row. With durable queueing, the durable queue insert also creates the idempotency reservation in the same database write. Completed and explicitly canceled workers delete the row; failed workers keep the row for inspection and duplicate protection until they are completed or canceled.

Caller-owned enqueue transactions are only supported with durable queueing. If persistence-backed idempotency is enabled without `QueueDurably()`, a queue request that supplies a persistence transaction is rejected because Workable would otherwise have to materialize and possibly start the in-memory worker before the idempotency reservation transaction commits.

Queue durable work without idempotency for at-least-once acceptance:

```csharp
configuration.QueueDurably();
```

Add persistence-backed idempotency when durable acceptance should reject duplicate subjects:

```csharp
configuration
    .QueueDurably()
    .RejectDuplicateSubjects(WorkIdempotencyStorage.Persistence);
```

Durable queueing rejects local idempotency when idempotency is enabled. A local duplicate check cannot coordinate with durable rows written by another process or by the same process after restart.

## Durable Queue And Concurrency

Local concurrency coordinates inside one work system process. It is still useful for ordinary in-memory work and for single-process durable workers.

Persistence-backed concurrency is for durable work shared by multiple runtimes. SQL Server enforces the limit while claiming durable queue rows, before the worker is materialized in memory. That means competing servers sharing the same work system name, definition name, and concurrency scope cannot both start work for a full concurrency group.

The supported persistence-backed concurrency shape is:

- `QueueDurably()` must be enabled.
- `storage: WorkConcurrencyStorage.Persistence` must be selected.
- `blockingMode` must be `WorkConcurrencyBlockingMode.WhileExecuting`.
- `limitReachedBehavior` must be `WorkConcurrencyLimitReachedBehavior.DeferStart`.

```csharp
configuration
    .QueueDurably()
    .LimitConcurrency(
        maximumCapacity: 1,
        scope: WorkConcurrencyScope.PerConcurrencyKey,
        blockingMode: WorkConcurrencyBlockingMode.WhileExecuting,
        limitReachedBehavior: WorkConcurrencyLimitReachedBehavior.DeferStart,
        storage: WorkConcurrencyStorage.Persistence);
```

Completed workers and explicitly canceled workers release durable concurrency by deleting the durable row. Failed workers are retained for inspection, but they do not continue holding `WhileExecuting` persistence-backed capacity after failure retention clears the execution bucket. Shutdown interruption is not explicit cancellation; interrupted durable work remains replayable after its lease expires.

## Durable Completion And Transactions

`CompleteDurably()` is an opt-in guarantee for work whose business data and Workable completion record must commit together.

Workable does not create the developer's transaction. Executor code creates the transaction, performs business writes, calls `IWorkExecutionContext.CompleteDurably(...)` with the persistence transaction, then commits. If the transaction rolls back, the business write and Workable durable completion roll back together.

If durable completion is enabled and executor code returns success without calling `CompleteDurably(...)`, Workable fails the execution instead of marking it completed. That failure is intentional because otherwise the durable row could be replayed even though the in-memory execution reported success.

Durable completion requires durable queueing or persistence-backed idempotency, and it is not supported for recurring work.

## Retention And Failure

Retention applies to final workers. Final workers are `Completed` and `Canceled`, so automatic purge uses the configured retention interval and asynchronously enforced final-worker count targets for those states.

Failed and interrupted workers remain available for inspection and control because they are not final. Failed workers can be started again or canceled. Interrupted durable work can be replayed by the durable queue reader after lease expiry, or explicitly canceled through the worker API when it is materialized. Neither state is automatically purged by final-worker retention.

## Logging And Service Lifetimes

Workable captures logs through decorated `ILogger<>` instances while worker execution is active. Executors and scoped or transient services created for that execution receive the decorated logger and can publish `worker.log` events.

Services that already hold a logger created outside worker execution keep that logger instance. If that logger is used later during worker execution and it is the Workable-decorated logger, it can still observe the active worker context. If it is a logger instance created outside Workable's service-provider decoration, Workable does not capture it.

## Reconfiguration And Worker Versions

Runtime reconfiguration requires the current `WorkerVersion`. Any state-changing operation can advance the worker revision, including lifecycle transitions, actions, completion, and reconfiguration.

When a caller receives a conflict outcome, it should read the worker again and decide whether the requested reconfiguration still makes sense for the current state and version.
