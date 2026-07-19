# Performance Benchmark Review: Next Enhancement Cases

Reviewed: 2026-06-29

This is a dated benchmark record. Scenario names and measurements below describe the recorded 2026-06-29 runs and are not a current performance guarantee; use the performance harness README and a fresh run for the current harness surface.

Primary data source: `artifacts/performance/workable-benchmark-history.xlsx`

This review uses the cleaned 2026-06-29 rerun as the current baseline. Scenario/load-harness rows are treated as the strongest product signal because they exercise complete runtime flows. BenchmarkDotNet rows are useful supporting evidence, but rows with minimum-iteration warnings or surprising allocation changes are not used as product-proof by themselves.

## Clean Rerun Scope

- Removed the prior problematic `artifacts/performance/rebenchmark-2026-06-29` artifact set before rerunning.
- Reran the scenario suite artifacts for `scenarios-all`, `event-fanout-matrix`, `signalr-fanout-matrix`, `durable-memory-release-after-purge`, `durable-workflow-memory-recovery-32x8`, and durable worker lifecycle breakdown at 1,000 and 10,000 workers.
- Reran the BenchmarkDotNet baseline suite with an explicit `--filter '*Baseline*`; this produced 56 baseline benchmark cases and excludes the opt-in million-worker stress benchmark.
- Logged 1,648 new normalized rows under `source_group` values prefixed with `2026-06-29-rebenchmark`.
- The workbook now has 3,548 data rows in `All History`, with 26 sheets and 26 resized Excel tables.
- Kept the clean rerun CSV/report artifacts and the single full-suite BDN log; removed temporary workbook backups and preliminary probe logs.

## Last Run Comparison

The comparison below uses the previous logged source groups as the baseline: 2026-06-25 scenario rows, 2026-06-26 BenchmarkDotNet rows, and the 2026-06-26 post-microbatch durable lifecycle rows.

| Area | Previous | Clean rerun | Change | Read |
| --- | ---: | ---: | ---: | --- |
| In-memory queue-only accepted/sec | 45,821 | 48,733 | +6.4% | Slightly better |
| In-memory start-to-completion completed/sec | 9,256 | 8,308 | -10.2% | Mild regression/noise |
| In-memory completion-only completed/sec | 10,132 | 9,859 | -2.7% | Mostly stable |
| Durable lifecycle admission/sec, 1k workers | 869 | 648 | -25.4% | Regressed |
| Durable lifecycle admission/sec, 10k workers | 1,075 | 714 | -33.6% | Regressed |
| Durable soak, 1k workers | 13.96 s | 17.541 s | +25.7% slower | Regressed |
| Durable memory-release admission/sec | 434 | 606 | +39.7% | Better, but different scenario than lifecycle |
| Event fanout baseline/no-subscriber completed/sec | 21,428 | 23,745 | +10.8% | Better |
| Event-type fanout completed/sec, 64 subs | 14,152 | 15,220 | +7.5% | Better |
| Identifier-match fanout completed/sec, 64 subs | 4,312 | 3,757 | -12.9% | Still poor |
| SignalR unfiltered delivery/sec, 1 listener | 17,363 | 17,207 | -0.9% | Stable |
| SignalR unfiltered delivery/sec, 64 listeners | 135,351 | 183,063 | +35.3% | Better |
| SignalR event-type delivery/sec, 64 listeners | 150,942 | 120,924 | -19.9% | Regressed or noisy |
| Worker broad first page, 100k workers | 10.085 ms | 8.185 ms | -18.8% faster | Better |
| Worker identifier facet, 100k workers | 51.9 ms | 60.4 ms | +16.4% slower | Regressed |
| Durable workflow recovery runs/sec | 31.806 | 34.701 | +9.1% | Better |

Important caveat: the authorized bulk-action BDN row is not a reliable improvement signal in this rerun. The 5,000-worker case moved from 245.74 ms and 186,458 KiB allocated to 263.69 us and 19.96 KiB allocated, which strongly suggests benchmark drift or that the measured path is no longer comparable. Treat it as a harness investigation, not a product performance win.

## Focused Regression Check

After the clean rerun, focused checks did not reproduce the scary parts as recent code regressions.

- Durable lifecycle still measures around 650-710 accepted/sec today, but the same result reproduces on commit `7bb98f4` (`Improve durable queue SQL throughput`): 666 accepted/sec and 651 claimed entries/sec for 1,000 workers. A fresh SQL schema, claim batch size 100, and restarting the SQL Server test container did not restore the older 869/1,075 accepted/sec rows. Treat the 2026-06-26 post-microbatch durable rows as optimistic or environment-sensitive until they can be reproduced.
- Event fanout focused rerun: event-type 64-subscriber fanout measured 16,542 completed/sec, above both the old 14,152 row and the clean rerun 15,220 row. Identifier-match fanout measured 4,082 completed/sec, still poor but much closer to the old 4,312 row than the clean rerun suggested.
- SignalR focused rerun: unfiltered single-listener delivery measured 17,379 events/sec, unfiltered 64-listener delivery measured 185,390 events/sec, and event-type 64-listener delivery measured 143,090 events/sec. The event-type row is still a little below the old 150,942 row, but the focused rerun looks more like noise than a confirmed transport regression.

## Repeat-Aware Current Vs Last

The 1,000-worker fanout and SignalR rows are short enough that one run is a weak regression signal. Repeating those scenarios and checking 10,000-worker runs changes the read: fanout is mostly stable or improved, SignalR has delivery-wait variance, and durable remains the largest absolute bottleneck.

| Area | Previous | Clean rerun | Repeat-aware current | Read |
| --- | ---: | ---: | ---: | --- |
| Durable lifecycle admission/sec, 1k workers | 869 | 648 | 650-710 | Bottleneck; old previous row did not reproduce |
| Durable lifecycle admission/sec, 10k workers | 1,075 | 714 | 709 | Bottleneck; old previous row did not reproduce |
| Event fanout baseline/no-subscriber completed/sec | 21,428 | 23,745 | 22,869 median | Better/stable |
| Event-type fanout completed/sec, 64 subs | 14,152 | 15,220 | 17,886 median | Better |
| Identifier-match fanout completed/sec, 64 subs | 4,312 | 3,757 | 4,248 median | Stable, still expensive |
| SignalR unfiltered delivery/sec, 1 listener | 17,363 | 17,207 | 17,625 median | Stable |
| SignalR unfiltered delivery/sec, 64 listeners | 135,351 | 183,063 | 188,242 median | Better |
| SignalR event-type delivery/sec, 64 listeners | 150,942 | 120,924 | 144,942 median | Mostly stable/noisy |
| Worker identifier facet, 100k workers | 51.9 ms | 60.4 ms | 53.4-55.2 ms focused | Not confirmed; still allocation-heavy |

The 10,000-worker repeat checks are better stability probes than the 1,000-worker rows. Event fanout repeated within about 6-10% for the key rows. SignalR single-listener repeated within about 4%, while 64-listener delivery still varied by filter shape because delivery wait moved by hundreds of milliseconds to nearly a second. Focused worker facet checks also weakened the clean-run regression read: the clean committed `HEAD` measured 53.4 ms, the active tree after cleaning generated output measured 55.2 ms, and the older `a54f00d` benchmark-era commit measured 52.6 ms.

## Priority 1: Durable SQL Lifecycle Throughput

Durable SQL remains the clearest end-to-end bottleneck, but the follow-up check no longer points to a recent code regression. The current reproducible lifecycle baseline is roughly 650-710 accepted/sec, and that same range reproduces on the earlier durable-throughput commit. The BDN durable soak case still needs attention, but the 2026-06-26 post-microbatch lifecycle rows should not be treated as a stable baseline until reproduced.

Next work:

- Add multi-run median/spread reporting for durable lifecycle rows before using them as regression gates.
- Continue breaking down claim SQL execution, row materialization, runtime acceptance, completion cleanup, and read-model catchup.
- Keep the lifecycle scenario as the main gate, because admission-only improvements are not enough if claim/materialization remains around the same order of throughput.
- Avoid adding multiple durable readers until the single-reader SQL and materialization cost is better understood.

## Priority 2: Event Fanout And Change Stream Adoption

The event-type filtered path improved, and the repeated runs do not confirm a fanout regression. The remaining issue is absolute cost for consumers that intentionally subscribe to raw events: identifier-match and unfiltered raw fanout are still expensive because many subscribers create real per-event delivery pressure. Normal state-oriented framework paths now use change-stream semantics rather than raw event fanout. Use `change-stream-fanout` to measure active state watchers separately from `event-fanout`/`event-delivery`.

Next work:

- Keep indexed raw-event filters, including event type and definition, on selective subscriptions; keep unfiltered broad subscribers on the global append/cursor path.
- Keep dashboard/view consumers on `WorkChangeStream`; guard this with tests so state watchers do not regress back to raw event subscriptions.
- Keep raw event streams for consumers that truly need every event.
- Keep separate raw-event and change-stream benchmark rows, including accepted, delivered, dropped, and coalesced counts by subscription shape.

## Priority 3: Worker Query Facets And Broad Pages

Broad first page improved at 100k workers, and focused reruns do not confirm that `IdentifierKeyTypeFacet` regressed from the old 51.9 ms row. The facet path is still worth improving because it scans the flattened worker-key list, groups by type, then builds and sorts a full worker overview list before paging; the 100k benchmark allocates about 32 MiB per query. This is a dashboard/query scalability issue, not currently a proven regression.

Next work:

- Make broad first-page queries page-aware so they do not sort/materialize every candidate.
- Avoid creating overview items until after the page window is known.
- Split facet counts from facet preview workers.
- Add benchmark rows for first page, deep page, and facet count-only paths.

## Priority 4: SignalR Delivery And Coalesced View Updates

The single-listener slowdown is not reproduced, 64-listener unfiltered delivery improved, and repeated 1,000-worker runs brought the event-type 64-listener median back near the previous baseline. SignalR still deserves attention because delivery wait is the source of the remaining variance: the 10,000-worker 64-listener runs spent seconds draining delivered events, and event-type/definition-name rows moved substantially by delivery wait rather than connection or watch setup.

Next work:

- Add batch count, batch size, and SignalR send-duration diagnostics by filter shape before changing transport code again.
- Push view and overview notifications through the core change stream where possible, then remove duplicated coalescing logic from SignalR.
- Keep measuring raw-event delivery separately from coalesced view delivery.

## Priority 5: Benchmark Harness Reliability

The scenario harness now supports `--repeat-runs`/`--repeats`, which writes per-run rows and min/median/max/mean/spread summary rows into the same CSV. Use this for durable lifecycle, fanout, SignalR, and other noisy scenarios before calling a regression. Some BDN rows still have minimum-iteration warnings, and the authorized bulk-action row appears non-comparable. HTTP and MCP rows remain mostly harness/lifecycle dominated around 100 ms. This does not block scenario-driven work, but it does block using those BDN rows as regression gates.

Next work:

- Repair or replace the authorized bulk benchmark before ranking that path.
- Increase operation counts for HTTP, MCP, SignalR, and very small authorization benchmarks.
- Mark known non-comparable rows in the workbook notes when a benchmark changes semantics.
- Prefer scenario breakdown metrics when product behavior is the question.

## Priority 6: Durable Workflow Recovery And Child Reconnect

The scenario recovery row improved from 31.806 to 34.701 recovered runs/sec, and durable state cleanup is healthy after recovery. This is no longer ahead of durable SQL lifecycle throughput, worker facets, or fanout/SignalR delivery costs, but it remains worth tightening after the higher-order bottlenecks.

Next work:

- Keep the 32x8 recovery scenario as the smoke gate.
- Add larger recovery shapes only after lifecycle throughput is stable.
- Split recovery timing into list, deserialize, reconnect children, resume execution, cleanup, and read-model catchup phases.

## Lower Priority Or Healthy Areas

- In-memory queue acceptance is healthy and slightly faster in the clean run.
- Durable workflow recovery improved in the scenario row.
- Read model publish regressed from 1.013 ms to 1.220 ms at 25k workers, but the absolute number is still small compared with durable lifecycle and facet/query issues.
