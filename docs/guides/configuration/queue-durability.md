# Queue Durability

Queue durability persists accepted work outside the in-memory work process. Durable workers are written to a host-configured durable queue store first, then a queue reader claims committed rows and accepts them into the normal Workable worker pipeline.

For configuration source order, precedence, and override rules that apply to every configuration facet, see [Work Configuration](README.md).

`QueueDurably()` selects persistent coordination storage automatically. It does not enable idempotency by itself. `CompleteDurably()` enables transaction-bound completion and also requires persistent coordination.

## Settings

| Setting | Default | Description |
| --- | --- | --- |
| `IsEnabled` | `false` | Enables durable queue persistence for accepted work. |
| `CompleteDurably` | `false` | Requires executor code to complete the persisted Workable row inside a developer-owned transaction before returning success. |
| `FallbackPollingInterval` | `TimeSpan.FromSeconds(5)` | Polling interval used when durable work cannot be discovered by an immediate local signal. Must be at least one second when durable queueing is enabled. |

## Related Queue-Time Option

| Setting | Default | Description |
| --- | --- | --- |
| `WorkerOptions.QueueDurabilityTransaction` | `null` | Optional caller-owned persistence transaction that lets durable enqueue acceptance join the caller's transaction. |

## Attribute Configuration

`WorkQueueDurabilityAttribute` declares default durable queue behavior on the executor type.

```csharp
[WorkQueueDurability]
public sealed class CapturePaymentWork : IWorkExecutor
{
    public Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());
}
```

`WorkQueueDurabilityAttribute` enables durable queueing with the default fallback polling interval. Durable completion is not configured by attribute; use startup, queue-time, or worker reconfiguration for that.

## Startup Configuration

At startup, the same behavior can also be configured with the convenience methods `QueueDurably` and `CompleteDurably`, or the full `UseCoordination` setter.

```csharp
services.AddWorkableSystem(builder =>
{
    builder.AddWork<CapturePaymentWork>(
        WorkDefinition.Create(
            name: "orders.capture-payment",
            description: "Capture payment for an order.",
            category: "Orders"),
        configuration => configuration.UseCoordination(
            new WorkCoordinationConfiguration
            {
                IsEnabled = true,
                Storage = WorkCoordinationStorage.Persistent,
                Durability = new WorkQueueDurabilityConfiguration
                {
                    IsEnabled = true,
                    CompleteDurably = false,
                    FallbackPollingInterval = TimeSpan.FromSeconds(5),
                },
            }));
});
```

## Queue-Time Configuration

```csharp
IWorkQueueDurabilityTransaction queueTransaction = ...;

var handle = await system.Queue.Enqueue(
    "orders.capture-payment",
    input: WorkInput.FromValue(new CapturePayment("order-123")),
    options: new WorkerOptions(
        Configuration: WorkConfiguration.Default with
        {
            Coordination = WorkCoordinationConfiguration.Default with
            {
                IsEnabled = true,
                Storage = WorkCoordinationStorage.Persistent,
                Durability = new WorkQueueDurabilityConfiguration
                {
                    IsEnabled = true,
                    CompleteDurably = false,
                    FallbackPollingInterval = TimeSpan.FromSeconds(5),
                },
            },
        },
        QueueDurabilityTransaction: queueTransaction));
```

## Reconfiguration

```csharp
var worker = await system.Query.Worker(workerId)
    ?? throw new InvalidOperationException("Worker was not found.");

var outcome = await system.Workers.Reconfigure(
    worker.Version,
    new WorkerReconfiguration(
        Coordination: WorkCoordinationConfiguration.Default with
        {
            IsEnabled = true,
            Storage = WorkCoordinationStorage.Persistent,
            Durability = new WorkQueueDurabilityConfiguration
            {
                IsEnabled = true,
                CompleteDurably = true,
                FallbackPollingInterval = TimeSpan.FromSeconds(5),
            },
        }));
```

## Operational Notes

The durable queue reader is signal-first. When this process accepts durable work without a caller-owned transaction, it wakes its local reader, waits briefly to coalesce bursty enqueue calls, then drains the database queue until empty. Each drain claims batches of up to 100 rows, so a long durable backlog is pulled into memory batch-by-batch instead of one row at a time.

Polling remains as the cross-process and caller-transaction fallback. If another process enqueues work, or if the enqueue joined a transaction supplied by the caller, readers discover the row on the fallback polling interval after the transaction commits. The default fallback polling interval is five seconds and the minimum is one second.

Durable queueing does not imply idempotency. `QueueDurably()` persists the queue entry and gives at-least-once acceptance; execution remains recoverable with lease-based replay if a process stops before final cleanup. It also selects persistent coordination storage, so any enabled idempotency or concurrency feature in the same configuration is persistence-backed.

The database provider is configured separately from work configuration. SQL Server is provided by [`Workable.SqlServer`](../../../integrations/sqlserver/README.md):

```csharp
services.AddWorkableSqlServerDurableQueue(
    connectionString,
    schemaName: "workable");
```

## Durable Completion

Durable completion is an opt-in completion guarantee for work whose successful business write and Workable final cleanup must commit together.

`CompleteDurably()` does not create a transaction for executor code. The executor owns the transaction, performs its business writes, asks Workable to complete the persisted row inside that transaction, and then commits:

```csharp
await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync(cancellationToken);
await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

await using var command = connection.CreateCommand();
command.Transaction = transaction;
command.CommandText = "INSERT INTO dbo.Orders (OrderId) VALUES (@OrderId)";
// add parameters and execute the business write

await context.CompleteDurablyWithSqlServerTransaction(connection, transaction, cancellationToken);
await transaction.CommitAsync(cancellationToken);

return WorkExecutionResult.Success();
```

If a worker with durable completion enabled returns a successful result without calling `IWorkExecutionContext.CompleteDurably(...)`, Workable fails the execution instead of marking the worker completed. This keeps successful Workable completion tied to the transaction boundary the developer controls. If the transaction rolls back, the business write and Workable durable completion roll back together.

Durable completion requires a persisted Workable row to complete. That row can come from durable queueing, or from persistence-backed idempotency without durable queueing.

Durable completion is not supported for recurring work yet. A successful recurring iteration is not final worker completion, and a later external stop does not happen inside the executor's transaction.

## Shutdown, Cancellation, And Replay

API cancellation and system shutdown have different durability meanings.

Calling `WorkAction.Cancel` is an explicit final decision. A canceled worker reaches `WorkerState.Canceled`, records `WorkCompletionStatus.Canceled`, and the durable provider deletes the row.

Stopping a Workable system interrupts active work instead. Queued, running, waiting, and retrying workers move through `Interrupting` or `Interrupted`, record `WorkCompletionStatus.Interrupted`, and publish `worker.interrupted`. `Interrupted` is not final, so durable rows are not deleted. If the process stops before the work later completes or is explicitly canceled, another runtime can replay the row after its lease expires.

Executor code receives the normal `CancellationToken` for cooperative shutdown, and can distinguish interruption from API cancellation through `IWorkExecutionContext.IsInterrupted` and `IWorkExecutionContext.InterruptionReason`:

```csharp
public async Task<WorkExecutionResult> Execute(
    IWorkExecutionContext context,
    WorkInput? input,
    CancellationToken cancellationToken)
{
    try
    {
        await processor.Process(input, cancellationToken);
        return WorkExecutionResult.Success();
    }
    catch (OperationCanceledException) when (context.InterruptionReason == WorkInterruptionReason.Shutdown)
    {
        await processor.RecordInterruptedShutdown(input);
        throw;
    }
    catch (OperationCanceledException) when (context.InterruptionReason == WorkInterruptionReason.LeaseLost)
    {
        await processor.RecordLostDurableLease(input);
        throw;
    }
}
```

Failed durable work is also retained. A failed row remains available for inspection and retry until the worker is explicitly completed by restart or explicitly canceled through the worker API.

SQL Server stores durable queue payloads and idempotency reservations in `workable.WorkEntries`. A row can represent queueing only, idempotency only, or both:

- `IsDurableQueued = 1` means the durable queue reader can claim it.
- `HasIdempotencyReservation = 1` means the row participates in duplicate-subject rejection.

Rows are deleted when a worker reaches `Completed` or `Canceled`, or when the worker is purged. Failed and interrupted rows are retained. Until a retained row is completed or canceled, durable queue rows renew their claimed lease so another process does not replay active work; if the process dies, the lease expires and another runtime can claim the row.

Lease ids are fencing tokens. Renewal and final cleanup include the current `LeaseId`; if a provider reports that the lease no longer owns the row, Workable interrupts the local worker with `WorkInterruptionReason.LeaseLost`. Stale executions must not delete or retain a row that another runtime has already claimed.

## Related Interactions

- [Durable Queue And Idempotency](interactions.md#durable-queue-and-idempotency): durable queueing and persistence-backed idempotency can share one atomic database write.
- [Durable Queue And Concurrency](interactions.md#durable-queue-and-concurrency): persistence-backed concurrency is enforced before durable rows are materialized.
- [Durable Completion And Transactions](interactions.md#durable-completion-and-transactions): durable completion ties Workable final cleanup to developer-owned business transactions.
