# Workable HTTP API

Workable can expose queueing, worker operations, system operations, and query APIs through the `Workable.HttpApi` adapter package.

The HTTP API adapter uses the same Workable catalog and queueing system as direct .NET code. A work definition can be queued through HTTP only when its invocation configuration allows `WorkInvocationChannel.HttpApi`.

Invocation-channel rules matter for work invocation, not for general system/query/worker discovery routes. Definition listing, diagnostics, worker reads, lifecycle routes, and other read/control surfaces are governed by authorization and route shape, not by definition invocation-channel settings.

HTTP queueing, worker actions, and worker reconfiguration record a `WorkRequestContext` from the request. Its nested `Origin` carries the durable actor/channel provenance, and the request context also captures the HTTP path as `RequestContext.Url`. Built-in queue, action, bulk-action, and reconfiguration request bodies can also supply an optional `description` value, which Workable stores on `RequestContext.Description`.

`Workable.HttpApi` is an authenticated transport. Anonymous callers are rejected before Workable routes run or request bodies are bound, and mapped systems must be authorization-enabled.

Each request creates a `WorkRequestContext` and an `IWorkSystemSession` for the selected system. Work-definition read access filters catalog, query, event, and view results. Work-definition operate access controls queueing, worker actions, and reconfiguration.

## Map Endpoints

Map the default Workable API endpoints from the host application.

```csharp
builder.Services.AddWorkableHttpApi();

app.MapWorkableApi();
```

The default prefix is `/workable`.

`MapWorkableApi` always requires authenticated callers. When `WorkableAspNetCoreAuthorizationOptions.TransportAuthenticationScheme` is also set, `MapWorkableApi` adds matching authorization metadata to the mapped endpoints so ASP.NET Core evaluates that specific scheme.

That transport scheme is not automatic. `AddWorkableHttpApi()` by itself does not choose one. It is commonly set by [Workable.Entra](../guides/entra-authentication.md), or by host code that wants Workable HTTP requests to authenticate with one specific ASP.NET Core scheme instead of inheriting the ambient default.

When a transport scheme is configured, the host pipeline must run authorization middleware before those endpoints execute. If your host already runs `app.UseAuthorization()`, no extra step is needed. If you are using [Microsoft Entra Authentication](../guides/entra-authentication.md), `app.UseWorkableEntraAuthorization()` already calls both `UseAuthentication()` and `UseAuthorization()`.

```csharp
app.UseAuthorization();
app.MapWorkableApi("/internal/work");
```

The default routes target `IWorkSystemRegistry.Default`. The same endpoints are also available for named systems under `/systems/{systemName}`.

```http
GET /workable/systems/email/definitions
POST /workable/systems/email/work/email.welcome.send
GET /workable/systems/email/workers/22222222-2222-2222-2222-222222222222
POST /workable/systems/email/views/workers
POST /workable/systems/email/workers/22222222-2222-2222-2222-222222222222/actions/cancel
```

Route matching is case-insensitive. Worker action route values are also parsed case-insensitively, so `/actions/cancel`, `/actions/Cancel`, and `/actions/CANCEL` all target `WorkAction.Cancel`.

`AddWorkableHttpApi` configures HTTP JSON enum handling so enum strings in request bodies can also be supplied without matching .NET enum casing exactly.

## Capabilities

Read host-level discovery information from the mapped HTTP API root.

```http
GET /workable/host
```

The response includes host capabilities plus each visible system's id, optional name, state, default-system marker, system capabilities, and the caller's system-level access summary.

```json
{
  "capabilities": {
    "realtime": {
      "enabled": true,
      "transport": "signalr",
      "hubPath": "/workable/realtime"
    }
  },
  "systems": [
    {
      "id": { "value": "11111111-1111-1111-1111-111111111111" },
      "name": null,
      "state": "Started",
      "isDefault": true,
      "capabilities": {
        "persistentCoordinationAvailable": true
      },
      "access": {
        "isSystemAdministrator": false,
        "isWorkAdministrator": false,
        "canViewDiagnostics": true,
        "canControlSystem": false,
        "canReadAllWork": false,
        "canOperateAllWork": false,
        "totalDefinitionCount": 12,
        "readableDefinitionCount": 8,
        "operableDefinitionCount": 4
      }
    }
  ]
}
```

The host-level `capabilities` object lets clients discover optional transport features exposed by the host. `realtime` reports whether `Workable.SignalR` is registered and, when it is, advertises the hub transport details clients should use.

The per-system `capabilities` object is reserved for system-specific runtime behavior. `persistentCoordinationAvailable` tells clients whether that system currently has persistent coordination available through a registered persistence store. In practice, that means persistent coordination settings such as `storage: "Persistent"` can be honored for features like durable queueing, persistence-backed idempotency, and persistence-backed coordination.

The systems list is filtered to systems where the caller has actual access. Read access, operate access, diagnostics access, control access, or administrator roles are all enough to make a system visible.

When realtime is not registered, `enabled` is `false`.

## Diagnostics

Read runtime diagnostics for the selected system.

```http
GET /workable/diagnostics
GET /workable/systems/email/diagnostics
```

The response includes queue, read-model, retention, concurrency, durability, and idempotency diagnostics. Use it to monitor alertable queue rejections, query freshness, projector pressure, retention lag, deferred-start backlog, durable coordination lag, duplicate rejection, and internal diagnostics failures.

Diagnostics require the system-level `Diagnostics` permission or `SystemAdministrator`.

```json
{
  "id": { "value": "11111111-1111-1111-1111-111111111111" },
  "name": "email",
  "state": "Started",
  "queue": {
    "rejectedWorkCount": 0,
    "lastRejectedAt": null,
    "lastRejectedStatus": null,
    "lastRejectedDefinitionId": null,
    "lastRejectedCode": null,
    "lastRejectedMessage": null,
    "alertableRejectedWorkCount": 0,
    "lastAlertableRejectedCode": null,
    "lastAlertableRejectedMessage": null
  },
  "readModel": {
    "enqueuedSequence": 42,
    "appliedSequence": 42,
    "appliedUpdateCount": 42,
    "publishedSnapshotCount": 7,
    "lastBatchSize": 3,
    "lastProjectionDuration": "00:00:00.0012000",
    "lastProjectedAt": "2026-05-17T16:19:00.168+00:00",
    "projectorFailureType": null,
    "projectorFailureMessage": null,
    "pendingUpdateCount": 0,
    "hasProjectorFailure": false
  },
  "retention": {
    "trackedFinalWorkerCount": 0,
    "scheduledPurgeCount": 0,
    "scheduledPurgeHighWaterMark": 0,
    "oldestScheduledPurgeDueAt": null,
    "oldestDuePurgeAge": "00:00:00",
    "pendingCountRetentionDefinitionCount": 0,
    "systemCountRetentionPending": false,
    "lastRunAt": null,
    "lastRunDuration": "00:00:00",
    "lastPurgedCount": 0,
    "totalPurgedCount": 0,
    "schedulerFailureType": null,
    "schedulerFailureMessage": null,
    "hasSchedulerFailure": false
  },
  "concurrency": {
    "deferredStartCount": 0,
    "oldestDeferredStartAge": "00:00:00",
    "lastDrainReleasedCount": 0
  },
  "durability": {
    "acceptedWaiterCount": 0,
    "oldestAcceptedWaiterAge": "00:00:00",
    "pendingCleanupCount": 0,
    "oldestPendingCleanupAge": "00:00:00",
    "readerFailureType": null,
    "readerFailureMessage": null,
    "leaseRenewalFailureType": null,
    "leaseRenewalFailureMessage": null,
    "cleanupFailureType": null,
    "cleanupFailureMessage": null,
    "hasReaderFailure": false,
    "hasLeaseRenewalFailure": false,
    "hasCleanupFailure": false
  },
  "idempotency": {
    "duplicateRejectionCount": 0,
    "lastDuplicateRejectedStorage": null
  }
}
```

`pendingUpdateCount` should usually return to `0`. Sustained growth means the read-model projector is falling behind accepted lifecycle updates.

See [Work Diagnostics](../concepts/diagnostics.md) for field meanings and warning guidance.

## System Lifecycle

Start or stop a Workable system through the HTTP API.

```http
POST /workable/lifecycle/start
POST /workable/lifecycle/stop
```

Named systems use the same route pattern.

```http
POST /workable/systems/email/lifecycle/start
POST /workable/systems/email/lifecycle/stop
```

Starting a system runs the normal system startup behavior. Work definition sources are not run again after the catalog has already been built, but automatic starts and startup work sources run each time a stopped system is started.

Lifecycle routes require the system-level `ControlSystem` permission or `SystemAdministrator`.

Stopping a system stops accepting new work, interrupts active workers, waits for the configured shutdown grace period, and then force-completes workers that did not finish cooperatively as `Interrupted`. After shutdown work completes, Workable clears in-memory worker and iteration records for that system. The stop response includes the shutdown grace period, summaries for workers asked to stop, and the names and summaries of workers that were force-interrupted after the grace period elapsed.

```json
{
  "id": { "value": "11111111-1111-1111-1111-111111111111" },
  "name": "email",
  "state": "Stopped",
  "forceInterruptedWorkers": [],
  "cancellationRequestedWorkers": [],
  "cancellationRequestedWorkerSummaries": [],
  "forceInterruptedWorkerNames": [],
  "forceInterruptedWorkerSummaries": [],
  "shutdownGracePeriod": "00:00:15"
}
```

## Definition Listing

The definitions endpoint returns the work definitions visible to the current caller in the selected system.

```http
GET /workable/definitions
```

`GET /workable/definitions` also supports `category`, `includeSubcategories`, and `level` query-string parameters. `level=true` returns the lightweight catalog level for one category instead of full definition records. Those lightweight definition rows include only `id`, `name`, and `category`.

Read a single full definition by id.

```http
GET /workable/definitions/11111111-1111-1111-1111-111111111111
```

Definitions include their invocation configuration and authorization metadata. Definitions that the caller cannot read are filtered out entirely. Definitions that allow read but not operate access still appear in discovery responses so clients can display them as unavailable through HTTP queueing. Queueing that work through HTTP returns an authorization response.

The HTTP invocation channel only affects whether a definition can be queued through HTTP. It does not affect whether that definition can appear in definition/query/read results once normal authorization allows access.

Filter work definitions with the same criteria shape as `IWorkQueryService.WorkDefinitions` and `WorkDefinitionCriteria`.

```http
POST /workable/definitions/query
Content-Type: application/json

{
  "category": "Email",
  "includeSubcategories": true
}
```

Reconfigure a definition's default worker options and default runtime configuration for future workers.

```http
POST /workable/definitions/11111111-1111-1111-1111-111111111111/reconfigure
Content-Type: application/json

{
  "revision": 0,
  "changes": {
    "defaultOptions": {
      "profilingEnabled": true
    },
    "configuration": {
      "start": {
        "policy": "DoNotStart"
      }
    }
  }
}
```

The definition reconfiguration route requires the current definition revision. Accepted changes advance the definition revision and affect workers queued afterward. Stale revisions return `409 Conflict`; invalid configuration returns `400 Bad Request`; unknown definitions return `404 Not Found`.

## Work Info

Get work info by work name or work definition id. The definition-id form is available under both `/work/id/{definitionId}/info` and `/definitions/{definitionId}/info`.

The HTTP `info` payload includes the full definition, worker rollup/status, and `queueRequestSchema`, so clients can open definition configuration or queue UI without a second schema request.

```http
GET /workable/work/email.welcome.send/info
GET /workable/work/id/11111111-1111-1111-1111-111111111111/info
GET /workable/definitions/11111111-1111-1111-1111-111111111111/info
```

## Queue Work

Queue work by name.

```http
POST /workable/work/email.welcome.send
Content-Type: application/json

{
  "input": {
    "userId": "user-123"
  }
}
```

Queue work by definition id.

```http
POST /workable/definitions/11111111-1111-1111-1111-111111111111/queue
Content-Type: application/json

{
  "input": {
    "userId": "user-123"
  }
}
```

By default, the HTTP adapter returns after queue acceptance.

By default, worker options and runtime configuration come from the targeted work definition. Include HTTP queue options only when you want to override those defaults for this one request.

Queue requests can also include an optional `description` when the caller wants to attach human-readable request context to the worker origin.

```json
{
  "input": {
    "userId": "user-123"
  },
  "description": "Retry welcome email after the user corrected their address."
}
```

Use `GET /workable/queue-request/schema` when a client wants to discover the accepted HTTP queue request shape for the selected system at runtime, including the optional `description` field.

Request completion when the caller needs the final worker output in the HTTP response.

```json
{
  "input": {
    "reportId": "report-123"
  },
  "completion": "WaitForCompletion"
}
```

Queue requests can include HTTP worker options and input identity metadata.

```json
{
  "input": {
    "userId": "user-123"
  },
  "options": {
    "profilingEnabled": true
  },
  "subjectId": {
    "type": "user",
    "value": "user-123"
  },
  "concurrencyKey": {
    "type": "tenant",
    "value": "tenant-456"
  },
  "identifiers": [
    {
      "type": "invoice",
      "value": "invoice-789"
    }
  ]
}
```

HTTP queue options use `WorkableHttpWorkerOptions`. Its `configuration` shape includes start behavior, coordination, recurrence, transient retry, logging, and retention. Coordination selects local or persistent coordination state, then enables duplicate protection, capacity limits, durable queueing, and durable completion under that mode. Retention includes `purgeInterval` and the asynchronously enforced `maximumFinalWorkers` target. Invocation channels are not part of the HTTP queue request because they are definition-level configuration, not per-request overrides.

The accepted worker remains owned by Workable and can be queried, observed, or controlled through Workable.

Queue work in a named system by including the system name in the route.

```http
POST /workable/systems/email/work/email.welcome.send
Content-Type: application/json

{
  "input": {
    "userId": "user-123"
  }
}
```

## Views And Components

The HTTP API can also expose the shared Workable view/component contract:

- `POST /workable/views/{viewName}`
- `POST /workable/components/query`
- `POST /workable/components/{componentName}`

These endpoints are the HTTP transport for the shared `Workable.Views` contract. The in-process `IWorkQueryService` still exposes typed query methods; the HTTP adapter maps view and component requests onto those typed queries.

```http
POST /workable/views/overview
Content-Type: application/json

{
  "scope": {
    "category": "Billing",
    "includeSubcategories": true
  },
  "components": [
    { "id": "system", "type": "system" },
    { "id": "workers", "type": "workers", "shape": "compact" },
    { "id": "failedIterations", "type": "failedIterations", "shape": "detailed" },
    { "id": "throughput", "type": "throughput", "shape": "standard", "options": { "windowSeconds": 60, "bucketSeconds": 1 } }
  ]
}
```

Component and view requests accept an optional `scope`. Scopes can target `definitionId`, `definitionName`, or `category`. When `category` is supplied, `includeSubcategories` defaults to `true`. Requests can also supply component `shape` and component-specific `options`.

Use these routes when the caller wants the shared transport-oriented view contract over HTTP. See [Views](../concepts/views.md) for canonical view names, component names, default compositions, shapes, scope behavior, and the efficiency model behind the contract.

Named systems use the same route shape under `/systems/{systemName}`.

```http
POST /workable/systems/fulfillment/views/overview
POST /workable/systems/fulfillment/components/throughput
```

## Query Workers

Get a worker snapshot.

```http
GET /workable/workers/22222222-2222-2222-2222-222222222222
```

Get the worker configuration payload used by config panels and worker-scoped queue flows. This route returns the worker's effective runtime configuration, queue seed fields (`input`, `subjectId`, and `concurrencyKey`), plus the associated definition info and queue-request schema metadata needed to render configuration or queue editors without additional `info` or `queue-request/schema` calls.

```http
GET /workable/workers/22222222-2222-2222-2222-222222222222/configuration
```

Get the worker-overview landing payload used by detail screens. This returns the typed `WorkWorkerOverviewComponent` contract instead of the generic named-view component map.

```http
GET /workable/workers/22222222-2222-2222-2222-222222222222/overview
GET /workable/workers/22222222-2222-2222-2222-222222222222/overview?activity=Timeline&activityTake=100&timelineSort=Asc&timelineCategories=Failure,UserAction
GET /workable/workers/22222222-2222-2222-2222-222222222222/overview?activity=Logs&logSort=Desc&logLevels=Error,Warning&logIterationSequence=12
```

The worker-overview route accepts:

- `activity`: `Auto`, `Logs`, or `Timeline`
- `activityTake` and `activityCursor`
- `recentIterationTake`
- `logSort`, `logLevels`, and `logIterationSequence`
- `timelineSort` and `timelineCategories`

Get the narrow logs and timeline payloads used when the detail screen expands those panels or paginates them. These routes return only the relevant section contract instead of the full worker overview payload, so callers do not need to re-fetch worker metadata, input, or iteration data just to page logs or timeline items.

```http
GET /workable/workers/22222222-2222-2222-2222-222222222222/overview/logs?activityTake=100&logSort=Desc&logLevels=Error,Warning&logIterationSequence=12
GET /workable/workers/22222222-2222-2222-2222-222222222222/overview/timeline?activityTake=100&timelineSort=Asc&timelineCategories=Failure,UserAction
```

The logs route accepts `activityTake`, `activityCursor`, `logSort`, `logLevels`, and `logIterationSequence`.

The timeline route accepts `activityTake`, `activityCursor`, `timelineSort`, and `timelineCategories`.

Retained log payloads include stable log entry ids plus `occurredAt`, iteration `sequence` when the row belongs to a retained iteration, and per-iteration `ordinal` for stable ordering among rows that share the same timestamp.

Get one completed worker iteration by worker id and iteration sequence.

```http
GET /workable/workers/22222222-2222-2222-2222-222222222222/iterations/1
```

Get the iteration-detail landing payload used by the iteration screen. This route staples the iteration snapshot together with the worker context needed by that screen: definition link data, keys, worker input, compact message counts, and the first page of retained logs.

```http
GET /workable/workers/22222222-2222-2222-2222-222222222222/iterations/1/detail
```

Get paged retained logs for one worker iteration. This route returns a compact severity summary plus a paged log list so expanded iteration log panels can page more history without re-fetching the whole iteration landing payload.

```http
GET /workable/workers/22222222-2222-2222-2222-222222222222/iterations/1/logs?take=50&sort=Desc
GET /workable/workers/22222222-2222-2222-2222-222222222222/iterations/1/logs?take=50&sort=Asc&logLevels=Error,Warning
GET /workable/workers/22222222-2222-2222-2222-222222222222/iterations/1/logs?take=50&cursor=eyJvY2N1cnJlZEF0IjoiMjAyNi0wNS0yOVQxMTowMDowMloiLCJpZCI6IjEyMyJ9
```

The iteration-logs route accepts:

- `take`
- `cursor`
- `sort`: `Asc` or `Desc`
- `logLevels`: comma-separated log levels such as `Error,Warning`

Iteration-log rows use the same retained log-entry shape as worker-overview log pages, including `occurredAt`, stable entry ids, iteration `sequence`, and per-iteration `ordinal`.

Get paged retained messages for one worker iteration. This route returns a summary count block plus a paged message list so message panels can keep compact severity totals while loading large retained message sets incrementally.

```http
GET /workable/workers/22222222-2222-2222-2222-222222222222/iterations/1/messages?take=50&sort=Desc
GET /workable/workers/22222222-2222-2222-2222-222222222222/iterations/1/messages?take=50&sort=Asc&severities=Information,Warning
GET /workable/workers/22222222-2222-2222-2222-222222222222/iterations/1/messages?take=50&cursor=50
```

The iteration-messages route accepts:

- `take`
- `cursor`
- `sort`: `Asc` or `Desc`
- `severities`: comma-separated `WorkMessageSeverity` values such as `Information,Warning`

Retained `WorkMessage` payloads now include `occurredAt` in addition to `code`, `severity`, `text`, optional `target`, and optional `metadata`.

## Local Realtime Debug

When the HTTP API is hosted in `Development`, or when the configured listener URLs are all loopback-only (`localhost`, `127.0.0.1`, or `::1`), the adapter also registers local realtime debug routes:

```http
GET /workable/debug/realtime
GET /workable/debug/realtime?connectionId=abc123
GET /workable/systems/fulfillment/debug/realtime
```

These routes are intended for local troubleshooting. In non-development environments, Workable registers them only for loopback-only listener configurations, and each request must also come from a loopback address. Other callers receive `404 Not Found`.

The debug payload includes:

- active raw event subscriptions
- active named-view subscriptions
- active worker-overview subscriptions
- current criteria, group name, and logical subscription ids
- worker-overview queue diagnostics such as `queuedCount`, `peakQueuedCount`, `acceptedEventCount`, `deliveredEventCount`, and `droppedEventCount`
- worker-overview lifecycle fields such as `isStreaming`, `streamingStartedAt`, `streamingStoppedAt`, `lastActivityAt`, and `lastError`

Worker and iteration collections can also be read through the shared views/component routes when a caller wants the transport-oriented `Workable.Views` contract instead of the narrower point routes shown here. The canonical component names and option shapes live in [Views](../concepts/views.md).

Get system activity counts by worker status.

```http
GET /workable/workers/status-summary
```

Get worker status counts for a narrower set of workers, such as one work definition.

```http
POST /workable/workers/status-summary
Content-Type: application/json

{
  "definitionName": "email.welcome.send"
}
```

Search known worker keys across subjects, concurrency keys, and identifiers.

```http
POST /workable/work-keys/query
Content-Type: application/json

{
  "search": "claim id CLM-123",
  "states": [ "Running" ],
  "take": 50
}
```

The response includes matching keys and the `WorkerOverviewItem` rows attached to each key.

List known worker key types. This is useful when a caller knows it is looking for claim work or customer work but does not yet know which exact key values exist.

```http
GET /workable/work-keys/types?search=claim%20work&skip=0&take=50
```

The key type query is also available as `POST /workable/work-keys/types/query` when a request body is preferred.

Key type responses are paginated and include `WorkerOverviewItem` rows attached to all matching keys of that type. Each type result counts a worker once per type, even when the same worker has that type as a subject, concurrency key, and identifier.

Search known work iteration keys when the caller wants execution rows rather than worker rows.

```http
POST /workable/work-iteration-keys/query
Content-Type: application/json

{
  "search": "claim id CLM-123",
  "statuses": [ "Failed" ],
  "take": 50
}
```

List known work iteration key types.

```http
GET /workable/work-iteration-keys/types?search=claim%20work&skip=0&take=50
```

The iteration key type query is also available as `POST /workable/work-iteration-keys/types/query`. Iteration key responses are paginated and include `WorkerIterationOverviewItem` rows attached to matching keys.

## Worker Operations

Worker operations require the worker id and the revision observed by the caller.

```http
POST /workable/workers/22222222-2222-2222-2222-222222222222/actions/cancel
Content-Type: application/json

{
  "revision": 3,
  "description": "Cancel the duplicate worker after operator review."
}
```

The action route supports `Start`, `Pause`, `Cancel`, `Push`, and `Purge`. `description` is optional and is copied into the action-history origin when supplied.

Apply a worker action to all workers in the system.

```http
POST /workable/workers/actions/cancel
Content-Type: application/json

{}
```

Bulk worker actions use the current server-side worker revision for each matched worker. The response contains one `WorkActionOutcome` per matched worker, so invalid states and conflicts are reported per worker.

```json
{
  "action": "Cancel",
  "filter": {
    "category": null,
    "includeSubcategories": true
  },
  "matchedWorkerCount": 0,
  "outcomes": [],
  "acceptedCount": 0,
  "conflictCount": 0,
  "invalidCount": 0,
  "unauthorizedCount": 0,
  "notFoundCount": 0
}
```

Target workers by work definition category.

```http
POST /workable/workers/actions/pause
Content-Type: application/json

{
  "category": "Billing",
  "includeSubcategories": true,
  "description": "Pause billing workers while the downstream service is unavailable."
}
```

Bulk action requests also accept an optional top-level `description`.

Runtime reconfiguration uses the same revision rule.

```http
POST /workable/workers/22222222-2222-2222-2222-222222222222/reconfigure
Content-Type: application/json

{
  "revision": 3,
  "description": "Enable profiling while investigating this worker.",
  "changes": {
    "profilingEnabled": true
  }
}
```

Use the named-system route form when operating on a worker that belongs to a named system.

```http
POST /workable/systems/email/workers/22222222-2222-2222-2222-222222222222/reconfigure
Content-Type: application/json

{
  "revision": 3,
  "description": "Enable profiling while investigating this worker.",
  "changes": {
    "profilingEnabled": true
  }
}
```

Worker reconfiguration requests also accept an optional `description`, which is retained in reconfiguration history.
