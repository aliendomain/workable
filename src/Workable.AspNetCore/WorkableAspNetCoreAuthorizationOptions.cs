using System.Security.Claims;

namespace Workable;

public sealed class WorkableAspNetCoreAuthorizationOptions
{
    public string? TransportAuthenticationScheme { get; set; }

    public IReadOnlyList<string> ActorIdClaimTypes { get; set; } =
        [ClaimTypes.NameIdentifier, "sub"];

    public IReadOnlyList<string> ActorNameClaimTypes { get; set; } =
        [ClaimTypes.Name, "name"];

    public IReadOnlyList<string> ActorEmailClaimTypes { get; set; } =
        [ClaimTypes.Email, "email"];

    public IReadOnlyList<string> GroupClaimTypes { get; set; } =
        ["groups", "roles", "role", ClaimTypes.Role];

    public IReadOnlyList<char> GroupClaimValueSeparators { get; set; } =
        [','];
}
