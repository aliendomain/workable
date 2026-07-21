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

    public IWorkflowDispatchEachBuilder DispatchEach<TSourceOutput, TChildInput>(
        string stepName,
        WorkflowStepReference<TSourceOutput> sourceStep,
        WorkDefinition workDefinition,
        Expression<Func<TSourceOutput, IEnumerable<TChildInput>?>> selector,
        WorkflowCanceledChildBehavior canceledChildBehavior = WorkflowCanceledChildBehavior.Continue)
    {
        ValidateDispatch(stepName, workDefinition);
        ArgumentNullException.ThrowIfNull(sourceStep);
        ArgumentNullException.ThrowIfNull(selector);
        if (!Enum.IsDefined(canceledChildBehavior))
        {
            throw new ArgumentOutOfRangeException(nameof(canceledChildBehavior));
        }

        this.steps.Add(new DispatchEachWorkflowStepDefinition(
            stepName,
            sourceStep,
            workDefinition,
            WorkflowOutputSelector.Create(selector),
            canceledChildBehavior));
        return new WorkflowDispatchEachBuilder(this, stepName);
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

    private sealed class WorkflowDispatchEachBuilder(
        WorkflowBuilder workflow,
        string stepName) : IWorkflowDispatchEachBuilder
    {
        public WorkflowStepReference<TOutput> Outputs<TOutput>()
            => new(stepName);

        public IWorkflowBuilder DispatchWork(
            string childStepName,
            WorkDefinition workDefinition,
            WorkInput? input = null)
            => workflow.DispatchWork(childStepName, workDefinition, input);

        public WorkflowStepReference<TOutput> DispatchWork<TOutput>(
            string childStepName,
            WorkDefinition workDefinition,
            WorkInput? input = null)
            => workflow.DispatchWork<TOutput>(childStepName, workDefinition, input);

        public IWorkflowBuilder DispatchWorkFromWorkflowInput(
            string childStepName,
            WorkDefinition workDefinition)
            => workflow.DispatchWorkFromWorkflowInput(childStepName, workDefinition);

        public WorkflowStepReference<TOutput> DispatchWorkFromWorkflowInput<TOutput>(
            string childStepName,
            WorkDefinition workDefinition)
            => workflow.DispatchWorkFromWorkflowInput<TOutput>(childStepName, workDefinition);

        public IWorkflowDispatchEachBuilder DispatchEach<TSourceOutput, TChildInput>(
            string childStepName,
            WorkflowStepReference<TSourceOutput> sourceStep,
            WorkDefinition workDefinition,
            Expression<Func<TSourceOutput, IEnumerable<TChildInput>?>> selector,
            WorkflowCanceledChildBehavior canceledChildBehavior = WorkflowCanceledChildBehavior.Continue)
            => workflow.DispatchEach(
                childStepName,
                sourceStep,
                workDefinition,
                selector,
                canceledChildBehavior);

        public IWorkflowBuilder RunParallel(
            string childStepName,
            Action<IWorkflowParallelBuilder> configure)
            => workflow.RunParallel(childStepName, configure);

        public IWorkflowBuilder Join(string childStepName)
            => workflow.Join(childStepName);
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

        public IWorkflowParallelBuilder Branch(
            string branchName,
            Action<IWorkflowBuilder> configure)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(branchName);
            ArgumentNullException.ThrowIfNull(configure);

            var branch = new WorkflowBuilder();
            configure(branch);
            this.steps.Add(new BranchWorkflowStepDefinition(branchName, branch.Build()));
            return this;
        }

        public IReadOnlyList<WorkflowStepDefinition> Build() => [.. this.steps];
    }
}
