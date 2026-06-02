# Documentation Audit

Date: 2026-06-01

This audit verifies the 39 Markdown documentation files under `Docs` against the current source. Source code is the source of truth. Historical audit documents were checked for broken links and stale public names, but were otherwise preserved as historical records.

## Doc Inventory

| Document | Primary source areas verified |
| --- | --- |
| `Docs/README.md` | Solution docs list, adapter summaries, linked source docs |
| `Docs/documentation-audit.md` | This source-to-doc audit record, validation notes, and remaining documentation debt |
| `Docs/adapters/http-api.md` | `src/Workable.HttpApi`, ASP.NET Core authorization mapping, queue/result models, query routes, worker routes, debug route guards |
| `Docs/adapters/mcp.md` | `src/Workable.Mcp`, MCP tool router, invocation options, query/action tool names |
| `Docs/adapters/realtime.md` | `src/Workable.SignalR`, SignalR hub methods, client method names, realtime options, debug route behavior |
| `Docs/backend-performance-audit.md` | `src/Workable.PerformanceHarness`, scenario names, benchmark commands, read-model/event performance notes |
| `Docs/concepts/abstractions-extension-points.md` | `Workable.Abstractions`, persistence/metrics/realtime extension points, SQL Server integration link |
| `Docs/concepts/abstractions-surface.md` | `Workable.Abstractions` public interfaces and session contracts |
| `Docs/concepts/aspnetcore-integration.md` | `src/Workable.AspNetCore`, request context and authorization group mapping |
| `Docs/concepts/authorization.md` | authorization builders, system permissions, group requirements, adapter authorization behavior |
| `Docs/concepts/core-api-surface.md` | core system, queue, worker, query, event, lifecycle public contracts |
| `Docs/concepts/diagnostics.md` | diagnostics snapshots, warnings, realtime diagnostics view sample |
| `Docs/concepts/execution-engine.md` | dispatcher, execution strategy, worker record, read-model update flow |
| `Docs/concepts/lifecycle.md` | queue/start/complete/fail/pause/resume/cancel/purge lifecycle behavior |
| `Docs/concepts/observability.md` | event stream, event payloads, filtering metadata, log event payloads |
| `Docs/concepts/outcomes-and-control.md` | queue/action/completion status enums and optimistic concurrency behavior |
| `Docs/concepts/profiling.md` | `IWorkProfiler`, `WorkerOptions.ProfilingEnabled`, profile snapshots, worker reconfiguration |
| `Docs/concepts/project-structure.md` | package and namespace layout |
| `Docs/concepts/querying.md` | read-model queries, view/query DTO examples, configuration JSON examples |
| `Docs/concepts/views.md` | `Workable.Views`, named view/component catalogs, HTTP and SignalR view adapters |
| `Docs/frontend-test-audit.md` | historical frontend audit; links and public route names checked |
| `Docs/guides/configuration/README.md` | configuration override order, defaults, definition reconfiguration |
| `Docs/guides/configuration/concurrency.md` | concurrency configuration, blocking modes, validation interactions |
| `Docs/guides/configuration/idempotency.md` | idempotency configuration and persistent coordination notes |
| `Docs/guides/configuration/interactions.md` | cross-feature configuration behavior and durability/concurrency constraints |
| `Docs/guides/configuration/invocation.md` | invocation channels and default allowed channels |
| `Docs/guides/configuration/logging.md` | worker-scoped log capture defaults and retention |
| `Docs/guides/configuration/queue-durability.md` | durable queueing, durable completion, fallback polling defaults |
| `Docs/guides/configuration/recurrence.md` | recurrence defaults, circuit breaker behavior, iteration retention |
| `Docs/guides/configuration/retention.md` | worker retention defaults and final-worker cleanup semantics |
| `Docs/guides/configuration/start.md` | start policy defaults and queue wait semantics |
| `Docs/guides/configuration/system-settings.md` | system capacity, final-worker cap, shutdown settings |
| `Docs/guides/configuration/transient-retry.md` | transient retry defaults, delay/backoff settings |
| `Docs/guides/entra-authentication.md` | Entra service registration and claim mapping behavior |
| `Docs/guides/getting-started.md` | package setup, basic registration, queue examples |
| `Docs/guides/implementing-work.md` | executor/lambda API, execution context, messages, cancellation |
| `Docs/guides/queueing.md` | queue request options, queue outcome statuses, handle waits |
| `Docs/guides/registration.md` | definition sources, startup work, named systems, invocation defaults |
| `Docs/test-suite-audit.md` | historical test audit; links and public route names checked |

## Source Areas Verified

- HTTP API route map in `src/Workable.HttpApi`: catalog, queue, query, worker operations, diagnostics, lifecycle, host capabilities, and local debug routes.
- MCP adapter in `src/Workable.Mcp`: work tool naming, query tools, action tools, invocation options, tool catalog options, and authorization flow.
- SignalR adapter in `src/Workable.SignalR`: hub methods, client method names, realtime options/defaults, event subscriptions, view subscriptions, and worker-overview subscriptions.
- Core runtime in `src/Workable` and `src/Workable.Abstractions`: queue/start/complete/fail/pause/resume/cancel/purge paths, read-model update flow, event stream, profiling, diagnostics, worker snapshots, action history, and status enums.
- Configuration models in `src/Workable.Sdk` and `src/Workable.Abstractions`: start, recurrence, transient retry, logging, retention, coordination, idempotency, concurrency, queue durability, invocation, system capacity, and system retention.
- Authorization in `src/Workable`, `src/Workable.AspNetCore`, `src/Workable.Entra`, `src/Workable.HttpApi`, `src/Workable.Mcp`, and `src/Workable.SignalR`.
- Performance harness in `src/Workable.PerformanceHarness`: scenario names, command shape, BenchmarkDotNet groups, and current benchmark audit references.

## Stale Or Inaccurate Docs Fixed

- `Docs/adapters/http-api.md`: clarified local realtime debug availability. The route is registered for Development or loopback listener URLs, and non-development requests still require loopback remote IP or receive 404.
- `Docs/adapters/realtime.md`: matched the same local debug route behavior and fixed a raw event record property from `DefinitionId` to `WorkDefinitionId`.
- `Docs/adapters/mcp.md`: removed a duplicated invocation-options sentence.
- `Docs/README.md`: updated the MCP adapter summary to include action tools and definition-default reconfiguration.
- `Docs/concepts/querying.md`: corrected stale action-history sample data that referenced non-existent `workable_reconfigure_worker`.
- `Docs/concepts/views.md`: added the current `worker` named view and its `workerDetail` / `workerCurrentIteration` components; fixed the HTTP API anchor link.
- `Docs/concepts/views.md`: corrected `workerGrid` and `iterationGrid` option names to include `keyKind`, `keyType`, and `keyValue`.
- `Docs/guides/queueing.md`: added the current `Unauthorized` queue outcome.

## Missing Docs For Public Features

- Fixed: `Docs/concepts/views.md` now names the current `worker` view and worker-detail components.
- Fixed: `Docs/concepts/views.md` now lists the full current grid key-filter option set.
- No critical missing adapter docs found for current HTTP, MCP, or SignalR public surfaces.
- Remaining low-priority debt: the historical audit files are useful records but are not a substitute for a generated API reference. A future doc pass could add a compact generated route/tool/message appendix.

## Incorrect Code Samples

- Fixed the querying action-history example to use the current HTTP worker reconfiguration origin instead of a non-existent MCP worker-reconfiguration tool.
- Fixed an absolute local SQL Server integration link in `Docs/concepts/abstractions-extension-points.md`.
- Verified profiling examples against `IWorkProfiler`, `IWorkExecutionContext.Profile`, `WorkerOptions.ProfilingEnabled`, and `WorkerReconfiguration`.

## Incorrect JSON Payloads

- `Docs/concepts/querying.md`: added `fallbackPollingInterval` to default durable configuration examples because `WorkQueueDurabilityConfiguration.Default` serializes it.
- `Docs/concepts/querying.md`: corrected default `transientRetry.count` from `0` to `3`.
- `Docs/adapters/http-api.md`: added `unauthorizedCount` to the bulk worker action response example.
- `Docs/concepts/observability.md`: added `ordinal` to the retained log event payload example.
- `Docs/adapters/realtime.md`: corrected the raw event JSON field source by documenting `WorkDefinitionId`.

## Routes, Tools, And Messages

- Verified HTTP routes include definitions, host, diagnostics, lifecycle, queueing by name/id, queue schema, work info, views/components, worker status summary, worker/detail/log/message endpoints, key queries, bulk actions, worker actions, and worker reconfiguration.
- Verified named system routes mirror the default routes under `/systems/{systemName}`.
- Verified MCP tools: `workable_query_workers`, `workable_get_worker`, `workable_get_worker_iteration`, `workable_query_worker_iterations`, `workable_get_work_info`, `workable_query_work_definitions`, `workable_query_worker_keys`, `workable_query_worker_key_types`, `workable_query_work_iteration_keys`, `workable_query_work_iteration_key_types`, `workable_get_worker_status_summary`, `workable_start_worker`, `workable_pause_worker`, `workable_cancel_worker`, `workable_push_worker`, `workable_purge_worker`, and `workable_reconfigure_work_definition`.
- Verified MCP work tools use `workable_work_` plus MCP-safe work names.
- Verified SignalR hub methods: `WatchView`, `UnwatchView`, `WatchWorkerOverview`, `UnwatchWorkerOverview`, `WatchEvents`, and `UnwatchEvents`.
- Verified SignalR client methods: `workable.view`, `workable.workerOverview`, `workable.event`, and `workable.events`.

## Config Keys And Defaults

- Verified defaults documented for start, recurrence, transient retry, logging, retention, coordination, queue durability, invocation, system capacity, and system retention.
- Corrected stale query JSON defaults for retry count and queue durability fallback polling.
- Verified `WorkInvocationConfiguration.Default` allows `DotNet` and `HttpApi`; `Mcp` and `SignalR` remain opt-in.
- Verified HTTP queue-time configuration intentionally omits invocation configuration and maps to `WorkInvocationConfiguration.Default`.

## Enum And Option Names

- Verified current queue, action, completion, definition-reconfiguration, start-policy, invocation-channel, key-kind, overflow-behavior, and configuration option names used by docs.
- Corrected completion status documentation to include `Executing`, `Paused`, `Invalid`, and `NotFound` where the docs described the full enum.
- Verified `WorkableHttpCompletion` names are `ReturnAfterAccepted` and `WaitForCompletion`.

## Authorization And Security

- Verified systems require authorization by default.
- Verified HTTP and SignalR adapters require mapped systems to be authorization-enabled and add ASP.NET authorization metadata only when a transport authentication scheme is configured.
- Verified MCP requires authorization-enabled systems and authenticated callers through its MCP request context.
- Verified system and work administrator group semantics in the authorization docs.
- Fixed debug-route wording so docs do not overpromise local debug access outside loopback/development.

## Performance And Benchmark Coverage

- Verified `Docs/backend-performance-audit.md` points to `src/Workable.PerformanceHarness` and lists the current named scenarios.
- Verified the harness supports scenario output for queue-only, completion-only, mixed queue/complete, completion-heavy and queue-heavy variants, mixed ratios, read-model latency, visibility latency, index update cost, memory growth, event fanout, event fanout matrix, and start-to-completion.
- Verified BenchmarkDotNet groups in `src/Workable.PerformanceHarness/README.md` remain current.
- No performance doc changes were needed in this pass.

## Internal Contradictions

- Removed one duplicated MCP invocation-options sentence.
- Fixed the Views-to-HTTP API anchor mismatch.
- No remaining broken relative Markdown file links or checked anchors were found.

## Recommended Fix Order

Completed in this pass:

1. Security-sensitive adapter behavior: local debug route conditions.
2. Public route/tool/message names and stale MCP action naming.
3. JSON/default examples for configuration, bulk action, observability, and realtime events.
4. Public view/component inventory.
5. Link and navigation cleanup.

Recommended future work:

1. Add a generated route/tool/message appendix so adapter docs can be mechanically diffed against source.
2. Add a small JSON-example validation harness for representative payloads.
3. Add markdown lint/link tooling if documentation is going to keep growing.

## Validation Commands Run

```powershell
Get-ChildItem -Path Docs -Recurse -File -Filter *.md
Select-String -Path src\Workable.HttpApi\**\*.cs -Pattern 'MapGet\("','MapPost\("'
Select-String -Path src\Workable.Mcp\WorkableMcpToolRouter.cs -Pattern 'workable_'
Select-String -Path src\Workable.SignalR\*.cs -Pattern 'Watch|workable\.|HubPath|PublishInterval|EventSubscriptionCapacity|EventOverflowBehavior'
Select-String -Path src\Workable.HttpApi\Queue\*.cs -Pattern 'record WorkableHttp|enum WorkableHttp'
rg -n "WorkViewWorkerGridOptions|WorkViewIterationGridOptions|workerDetail|workerCurrentIteration|NormalizeViewComponentRequests" src\Workable.Views Docs\concepts\views.md
rg -n 'TODO|TBD|coming soon|not implemented|workable_reconfigure_worker|WaitForAccepted|WaitUntilAccepted|StartedAndReturned|/ui/views|ui-views-and-components|WorkDefinitionId\? DefinitionId|"count": 0' Docs -g '!documentation-audit.md'
rg -n --glob package.json --glob *.yml --glob *.yaml --glob *.csproj --glob *.slnx "markdownlint|lychee|remark|docfx|markdown-link|docs" .
```

Also ran a PowerShell local Markdown link and anchor check over every `Docs/**/*.md` file. Result:

```text
No broken relative Markdown file links or checked anchors found.
```

No configured markdownlint, lychee, remark, docfx, or markdown-link-check command was found. `Workable.slnx` lists the main docs as solution files, but the audit files are not part of that list.

No .NET build or test command was run because this pass changed Markdown documentation only and did not change generated docs, examples that compile as part of the solution, or source behavior.
