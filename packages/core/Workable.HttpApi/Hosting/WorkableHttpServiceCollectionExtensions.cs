using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Workable;

/// <summary>
/// Registers the services required by the Workable HTTP API adapter.
/// </summary>
public static class WorkableHttpServiceCollectionExtensions
{
    /// <summary>
    /// Adds the built-in Workable HTTP API adapter services to the host.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">Optional HTTP API adapter configuration.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddWorkableHttpApi(
        this IServiceCollection services,
        Action<WorkableHttpApiOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddOptions<WorkableHttpApiOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        services.AddWorkableAspNetCoreAuthorization();
        services.AddScoped<WorkableHttpRequestAccessContext>();
        services.TryAddSingleton<WorkableHttpTopologyResolver>();
        services.TryAddSingleton<WorkableHttpCatalogAdapter>();
        services.TryAddSingleton<WorkableHttpQueueAdapter>();
        services.TryAddSingleton<WorkableHttpQueryAdapter>();
        services.TryAddSingleton<WorkableHttpWorkerAdapter>();
        return services;
    }
}
