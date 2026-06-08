# Backend Performance Audit

Date: 2026-06-01

## Benchmark Utility

Location: `apps/tools/Workable.PerformanceHarness`

Primary scenario command used for this audit:

```powershell
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release --no-build -- --scenario all --workers 2000 --parallelism 32 --work-delay-ms 0 --view-subscriptions 8 --view-iterations 1 --warmup-workers 0 --warmup-views 0 --serialize-payloads false
```

Build command used before runs:

```powershell
dotnet build apps\tools\Workable.PerformanceHarness\Workable.PerformanceHarness.csproj --configuration Release --no-restore
```

The scenario harness now supports:

- `queue-only`
- `start-to-completion`
- `completion-only`
- `mixed-queue-complete`
- `completion-while-queue-heavy`
- `queue-while-completion-heavy`
- `mixed-90-10`, `mixed-50-50`, `mixed-10-90`
- `read-model-latency`
- `visibility-latency`
- `index-update-cost`
- `memory-growth`
- `event-fanout`
- `event-fanout-matrix`

`--scenario lifecycle-fanout` preserves the prior combined lifecycle/view runner. Named scenarios currently target the in-memory backend; durable queue modes remain available through the legacy lifecycle scenario.

Event fanout matrix command used for the subscriber follow-up pass:

```powershell
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release --no-build -- --scenario event-fanout-matrix --workers 2000 --parallelism 32 --work-delay-ms 0 --view-subscriptions 8 --view-iterations 1 --warmup-workers 0 --warmup-views 0 --serialize-payloads false
```

Start-to-completion follow-up command used for lifecycle stage timing:

```powershell
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release --no-build -- --scenario start-to-completion --workers 2000 --parallelism 32 --work-delay-ms 0 --view-subscriptions 8 --view-iterations 1 --warmup-workers 0 --warmup-views 0 --serialize-payloads false
```

## Architecture

Authoritative state is held by `WorkerOperations` and `WorkerRecord`.

- Queue entry: `WorkQueueService` -> `WorkerOperations.CreateWorker` -> `WorkerPersistenceCoordinator.AcceptQueuedWorker` -> in-memory `WorkerRecord`.
- Queue acceptance records authoritative worker memory, `WorkerIndex`, queue diagnostics, and `worker.queued`.
- Start path: dispatcher or `Workers.Execute(Start)` -> `WorkerRecord.Start` -> current iteration creation -> `RegisterIterationIfTracked` -> read-model iteration update.
- Complete/fail/cancel path: execution strategy -> `WorkerExecutionCompletionRecorder` -> `WorkerRecord.Complete*` -> retained iteration update -> worker events -> retention/persistence/concurrency synchronization.
- Pause/cancel/purge/reconfigure/status paths apply authoritative state first, then publish events and read-model updates as needed.

Read-model state is asynchronous and snapshot-based:

- `WorkSystemReadModel.RecordWorker/RecordIteration/Forget*` enqueue updates through one `updateSync` lock.
- Pending updates are coalesced by worker id, iteration reference, worker batch, or clear.
- A single projector task reads a channel signal, applies coalesced batches, and publishes immutable `WorkSystemReadModelSnapshot` instances.
- Query paths read `readModel.Current`; they do not synchronously flush pending updates.

Consistency observed: read-model visibility is eventual. The new visibility benchmark measured authoritative queue-to-read-model visibility at about 15.6 ms p50 for single-update waits on this machine.

## Index Inventory

Authoritative/control indexes in `WorkerIndex`:

- `byDefinition`
- `bySubject`
- `byDefinitionAndSubject`
- `byState`
- `byDefinitionAndState`
- `keysByWorker`

Read-model worker indexes:

- by id
- by definition
- by state
- by definition and state
- by subject
- by definition and subject
- by concurrency key
- by definition and concurrency key
- by identifier
- by recurrence enabled
- by concurrency enabled
- by profiling enabled
- worker keys for subject/concurrency/identifier facets

Read-model iteration indexes:

- by reference
- by worker
- by definition
- by completion status
- by definition and status
- by subject
- by concurrency key
- by identifier
- iteration keys for subject/concurrency/identifier facets

Other in-memory structures:

- `ConcurrentDictionary<WorkerId, WorkerRecord>` authoritative worker store.
- `ConcurrentDictionary<WorkerIterationReference, WorkCompletionStatus>` iteration status tracking.
- `WorkerDispatcher` unbounded channel.
- `WorkEventStream` copy-on-write subscriber array plus bounded per-subscription channels.
- `WorkConcurrencyCoordinator` per-definition locks and deferred-start queues.
- `WorkerRetentionScheduler` lock, `SemaphoreSlim`, priority queue, and final-worker indexes.
- `WorkQueueDurabilityCoordinator` channels, leases, TCS waiters, and concurrent dictionaries for durable mode.
- `WorkableLogCaptureContext` uses `AsyncLocal`.

## Baseline Results

Corrected baseline was captured before optimization with the primary scenario command above.

| Scenario | Before | After | Notes |
| --- | ---: | ---: | --- |
| Queue-only accepted/sec | 26,478 | 30,357 | Queue-only not targeted; variance is visible. |
| Queue-only read-model catch-up | 52.8 ms | 54.6 ms | 2,000 queued worker updates in both. |
| Completion-only completed/sec | 22,079 | 18,524 | Producer throughput is noisy; see update-count result. |
| Completion-only post-completion backlog | 14,000 updates | 4,000 updates | Main targeted win: 7 -> 2 completion-path updates per worker after prefill. |
| Completion-only read-model catch-up | 90.6 ms | 57.8 ms | 36% lower catch-up time. |
| Mixed queue+complete completed/sec | 21,056 | 23,617 | 2,000 queues + 2,000 completions. |
| Mixed queue+complete read-model catch-up | 64.5 ms | 15.6 ms | 76% lower catch-up time. |
| Queue while completion-heavy queue/sec | 10,271 | 9,497 | Still degraded under 8,000 concurrent completions. |
| Queue while completion-heavy completed/sec | 21,314 | 23,027 | Completion side slightly better in this run. |
| Queue while completion-heavy backlog | 28,878 updates | 6,703 updates | 77% lower backlog. |
| Read-model update latency p50 | 15.65 ms | 15.70 ms | Single-update latency unchanged. |
| Visibility latency p50 | 15.72 ms | 15.69 ms | Eventual-consistency latency unchanged. |
| Event fanout no-subscriber completed/sec | 17,659 | 26,180 | Fewer read-model updates improved no-subscriber lifecycle. |
| Event fanout with 8 subscribers completed/sec | 1,629 | 1,740 | Still the largest remaining bottleneck. |
| Managed memory growth per queued worker | 3,647 bytes | 3,614 bytes | No memory increase. |

Subscriber fanout follow-up baseline and final result used the `event-fanout-matrix` command above. These are single-run measurements, so some scheduler/read-model noise is visible, but the matched-subscriber rows moved consistently after the kept lazy-metadata optimization.

| Fanout profile | Before completed/sec | After completed/sec | Before allocated | After allocated | Notes |
| --- | ---: | ---: | ---: | ---: | --- |
| Unfiltered, 0 subscribers | 13,807 | 14,903 | 52.36 MB | 51.21 MB | No event writes; now avoids metadata construction too. |
| Unfiltered, 1 subscriber | 1,805 | 1,940 | 547.19 MB | 536.97 MB | 10,000 accepted event writes. |
| Unfiltered, 2 subscribers | 2,824 | 2,932 | 417.81 MB | 391.63 MB | 20,000 accepted event writes. |
| Unfiltered, 8 subscribers | 2,941 | 4,040 | 436.55 MB | 369.46 MB | 80,000 accepted event writes. |
| Event type `worker.completed`, 8 subscribers | 7,668 | 10,084 | 119.67 MB | 108.47 MB | 16,000 accepted event writes. |
| Event type no-match, 8 subscribers | 10,275 | 13,640 | 65.87 MB | 61.67 MB | Filter checks only; no event payloads. |
| Identifier match, 8 subscribers | 2,741 | 4,099 | 518.21 MB | 391.26 MB | Metadata identifier snapshot plus 80,000 writes. |
| Identifier no-match, 8 subscribers | 20,695 | 16,140 | 60.50 MB | 64.32 MB | No event payloads; this row is noisy and not the target. |

Start-to-completion follow-up baseline and final result used the primary `all` command after adding the named lifecycle scenario. The scenario records queue request latency, queue return to completion observation, queue-to-executor-start time, executor duration, executor-end to completion observation, total start-to-completion latency, allocations, and read-model catch-up.

| Scenario | Before | After | Notes |
| --- | ---: | ---: | --- |
| Start-to-completion completed/sec | 14,681 | 15,388 | Kept optimization improved the full guardrail pass by about 4.8%. |
| Start-to-completion allocated bytes | 48.52 MB | 47.18 MB | About 1.34 MB lower for 2,000 lifecycle workers. |
| Start-to-completion latency p50 | 0.677 ms | 0.763 ms | Similar; single-run scheduler noise is visible. |
| Start-to-completion latency p95 | 9.685 ms | 11.611 ms | Worse in the full pass, but focused A/B showed the main stage remains queue-to-executor scheduling. |
| Queue start to executor start p50 | 0.625 ms | 0.711 ms | Dominant measured stage; executor body p50 stayed 0 ms. |
| Queue start to executor start p95 | 9.559 ms | 11.359 ms | Remaining lifecycle bottleneck is dispatch/scheduling, not executor work. |
| Executor end to completion observed p50 | not recorded | 0.044 ms | Completion transition is small relative to dispatch/scheduling. |
| Completion-only completed/sec | 36,258 | 56,463 | Guardrail improved in the final `all` run. |
| Mixed queue+complete queue/sec | 15,665 | 17,536 | Guardrail improved in the final `all` run. |
| Mixed queue+complete completed/sec | 21,377 | 19,541 | Slightly lower in the final `all` run; focused runs were noisy. |
| Queue while completion-heavy queue/sec | 9,737 | 10,141 | Guardrail improved slightly. |
| Queue while completion-heavy completed/sec | 19,695 | 23,666 | Guardrail improved. |
| Mixed 10/90 completed/sec | 50,937 | 26,063 | Standalone rerun confirmed this row is lower; completion-only is strong, so this remains a mixed-load scheduling/contention suspect. |
| Event fanout no-subscriber completed/sec | 30,160 | 22,733 | Lower than the prior guardrail pass, but still no-subscriber path has no event writes; this row is noisy across full-matrix runs. |
| Event fanout unfiltered 8 subscribers completed/sec | 4,259 | 3,591 | Subscriber fanout remains the largest measured cost. |

Focused execution-capture A/B runs after the lifecycle scenario was added:

| Candidate | Start-to-completion/sec | Completion-only/sec | Mixed completion/sec | Allocation, start-to-completion |
| --- | ---: | ---: | ---: | ---: |
| Old capture shape | 11,212 | 27,908 | 13,773 | 48.90 MB |
| Direct `await` capture plus struct | 13,010 | 21,019 | 15,061 | 47.19 MB |
| Struct-only capture | 11,775 | 26,254 | 15,099 | 48.43 MB |
| Kept completed-task fast path plus struct | 11,578 focused / 15,388 in final matrix | 26,439 focused / 56,463 in final matrix | 15,099 focused / 19,541 in final matrix | 47.96 MB focused / 47.18 MB final matrix |

The direct `await` version was not kept because it improved auto-start lifecycle throughput but showed a repeatable completion-only guardrail drop in focused runs. A dispatcher channel-drain rewrite and a profiler no-op fast path were also measured and discarded because they did not improve the target scenario.

The original legacy scenario was also smoke-run before benchmark extension:

```text
1,000 workers, p=16, work-delay=0, one view call:
Accepted/sec 18,888; completed/sec 10,893; read model 7,000 enqueued / 17 applied at scenario end.
```

That exposed the old harness issue: it reported scenario completion while the read model still had a large pending backlog.

## Diagnosis

Measured bottlenecks and risks:

- High impact: fast lifecycle completion produced redundant read-model worker updates. A completed auto-start worker produced 7 enqueued read-model updates before the change. The optimized path records queue plus iteration-carried worker snapshots, reducing the common fast lifecycle to 3 updates.
- High impact: event fanout dominates when subscribers exist. The matrix narrowed the cost: no-match filters stay near baseline because they avoid payload creation and channel writes, while matched subscribers force event materialization and bounded-channel writes. In the 8-unfiltered case, 2,000 lifecycle workers generated 80,000 accepted subscription writes; after optimization this still ran at only about 0.27x of the no-subscriber row.
- Medium impact: read-model snapshot publication freezes all dictionaries and index buckets into arrays. Queue-only catch-up for 2,000 workers spent about 20-22 ms in the final projection snapshot.
- Medium impact: query APIs still materialize and sort full candidate sets (`ToArray`, `OrderBy`, grouping) for many list/facet calls. This is read-side cost, not queue/complete producer cost.
- Medium impact: completion-heavy mixed workloads still reduce queue throughput sharply. After the read-model update reduction, the remaining suspects are CPU/thread-pool pressure from thousands of worker execution tasks, `WorkerRecord` locks during event payload generation, and the harness itself starting many tasks concurrently.
- Medium impact: start-to-completion timing is dominated by queue-to-executor-start scheduling/dispatch. In the lifecycle scenario, executor duration p50 was 0 ms and executor-end to completion observation p50 was about 0.04 ms, while queue-to-executor-start p50/p95 was about 0.7/11.4 ms in the final matrix.
- Low/medium impact: per-update visibility latency is governed by the asynchronous projector and publish cadence. It was not changed by the optimization.

## Optimization Implemented

Changed `WorkerEventPublisher` and `WorkerExecutionCompletionRecorder` so lifecycle events can publish without redundantly recording a worker read-model update when the worker snapshot is already carried by an iteration update.

Affected event types:

- `worker.started`
- `worker.iteration.started`
- `worker.iteration.completed`
- `worker.iteration.failed`
- `worker.retrying`
- `worker.waiting`
- `worker.recurrence.circuit_opened`
- `worker.failed`
- accepted `worker.start` action events
- normal completion/cancellation completion-recorded events

Events are still published. Authoritative state is unchanged. Read-model consistency is preserved because `WorkerReadModelIterationUpdate` applies the included worker snapshot before indexing the iteration.

Characterization added:

- `QueryReadModelTracksFastCompletionThroughIterationUpdates` verifies a fast completed worker and completed iteration are visible with only 3 read-model sequence entries.

Subscriber fanout follow-up:

- Added `event-fanout-matrix` output to compare 0/1/2/N subscribers, unfiltered subscribers, matching event-type filters, no-match event-type filters, matching identifier filters, no-match identifier filters, subscription accepted/dropped counts, and process allocation deltas.
- Added a lazy metadata factory path in `WorkEventStream`. It checks the active subscriber snapshot first, skips metadata entirely when there are no subscribers, and also skips metadata for unfiltered subscribers while preserving bounded-channel drop behavior.
- Updated `WorkerEventPublisher` to use the lazy metadata path for worker lifecycle, log, and purge events.
- Added `WorkEventStreamTests` coverage proving the lazy path does not create metadata/events with no subscribers, delivers unfiltered subscribers without metadata, still creates metadata for filtered subscribers, and preserves metadata-before-event ordering when filtered and unfiltered subscribers are mixed.
- Tried reusing one worker identifier snapshot across event envelope and event payload creation, but did not keep that change because the matrix result was mixed and did not improve the main unfiltered 8-subscriber row.

Start-to-completion follow-up:

- Added the named `start-to-completion` scenario with lifecycle stage timing and allocation output.
- Changed `WorkerExecutionAttemptRunner` to avoid the incomplete-task capture path when `WorkerExecutionInvoker.Execute` has already completed successfully.
- Changed `ExecutionCapture` from a reference record to a readonly record struct, removing a small hot-path object allocation.
- Preserved the original `Task.WhenAny`-based incomplete/fault/cancel capture path to avoid the completion-only regression observed with the direct-await candidate.

## Memory Impact

No new cache or index was added. Managed memory growth for 2,000 queued workers stayed roughly flat:

- Before: 7.29 MB total, about 3,647 bytes/worker.
- After: 7.23 MB total, about 3,614 bytes/worker.

The main retained memory costs remain `WorkerRecord`, retained iteration/log/profile structures, read-model snapshots, read-model index arrays, key facet rows, and event subscription buffers.

The subscriber fanout follow-up also added no retained cache/index. It reduced measured process allocations in matched fanout rows, for example:

- 8 unfiltered subscribers: 436.55 MB -> 369.46 MB for the measured pass.
- 8 identifier-matching subscribers: 518.21 MB -> 391.26 MB for the measured pass.

The start-to-completion follow-up added no retained cache or index. It only reduces transient execution-capture allocation. The final full matrix measured start-to-completion allocation at 47.18 MB for 2,000 workers, down from 48.52 MB in the comparable baseline pass.

## Validation

Commands run:

```powershell
dotnet build apps\tools\Workable.PerformanceHarness\Workable.PerformanceHarness.csproj --configuration Release --no-restore
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release --no-build -- --scenario queue-only --workers 100 --parallelism 8 --work-delay-ms 0 --view-subscriptions 2 --view-iterations 1 --warmup-workers 0 --warmup-views 0 --serialize-payloads false
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release --no-build -- --scenario all --workers 2000 --parallelism 32 --work-delay-ms 0 --view-subscriptions 8 --view-iterations 1 --warmup-workers 0 --warmup-views 0 --serialize-payloads false
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release --no-build -- --scenario event-fanout-matrix --workers 2000 --parallelism 32 --work-delay-ms 0 --view-subscriptions 8 --view-iterations 1 --warmup-workers 0 --warmup-views 0 --serialize-payloads false
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release --no-build -- --scenario start-to-completion --workers 2000 --parallelism 32 --work-delay-ms 0 --view-subscriptions 8 --view-iterations 1 --warmup-workers 0 --warmup-views 0 --serialize-payloads false
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release --no-build -- --scenario completion-only --workers 2000 --parallelism 32 --work-delay-ms 0 --view-subscriptions 8 --view-iterations 1 --warmup-workers 0 --warmup-views 0 --serialize-payloads false
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release --no-build -- --scenario mixed-queue-complete --workers 2000 --parallelism 32 --work-delay-ms 0 --view-subscriptions 8 --view-iterations 1 --warmup-workers 0 --warmup-views 0 --serialize-payloads false
dotnet run --project apps\tools\Workable.PerformanceHarness --configuration Release --no-build -- --scenario mixed-10-90 --workers 2000 --parallelism 32 --work-delay-ms 0 --view-subscriptions 8 --view-iterations 1 --warmup-workers 0 --warmup-views 0 --serialize-payloads false
dotnet test tests\Workable.Tests\Workable.Tests.csproj --no-restore --logger "console;verbosity=minimal" --blame-hang-timeout 2m --filter "Category=EventStream|Category=Events"
dotnet test tests\Workable.Tests\Workable.Tests.csproj --no-restore --logger "console;verbosity=minimal" --blame-hang-timeout 2m --filter "Category=Query|Category=WorkerLifecycle|Category=Events"
dotnet test tests\Workable.Tests\Workable.Tests.csproj --no-restore --logger "console;verbosity=minimal" --blame-hang-timeout 2m --filter "Category=Execution|Category=WorkerLifecycle|Category=Events|Category=EventStream"
dotnet test tests\Workable.Tests\Workable.Tests.csproj --no-restore --logger "console;verbosity=minimal" --blame-hang-timeout 2m
dotnet build Workable.slnx --configuration Release --no-restore
dotnet test Workable.slnx --configuration Release --no-restore --logger "console;verbosity=minimal" --blame-hang-timeout 2m
```

Test results after optimization:

```text
Event subscriber slice: Passed 46, Failed 0, Skipped 0
Relevant slice from first pass: Passed 225, Failed 0, Skipped 0
Lifecycle/event execution slice after execution-capture change: Passed 222, Failed 0, Skipped 0
Main backend tests from first pass: Passed 1,016, Failed 0, Skipped 0
Final Release solution tests: Workable.Tests passed 1,022; Workable.SqlServer.Tests passed 45
Release solution build: succeeded with 0 warnings and 0 errors
```

An initial Debug solution build failed only because `Workable.SampleHost` was already running as PID 61380 and locking its Debug output DLLs. The Release solution build used separate output paths and passed.

## Optimization Order

Done:

1. Extend the harness with named, comparable scenarios and tab-separated output.
2. Measure corrected baseline before optimization.
3. Remove redundant read-model worker updates carried by iteration updates.
4. Add correctness characterization for fast completion visibility.
5. Add subscriber fanout matrix measurements.
6. Keep the lazy event metadata optimization and discard the unproven payload snapshot tweak.
7. Add start-to-completion lifecycle stage timing.
8. Keep the completed-task execution-capture fast path and struct capture; discard direct-await, dispatcher drain, and profiler no-op alternatives.
9. Rerun benchmarks and relevant backend tests.

Recommended next:

1. Profile matched event fanout with runtime counters/traces to separate JSON payload creation from bounded-channel write and counter costs.
2. Add a read-model snapshot-publish microbenchmark that isolates `WorkSystemReadModelState.ToSnapshot`.
3. Investigate queue throughput under completion-heavy load with runtime counters/thread-pool traces to separate real contention from harness scheduling artifacts.
4. Consider query-side paging optimizations that avoid full materialization for common first-page reads.
5. Revisit read-model publish cadence only if product consistency expectations allow different visibility latency.
6. Add repeat/median support to the harness before making smaller lifecycle decisions; the current single-pass mixed-ratio rows show scheduler noise.
