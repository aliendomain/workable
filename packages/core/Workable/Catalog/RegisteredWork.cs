using System.Collections.Concurrent;

namespace Workable;

internal sealed record RegisteredWork(
    WorkDefinition Definition,
    Func<IServiceProvider, IWorkExecutor> ExecutorFactory,
    IReadOnlyList<WorkExceptionClassifier> ExceptionClassifiers,
    IReadOnlyList<WorkAutomaticStartRegistration> AutomaticStarts,
    IReadOnlyList<WorkInitializationRegistration> Initializers,
    WorkOperateAuthorizationConfiguration OperateAuthorization)
{
    private readonly ConcurrentDictionary<WorkInitializationId, LazyInitializationState> lazyInitializationStates = [];

    public RegisteredWorkRuntimePlan DefaultRuntimePlan { get; } = RegisteredWorkRuntimePlan.CreateDefault(Definition);

    public IReadOnlyList<WorkInitializationRegistration> OrderedInitializers { get; } =
        WorkInitializationRegistration.Order(Initializers);

    public RegisteredWork(
        WorkDefinition definition,
        Func<IServiceProvider, IWorkExecutor> executorFactory,
        IReadOnlyList<WorkExceptionClassifier> exceptionClassifiers)
        : this(
            definition,
            executorFactory,
            exceptionClassifiers,
            [],
            [],
            WorkOperateAuthorizationConfiguration.FromDefinition(definition.Authorization))
    {
    }

    public RegisteredWork(
        WorkDefinition definition,
        Func<IServiceProvider, IWorkExecutor> executorFactory,
        IReadOnlyList<WorkExceptionClassifier> exceptionClassifiers,
        IReadOnlyList<WorkAutomaticStartRegistration> automaticStarts,
        IReadOnlyList<WorkInitializationRegistration> initializers)
        : this(
            definition,
            executorFactory,
            exceptionClassifiers,
            automaticStarts,
            initializers,
            WorkOperateAuthorizationConfiguration.FromDefinition(definition.Authorization))
    {
    }

    public RegisteredWork WithDefinition(WorkDefinition definition)
        => new(
            definition,
            this.ExecutorFactory,
            this.ExceptionClassifiers,
            this.AutomaticStarts,
            this.Initializers,
            this.OperateAuthorization);

    public async Task<WorkExecutionResult> RunLazyInitialization(
        WorkInitializationRegistration initializer,
        Func<Task<WorkExecutionResult>> initialize)
    {
        var state = this.lazyInitializationStates.GetOrAdd(initializer.Id, _ => new LazyInitializationState());
        await state.Sync.WaitAsync();
        try
        {
            if (state.IsComplete)
            {
                return WorkExecutionResult.Success();
            }

            var result = await initialize();
            if (!result.HasErrors)
            {
                state.IsComplete = true;
            }

            return result;
        }
        finally
        {
            state.Sync.Release();
        }
    }

    private sealed class LazyInitializationState
    {
        public SemaphoreSlim Sync { get; } = new(1, 1);

        public bool IsComplete { get; set; }
    }
}

internal sealed record RegisteredWorkRuntimePlan(
    WorkerOptions Options,
    WorkConfiguration Configuration,
    IReadOnlyList<WorkMessage> ConfigurationErrors)
{
    public WorkStartPolicy StartPolicy => this.Configuration.Start.Policy;

    public bool ShouldStart => this.StartPolicy != WorkStartPolicy.DoNotStart;

    public static RegisteredWorkRuntimePlan CreateDefault(WorkDefinition definition)
        => Create(
            definition.DefaultOptions,
            definition.Configuration.MergeRuntimeOptions(definition.DefaultOptions.Configuration));

    public static RegisteredWorkRuntimePlan Create(WorkDefinition definition, WorkerOptions options)
        => Create(
            definition.DefaultOptions.Merge(options),
            definition.Configuration
                .MergeRuntimeOptions(definition.DefaultOptions.Configuration)
                .MergeRuntimeOptions(options.Configuration));

    private static RegisteredWorkRuntimePlan Create(
        WorkerOptions options,
        WorkConfiguration configuration)
    {
        var errors = WorkConfigurationValidator.ValidateWorkerOptions(options)
            .Concat(WorkConfigurationValidator.Validate(configuration))
            .ToArray();
        return new RegisteredWorkRuntimePlan(
            options,
            configuration,
            errors.Length == 0 ? [] : errors);
    }
}
