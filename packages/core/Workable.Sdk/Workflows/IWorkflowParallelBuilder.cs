namespace Workable;

/// <summary>
/// Builds the child steps for one parallel workflow section.
/// </summary>
public interface IWorkflowParallelBuilder
{
    /// <summary>
    /// Adds a child step that queues one existing Workable work definition.
    /// </summary>
    /// <param name="stepName">The stable workflow-local child step name.</param>
    /// <param name="workDefinition">The target Workable work definition.</param>
    /// <param name="input">Optional static work input payload.</param>
    /// <returns>The same builder so additional parallel branches can be chained.</returns>
    IWorkflowParallelBuilder DispatchWork(
        string stepName,
        WorkDefinition workDefinition,
        WorkInput? input = null);

    /// <summary>
    /// Adds a child step that queues one existing Workable work definition with the input supplied when the workflow run starts.
    /// </summary>
    /// <param name="stepName">The stable workflow-local child step name.</param>
    /// <param name="workDefinition">The target Workable work definition.</param>
    /// <returns>The same builder so additional parallel branches can be chained.</returns>
    IWorkflowParallelBuilder DispatchWorkFromWorkflowInput(
        string stepName,
        WorkDefinition workDefinition);

    /// <summary>
    /// Adds a named sequential branch that may contain worker and structure nodes.
    /// </summary>
    /// <param name="branchName">The stable workflow-local branch name.</param>
    /// <param name="configure">Builds the sequential branch body.</param>
    /// <returns>The same builder so additional parallel branches can be chained.</returns>
    IWorkflowParallelBuilder Branch(
        string branchName,
        Action<IWorkflowBuilder> configure);
}
