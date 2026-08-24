using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Workable;

internal static class WorkableEntraIdentity
{
    public const string MicrosoftObjectIdClaimType =
        "http://schemas.microsoft.com/identity/claims/objectidentifier";

    public static bool IsMatch(
        ClaimsIdentity identity,
        WorkableEntraAuthorizationRegistration registration,
        IHttpContextAccessor httpContextAccessor)
    {
        var options = registration.Options;
        if (options.IdentityPredicate is not null)
        {
            return options.IdentityPredicate(identity);
        }

        return identity.Claims.Any(claim =>
                string.Equals(claim.Type, "oid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(claim.Type, MicrosoftObjectIdClaimType, StringComparison.OrdinalIgnoreCase)) ||
            options.AuthenticationScheme is not null &&
            WorkableAspNetCoreAuthentication.IsCurrentIdentity(
                httpContextAccessor.HttpContext,
                identity);
    }
}
