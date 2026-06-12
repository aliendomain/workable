# Workable SQL Server Integration

`Workable.SqlServer` provides SQL Server persistence for durable queueing, persistence-backed idempotency, and persistence-backed concurrency.

See also:

- [Documentation Index](../../docs/README.md)
- [Getting Started](../../docs/guides/getting-started.md)
- [Queue Durability Configuration](../../docs/guides/configuration/queue-durability.md)
- [Configuration Interactions](../../docs/guides/configuration/interactions.md)

## Runtime Configuration

```csharp
services.AddWorkableSqlServerDurableQueue(
    connectionString,
    schemaName: "workable");
```

By default, the SQL Server integration auto-deploys the required schema when the Workable system starts. Startup fails if SQL Server rejects the deployment because of permissions, connectivity, or another SQL error.

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
});
```

When worker profiling is enabled, the SQL Server integration can also capture `Microsoft.Data.SqlClient` command execution as profile timing nodes:

```csharp
services.AddWorkableSqlServerProfiling();
```

This registration is separate from `AddWorkableSqlServerDurableQueue(...)`. Configuring SQL profiling only makes SQL capture available; Workable still emits SQL nodes only for workers whose profiling is enabled. The profiling hook listens at the provider layer, so it covers direct `SqlConnection` / `SqlCommand` usage and any higher-level data access code that ultimately executes through `Microsoft.Data.SqlClient`. Captured SQL metadata includes the operation kind, full SQL statement text, command shape, and parameter names and values. Obvious secret-like parameter names such as `password` or `accessToken` are redacted automatically. Binary parameter values are emitted as full hex string literals.

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

Persistence-backed concurrency is enforced during durable queue claiming. Configure persistent coordination with `CoordinatePersistently()`, enable durable queueing, and use `WhileExecuting` blocking with `DeferStart` limit behavior when multiple runtimes share the same SQL Server queue.

The queue reader is signal-first. Durable enqueues that Workable commits itself wake the local reader, which coalesces bursts briefly and drains ready rows in batches of 100 until the queue is empty. A fallback poll remains for rows committed by another process or by a caller-owned transaction. Configure that fallback per durable work definition:

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

Executor code can tell interruption from explicit cancellation by checking `IWorkExecutionContext.IsInterrupted` when the execution `CancellationToken` is canceled. `IWorkExecutionContext.InterruptionReason` distinguishes shutdown interruption from a lost durable queue lease.

SQL Server treats `LeaseId` as a fencing token. Lease renewal, failed-row retention, and final cleanup all require the current lease id for queue-backed work. If renewal or cleanup affects no rows, the integration reports a lost lease and Workable interrupts the local worker with `WorkInterruptionReason.LeaseLost` instead of letting stale execution finalize a row claimed by another runtime.

## Schema CLI

The SQL Server integration includes a CLI project at `tools/Workable.SqlServer.Cli`.

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

The scanner looks for durable queue configuration, persistence-backed idempotency configuration, and persistence-backed concurrency configuration. If connection strings or schema names are supplied dynamically through application configuration, pass them to the CLI explicitly; literal `AddWorkableSqlServerDurableQueue("...", schemaName: "...")` values can be discovered automatically.

You can also provide the connection string with `WORKABLE_SQLSERVER_CONNECTION_STRING`:

```powershell
$env:WORKABLE_SQLSERVER_CONNECTION_STRING="Server=(localdb)\MSSQLLocalDB;Database=Workable;Integrated Security=true;TrustServerCertificate=true"
dotnet run --project apps\tools\Workable.SqlServer.Cli -- schema apply --schema workable
```

The generated and applied schema are produced by the same `WorkableSqlServerSchema` helper used by the runtime initializer, so the CLI and runtime stay in sync.
