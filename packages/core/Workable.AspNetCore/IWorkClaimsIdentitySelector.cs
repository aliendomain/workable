using System.Security.Claims;

namespace Workable;

/// <summary>
/// Selects the single authenticated identity Workable uses from a host-produced claims principal.
/// </summary>
public interface IWorkClaimsIdentitySelector
{
    /// <summary>
    /// Selects the authenticated identity that supplies the Workable actor and authorization groups.
    /// </summary>
    /// <param name="principal">The principal produced by the host authentication pipeline.</param>
    /// <returns>The selected authenticated identity, or <see langword="null"/> when none is available.</returns>
    ClaimsIdentity? SelectIdentity(ClaimsPrincipal principal);
}

internal sealed class PrimaryWorkClaimsIdentitySelector : IWorkClaimsIdentitySelector
{
    public ClaimsIdentity? SelectIdentity(ClaimsPrincipal principal)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return principal.Identity is ClaimsIdentity { IsAuthenticated: true } identity
            ? identity
            : null;
    }
}
