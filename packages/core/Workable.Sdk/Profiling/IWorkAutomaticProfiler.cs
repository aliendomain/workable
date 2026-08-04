namespace Workable;

/// <summary>
/// Receives nodes produced automatically by profiling instrumentation.
/// </summary>
/// <remarks>
/// Automatic instrumentation uses this surface so Workable can enforce a shared per-profile budget
/// across SQL, HTTP, and future instrumentation without limiting explicit application profile nodes.
/// </remarks>
public interface IWorkAutomaticProfiler
{
    /// <summary>
    /// Attempts to add an automatic informational node.
    /// </summary>
    /// <param name="instrumentation">The stable instrumentation key.</param>
    /// <param name="name">The profile node label.</param>
    /// <param name="context">Optional structured context captured under the profile node.</param>
    /// <returns><see langword="true"/> when the node was captured; otherwise <see langword="false"/>.</returns>
    bool TryAddAutomaticInfo(string instrumentation, string name, object? context = null);

    /// <summary>
    /// Attempts to add an automatic informational node, creating its context only after admission succeeds.
    /// </summary>
    bool TryAddAutomaticInfo<TContext>(
        string instrumentation,
        string name,
        Func<TContext> contextFactory)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        return this.TryAddAutomaticInfo(instrumentation, name, contextFactory());
    }

    /// <summary>
    /// Attempts to start an automatic timing node.
    /// </summary>
    /// <param name="instrumentation">The stable instrumentation key.</param>
    /// <param name="name">The profile node label.</param>
    /// <param name="context">Optional structured context captured under the profile node.</param>
    /// <param name="scope">The timing scope when capture was admitted.</param>
    /// <returns><see langword="true"/> when the timing was captured; otherwise <see langword="false"/>.</returns>
    bool TryStartAutomaticTiming(
        string instrumentation,
        string name,
        object? context,
        out IWorkProfileScope? scope);

    /// <summary>
    /// Attempts to start an automatic timing node, creating its context only after admission succeeds.
    /// </summary>
    bool TryStartAutomaticTiming<TContext>(
        string instrumentation,
        string name,
        Func<TContext> contextFactory,
        out TContext? context,
        out IWorkProfileScope? scope)
        where TContext : class
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        context = contextFactory();
        if (this.TryStartAutomaticTiming(instrumentation, name, context, out scope))
        {
            return true;
        }

        context = null;
        return false;
    }
}
