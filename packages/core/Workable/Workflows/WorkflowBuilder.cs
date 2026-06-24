namespace Workable;

internal sealed class WorkflowBuilder : IWorkflowBuilder
{
    private readonly List<WorkflowStepDefinition> steps = [];

    public IWorkflowBuilder DispatchWork(
        string stepName,
        string workDefinitionName,
        WorkInput? input = null)
    {
        ValidateNames(stepName, workDefinitionName);
        this.steps.Add(new DispatchWorkflowStepDefinition(stepName, workDefinitionName, input));
        return this;
    }

    public IWorkflowBuilder RunParallel(
        string stepName,
        Action<IWorkflowParallelBuilder> configure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        ArgumentNullException.ThrowIfNull(configure);

        var parallel = new WorkflowParallelBuilder();
        configure(parallel);
        this.steps.Add(new ParallelWorkflowStepDefinition(stepName, parallel.Build()));
        return this;
    }

    public IWorkflowBuilder Join(string stepName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        this.steps.Add(new JoinWorkflowStepDefinition(stepName));
        return this;
    }

    public IReadOnlyList<WorkflowStepDefinition> Build() => [.. this.steps];

    private static void ValidateNames(string stepName, string workDefinitionName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        ArgumentException.ThrowIfNullOrWhiteSpace(workDefinitionName);
    }

    private sealed class WorkflowParallelBuilder : IWorkflowParallelBuilder
    {
        private readonly List<WorkflowStepDefinition> steps = [];

        public IWorkflowParallelBuilder DispatchWork(
            string stepName,
            string workDefinitionName,
            WorkInput? input = null)
        {
            ValidateNames(stepName, workDefinitionName);
            this.steps.Add(new DispatchWorkflowStepDefinition(stepName, workDefinitionName, input));
            return this;
        }

        public IReadOnlyList<WorkflowStepDefinition> Build() => [.. this.steps];
    }
}
