# Workable Performance Harness

This is an opt-in runner for maintainers working on Workable runtime, query, view, and realtime internals. It is not part of the normal test suite because the output is timing-oriented and machine-dependent.

Run it from the repository root:

```powershell
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release -- --workers 1000 --view-subscriptions 6 --view-iterations 50
```

Useful quick run:

```powershell
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release -- --workers 100 --view-subscriptions 3 --view-iterations 10
```

Durable queueing with SQL Server LocalDB and persistence-backed idempotency:

```powershell
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release -- --queue-mode durable-idempotent --workers 1000 --parallelism 16
```

Durable queueing without idempotency requirements:

```powershell
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release -- --queue-mode durable-non-idempotent --workers 1000 --parallelism 16
```

The harness queues simple workers while concurrently recomputing overview component views. It reports worker throughput, completion latency, overview view latency, serialized payload size, and read-model diagnostics such as pending update count and projection duration.

The durable modes use SQL Server LocalDB by default:

```text
Server=(localdb)\MSSQLLocalDB;Database=WorkablePerformanceHarness;Integrated Security=true;TrustServerCertificate=true
```

The harness creates that database when possible, deploys the `workable_perf` schema, and deletes existing durable rows before each run by default. Override those with `--durability-connection-string`, `--durability-schema`, and `--durability-reset-store false`.

Use `--help` to see all options.

## BenchmarkDotNet baselines

The project also includes focused BenchmarkDotNet baselines for the hot paths identified during performance review. These are separate from the scenario harness above.

Run the default baseline set:

```powershell
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release -- --benchmarks
```

The default `--benchmarks` command runs only benchmark classes whose names contain `Baseline`. That keeps the million-worker stress benchmark opt-in.

Run a single benchmark group:

```powershell
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release -- --benchmarks --filter *BaselineWorkerQuery*
```

Run the opt-in million-worker query stress benchmark:

```powershell
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release -- --benchmarks --filter *StressMillionWorkerQuery*
```

Current benchmark groups:

- `BaselineWorkerQueryBenchmarks` measures broad first-page worker queries, exact identifier-index queries, and identifier key-type facets at 100, 10,000, and 100,000 queued workers.
- `BaselineReadModelPublishBenchmarks` measures the cost of flushing one new worker update into already-large read-model snapshots at 100, 5,000, and 25,000 queued workers.
- `BaselineAuthorizedBulkActionBenchmarks` measures authorized `ExecuteAll(Cancel)` over queued workers at 100, 1,000, and 5,000 workers.
- `StressMillionWorkerQueryBenchmarks` measures broad and indexed first-page queries over 1,000,000 queued workers. This benchmark is intentionally excluded from the default filter.
