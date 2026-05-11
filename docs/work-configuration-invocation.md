# Invocation Configuration

Invocation configuration controls which entry points may start a work definition.

The default allows .NET queueing and the Workable HTTP API. MCP is not allowed by default.

```csharp
WorkConfiguration.Default.Invocation.Allows(WorkInvocationChannel.DotNet);  // true
WorkConfiguration.Default.Invocation.Allows(WorkInvocationChannel.HttpApi); // true
WorkConfiguration.Default.Invocation.Allows(WorkInvocationChannel.Mcp);     // false
```

## Attribute

Use `WorkInvocationAttribute` when work should be available from additional channels. Attribute channels are added to the work definition's existing channels.

```csharp
[WorkMetadata("email.welcome.send", "Email:Lifecycle")]
[WorkInvocation(WorkInvocationChannel.Mcp)]
public sealed class SendWelcomeEmailWork : IWorkExecutor
{
    public Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());
}
```

## Bootstrap

Invocation can also be configured during registration.

```csharp
builder.AddWork<SendWelcomeEmailWork>(
    configuration => configuration.AllowInvocationFrom(WorkInvocationChannel.Mcp));
```

`AllowInvocationFrom` is additive. Calling it with `WorkInvocationChannel.Mcp` keeps the existing .NET and HTTP channels and also allows MCP.

Use `UseInvocation` when the intended behavior is exact replacement.

```csharp
builder.AddWork<InternalOnlyWork>(
    configuration => configuration.UseInvocation(
        WorkInvocationConfiguration.Allow(WorkInvocationChannel.DotNet)));
```

## Design-Time Rule

Invocation is definition-level configuration. It is read from the catalog definition by adapter surfaces such as `Workable.HttpApi` and `Workable.Mcp`.

Queue-time `WorkerOptions` and runtime `WorkerReconfiguration` do not change invocation channels. A worker already exists after invocation has been accepted, so changing invocation on that worker would not describe who may start the work definition.

## Channel Behavior

- `DotNet` covers direct C# calls to `IWorkQueue`.
- `HttpApi` covers the Workable HTTP API adapter.
- `Mcp` covers the Workable MCP adapter.

The Workable HTTP API lists and invokes work allowed for `HttpApi`. The MCP adapter lists and invokes work allowed for `Mcp`.
