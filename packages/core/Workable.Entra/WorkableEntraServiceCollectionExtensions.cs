using System.Linq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Workable;

/// <summary>
/// Integrates host-authenticated Microsoft Entra identities with Workable ASP.NET Core authorization.
/// </summary>
public static class WorkableEntraServiceCollectionExtensions
{
    /// <summary>
    /// Adds Workable integration for the host's existing Microsoft Entra authentication configuration.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddWorkableEntraAuthorization(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (FindRegistration(services) is not null)
        {
            return services;
        }

        return services.AddWorkableEntraAuthorization(new WorkableEntraAuthorizationOptions());
    }

    /// <summary>
    /// Adds Workable Entra integration using values from configuration.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The configuration section containing Workable Entra integration settings.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the configured Entra options are invalid.</exception>
    /// <remarks>
    /// After Workable Entra integration is registered, a configuration section containing none of the recognized
    /// integration keys is ensure-only and preserves the existing option set.
    /// Authentication settings such as tenant, issuer, audience, token validation, and JWT events belong to the host's
    /// authentication registration and are not read from this configuration section.
    /// </remarks>
    public static IServiceCollection AddWorkableEntraAuthorization(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        if (FindRegistration(services) is not null &&
            !WorkableEntraAuthorizationOptions.HasConfiguredValues(configuration))
        {
            return services;
        }

        return services.AddWorkableEntraAuthorization(
            WorkableEntraAuthorizationOptions.FromConfiguration(configuration));
    }

    /// <summary>
    /// Adds Workable Entra integration using an imperative options callback.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configure">The callback that configures Entra options.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the configured Entra options are invalid.</exception>
    public static IServiceCollection AddWorkableEntraAuthorization(
        this IServiceCollection services,
        Action<WorkableEntraAuthorizationOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new WorkableEntraAuthorizationOptions();
        configure(options);
        return services.AddWorkableEntraAuthorization(options);
    }

    private static IServiceCollection AddWorkableEntraAuthorization(
        this IServiceCollection services,
        WorkableEntraAuthorizationOptions options)
    {
        options.ThrowIfInvalid();
        var registration = FindRegistration(services);
        if (registration is not null)
        {
            registration.Options = options;
            return services;
        }

        registration = new WorkableEntraAuthorizationRegistration(options);
        services.AddSingleton(registration);
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IWorkActorClaimsMapper,
            WorkableEntraActorClaimsMapper>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IWorkAuthorizationGroupClaimMapper,
            WorkableEntraAuthorizationGroupClaimMapper>());
        services.AddWorkableAspNetCoreAuthorization(authorization =>
            ConfigureAuthorization(authorization, registration.Options));

        return services;
    }

    private static WorkableEntraAuthorizationRegistration? FindRegistration(IServiceCollection services)
        => services
            .Where(descriptor => descriptor.ServiceType == typeof(WorkableEntraAuthorizationRegistration))
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<WorkableEntraAuthorizationRegistration>()
            .SingleOrDefault();

    private static void ConfigureAuthorization(
        WorkableAspNetCoreAuthorizationOptions authorization,
        WorkableEntraAuthorizationOptions options)
    {
        if (options.AuthenticationScheme is not null)
        {
            authorization.TransportAuthenticationScheme = options.AuthenticationScheme;
        }
    }
}
