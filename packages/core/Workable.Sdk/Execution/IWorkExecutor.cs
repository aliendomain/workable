namespace Workable;
/// <summary>
/// Executes work using raw <see cref="WorkInput"/> data.
/// </summary>
/// <remarks>
/// Implement this interface when the executor wants direct access to serialized input and does not need
/// Workable to deserialize a typed input object first.
/// </remarks>
public interface IWorkExecutor
{
    /// <summary>
    /// Runs one worker iteration for the definition.
    /// </summary>
    /// <param name="context">
    /// The execution context for the current worker iteration, including services, messaging, profiling,
    /// and identifier helpers.
    /// </param>
    /// <param name="input">The raw input payload supplied when the worker was queued, if any.</param>
    /// <param name="cancellationToken">
    /// A token that is canceled when the worker should stop promptly because the host or caller requested cancellation.
    /// </param>
    /// <returns>
    /// A task that returns the iteration result, including any output and structured messages that should be retained.
    /// </returns>
    Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken);
}
