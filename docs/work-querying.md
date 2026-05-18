# Work Querying

## Intent

Workable query APIs provide read-only access to worker state and registered work definitions. Use the methods on `IWorkSystem.Query` to inspect work without changing it. `IWorkQueryService` is intentionally discoverable: each built-in query has its own method.

Runtime worker and iteration queries are served from an in-memory read model. Worker lifecycle code publishes lightweight updates to a projector, and query methods read the latest published snapshot instead of locking worker records. The read model starts empty with the process and is cleared when the in-memory system stops; it is eventually consistent with worker execution, while query methods wait for updates already accepted by the projector before returning.

## Read Model Boundaries

`IWorkSystem.Query` is the display and inspection read surface. It uses the projected read model for worker detail, worker rows, iteration detail, iteration rows, key search, system details, and overview component views.

Control and correctness paths continue to read live worker records. This includes idempotency checks during queue acceptance, concurrency reservations, worker actions and reconfiguration, shutdown cancellation, retention purge selection, and bulk action execution. Those paths need authoritative current state and optimistic concurrency behavior, so they do not rely on the eventually consistent read model.

## Read Model Diagnostics

`IWorkSystem.Diagnostics.ReadModel` exposes projection counters for operators and performance tests:

- `EnqueuedSequence` and `AppliedSequence` show read-model lag.
- `PendingUpdateCount` is the current sequence gap.
- `AppliedUpdateCount`, `PublishedSnapshotCount`, and `LastBatchSize` show projector activity.
- `LastProjectionDuration` and `LastProjectedAt` show recent projection cost and recency.
- `HasProjectorFailure`, `ProjectorFailureType`, and `ProjectorFailureMessage` surface projector failures.

The read-model channel is unbounded so accepted updates are not dropped. Treat sustained `PendingUpdateCount` growth or rising `LastProjectionDuration` as pressure signals for tuning projection batching, snapshot shape, or publish cadence.

See [Work Diagnostics](work-diagnostics.md) for the full diagnostics model and warning guidance.

## Worker Queries

Use `IWorkQueryService.Worker` when you need full worker detail.

```csharp
WorkerSnapshot? worker = await workSystem.Query.Worker(workerId, cancellationToken: cancellationToken);
```

`WorkerSnapshot.ActionHistory` records worker action and reconfiguration attempts that were applied to that worker. Each entry includes the operation kind, action when applicable, outcome status, origin, revision, state sequence, messages, and requested reconfiguration changes when applicable.

Use `IWorkQueryService.Workers` to retrieve workers that match a `WorkerCriteria`. It returns `WorkerOverviewItem` rows instead of full snapshots.

```csharp
var result = await workSystem.Query.Workers(new WorkerCriteria(
            DefinitionName: "email.welcome.send",
            SubjectId: new WorkSubjectId("user", "user-123"),
            Identifier: new WorkIdentifier("order", "order-456"),
            Take: 50), cancellationToken: cancellationToken);

foreach (var worker in result.Workers)
{
    WorkerId id = worker.Id;
    WorkSubjectId? subject = worker.SubjectId;
    WorkerState state = worker.State;
}
```

Worker queries can filter by work definition id, work definition name, category, worker state, relationship keys, selected configuration flags, created time, and updated time.

Worker queries are paged. If `Take` is omitted, zero, or negative, Workable returns up to `50` workers. Requests for more than `50` workers are capped at `50`; use `Skip` to page through larger result sets.

Filter by worker state to retrieve workers in any lifecycle status.

```csharp
WorkerQueryResult running =
    await workSystem.Query.Workers(new WorkerCriteria(
            States: new HashSet<WorkerState> { WorkerState.Running }), cancellationToken: cancellationToken);
```

### Worker Query Filters

Use `WorkerCriteria` relationship filters when the caller already knows the exact key to search for.

- `SubjectId` filters by the main business subject of work, such as a user, customer, claim, order, or invoice.
- `ConcurrencyKey` filters by a capacity grouping key, such as tenant, account, region, or external system.
- `Identifier` filters by a secondary relationship marker, such as an email message id, invoice id, batch id, or downstream job id.

All three relationship filters use a `type` and `value`. For example, `new WorkSubjectId("claim", "CLM-123")` and `new WorkIdentifier("claim", "CLM-123")` are different filters even though they carry the same text.

Relationship keys are supplied when queueing work, and identifiers can also be discovered during execution. See [work-queueing.md](work-queueing.md) for the queueing-side details.

Use `WorkerConfigurationCriteria` to filter by selected effective worker configuration and options. These filters are indexed.

```csharp
WorkerQueryResult recurringProfiledWorkers =
    await workSystem.Query.Workers(new WorkerCriteria(
            Configuration: new WorkerConfigurationCriteria(
                RecurrenceEnabled: true,
                ProfilingEnabled: true)), cancellationToken: cancellationToken);
```

Configuration filters currently support `RecurrenceEnabled`, `ConcurrencyEnabled`, and `ProfilingEnabled`.

Use `Category` to retrieve workers for every definition in a category path. With `IncludeSubcategories` enabled, `Billing` includes workers for definitions registered under `Billing:Invoices` and `Billing:Renewals`.

```csharp
WorkerQueryResult billingWorkers =
    await workSystem.Query.Workers(new WorkerCriteria(
            Category: "Billing",
            IncludeSubcategories: true), cancellationToken: cancellationToken);
```

## Iteration Queries

Workers expose the current and last iteration sequence on `WorkerSnapshot` so callers can cheaply know which iteration is active or most recently completed.

```csharp
WorkerSnapshot? worker = await workSystem.Query.Worker(workerId, cancellationToken: cancellationToken);

long? current = worker?.CurrentIterationSequence;
long? last = worker?.LastIterationSequence;
```

Use `IWorkQueryService.WorkerIteration` when you need one full iteration snapshot by worker id and sequence.

```csharp
WorkerIterationSnapshot? iteration =
    await workSystem.Query.WorkerIteration(new WorkerIterationReference(workerId, sequence: 1), cancellationToken: cancellationToken);
```

Use `IWorkQueryService.WorkerIterations` to retrieve lightweight iteration rows across workers.

```csharp
WorkerIterationQueryResult iterations =
    await workSystem.Query.WorkerIterations(new WorkerIterationCriteria(
            DefinitionName: "email.welcome.send",
            Statuses: new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Failed },
            Identifier: new WorkIdentifier("claim", "CLM-123")), cancellationToken: cancellationToken);
```

Iteration queries can filter by worker id, work definition id, work definition name, category, completion status, relationship keys, started time, and completed time. `WorkCompletionStatus.Executing` represents the current iteration while developer code is executing and can be used anywhere other iteration statuses can be filtered. The result rows include worker id, iteration sequence, definition identity, category, worker state, completion status, timing, and relationship keys.

## Work Key Search

Use work key search when the caller does not yet know the exact relationship filter to use.

`IWorkQueryService.WorkerKeys` searches across subjects, concurrency keys, and identifiers at the worker level. It returns matching keys and the `WorkerOverviewItem` rows attached to each key.

```csharp
WorkerKeyQueryResult keys = await workSystem.Query.WorkerKeys(new WorkerKeyCriteria(
        Search: "claim id CLM-123",
        States: new HashSet<WorkerState> { WorkerState.Running }), cancellationToken: cancellationToken);

foreach (var key in keys.Keys)
{
    WorkKeyKind kind = key.Kind; // Subject, ConcurrencyKey, or Identifier
    string type = key.Type;
    string value = key.Value;
    IReadOnlyList<WorkerOverviewItem> workers = key.Workers;
}
```

Use `IWorkQueryService.WorkerKeyTypes` when the caller only knows the type of relationship they are looking for, such as claim work or customer work. It returns matching key types and the worker overview rows attached to all keys of each type across subjects, concurrency keys, and identifiers.

```csharp
WorkerKeyTypeQueryResult types = await workSystem.Query.WorkerKeyTypes(new WorkerKeyTypeCriteria(Search: "claim work", Skip: 0, Take: 50), cancellationToken: cancellationToken);
```

`IWorkQueryService.WorkIterationKeys` and `IWorkQueryService.WorkIterationKeyTypes` use the same key concepts but return `WorkerIterationOverviewItem` rows. Use them when the caller wants actual execution rows, failed attempts, completed iterations, or recurring activity.

```csharp
WorkIterationKeyQueryResult iterationKeys =
    await workSystem.Query.WorkIterationKeys(new WorkIterationKeyCriteria(
            Search: "claim id CLM-123",
            Statuses: new HashSet<WorkCompletionStatus> { WorkCompletionStatus.Failed }), cancellationToken: cancellationToken);

WorkIterationKeyTypeQueryResult iterationTypes =
    await workSystem.Query.WorkIterationKeyTypes(new WorkIterationKeyTypeCriteria(Search: "claim work", Skip: 0, Take: 50), cancellationToken: cancellationToken);
```

`Search` is a free-text convenience over key type and value. Exact `Kind`, `Type`, `Value`, `States`, and `Statuses` filters are available when the caller already knows part of the key shape. Key type queries are paginated and can also use exact `Type` matching.

## Work Definition Queries

Use `IWorkQueryService.WorkDefinitions` to retrieve registered work definitions.

```csharp
IReadOnlyList<WorkDefinition> definitions =
    (await workSystem.Query.WorkDefinitions(new WorkDefinitionCriteria(
            Category: "Email",
            Search: "welcome"), cancellationToken: cancellationToken)).Definitions;
```

`WorkDefinition.Name` is the unique queue/query name. `WorkDefinition.Category` can use colon-delimited paths such as `Email:Lifecycle`.

Use the catalog when you only need definitions in a category.

```csharp
IReadOnlyList<WorkDefinition> emailDefinitions =
    workSystem.Catalog.ListByCategory("Email");
```

Category lookup is case-insensitive. With `includeSubcategories` enabled, `Email` includes definitions registered under categories such as `Email:Lifecycle` and `Email:Reports`.

## Work Info

Use `IWorkQueryService.WorkInfo` to retrieve one definition plus its current worker rollup.

```csharp
WorkInfo? info = await workSystem.Query.WorkInfo("email.welcome.send", cancellationToken: cancellationToken);

if (info is not null)
{
    WorkDefinition definition = info.Definition;
    WorkDefinitionStatus status = info.Status;
    WorkerRollup workers = info.Workers;
}
```

The rollup includes total, active, queued, running, waiting, paused, failed, canceled, completed, and last activity values.

## Status Summary

Use `IWorkQueryService.WorkerStatusSummary` for counts by worker state.

```csharp
WorkerStatusSummary summary = await workSystem.Query.WorkerStatusSummary(new WorkerCriteria(DefinitionName: "email.welcome.send"), cancellationToken: cancellationToken);
```

Call `IWorkQueryService.WorkerStatusSummary` without criteria to summarize all workers in the system.

## Returned Structures

The examples below show the serialized JSON shape returned by HTTP and MCP adapters. In-process .NET callers receive the same records as CLR objects. JSON property names are camel-case, enum values are strings, and nullable properties are represented as `null`.

### Worker Overview Item

`IWorkQueryService.Workers` returns a `WorkerQueryResult` containing `WorkerOverviewItem` rows.

```json
{
  "workers": [
    {
      "id": { "value": "00000000-0000-0000-0000-000000000000" },
      "definitionId": { "value": "00000000-0000-0000-0000-000000000000" },
      "definitionName": "email.welcome.send",
      "subjectId": { "type": "user", "value": "user-123" },
      "concurrencyKey": { "type": "tenant", "value": "tenant-456" },
      "identifiers": [
        { "type": "order", "value": "order-789" }
      ],
      "revision": 3,
      "category": "Email",
      "state": "Completed",
      "createdAt": "2026-05-11T12:00:00Z",
      "updatedAt": "2026-05-11T12:00:03Z",
      "queueDuration": "00:00:00.0500000",
      "totalExecutionDuration": "00:00:01",
  "nextRunAt": null
    }
  ],
  "totalCount": 1,
  "skip": 0,
  "take": 50
}
```

### Worker Iteration Overview Item

`IWorkQueryService.WorkerIterations` returns a `WorkerIterationQueryResult` containing `WorkerIterationOverviewItem` rows.

```json
{
  "iterations": [
    {
      "workerId": { "value": "00000000-0000-0000-0000-000000000000" },
      "sequence": 2,
      "definitionId": { "value": "00000000-0000-0000-0000-000000000000" },
      "definitionName": "email.welcome.send",
      "category": "Email",
      "workerState": "Completed",
      "status": "Completed",
      "startedAt": "2026-05-11T12:00:02Z",
      "completedAt": "2026-05-11T12:00:03Z",
      "executionDuration": "00:00:01",
      "subjectId": { "type": "user", "value": "user-123" },
      "concurrencyKey": { "type": "tenant", "value": "tenant-456" },
      "identifiers": [
        { "type": "claim", "value": "CLM-123" }
      ]
    }
  ],
  "totalCount": 1,
  "skip": 0,
  "take": 50
}
```

`IWorkQueryService.WorkerIteration` returns the full retained `WorkerIterationSnapshot`, including output, messages, logs, and profile for that iteration.

```json
{
  "sequence": 2,
  "startedAt": "2026-05-11T12:00:02Z",
  "completedAt": "2026-05-11T12:00:03Z",
  "executionDuration": "00:00:01",
  "occurredAt": "2026-05-11T12:00:03Z",
  "status": "Completed",
  "output": {
    "json": "{\"sent\":true}",
    "contentType": "application/json"
  },
  "messages": [],
  "logs": [],
  "profile": null
}
```

### Worker Key Results

`IWorkQueryService.WorkerKeys` returns known subject, concurrency key, and identifier values with the workers attached to each key.

```json
{
  "keys": [
    {
      "kind": "Subject",
      "type": "claim",
      "value": "CLM-123",
      "workers": [
        {
          "id": { "value": "00000000-0000-0000-0000-000000000000" },
          "definitionId": { "value": "00000000-0000-0000-0000-000000000000" },
          "definitionName": "claim.review",
          "subjectId": { "type": "claim", "value": "CLM-123" },
          "concurrencyKey": null,
          "identifiers": [],
          "revision": 0,
          "category": "Claims",
          "state": "Running",
          "createdAt": "2026-05-11T12:00:00Z",
          "updatedAt": "2026-05-11T12:00:01Z"
        }
      ]
    }
  ],
  "totalCount": 1,
  "skip": 0,
  "take": 50
}
```

`IWorkQueryService.WorkerKeyTypes` returns the known key types with the workers attached to all keys of that type. Key type results group by `type` first; `workerCount` counts each worker once per type even if the worker has the same type as a subject, concurrency key, and identifier.

```json
{
  "types": [
    {
      "type": "claim",
      "workerCount": 2,
      "workerCountByKind": {
        "Subject": 1,
        "ConcurrencyKey": 1,
        "Identifier": 2
      },
      "workers": [
        {
          "id": { "value": "00000000-0000-0000-0000-000000000000" },
          "definitionId": { "value": "00000000-0000-0000-0000-000000000000" },
          "definitionName": "claim.review",
          "subjectId": { "type": "claim", "value": "CLM-123" },
          "concurrencyKey": null,
          "identifiers": [
            { "type": "claim-note", "value": "note-456" }
          ],
          "revision": 0,
          "category": "Claims",
          "state": "Completed",
          "createdAt": "2026-05-11T12:00:00Z",
          "updatedAt": "2026-05-11T12:00:03Z"
        }
      ]
    }
  ],
  "totalCount": 1,
  "skip": 0,
  "take": 50
}
```

### Iteration Key Results

`IWorkQueryService.WorkIterationKeys` returns known subject, concurrency key, and identifier values with the worker iterations attached to each key.

```json
{
  "keys": [
    {
      "kind": "Subject",
      "type": "claim",
      "value": "CLM-123",
      "iterations": [
        {
          "workerId": { "value": "00000000-0000-0000-0000-000000000000" },
          "sequence": 2,
          "definitionId": { "value": "00000000-0000-0000-0000-000000000000" },
          "definitionName": "claim.review",
          "category": "Claims",
          "workerState": "Completed",
          "status": "Completed",
          "startedAt": "2026-05-11T12:00:02Z",
          "completedAt": "2026-05-11T12:00:03Z",
          "executionDuration": "00:00:01",
          "subjectId": { "type": "claim", "value": "CLM-123" },
          "concurrencyKey": null,
          "identifiers": []
        }
      ]
    }
  ],
  "totalCount": 1,
  "skip": 0,
  "take": 50
}
```

`IWorkQueryService.WorkIterationKeyTypes` returns the known key types with the worker iterations attached to all keys of that type. Key type results group by `type` first; `iterationCount` counts each iteration once per type even if the iteration has the same type as a subject, concurrency key, and identifier.

```json
{
  "types": [
    {
      "type": "claim",
      "iterationCount": 2,
      "iterationCountByKind": {
        "Subject": 1,
        "ConcurrencyKey": 1,
        "Identifier": 2
      },
      "iterations": []
    }
  ],
  "totalCount": 1,
  "skip": 0,
  "take": 50
}
```

### Worker Snapshot

`IWorkQueryService.Worker` returns a full `WorkerSnapshot`. It includes the same worker identity, relationship, and state fields as `WorkerOverviewItem`, plus input, output, options, configuration, messages, origin, retained iterations, captured logs, durable action history, timing fields, and the latest profile snapshot when profiling is enabled.

```json
{
  "id": { "value": "00000000-0000-0000-0000-000000000000" },
  "revision": 3,
  "stateSequence": 5,
  "definitionId": { "value": "00000000-0000-0000-0000-000000000000" },
  "definitionName": "email.welcome.send",
  "definitionCategory": "Email",
  "subjectId": { "type": "user", "value": "user-123" },
  "concurrencyKey": { "type": "tenant", "value": "tenant-456" },
  "identifiers": [
    { "type": "order", "value": "order-789" }
  ],
  "origin": {
    "id": { "value": "00000000-0000-0000-0000-000000000000" },
    "createdAt": "2026-05-11T12:00:00Z",
    "channel": "Mcp",
    "actor": {
      "id": "assistant-user",
      "name": "Assistant User"
    },
    "description": "MCP tool 'workable_work_email_welcome_send'",
    "url": "/workable/mcp"
  },
  "state": "Completed",
  "input": {
    "json": "{\"userId\":\"user-123\"}",
    "clrType": "Sample.SendWelcomeEmailInput, Sample",
    "contentType": "application/json",
    "subjectId": { "type": "user", "value": "user-123" },
    "concurrencyKey": { "type": "tenant", "value": "tenant-456" },
    "identifiers": [
      { "type": "order", "value": "order-789" }
    ]
  },
  "output": {
    "json": "{\"sent\":true}",
    "clrType": "Sample.SendWelcomeEmailOutput, Sample",
    "contentType": "application/json"
  },
  "options": {
    "profilingEnabled": true,
    "configuration": null
  },
  "configuration": {
    "start": {
      "policy": "StartAndReturnAfterAccepted"
    },
    "idempotency": {
      "isEnabled": false,
      "conflictPolicy": "RejectDuplicates"
    },
    "recurrence": {
      "isEnabled": false,
      "interval": "00:00:00",
      "continueAfterFailure": true,
      "circuitBreakerFailureThreshold": 3,
      "retainedSuccessfulIterations": 25,
      "retainedFailedIterations": 5,
      "raiseCircuitBreakerOpenedEvent": true
    },
    "transientRetry": {
      "count": 0,
      "initialDelay": "00:00:00.8000000",
      "jitter": "00:00:00.5000000",
      "maximumDelay": "00:00:30",
      "backoff": "Exponential"
    },
    "logging": {
      "isEnabled": true,
      "level": "Information",
      "maximumBufferedEntries": 100
    },
    "retention": {
      "purgeInterval": "00:10:00",
      "maximumFinalWorkers": 1000
    },
    "concurrency": {
      "isEnabled": false,
      "maximumCapacity": 0,
      "scope": "PerDefinition",
      "blockingMode": "WhileExecutingPausedOrFailed",
      "limitReachedBehavior": "Ignore",
      "overrideBehavior": "Flexible"
    },
    "invocation": {
      "allowedChannels": ["DotNet", "HttpApi"]
    }
  },
  "messages": [
    {
      "code": "email.sent",
      "severity": "Info",
      "text": "Email was sent.",
      "target": "email"
    }
  ],
  "createdAt": "2026-05-11T12:00:00Z",
  "updatedAt": "2026-05-11T12:00:03Z",
  "queueDuration": "00:00:00.0500000",
  "totalExecutionDuration": "00:00:01",
  "nextRunAt": null,
  "currentIterationSequence": null,
  "lastIterationSequence": 1,
  "version": {
    "workerId": { "value": "00000000-0000-0000-0000-000000000000" },
    "revision": 3
  },
  "iterations": [
    {
      "sequence": 1,
      "startedAt": "2026-05-11T12:00:00Z",
      "completedAt": "2026-05-11T12:00:01Z",
      "executionDuration": "00:00:01",
      "occurredAt": "2026-05-11T12:00:01Z",
      "status": "Completed",
      "output": {
        "json": "{\"sent\":true}",
        "contentType": "application/json"
      },
      "messages": [],
      "logs": []
    }
  ],
  "lastIteration": {
    "sequence": 1,
    "startedAt": "2026-05-11T12:00:00Z",
    "completedAt": "2026-05-11T12:00:01Z",
    "executionDuration": "00:00:01",
    "occurredAt": "2026-05-11T12:00:01Z",
    "status": "Completed",
    "messages": []
  },
  "logs": [
    {
      "occurredAt": "2026-05-11T12:00:01Z",
      "workerId": { "value": "00000000-0000-0000-0000-000000000000" },
      "definitionId": { "value": "00000000-0000-0000-0000-000000000000" },
      "category": "Sample.EmailSender",
      "level": "Information",
      "eventId": {
        "id": 100,
        "name": "Sent"
      },
      "message": "Email sent."
    }
  ],
  "actionHistory": [
    {
      "occurredAt": "2026-05-11T12:00:02Z",
      "kind": "WorkerAction",
      "action": "Cancel",
      "status": "Accepted",
      "origin": {
        "id": { "value": "00000000-0000-0000-0000-000000000000" },
        "createdAt": "2026-05-11T12:00:02Z",
        "channel": "HttpApi",
        "actor": {
          "id": "user-123"
        },
        "description": "Apply worker action 'Cancel' through HTTP API.",
        "url": "/workable/workers/00000000-0000-0000-0000-000000000000/actions/cancel"
      },
      "revision": 2,
      "stateSequence": 4,
      "messages": []
    },
    {
      "occurredAt": "2026-05-11T12:00:03Z",
      "kind": "Reconfiguration",
      "status": "Accepted",
      "origin": {
        "id": { "value": "00000000-0000-0000-0000-000000000000" },
        "createdAt": "2026-05-11T12:00:03Z",
        "channel": "Mcp",
        "actor": {
          "id": "assistant-user"
        },
        "description": "MCP tool 'workable_reconfigure_worker'",
        "url": "/workable/mcp"
      },
      "revision": 3,
      "stateSequence": 4,
      "messages": [],
      "reconfiguration": {
        "profilingEnabled": true
      }
    }
  ],
  "profile": {
    "root": {
      "metricType": "Scope",
      "treeMilliseconds": 1000,
      "nodeMilliseconds": 20,
      "label": "Worker 00000000-0000-0000-0000-000000000000 email.welcome.send",
      "context": null,
      "children": []
    },
    "startedAt": "2026-05-11T12:00:00Z",
    "capturedAt": "2026-05-11T12:00:01Z"
  }
}
```

### Work Definition

`IWorkQueryService.WorkDefinitions` returns `WorkDefinition` records. `IWorkQueryService.WorkInfo` includes one `WorkDefinition` inside its response.

```json
[
  {
    "id": { "value": "00000000-0000-0000-0000-000000000000" },
    "name": "email.welcome.send",
    "category": "Email",
    "description": "Sends a welcome email.",
    "inputSchema": {
      "jsonSchema": "{\"type\":\"object\"}",
      "contentType": "application/schema+json",
      "schemaDialect": "https://json-schema.org/draft/2020-12/schema"
    },
    "outputSchema": {
      "jsonSchema": "{\"type\":\"object\"}",
      "contentType": "application/schema+json",
      "schemaDialect": "https://json-schema.org/draft/2020-12/schema"
    },
    "defaultOptions": {
      "profilingEnabled": false,
      "configuration": null
    },
    "configuration": {
      "start": {
        "policy": "StartAndReturnAfterAccepted"
      },
      "idempotency": {
        "isEnabled": false,
        "conflictPolicy": "RejectDuplicates"
      },
      "recurrence": {
        "isEnabled": false,
        "interval": "00:00:00",
        "continueAfterFailure": true,
        "circuitBreakerFailureThreshold": 3,
        "retainedSuccessfulIterations": 25,
        "retainedFailedIterations": 5,
        "raiseCircuitBreakerOpenedEvent": true
      },
      "transientRetry": {
        "count": 0,
        "initialDelay": "00:00:00.8000000",
        "jitter": "00:00:00.5000000",
        "maximumDelay": "00:00:30",
        "backoff": "Exponential"
      },
      "logging": {
        "isEnabled": true,
        "level": "Information",
        "maximumBufferedEntries": 100
      },
      "retention": {
        "purgeInterval": "00:10:00",
        "maximumFinalWorkers": 1000
      },
      "concurrency": {
        "isEnabled": false,
        "maximumCapacity": 0,
        "scope": "PerDefinition",
        "blockingMode": "WhileExecutingPausedOrFailed",
        "limitReachedBehavior": "Ignore",
        "overrideBehavior": "Flexible"
      },
      "invocation": {
        "allowedChannels": ["DotNet", "HttpApi"]
      }
    },
    "metadata": {
      "purpose": "Send onboarding communication.",
      "risk": "Low",
      "requiresApproval": false,
      "capabilities": []
    },
    "revision": 0
  }
]
```

### Work Info

`IWorkQueryService.WorkInfo` returns one definition plus its current status and worker rollup.

```json
{
  "definition": {
    "id": { "value": "00000000-0000-0000-0000-000000000000" },
    "name": "email.welcome.send",
    "category": "Email",
    "description": "Sends a welcome email.",
    "inputSchema": {
      "jsonSchema": "{\"type\":\"object\"}",
      "contentType": "application/schema+json",
      "schemaDialect": "https://json-schema.org/draft/2020-12/schema"
    },
    "outputSchema": {
      "jsonSchema": "{\"type\":\"object\"}",
      "contentType": "application/schema+json",
      "schemaDialect": "https://json-schema.org/draft/2020-12/schema"
    },
    "defaultOptions": {
      "profilingEnabled": false,
      "configuration": null
    },
    "configuration": {
      "start": {
        "policy": "StartAndReturnAfterAccepted"
      },
      "idempotency": {
        "isEnabled": false,
        "conflictPolicy": "RejectDuplicates"
      },
      "recurrence": {
        "isEnabled": false,
        "interval": "00:00:00",
        "continueAfterFailure": true,
        "circuitBreakerFailureThreshold": 3,
        "retainedSuccessfulIterations": 25,
        "retainedFailedIterations": 5,
        "raiseCircuitBreakerOpenedEvent": true
      },
      "transientRetry": {
        "count": 0,
        "initialDelay": "00:00:00.8000000",
        "jitter": "00:00:00.5000000",
        "maximumDelay": "00:00:30",
        "backoff": "Exponential"
      },
      "logging": {
        "isEnabled": true,
        "level": "Information",
        "maximumBufferedEntries": 100
      },
      "retention": {
        "purgeInterval": "00:10:00",
        "maximumFinalWorkers": 1000
      },
      "concurrency": {
        "isEnabled": false,
        "maximumCapacity": 0,
        "scope": "PerDefinition",
        "blockingMode": "WhileExecutingPausedOrFailed",
        "limitReachedBehavior": "Ignore",
        "overrideBehavior": "Flexible"
      },
      "invocation": {
        "allowedChannels": ["DotNet", "HttpApi"]
      }
    },
    "metadata": null,
    "revision": 0
  },
  "status": "Healthy",
  "workers": {
    "total": 10,
    "active": 2,
    "queued": 1,
    "running": 1,
    "waiting": 0,
    "paused": 0,
    "failed": 1,
    "canceled": 2,
    "completed": 5,
    "lastActivityAt": "2026-05-11T12:00:03Z"
  }
}
```

### Worker Status Summary

`IWorkQueryService.WorkerStatusSummary` returns counts by worker state for the whole system or the supplied worker criteria.

```json
{
  "total": 10,
  "active": 3,
  "final": 7,
  "counts": {
    "Queued": 1,
    "Running": 1,
    "Waiting": 1,
    "Paused": 0,
    "Failed": 1,
    "Canceled": 2,
    "Completed": 4
  }
}
```

### System Aggregates

`IWorkQueryService.SystemDetails` returns the broad whole-system diagnostic shape with catalog entries, worker state, queue pressure, iteration activity, common key types, recent failed workers, and recent failed/completed iterations.

System queries can be scoped to one category path or work definition while keeping the same return shape.

```csharp
WorkSystemDetails billingDetails = await workSystem.Query.SystemDetails(new WorkSystemCriteria(Category: "Billing"));

WorkSystemDetails definitionDetails = await workSystem.Query.SystemDetails(new WorkSystemCriteria(DefinitionName: "billing.invoice.sync"));
```

With category scoping, `IncludeSubcategories` defaults to `true`. Scoped system counts, queue pressure, common key types, failed workers, and recent iterations include only workers and iterations for matching definitions. `OldestQueuedAt` is maintained by definition in the worker index, so category scopes find the oldest queued timestamp by checking the matching definitions instead of enumerating queued workers.

For incremental refreshes, the system state can be queried in smaller slices:

- `IWorkQueryService.SystemWorkerCounts`
- `IWorkQueryService.SystemIterationCounts`
- `IWorkQueryService.SystemCommonKeyTypes`
- `IWorkQueryService.SystemFailedWorkers` returns worker counts and the recent failed worker rows.
- `IWorkQueryService.SystemFailedIterations`
- `IWorkQueryService.SystemCompletedIterations`

```json
{
  "systemName": "email",
  "systemState": "Started",
  "definitionCount": 3,
  "activeWorkerCount": 3,
  "finalWorkerCount": 6,
  "failedWorkerCount": 1,
  "workerCountByState": {
    "Queued": 1,
    "Running": 1,
    "Waiting": 1,
    "Failed": 1,
    "Canceled": 2,
    "Completed": 4
  },
  "oldestQueuedAt": "2026-05-11T12:00:00Z",
  "currentIterationCount": 1,
  "completedIterationCount": 4,
  "failedIterationCount": 1,
  "canceledIterationCount": 2,
  "iterationCountByStatus": {
    "Executing": 1,
    "Completed": 4,
    "Failed": 1,
    "Canceled": 2
  },
  "commonKeyTypes": [
    {
      "type": "claim",
      "iterationCount": 8,
      "iterationCountByKind": {
        "Subject": 6,
        "ConcurrencyKey": 2,
        "Identifier": 5
      }
    }
  ],
  "failedWorkers": [
    {
      "id": { "value": "00000000-0000-0000-0000-000000000000" },
      "definitionId": { "value": "00000000-0000-0000-0000-000000000000" },
      "definitionName": "email.digest.send",
      "subjectId": { "type": "claim", "value": "CLM-123" },
      "concurrencyKey": null,
      "identifiers": [],
      "revision": 4,
      "category": "Email",
      "state": "Failed",
      "createdAt": "2026-05-11T12:00:00Z",
      "stateChangedAt": "2026-05-11T12:00:03Z",
      "updatedAt": "2026-05-11T12:00:03Z",
      "queueDuration": null,
      "totalExecutionDuration": "00:00:01",
      "nextRunAt": null
    }
  ],
  "failedIterations": [
    {
      "workerId": { "value": "00000000-0000-0000-0000-000000000000" },
      "sequence": 2,
      "definitionId": { "value": "00000000-0000-0000-0000-000000000000" },
      "definitionName": "email.digest.send",
      "category": "Email",
      "workerState": "Retrying",
      "status": "Failed",
      "startedAt": "2026-05-11T12:00:02Z",
      "completedAt": "2026-05-11T12:00:03Z",
      "executionDuration": "00:00:01",
      "subjectId": { "type": "claim", "value": "CLM-123" },
      "concurrencyKey": null,
      "identifiers": []
    }
  ],
  "completedIterations": []
}
```
