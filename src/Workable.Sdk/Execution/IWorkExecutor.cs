namespace Workable;
public interface IWorkExecutor
{
    Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken);
}
