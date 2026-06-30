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

Write scenario metrics to CSV:

```powershell
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release -- --scenario all --csv-output .\artifacts\performance\scenario-baseline.csv
```

Repeat a scenario and include per-run plus min/median/max/mean/spread summary rows in the same CSV:

```powershell
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release -- --scenario signalr-fanout-matrix --workers 10000 --repeat-runs 5 --csv-output .\artifacts\performance\signalr-repeat.csv
```

Durable queueing with persistence-backed idempotency:

```powershell
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release -- --queue-mode durable-idempotent --workers 1000 --parallelism 16
```

Durable queueing without idempotency requirements:

```powershell
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release -- --queue-mode durable-non-idempotent --workers 1000 --parallelism 16
```

The harness queues simple workers while concurrently recomputing overview component views. It reports worker throughput, completion latency, overview view latency, serialized payload size, event-delivery behavior, read-model diagnostics, and selected retained-memory snapshots.

## Scenario catalog

In-memory scenario suite:

- `all` runs the full in-memory named scenario suite below. `scenarios-all` is accepted as a compatibility alias for existing benchmark notes.
- `queue-only` measures authoritative queue acceptance plus read-model catchup for queued workers that do not start.
- `dequeue-only` measures the handoff from `Start` action dispatch to executor start for already-queued workers.
- `start-to-completion` measures end-to-end queue-to-completion latency for workers that start immediately.
- `completion-only` measures starting and completing a prefilled queued set.
- `mixed-queue-complete` measures a balanced mix of queueing new queued workers while starting an equally sized prefilled queued set.
- `completion-while-queue-heavy` measures completion throughput while queue traffic dominates the mixed workload.
- `queue-while-completion-heavy` measures queue throughput while completion traffic dominates the mixed workload.
- `mixed-90-10` measures a mixed workload with roughly 90% queueing and 10% completion.
- `mixed-50-50` measures a mixed workload with roughly even queueing and completion work.
- `mixed-10-90` measures a mixed workload with roughly 10% queueing and 90% completion.
- `read-model-latency` measures one-update-at-a-time latency from queue acceptance to read-model application.
- `visibility-latency` measures how long it takes newly queued workers to become query-visible.
- `index-update-cost` measures queue throughput and read-model/index catchup cost for a large queued batch.
- `memory-growth` measures retained managed and private memory growth after queueing queued workers.
- `memory-release-after-purge` measures how much in-memory worker state is released after completed workers are explicitly purged.
- `event-fanout` measures steady-state publish and subscription fanout cost across subscription counts and filter shapes.
- `event-delivery` measures publish cost while subscriptions actively drain events and report delivered versus dropped counts.
- `change-stream-fanout` measures the intended state-watcher path with active `IWorkChangeStream` readers across subscription counts and reports delivered, coalesced, and dropped changes.
- `subscription-churn` measures subscription attach/detach throughput and latency.
- `subscription-memory-release` measures retained managed and private memory after large subscription sets are disposed.
- `publish-under-churn` measures worker publish/completion throughput while subscriptions are continuously created and removed.

Focused opt-in scenarios:

- `event-fanout-matrix` runs the same event fanout matrix logic as `event-fanout`, but as a dedicated single-scenario command for focused baselines.
- `signalr-fanout-matrix` measures realtime transport fanout with a dedicated Kestrel host, live SignalR connections, low-latency publish windows, warmup validation, and bounded delivery waits.
- `durable-worker-lifecycle-breakdown` measures SQL-backed durable worker admission, queue-to-executor-start latency, completion observation, read-model catchup, durability diagnostics, and durable row counts. Its `completed_per_sec` metric uses the observed window from the first queue start to the last completion observation so the reported throughput is stable even when completion observers have already drained by the time the harness awaits them. Run it with `--queue-mode durable-idempotent` or `--queue-mode durable-non-idempotent`.
- `durable-memory-release-after-purge` measures durable worker memory retention before purge, after purge-driven cleanup, and after a clean restart. Run it with `--queue-mode durable-idempotent` or `--queue-mode durable-non-idempotent`.
- `durable-workflow-memory-recovery` measures durable workflow memory retention across interrupted runs, recovery completion, and a clean restart. Run it with `--queue-mode durable-idempotent` or `--queue-mode durable-non-idempotent`.

Legacy lifecycle benchmark:

- `lifecycle-fanout` is the original mixed runtime/query benchmark path. It can run against in-memory and durable queue modes and reports worker lifecycle plus overview fanout in one pass.

Queue-mode rules:

- All named scenarios in the in-memory suite require `--queue-mode in-memory`.
- The durable focused scenarios require a durable queue mode.
- `lifecycle-fanout` is the only non-named scenario path that supports both in-memory and durable queue modes.

When durable queueing is enabled and `--durability-connection-string` is omitted, the harness uses the same SQL discovery behavior as the SQL Server integration tests:

- `WORKABLE_SQLSERVER_TEST_CONNECTION_STRING` wins when it is set.
- Otherwise the harness looks for `docker` or `podman`, starts or reuses the `workable-sqlserver-tests` SQL Server container, and connects through the published port. OrbStack works through its Docker-compatible CLI.
- `WORKABLE_SQLSERVER_TEST_CONTAINER_RUNTIME`, `WORKABLE_SQLSERVER_TEST_CONTAINER_IMAGE`, and `WORKABLE_SQLSERVER_TEST_CONTAINER_REUSE` are honored the same way as the integration test suite.

The harness deploys the `workable_perf` schema and deletes existing durable rows before each run by default. Override those with `--durability-connection-string`, `--durability-schema`, and `--durability-reset-store false`.

Use `--durable-enqueue-batch-size` and `--durable-enqueue-batch-window-ms` to tune SQL durable enqueue microbatching during admission sweeps. Batching is the default durable SQL enqueue path; these flags only change the size and coalescing window used by the run.

Use `--durable-claim-batch-size` to tune how many ready SQL durable queue rows each reader claim attempts to reserve before handing work back to the in-memory runtime.

Use `--durable-claim-sample-capacity` to retain a bounded set of recent per-claim diagnostic samples. The default is `0`, so detailed sampling is off unless a run explicitly enables it.

Use `--help` to see all options.

## BenchmarkDotNet baselines

The project also includes focused BenchmarkDotNet baselines for the hot paths identified during performance review. These are separate from the scenario harness above.

Run the default baseline set:

```powershell
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release -- --benchmarks
```

The default `--benchmarks` command runs only benchmark classes whose names contain `Baseline`. That keeps the million-worker stress benchmark opt-in.

BenchmarkDotNet writes CSV, GitHub-flavored Markdown, and HTML reports under `BenchmarkDotNet.Artifacts/results`.

The workflow microbenchmarks batch `4096` workflow runs inside each measured invocation and use `OperationsPerInvoke` so BenchmarkDotNet still reports per-workflow cost while ShortRun stays above the minimum-iteration guidance.

Realtime transport delivery is intentionally measured through the scenario runner rather than the default BenchmarkDotNet baseline set. The async SignalR delivery path is a better fit for the harness-style scenarios above because they can do explicit warmup, event-count validation, bounded end-to-end waits, and a dedicated low-latency host configuration.

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
- `BaselineDurableLifecycleBenchmarks` measures representative SQL-backed queue, complete, and queued-start action paths.
- `BaselineDurableSoakBenchmarks` measures larger SQL-backed queue, completion, and follow-up query batches to catch durable memory or latency regressions.
- `BaselineWorkflowDispatchBenchmarks` measures single-dispatch workflow startup and completion overhead and reports per-workflow cost from batched invocations.
- `BaselineWorkflowParallelJoinBenchmarks` measures parallel branch fan-out and join bookkeeping across branch counts and reports per-workflow cost from batched invocations.
- `BaselineDurableWorkflowRecoveryBenchmarks` measures startup recovery for interrupted durable workflow runs.
- `BaselineDurableChildReconnectBenchmarks` measures partial durable workflow recovery where only unfinished child branches should resume.
- `BaselineHttpApiBenchmarks` measures end-to-end HTTP queue, worker action, and workflow control requests.
- `BaselineHttpQueryBenchmarks` measures HTTP worker detail and worker-summary query routes over seeded data.
- `BaselineMcpBenchmarks` measures MCP workflow and worker control tool routing.
- `BaselineMcpQueryBenchmarks` measures MCP worker query, summary, and detail tools over seeded data.
- `BaselineAuthorizationBenchmarks` measures authorization-sensitive queue, query, and workflow execution paths.
- `BaselineIdempotencyBenchmarks` measures persistence-backed idempotency acceptance, duplicate rejection, and duplicate contention.
- `StressMillionWorkerQueryBenchmarks` measures broad and indexed first-page queries over 1,000,000 queued workers. This benchmark is intentionally excluded from the default filter.

Experimental opt-in benchmarks:

- `ExperimentalSignalRConnectionBenchmarks` measures connection and watch/unwatch setup only. Use `--scenario signalr-fanout-matrix` for realtime delivery and fanout measurements.
