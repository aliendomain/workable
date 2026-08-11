# Workable SQL Server Integration

`Workable.SqlServer` provides SQL Server persistence for durable queueing, persistence-backed idempotency, persistence-backed concurrency, durable workflow-run persistence, and expiring execution diagnostics.

See also:

- [Documentation Index](../../../docs/README.md)
- [Getting Started](../../../docs/guides/getting-started.md)
- [Persistent Execution Diagnostics](../../../docs/guides/configuration/execution-diagnostics-persistence.md)
- [Queue Durability Configuration](../../../docs/guides/configuration/queue-durability.md)
- [Configuration Interactions](../../../docs/guides/configuration/interactions.md)

## Runtime Configuration

Register SQL Server for persistent iteration logs, profiles, instrumentation summaries, and temporary capture rules without enabling durable queueing:

```csharp
services.AddWorkableSqlServerPersistence(
    connectionString,
    schemaName: "workable",
    persistenceScope: "my-application");
```

This is host-level service registration: one SQL connection/schema pair supplies the repository for all Workable systems in the service collection. The persistence scope and logical Workable system name form each system's stable, isolated query boundary across application restarts. See [Persistent Execution Diagnostics](../../../docs/guides/configuration/execution-diagnostics-persistence.md) for work/system policy, expiry, background writing, and query surfaces.

Durable queue registration also registers this diagnostics repository:

```csharp
services.AddWorkableSqlServerDurableQueue(
    connectionString,
    schemaName: "workable");
```

When both methods are used, they must identify the same SQL Server connection and schema. The explicit `AddWorkableSqlServerPersistence(...)` options supply the diagnostics persistence scope regardless of registration order; conflicting stores or conflicting explicit persistence configurations fail during service registration.

By default, the SQL Server integration auto-deploys the required schema when the Workable system starts. Execution diagnostics uses the shared `SchemaVersion` table with its own component version, separate from queue durability and workflow persistence. Startup fails if SQL Server rejects the deployment because of permissions, connectivity, or another SQL error.

Disable runtime schema deployment when schema changes are managed outside the app:

```csharp
services.AddWorkableSqlServerDurableQueue(
    connectionString,
    schemaName: "workable",
    autoDeploySchema: false);
```

When `autoDeploySchema` is `false`, startup validates that the required schema is already installed and fails if it is missing or incomplete.

The package also exposes an options-object overload when the host wants to bind the integration from configuration:

```csharp
services.AddWorkableSqlServerDurableQueue(new WorkableSqlServerQueueDurabilityOptions
{
    ConnectionString = connectionString,
    SchemaName = "workable",
    AutoDeploySchema = true,
    ClaimBatchSize = WorkableSqlServerQueueDurabilityOptions.DefaultClaimBatchSize,
});
```

When worker profiling is enabled, the SQL Server integration can also capture `Microsoft.Data.SqlClient` command execution as profile timing nodes:

```csharp
services.AddWorkableSqlServerProfiling();
```

This registration is separate from `AddWorkableSqlServerDurableQueue(...)`. Configuring SQL profiling only makes SQL capture available; Workable still emits SQL nodes only for workers whose profiling is enabled. One shared provider observer serves all started Workable systems in the host. It covers direct `SqlConnection` / `SqlCommand` usage and higher-level data access code that ultimately executes through `Microsoft.Data.SqlClient`.

Captured SQL metadata includes the operation kind, bounded SQL statement text, command shape, and bounded parameter previews. Binary parameter values are never retained; their metadata is shown with a `<binary omitted>` value marker and `IsBinaryOmitted` flag. Obvious secret-like parameter names such as `password` or `accessToken` are redacted automatically. Statements are inspected and retained only up to 8,192 characters, at most 32 parameters are retained, aggregate parameter context is limited to approximately 4,096 characters, individual text values are limited to 1,024 characters, exception messages to 1,024 characters, and individual metadata fields to 512 characters. Unsupported application-defined parameter values are represented by a type placeholder without invoking application `ToString()` code. A failure adds its bounded exception details to the original timing node without duplicating the command context. Truncation is reported in the captured context. Commands still active when the worker profile is published are finalized as `Incomplete`. SqlClient command-start diagnostics are disabled outside an eligible Workable profile; completion events remain enabled only while a profiled command is outstanding or an eligible profile is active. See [Work Profiling](../../../docs/concepts/profiling.md#automatic-sql-client-timing) for the complete capture, privacy, and limit behavior.

## Integration Tests

The SQL Server integration test project at `tests/extensions/sqlserver/Workable.SqlServer.Tests` is cross-platform.

- Set `WORKABLE_SQLSERVER_TEST_CONNECTION_STRING` to point at an existing SQL Server instance when you want to manage the database host yourself.
- Or create `tests/extensions/sqlserver/Workable.SqlServer.Tests/appsettings.local.json` with a `Workable:SqlServerTests:ConnectionString` value when you want a local file instead of an environment variable.
- Otherwise the test fixture auto-starts a local SQL Server 2022 container through `docker` or `podman` and reuses it across runs.
- The managed container is named `workable-sqlserver-tests`.
- Set `WORKABLE_SQLSERVER_TEST_CONTAINER_REUSE=false` when you want the fixture to stop containers it creates for the current run instead of keeping them warm for reuse.

That means the same SQL persistence suite can run on Windows, macOS, Linux, and CI runners that have a Docker-compatible runtime available.

The test project includes `appsettings.local.example.json` with a LocalDB-shaped connection string. On Windows, a convenient local setup is:

1. Copy `tests/extensions/sqlserver/Workable.SqlServer.Tests/appsettings.local.example.json` to `tests/extensions/sqlserver/Workable.SqlServer.Tests/appsettings.local.json`.
2. Start LocalDB with `sqllocaldb start MSSQLLocalDB`.
3. Run the SQL integration tests outside the Codex sandbox.

The SQL integration tests create and drop temporary databases on the target instance, so the configured login must be able to connect to `master` and execute `CREATE DATABASE` / `DROP DATABASE`.

## Durable Queue Runtime Behavior

The SQL Server durable queue writes accepted durable workers to the configured schema before returning from enqueue. Without a caller transaction, the integration commits its own insert before returning. With `WorkerOptions.WithSqlServerQueueDurabilityTransaction(connection, transaction)`, the insert participates in the caller's transaction and the queue reader cannot claim the work until that transaction commits. This caller-owned enqueue transaction path requires `QueueDurably()`; persistence-backed idempotency without durable queueing rejects queue requests that supply the queue durability transaction option.

Durable workflows use the same SQL Server store for workflow-run snapshots. Workable initializes workflow persistence support only when a system has durable workflow definitions, persists durable workflow runs in `workable.WorkflowRuns`, scopes recovery by the configured Workable system name, persists accepted workflow pause and cancel requests on that run snapshot, persists retained child completion receipts in `ChildReceiptsJson`, materializes durable workflow runs for the same persistence scope during system startup, auto-resumes recovered blocked runs when their outstanding failed child workers have already completed successfully, batches outstanding-child existence checks into one set-based query, and uses one SQL transaction to advance the workflow-run snapshot and enqueue durable child workers at each durable dispatch boundary.

Persistence-backed concurrency is enforced during durable queue claiming. Configure persistent coordination with `CoordinatePersistently()`, enable durable queueing, and use `WhileExecuting` blocking with `DeferStart` limit behavior when multiple runtimes share the same SQL Server queue.

The queue reader is signal-first. Durable enqueues that Workable commits itself wake the local reader, which coalesces bursts briefly and drains ready rows in configurable batches of 7,500 by default until the queue is empty. After committing a caller-owned transaction, explicitly notify the queue used to enqueue the work so the local reader can start it promptly:

```csharp
var handle = await session.Queue.Enqueue(
    "orders.capture-payment",
    input,
    WorkerOptions.Default.WithSqlServerQueueDurabilityTransaction(connection, transaction),
    cancellationToken);

await transaction.CommitAsync(cancellationToken);
session.Queue.NotifyDurableWorkAvailable();
```

Call the notification only after `CommitAsync` succeeds. Notifications are synchronous and safe to repeat; calls are coalesced while one wake remains pending, but a later call can schedule another drain after the reader consumes that wake. Treat this as a trusted in-process hint rather than exposing it directly to untrusted clients. Notifications wake only the local runtime; rows committed by another process and missed notifications remain covered by fallback polling. Waiting on the returned worker handle issues one immediate notification but does not lower the configured polling interval. Configure that fallback per durable work definition:

```csharp
configuration.QueueDurably(fallbackPollingInterval: TimeSpan.FromSeconds(5));
```

The default fallback polling interval is five seconds. The minimum supported interval is one second.

Durable completion is opt-in for work that needs successful business writes and Workable's final durable cleanup to commit together. It is usually paired with durable queueing:

```csharp
configuration.QueueDurably().CompleteDurably();
```

When durable completion is enabled, executor code must create the SQL transaction and explicitly complete Workable's durable row inside that transaction before committing it:

```csharp
await using var connection = new SqlConnection(connectionString);
await connection.OpenAsync(cancellationToken);
await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

await using var command = connection.CreateCommand();
command.Transaction = transaction;
command.CommandText = "INSERT INTO dbo.Orders (OrderId) VALUES (@OrderId)";
// add parameters, then execute

await context.CompleteDurablyWithSqlServerTransaction(connection, transaction, cancellationToken);
await transaction.CommitAsync(cancellationToken);
return WorkExecutionResult.Success();
```

`CompleteDurablyWithSqlServerTransaction` deletes Workable's durable row using the same lease-fenced transaction. If executor code returns a successful result without calling durable completion, Workable fails the execution instead of marking the worker completed. If the transaction rolls back, the business writes and Workable's durable completion roll back together.

Completion cleanup is intentional:

- `Completed` and explicit API `Canceled` workers delete their durable row.
- `Failed` workers keep their row for inspection and later retry or explicit cancellation.
- Shutdown-interrupted workers keep their row. Workable records `WorkerState.Interrupted`, publishes `worker.interrupted`, and allows another runtime to replay the row after its lease expires.

Durable workflow-run cleanup is separate from durable worker-row cleanup. Durable workflow snapshots remain available for startup materialization while their run lifetime is still active. When a workflow reaches a final state, Workable keeps the persisted workflow row until the run no longer has any retained child workers. Completed child workers can age out independently because the workflow run already retains the child completion facts it needs for joins and operator views.

Executor code can tell interruption from explicit cancellation by checking `IWorkExecutionContext.IsInterrupted` when the execution `CancellationToken` is canceled. `IWorkExecutionContext.InterruptionReason` distinguishes shutdown interruption from a lost durable queue lease.

SQL Server treats `LeaseId` as a fencing token. Lease renewal, failed-row retention, and final cleanup all require the current lease id for queue-backed work. If renewal or cleanup affects no rows, the integration reports a lost lease and Workable interrupts the local worker with `WorkInterruptionReason.LeaseLost` instead of letting stale execution finalize a row claimed by another runtime.

## Schema CLI

The SQL Server integration includes a CLI project at `apps/tools/Workable.SqlServer.Cli`.

`schema generate` emits the complete current schema for a fresh installation. `schema apply` and runtime auto-deployment first inspect the component versions in `SchemaVersion`: an empty database receives the current schema directly, while a versioned database runs only the ordered migrations newer than its installed queue, workflow, or diagnostics version. A database containing Workable tables without version metadata is rejected as an ambiguous legacy or partial deployment instead of being silently treated as fresh.

Generate the schema script:

```powershell
dotnet run --project apps\tools\Workable.SqlServer.Cli -- schema generate --schema workable --output workable.sql
```

Print the schema script to stdout:

```powershell
dotnet run --project apps\tools\Workable.SqlServer.Cli -- schema generate --schema workable
```

Apply the schema directly:

```powershell
dotnet run --project apps\tools\Workable.SqlServer.Cli -- schema apply --connection-string "Server=(localdb)\MSSQLLocalDB;Database=Workable;Integrated Security=true;TrustServerCertificate=true" --schema workable
```

Generate the schema only when a project or solution contains Workable configuration that needs SQL Server persistence:

```powershell
dotnet run --project apps\tools\Workable.SqlServer.Cli -- schema generate --solution .\Workable.slnx --schema workable --output workable.sql
```

Deploy after scanning a project or solution:

```powershell
dotnet run --project apps\tools\Workable.SqlServer.Cli -- schema apply --project .\src\MyApp\MyApp.csproj --connection-string "Server=(localdb)\MSSQLLocalDB;Database=Workable;Integrated Security=true;TrustServerCertificate=true" --schema workable
```

`--project` can be repeated, and `--solution` scans all non-test projects in the solution. Pass `--include-tests` when a test project is intentionally part of the scan. `schema apply` also accepts repeated `--connection-string` values so the same detected schema requirements can be deployed to multiple databases.

The scanner looks for durable queue configuration, persistence-backed idempotency configuration, persistence-backed concurrency configuration, durable workflow configuration, and execution-diagnostics persistence registration or work configuration. If connection strings or schema names are supplied dynamically through application configuration, pass them to the CLI explicitly; literal `AddWorkableSqlServerPersistence("...", schemaName: "...")` and `AddWorkableSqlServerDurableQueue("...", schemaName: "...")` values can be discovered automatically.

You can also provide the connection string with `WORKABLE_SQLSERVER_CONNECTION_STRING`:

```powershell
$env:WORKABLE_SQLSERVER_CONNECTION_STRING="Server=(localdb)\MSSQLLocalDB;Database=Workable;Integrated Security=true;TrustServerCertificate=true"
dotnet run --project apps\tools\Workable.SqlServer.Cli -- schema apply --schema workable
```

The generated current schema and the ordered upgrade migrations are owned by the same `WorkableSqlServerSchema` helper used by the runtime initializer, so the CLI and runtime stay in sync.
