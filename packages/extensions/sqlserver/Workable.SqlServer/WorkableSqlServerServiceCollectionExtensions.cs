using Microsoft.Extensions.DependencyInjection;

namespace Workable.SqlServer;

public static class WorkableSqlServerServiceCollectionExtensions
{
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
