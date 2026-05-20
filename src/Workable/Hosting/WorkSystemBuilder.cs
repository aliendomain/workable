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
    private readonly List<Func<IServiceProvider, IWorkDefinitionSource>> workDefinitionSourceFactories = [];
    private readonly List<Func<IServiceProvider, IStartupWorkSource>> startupWorkSourceFactories = [];
    private readonly List<WorkExceptionClassifier> exceptionClassifiers = [];
    private bool includeContributedWork = true;
    private bool requiresAuthorization = true;
    private bool startWithHost;
    private WorkSystemShutdownGracePeriod shutdownGracePeriod = WorkSystemShutdownGracePeriod.HostRelative();
    private WorkSystemRetentionConfiguration retention = WorkSystemRetentionConfiguration.Default;
    private WorkSystemCapacityConfiguration capacity = WorkSystemCapacityConfiguration.Default;
    private Func<IServiceProvider, IDotNetWorkOriginProvider>? dotNetOriginProviderFactory;

    public IWorkSystemBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute)
        => this.AddWork(definition, execute, configure: null);

    public IWorkSystemBuilder AddWork(
        WorkDefinition definition,
        Func<IWorkExecutionContext, WorkInput?, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(execute);

        var registration = WorkConfigurationComposer.ApplyRegistration(definition, executorType: null, configure);
        this.RegisterInitializerTypes(registration);
        this.registeredWork.Add(new RegisteredWork(
            registration.Definition,
            _ => new DelegateWorkExecutor(execute),
            registration.ExceptionClassifiers,
            registration.AutomaticStarts,
            registration.Initializers));
        return this;
    }

    public IWorkSystemBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute)
        => this.AddWork(definition, execute, configure: null);

    public IWorkSystemBuilder AddWork<TInput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult>> execute,
        Action<IWorkConfigurationBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(execute);

        definition = WorkExecutorAdapterFactory.ApplyTypedSchemas<TInput>(definition);
        var registration = WorkConfigurationComposer.ApplyRegistration(definition, executorType: null, configure);
        this.RegisterInitializerTypes(registration);
        this.registeredWork.Add(new RegisteredWork(
            registration.Definition,
            _ => new TypedDelegateWorkExecutor<TInput>(execute),
            registration.ExceptionClassifiers,
            registration.AutomaticStarts,
            registration.Initializers));
        return this;
    }

    public IWorkSystemBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute)
        => this.AddWork(definition, execute, configure: null);

    public IWorkSystemBuilder AddWork<TInput, TOutput>(
        WorkDefinition definition,
        Func<IWorkExecutionContext, TInput, CancellationToken, Task<WorkExecutionResult<TOutput>>> execute,
        Action<IWorkConfigurationBuilder>? configure)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(execute);

        definition = WorkExecutorAdapterFactory.ApplyTypedSchemas<TInput, TOutput>(definition);
        var registration = WorkConfigurationComposer.ApplyRegistration(definition, executorType: null, configure);
        this.RegisterInitializerTypes(registration);
        this.registeredWork.Add(new RegisteredWork(
            registration.Definition,
            _ => new TypedDelegateWorkExecutor<TInput, TOutput>(execute),
            registration.ExceptionClassifiers,
            registration.AutomaticStarts,
            registration.Initializers));
        return this;
    }

    public IWorkSystemBuilder AddWork<TExecutor>(WorkDefinition definition)
        where TExecutor : class
        => this.AddWork<TExecutor>(definition, configure: null);

    public IWorkSystemBuilder AddWork<TExecutor>()
        where TExecutor : class
        => this.AddWork<TExecutor>(
            WorkConfigurationComposer.CreateDefinitionFromAttributes(typeof(TExecutor)),
            configure: null);

    public IWorkSystemBuilder AddWork<TExecutor>(
        WorkDefinition definition,
        Action<IWorkConfigurationBuilder>? configure)
        where TExecutor : class
    {
        ArgumentNullException.ThrowIfNull(definition);
        WorkExecutorAdapterFactory.ThrowIfUnsupported(typeof(TExecutor));

        services.AddScoped<TExecutor>();
        var registration = WorkConfigurationComposer.ApplyRegistration(definition, typeof(TExecutor), configure);
        this.RegisterInitializerTypes(registration);
        this.registeredWork.Add(new RegisteredWork(
            registration.Definition,
            serviceProvider => WorkExecutorAdapterFactory.Create(serviceProvider.GetRequiredService<TExecutor>()),
            registration.ExceptionClassifiers,
            registration.AutomaticStarts,
            registration.Initializers));
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

    public IWorkSystemBuilder AddWork<TExecutor>(Action<IWorkConfigurationBuilder> configure)
        where TExecutor : class
        => this.AddWork<TExecutor>(
            WorkConfigurationComposer.CreateDefinitionFromAttributes(typeof(TExecutor)),
            configure);

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

    public IWorkSystemBuilder UseDotNetOriginProvider<TProvider>()
        where TProvider : class, IDotNetWorkOriginProvider
    {
        services.AddSingleton<TProvider>();
        this.dotNetOriginProviderFactory = serviceProvider => serviceProvider.GetRequiredService<TProvider>();
        return this;
    }

    public IWorkSystemBuilder UseDotNetOriginProvider(Func<IServiceProvider, IDotNetWorkOriginProvider> factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        this.dotNetOriginProviderFactory = factory;
        return this;
    }

    internal WorkSystemRegistration BuildRegistration()
        => new(
            WorkSystemId.New(),
            name,
            [.. this.registeredWork],
            [.. this.workDefinitionSourceFactories],
            [.. this.startupWorkSourceFactories],
            [.. this.exceptionClassifiers],
            this.dotNetOriginProviderFactory,
            this.includeContributedWork,
            this.requiresAuthorization,
            this.startWithHost,
            this.shutdownGracePeriod,
            this.retention,
            this.capacity);

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
}
