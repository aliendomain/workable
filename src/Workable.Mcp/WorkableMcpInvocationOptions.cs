namespace Workable;

public sealed record WorkableMcpInvocationOptions
{
    public static WorkableMcpInvocationOptions Default { get; } = new();

    public WorkableMcpInvocationCompletion Completion { get; init; } = WorkableMcpInvocationCompletion.WaitForCompletion;

    public WorkerOptions? WorkerOptions { get; init; }

    public TimeSpan? CompletionTimeout { get; init; }
}
