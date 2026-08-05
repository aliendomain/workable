# Workable HTTP API

Workable can expose queueing, workflow runs, worker operations, system operations, and query APIs through the `Workable.HttpApi` adapter package.

The HTTP API adapter uses the same Workable catalog and queueing system as direct in-process code. A work definition can be queued through HTTP only when its invocation configuration allows `WorkInvocationChannel.HttpApi`.

The built-in HTTP adapter also stamps queued work and worker actions with `WorkOriginSurface.WorkableAdapter`. That keeps built-in `/workable` route usage visibly distinct from host-defined endpoints that use Workable through `IHttpContextWorkCommandDispatcher` or `IWorkRequestContextFactory`, which still use the `HttpApi` channel but default to `WorkOriginSurface.HostApplication`.

Invocation-channel rules matter for work invocation, not for general system/query/worker discovery routes. Definition listing, diagnostics, worker reads, lifecycle routes, and other read/control surfaces are governed by authorization and route shape, not by definition invocation-channel settings.

HTTP queueing, worker actions, and worker reconfiguration record a `WorkRequestContext` from the request. Its nested `Origin` carries the durable actor/channel provenance, and the request context also captures the HTTP path as `RequestContext.Url`. Built-in queue, action, bulk-action, and reconfiguration request bodies can also supply an optional `description` value. For a single-worker action, the adapter maps that wire-level value to `WorkerActionRequest.Reason`, and Workable records it on the action context as `RequestContext.Description`.

`Workable.HttpApi` is an authenticated transport. Anonymous callers are rejected before Workable routes run or request bodies are bound, and mapped systems must be authorization-enabled.

Each request creates a `WorkRequestContext` and an `IWorkSystemSession` for the selected system. Work-definition read access filters catalog, query, event, and view results. Work-definition operate access answers the broad "can this caller operate this definition at all" question, and the runtime then enforces the specific queue, action, or reconfiguration permission required by the current HTTP request.

Inside the built-in adapter, HTTP authorization is orchestrated through a request-scoped cache. The adapter resolves the caller once for the request, caches host-level group checks and per-system access summaries, and reuses that data across outer-gate checks, built-in surface checks, host discovery, and session creation.

If your host also exposes custom controllers or minimal APIs that need to queue work, prefer `IHttpContextWorkCommandDispatcher` from `Workable.AspNetCore` instead of recreating that orchestration yourself. The built-in HTTP adapter follows the same dispatcher-first pattern internally for queue requests.

## Map Endpoints

Map the default Workable API endpoints from the host application.

```csharp
builder.Services.AddWorkableHttpApi();

app.MapWorkableApi();
```

The default prefix is `/workable`.

`MapWorkableApi` always requires authenticated callers. When `WorkableAspNetCoreAuthorizationOptions.TransportAuthenticationScheme` is also set, `MapWorkableApi` adds matching authorization metadata to the mapped endpoints so ASP.NET Core evaluates that specific scheme.

Beyond authentication, the built-in `/workable` routes are system-scoped admin surfaces. A caller must be recognized as either a `SystemAdministrator` or `WorkAdministrator` for the target system before those built-in routes run for that system. That applies to both the default-system routes under `/workable/...` and the named-system routes under `/workable/systems/{systemName}/...`.

The built-in adapter has two authorization gates after transport authentication:

- outer gate: optional host-wide `SurfaceAccessGroups` that control whether the caller may enter the built-in `/workable` surface at all
- inner gate: required system-scoped built-in surface access, granted by `SystemAdministrator`, `WorkAdministrator`, or any groups configured with `AllowBuiltInHttpApiToGroups(...)` for the target system

If the entire built-in `/workable` path should also require one more top-level group before any system-specific surface access is considered, configure `SurfaceAccessGroups` as an outer gate:

```csharp
builder.Services.AddWorkableHttpApi(options =>
{
    options.SurfaceAccessGroups = ["workable.surface"];
});
```

Those groups are evaluated only for routes mapped by `MapWorkableApi(...)`. Host-defined endpoints that use Workable directly are unaffected.
Once `SurfaceAccessGroups` contains at least one group, every caller to every built-in `/workable` route must match at least one configured outer-gate group before any per-system surface decision is considered.

The gate order is:

1. transport authentication
2. optional outer gate via `SurfaceAccessGroups`
3. inner gate for built-in surface access on the target system
4. normal Workable system and work-definition authorization inside the created session

That means the built-in `/workable` routes are intentionally stricter than host-defined HTTP endpoints that dispatch into Workable. A host-defined endpoint can still choose its own authorization model and then call `IHttpContextWorkCommandDispatcher`, `IWorkRequestContextFactory`, or `IWorkSystem.CreateSession(...)` directly.

That transport scheme is not automatic. `AddWorkableHttpApi()` by itself does not choose one. It is commonly set by [Workable.Entra](../guides/entra-authentication.md), or by host code that wants Workable HTTP requests to authenticate with one specific ASP.NET Core scheme instead of inheriting the ambient default.

When a transport scheme is configured, the host pipeline must run authentication and authorization middleware before those endpoints execute. If your host already runs `app.UseAuthentication()` and `app.UseAuthorization()`, no extra step is needed.

```csharp
app.UseAuthentication();
app.UseAuthorization();
app.MapWorkableApi("/internal/work");
```

The default routes target `IWorkSystemRegistry.Default`. The same endpoints are also available for named systems under `/systems/{systemName}`.

```http
GET /workable/systems/email/definitions
POST /workable/systems/email/work/email.welcome.send
GET /workable/systems/email/workers/22222222-2222-2222-2222-222222222222
POST /workable/systems/email/views/workers
GET /workable/systems/email/workflow-runs
POST /workable/systems/email/workers/22222222-2222-2222-2222-222222222222/actions/cancel
```

Route matching is case-insensitive. Worker action route values are also parsed case-insensitively, so `/actions/cancel`, `/actions/Cancel`, and `/actions/CANCEL` all target `WorkAction.Cancel`.

`AddWorkableHttpApi` configures HTTP JSON enum handling so enum strings in request bodies can also be supplied without matching .NET enum casing exactly.

## Queue Work From Your Own HTTP Endpoints

`MapWorkableApi()` is for Workable's built-in transport routes. When your application also needs custom HTTP endpoints that trigger Workable work, the simplest path is `IHttpContextWorkCommandDispatcher`.

```csharp
app.MapPost("/admin/retry-welcome-email/{userId}", async (
    string userId,
    IHttpContextWorkCommandDispatcher commands,
    CancellationToken cancellationToken) =>
{
    var result = await commands.Dispatch<SendWelcomeEmailArgs, object?>(
        "email.welcome.send",
        new SendWelcomeEmailArgs(userId),
        "Retry welcome email from custom admin endpoint.",
        new WorkDispatchOptions(WorkDispatchCompletion.ReturnAfterAccepted),
        cancellationToken);

    return Results.Ok(new
    {
        result.Status,
        result.WorkerId,
        result.ErrorCode,
        result.ErrorMessage,
    });
});
```

That gives custom HTTP code the same behavior the built-in adapter already needs:

- create an HTTP-bound `WorkRequestContext`
- preserve actor, URL, and authenticated-caller information
- resolve the target system
- queue work
- optionally wait for completion
- return a standardized `WorkDispatchResult<T>`

Use `WorkDispatchCompletion.WaitForCompletion` when the endpoint should return the worker's final output instead of returning after acceptance.

Drop down to `IWorkRequestContextFactory` and `IWorkSystem.CreateSession(...)` only when the endpoint needs broader session work such as direct query, worker action, catalog, or lifecycle access.

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
        "persistentCoordinationAvailable": true,
        "sqlProfilingAvailable": true,
        "httpClientProfilingAvailable": true
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

The per-system `capabilities` object is reserved for system-specific runtime behavior. `persistentCoordinationAvailable` tells clients whether that system currently has persistent coordination available through a registered persistence store. In practice, that means persistent coordination settings such as `storage: "Persistent"` can be honored for features like durable queueing, persistence-backed idempotency, and persistence-backed coordination. `sqlProfilingAvailable` and `httpClientProfilingAvailable` report whether the corresponding automatic profiling instrumentation is registered for captured worker profiles.

The systems list is filtered to systems where the caller has actual access. Read access, operate access, diagnostics access, control access, or administrator roles are all enough to make a system visible.
For the built-in HTTP API specifically, `/workable/host` lists only systems where the caller has both:

- built-in surface access for that system (`SystemAdministrator`, `WorkAdministrator`, or a group granted through `AllowBuiltInHttpApiToGroups(...)`)
- some real Workable access inside that system

For named built-in routes such as `/workable/systems/{systemName}/...`, Workable also requires both:

- inner built-in surface access for that system
- some real Workable access in that system

When the caller lacks real system access on a named built-in route, Workable returns the normal system-level authorization failure. When the caller lacks built-in surface access, Workable returns the built-in surface denial.

When realtime is not registered, `enabled` is `false`.

## Request Concurrency

The built-in HTTP adapter caches authorization state in a scoped per-request service. That is what lets the adapter avoid repeating group-provider and `DescribeAccess(...)` work across multiple authorization checks in one request.

That cache currently assumes the built-in adapter performs authorization work in the normal sequential request pipeline. It is not designed for concurrent mutation from multiple parallel authorization tasks inside the same request. If built-in HTTP authorization ever starts evaluating systems in parallel within one request, that cache will need synchronization or a concurrent structure.

## Diagnostics

Read runtime diagnostics for the selected system.

```http
GET /workable/diagnostics
GET /workable/systems/email/diagnostics
```

The response includes queue, read-model, retention, concurrency, durability, and idempotency diagnostics. Use it to monitor alertable queue rejections, query freshness, projector pressure, retention lag, deferred-start backlog, durable coordination lag, duplicate rejection, and internal diagnostics failures.

Diagnostics require the system-level `Diagnostics` permission or `SystemAdministrator`.

## Temporary Full Profile Capture

The built-in API exposes temporary rules that enable full profiling for matching future workers:

```http
GET /workable/profiling/capture-rules
POST /workable/profiling/capture-rules
DELETE /workable/profiling/capture-rules/{ruleId}
```

Named-system variants are available under `/workable/systems/{systemName}/profiling/capture-rules`.

Create a rule for a work definition, stable actor id, or both:

```json
{
  "definitionName": "payment.authorize",
  "actorId": "user-123",
  "maximumMatches": 5,
  "expiresAfterMinutes": 30,
  "description": "Investigate intermittent payment latency"
}
```

At least one of `definitionName` or `actorId` is required. Each selector is limited to 512 characters. `maximumMatches` defaults to `1` and must be between `1` and `1000`. `expiresAfterMinutes` defaults to `30` and must be between `1` and `1440`. A system can have at most 1,000 active rules; creating another returns a validation error. Definition matching is case-insensitive, while actor ids use exact ordinal matching. If several rules match, combined definition-and-actor rules take precedence over single-selector rules; equally specific rules are selected oldest first.

Matching happens transactionally during queue acceptance. Steady-state queue requests read a preordered immutable rule snapshot and atomically reserve a match without taking the rule-administration lock; administration and one-time terminal rule cleanup use the lock. A rejected queue attempt returns its reserved match to the rule. An accepted worker has profiling enabled and retains `profilingCaptureMode: "Full"` in its effective options, including when it is placed in a durable queue. Rules themselves are temporary in-memory operational state. They remain through a system stop/start in the same host but are cleared by a host restart.

`Full` means that automatic SQL, HTTP, and extension nodes do not consume the system's bounded automatic-instrumentation allowance for that worker iteration. It does not bypass queue authorization, invocation-channel restrictions, profile retention, HTTP privacy exclusions, or SQL parameter redaction.

Listing, creating, and deleting rules require diagnostics access. `SystemAdministrator` has diagnostics permission by default, but it does not receive work queue permission merely by creating a rule. The matching worker must still be queued by a caller authorized for that definition and invocation channel.

An authorized definition-reconfiguration request that explicitly sets `profilingCaptureMode` to `Full` also requires diagnostics access in addition to the definition's normal reconfiguration permission. This prevents work-operation permission alone from bypassing the bounded automatic-instrumentation limit. Trusted host startup configuration is unaffected.

Returning retained profile data also requires diagnostics access. Without it, worker and iteration query responses, wait-for-completion results, worker-action outcomes, and system-stop worker snapshots retain their normal authorized fields but expose no latest or per-iteration profile. Work read or operate permission by itself is not sufficient to retrieve profile telemetry.

Every returned profile node includes a required `instrumentation` key. Explicit application profiling uses `application`, Workable's truncation summary uses `workable.profiling`, and the built-in SQL and outbound HTTP integrations use `sql.client` and `http.client`. Clients should classify and filter profile nodes by this field rather than inferring a source from labels or context payloads.

For example, an outbound HTTP timing is serialized with both its generic metric shape and its specific instrumentation identity:

```json
{
  "metricType": "Timing",
  "treeMilliseconds": 42,
  "nodeMilliseconds": 42,
  "label": "HTTP Request",
  "context": {
    "provider": "System.Net.Http",
    "method": "GET",
    "uri": "https://example.test/orders",
    "statusCode": 200,
    "outcome": "Completed"
  },
  "children": [],
  "instrumentation": "http.client"
}
```

`metricType` describes whether the node is a method scope, logical scope, timing, or metric. `instrumentation` identifies who produced it. The latter is the stable filtering contract and is required on every node.

The Workable admin UI exposes the same operations:

- For a work type, select the system, open **Catalog**, select the definition, and use **Full profile capture**.
- For a user, open **Workers**, select a retained worker for that actor, expand **Worker controls**, and use **Capture by user**.
- Use **Capture this user + work type** from the same worker card when both selectors should match.

The selected existing worker supplies an actor id; it is not changed or reprofiled. Capture rules apply only to future accepted workers.

## Diagnostics Response Example

```json
{
  "name": "email",
  "state": "Started",
  "queue": {
    "rejectedWorkCount": 0,
    "lastRejectedAt": null,
    "lastRejectedStatus": null,
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

`GET /workable/definitions` also supports `category`, `includeSubcategories`, and `level` query-string parameters. `level=true` returns the lightweight catalog level for one category instead of full definition records. Those lightweight definition rows include only `name` and `category`.

Read a single full definition by name.

```http
GET /workable/definitions/email.welcome.send
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
POST /workable/definitions/email.welcome.send/reconfigure
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

Get work info by work name. The same definition can be addressed under either `/work/{name}/info` or `/definitions/{name}/info`.

The HTTP `info` payload includes the full definition, worker rollup/status, and `queueRequestSchema`, so clients can open definition configuration or queue UI without a second schema request.

```http
GET /workable/work/email.welcome.send/info
GET /workable/definitions/email.welcome.send/info
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

HTTP queue options use `WorkableHttpWorkerOptions`. Its `configuration` shape includes start behavior, coordination, recurrence, transient retry, failed-worker handling, logging, and retention. Coordination selects local or persistent coordination state, then enables duplicate protection, capacity limits, durable queueing, and durable completion under that mode. Retention includes `purgeInterval` and the asynchronously enforced `maximumFinalWorkers` target. Invocation channels are not part of the HTTP queue request because they are definition-level configuration, not per-request overrides.

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

Component and view requests accept an optional `scope`. Scopes can target `definitionName` or `category`. When `category` is supplied, `includeSubcategories` defaults to `true`. Requests can also supply component `shape` and component-specific `options`.

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

When `activity=Auto`, the server chooses `Timeline` for final or recurring workers and `Logs` otherwise.

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

Get the iteration-overview landing payload used by the iteration screen. This returns the typed `WorkWorkerIterationOverviewComponent` contract and can be trimmed to the panels the UI is actually showing.

```http
GET /workable/workers/22222222-2222-2222-2222-222222222222/iterations/1/overview
GET /workable/workers/22222222-2222-2222-2222-222222222222/iterations/1/overview?activity=None&includeProfile=false&includeInput=false&includeOutput=false
GET /workable/workers/22222222-2222-2222-2222-222222222222/iterations/1/overview?activity=Logs&activityTake=100&logSort=Asc&logLevels=Error,Warning
```

The iteration-overview route accepts:

- `activity`: `Auto`, `None`, `Messages`, or `Logs`
- `activityTake` and `activityCursor`
- `includeInput`, `includeOutput`, and `includeProfile`
- `messageSort` and `severities`
- `logSort` and `logLevels`

When `activity=Auto`, the server prefers `Logs`, then `Messages`, then `None` based on the retained activity present on that iteration.

The legacy iteration `/detail`, `/messages`, and `/logs` routes are not exposed. Use `/overview`, `/overview/messages`, and `/overview/logs`.

Get paged retained logs for one worker iteration. This route returns only the retained-log section contract so expanded iteration log panels can page more history without re-fetching worker context, iteration timing, output, or profile data.

```http
GET /workable/workers/22222222-2222-2222-2222-222222222222/iterations/1/overview/logs?take=50&sort=Desc
GET /workable/workers/22222222-2222-2222-2222-222222222222/iterations/1/overview/logs?take=50&sort=Asc&logLevels=Error,Warning
GET /workable/workers/22222222-2222-2222-2222-222222222222/iterations/1/overview/logs?take=50&cursor=eyJvY2N1cnJlZEF0IjoiMjAyNi0wNS0yOVQxMTowMDowMloiLCJpZCI6IjEyMyJ9
```

The iteration-logs route accepts:

- `take`
- `cursor`
- `sort`: `Asc` or `Desc`
- `logLevels`: comma-separated log levels such as `Error,Warning`

Iteration-log rows use the same retained log-entry shape as worker-overview log pages, including `occurredAt`, stable entry ids, iteration `sequence`, and per-iteration `ordinal`.

Get paged retained messages for one worker iteration. This route returns only the retained-message section contract so compact severity totals and expanded message pages can be loaded independently from the rest of the iteration landing payload.

```http
GET /workable/workers/22222222-2222-2222-2222-222222222222/iterations/1/overview/messages?take=50&sort=Desc
GET /workable/workers/22222222-2222-2222-2222-222222222222/iterations/1/overview/messages?take=50&sort=Asc&severities=Information,Warning
GET /workable/workers/22222222-2222-2222-2222-222222222222/iterations/1/overview/messages?take=50&cursor=50
```

The iteration-messages route accepts:

- `take`
- `cursor`
- `sort`: `Asc` or `Desc`
- `severities`: comma-separated `WorkMessageSeverity` values such as `Information,Warning`

Retained `WorkMessage` payloads include `occurredAt` in addition to `code`, `severity`, `text`, optional `target`, and optional `metadata`.

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

## Workflow Runs

Start a registered workflow by name. The body is optional; by default the route returns after acceptance.

```http
POST /workable/workflows/orders.fulfillment
Content-Type: application/json

{
  "input": {
    "orderId": "order-123"
  },
  "description": "Start fulfillment from the operations console.",
  "completion": "ReturnAfterAccepted"
}
```

The start request also accepts optional `subjectId`, `concurrencyKey`, and `identifiers` values that are attached to the workflow input. Set `completion` to `WaitForCompletion` when the response should wait for a terminal workflow-run result.

List visible active workflow runs, optionally including final runs or filtering by workflow definition name.

```http
GET /workable/workflow-runs?includeFinal=true&definitionName=orders.fulfillment&childSampleSize=3
```

Read one workflow run and its operator-oriented step graph.

```http
GET /workable/workflow-runs/33333333-3333-3333-3333-333333333333?childSampleSize=3
```

Page through the child workers associated with one selected workflow node. The node can be a dispatch, fan-out, parallel, or branch structure node.

```http
GET /workable/workflow-runs/33333333-3333-3333-3333-333333333333/steps/release-streams/children?skip=0&take=25
```

Operate an existing workflow run with `start`, `pause`, or `cancel`:

```http
POST /workable/workflow-runs/33333333-3333-3333-3333-333333333333/actions/pause
Content-Type: application/json

{
  "description": "Pause the release while the downstream API is unavailable."
}
```

`stop` remains a compatibility alias for `pause`. Workflow action route values are parsed case-insensitively. The named-system forms place the same paths under `/workable/systems/{systemName}`.

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

The action route supports `Start`, `Pause`, `Cancel`, `Push`, and `Purge`. `description` remains the public wire name and is mapped to `WorkerActionRequest.Reason`. Workable records it as the action history's `RequestContext.Description`; for an accepted cancel of running code, the same value is available to executor code through `IWorkExecutionContext.CancellationRequestContext.Description` before the execution token is signaled.

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
