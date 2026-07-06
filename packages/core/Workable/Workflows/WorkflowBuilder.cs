using System.Linq.Expressions;

namespace Workable;

internal sealed class WorkflowBuilder : IWorkflowBuilder
{
    private readonly List<WorkflowStepDefinition> steps = [];

    public IWorkflowBuilder DispatchWork(
        string stepName,
        WorkDefinition workDefinition,
        WorkInput? input = null)
    {
        ValidateDispatch(stepName, workDefinition);
        this.steps.Add(new DispatchWorkflowStepDefinition(stepName, workDefinition, input));
        return this;
    }

    public WorkflowStepReference<TOutput> DispatchWork<TOutput>(
        string stepName,
        WorkDefinition workDefinition,
        WorkInput? input = null)
    {
        this.DispatchWork(stepName, workDefinition, input);
        return new WorkflowStepReference<TOutput>(stepName);
    }

    public IWorkflowBuilder DispatchWorkFromWorkflowInput(
        string stepName,
        WorkDefinition workDefinition)
    {
        ValidateDispatch(stepName, workDefinition);
        this.steps.Add(new DispatchWorkflowStepDefinition(
            stepName,
            workDefinition,
            InputSource: WorkflowDispatchInputSource.WorkflowInput));
        return this;
    }

    public WorkflowStepReference<TOutput> DispatchWorkFromWorkflowInput<TOutput>(
        string stepName,
        WorkDefinition workDefinition)
    {
        this.DispatchWorkFromWorkflowInput(stepName, workDefinition);
        return new WorkflowStepReference<TOutput>(stepName);
    }

    public IWorkflowBuilder DispatchEach<TSourceOutput, TChildInput>(
        string stepName,
        WorkflowStepReference<TSourceOutput> sourceStep,
        WorkDefinition workDefinition,
        Expression<Func<TSourceOutput, IEnumerable<TChildInput>?>> selector)
    {
        ValidateDispatch(stepName, workDefinition);
        ArgumentNullException.ThrowIfNull(sourceStep);
        ArgumentNullException.ThrowIfNull(selector);
        this.steps.Add(new DispatchEachWorkflowStepDefinition(
            stepName,
            sourceStep,
            workDefinition,
            WorkflowOutputSelector.Create(selector)));
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

    private static void ValidateDispatch(string stepName, WorkDefinition workDefinition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stepName);
        ArgumentNullException.ThrowIfNull(workDefinition);
    }

    private sealed class WorkflowParallelBuilder : IWorkflowParallelBuilder
    {
        private readonly List<WorkflowStepDefinition> steps = [];

        public IWorkflowParallelBuilder DispatchWork(
            string stepName,
            WorkDefinition workDefinition,
            WorkInput? input = null)
        {
            ValidateDispatch(stepName, workDefinition);
            this.steps.Add(new DispatchWorkflowStepDefinition(stepName, workDefinition, input));
            return this;
        }

        public IWorkflowParallelBuilder DispatchWorkFromWorkflowInput(
            string stepName,
            WorkDefinition workDefinition)
        {
            ValidateDispatch(stepName, workDefinition);
            this.steps.Add(new DispatchWorkflowStepDefinition(
                stepName,
                workDefinition,
                InputSource: WorkflowDispatchInputSource.WorkflowInput));
            return this;
        }

        public IReadOnlyList<WorkflowStepDefinition> Build() => [.. this.steps];
    }
}
