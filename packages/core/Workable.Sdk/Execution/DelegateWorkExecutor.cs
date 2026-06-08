namespace Workable;
internal sealed class DelegateWorkExecutor(
    Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute) : IWorkExecutor
{
    /// <summary>
    /// Executes the registered raw delegate for the current worker iteration.
    /// </summary>
    /// <param name="context">The execution context for the current worker iteration.</param>
    /// <param name="input">The raw input payload supplied to the worker, when one exists.</param>
    /// <param name="cancellationToken">A token that cancels execution.</param>
    /// <returns>The execution result produced by the delegate.</returns>
    public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        => execute(context, input, cancellationToken);
}
