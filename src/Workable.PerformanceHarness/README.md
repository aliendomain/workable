# Workable Performance Harness

This is an opt-in runner for maintainers working on Workable runtime, query, view, and realtime internals. It is not part of the normal test suite because the output is timing-oriented and machine-dependent.

Run it from the repository root:

```powershell
dotnet run --project src\Workable.PerformanceHarness --configuration Release -- --workers 1000 --view-subscriptions 6 --view-iterations 50
```

Useful quick run:

```powershell
dotnet run --project src\Workable.PerformanceHarness --configuration Release -- --workers 100 --view-subscriptions 3 --view-iterations 10
```

Durable queueing with SQL Server LocalDB and persistence-backed idempotency:

```powershell
dotnet run --project src\Workable.PerformanceHarness --configuration Release -- --queue-mode durable-idempotent --workers 1000 --parallelism 16
```

Durable queueing without idempotency requirements:

```powershell
dotnet run --project src\Workable.PerformanceHarness --configuration Release -- --queue-mode durable-non-idempotent --workers 1000 --parallelism 16
```

The harness queues simple workers while concurrently recomputing overview component views. It reports worker throughput, completion latency, overview view latency, serialized payload size, and read-model diagnostics such as pending update count and projection duration.

The durable modes use SQL Server LocalDB by default:

```text
Server=(localdb)\MSSQLLocalDB;Database=WorkablePerformanceHarness;Integrated Security=true;TrustServerCertificate=true
```

The harness creates that database when possible, deploys the `workable_perf` schema, and deletes existing durable rows before each run by default. Override those with `--durability-connection-string`, `--durability-schema`, and `--durability-reset-store false`.

Use `--help` to see all options.
