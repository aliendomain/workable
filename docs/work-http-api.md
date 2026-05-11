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
POST /workable/systems/email/workers/query
POST /workable/systems/email/workers/22222222-2222-2222-2222-222222222222/actions/cancel
```

Route matching is case-insensitive. Worker action route values are also parsed case-insensitively, so `/actions/cancel`, `/actions/Cancel`, and `/actions/CANCEL` all target `WorkAction.Cancel`.

`AddWorkableHttpApi` configures HTTP JSON enum handling so enum strings in request bodies can also be supplied without matching .NET enum casing exactly.

## Definition Listing

The definitions endpoint returns work definitions that allow HTTP API invocation.

```http
GET /workable/definitions
```

Definitions that do not allow `WorkInvocationChannel.HttpApi` are omitted.

Query definitions with the same filter shape as `IWorkQuery.QueryWorkDefinitions`.

```http
POST /workable/definitions/query
Content-Type: application/json

{
  "category": "Email",
  "includeSubcategories": true
}
```

Get work info by name or definition id.

```http
GET /workable/work/email.welcome.send/info
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

Request completion when the caller needs the final worker output in the HTTP response.

```json
{
  "input": {
    "reportId": "report-123"
  },
  "completion": "WaitForCompletion"
}
```

Queue requests can include worker options and input identity metadata.

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

## Query Workers

Get a worker snapshot.

```http
GET /workable/workers/22222222-2222-2222-2222-222222222222
```

Query worker summaries.

```http
POST /workable/workers/query
Content-Type: application/json

{
  "definitionName": "email.welcome.send",
  "states": [ "Completed", "Failed" ],
  "skip": 0,
  "take": 50
}
```

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
