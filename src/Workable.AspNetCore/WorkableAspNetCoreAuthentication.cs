using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Workable;

public static class WorkableAspNetCoreAuthentication
{
    private static readonly object ExplicitAuthenticationCacheKey = new();

    public static bool IsAuthenticated(HttpContext? httpContext)
        => httpContext?.User?.Identity?.IsAuthenticated == true;

    public static async Task<bool> EnsureAuthenticatedAsync(HttpContext? httpContext)
        => (await GetAuthenticatedPrincipalAsync(httpContext))?.Identity?.IsAuthenticated == true;

    public static async Task<ClaimsPrincipal?> GetAuthenticatedPrincipalAsync(HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return null;
        }

        var authenticationScheme = GetTransportAuthenticationScheme(httpContext);
        if (string.IsNullOrWhiteSpace(authenticationScheme))
        {
            return IsAuthenticated(httpContext)
                ? httpContext.User
                : null;
        }

        if (httpContext.Items.TryGetValue(ExplicitAuthenticationCacheKey, out var cached) &&
            cached is ClaimsPrincipal cachedPrincipal)
        {
            return cachedPrincipal.Identity?.IsAuthenticated == true
                ? cachedPrincipal
                : null;
        }

        var result = await httpContext.AuthenticateAsync(authenticationScheme);
        var principal = result.Succeeded && result.Principal?.Identity?.IsAuthenticated == true
            ? result.Principal
            : new ClaimsPrincipal(new ClaimsIdentity());
        httpContext.User = principal;
        httpContext.Items[ExplicitAuthenticationCacheKey] = principal;

        return principal.Identity?.IsAuthenticated == true
            ? principal
            : null;
    }

    private static string? GetTransportAuthenticationScheme(HttpContext httpContext)
        => httpContext.RequestServices
            .GetService<IOptions<WorkableAspNetCoreAuthorizationOptions>>()
            ?.Value
            .TransportAuthenticationScheme;
}
