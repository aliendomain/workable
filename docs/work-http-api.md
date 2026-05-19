# Workable HTTP API

Workable can expose queueing, worker operations, and query APIs through the `Workable.HttpApi` adapter package.

The HTTP API adapter uses the same Workable catalog and queueing system as direct .NET code. Work is available through the HTTP API when its invocation configuration allows `WorkInvocationChannel.HttpApi`.

HTTP queueing, worker actions, and worker reconfiguration record a `WorkOrigin` from the request. The origin uses `HttpContext.User` for actor identity and records the HTTP path as the origin URL.

## Map Endpoints

Map the default Workable API endpoints from the host application.

```csharp
builder.Services.AddWorkableHttpApi();

app.MapWorkableApi();
```

The default prefix is `/workable`.

```csharp
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

List available Workable systems from the mapped HTTP API root.

```http
GET /workable/systems
```

The response includes each system's id, optional name, state, default-system marker, and capabilities.

```json
{
  "systems": [
    {
      "id": { "value": "11111111-1111-1111-1111-111111111111" },
      "name": null,
      "state": "Started",
      "isDefault": true,
      "capabilities": {
        "persistentCoordinationAvailable": true,
        "realtime": {
          "enabled": true,
          "transport": "signalr",
          "hubPath": "/workable/realtime",
          "features": ["worker-events", "component-views"]
        }
      }
    }
  ]
}
```

The `capabilities` object lets clients discover optional adapter and host features for each system. `persistentCoordinationAvailable` tells clients whether this system can accept persistent coordination settings. The `realtime` section reports whether `Workable.SignalR` is registered.

```json
{
  "persistentCoordinationAvailable": true,
  "realtime": {
    "enabled": true,
    "transport": "signalr",
    "hubPath": "/workable/realtime",
    "features": ["worker-events", "component-views"]
  }
}
```

When realtime is not registered, `enabled` is `false`.

## Diagnostics

Read runtime diagnostics for the selected system.

```http
GET /workable/diagnostics
GET /workable/systems/email/diagnostics
```

The response includes queue, read-model, retention, concurrency, durability, and idempotency diagnostics. Use it to monitor alertable queue rejections, query freshness, projector pressure, retention lag, deferred-start backlog, durable coordination lag, duplicate rejection, and internal diagnostics failures.

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

See [Work Diagnostics](work-diagnostics.md) for field meanings and warning guidance.

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

Stopping a system stops accepting new work, interrupts active workers, waits for the configured shutdown grace period, and then force-completes workers that did not finish cooperatively as `Interrupted`. After shutdown work completes, Workable clears in-memory worker and iteration records for that system. The stop response includes the shutdown grace period, summaries for workers asked to stop, and the names and summaries of workers that were force-completed after the grace period elapsed.

The response keeps the existing `forceCanceled*` JSON property names for compatibility. During shutdown interruption, those lists contain workers force-completed as `Interrupted`.

```json
{
  "id": { "value": "11111111-1111-1111-1111-111111111111" },
  "name": "email",
  "state": "Stopped",
  "forceCanceledWorkers": [],
  "forceCanceledWorkerNames": [],
  "forceCanceledWorkerSummaries": [],
  "shutdownGracePeriod": "00:00:15"
}
```

## Definition Listing

The definitions endpoint returns all work definitions in the selected system.

```http
GET /workable/definitions
```

Definitions include their invocation configuration. A definition that does not allow `WorkInvocationChannel.HttpApi` still appears in discovery responses so clients can display it as unavailable through HTTP. Queueing that work through HTTP returns a validation response.

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

Get work info by work name or work definition id.

```http
GET /workable/work/email.welcome.send/info
GET /workable/work/id/11111111-1111-1111-1111-111111111111/info
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

HTTP queue options use `WorkableHttpWorkerOptions`. Its `configuration` shape includes start behavior, coordination, recurrence, transient retry, logging, and retention. Coordination selects local or persistent coordination state, then enables duplicate protection, capacity limits, durable queueing, and durable completion under that mode. Retention includes `purgeInterval` and the asynchronously enforced `maximumFinalWorkers` target. Invocation channels are not part of the HTTP queue request because they are definition-level configuration.

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

## UI Views And Components

Dashboard-style screens request view data with component selections. The overview view returns a component map so clients can fetch only the panels they intend to render.

View and component names are HTTP adapter concerns. The in-process `IWorkQueryService` exposes typed system queries such as `SystemDetails`; the HTTP adapter maps view/component requests onto those typed queries.

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

When `components` is omitted, the overview view returns the default non-live components: `system`, `workers`, `failedWorkers`, `iterations`, `failedIterations`, and `completedIterations`. The default overview story uses `standard` for `workers`, `failedWorkers`, `iterations`, `failedIterations`, and `completedIterations`. Add `throughput` to the overview component list only when the throughput panel is visible, and request `catalog` only when the filter UI is open.

Component requests can include a UI shape of `compact`, `standard`, or `detailed`, and each component result echoes the normalized `shape` that was served. Hidden panels should be omitted from the request. Collapsed panels should request `compact` when the component supports it. The HTTP adapter treats shape as an efficiency contract: if a smaller shape can avoid an indexed domain aggregation, the adapter calls the narrower query; if the domain query already has the rows in hand, the adapter still projects the result before serialization so clients only receive fields they can render.

Overview component shape support:

- `system` returns system name and state; shape is accepted for consistency but does not change the payload.
- `workers` supports `compact` and `standard`. `compact` returns active workers, failed workers, and oldest queued time. `standard` adds definition count, final workers, and worker counts by state.
- `failedWorkers` supports `standard` and `detailed`. `standard` returns definition, worker id, revision, updated time, and duration; the failed state is implied by the component. `detailed` adds state and relationship fields used by the detailed table.
- `iterations` supports `compact` and `standard`. `compact` returns `iterationCountByStatus` only. `standard` adds common relationship key-type facets.
- `failedIterations` and `completedIterations` support `standard` and `detailed`. `standard` returns definition, worker id, iteration sequence, completed time, and duration. `detailed` adds worker state and relationship fields.
- `throughput` supports `compact` and `standard`; both use the requested throughput window/options. `compact` returns active worker count plus settled count, live rates, and completed-execution summaries for the pill strip, without chart buckets. `standard` adds chart buckets trimmed to `at`, started/completed/failed/canceled counts, and completed-only average execution milliseconds. `detailed` normalizes to `standard`.
- `workerGrid` and `iterationGrid` support `detailed` for the Workers and Iterations views. They return paged grid rows plus `totalCount`, `skip`, and `take`.

The `workers` component returns worker state counts plus `oldestQueuedAt`, which is the oldest queued worker state-entry timestamp in the requested scope. Queue backlog is reported with worker state counts in the same component.

The `throughput` component returns iteration throughput plus live execution pressure. `throughput.windowSeconds` is the selected throughput/execution window. `throughput.settledCount` is completed plus failed plus canceled iterations in the requested closed-bucket throughput window. `buckets` contains only fully closed time buckets so charts do not include the still-open bucket at the right edge. Each bucket includes started/completed/failed/canceled counts and completed-only average execution milliseconds. `executionSummary` reports exact completed execution count, exact completed-only average execution milliseconds, exact completed-only slowest execution milliseconds, and approximate completed-only p95/p99 execution milliseconds interpolated from fast-work-optimized histogram buckets across the requested closed-bucket throughput window. Failed and canceled iterations remain visible in throughput rates, but they are excluded from execution timing. `liveSummary` reports only live rates for the fixed `rateWindowSeconds` live-rate window through the current moment. `liveSummary.inFlightDeltaPerSecond` is started iterations per second minus completed, failed, and canceled iterations per second.

Clients can also request arbitrary components without binding the request to a named view.

```http
POST /workable/components/query
POST /workable/components/throughput
```

Component and view requests accept an optional `scope`. Scopes can target `definitionId`, `definitionName`, or `category`. When `category` is supplied, `includeSubcategories` defaults to `true`.

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

Get one completed worker iteration by worker id and iteration sequence.

```http
GET /workable/workers/22222222-2222-2222-2222-222222222222/iterations/1
```

Query the workers view. The `workerGrid` component currently supports the `detailed` shape and returns paged worker rows for the screen.

```http
POST /workable/views/workers
Content-Type: application/json

{
  "scope": {
    "definitionName": "email.welcome.send",
    "category": "Email",
    "includeSubcategories": true
  },
  "components": [
    {
      "id": "workerGrid",
      "type": "workerGrid",
      "shape": "detailed",
      "options": {
        "states": [ "Completed", "Failed" ],
        "keyType": "claim",
        "skip": 0,
        "take": 50
      }
    }
  ]
}
```

The worker grid detailed row includes definition, full worker id, revision, state, updated time, duration, subject id, and identifiers. The optional `keyType` filter matches workers with any relationship of that type.

Query the iterations view. The `iterationGrid` component currently supports the `detailed` shape and returns paged iteration rows for the screen.

```http
POST /workable/views/iterations
Content-Type: application/json

{
  "scope": {
    "definitionName": "email.welcome.send"
  },
  "components": [
    {
      "id": "iterationGrid",
      "type": "iterationGrid",
      "shape": "detailed",
      "options": {
        "statuses": [ "Failed", "Completed" ],
        "keyType": "claim",
        "skip": 0,
        "take": 50
      }
    }
  ]
}
```

The iteration grid detailed row includes definition, full worker id, iteration sequence, worker state, iteration status, completed time, duration, subject id, and identifiers. The optional `keyType` filter matches iterations with any relationship of that type.

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
  "revision": 3
}
```

The action route supports `Start`, `Pause`, `Cancel`, `Push`, and `Purge`.

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
  "notFoundCount": 0
}
```

Target workers by work definition category.

```http
POST /workable/workers/actions/pause
Content-Type: application/json

{
  "category": "Billing",
  "includeSubcategories": true
}
```

Runtime reconfiguration uses the same revision rule.

```http
POST /workable/workers/22222222-2222-2222-2222-222222222222/reconfigure
Content-Type: application/json

{
  "revision": 3,
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
  "changes": {
    "profilingEnabled": true
  }
}
```
