using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Workable;

internal sealed class WorkableEntraActorClaimsMapper(
    WorkableEntraAuthorizationRegistration registration,
    IHttpContextAccessor httpContextAccessor,
    IOptions<WorkableAspNetCoreAuthorizationOptions> authorizationOptions)
    : IWorkActorClaimsMapper
{
    public int Order => 1000;

    public bool TryCreate(ClaimsIdentity identity, out WorkActor actor)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!WorkableEntraIdentity.IsMatch(identity, registration, httpContextAccessor))
        {
            actor = WorkActor.Unknown;
            return false;
        }

        var options = authorizationOptions.Value;
        actor = new WorkActor(
            Id: FindFirst(identity, ["oid", WorkableEntraIdentity.MicrosoftObjectIdClaimType]) ??
                FindFirst(identity, options.ActorIdClaimTypes),
            Name: identity.Name ??
                FindFirst(identity, options.ActorNameClaimTypes) ??
                FindFirst(identity, ["name", "preferred_username"]),
            Email: FindFirst(identity, options.ActorEmailClaimTypes) ??
                FindFirst(identity, ["email", "preferred_username", "upn", ClaimTypes.Upn]));
        return true;
    }

    private static string? FindFirst(ClaimsIdentity identity, IEnumerable<string> claimTypes)
        => claimTypes
            .Select(identity.FindFirst)
            .FirstOrDefault(claim => !string.IsNullOrWhiteSpace(claim?.Value))
            ?.Value;
}
