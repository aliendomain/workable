namespace Workable;

/// <summary>
/// Executes work using typed input deserialized by Workable.
/// </summary>
/// <typeparam name="TInput">The input type Workable should deserialize from the queued <see cref="WorkInput"/> payload.</typeparam>
public interface IWorkExecutor<TInput>
{
    /// <summary>
    /// Runs one worker iteration for the definition.
    /// </summary>
    /// <param name="context">
    /// The execution context for the current worker iteration, including services, messaging, profiling,
    /// and identifier helpers.
    /// </param>
    /// <param name="input">The typed input value deserialized for the worker.</param>
    /// <param name="cancellationToken">
    /// A token that is canceled when the worker should stop promptly because the host or caller requested cancellation.
    /// </param>
    /// <returns>
    /// A task that returns the iteration result, including any structured messages that should be retained.
    /// </returns>
    Task<WorkExecutionResult> Execute(
        IWorkExecutionContext context,
        TInput input,
        CancellationToken cancellationToken);
}

/// <summary>
/// Executes work using typed input and returns typed output that Workable serializes for callers and retained worker state.
/// </summary>
/// <typeparam name="TInput">The input type Workable should deserialize from the queued <see cref="WorkInput"/> payload.</typeparam>
/// <typeparam name="TOutput">The output type Workable should serialize into retained worker output.</typeparam>
public interface IWorkExecutor<TInput, TOutput>
{
    /// <summary>
    /// Runs one worker iteration for the definition.
    /// </summary>
    /// <param name="context">
    /// The execution context for the current worker iteration, including services, messaging, profiling,
    /// and identifier helpers.
    /// </param>
    /// <param name="input">The typed input value deserialized for the worker.</param>
    /// <param name="cancellationToken">
    /// A token that is canceled when the worker should stop promptly because the host or caller requested cancellation.
    /// </param>
    /// <returns>
    /// A task that returns the iteration result, including typed output and structured messages that should be retained.
    /// </returns>
    Task<WorkExecutionResult<TOutput>> Execute(
        IWorkExecutionContext context,
        TInput input,
        CancellationToken cancellationToken);
}
