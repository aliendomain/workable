using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Workable;

internal sealed class WorkableEntraAuthorizationGroupClaimMapper(
    WorkableEntraAuthorizationRegistration registration,
    IHttpContextAccessor httpContextAccessor,
    IOptions<WorkableAspNetCoreAuthorizationOptions> authorizationOptions)
    : IWorkAuthorizationGroupClaimMapper
{
    private const string MicrosoftScopeClaimType =
        "http://schemas.microsoft.com/identity/claims/scope";

    public int Order => 1000;

    public bool TryMap(
        ClaimsIdentity identity,
        Claim claim,
        out IReadOnlyList<string> groups)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(claim);
        if (!WorkableEntraIdentity.IsMatch(identity, registration, httpContextAccessor))
        {
            groups = [];
            return false;
        }

        if (IsClaimType(
                claim.Type,
                WorkableEntraAuthorizationDefaults.ScopeClaimType,
                MicrosoftScopeClaimType))
        {
            if (registration.Options.MapScopesToWorkableGroups)
            {
                groups = SplitEntraClaim(claim, [' ']);
                return true;
            }

            groups = [];
            return true;
        }

        if (IsClaimType(claim.Type, WorkableEntraAuthorizationDefaults.GroupsClaimType))
        {
            if (registration.Options.MapGroupsToWorkableGroups)
            {
                groups = SplitEntraClaim(claim);
                return true;
            }

            groups = [];
            return true;
        }

        if (IsClaimType(
                claim.Type,
                WorkableEntraAuthorizationDefaults.RolesClaimType,
                WorkableEntraAuthorizationDefaults.RoleClaimType,
                ClaimTypes.Role))
        {
            if (registration.Options.MapAppRolesToWorkableGroups)
            {
                groups = SplitEntraClaim(claim);
                return true;
            }

            groups = [];
            return true;
        }

        if (IsClaimType(claim.Type, identity.RoleClaimType))
        {
            if (registration.Options.MapAppRolesToWorkableGroups)
            {
                groups = SplitHostRoleClaim(claim);
                return true;
            }

            groups = [];
            return true;
        }

        groups = [];
        return false;
    }

    private IReadOnlyList<string> SplitEntraClaim(
        Claim claim,
        IReadOnlyList<char>? definedSeparators = null)
    {
        var options = authorizationOptions.Value;
        var separators = options.GroupClaimValueSeparatorsByClaimType.TryGetValue(
            claim.Type,
            out var configured)
            ? configured.ToList()
            : definedSeparators?.ToList() ?? [];

        return Split(claim.Value, separators);
    }

    private IReadOnlyList<string> SplitHostRoleClaim(Claim claim)
    {
        var options = authorizationOptions.Value;
        var separators = options.GroupClaimValueSeparatorsByClaimType.TryGetValue(
            claim.Type,
            out var configured)
            ? configured
            : options.GroupClaimValueSeparators;

        return Split(claim.Value, separators);
    }

    private static IReadOnlyList<string> Split(
        string value,
        IReadOnlyList<char> separators)
        => separators.Count > 0 && value.IndexOfAny([.. separators]) >= 0
            ? value.Split(
                [.. separators],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : [value];

    private static bool IsClaimType(string candidate, params string[] claimTypes)
        => claimTypes.Any(claimType =>
            !string.IsNullOrWhiteSpace(claimType) &&
            string.Equals(candidate, claimType, StringComparison.OrdinalIgnoreCase));
}
