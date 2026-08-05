using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Workable;
internal sealed class WorkSystemBuilder(IServiceCollection services, string? name) : IWorkSystemBuilder
{
    private readonly List<RegisteredWork> registeredWork = [];
    private readonly List<RegisteredWorkflow> registeredWorkflows = [];
    private readonly List<Func<IServiceProvider, IWorkDefinitionSource>> workDefinitionSourceFactories = [];
    private readonly List<Func<IServiceProvider, IStartupWorkSource>> startupWorkSourceFactories = [];
    private readonly List<WorkExceptionClassifier> exceptionClassifiers = [];
    private bool includeContributedWork = true;
    private bool requiresAuthorization = true;
    private WorkSystemAuthorizationConfiguration authorization = WorkSystemAuthorizationConfiguration.Default;
    private bool startWithHost;
    private WorkSystemShutdownGracePeriod shutdownGracePeriod = WorkSystemShutdownGracePeriod.HostRelative();
    private WorkSystemRetentionConfiguration retention = WorkSystemRetentionConfiguration.Default;
    private WorkSystemCapacityConfiguration capacity = WorkSystemCapacityConfiguration.Default;
    private WorkSystemProfilingConfiguration profiling = WorkSystemProfilingConfiguration.Default;

    public IWorkSystemBuilder WithWorkDefaults(
        Action<IWorkDefinitionBuilder> register,
        Action<IWorkConfigurationBuilder>? configure = null,
        Action<IWorkAuthorizationBuilder>? authorize = null)
    {
        ArgumentNullException.ThrowIfNull(register);

        register(new DefaultingWorkDefinitionBuilder(
            new SystemWorkDefinitionBuilderAdapter(this),
            configure,
            authorize));
        return this;
    }

    public IWorkSystemBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute)
        => this.AddWork(definition, execute, configure: null, authorize: null);

    public IWorkSystemBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure)
        => this.AddWork(definition, execute, configure, authorize: null);

    public IWorkSystemBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(execute);

        var registration = WorkConfigurationComposer.ApplyRegistration(definition, executorType: null, configure, authorize);
        this.RegisterInitializerTypes(registration);
        this.registeredWork.Add(new RegisteredWork(
            registration.Definition,
            _ => new DelegateWorkExecutor(execute),
            registration.ExceptionClassifiers,
            registration.AutomaticStarts,
            registration.Initializers,
            registration.OperateAuthorization));
        return this;
    }

    public IWorkSystemBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute)
        => this.AddWork(definition, execute, configure: null, authorize: null);

    public IWorkSystemBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure)
        => this.AddWork(definition, execute, configure, authorize: null);

    public IWorkSystemBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(execute);

        definition = WorkExecutorAdapterFactory.ApplyTypedSchemas<TInput>(definition);
        var registration = WorkConfigurationComposer.ApplyRegistration(definition, executorType: null, configure, authorize);
        this.RegisterInitializerTypes(registration);
        this.registeredWork.Add(new RegisteredWork(
            registration.Definition,
            _ => new TypedDelegateWorkExecutor<TInput>(execute),
            registration.ExceptionClassifiers,
            registration.AutomaticStarts,
            registration.Initializers,
            registration.OperateAuthorization));
        return this;
    }

    public IWorkSystemBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute)
        => this.AddWork(definition, execute, configure: null, authorize: null);

    public IWorkSystemBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        Action<IWorkConfigurationBuilder>? configure)
        => this.AddWork(definition, execute, configure, authorize: null);

    public IWorkSystemBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(execute);

        definition = WorkExecutorAdapterFactory.ApplyTypedSchemas<TInput, TOutput>(definition);
        var registration = WorkConfigurationComposer.ApplyRegistration(definition, executorType: null, configure, authorize);
        this.RegisterInitializerTypes(registration);
        this.registeredWork.Add(new RegisteredWork(
            registration.Definition,
            _ => new TypedDelegateWorkExecutor<TInput, TOutput>(execute),
            registration.ExceptionClassifiers,
            registration.AutomaticStarts,
            registration.Initializers,
            registration.OperateAuthorization));
        return this;
    }

    public IWorkSystemBuilder AddWork<TExecutor>(WorkDefinition definition)
        where TExecutor : class
        => this.AddWork<TExecutor>(definition, configure: null, authorize: null);

    public IWorkSystemBuilder AddWork<TExecutor>()
        where TExecutor : class
        => this.AddWork<TExecutor>(
            WorkConfigurationComposer.CreateDefinitionFromAttributes(typeof(TExecutor)),
            configure: null,
            authorize: null);

    public IWorkSystemBuilder AddWork<TExecutor>(
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder>? configure)
        where TExecutor : class
        => this.AddWork<TExecutor>(definition, configure, authorize: null);

    public IWorkSystemBuilder AddWork<TExecutor>(
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
        where TExecutor : class
    {
        ArgumentNullException.ThrowIfNull(definition);
        WorkExecutorAdapterFactory.ThrowIfUnsupported(typeof(TExecutor));

        services.AddScoped<TExecutor>();
        var registration = WorkConfigurationComposer.ApplyRegistration(definition, typeof(TExecutor), configure, authorize);
        this.RegisterInitializerTypes(registration);
        this.registeredWork.Add(new RegisteredWork(
            registration.Definition,
            serviceProvider => WorkExecutorAdapterFactory.Create(serviceProvider.GetRequiredService<TExecutor>()),
            registration.ExceptionClassifiers,
            registration.AutomaticStarts,
            registration.Initializers,
            registration.OperateAuthorization));
        return this;
    }

    public IWorkSystemBuilder StartWithHost(bool enabled = true)
    {
        this.startWithHost = enabled;
        return this;
    }

    public IWorkSystemBuilder UseShutdownGracePeriod(TimeSpan gracePeriod)
    {
        this.shutdownGracePeriod = WorkSystemShutdownGracePeriod.Explicit(gracePeriod);
        return this;
    }

    public IWorkSystemBuilder UseShutdownGracePeriodRatio(double hostShutdownTimeoutRatio)
    {
        this.shutdownGracePeriod = WorkSystemShutdownGracePeriod.HostRelative(
            hostShutdownTimeoutRatio,
            nameof(hostShutdownTimeoutRatio));
        return this;
    }

    public IWorkSystemBuilder UseRetention(WorkSystemRetentionConfiguration retention)
    {
        ArgumentNullException.ThrowIfNull(retention);
        ValidateRetention(retention);

        this.retention = retention;
        return this;
    }

    public IWorkSystemBuilder ConfigureRetention(int? maximumFinalWorkers = null)
    {
        this.retention = this.retention with
        {
            MaximumFinalWorkers = maximumFinalWorkers ?? WorkSystemRetentionConfiguration.Default.MaximumFinalWorkers,
        };
        ValidateRetention(this.retention);
        return this;
    }

    public IWorkSystemBuilder UseCapacity(WorkSystemCapacityConfiguration capacity)
    {
        ArgumentNullException.ThrowIfNull(capacity);
        ValidateCapacity(capacity);

        this.capacity = capacity;
        return this;
    }

    public IWorkSystemBuilder ConfigureCapacity(int? maximumWorkers = null)
    {
        this.capacity = this.capacity with
        {
            MaximumWorkers = maximumWorkers ?? WorkSystemCapacityConfiguration.Default.MaximumWorkers,
        };
        ValidateCapacity(this.capacity);
        return this;
    }

    public IWorkSystemBuilder UseProfiling(WorkSystemProfilingConfiguration profiling)
    {
        ArgumentNullException.ThrowIfNull(profiling);
        ValidateProfiling(profiling);

        this.profiling = profiling;
        return this;
    }

    public IWorkSystemBuilder ConfigureProfiling(int? maximumAutomaticInstrumentationNodes = null)
    {
        this.profiling = this.profiling with
        {
            MaximumAutomaticInstrumentationNodes = maximumAutomaticInstrumentationNodes ??
                WorkSystemProfilingConfiguration.Default.MaximumAutomaticInstrumentationNodes,
        };
        ValidateProfiling(this.profiling);
        return this;
    }

    public IWorkSystemBuilder IncludeContributedWork(bool enabled = true)
    {
        this.includeContributedWork = enabled;
        return this;
    }

    public IWorkSystemBuilder RequireAuthorization(bool required = true)
    {
        this.requiresAuthorization = required;
        return this;
    }

    public IWorkSystemBuilder ConfigureAuthorization(Action<IWorkSystemAuthorizationBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var builder = new WorkSystemAuthorizationBuilder(this.authorization);
        configure(builder);
        this.authorization = builder.Build();
        return this;
    }

    public IWorkSystemBuilder AddWork<TExecutor>(Action<IWorkConfigurationBuilder> configure)
        where TExecutor : class
        => this.AddWork<TExecutor>(
            WorkConfigurationComposer.CreateDefinitionFromAttributes(typeof(TExecutor)),
            configure,
            authorize: null);

    public IWorkSystemBuilder AddWork<TExecutor>(
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
        where TExecutor : class
        => this.AddWork<TExecutor>(
            WorkConfigurationComposer.CreateDefinitionFromAttributes(typeof(TExecutor)),
            configure,
            authorize);

    public IWorkSystemBuilder AddWorkflow(
        WorkflowDefinition definition,
        Action<IWorkflowBuilder> build)
        => this.AddWorkflow(definition, build, authorize: null);

    public IWorkSystemBuilder AddWorkflow(
        WorkflowDefinition definition,
        Action<IWorkflowBuilder> build,
        Action<IWorkAuthorizationBuilder>? authorize)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(build);

        var authorizedDefinition = ApplyWorkflowAuthorization(definition, authorize, out var operateAuthorization);
        var builder = new WorkflowBuilder();
        build(builder);
        this.registeredWorkflows.Add(new RegisteredWorkflow(
            authorizedDefinition,
            builder.Build(),
            operateAuthorization));
        return this;
    }

    public IWorkSystemBuilder AddWorkDefinitionSource<TSource>()
        where TSource : class, IWorkDefinitionSource
    {
        services.TryAddScoped<TSource>();
        return this.AddWorkDefinitionSource(serviceProvider => serviceProvider.GetRequiredService<TSource>());
    }

    public IWorkSystemBuilder AddWorkDefinitionSource<TSource>(Func<IServiceProvider, TSource> sourceFactory)
        where TSource : class, IWorkDefinitionSource
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);

        this.workDefinitionSourceFactories.Add(serviceProvider => sourceFactory(serviceProvider));
        return this;
    }

    public IWorkSystemBuilder AddStartupWorkSource<TSource>()
        where TSource : class, IStartupWorkSource
    {
        services.TryAddScoped<TSource>();
        return this.AddStartupWorkSource(serviceProvider => serviceProvider.GetRequiredService<TSource>());
    }

    public IWorkSystemBuilder AddStartupWorkSource<TSource>(Func<IServiceProvider, TSource> sourceFactory)
        where TSource : class, IStartupWorkSource
    {
        ArgumentNullException.ThrowIfNull(sourceFactory);

        this.startupWorkSourceFactories.Add(serviceProvider => sourceFactory(serviceProvider));
        return this;
    }

    public IWorkSystemBuilder ClassifyExceptions(WorkExceptionClassifier classifier)
    {
        ArgumentNullException.ThrowIfNull(classifier);

        this.exceptionClassifiers.Add(classifier);
        return this;
    }

    internal WorkSystemRegistration BuildRegistration()
        => new(
            WorkSystemId.New(),
            name,
            [.. this.registeredWork],
            [.. this.registeredWorkflows],
            [.. this.workDefinitionSourceFactories],
            [.. this.startupWorkSourceFactories],
            [.. this.exceptionClassifiers],
            this.includeContributedWork,
            this.requiresAuthorization,
            this.authorization,
            this.startWithHost,
            this.shutdownGracePeriod,
            this.retention,
            this.capacity,
            this.profiling);

    private static WorkflowDefinition ApplyWorkflowAuthorization(
        WorkflowDefinition definition,
        Action<IWorkAuthorizationBuilder>? authorize,
        out WorkOperateAuthorizationConfiguration operateAuthorization)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var authorization = definition.Authorization;
        operateAuthorization = WorkOperateAuthorizationConfiguration.FromDefinition(authorization);
        if (authorize is not null)
        {
            var builder = new WorkAuthorizationBuilder();
            authorize(builder);
            var registration = builder.BuildRegistration();
            authorization = registration.DefinitionAuthorization;
            operateAuthorization = registration.OperateAuthorization;
        }

        return definition with
        {
            Authorization = authorization,
        };
    }

    private void RegisterInitializerTypes(WorkRegistrationConfiguration registration)
    {
        foreach (var initializerType in registration.InitializerTypes)
        {
            services.TryAddScoped(initializerType);
        }
    }

    private static void ValidateRetention(WorkSystemRetentionConfiguration retention)
    {
        if (retention.MaximumFinalWorkers <= 0)
        {
            throw new InvalidOperationException("System retention maximum final workers must be greater than zero.");
        }
    }

    private static void ValidateCapacity(WorkSystemCapacityConfiguration capacity)
    {
        if (capacity.MaximumWorkers <= 0)
        {
            throw new InvalidOperationException("System capacity maximum workers must be greater than zero.");
        }
    }

    private static void ValidateProfiling(WorkSystemProfilingConfiguration profiling)
    {
        if (profiling.MaximumAutomaticInstrumentationNodes <= 0)
        {
            throw new InvalidOperationException("System profiling maximum automatic instrumentation nodes must be greater than zero.");
        }
    }
}
