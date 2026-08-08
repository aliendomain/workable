using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Workable;

/// <summary>
/// Registers ASP.NET Core integration services for Workable.
/// </summary>
public static class WorkableAspNetCoreServiceCollectionExtensions
{
    /// <summary>
    /// Adds the default ASP.NET Core actor, request-context, authorization-group, and dispatch integrations for Workable.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">Optional configuration for ASP.NET Core authorization integration.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddWorkableAspNetCoreAuthorization(
        this IServiceCollection services,
        Action<WorkableAspNetCoreAuthorizationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.AddAuthorization();
        services.AddOptions<WorkableAspNetCoreAuthorizationOptions>();
        if (configure is not null)
        {
            services.Configure(configure);
        }

        services.TryAddSingleton<IWorkActorFactory, HttpContextWorkActorFactory>();
        services.TryAddSingleton<IWorkRequestContextFactory, HttpContextWorkRequestContextFactory>();
        services.TryAddSingleton<IHttpContextWorkCommandDispatcher, HttpContextWorkCommandDispatcher>();
        services.TryAddSingleton<IHttpContextWorkflowCommandDispatcher, HttpContextWorkflowCommandDispatcher>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IWorkAuthorizationGroupContextProvider, HttpContextClaimsWorkAuthorizationGroupProvider>());
        return services;
    }
}
