# Invocation Configuration

Invocation configuration controls which entry points may start a work definition.

For configuration source order, precedence, and override rules that apply to every configuration facet, see [Work Configuration](README.md).

Invocation is definition-level configuration. It can be supplied at startup and changed later through definition default reconfiguration, but it is not part of queue-time `WorkerOptions` or runtime `WorkerReconfiguration`.

## Settings

| Setting | Default | Description |
| --- | --- | --- |
| `AllowedChannels` | `InProcess`, `HttpApi` | The entry points that may start the work definition. The default allows direct in-process queueing and the Workable HTTP API. `Mcp` and `SignalR` are not allowed by default. |

## Channels

| Channel | Description |
| --- | --- |
| `InProcess` | Direct in-process calls to `IWorkQueueService`. |
| `HttpApi` | The Workable HTTP API adapter. |
| `Mcp` | The Workable MCP adapter. |
| `SignalR` | The Workable SignalR adapter. |

## Attribute Configuration

`WorkInvocationAttribute` declares additional allowed invocation channels on the executor type.

```csharp
[WorkInvocation(WorkInvocationChannel.Mcp, WorkInvocationChannel.SignalR)]
public sealed class SendWelcomeEmailWork : IWorkExecutor
{
    public Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());
}
```

`WorkInvocationAttribute` is additive. It adds channels to the definition's existing allowed channels instead of replacing them.

## Startup Configuration

At startup, the same behavior can also be configured with the additive convenience method `AllowInvocationFrom` or the full `UseInvocation` setter.

```csharp
services.AddWorkableSystem(builder =>
{
    builder.AddWork<SendWelcomeEmailWork>(
        WorkDefinition.Create(
            name: "email.welcome.send",
            description: "Sends a welcome email to a new user.",
            category: "Email:Lifecycle"),
        configuration => configuration.UseInvocation(
            new WorkInvocationConfiguration
            {
                AllowedChannels = new HashSet<WorkInvocationChannel>
                {
                    WorkInvocationChannel.InProcess,
                    WorkInvocationChannel.HttpApi,
                    WorkInvocationChannel.SignalR,
                },
            }));
});
```

## Queue-Time Configuration

Queue-time configuration cannot change invocation channels.

```csharp
// Not supported: WorkerOptions.Configuration does not apply invocation changes at queue time.
```

## Worker Reconfiguration

Runtime worker reconfiguration cannot change invocation channels.

```csharp
// Not supported: WorkerReconfiguration does not include invocation.
```

## Definition Reconfiguration

Invocation can be reconfigured only at the definition level through `IWorkCatalog.Reconfigure`.

```csharp
WorkDefinition definition = workSystem.Catalog.Definitions
    .Single(definition => definition.Name == "email.welcome.send");

WorkDefinitionReconfigurationOutcome outcome =
    await workSystem.Catalog.Reconfigure(
        definition.Version,
        new WorkDefinitionReconfiguration(
            Configuration: definition.Configuration with
            {
                Invocation = new WorkInvocationConfiguration
                {
                    AllowedChannels = new HashSet<WorkInvocationChannel>
                    {
                        WorkInvocationChannel.InProcess,
                        WorkInvocationChannel.HttpApi,
                        WorkInvocationChannel.Mcp,
                    },
                },
            }),
        cancellationToken);
```
