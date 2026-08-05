using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Workable;

/// <summary>
/// Registers automatic outbound HTTP client profiling for Workable workers.
/// </summary>
public static class WorkableHttpClientProfilingServiceCollectionExtensions
{
    /// <summary>
    /// Captures outbound <see cref="HttpClient"/> request execution inside active Workable profiles.
    /// </summary>
    /// <remarks>
    /// This hooks the built-in <c>System.Net.Http</c> diagnostics pipeline, so it covers requests
    /// executed through the standard HTTP client handler. Request and response bodies, headers,
    /// query strings, and URI user information are not captured.
    /// </remarks>
    /// <param name="services">The service collection that should receive HTTP client profiling.</param>
    /// <returns>The same service collection so additional application services can be registered.</returns>
    public static IServiceCollection AddWorkableHttpClientProfiling(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        if (services.Any(descriptor => descriptor.ServiceType == typeof(WorkableHttpClientProfilingRegistrationMarker)))
        {
            return services;
        }

        services.AddSingleton<WorkableHttpClientProfilingRegistrationMarker>();
        services.AddSingleton<IWorkSystemCapabilityContributor, WorkableHttpClientProfilingCapabilityContributor>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IWorkProfilingInstrumentationFactory, WorkableHttpClientProfilingInstrumentationFactory>());
        return services;
    }
}
