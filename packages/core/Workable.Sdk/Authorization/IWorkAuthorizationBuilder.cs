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
    /// <param name="groups">The groups allowed to queue, operate, and reconfigure the definition.</param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkAuthorizationBuilder AllowOperateToGroups(params string[] groups);

    /// <summary>
    /// Adds a group-based operate grant with additional synchronous queue, worker-action, and reconfiguration requirements.
    /// </summary>
    /// <param name="groups">The groups that can satisfy this operate grant.</param>
    /// <param name="configure">Configures the additional synchronous requirement checks for the grant.</param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkAuthorizationBuilder AllowOperateToGroups(
        IEnumerable<string> groups,
        Action<IWorkOperateRequirementBuilder> configure);

    /// <summary>
    /// Adds a group-based grant that allows queueing only.
    /// </summary>
    /// <param name="groups">The groups that can queue the definition.</param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkAuthorizationBuilder AllowQueueToGroups(params string[] groups);

    /// <summary>
    /// Adds a group-based queueing grant with additional synchronous requirements.
    /// </summary>
    /// <param name="groups">The groups that can satisfy this queueing grant.</param>
    /// <param name="configure">Configures the additional synchronous requirement checks for the grant.</param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkAuthorizationBuilder AllowQueueToGroups(
        IEnumerable<string> groups,
        Action<IWorkOperateRequirementBuilder> configure);

    /// <summary>
    /// Adds a group-based grant that allows worker actions only.
    /// </summary>
    /// <param name="groups">The groups that can perform worker actions.</param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkAuthorizationBuilder AllowWorkerActionsToGroups(params string[] groups);

    /// <summary>
    /// Adds a group-based worker-action grant with additional synchronous requirements.
    /// </summary>
    /// <param name="groups">The groups that can satisfy this worker-action grant.</param>
    /// <param name="configure">Configures the additional synchronous requirement checks for the grant.</param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkAuthorizationBuilder AllowWorkerActionsToGroups(
        IEnumerable<string> groups,
        Action<IWorkOperateRequirementBuilder> configure);

    /// <summary>
    /// Adds a group-based grant for a custom operation mask.
    /// </summary>
    /// <param name="groups">The groups that can satisfy this grant.</param>
    /// <param name="permissions">The operation mask the grant should allow.</param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkAuthorizationBuilder AllowOperationsToGroups(
        IEnumerable<string> groups,
        WorkOperationPermissions permissions);

    /// <summary>
    /// Adds a group-based grant for a custom operation mask with additional synchronous requirements.
    /// </summary>
    /// <param name="groups">The groups that can satisfy this grant.</param>
    /// <param name="permissions">The operation mask the grant should allow.</param>
    /// <param name="configure">Configures the additional synchronous requirement checks for the grant.</param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkAuthorizationBuilder AllowOperationsToGroups(
        IEnumerable<string> groups,
        WorkOperationPermissions permissions,
        Action<IWorkOperateRequirementBuilder> configure);

    /// <summary>
    /// Allows operate access for callers that are authenticated and resolve to a known Workable actor.
    /// </summary>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkAuthorizationBuilder AllowOperateToKnownAuthenticatedUsers();

    /// <summary>
    /// Allows operate access for callers that are authenticated and resolve to a known Workable actor,
    /// then applies additional synchronous queue, worker-action, and reconfiguration requirements.
    /// </summary>
    /// <param name="configure">Configures the additional synchronous requirement checks for the grant.</param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkAuthorizationBuilder AllowOperateToKnownAuthenticatedUsers(
        Action<IWorkOperateRequirementBuilder> configure);

    /// <summary>
    /// Allows queueing for callers that are authenticated and resolve to a known Workable actor.
    /// </summary>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkAuthorizationBuilder AllowQueueToKnownAuthenticatedUsers();

    /// <summary>
    /// Allows queueing for callers that are authenticated and resolve to a known Workable actor,
    /// then applies additional synchronous requirements.
    /// </summary>
    /// <param name="configure">Configures the additional synchronous requirement checks for the grant.</param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkAuthorizationBuilder AllowQueueToKnownAuthenticatedUsers(
        Action<IWorkOperateRequirementBuilder> configure);

    /// <summary>
    /// Allows worker actions for callers that are authenticated and resolve to a known Workable actor.
    /// </summary>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkAuthorizationBuilder AllowWorkerActionsToKnownAuthenticatedUsers();

    /// <summary>
    /// Allows worker actions for callers that are authenticated and resolve to a known Workable actor,
    /// then applies additional synchronous requirements.
    /// </summary>
    /// <param name="configure">Configures the additional synchronous requirement checks for the grant.</param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkAuthorizationBuilder AllowWorkerActionsToKnownAuthenticatedUsers(
        Action<IWorkOperateRequirementBuilder> configure);

    /// <summary>
    /// Allows a custom operation mask for callers that are authenticated and resolve to a known Workable actor.
    /// </summary>
    /// <param name="permissions">The operation mask the grant should allow.</param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkAuthorizationBuilder AllowOperationsToKnownAuthenticatedUsers(
        WorkOperationPermissions permissions);

    /// <summary>
    /// Allows a custom operation mask for callers that are authenticated and resolve to a known Workable actor,
    /// then applies additional synchronous requirements.
    /// </summary>
    /// <param name="permissions">The operation mask the grant should allow.</param>
    /// <param name="configure">Configures the additional synchronous requirement checks for the grant.</param>
    /// <returns>The same builder so additional authorization rules can be chained.</returns>
    IWorkAuthorizationBuilder AllowOperationsToKnownAuthenticatedUsers(
        WorkOperationPermissions permissions,
        Action<IWorkOperateRequirementBuilder> configure);
}
