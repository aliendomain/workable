using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Workable;

/// <summary>
/// Creates <see cref="WorkActor"/> values from ASP.NET Core user context.
/// </summary>
public sealed class HttpContextWorkActorFactory(
    IOptions<WorkableAspNetCoreAuthorizationOptions> options) : IWorkActorFactory
{
    /// <summary>
    /// Creates a <see cref="WorkActor"/> from an HTTP context.
    /// </summary>
    /// <param name="httpContext">The HTTP context to inspect.</param>
    /// <returns>The resolved actor, or <see cref="WorkActor.Unknown"/> when the user is not authenticated.</returns>
    public WorkActor Create(HttpContext? httpContext)
        => this.Create(httpContext?.User);

    /// <summary>
    /// Creates a <see cref="WorkActor"/> from a claims principal.
    /// </summary>
    /// <param name="user">The claims principal to inspect.</param>
    /// <returns>The resolved actor, or <see cref="WorkActor.Unknown"/> when the user is not authenticated.</returns>
    public WorkActor Create(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true)
        {
            return WorkActor.Unknown;
        }

        return new WorkActor(
            Id: FindFirst(user, options.Value.ActorIdClaimTypes),
            Name: user.Identity?.Name ?? FindFirst(user, options.Value.ActorNameClaimTypes),
            Email: FindFirst(user, options.Value.ActorEmailClaimTypes));
    }

    private static string? FindFirst(
        ClaimsPrincipal user,
        IEnumerable<string> claimTypes)
        => claimTypes
            .Select(user.FindFirst)
            .FirstOrDefault(claim => !string.IsNullOrWhiteSpace(claim?.Value))
            ?.Value;
}
