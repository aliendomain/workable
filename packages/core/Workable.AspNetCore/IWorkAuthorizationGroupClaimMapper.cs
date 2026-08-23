using System.Security.Claims;

namespace Workable;

/// <summary>
/// Maps claims from an authenticated identity to Workable authorization groups.
/// </summary>
public interface IWorkAuthorizationGroupClaimMapper
{
    /// <summary>
    /// Gets the mapper order. Host mappers run before integration defaults when they use a lower value.
    /// </summary>
    int Order => 0;

    /// <summary>
    /// Attempts to handle a claim for a specific identity.
    /// </summary>
    /// <param name="identity">The identity that owns the claim.</param>
    /// <param name="claim">The claim to inspect.</param>
    /// <param name="groups">
    /// The mapped Workable groups. An empty result indicates that the claim was intentionally handled without
    /// contributing groups.
    /// </param>
    /// <returns><see langword="true"/> when this mapper owns the claim; otherwise <see langword="false"/>.</returns>
    bool TryMap(
        ClaimsIdentity identity,
        Claim claim,
        out IReadOnlyList<string> groups);
}
