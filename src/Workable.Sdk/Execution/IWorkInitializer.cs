namespace Workable;

public interface IWorkInitializer
{
    Task<WorkExecutionResult> Initialize(
        IWorkExecutionContext context,
        CancellationToken cancellationToken = default);
}

public interface IWorkInitializer<TInput>
{
    Task<WorkExecutionResult> Initialize(
        IWorkExecutionContext context,
        TInput input,
        CancellationToken cancellationToken = default);
}
