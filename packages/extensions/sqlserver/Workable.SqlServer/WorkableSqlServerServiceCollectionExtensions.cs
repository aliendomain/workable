using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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
        return services;
    }
}
