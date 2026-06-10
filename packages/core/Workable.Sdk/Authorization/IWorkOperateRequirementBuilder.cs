namespace Workable;

/// <summary>
/// Configures additional synchronous operate requirements for queueing, worker actions, and reconfiguration.
/// </summary>
public interface IWorkOperateRequirementBuilder
{
    /// <summary>
    /// Adds a requirement that can authorize queueing, worker actions, and reconfiguration.
    /// </summary>
    /// <param name="requirement">The synchronous requirement to evaluate.</param>
    /// <returns>The same builder so additional requirements can be chained.</returns>
    IWorkOperateRequirementBuilder WhenOperatingRequire(
        Func<WorkOperateRequirementContext, bool> requirement);

    /// <summary>
    /// Adds a typed requirement that can authorize queueing, worker actions, and worker reconfiguration.
    /// </summary>
    /// <typeparam name="TInput">The input type to deserialize before evaluation.</typeparam>
    /// <param name="requirement">The synchronous requirement to evaluate.</param>
    /// <returns>The same builder so additional requirements can be chained.</returns>
    IWorkOperateRequirementBuilder WhenOperatingRequire<TInput>(
        Func<WorkOperateRequirementContext<TInput>, bool> requirement);

    /// <summary>
    /// Adds a requirement that applies only when queueing work.
    /// </summary>
    /// <param name="requirement">The synchronous requirement to evaluate.</param>
    /// <returns>The same builder so additional requirements can be chained.</returns>
    IWorkOperateRequirementBuilder WhenQueueingRequire(
        Func<WorkQueueRequirementContext, bool> requirement);

    /// <summary>
    /// Adds a typed requirement that applies only when queueing work.
    /// </summary>
    /// <typeparam name="TInput">The input type to deserialize before evaluation.</typeparam>
    /// <param name="requirement">The synchronous requirement to evaluate.</param>
    /// <returns>The same builder so additional requirements can be chained.</returns>
    IWorkOperateRequirementBuilder WhenQueueingRequire<TInput>(
        Func<WorkQueueRequirementContext<TInput>, bool> requirement);

    /// <summary>
    /// Adds a requirement that applies only to worker actions.
    /// </summary>
    /// <param name="requirement">The synchronous requirement to evaluate.</param>
    /// <returns>The same builder so additional requirements can be chained.</returns>
    IWorkOperateRequirementBuilder WhenWorkerActionsRequire(
        Func<WorkWorkerActionRequirementContext, bool> requirement);

    /// <summary>
    /// Adds a typed requirement that applies only to worker actions.
    /// </summary>
    /// <typeparam name="TInput">The input type to deserialize before evaluation.</typeparam>
    /// <param name="requirement">The synchronous requirement to evaluate.</param>
    /// <returns>The same builder so additional requirements can be chained.</returns>
    IWorkOperateRequirementBuilder WhenWorkerActionsRequire<TInput>(
        Func<WorkWorkerActionRequirementContext<TInput>, bool> requirement);

    /// <summary>
    /// Adds a requirement that applies to both worker and definition reconfiguration.
    /// </summary>
    /// <param name="requirement">The synchronous requirement to evaluate.</param>
    /// <returns>The same builder so additional requirements can be chained.</returns>
    IWorkOperateRequirementBuilder WhenReconfiguringRequire(
        Func<WorkReconfigurationRequirementContext, bool> requirement);

    /// <summary>
    /// Adds a typed requirement that applies to worker reconfiguration and can also observe definition reconfiguration.
    /// </summary>
    /// <typeparam name="TInput">The input type to deserialize before evaluation.</typeparam>
    /// <param name="requirement">The synchronous requirement to evaluate.</param>
    /// <returns>The same builder so additional requirements can be chained.</returns>
    IWorkOperateRequirementBuilder WhenReconfiguringRequire<TInput>(
        Func<WorkReconfigurationRequirementContext<TInput>, bool> requirement);

    /// <summary>
    /// Adds a requirement that applies only to worker reconfiguration.
    /// </summary>
    /// <param name="requirement">The synchronous requirement to evaluate.</param>
    /// <returns>The same builder so additional requirements can be chained.</returns>
    IWorkOperateRequirementBuilder WhenWorkerReconfiguringRequire(
        Func<WorkWorkerReconfigurationRequirementContext, bool> requirement);

    /// <summary>
    /// Adds a typed requirement that applies only to worker reconfiguration.
    /// </summary>
    /// <typeparam name="TInput">The input type to deserialize before evaluation.</typeparam>
    /// <param name="requirement">The synchronous requirement to evaluate.</param>
    /// <returns>The same builder so additional requirements can be chained.</returns>
    IWorkOperateRequirementBuilder WhenWorkerReconfiguringRequire<TInput>(
        Func<WorkWorkerReconfigurationRequirementContext<TInput>, bool> requirement);

    /// <summary>
    /// Adds a requirement that applies only to definition reconfiguration.
    /// </summary>
    /// <param name="requirement">The synchronous requirement to evaluate.</param>
    /// <returns>The same builder so additional requirements can be chained.</returns>
    IWorkOperateRequirementBuilder WhenDefinitionReconfiguringRequire(
        Func<WorkDefinitionReconfigurationRequirementContext, bool> requirement);
}
