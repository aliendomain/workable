namespace Workable;

/// <summary>
/// Continues a workflow after declaring a <c>DispatchEach</c> step and can expose its child outputs.
/// </summary>
public interface IWorkflowDispatchEachBuilder : IWorkflowBuilder
{
    /// <summary>
    /// Returns a typed reference to the output produced by each child dispatched by this step.
    /// </summary>
    /// <typeparam name="TOutput">The logical output type produced by each dispatched child.</typeparam>
    /// <returns>A typed reference that later workflow steps can use.</returns>
    WorkflowStepReference<TOutput> Outputs<TOutput>();
}
