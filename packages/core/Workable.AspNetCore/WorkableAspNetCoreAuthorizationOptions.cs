using System.Security.Claims;

namespace Workable;

/// <summary>
/// Configures how Workable derives actors and groups from ASP.NET Core authentication state.
/// </summary>
public sealed class WorkableAspNetCoreAuthorizationOptions
{
    /// <summary>
    /// Gets or sets the existing host authentication scheme Workable should authenticate explicitly for its
    /// transport-level principal. Workable does not register or configure the selected scheme.
    /// </summary>
    public string? TransportAuthenticationScheme { get; set; }

    /// <summary>
    /// Gets or sets the claim types Workable inspects for actor identifiers.
    /// </summary>
    public IReadOnlyList<string> ActorIdClaimTypes { get; set; } =
        [ClaimTypes.NameIdentifier, "sub"];

    /// <summary>
    /// Gets or sets the claim types Workable inspects for actor display names.
    /// </summary>
    public IReadOnlyList<string> ActorNameClaimTypes { get; set; } =
        [ClaimTypes.Name, "name"];

    /// <summary>
    /// Gets or sets the claim types Workable inspects for actor email addresses.
    /// </summary>
    public IReadOnlyList<string> ActorEmailClaimTypes { get; set; } =
        [ClaimTypes.Email, "email"];

    /// <summary>
    /// Gets or sets the claim types Workable inspects for authorization groups.
    /// </summary>
    public IReadOnlyList<string> GroupClaimTypes { get; set; } =
        ["groups", "roles", "role", ClaimTypes.Role];

    /// <summary>
    /// Gets or sets the separators used when a group claim contains multiple values in one string.
    /// </summary>
    public IReadOnlyList<char> GroupClaimValueSeparators { get; set; } =
        [','];

    /// <summary>
    /// Gets claim-type-specific separators that override <see cref="GroupClaimValueSeparators"/>.
    /// </summary>
    public IDictionary<string, IReadOnlyList<char>> GroupClaimValueSeparatorsByClaimType { get; } =
        new Dictionary<string, IReadOnlyList<char>>(StringComparer.OrdinalIgnoreCase);
}
