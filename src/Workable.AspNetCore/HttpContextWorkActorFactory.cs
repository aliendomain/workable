using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Workable;

public sealed class HttpContextWorkActorFactory(
    IOptions<WorkableAspNetCoreAuthorizationOptions> options) : IWorkActorFactory
{
    public WorkActor Create(HttpContext? httpContext)
        => this.Create(httpContext?.User);

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
