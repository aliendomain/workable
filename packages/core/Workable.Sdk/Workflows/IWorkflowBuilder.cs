namespace Workable;

/// <summary>
/// Builds one workflow definition by composing built-in step shapes.
/// </summary>
public interface IWorkflowBuilder
{
    /// <summary>
    /// Adds a step that queues one existing Workable work definition.
    /// </summary>
    /// <param name="stepName">The stable workflow-local step name.</param>
    /// <param name="workDefinitionName">The target Workable work definition name.</param>
    /// <param name="input">Optional static work input payload.</param>
    /// <returns>The same builder so additional steps can be chained.</returns>
    IWorkflowBuilder DispatchWork(
        string stepName,
        string workDefinitionName,
        WorkInput? input = null);

    /// <summary>
    /// Adds a parallel step that contains child work-dispatch steps.
    /// </summary>
    /// <param name="stepName">The stable workflow-local step name.</param>
    /// <param name="configure">Builds the child parallel branches.</param>
    /// <returns>The same builder so additional steps can be chained.</returns>
    IWorkflowBuilder RunParallel(
        string stepName,
        Action<IWorkflowParallelBuilder> configure);

    /// <summary>
    /// Adds a join step that waits for earlier parallel branches to settle.
    /// </summary>
    /// <param name="stepName">The stable workflow-local step name.</param>
    /// <returns>The same builder so additional steps can be chained.</returns>
    IWorkflowBuilder Join(string stepName);
}
