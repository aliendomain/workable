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
    /// <param name="workDefinitionName">The target Workable work definition name.</param>
    /// <param name="input">Optional static work input payload.</param>
    /// <returns>The same builder so additional parallel branches can be chained.</returns>
    IWorkflowParallelBuilder DispatchWork(
        string stepName,
        string workDefinitionName,
        WorkInput? input = null);
}
