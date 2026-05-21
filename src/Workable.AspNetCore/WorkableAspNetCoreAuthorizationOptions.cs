using System.Security.Claims;

namespace Workable;

public sealed class WorkableAspNetCoreAuthorizationOptions
{
    public IReadOnlyList<string> ActorIdClaimTypes { get; init; } =
        [ClaimTypes.NameIdentifier, "sub"];

    public IReadOnlyList<string> ActorNameClaimTypes { get; init; } =
        [ClaimTypes.Name, "name"];

    public IReadOnlyList<string> ActorEmailClaimTypes { get; init; } =
        [ClaimTypes.Email, "email"];

    public IReadOnlyList<string> GroupClaimTypes { get; init; } =
        ["groups", "roles", "role", ClaimTypes.Role];
}
