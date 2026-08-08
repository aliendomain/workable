using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;

namespace Workable;

/// <summary>
/// Captures caller authorization state so it can be replayed after the original request is gone.
/// </summary>
/// <param name="Actor">The caller identity represented by the snapshot.</param>
/// <param name="Groups">The normalized caller groups represented by the snapshot.</param>
/// <param name="ReadFingerprint">A stable fingerprint of the caller's readable definitions.</param>
public sealed record WorkAuthorizationSnapshot(
    WorkActor Actor,
    IReadOnlySet<string> Groups,
    string ReadFingerprint)
{
    /// <summary>
    /// Creates an authorization snapshot from raw caller groups and readable definition ids.
    /// </summary>
    /// <param name="actor">The caller identity to snapshot.</param>
    /// <param name="groups">The raw caller groups to normalize.</param>
    /// <param name="readableDefinitionIds">The readable definition ids used to derive the read fingerprint.</param>
    /// <returns>The created authorization snapshot.</returns>
    public static WorkAuthorizationSnapshot Create(
        WorkActor actor,
        IEnumerable<string>? groups,
        IEnumerable<WorkDefinitionId>? readableDefinitionIds)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return new(
            actor,
            WorkAuthorizationGroups.Normalize(groups),
            CreateReadFingerprint(readableDefinitionIds));
    }

    private static string CreateReadFingerprint(IEnumerable<WorkDefinitionId>? readableDefinitionIds)
    {
        var normalized = string.Join(
            "|",
            readableDefinitionIds?
                .Distinct()
                .OrderBy(static definitionId => definitionId.Value)
                .Select(static definitionId => definitionId.Value.ToString("N"))
            ?? Array.Empty<string>());
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}

internal static class WorkAuthorizationGroups
{
    private static readonly IReadOnlySet<string> Empty =
        Array.Empty<string>().ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    internal static IReadOnlySet<string> Normalize(IEnumerable<string>? groups)
    {
        if (groups is null)
        {
            return Empty;
        }

        if (groups is FrozenSet<string> frozen &&
            frozen.Comparer.Equals(StringComparer.OrdinalIgnoreCase) &&
            frozen.All(static group => !string.IsNullOrWhiteSpace(group) && group == group.Trim()))
        {
            return frozen;
        }

        return groups
            .Where(static group => !string.IsNullOrWhiteSpace(group))
            .Select(static group => group.Trim())
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    }
}
