using Microsoft.Extensions.DependencyInjection;

namespace Workable.SqlServer;

public static class WorkableSqlServerServiceCollectionExtensions
{
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
        services.AddSingleton<WorkableSqlServerProfilingLifecycleObserver>();
        services.AddSingleton<IWorkSystemLifecycleObserver>(serviceProvider =>
            serviceProvider.GetRequiredService<WorkableSqlServerProfilingLifecycleObserver>());
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

        services.AddSingleton(options);
        services.AddSingleton<WorkableSqlServerQueueDurabilityStore>();
        services.AddSingleton<IWorkPersistenceStore>(services => services.GetRequiredService<WorkableSqlServerQueueDurabilityStore>());
        return services;
    }
}
