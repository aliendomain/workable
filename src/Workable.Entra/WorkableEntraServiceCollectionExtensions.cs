using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using System.Linq;

namespace Workable;

/// <summary>
/// Registers Microsoft Entra authentication and Workable claim-mapping integration for ASP.NET Core hosts.
/// </summary>
public static class WorkableEntraServiceCollectionExtensions
{
    /// <summary>
    /// Adds Workable Entra authentication using values from configuration.
    /// </summary>
    /// <param name="services">The service collection to configure.</param>
    /// <param name="configuration">The configuration section containing Entra settings.</param>
    /// <returns>The same service collection for chaining.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the configured Entra options are invalid.</exception>
    public static IServiceCollection AddWorkableEntraAuthorization(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        return services.AddWorkableEntraAuthorization(
            WorkableEntraAuthorizationOptions.FromConfiguration(configuration));
    }

    /// <summary>
    /// Adds Workable Entra authentication using an imperative options callback.
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
        var audiences = options.GetAudiences();

        services
            .AddAuthentication()
            .AddJwtBearer(options.AuthenticationScheme, jwt =>
            {
                jwt.Authority = options.Authority;
                jwt.Audience = options.Audience;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateAudience = true,
                    ValidAudiences = audiences,
                    ValidateIssuer = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                };
                jwt.Events = new JwtBearerEvents
                {
                    OnMessageReceived = context =>
                    {
                        if (TryGetSignalRAccessToken(context.HttpContext, options, out var accessToken))
                        {
                            context.Token = accessToken;
                        }

                        return Task.CompletedTask;
                    },
                };
            });

        services.AddAuthorization();
        services.AddWorkableAspNetCoreAuthorization(authorization =>
        {
            authorization.TransportAuthenticationScheme = options.AuthenticationScheme;
            authorization.ActorIdClaimTypes = AddUnique(
                authorization.ActorIdClaimTypes,
                "oid",
                "sub");
            authorization.ActorNameClaimTypes = AddUnique(
                authorization.ActorNameClaimTypes,
                "name",
                "preferred_username");
            authorization.ActorEmailClaimTypes = AddUnique(
                authorization.ActorEmailClaimTypes,
                "email",
                "preferred_username",
                "upn");

            var groupClaimTypes = authorization.GroupClaimTypes.ToList();
            if (options.MapScopesToWorkableGroups)
            {
                AddUnique(groupClaimTypes, WorkableEntraAuthorizationDefaults.ScopeClaimType);
            }

            if (options.MapAppRolesToWorkableGroups)
            {
                AddUnique(
                    groupClaimTypes,
                    WorkableEntraAuthorizationDefaults.RolesClaimType,
                    WorkableEntraAuthorizationDefaults.RoleClaimType,
                    ClaimTypes.Role);
            }

            if (options.MapGroupsToWorkableGroups)
            {
                AddUnique(groupClaimTypes, WorkableEntraAuthorizationDefaults.GroupsClaimType);
            }

            authorization.GroupClaimTypes = groupClaimTypes;
            authorization.GroupClaimValueSeparators = AddUnique(
                authorization.GroupClaimValueSeparators,
                ',',
                ' ');
        });

        return services;
    }

    private static bool TryGetSignalRAccessToken(
        HttpContext httpContext,
        WorkableEntraAuthorizationOptions options,
        out string? accessToken)
    {
        accessToken = null;
        if (!options.AllowSignalRAccessTokensFromQueryString ||
            HasBearerAuthorizationHeader(httpContext) ||
            !httpContext.Request.Query.TryGetValue(options.SignalRAccessTokenQueryStringName, out var values))
        {
            return false;
        }

        var candidate = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(candidate))
        {
            return false;
        }

        foreach (var path in options.GetSignalRAccessTokenQueryStringPaths()
            .Where(path => httpContext.Request.Path.StartsWithSegments(new PathString(path))))
        {
            accessToken = candidate;
            return true;
        }

        return false;
    }

    private static bool HasBearerAuthorizationHeader(HttpContext httpContext)
    {
        var authorization = httpContext.Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(authorization))
        {
            return false;
        }

        return authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> AddUnique(
        IReadOnlyList<string> existing,
        params string[] values)
    {
        var merged = existing.ToList();
        AddUnique(merged, values);
        return merged;
    }

    private static IReadOnlyList<char> AddUnique(
        IReadOnlyList<char> existing,
        params char[] values)
    {
        var merged = existing.ToList();
        foreach (var value in values.Where(value => !merged.Contains(value)))
        {
            merged.Add(value);
        }

        return merged;
    }

    private static void AddUnique(List<string> target, params string[] values)
    {
        foreach (var value in values.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            if (!target.Contains(value, StringComparer.OrdinalIgnoreCase))
            {
                target.Add(value);
            }
        }
    }
}
