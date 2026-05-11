namespace Workable;

public interface IWorkExecutor<TInput>
{
    Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        TInput input,
        CancellationToken cancellationToken);
}

public interface IWorkExecutor<TInput, TOutput>
{
    Task<WorkExecutionResult<TOutput>> Execute(
        IWorkExecutionContext context,
        TInput input,
        CancellationToken cancellationToken);
}
