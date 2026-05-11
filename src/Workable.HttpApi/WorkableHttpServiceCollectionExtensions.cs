using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Workable;

public static class WorkableHttpServiceCollectionExtensions
{
    public static IServiceCollection AddWorkableHttpApi(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        services.AddWorkableAspNetCoreOrigins();
        services.TryAddSingleton<WorkableHttpWorkService>();
        return services;
    }
}
