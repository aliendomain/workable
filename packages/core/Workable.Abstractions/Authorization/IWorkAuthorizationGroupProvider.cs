namespace Workable;

/// <summary>
/// Resolves authorization groups for a caller within a system scope.
/// </summary>
public interface IWorkAuthorizationGroupProvider
{
    /// <summary>
    /// Gets the group values associated with an actor for a specific system.
    /// </summary>
    /// <param name="actor">The caller identity for which groups should be resolved.</param>
    /// <param name="systemName">The system name being authorized, or <see langword="null"/> for the default unnamed system.</param>
    /// <returns>The resolved group values for the actor within the system scope.</returns>
    IReadOnlySet<string> GetGroups(WorkActor actor, string? systemName);
}
