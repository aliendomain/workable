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
    private static readonly object AuthenticationCacheKey = new();
    private static readonly AsyncLocal<WorkableAuthenticationSnapshot?> ActiveSnapshot = new();

    private sealed record AuthenticationCacheEntry(WorkableAuthenticationSnapshot? Snapshot);

    internal sealed class WorkableAuthenticationSnapshot(
        ClaimsPrincipal principal,
        ClaimsIdentity identity,
        string? authenticationScheme,
        DateTimeOffset? authenticationExpiresUtc = null)
    {
        public ClaimsPrincipal Principal { get; } = principal;

        public ClaimsIdentity Identity { get; } = identity;

        public string? AuthenticationScheme { get; } = authenticationScheme;

        public DateTimeOffset? AuthenticationExpiresUtc { get; } = authenticationExpiresUtc;

        public WorkActor? Actor { get; set; }

        public IReadOnlySet<string>? ClaimsGroups { get; set; }
    }

    /// <summary>
    /// Determines whether an authenticated principal is available to Workable for the current request.
    /// </summary>
    /// <param name="httpContext">The HTTP context to inspect.</param>
    /// <returns><see langword="true"/> when Workable's current principal is authenticated; otherwise <see langword="false"/>.</returns>
    public static bool IsAuthenticated(HttpContext? httpContext)
        => GetCurrentSnapshot(httpContext) is not null;

    /// <summary>
    /// Ensures the current HTTP request has an authenticated principal available to Workable.
    /// </summary>
    /// <param name="httpContext">The HTTP context to inspect.</param>
    /// <returns><see langword="true"/> when an authenticated principal is available; otherwise <see langword="false"/>.</returns>
    public static async Task<bool> EnsureAuthenticatedAsync(HttpContext? httpContext)
        => await GetAuthenticatedSnapshotAsync(httpContext) is not null;

    /// <summary>
    /// Gets the authenticated principal for the current HTTP request, applying an explicit transport scheme when configured.
    /// </summary>
    /// <param name="httpContext">The HTTP context to inspect.</param>
    /// <returns>The authenticated principal, or <see langword="null"/> when no authenticated principal is available.</returns>
    public static async Task<ClaimsPrincipal?> GetAuthenticatedPrincipalAsync(HttpContext? httpContext)
        => (await GetAuthenticatedSnapshotAsync(httpContext))?.Principal;

    /// <summary>
    /// Invokes the challenge behavior owned by the host's selected authentication scheme.
    /// </summary>
    /// <param name="httpContext">The HTTP context whose authentication handler should be challenged.</param>
    /// <returns><see langword="true"/> when a configured host scheme was challenged; otherwise <see langword="false"/>.</returns>
    public static async Task<bool> ChallengeAsync(HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return false;
        }

        var schemes = httpContext.RequestServices?.GetService<IAuthenticationSchemeProvider>();
        if (schemes is null)
        {
            return false;
        }

        var selectedScheme = GetTransportAuthenticationScheme(httpContext);
        var challengeScheme = string.IsNullOrWhiteSpace(selectedScheme)
            ? await schemes.GetDefaultChallengeSchemeAsync()
            : await schemes.GetSchemeAsync(selectedScheme);
        if (challengeScheme is null)
        {
            return false;
        }

        await httpContext.ChallengeAsync(challengeScheme.Name);
        return true;
    }

    internal static ClaimsPrincipal? GetCurrentPrincipal(HttpContext? httpContext)
        => GetCurrentSnapshot(httpContext)?.Principal;

    internal static ClaimsIdentity? GetCurrentIdentity(HttpContext? httpContext)
        => GetCurrentSnapshot(httpContext)?.Identity;

    internal static WorkableAuthenticationSnapshot? GetCurrentSnapshot(HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return null;
        }

        if (httpContext.Items.TryGetValue(AuthenticationCacheKey, out var cached) &&
            cached is AuthenticationCacheEntry cacheEntry)
        {
            return cacheEntry.Snapshot;
        }

        if (!string.IsNullOrWhiteSpace(GetTransportAuthenticationScheme(httpContext)))
        {
            return null;
        }

        var snapshot = CreateSnapshot(httpContext, httpContext.User, authenticationScheme: null);
        httpContext.Items[AuthenticationCacheKey] = new AuthenticationCacheEntry(snapshot);
        return snapshot;
    }

    internal static bool IsCurrentIdentity(
        HttpContext? httpContext,
        ClaimsIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return ReferenceEquals(ActiveSnapshot.Value?.Identity, identity) ||
            ReferenceEquals(GetCurrentSnapshot(httpContext)?.Identity, identity);
    }

    internal static WorkableAuthenticationSnapshot? GetActiveSnapshot()
        => ActiveSnapshot.Value;

    internal static IDisposable UseSnapshot(WorkableAuthenticationSnapshot? snapshot)
    {
        var prior = ActiveSnapshot.Value;
        ActiveSnapshot.Value = snapshot;
        return new ActiveSnapshotScope(prior);
    }

    internal static async Task PrepareAuthorizationSnapshotAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var snapshot = GetCurrentSnapshot(httpContext);
        if (snapshot is null)
        {
            return;
        }

        var requestServices = httpContext.RequestServices;
        if (requestServices is null)
        {
            return;
        }

        var actors = requestServices.GetRequiredService<IWorkActorFactory>();
        snapshot.Actor ??= actors.Create(httpContext);
        var claimsProvider = requestServices
            .GetServices<IWorkAuthorizationGroupContextProvider>()
            .OfType<RequestScopedHttpContextClaimsWorkAuthorizationGroupProvider>()
            .SingleOrDefault();
        if (claimsProvider is not null)
        {
            snapshot.ClaimsGroups = await claimsProvider.GetCurrentGroups(
                httpContext,
                snapshot.Actor,
                systemName: null,
                httpContext.RequestAborted);
        }
    }

    private static async Task<WorkableAuthenticationSnapshot?> GetAuthenticatedSnapshotAsync(
        HttpContext? httpContext)
    {
        if (httpContext is null)
        {
            return null;
        }

        if (httpContext.Items.TryGetValue(AuthenticationCacheKey, out var cached) &&
            cached is AuthenticationCacheEntry cacheEntry)
        {
            return cacheEntry.Snapshot;
        }

        var authenticationScheme = GetTransportAuthenticationScheme(httpContext);
        if (string.IsNullOrWhiteSpace(authenticationScheme))
        {
            return GetCurrentSnapshot(httpContext);
        }

        var result = await httpContext.AuthenticateAsync(authenticationScheme);
        var snapshot = result.Succeeded && result.Principal is not null
            ? CreateSnapshot(
                httpContext,
                result.Principal,
                authenticationScheme,
                result.Properties?.ExpiresUtc)
            : null;
        httpContext.Items[AuthenticationCacheKey] = new AuthenticationCacheEntry(snapshot);
        return snapshot;
    }

    private static WorkableAuthenticationSnapshot? CreateSnapshot(
        HttpContext httpContext,
        ClaimsPrincipal principal,
        string? authenticationScheme,
        DateTimeOffset? authenticationExpiresUtc = null)
    {
        var selector = httpContext.RequestServices?
                .GetService<IWorkClaimsIdentitySelector>() ??
            new PrimaryWorkClaimsIdentitySelector();
        return selector.SelectIdentity(principal) is { IsAuthenticated: true } identity
            ? new WorkableAuthenticationSnapshot(
                principal,
                identity,
                authenticationScheme,
                authenticationExpiresUtc)
            : null;
    }

    private static string? GetTransportAuthenticationScheme(HttpContext httpContext)
        => httpContext.RequestServices?
            .GetService<IOptions<WorkableAspNetCoreAuthorizationOptions>>()
            ?.Value
            .TransportAuthenticationScheme;

    private sealed class ActiveSnapshotScope(WorkableAuthenticationSnapshot? prior) : IDisposable
    {
        private bool disposed;

        public void Dispose()
        {
            if (this.disposed)
            {
                return;
            }

            ActiveSnapshot.Value = prior;
            this.disposed = true;
        }
    }
}
