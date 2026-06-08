namespace Workable;

/// <summary>
/// Configures read and operate authorization requirements for one work definition registration.
/// </summary>
public interface IWorkAuthorizationBuilder
{
    /// <summary>
    /// Sets both the read and operate group requirements in one call.
    /// </summary>
    /// <param name="readGroups">The groups allowed to discover and read the definition and its retained data.</param>
    /// <param name="operateGroups">The groups allowed to queue the definition and control its workers.</param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkAuthorizationBuilder RequireGroups(
        IEnumerable<string>? readGroups = null,
        IEnumerable<string>? operateGroups = null);

    /// <summary>
    /// Replaces the definition's read-group requirement.
    /// </summary>
    /// <param name="groups">The groups allowed to read the definition and related retained data.</param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkAuthorizationBuilder AllowReadToGroups(params string[] groups);

    /// <summary>
    /// Replaces the definition's operate-group requirement.
    /// </summary>
    /// <param name="groups">The groups allowed to queue the definition and perform worker actions.</param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkAuthorizationBuilder AllowOperateToGroups(params string[] groups);

    /// <summary>
    /// Allows operate access for callers that are authenticated and resolve to a known Workable actor.
    /// </summary>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkAuthorizationBuilder AllowOperateToKnownAuthenticatedUsers();
}
