using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Workable.SqlServer;

public static class WorkableSqlServerServiceCollectionExtensions
{
    /// <summary>
    /// Registers SQL Server storage for persistent execution diagnostics and future shared persistence features.
    /// </summary>
    public static IServiceCollection AddWorkableSqlServerPersistence(
        this IServiceCollection services,
        string connectionString,
        string schemaName = "workable",
        string persistenceScope = "default",
        bool autoDeploySchema = true)
        => services.AddWorkableSqlServerPersistence(new WorkableSqlServerPersistenceOptions
        {
            ConnectionString = connectionString,
            SchemaName = schemaName,
            PersistenceScope = persistenceScope,
            AutoDeploySchema = autoDeploySchema,
        });

    /// <summary>
    /// Registers SQL Server storage for persistent execution diagnostics and future shared persistence features.
    /// </summary>
    public static IServiceCollection AddWorkableSqlServerPersistence(
        this IServiceCollection services,
        WorkableSqlServerPersistenceOptions options)
        => AddWorkableSqlServerPersistence(services, options, isExplicitRegistration: true);

    private static IServiceCollection AddWorkableSqlServerPersistence(
        IServiceCollection services,
        WorkableSqlServerPersistenceOptions options,
        bool isExplicitRegistration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SchemaName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.PersistenceScope);

        var existing = services
            .LastOrDefault(descriptor =>
                descriptor.ServiceType == typeof(WorkableSqlServerPersistenceRegistration))
            ?.ImplementationInstance as WorkableSqlServerPersistenceRegistration;
        if (existing is not null)
        {
            if (!SameStore(existing.Options, options))
            {
                throw new InvalidOperationException(
                    "Workable SQL Server persistence is already registered with a different connection string or schema.");
            }

            if (existing.IsExplicitRegistration && isExplicitRegistration && existing.Options != options)
            {
                throw new InvalidOperationException(
                    "Workable SQL Server persistence is already registered with different options.");
            }

            if (!existing.IsExplicitRegistration && isExplicitRegistration)
            {
                services.Replace(ServiceDescriptor.Singleton(
                    new WorkableSqlServerPersistenceRegistration(options, IsExplicitRegistration: true)));
            }

            return services;
        }

        services.AddSingleton(new WorkableSqlServerPersistenceRegistration(options, isExplicitRegistration));
        services.AddSingleton(serviceProvider =>
            serviceProvider.GetRequiredService<WorkableSqlServerPersistenceRegistration>().Options);
        services.TryAddSingleton<WorkableSqlServerExecutionDiagnosticsRepository>();
        services.TryAddSingleton<IWorkExecutionDiagnosticsRepository>(serviceProvider =>
            serviceProvider.GetRequiredService<WorkableSqlServerExecutionDiagnosticsRepository>());
        services.TryAddSingleton(serviceProvider =>
        {
            var persistenceOptions = serviceProvider.GetRequiredService<WorkableSqlServerPersistenceOptions>();
            var queueOptions = serviceProvider.GetService<WorkableSqlServerQueueDurabilityOptions>();
            return new WorkableSqlServerSchemaInitializer(
                persistenceOptions.ConnectionString,
                persistenceOptions.SchemaName,
                persistenceOptions.AutoDeploySchema || queueOptions?.AutoDeploySchema == true);
        });
        return services;
    }

    private static bool SameStore(
        WorkableSqlServerPersistenceOptions left,
        WorkableSqlServerPersistenceOptions right)
        => string.Equals(left.ConnectionString, right.ConnectionString, StringComparison.Ordinal) &&
            string.Equals(left.SchemaName, right.SchemaName, StringComparison.Ordinal);

    /// <summary>
    /// Captures <c>Microsoft.Data.SqlClient</c> command execution inside active Workable profiles.
    /// </summary>
    /// <remarks>
    /// This hooks provider diagnostics, so it covers any data access path that executes through
    /// <c>Microsoft.Data.SqlClient</c>.
    /// </remarks>
    public static IServiceCollection AddWorkableSqlServerProfiling(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(WorkableSqlServerProfilingRegistrationMarker)))
        {
            return services;
        }

        services.AddSingleton<WorkableSqlServerProfilingRegistrationMarker>();
        services.AddSingleton<IWorkSystemCapabilityContributor, WorkableSqlServerProfilingCapabilityContributor>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IWorkProfilingInstrumentationFactory, WorkableSqlServerProfilingInstrumentationFactory>());
        return services;
    }

    public static IServiceCollection AddWorkableSqlServerDurableQueue(
        this IServiceCollection services,
        string connectionString,
        string schemaName = "workable",
        bool autoDeploySchema = true)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);

        return services.AddWorkableSqlServerDurableQueue(new WorkableSqlServerQueueDurabilityOptions
        {
            ConnectionString = connectionString,
            SchemaName = schemaName,
            AutoDeploySchema = autoDeploySchema,
        });
    }

    public static IServiceCollection AddWorkableSqlServerDurableQueue(
        this IServiceCollection services,
        WorkableSqlServerQueueDurabilityOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.ConnectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.SchemaName);
        if (options.EnqueueBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.EnqueueBatchSize,
                "SQL Server durable queue enqueue batch size must be greater than zero.");
        }

        if (options.EnqueueBatchWindow < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.EnqueueBatchWindow,
                "SQL Server durable queue enqueue batch window must not be negative.");
        }

        if (options.ClaimBatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.ClaimBatchSize,
                "SQL Server durable queue claim batch size must be greater than zero.");
        }

        if (options.RecentClaimSampleCapacity < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                options.RecentClaimSampleCapacity,
                "SQL Server durable queue recent claim sample capacity must not be negative.");
        }

        services.AddSingleton(options);
        services.AddSingleton(new global::Workable.WorkQueueDurabilityRuntimeOptions
        {
            ClaimBatchSize = options.ClaimBatchSize,
            RecentClaimSampleCapacity = options.RecentClaimSampleCapacity,
        });
        services.AddSingleton<WorkableSqlServerQueueDurabilityStore>();
        services.AddSingleton<IWorkPersistenceStore>(services => services.GetRequiredService<WorkableSqlServerQueueDurabilityStore>());
        AddWorkableSqlServerPersistence(services, new WorkableSqlServerPersistenceOptions
        {
            ConnectionString = options.ConnectionString,
            SchemaName = options.SchemaName,
            PersistenceScope = "default",
            AutoDeploySchema = options.AutoDeploySchema,
        }, isExplicitRegistration: false);
        return services;
    }
}

internal sealed record WorkableSqlServerPersistenceRegistration(
    WorkableSqlServerPersistenceOptions Options,
    bool IsExplicitRegistration);
