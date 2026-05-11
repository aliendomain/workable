namespace Workable;
internal sealed class DelegateWorkExecutor(
    Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute) : IWorkExecutor
{
    public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        => execute(context, input, cancellationToken);
}
