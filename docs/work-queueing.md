# Work Queueing

## Intent

Queueing creates a worker for a registered work definition. `IWorkSystem.Queue` accepts work, validates the request, returns an immediate queue outcome, and provides a handle that can be awaited for completion.

## Queue By Name

Queue by work definition name when the caller knows the registered name.

```csharp
IWorkerHandle handle = await workSystem.Queue.Enqueue(
    "email.welcome.send",
    cancellationToken: cancellationToken);
```

The name is matched case-insensitively within the system catalog.

## Queue By Definition Id

Queue by `WorkDefinitionId` when the caller already has a definition from the catalog or query API.

```csharp
WorkDefinition definition = workSystem.Catalog.Definitions
    .Single(definition => definition.Name == "email.welcome.send");

IWorkerHandle handle = await workSystem.Queue.Enqueue(
    definition.Id,
    cancellationToken: cancellationToken);
```

## Work Input

`WorkInput` carries serialized input data plus optional identity and grouping metadata.

```csharp
var input = WorkInput.FromValue(
    new SendWelcomeEmail("user-123"),
    subjectId: new WorkSubjectId("user", "user-123"));

IWorkerHandle handle = await workSystem.Queue.Enqueue(
    "email.welcome.send",
    input,
    cancellationToken: cancellationToken);
```

When queueing from C#, typed input can be passed directly to `IWorkQueueService`. Workable serializes it into `WorkInput`.

```csharp
IWorkerHandle handle = await workSystem.Queue.Enqueue(
    "email.welcome.send",
    new SendWelcomeEmailArgs("user-123"),
    cancellationToken: cancellationToken);
```

Typed input can also be queued by definition id.

```csharp
IWorkerHandle handle = await workSystem.Queue.Enqueue(
    definition.Id,
    new SendWelcomeEmailArgs("user-123"),
    cancellationToken: cancellationToken);
```

Use `WorkInput.Empty` when the work does not need input data.

```csharp
await workSystem.Queue.Enqueue("cache.refresh", WorkInput.Empty, cancellationToken: cancellationToken);
```

## Relationship Keys

Relationship keys attach searchable business context to a worker. They can be used later by worker queries, work key search, event filters, and observability tools.

All relationship keys have a `type` and `value`, but each kind has a different meaning:

- `WorkSubjectId` identifies the main business subject of the worker.
- `WorkConcurrencyKey` identifies a capacity grouping key when concurrency is configured by key.
- `WorkIdentifier` identifies secondary or discovered relationships.

## Subject Id

`WorkSubjectId` identifies the business subject of the worker. It can be used for query, correlation, event filtering, and idempotency.

```csharp
var input = WorkInput.Empty
    .WithSubject(new WorkSubjectId("order", "order-456"));
```

Supplying a subject does not reject duplicates by itself. Duplicate prevention is controlled by idempotency configuration.

## Concurrency Key

`WorkConcurrencyKey` groups workers when concurrency is configured with `PerConcurrencyKey`.

```csharp
var input = WorkInput.Empty
    .WithConcurrencyKey(new WorkConcurrencyKey("tenant", "tenant-123"));
```

Supplying a concurrency key does not limit execution by itself. Capacity limits are controlled by concurrency configuration.

## Work Identifiers

`WorkIdentifier` adds arbitrary relationships that can be queried and used for event filtering. Supply known identifiers when queueing work.

```csharp
var input = WorkInput.Empty
    .WithIdentifier(new WorkIdentifier("customer", "customer-123"))
    .WithIdentifier(new WorkIdentifier("invoice", "invoice-789"));
```

Identifiers supplied with input are available on `WorkerSnapshot` and `WorkEvent`, and can be used by worker queries.

Identifiers can also be discovered during execution.

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

## Queue Options

`WorkerOptions` can override worker options and effective runtime configuration for one queued worker.

```csharp
var options = new WorkerOptions(
    ProfilingEnabled: true);

IWorkerHandle handle = await workSystem.Queue.Enqueue(
    "email.welcome.send",
    input: WorkInput.Empty,
    options: options,
    cancellationToken: cancellationToken);
```

When profiling is enabled, Workable captures an execution tree for each worker iteration. The tree includes Workable's executor call scope and any profile scopes, timings, or info entries added through `IWorkExecutionContext.Profile` or injected `IWorkProfiler` services during execution. The latest profile is exposed on `WorkerSnapshot.Profile`, and retained iteration profiles are exposed on `WorkerSnapshot.Iterations`.

Use `WorkerOptions.Configuration` for queue-time configuration overrides.

```csharp
var options = new WorkerOptions(
    Configuration: WorkConfiguration.Default with
    {
        Start = WorkStartConfiguration.DoNotStart,
    });

IWorkerHandle handle = await workSystem.Queue.Enqueue(
    "email.welcome.send",
    options: options,
    cancellationToken: cancellationToken);
```

Configuration supplied through queue options is merged over the definition defaults.

## Request Context And Origin

Direct .NET queue calls record a `WorkOrigin` with `WorkInvocationChannel.DotNet`. By default, the actor is unknown.

When the caller already knows who is making the request, create a `WorkRequestContext` and queue through a session instead of calling `IWorkSystem.Queue` directly.

```csharp
var requestContext = WorkRequestContext.Create(
    WorkInvocationChannel.DotNet,
    new WorkActor(Id: "current-user-id"),
    "Queue welcome email from application service.");

var session = workSystem.CreateSession(requestContext);

IWorkerHandle handle = await session.Queue.Enqueue(
    "email.welcome.send",
    input: WorkInput.Empty,
    cancellationToken: cancellationToken);
```

ASP.NET Core hosts can use `Workable.AspNetCore` to create authenticated request contexts from `HttpContext` inside their own controllers or minimal API routes. This does not expose Workable's built-in HTTP API endpoints.

```csharp
services.AddWorkableSystem(builder =>
{
    builder.StartWithHost();
    builder.RequireAuthorization();
});

services.AddWorkableAspNetCoreAuthorization();
```

## Queue Outcome

`Enqueue` returns an `IWorkerHandle`. The handle always includes a `WorkQueueOutcome`.

```csharp
IWorkerHandle handle = await workSystem.Queue.Enqueue("email.welcome.send");

if (!handle.QueueOutcome.IsAccepted)
{
    IReadOnlyList<WorkMessage> messages = handle.QueueOutcome.Messages;
    return;
}

WorkerId workerId = handle.WorkerId!.Value;
```

Queue outcome statuses are:

- `Accepted`: a worker was created.
- `Invalid`: validation rejected the request.
- `NotFound`: no matching work definition was found.

Validation failures are returned as structured `WorkMessage` values. If the system is stopping, queueing returns `Invalid` with message code `workable.system.stopping`.

## Await Completion

Use `WaitForCompletion` when the caller needs the final result.

```csharp
IWorkerHandle handle = await workSystem.Queue.Enqueue("email.welcome.send");
WorkCompletion completion = await handle.WaitForCompletion(cancellationToken);

if (completion.IsCompletedSuccessfully)
{
    WorkOutput? output = completion.Output;
}
```

`WorkCompletion` includes completion status, the final worker snapshot when one exists, output, and messages.

When the work returns typed output, the handle can deserialize the completed output for the caller.

```csharp
IWorkerHandle handle = await workSystem.Queue.Enqueue(
    "email.welcome.send",
    new SendWelcomeEmailArgs("user-123"),
    cancellationToken: cancellationToken);

WorkCompletion<SendWelcomeEmailResult> completion =
    await handle.WaitForCompletion<SendWelcomeEmailResult>(cancellationToken);

if (completion.IsCompletedSuccessfully)
{
    string messageId = completion.Output!.MessageId;
}
```

Typed completions preserve the raw serialized `WorkOutput` on `RawOutput`, along with status, messages, and the final worker snapshot.

## Fire And Forget

A caller can discard the handle after queueing. Accepted workers continue independently of the caller.

```csharp
await workSystem.Queue.Enqueue("email.welcome.send", input);
```

Use `WorkerId`, query filters, or event filters when the worker needs to be found later.

## Queue Cancellation

The cancellation token passed to `Enqueue` applies to queue acceptance and configured queue wait behavior. It does not become the execution cancellation token for the worker after the worker has been accepted.

```csharp
using var queueTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

IWorkerHandle handle = await workSystem.Queue.Enqueue(
    "email.welcome.send",
    cancellationToken: queueTimeout.Token);
```

Use worker actions to control an accepted worker.

```csharp
WorkerSnapshot? worker = await workSystem.Query.Worker(handle.WorkerId!.Value);

if (worker is not null)
{
    await workSystem.Workers.Execute(worker.Version, WorkAction.Cancel, cancellationToken);
}
```

## Start Configuration

Start configuration controls whether queueing starts the worker automatically and when `Enqueue` returns.

```csharp
var options = new WorkerOptions(
    Configuration: WorkConfiguration.Default with
    {
        Start = WorkStartConfiguration.DoNotStart,
    });

IWorkerHandle handle = await workSystem.Queue.Enqueue(
    "email.welcome.send",
    options: options,
    cancellationToken: cancellationToken);
```

See [Start Configuration](work-configuration-start.md) for the available policies and behavior.

## Configuration

Queueing applies definition configuration, contributed configuration, and queue options before accepting the worker. Workable has configuration options for start behavior, coordination state and protections, recurrence, transient retry, logging, and retention. At the system level, `MaximumWorkers` is checked before accepting a worker so an in-memory system can reject new queue requests when the approximate non-final worker record count is at capacity. Completed and canceled workers are retained for history but do not block admission; interrupted and failed workers are not final.

See [Work Configuration](work-configuration.md) for the configuration surface and the per-feature configuration documents.
