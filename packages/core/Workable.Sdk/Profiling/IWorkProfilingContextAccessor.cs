namespace Workable;

/// <summary>
/// Resolves the ambient worker profiling context for the current async flow.
/// </summary>
public interface IWorkProfilingContextAccessor
{
    /// <summary>
    /// Attempts to resolve the current worker profiling context.
    /// </summary>
    /// <param name="context">The ambient profiling context when one is active for the current async flow.</param>
    /// <returns><see langword="true"/> when the current async flow is executing inside a profiled worker; otherwise <see langword="false"/>.</returns>
    bool TryGetCurrent(out WorkProfilingContext context);
}
