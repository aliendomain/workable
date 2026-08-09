using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

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
    private WorkSystemIterationStatusConfiguration iterationStatuses = WorkSystemIterationStatusConfiguration.Default;
    private WorkSystemProfilingConfiguration profiling = WorkSystemProfilingConfiguration.Default;
    private WorkSystemExecutionDiagnosticsPersistenceConfiguration executionDiagnostics =
        WorkSystemExecutionDiagnosticsPersistenceConfiguration.Default;

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

    public IWorkSystemBuilder UseIterationStatuses(WorkSystemIterationStatusConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ValidateIterationStatuses(configuration);

        this.iterationStatuses = configuration;
        return this;
    }

    public IWorkSystemBuilder ConfigureIterationStatuses(
        int? replayItemCapacity = null,
        int? replayPayloadByteCapacity = null,
        int? systemReplayItemCapacity = null,
        int? systemReplayByteCapacity = null,
        int? maximumPayloadBytes = null,
        int? maximumTypeBytes = null,
        int? maximumSubscriptions = null,
        int? maximumSubscriptionsPerIteration = null)
    {
        this.iterationStatuses = this.iterationStatuses with
        {
            ReplayItemCapacity = replayItemCapacity ?? WorkSystemIterationStatusConfiguration.Default.ReplayItemCapacity,
            ReplayPayloadByteCapacity = replayPayloadByteCapacity ??
                WorkSystemIterationStatusConfiguration.Default.ReplayPayloadByteCapacity,
            SystemReplayItemCapacity = systemReplayItemCapacity ??
                WorkSystemIterationStatusConfiguration.Default.SystemReplayItemCapacity,
            SystemReplayByteCapacity = systemReplayByteCapacity ??
                WorkSystemIterationStatusConfiguration.Default.SystemReplayByteCapacity,
            MaximumPayloadBytes = maximumPayloadBytes ?? WorkSystemIterationStatusConfiguration.Default.MaximumPayloadBytes,
            MaximumTypeBytes = maximumTypeBytes ?? WorkSystemIterationStatusConfiguration.Default.MaximumTypeBytes,
            MaximumSubscriptions = maximumSubscriptions ??
                WorkSystemIterationStatusConfiguration.Default.MaximumSubscriptions,
            MaximumSubscriptionsPerIteration = maximumSubscriptionsPerIteration ??
                WorkSystemIterationStatusConfiguration.Default.MaximumSubscriptionsPerIteration,
        };
        ValidateIterationStatuses(this.iterationStatuses);
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

    public IWorkSystemBuilder UseExecutionDiagnosticsPersistence(
        WorkSystemExecutionDiagnosticsPersistenceConfiguration persistence)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        ValidateExecutionDiagnostics(persistence);
        this.executionDiagnostics = persistence;
        return this;
    }

    public IWorkSystemBuilder PersistExecutionDiagnostics(
        TimeSpan retentionPeriod,
        LogLevel minimumLogLevel = LogLevel.Information,
        WorkProfileCaptureMode profileCaptureMode = WorkProfileCaptureMode.Bounded)
        => this.UseExecutionDiagnosticsPersistence(new WorkSystemExecutionDiagnosticsPersistenceConfiguration
        {
            IsEnabled = true,
            Retention = retentionPeriod,
            MinimumLogLevel = minimumLogLevel,
            ProfileCaptureMode = profileCaptureMode,
        });

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
            this.iterationStatuses,
            this.profiling,
            this.executionDiagnostics);

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

    private static void ValidateIterationStatuses(WorkSystemIterationStatusConfiguration configuration)
    {
        if (configuration.ReplayItemCapacity <= 0)
        {
            throw new InvalidOperationException("Iteration status replay item capacity must be greater than zero.");
        }

        if (configuration.MaximumPayloadBytes <= 0)
        {
            throw new InvalidOperationException("Iteration status maximum payload bytes must be greater than zero.");
        }

        if (configuration.MaximumTypeBytes <= 0)
        {
            throw new InvalidOperationException("Iteration status maximum type bytes must be greater than zero.");
        }

        if (configuration.ReplayPayloadByteCapacity <= 0)
        {
            throw new InvalidOperationException("Iteration status replay payload byte capacity must be greater than zero.");
        }

        if (configuration.SystemReplayItemCapacity <= 0)
        {
            throw new InvalidOperationException("Iteration status system replay item capacity must be greater than zero.");
        }

        if (configuration.SystemReplayByteCapacity <= 0)
        {
            throw new InvalidOperationException("Iteration status system replay byte capacity must be greater than zero.");
        }

        if (configuration.ReplayPayloadByteCapacity <
            (long)configuration.MaximumPayloadBytes + configuration.MaximumTypeBytes)
        {
            throw new InvalidOperationException(
                "Iteration status replay payload byte capacity cannot be less than the combined maximum type and payload bytes.");
        }

        if (configuration.SystemReplayItemCapacity < configuration.ReplayItemCapacity)
        {
            throw new InvalidOperationException(
                "Iteration status system replay item capacity cannot be less than the per-iteration replay item capacity.");
        }

        if (configuration.SystemReplayByteCapacity < configuration.ReplayPayloadByteCapacity)
        {
            throw new InvalidOperationException(
                "Iteration status system replay byte capacity cannot be less than the per-iteration replay byte capacity.");
        }

        if (configuration.MaximumSubscriptionsPerIteration <= 0)
        {
            throw new InvalidOperationException(
                "Iteration status maximum subscriptions per iteration must be greater than zero.");
        }

        if (configuration.MaximumSubscriptions <= 0)
        {
            throw new InvalidOperationException("Iteration status maximum subscriptions must be greater than zero.");
        }

        if (configuration.MaximumSubscriptions < configuration.MaximumSubscriptionsPerIteration)
        {
            throw new InvalidOperationException(
                "Iteration status maximum subscriptions cannot be less than the per-iteration subscription limit.");
        }
    }

    private static void ValidateProfiling(WorkSystemProfilingConfiguration profiling)
    {
        if (profiling.MaximumAutomaticInstrumentationNodes <= 0)
        {
            throw new InvalidOperationException("System profiling maximum automatic instrumentation nodes must be greater than zero.");
        }
    }

    private static void ValidateExecutionDiagnostics(
        WorkSystemExecutionDiagnosticsPersistenceConfiguration persistence)
    {
        if (persistence.Retention < WorkExecutionDiagnosticsPersistenceConfiguration.MinimumRetention ||
            persistence.Retention > WorkExecutionDiagnosticsPersistenceConfiguration.MaximumRetention)
        {
            throw new InvalidOperationException(
                "System execution diagnostics retention must be between one minute and 30 days.");
        }

        if (persistence.IsEnabled &&
            (persistence.MinimumLogLevel == LogLevel.None || !Enum.IsDefined(persistence.MinimumLogLevel)))
        {
            throw new InvalidOperationException(
                "Enabled system execution diagnostics require a persistent log level other than None.");
        }

        if (persistence.IsEnabled && !Enum.IsDefined(persistence.ProfileCaptureMode))
        {
            throw new InvalidOperationException(
                "Enabled system execution diagnostics require a valid profile capture mode.");
        }

        if (persistence.ChannelCapacity <= 0 || persistence.ControlOperationCapacity <= 0)
        {
            throw new InvalidOperationException("Execution diagnostics evidence and control capacities must be greater than zero.");
        }

        if (persistence.MaximumPendingLogBytes <= 0 ||
            persistence.MaximumLogsPerIteration <= 0 ||
            persistence.MaximumLogBytesPerIteration <= 0 ||
            persistence.MaximumLogMessageLength <= 0 ||
            persistence.MaximumLogPropertiesLength <= 0 ||
            persistence.MaximumExceptionTextLength <= 0)
        {
            throw new InvalidOperationException("Execution diagnostics log bounds must be greater than zero.");
        }

        if (persistence.MaximumPendingProfiles <= 0 ||
            persistence.MaximumProfileNodeCount <= 0 ||
            persistence.MaximumProfileJsonLength <= 0)
        {
            throw new InvalidOperationException(
                "Execution diagnostics profile bounds must be greater than zero.");
        }

        if (persistence.MaximumCaptureRules <= 0)
        {
            throw new InvalidOperationException("Execution diagnostics maximum capture rules must be greater than zero.");
        }

        if (persistence.LogBatchSize <= 0 || persistence.LogBatchSize > persistence.ChannelCapacity)
        {
            throw new InvalidOperationException(
                "Execution diagnostics log batch size must be greater than zero and no larger than channel capacity.");
        }

        if (persistence.CleanupInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Execution diagnostics cleanup interval must be greater than zero.");
        }


        if (persistence.CleanupBatchSize <= 0 ||
            persistence.MaximumCleanupBatchesPerInterval <= 0 ||
            persistence.CleanupBacklogDelay < TimeSpan.Zero)
        {
            throw new InvalidOperationException(
                "Execution diagnostics cleanup bounds must be greater than zero and the backlog delay must not be negative.");
        }
    }
}
