using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Workable;
public static class WorkableSignalREndpointRouteBuilderExtensions
{
    public static IEndpointConventionBuilder MapWorkableSignalR(
        this IEndpointRouteBuilder endpoints,
        string? path = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        EnsureAllSystemsRequireAuthorization(endpoints.ServiceProvider.GetRequiredService<IWorkSystemRegistry>());

        var options = endpoints.ServiceProvider.GetRequiredService<IOptions<WorkableSignalROptions>>().Value;
        if (path is null)
        {
            path = options.HubPath;
        }
        else
        {
            options.HubPath = path;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var builder = endpoints.MapHub<WorkableRealtimeHub>(path);
        RequireAuthenticated(builder);
        return builder;
    }

    private static void EnsureAllSystemsRequireAuthorization(IWorkSystemRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);

        var unsecuredSystems = registry.Systems
            .Where(system => !system.RequiresAuthorization)
            .Select(system => system.Name ?? "<default>")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (unsecuredSystems.Length == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Workable SignalR requires authorization-enabled systems. The following systems do not require authorization: {string.Join(", ", unsecuredSystems)}.");
    }

    private static void RequireAuthenticated(IEndpointConventionBuilder builder)
    {
        builder.Add(endpointBuilder =>
        {
            var next = endpointBuilder.RequestDelegate
                ?? throw new InvalidOperationException("Workable SignalR endpoint did not provide a request delegate.");
            endpointBuilder.RequestDelegate = async httpContext =>
            {
                if (!HttpMethods.IsOptions(httpContext.Request.Method) &&
                    !await WorkableAspNetCoreAuthentication.EnsureAuthenticatedAsync(httpContext))
                {
                    httpContext.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                await next(httpContext);
            };
        });
    }
}
