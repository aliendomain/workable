# Performance Benchmark Review: Next Enhancement Cases

Reviewed: 2026-06-26

Primary data source: `artifacts/performance/workable-benchmark-history.xlsx`

This review looks for the highest-leverage next performance work from the current benchmark history. It treats scenario/load-harness rows as the strongest signal, then uses BenchmarkDotNet rows and source-path inspection to explain likely causes and shape the next implementation cases.

## Reading Notes

- The workbook contains 1,900 normalized history data rows in `All History` after the durable microbatch run.
- Scenario rows are the best signal for runtime prioritization because they exercise complete queue, event, query, durability, and transport flows.
- Several BenchmarkDotNet rows are useful directional evidence but need cleanup before they should drive product decisions. Many use short runs or one invocation per iteration, and several historical logs include timeout or `NA` cases.
- HTTP API, MCP, and some SignalR connection microbenchmarks cluster around roughly 100 ms. Those rows are likely dominated by harness/TestServer/BenchmarkDotNet minimum-iteration behavior rather than pure runtime cost.
- Memory release after purge and subscription disposal look healthy in the current scenario data. They are not first-order optimization targets.

## Priority 1: Continue Durable SQL Queue Admission Work

This is the clearest high-impact case. Durable queueing is still far slower than the in-memory path, but internal SQL enqueue micro-batching moved the first meaningful throughput wall.

Evidence:

- Durable memory-release scenario, 1,000 durable workers: accepted throughput is 433.718 workers/sec.
- The same durable scenario has queue latency p95 48.909 ms and p99 81.171 ms.
- Durable soak BenchmarkDotNet row for 1,000 workers takes 13,960 ms and allocates about 330,322 KiB.
- In-memory memory-release scenario accepts 169,727 workers/sec and completes 11,008 workers/sec under similar 1,000-worker scale, which shows the gap is durable path specific.

Original likely causes:

- `WorkableSqlServerQueueDurabilityStore.Enqueue` opened a connection and issued one insert per worker when no caller transaction was provided.
- The durable reader path in `WorkQueueDurabilityCoordinator` had a fixed local reader-signal debounce and a 100-item claim batch.
- SQL Server durable claims now use split queue/payload tables, configurable claim batches, and batch updates; remaining cost is concentrated in SQL claim execution, row materialization, runtime acceptance, execution, and cleanup.
- Persistent concurrency claim paths now use explicit `HasPersistentConcurrency`, `ConcurrencyScope`, and `ConcurrencyMaximumCapacity` queue columns, avoiding JSON extraction on the claim path.

Next enhancements:

- Re-run the SQL enqueue microbatch size/window sweep when representative caller parallelism or payload shape changes; the current defaults favor the fastest observed sweep shape.
- Keep caller-owned transaction enqueue on the direct path; evaluate whether transactional batch participation is worth the complexity later.
- Investigate claim/materialization throughput next, using aggregate claim diagnostics and lifecycle breakdown metrics before introducing multiple readers.
- Consider additional explicit columns only if future claim predicates start reading JSON again.

Next benchmark case:

- Continue using `durable-worker-lifecycle-breakdown` at 1,000 and 10,000 workers to split admission latency, queue-to-executor-start latency, completion observation, read-model catchup, and durable row cleanup. Re-run the batch-size/window matrix when caller parallelism, payload size, or SQL Server deployment shape changes.

Current `durable-worker-lifecycle-breakdown` baseline, durable non-idempotent, parallelism 16, 1 ms work delay:

- 1,000 workers: total 2,371.076 ms; admission 2,250.005 ms at 444.444 workers/sec; admission p95 46.885 ms and p99 78.829 ms; queue-to-executor-start p95 151.798 ms and p99 246.699 ms; executor p95 4.79 ms; executor-end-to-completion-observed p95 0.937 ms; allocation 163,094.584 bytes/worker.
- 10,000 workers: total 22,230.073 ms; admission 22,098.91 ms at 452.511 workers/sec; admission p95 46.532 ms and p99 54.195 ms; queue-to-executor-start p95 138.518 ms and p99 161.223 ms; executor p95 4.544 ms; executor-end-to-completion-observed p95 0.294 ms; allocation 176,013.513 bytes/worker.
- These rows are now stored in `artifacts/performance/workable-benchmark-history.xlsx` under `source_group=2026-06-26-durable-worker-lifecycle-breakdown`.

Post-change non-batching run, after removing the explicit SQL transaction from single-row enqueue and draining immediately on local reader signal:

- 1,000 workers: total 2,217.55 ms; admission 2,105.515 ms at 474.943 workers/sec; admission p95 45.898 ms and p99 82.499 ms; queue-to-executor-start p95 135.288 ms and p99 165.172 ms; executor p95 4.205 ms; executor-end-to-completion-observed p95 1.424 ms; allocation 162,384.232 bytes/worker.
- 10,000 workers: total 22,264.576 ms; admission 22,113.643 ms at 452.21 workers/sec; admission p95 47.385 ms and p99 54.45 ms; queue-to-executor-start p95 140.931 ms and p99 164.895 ms; executor p95 4.014 ms; executor-end-to-completion-observed p95 0.349 ms; allocation 174,148.545 bytes/worker.
- These rows are stored in the workbook under `source_group=2026-06-26-durable-worker-lifecycle-breakdown-post-nonbatch`. The result is a clear 1,000-worker tail-latency improvement, but the 10,000-worker run is mostly neutral; durable admission still dominates and still needs deeper work.

Post-microbatch run, after adding internal SQL provider micro-batching for non-transactional durable enqueue:

- 1,000 workers: total 1,256.648 ms; admission 1,150.289 ms at 869.347 workers/sec; admission p95 31.363 ms and p99 69.798 ms; queue-to-executor-start p95 127.901 ms and p99 140.871 ms; executor p95 6.064 ms; executor-end-to-completion-observed p95 0.552 ms; allocation 87,262.936 bytes/worker.
- 10,000 workers: total 9,440.796 ms; admission 9,299.02 ms at 1,075.382 workers/sec; admission p95 26.338 ms and p99 35.799 ms; queue-to-executor-start p95 90.262 ms and p99 113.614 ms; executor p95 3.379 ms; executor-end-to-completion-observed p95 0.351 ms; allocation 89,355.46 bytes/worker.
- These rows are stored in the workbook under `source_group=2026-06-26-durable-worker-lifecycle-breakdown-post-microbatch`. The 10,000-worker run improved admission throughput by about 2.38x versus the post-nonbatch run and cut total elapsed by about 58%.

Exploratory 10,000-worker batch-size/window sweep, not added to the workbook:

- With default caller parallelism 18, batch size 64 and window 1 ms admitted 983.859 workers/sec in this run.
- Raising caller parallelism is necessary to exercise larger microbatches; batch size alone cannot help when only 18 enqueue calls are outstanding.
- Parallelism 128, batch size 64, window 1 ms admitted 5,218.434 workers/sec.
- Parallelism 256, batch size 64, window 1 ms admitted 8,800.734 workers/sec.
- Parallelism 512, batch size 64, window 1 ms admitted 11,770.512 workers/sec.
- Larger batches were not automatically better in this harness. At parallelism 512 and window 1 ms, batch size 256 admitted 10,669.036 workers/sec and batch size 512 admitted 9,525.016 workers/sec.
- Longer windows did not help the fastest cases. At parallelism 256 and batch size 256, window 1 ms admitted 6,173.616 workers/sec, window 2 ms admitted 5,710.417 workers/sec, and window 5 ms admitted 4,773.451 workers/sec.
- Once admission exceeds roughly 5,000 workers/sec, queue-to-executor-start p95 rises into multi-second territory in this benchmark. That means insertion can exceed the 5x target, but durable reader/materialization/claim throughput becomes the next end-to-end bottleneck.

Post-claim-fast-path smoke run, after adding explicit persistent-concurrency claim metadata:

- 1,000 workers: total 1,095.434 ms; admission 966.541 ms at 1,034.618 workers/sec; queue-to-executor-start p95 86.927 ms and p99 145.321 ms; claim total elapsed 980.073 ms; claim throughput 1,020.332 entries/sec.
- 10,000 workers: total 10,324.647 ms; admission 10,231.28 ms at 977.395 workers/sec; queue-to-executor-start p95 93.37 ms and p99 110.097 ms; claim total elapsed 10,141.152 ms; claim throughput 986.081 entries/sec.
- These smoke rows were not added to the workbook; they were used to validate the claim-path change.

## Priority 2: Reduce Event Fanout Pressure, Drops, And Per-Subscriber Work

Event fanout is the largest throughput and allocation cliff in the scenario data. It also directly affects SignalR and dashboard workloads.

Evidence:

- Event fanout baseline with no subscriptions completes 63,146 workers/sec.
- One unfiltered subscription drops completion throughput to 3,348 workers/sec and raises allocation from about 26.8 KiB/worker to 432.8 KiB/worker.
- Sixty-four unfiltered subscriptions complete 4,563 workers/sec and drop roughly 303,558 of 320,000 accepted subscription events.
- Sixty-four identifier-match subscriptions complete 4,376 workers/sec and allocate about 406.1 KiB/worker.
- Sixty-four event-type-completed subscriptions are better at 13,889 workers/sec and about 79.5 KiB/worker, but still only 0.22x baseline throughput.
- Event-delivery fanout with 64 unfiltered subscribers delivers all 320,000 events but completion wait p99 reaches 46.245 ms and allocation reaches about 737.6 KiB/worker.

Likely causes:

- `WorkEventStream` publishes to per-subscription bounded channels.
- Unfiltered and most filtered subscriptions still require per-subscriber matching and enqueue work.
- Identifier subscriptions are indexed, but event type and definition filters are still largely scanned.
- The default subscription queue capacity causes heavy drops under bursty fanout. Dropping protects producers but still leaves significant matching/enqueue overhead.
- Dashboard-style consumers often need latest state rather than every intermediate event.

Next enhancements:

- Add subscription indexes for event type and definition name, similar in spirit to the identifier index.
- Add a coalesced or latest-state subscription mode for dashboard views that do not require every event.
- Avoid per-subscriber event materialization/enqueue when every relevant subscriber queue is already full.
- Make event subscription capacity/backpressure policy explicit in options and scenario matrices.
- Add telemetry for per-subscription accepted, dropped, delivered, and queue depth so fanout regressions are visible without custom scenario parsing.

Next benchmark case:

- Add an event fanout matrix that varies subscription count, filter selectivity, queue capacity, and delivery mode: raw event, coalesced latest-state, and view-update stream.

## Priority 3: Make Broad Worker Queries And Facets Page-Aware

Indexed worker queries are already strong, but broad first-page queries and facets materialize too much data.

Evidence:

- `IndexedIdentifierFirstPage` over 100,000 workers takes 0.286 ms and allocates about 221 KiB.
- `BroadFirstPage` over 100,000 workers takes 10.085 ms and allocates about 3,517 KiB.
- `BroadFirstPage` over 1,000,000 workers takes 76.54 ms and allocates about 35,154 KiB.
- `IdentifierKeyTypeFacet` over 100,000 workers takes 51.9 ms and allocates about 33,203 KiB.

Likely causes:

- `WorkSystemReadModelQueryService.Workers` filters, sorts, converts to overview items, materializes an array, then applies `Skip` and `Take`.
- `WorkerKeyTypes` groups all worker keys and creates full worker overview lists per group before pagination.
- The read model has useful indexes for narrow criteria, but broad sorted pages still behave like full scans.

Next enhancements:

- Add a page-aware query path for broad first pages, using a bounded top-N selection or maintained sorted index instead of sorting/materializing every candidate.
- Avoid creating `WorkerOverviewItem` objects until after the page window is known.
- Split facet results into counts and page previews; do not return full worker lists for each facet by default.
- Maintain precomputed facet counts for identifier key type, definition, status, and common dashboard pivots.
- Add an explicit benchmark for page 1, middle page, and deep page so offset behavior is visible.

Next benchmark case:

- Add a read-model query matrix for 100,000 and 1,000,000 workers covering broad first page, broad deep page, identifier first page, status-filtered page, and key-type facets with and without preview workers.

## Priority 4: Lower Allocation And Double Reads In Authorized Bulk Actions

Authorized bulk action is functional but allocates heavily at scale. The authorization wrapper likely prevents the faster direct bulk path from carrying most of the work.

Evidence:

- Authorized cancel of 1,000 queued workers takes 5.562 ms and allocates about 15,881 KiB.
- Authorized cancel of 5,000 queued workers takes 245.74 ms and allocates about 186,458 KiB.
- The growth is more than linear in elapsed time and remains allocation-heavy.

Likely causes:

- `AuthorizedWorkerOperations.ExecuteAll` pages workers through `query.Workers`.
- Each returned overview is then authorized through `AuthorizeAction`, which fetches the full worker snapshot again through `query.Worker(workerId)`.
- After authorization, each worker is passed individually to the inner operation.
- Offset paging over a mutating data set can become inefficient or unstable because action execution changes worker state while later pages are selected.

Next enhancements:

- Add an authorized bulk execution path that authorizes from candidate snapshots already selected for the operation.
- Stream worker ids and versions from the read model or worker index instead of repeatedly materializing overview pages.
- Avoid offset pagination over the same set being mutated; use stable id/version cursors or collect candidate ids once.
- Add an aggregate-result mode for callers that need counts and failures rather than one full outcome object per worker.
- Let the direct bulk path handle execution after authorization has produced the candidate set.

Next benchmark case:

- Add authorized bulk cancel/retry/purge cases at 1,000, 5,000, and 25,000 workers, with metrics for query time, authorization time, execution time, outcome allocation, and total allocation.

## Priority 5: Tune SignalR Fanout Batching And View Update Delivery

SignalR raw event delivery is complete, but 64-subscriber scenarios show hundreds of milliseconds of delivery wait. View and overview delivery have additional per-client work that can become the next bottleneck for the admin UI.

Evidence:

- SignalR fanout with one unfiltered subscription delivers 1,000 events at 17,363 workers/sec.
- SignalR fanout with 64 unfiltered subscriptions delivers all 64,000 events, but worker throughput drops to 2,115 workers/sec and delivery wait reaches 418.05 ms.
- Sixty-four event-type subscriptions deliver all 64,000 events at 2,359 workers/sec with delivery wait 286.70 ms.
- Sixty-four definition-name or identifier subscriptions are slightly better, around 2,528 to 2,580 workers/sec, but still spend hundreds of milliseconds in delivery wait.

Likely causes:

- Raw event broadcasting uses SignalR groups, which is the right primitive, but batching windows and batch sizes may not match bursty event production.
- Worker overview and view update paths send client-specific envelopes by iterating group subscriptions.
- The current transport benchmark host uses TestServer and LongPolling, which is useful for repeatable local testing but not enough to represent production WebSockets behavior.

Next enhancements:

- Tune event batch size and batch window under high subscriber counts.
- Coalesce view and overview updates by worker/view key before sending to clients.
- Introduce group-level envelopes where client-side subscription ids can be mapped without per-connection server sends.
- Add delivery telemetry for batch size, batch wait, send duration, and group subscriber count.
- Run the same scenario over WebSockets where possible before making transport-level conclusions.

Next benchmark case:

- Add a SignalR matrix that compares LongPolling/TestServer with WebSocket-capable hosting, varies batch size/window, and separates raw-event delivery from worker-overview/view update delivery.

## Priority 6: Tighten Durable Workflow Recovery And Child Reconnect

Workflow recovery is correct enough to benchmark, but it is still slow and some historical microbenchmarks have timeout noise. Treat this as a focused recovery-improvement case after durable admission and event fanout.

Evidence:

- Durable workflow memory-recovery scenario with 32 workflows and 8 branches takes 567.920 ms for startup and 1,006.1 ms for recovery.
- Recovery throughput is 31.806 runs/sec.
- Managed memory retained after recovery is about 8.63 MiB.
- Durable child reconnect BenchmarkDotNet rows show 209.5 ms for 2 branches and 313.2 ms for 8 branches, but historical logs include timeout or `NA` cases.

Likely causes:

- Recovery has to list incomplete workflow runs, rehydrate workflow state, reconnect child workers, and clean final state.
- Workflow state and child-branch information are durable JSON payloads, so deserialize/rehydrate cost can dominate small recovery runs.
- Some BenchmarkDotNet recovery cases need harness fixes before they can be used as stable regression gates.

Next enhancements:

- Profile recovery into list, deserialize, reconnect child branches, resume execution, final cleanup, and read-model catchup phases.
- Recover child branches with bounded parallelism when ordering does not require serial processing.
- Reduce durable workflow state payload size for branch metadata and completed child records.
- Make recovery benchmarks deterministic enough to fail on regressions rather than environmental timeouts.

Next benchmark case:

- Add a durable workflow recovery matrix for 32, 128, and 512 workflows with 2, 8, and 32 branches, including interrupted-load memory growth and post-recovery retained memory.

## Priority 7: Clean Up Benchmark Harness Reliability

This is not the highest product-runtime optimization, but it is necessary for making future performance calls with confidence.

Evidence:

- HTTP API benchmark rows cluster around 102 to 107 ms with about 5.6 to 6.3 MiB allocated per operation.
- Experimental SignalR connection rows cluster around 101 to 105 ms with 6.7 to 9.8 MiB allocated.
- Several BenchmarkDotNet logs contain timeout, failed, or `NA` rows for historical durable workflow, child reconnect, HTTP query, MCP stop/cancel, and SignalR fanout cases.
- The workbook references normalized BenchmarkDotNet result sources, but current artifacts only include the history workbook and logs.

Likely causes:

- Some benchmarks use one invocation per iteration and short jobs, which makes BenchmarkDotNet minimum-iteration warnings dominate.
- TestServer setup, host lifecycle, LongPolling transport, and adapter authorization setup can hide the cost of the operation being measured.
- Failed historical rows make trend comparisons ambiguous.

Next enhancements:

- Increase operation count per invocation for HTTP, MCP, and SignalR microbenchmarks.
- Separate host/client setup from measured operation paths where possible.
- Export and retain BenchmarkDotNet CSV/JSON artifacts alongside the normalized workbook.
- Mark failed/timeout rows explicitly in the workbook and exclude them from priority summaries.
- Add scenario-level breakdown metrics before adding more microbenchmarks for the same surface.

Next benchmark case:

- Add a harness-health suite that runs representative HTTP, MCP, SignalR, durable recovery, and child reconnect cases with enough invocations to avoid minimum-iteration warnings, then records whether each case is stable enough for regression gating.

## Lower-Priority Or Healthy Areas

- In-memory purge looks healthy. The in-memory memory-release scenario purges 1,000 workers in 32.193 ms and releases more memory than the measured pre-purge growth.
- Subscription disposal looks healthy. The subscription memory-release scenario releases about 23.51 MiB of 23.66 MiB growth and retains only about 158 KiB after disposal.
- Read-model catchup is generally small in lifecycle scenarios, often around 1 to 13 ms. Backlog counters can be high immediately after a burst, but catchup time is not the first bottleneck compared with event fanout, durable admission, or broad query materialization.
- Authorization single-operation benchmarks are small compared with bulk-action allocation and query/fanout costs.
- HTTP API and MCP latency should not be optimized from the current 100 ms BenchmarkDotNet rows until the harness issue is resolved.

## Recommended Order Of Work

1. Continue durable SQL claim/materialization tuning now that enqueue micro-batching, split queue rows, claim batch sizing, and aggregate diagnostics are in place.
2. Add event fanout indexes and a coalesced/latest-state subscription mode, then rerun event and SignalR fanout matrices.
3. Make broad worker queries page-aware and split facets from full worker lists.
4. Rework authorized bulk actions to avoid double reads and repeated materialization.
5. Tune SignalR batching and view/overview delivery using transport-specific benchmarks.
6. Profile and optimize durable workflow recovery after the durable queue path has better instrumentation.
7. Stabilize the BenchmarkDotNet harness so HTTP, MCP, and recovery microbenchmarks become reliable regression gates.
