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
- `durable-worker-claim-isolation` preloads durable SQL rows outside the runtime, starts a durable worker host, and isolates startup claim/materialization time, read-model catchup, claim diagnostics, and remaining durable-row counts.
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
- `BaselineActorWorkerQueryBenchmarks` measures exact originating-actor queries at approximately 100% and 1% selectivity across the same worker-count scale.
- `BaselineReadModelPublishBenchmarks` measures the cost of flushing one new worker update into already-large read-model snapshots at 100, 5,000, and 25,000 queued workers.
- `BaselineAuthorizedBulkActionBenchmarks` measures authorized `ExecuteAll(Cancel)` over queued workers at 100, 1,000, and 5,000 workers.
- `BaselineDurableLifecycleBenchmarks` measures representative SQL-backed queue, complete, queued-start action, and caller-owned transaction commit/notify paths.
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
- `BaselineAuthorizationResolutionBenchmarks` isolates matching-snapshot session creation, mismatched-snapshot fallback resolution, and access description, including allocation counts for the asynchronous authorization entry points.
- `BaselineIdempotencyBenchmarks` measures persistence-backed idempotency acceptance, duplicate rejection, and duplicate contention.
- `BaselineProfilingAdmissionBenchmarks` measures bounded automatic-node omission accounting, temporary full-capture rule misses at zero and maximum active rules, the worst case where 1,000 matching rules have pending exhausted leases, and completion of 1,000 one-shot rules.
- `BaselineProfilingHttpBenchmarks` measures the process-wide listener tax, admitted and post-cap HTTP activity overhead, concurrent HTTP sampling at the profile cap, and the actual admitted-request path with a 1,000,000-character URI.
- `BaselineProfilingHttpUriBenchmarks` measures sanitized URI capture at 128, 32,768, and 1,000,000 path characters.
- `BaselineProfilingFinalizationBenchmarks` measures profile publication with zero, 100, and 1,000 settled pending instrumentation operations.
- `BaselineProfilingSnapshotBenchmarks` measures large flat and deeply nested profile snapshot publication plus bounded deep-tree text rendering.
- `BaselineProfilingTeardownBenchmarks` measures unregistering one system while a shared HTTP observer tracks active requests for eight systems.
- `BaselineSqlProfilingBenchmarks` measures successful, failed, and unsupported-value SQL profile context capture.
- `BaselineSqlProfilingListenerBenchmarks` measures SqlClient event admission with no SQL listener, outside a Workable profile, and inside an eligible Workable profile. It batches 50,000,000 checks per invocation and reports per-check cost.
- `BaselineIterationStatusReplayBufferBenchmarks` compares the original front-removal replay buffer with indexed eviction and isolates aggregate payload-byte accounting overhead.
- `BaselineIterationStatusPublishBenchmarks` measures actual publication across payload sizes and subscriber counts.
- `BaselineIterationStatusConcurrencyBenchmarks` measures parallel publication through one system stream and independent system streams.
- `BaselineIterationStatusSystemRetentionBenchmarks` measures steady-state publication at the full system replay limit across 4,096 to 65,536 iteration buffers.
- `BaselineIterationStatusReplayBenchmarks` measures completed-stream replay for short resume windows and the full default buffer.
- `StressMillionWorkerQueryBenchmarks` measures broad and indexed first-page queries over 1,000,000 queued workers. This benchmark is intentionally excluded from the default filter.

### Profiling optimization comparison

The profiling performance pass on 2026-08-04 used the same `MediumRun` BenchmarkDotNet cases before and after the implementation changes on an Apple M5 Max with .NET 10.0.8. The most representative results were:

| Case | Before | After | Change |
| --- | ---: | ---: | ---: |
| Admitted HTTP request | 2.894 μs, 5.75 KB | 2.600 μs, 5.41 KB | 1.11x faster |
| Sanitized URI, 128-character path | 385.4 ns, 904 B | 385.2 ns, 904 B | unchanged common path |
| Sanitized URI, 32,768-character path | 50.21 μs, 132.41 KB | 6.82 μs, 28.27 KB | 7.36x faster |
| Sanitized URI, 1,000,000-character path | 1.697 ms, 3.82 MB | 6.82 μs, 28.27 KB | 249x faster |
| Miss across 1,000 exhausted matching capture rules | 10.72 μs | 85.88 ns | 125x faster |
| Miss with 1,000 unrelated capture rules | 73.98 ns | 67.82 ns | 1.09x faster |
| Render a 1,000-level profile | 727.6 μs, 8.88 MB | 100.7 μs, 667.33 KB | 7.23x faster |
| Unregister one of eight active systems | 64.18 μs, 24.02 KB | 22.73 μs, 8.06 KB | 2.82x faster |
| Finalize 1,000 settled pending operations | 85.76 μs, 8.11 KB | 81.91 μs, 8.11 KB | no regression; the profile registration drain no longer spins |

The listener-only and post-cap measurements remained within run-to-run variance. Snapshot materialization for 10,000 flat nodes and 5,000 nested scopes also remained effectively unchanged; the rendering improvement comes from applying explicit output bounds rather than changing immutable snapshot contents.

A follow-up pass on the same machine measured the remaining automatic-capture hot paths with identical before/after cases:

| Case | Before | After | Change |
| --- | ---: | ---: | ---: |
| Complete one of 1,000 one-shot capture rules | 15.32 μs, 41.81 KB | 224.2 ns, 40 B | 68.3x faster; allocations reduced by more than 99.9% |
| Admitted HTTP request with 1,000,000-character URI | 46.89 μs, 33.23 KB | 11.93 μs, 33.3 KB | 3.93x faster; source inspection is bounded |
| Successful representative SQL context | 8.694 μs, 75.73 KB | 9.137 μs, 27.8 KB | latency within run variance; 63.3% less allocation |
| Failed representative SQL context | 10.057 μs, 84 KB | 8.975 μs, 30.07 KB | 1.12x faster; 64.2% less allocation |
| Unsupported value with 100,000-character whitespace statement | 33.085 μs, 3.51 KB | 13.995 μs, 3.51 KB | 2.36x faster |
| SQL event check outside a Workable profile | enabled, 1.6698 ns | rejected, 3.3223 ns | adds a 1.65 ns admission check and prevents provider payload emission |
| SQL event check inside an eligible profile | enabled, 1.7145 ns | enabled, 5.4372 ns | adds 3.72 ns only while SQL profiling is active |

The SQL-listener benchmark measures only the provider's `IsEnabled` decision. Its outside-profile improvement is the change from an enabled event to a rejected event: the slightly more expensive predicate prevents SqlClient from constructing and publishing the much larger diagnostic payload. Focused tests assert the enabled/rejected states in addition to the timing benchmark.

### Iteration status stream comparison

The iteration status stream performance review on 2026-08-07 ran on an Apple M5 Max with .NET 10.0.8 and BenchmarkDotNet 0.15.6. The replay-buffer comparison isolates the original `List.RemoveRange(0, ...)` eviction algorithm from indexed eviction; the remaining cases exercise the production stream implementation.

| Replay-buffer append case | Original front removal | Indexed item eviction | Indexed item + payload-byte eviction |
| --- | ---: | ---: | ---: |
| 256-item capacity, no payload | 25.157 ns | 3.734 ns | 3.177 ns |
| 256-item capacity, 1,024-byte payload accounting | 24.949 ns | 3.694 ns | 3.156 ns |
| 4,096-item capacity, no payload | 320.461 ns | 3.703 ns | 3.333 ns |
| 4,096-item capacity, 1,024-byte payload accounting | 319.901 ns | 3.527 ns | 3.307 ns |

At the default 4,096-item replay capacity, indexed eviction is approximately 97x faster than front removal. Aggregate payload-byte accounting did not produce a measurable regression relative to item-only indexed eviction, and all buffer cases allocated zero bytes per append.

The hardening pass added system-wide item and byte budgets, type accounting, subscription quotas, and per-iteration synchronization. Representative before/after production-stream measurements were:

| Case | Before hardening | After hardening | After allocation |
| --- | ---: | ---: | ---: |
| Publish without payload or subscribers | 38.04 ns | 55.08 ns | 128 B |
| Publish a 128-character payload to one subscriber | 75.47 ns | 96.14 ns | 448 B |
| Publish a 128-character payload to 16 subscribers | 174.52 ns | 192.77 ns | 568 B |
| Publish a 128-character payload to 256 subscribers | 1.874 us | 1.927 us | 2,488 B |
| Publish a 4,096-character payload to one subscriber | 530.38 ns | 603.01 ns | 8,384 B |
| Replay 64 retained statuses | 978.7 ns | 838.8 ns | 1.22 KB |
| Replay the full 4,096-item default buffer | 48.901 us | 41.533 us | 1.22 KB |

The system-budget counters add 17 ns to an empty, listener-free publication and progressively less relative overhead as payload or fanout work grows. Replay improved by 14-15% because reads now synchronize only on their iteration buffer.

The payload ceiling changed from 65,536 to 32,768 UTF-8 JSON bytes after the 64 KiB case showed a roughly 131 KB allocation and Gen 2 collections. At the new 32 KiB boundary, one-subscriber publication measured 3.040 us and 65,720 B with no Gen 2 collections; 256-subscriber publication measured 5.064 us and 67,760 B. Payload fanout remains approximately linear.

Parallel publication before and after replacing the single system lock with per-iteration locks was:

| Parallel publishers | Shared stream before | Shared stream after | Independent streams after |
| --- | ---: | ---: | ---: |
| 1 | 65.44 ns/status | 71.57 ns/status | 72.93 ns/status |
| 4 | 155.19 ns/status | 206.66 ns/status | 91.62 ns/status |
| 16 | 213.69 ns/status | 254.91 ns/status | 163.24 ns/status |

Per-iteration buffer mutation no longer passes through one broad stream lock. Publications still coordinate briefly around aggregate retention ordering and counters, adding 19-33% to the former shared-stream measurements in these short runs. The four-publisher hardened case remains approximately 4.8 million statuses per second.

A final high-cardinality review replaced the aggregate-limit scan with an ordered, compacting retention index. The production BenchmarkDotNet case publishes continuously while the system item budget is full:

| Active iteration buffers | Mean per publish | Allocation |
| ---: | ---: | ---: |
| 4,096 | 142.8 ns | 128 B |
| 16,384 | 278.1 ns | 128 B |
| 65,536 | 661.6 ns | 128 B |

The 65,536-buffer short run was noisy, with a 542.1 ns median, but remained sub-microsecond. An isolated old/new algorithm probe showed why the index matters: publication at 4,096, 16,384, and 65,536 buffers fell from 0.194 ms, 0.355 ms, and 2.076 ms with the full scan to 0.005 ms, 0.002 ms, and 0.003 ms with indexed retention. Normal publication pays a small fixed bookkeeping cost: the empty no-subscriber case moved from 44.75 ns to 92.83 ns, 4 KiB payload cases increased 6-13%, 32 KiB cases increased 3-7%, and high-fanout cases were effectively unchanged. This trades tens of nanoseconds on ordinary publication for eliminating an eventual millisecond-scale scan as historical iteration count grows.

Re-run these cases with:

```powershell
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release -- --benchmarks --filter *BaselineIterationStatus*
```

Experimental opt-in benchmarks:

- `ExperimentalSignalRConnectionBenchmarks` measures connection and watch/unwatch setup only. Use `--scenario signalr-fanout-matrix` for realtime delivery and fanout measurements.
