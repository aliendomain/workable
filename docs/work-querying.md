# Work Querying

## Intent

Workable query APIs provide read-only access to worker state and registered work definitions. Use `IWorkSystem.Query` to inspect work without changing it.

## Worker Queries

Use `GetWorker` when you need full worker detail.

```csharp
WorkerSnapshot? worker = await workSystem.Query.GetWorker(workerId, cancellationToken);
```

`WorkerSnapshot.ActionHistory` records worker action and reconfiguration attempts that were applied to that worker. Each entry includes the operation kind, action when applicable, outcome status, origin, revision, state sequence, messages, and requested reconfiguration changes when applicable.

Use `QueryWorkers` to retrieve workers that match a `WorkerQuery`. It returns `WorkerSummary` items instead of full snapshots.

```csharp
var result = await workSystem.Query.QueryWorkers(
    new WorkerQuery(
        DefinitionName: "email.welcome.send",
        SubjectId: new WorkSubjectId("user", "user-123"),
        Identifier: new WorkIdentifier("order", "order-456"),
        Take: 50),
    cancellationToken);

foreach (var worker in result.Workers)
{
    WorkerId id = worker.Id;
    WorkerState state = worker.State;
}
```

Worker queries can filter by work definition id, work definition name, subject id, concurrency key, arbitrary work identifier, worker state, created time, and updated time.

Filter by worker state to retrieve workers in any lifecycle status.

```csharp
WorkerQueryResult running =
    await workSystem.Query.QueryWorkers(
        new WorkerQuery(
            States: new HashSet<WorkerState> { WorkerState.Running }),
        cancellationToken);
```

## Work Identifiers

`WorkSubjectId` has special meaning: it can participate in idempotency. `WorkIdentifier` is a general relationship marker used for query and observability.

Supply known identifiers when queueing work:

```csharp
var input = WorkInput.Empty
    .WithIdentifier(new WorkIdentifier("order", "order-456"))
    .WithIdentifier(new WorkIdentifier("customer", "customer-123"));

await workSystem.Queue.Enqueue("email.welcome.send", input, cancellationToken: cancellationToken);
```

Add discovered identifiers during execution:

```csharp
public sealed class SendWelcomeEmailExecutor : IWorkExecutor
{
    public Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
    {
        context.AddIdentifier(new WorkIdentifier("email-message", "message-789"));
        return Task.FromResult(WorkExecutionResult.Success());
    }
}
```

Adding the same identifier more than once is ignored.

## Work Definition Queries

Use `QueryWorkDefinitions` to retrieve registered work definitions.

```csharp
IReadOnlyList<WorkDefinition> definitions =
    await workSystem.Query.QueryWorkDefinitions(
        new WorkDefinitionQuery(
            Category: "Email",
            Search: "welcome"),
        cancellationToken);
```

`WorkDefinition.Name` is the unique queue/query name. `WorkDefinition.Category` can use colon-delimited paths such as `Email:Lifecycle`.

Use the catalog when you only need definitions in a category.

```csharp
IReadOnlyList<WorkDefinition> emailDefinitions =
    workSystem.Catalog.ListByCategory("Email");
```

Category lookup is case-insensitive. With `includeSubcategories` enabled, `Email` includes definitions registered under categories such as `Email:Lifecycle` and `Email:Reports`.

## Work Info

Use `GetWorkInfo` to retrieve one definition plus its current worker rollup.

```csharp
WorkInfo? info = await workSystem.Query.GetWorkInfo("email.welcome.send", cancellationToken);

if (info is not null)
{
    WorkDefinition definition = info.Definition;
    WorkDefinitionStatus status = info.Status;
    WorkerRollup workers = info.Workers;
}
```

The rollup includes total, active, queued, running, waiting, paused, failed, canceled, completed, and last activity values.

## Status Summary

Use `GetWorkerStatusSummary` for counts by worker state.

```csharp
WorkerStatusSummary summary = await workSystem.Query.GetWorkerStatusSummary(
    new WorkerQuery(DefinitionName: "email.welcome.send"),
    cancellationToken);
```

Call `GetWorkerStatusSummary` without a query to summarize all workers in the system.

## Returned Structures

The examples below show the serialized JSON shape returned by HTTP and MCP adapters. In-process .NET callers receive the same records as CLR objects. JSON property names are camel-case, enum values are strings, and nullable properties are represented as `null`.

### Worker Summary

`QueryWorkers` returns a `WorkerQueryResult` containing `WorkerSummary` rows.

```json
{
  "workers": [
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
        "channel": "HttpApi",
        "actor": {
          "id": "user-123",
          "name": "Greya",
          "email": "greya@example.test"
        },
        "description": "Queue work 'email.welcome.send' through HTTP API.",
        "url": "/workable/work/email.welcome.send"
      },
      "state": "Completed",
      "createdAt": "2026-05-11T12:00:00Z",
      "updatedAt": "2026-05-11T12:00:03Z",
      "version": {
        "workerId": { "value": "00000000-0000-0000-0000-000000000000" },
        "revision": 3
      }
    }
  ],
  "totalCount": 1,
  "skip": 0,
  "take": 100
}
```

### Worker Snapshot

`GetWorker` returns a full `WorkerSnapshot`. It includes the same worker identity and state fields as `WorkerSummary`, plus input, output, options, configuration, messages, retained iterations, captured logs, durable action history, and the latest profile snapshot when profiling is enabled.

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
    "url": "/mcp"
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
      "maximumSuccessfulIterations": 25,
      "maximumFailedIterations": 5,
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
      "purgeInterval": "00:05:00"
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
  "version": {
    "workerId": { "value": "00000000-0000-0000-0000-000000000000" },
    "revision": 3
  },
  "iterations": [
    {
      "sequence": 1,
      "occurredAt": "2026-05-11T12:00:01Z",
      "status": "Completed",
      "output": {
        "json": "{\"sent\":true}",
        "contentType": "application/json"
      },
      "messages": []
    }
  ],
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
        "url": "/mcp"
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

`QueryWorkDefinitions` returns `WorkDefinition` records. `GetWorkInfo` includes one `WorkDefinition` inside its response.

```json
[
  {
    "id": { "value": "00000000-0000-0000-0000-000000000000" },
    "name": "email.welcome.send",
    "category": "Email",
    "description": "Sends a welcome email.",
    "inputSchema": {
      "jsonSchema": "{\"type\":\"object\"}",
      "contentType": "application/schema+json"
    },
    "outputSchema": {
      "jsonSchema": "{\"type\":\"object\"}",
      "contentType": "application/schema+json"
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
        "maximumSuccessfulIterations": 25,
        "maximumFailedIterations": 5,
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
        "purgeInterval": "00:05:00"
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
    }
  }
]
```

### Work Info

`GetWorkInfo` returns one definition plus its current status and worker rollup.

```json
{
  "definition": {
    "id": { "value": "00000000-0000-0000-0000-000000000000" },
    "name": "email.welcome.send",
    "category": "Email",
    "description": "Sends a welcome email.",
    "inputSchema": {
      "jsonSchema": "{\"type\":\"object\"}",
      "contentType": "application/schema+json"
    },
    "outputSchema": {
      "jsonSchema": "{\"type\":\"object\"}",
      "contentType": "application/schema+json"
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
        "maximumSuccessfulIterations": 25,
        "maximumFailedIterations": 5,
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
        "purgeInterval": "00:05:00"
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
    "metadata": null
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

`GetWorkerStatusSummary` returns counts by worker state for the whole system or the supplied worker query.

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
