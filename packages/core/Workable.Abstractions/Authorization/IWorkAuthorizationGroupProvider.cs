namespace Workable;

/// <summary>
/// Resolves authorization groups for a caller within a system scope.
/// </summary>
/// <remarks>
/// Implementations are registered as singletons and may be called concurrently. Implementations that use scoped
/// resources such as a database context should create those resources through a thread-safe factory for each call.
/// Implementations must observe the supplied cancellation token and should not apply an unbounded internal retry policy.
/// </remarks>
public interface IWorkAuthorizationGroupProvider
{
    /// <summary>
    /// Gets the group values associated with an actor for a specific system.
    /// </summary>
    /// <param name="actor">The caller identity for which groups should be resolved.</param>
    /// <param name="systemName">The system name being authorized, or <see langword="null"/> for the default unnamed system.</param>
    /// <param name="cancellationToken">A token that cancels group resolution.</param>
    /// <returns>The resolved group values for the actor within the system scope.</returns>
    ValueTask<IReadOnlySet<string>> GetGroups(
        WorkActor actor,
        string? systemName,
        CancellationToken cancellationToken = default);
}
