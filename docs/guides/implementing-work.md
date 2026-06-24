# Implementing Work

## Intent

This guide is for developers writing executor code.

It explains what runs inside a work implementation, what `IWorkExecutionContext` exposes, and what Workable does when executor code returns a result, requests failure, throws an exception, or is canceled because of pause, cancel, or interruption.

## Related Authoring Features

This guide focuses on executor code: what runs when a worker executes and how that execution behaves.

Some work authoring features are closely related, but they are documented in other guides because they are registration-time or configuration-time concerns rather than execution-time implementation details:

- [Work Registration](registration.md): automatic start, startup work sources, named systems, and feature-contributed work.
- [Work Configuration](configuration/README.md): runtime behavior such as start policy, retry, recurrence, failed-worker handling, concurrency, retention, logging, durability, and invocation rules.

## Basic Shape

Work can be implemented with a delegate or an executor class. In either case, executor code receives:

- `IWorkExecutionContext`
- typed input or raw `WorkInput`
- a `CancellationToken`

```csharp
public sealed class SendWelcomeEmailWork : IWorkExecutor<SendWelcomeEmailArgs, SendWelcomeEmailResult>
{
    public async Task<WorkExecutionResult<SendWelcomeEmailResult>> Execute(
        IWorkExecutionContext context,
        SendWelcomeEmailArgs input,
        CancellationToken cancellationToken)
    {
        var mailer = context.Services.GetRequiredService<WelcomeMailer>();

        await mailer.Send(input.UserId, cancellationToken);

        return WorkExecutionResult<SendWelcomeEmailResult>.Success(
            new SendWelcomeEmailResult(MessageId: Guid.NewGuid().ToString("N")));
    }
}
```

Executor classes can still use normal constructor injection, and that is usually the preferred shape for stable dependencies. `IWorkExecutionContext.Services` is most useful when executor code needs execution-scoped lookups, optional services, or dynamic resolution patterns that do not fit clean constructor injection.

## Execution Scope

Each execution runs inside a Workable-owned execution scope.

- Scoped services resolved from `context.Services` belong to that execution.
- Initializers run before the executor, but they do not share the same scoped service instances as the executor.
- Recurring work and transient retry create a fresh execution scope for each iteration or retry attempt.

That means executor code should treat each execution attempt as a clean DI scope, even when the worker identity stays the same across recurrence or retry.

## Execution Context

`IWorkExecutionContext` exposes execution identity, runtime settings, control-state information, and a few behavior-changing methods.

### Identity And Configuration

- `WorkSystemId`: the system that owns this worker.
- `WorkSystemName`: the optional name of that system.
- `WorkerId`: the worker currently executing.
- `Definition`: the `WorkDefinition` being executed.
- `RequestContext`: the original `WorkRequestContext` for this worker. Use `RequestContext.Origin` for durable provenance and `RequestContext.Description` / `RequestContext.Url` for caller-supplied request metadata.
- `Options`: the effective `WorkerOptions` for this worker.
- `Configuration`: the effective `WorkConfiguration` for this worker.

These are read-only execution facts.

### Services And Profiling

- `Services`: the execution-scope `IServiceProvider`
- `Profile`: the active `IWorkProfiler`

Use `Services` to resolve scoped collaborators when constructor injection is not the better fit:

```csharp
var processor = context.Services.GetRequiredService<MailboxProcessor>();
```

Use `Profile` to add timing or diagnostic detail to the captured worker profile:

```csharp
using var scope = context.Profile.CreateScope("Load mailbox");
using var timing = context.Profile.StartTiming("Query database");
context.Profile.AddInfo("mailbox", input.MailboxName);
```

See [Profiling](../concepts/profiling.md) for deeper examples.

### Interruption State

- `IsInterrupted`
- `InterruptionReason`

These are `only` about Workable interruption, not every cancellation.

When they are set:

- `Shutdown`: the system is stopping and Workable is cooperatively interrupting the worker.
- `LeaseLost`: a durable worker lost ownership of its persistence lease and must stop.

When they are not set:

- normal success paths
- explicit `Pause`
- explicit `Cancel`
- caller-side queue cancellation before acceptance

This distinction matters because `Pause` and `Cancel` also cancel the execution token, but they do not mark the context as interrupted.

### AddIdentifier

`AddIdentifier(WorkIdentifier identifier)` adds a discovered identifier to the worker at execution time.

Use it when the executor learns a useful query key only after it starts running, such as:

- an external job id
- an email provider message id
- a downstream batch id

```csharp
context.AddIdentifier(new WorkIdentifier("email-message", providerMessageId));
```

The identifier becomes part of worker query and correlation surfaces.

### Fail

`Fail(string code, string message, string? target = null, bool transient = false)` asks Workable to finish this execution as failed without throwing an exception.

```csharp
if (!validation.Allowed)
{
    context.Fail(
        "orders.fulfillment.rejected",
        "Order fulfillment was rejected by the downstream policy engine.",
        "input.orderId");

    return WorkExecutionResult.Success();
}
```

Use `Fail(...)` when:

- the failure is expected business logic
- the failure should not look like a runtime bug
- you want a structured failure message without exception classification
- you discover the failure imperatively inside a longer execution flow and want Workable to force the final result to `Failed`

What Workable does:

- creates an `Error` `WorkMessage` from the supplied `code`, `message`, and optional `target`, timestamped at the time `Fail(...)` is called
- marks the execution result as failed when the executor returns
- preserves any additional messages returned by the executor alongside the requested failure
- when `transient` is `true`, lets transient retry treat the failure as retryable without using exception classification

Important nuance:

- `Fail(..., transient: false)` does not trigger transient retry
- `Fail(..., transient: true)` retries through transient retry configuration
- thrown exceptions use [Exception Classification](configuration/transient-retry.md#exception-classification)

`Fail(...)` is not the same as returning `WorkExecutionResult.Failure(...)`.

- `WorkExecutionResult.Failure(...)` is the normal result model when the executor is already assembling and returning a complete failure outcome.
- `Fail(...)` is an imperative signal that says "this execution must fail" even if the method later returns `Success(...)`.
- `WorkExecutionResult.Failure(...)` has no first-class way to mark the failure as transient.
- `Fail(..., transient: true)` gives a non-exception failure a retryable transient meaning.
- If both are used together, Workable keeps the requested `Fail(...)` message and preserves the other returned messages.

In practice:

- return `WorkExecutionResult.Failure(...)` when the whole method naturally resolves to a failure result
- call `Fail(...)` when failure is discovered mid-flow, when cleanup or additional result shaping still needs to happen before returning, or when the failure should be marked transient without throwing

### Failed-Worker Handling Override

Executor code can also control whether a failed worker should remain for manual handling or be auto-canceled after failure.

```csharp
if (input.RequestCameFromInteractiveCaller)
{
    context.AllowFailedWorkerAutoCancel(TimeSpan.FromMinutes(1));
}
else
{
    context.RequireManualFailedWorkerHandling();
}
```

Use these methods when the current execution has stronger operational knowledge than the static work configuration:

- `RequireManualFailedWorkerHandling()` forces the worker to stay in `Failed` if execution fails.
- `AllowFailedWorkerAutoCancel()` uses the worker's configured failed-worker auto-cancel delay if execution fails.
- `AllowFailedWorkerAutoCancel(TimeSpan delay)` uses an execution-specific failed-worker auto-cancel delay if execution fails.

These overrides affect the worker disposition after failure. They do not erase the failed iteration, messages, or other failure evidence Workable retains.

Recurring work cannot opt into failed-worker auto-cancel. Calling `AllowFailedWorkerAutoCancel(...)` on a recurring worker throws.

### CompleteDurably

`CompleteDurably(...)` is for durable completion scenarios where business data and Workable completion must commit inside the same developer-owned transaction.

Use it only when the worker is configured for durable completion.

```csharp
await context.CompleteDurably(transaction, cancellationToken);
```

If durable completion is enabled and executor code returns success without calling `CompleteDurably(...)`, Workable fails the execution intentionally.

See [Queue Durability Configuration](configuration/queue-durability.md) for the full durable-completion flow.

## Returning Results

Executor code normally finishes in one of three ways:

1. return success
2. return failure messages or call `Fail(...)`
3. throw

### Success

Return `WorkExecutionResult.Success(...)` when the execution completed normally.

```csharp
return WorkExecutionResult.Success();
```

### Declarative Failure

There are two non-exception ways to fail:

- return a result that includes `Error` severity messages
- call `context.Fail(...)`

Both end the execution in `Failed`. Returned error messages are not retried. `context.Fail(...)` participates in transient retry only when called with `transient: true`.

Use these for business failures, validation failures, or policy failures where throwing would overstate the situation.

Choose between them based on where the failure lives in your control flow:

- if the executor is simply returning a structured failure outcome, use `WorkExecutionResult.Failure(...)`
- if the executor needs to declare failure while continuing through the rest of its method body, use `context.Fail(...)`
- if the failure must be treated as transient without throwing, use `context.Fail(..., transient: true)`

### Exceptions

Unhandled exceptions are turned into an execution failure message with exception metadata, including:

- exception type
- exception message
- stack trace
- inner exception chain

Workable then classifies the exception as:

- `Transient`
- `NonTransient`
- `Unknown`

If the final classification is transient and retry is enabled, Workable can retry the execution.

See [Exception Classification](configuration/transient-retry.md#exception-classification) for classifier details.

## Pause, Cancel, And Interruption

These are easy to conflate, but they are not the same thing.

### Pause

If a running worker is paused:

- the worker moves to `Pausing`
- Workable cancels the execution token
- `context.IsInterrupted` remains `false`

If executor code cooperates and returns promptly, the worker becomes `Paused`.

If the worker was `Queued`, `Waiting`, or `Retrying`, pause happens immediately and no executor code is running at that moment.

Important nuance:

- pause is cooperative
- Workable cannot freeze your method in place
- executor code must observe the token or finish on its own before the worker can settle into `Paused`

### Cancel

If a running worker is canceled:

- the worker moves to `Canceling`
- Workable cancels the execution token
- `context.IsInterrupted` remains `false`

If executor code exits promptly, the worker becomes `Canceled`.

If the worker was `Queued`, `Waiting`, or `Retrying`, cancellation can complete without an active executor body.

### Interruption

Interruption is Workable saying "stop because runtime ownership or system lifetime changed."

Current interruption reasons are:

- `Shutdown`
- `LeaseLost`

If a running worker is interrupted:

- the worker moves to `Interrupting`
- Workable cancels the execution token
- `context.IsInterrupted` becomes `true`
- `context.InterruptionReason` explains why

If executor code exits promptly, the worker becomes `Interrupted`.

For queued, waiting, or retrying workers, interruption can complete without active executor code.

### Recommended Cancellation Pattern

When executor code needs to react differently to interruption than to ordinary cancellation, inspect the context in a cancellation catch block:

```csharp
try
{
    await processor.Run(input, cancellationToken);
    return WorkExecutionResult.Success();
}
catch (OperationCanceledException) when (context.InterruptionReason == WorkInterruptionReason.Shutdown)
{
    return WorkExecutionResult.Success(
        messages:
        [
            WorkMessage.Warning(
                "orders.sync.interrupted",
                "Order sync stopped because the Workable system is shutting down.",
                "worker")
        ]);
}
```

In many cases, though, the best behavior is simply to let `OperationCanceledException` propagate so Workable can translate the current control state into `Paused`, `Canceled`, or `Interrupted`.

## What Happens If Executor Code Swallows Cancellation

Workable cancellation is cooperative.

If executor code catches `OperationCanceledException` and keeps running:

- `Pause` will not become `Paused` until the method finally returns
- `Cancel` will not become `Canceled` until the method finally returns
- `Shutdown` interruption may escalate to a forced interrupted state if the host shutdown grace period expires

So swallowing cancellation should be deliberate and rare.

## Recurrence And Retry Nuance

Work implementations often need to know whether a failure will stop the worker entirely.

The answer depends on the execution mode:

- run-once work: failure ends the worker
- transient retry work: transient thrown exceptions can lead to another attempt
- recurring work: a failed iteration can still be followed by another iteration, depending on recurrence settings

Important distinctions:

- `context.Fail(..., transient: false)` creates a failed execution that is not retried
- `context.Fail(..., transient: true)` creates a failed execution that transient retry can retry
- returning `Error` messages creates a failed execution, not a retryable exception
- throwing a transient-classified exception can retry the current execution attempt
- recurring workers can continue after a failed iteration when recurrence is configured to continue after failure

See [Recurrence Configuration](configuration/recurrence.md), [Transient Retry Configuration](configuration/transient-retry.md), and [Lifecycle](../concepts/lifecycle.md).

## Practical Guidance

- Use `context.Services` for scoped collaborators instead of reaching for root singletons.
- Prefer constructor injection for normal executor dependencies; use `context.Services` when execution-scoped or dynamic lookup is the better fit.
- Use `context.AddIdentifier(...)` when execution discovers a durable correlation key.
- Use `context.Fail(...)` for expected business failures.
- Use `RequireManualFailedWorkerHandling()` or `AllowFailedWorkerAutoCancel(...)` only when the current execution should override the configured failed-worker policy.
- Let `OperationCanceledException` propagate unless you have a clear reason to translate it.
- Check `context.IsInterrupted` and `context.InterruptionReason` only when you need to distinguish interruption from pause or cancel.
- Use `context.Profile` when timing or diagnostic detail would help future operators understand expensive work.
- Use `context.CompleteDurably(...)` only for durable-completion flows that truly need transaction-bound completion.

## Related Docs

- [Getting Started](getting-started.md)
- [Registration](registration.md)
- [Queueing](queueing.md)
- [Failed-Worker Handling Configuration](configuration/failed-worker.md)
- [Recurrence Configuration](configuration/recurrence.md)
- [Transient Retry Configuration](configuration/transient-retry.md)
- [Queue Durability Configuration](configuration/queue-durability.md)
- [Lifecycle](../concepts/lifecycle.md)
- [Profiling](../concepts/profiling.md)
