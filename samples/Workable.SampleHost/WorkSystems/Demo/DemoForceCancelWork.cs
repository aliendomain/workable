namespace Workable.SampleHost.Demo;

public sealed record DemoForceCancelInput(string Reason = "Manual shutdown force-cancel demo");

public sealed class DemoForceCancelWork : IWorkExecutor<DemoForceCancelInput>
{
    public async Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        DemoForceCancelInput input,
        CancellationToken cancellationToken)
    {
        await Task.Delay(Timeout.InfiniteTimeSpan);
        return WorkExecutionResult.Success();
    }
}
