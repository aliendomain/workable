namespace Workable;

/// <summary>
/// Performs initialization logic before an untyped executor runs.
/// </summary>
public interface IWorkInitializer
{
    /// <summary>
    /// Runs initialization for the current worker iteration.
    /// </summary>
    /// <param name="context">The execution context for the current worker iteration.</param>
    /// <param name="cancellationToken">A token that cancels initialization.</param>
    /// <returns>The initialization result that determines whether execution should continue.</returns>
    Task<WorkExecutionResult> Initialize(
        IWorkExecutionContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Performs initialization logic before a typed executor runs.
/// </summary>
/// <typeparam name="TInput">The typed input payload supplied to the executor.</typeparam>
public interface IWorkInitializer<TInput>
{
    /// <summary>
    /// Runs initialization for the current worker iteration.
    /// </summary>
    /// <param name="context">The execution context for the current worker iteration.</param>
    /// <param name="input">The typed input payload that will be supplied to the executor.</param>
    /// <param name="cancellationToken">A token that cancels initialization.</param>
    /// <returns>The initialization result that determines whether execution should continue.</returns>
    Task<WorkExecutionResult> Initialize(
        IWorkExecutionContext context,
        TInput input,
        CancellationToken cancellationToken = default);
}
