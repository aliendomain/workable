namespace Workable;

/// <summary>
/// Configures system-level authorization for one registered Workable system.
/// </summary>
public interface IWorkSystemAuthorizationBuilder
{
    /// <summary>
    /// Assigns the groups that should be treated as system administrators.
    /// </summary>
    /// <param name="groups">
    /// Groups that should receive diagnostics access, system control access, and read-all-work access
    /// for the configured system.
    /// </param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkSystemAuthorizationBuilder SystemAdministrators(params string[] groups);

    /// <summary>
    /// Assigns the groups that should be treated as work administrators.
    /// </summary>
    /// <param name="groups">
    /// Groups that should receive read-all-work and operate-all-work access for the configured system.
    /// </param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkSystemAuthorizationBuilder WorkAdministrators(params string[] groups);

    /// <summary>
    /// Grants access to diagnostics for the configured system.
    /// </summary>
    /// <param name="groups">Groups that may access diagnostics and diagnostics-backed transport surfaces.</param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkSystemAuthorizationBuilder AllowDiagnosticsToGroups(params string[] groups);

    /// <summary>
    /// Grants permission to start and stop the configured system.
    /// </summary>
    /// <param name="groups">Groups that may control the runtime lifecycle of the system.</param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkSystemAuthorizationBuilder AllowControlSystemToGroups(params string[] groups);

    /// <summary>
    /// Grants permission to use Workable's built-in HTTP API surface for the configured system.
    /// </summary>
    /// <param name="groups">
    /// Groups that may use the built-in <c>MapWorkableApi(...)</c> routes for this system without being
    /// system administrators or work administrators.
    /// </param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkSystemAuthorizationBuilder AllowBuiltInHttpApiToGroups(params string[] groups);

    /// <summary>
    /// Grants read access to every work definition in the configured system.
    /// </summary>
    /// <param name="groups">Groups that should be able to read all work without per-definition read grants.</param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkSystemAuthorizationBuilder AllowReadAllWorkToGroups(params string[] groups);

    /// <summary>
    /// Grants operate access to every work definition in the configured system.
    /// </summary>
    /// <param name="groups">Groups that should be able to queue and control all work without per-definition operate grants.</param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkSystemAuthorizationBuilder AllowOperateAllWorkToGroups(params string[] groups);
}
