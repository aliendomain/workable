using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Workable;

/// <summary>
/// Provides ASP.NET Core-specific helpers for resolving the current authenticated principal for Workable.
/// </summary>
public static class WorkableAspNetCoreAuthentication
{
    private static readonly object ExplicitAuthenticationCacheKey = new();

    /// <summary>
    /// Determines whether the current HTTP user is already authenticated.
    /// </summary>
    /// <param name="httpContext">The HTTP context to inspect.</param>
    /// <returns><see langword="true"/> when the current user is authenticated; otherwise <see langword="false"/>.</returns>
    public static bool IsAuthenticated(HttpContext? httpContext)
        => httpContext?.User?.Identity?.IsAuthenticated == true;

    /// <summary>
    /// Ensures the current HTTP request has an authenticated principal available to Workable.
    /// </summary>
    /// <param name="httpContext">The HTTP context to inspect.</param>
    /// <returns><see langword="true"/> when an authenticated principal is available; otherwise <see langword="false"/>.</returns>
    public static async Task<bool> EnsureAuthenticatedAsync(HttpContext? httpContext)
        => (await GetAuthenticatedPrincipalAsync(httpContext))?.Identity?.IsAuthenticated == true;

    /// <summary>
    /// Gets the authenticated principal for the current HTTP request, applying an explicit transport scheme when configured.
    /// </summary>
    /// <param name="httpContext">The HTTP context to inspect.</param>
    /// <returns>The authenticated principal, or <see langword="null"/> when no authenticated principal is available.</returns>
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
