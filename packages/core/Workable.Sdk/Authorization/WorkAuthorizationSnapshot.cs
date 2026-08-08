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
    /// Gets the logical Workable system whose authorization resolution produced this snapshot.
    /// </summary>
    /// <remarks>
    /// A <see langword="null"/> scope identifies a legacy or otherwise unscoped snapshot and is not eligible for reuse by a system.
    /// A non-null scope whose system name is <see langword="null"/> identifies the default unnamed system.
    /// </remarks>
    public WorkAuthorizationScope? Scope { get; init; }

    /// <summary>
    /// Creates a legacy unscoped authorization snapshot from raw caller groups and readable definition ids.
    /// </summary>
    /// <remarks>
    /// Unscoped snapshots are not reused during authorization. Use <see cref="CreateForSystem"/> for a trusted snapshot.
    /// </remarks>
    /// <param name="actor">The caller identity to snapshot.</param>
    /// <param name="groups">The raw caller groups to normalize.</param>
    /// <param name="readableDefinitionIds">The readable definition ids used to derive the read fingerprint.</param>
    /// <returns>The created authorization snapshot.</returns>
    [Obsolete("Use CreateForSystem so the snapshot can only be reused by its source system.")]
    public static WorkAuthorizationSnapshot Create(
        WorkActor actor,
        IEnumerable<string>? groups,
        IEnumerable<WorkDefinitionId>? readableDefinitionIds)
        => CreateCore(actor, groups, readableDefinitionIds, scope: null);

    private static WorkAuthorizationSnapshot CreateCore(
        WorkActor actor,
        IEnumerable<string>? groups,
        IEnumerable<WorkDefinitionId>? readableDefinitionIds,
        WorkAuthorizationScope? scope)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return new WorkAuthorizationSnapshot(
            actor,
            WorkAuthorizationGroups.Normalize(groups),
            CreateReadFingerprint(readableDefinitionIds))
        {
            Scope = scope,
        };
    }

    /// <summary>
    /// Creates a system-scoped authorization snapshot from raw caller groups and readable definition ids.
    /// </summary>
    /// <param name="systemName">The logical system name, or <see langword="null"/> for the default unnamed system.</param>
    /// <param name="actor">The caller identity to snapshot.</param>
    /// <param name="groups">The raw caller groups to normalize.</param>
    /// <param name="readableDefinitionIds">The readable definition ids used to derive the read fingerprint.</param>
    /// <returns>The created system-scoped authorization snapshot.</returns>
    public static WorkAuthorizationSnapshot CreateForSystem(
        string? systemName,
        WorkActor actor,
        IEnumerable<string>? groups,
        IEnumerable<WorkDefinitionId>? readableDefinitionIds)
        => CreateCore(
            actor,
            groups,
            readableDefinitionIds,
            new WorkAuthorizationScope(systemName));

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

/// <summary>
/// Identifies the logical Workable system that produced an authorization snapshot.
/// </summary>
/// <param name="SystemName">The configured system name, or <see langword="null"/> for the default unnamed system.</param>
public sealed record WorkAuthorizationScope(string? SystemName)
{
    /// <summary>
    /// Determines whether this scope identifies the supplied logical system.
    /// </summary>
    /// <param name="systemName">The configured system name, or <see langword="null"/> for the default unnamed system.</param>
    /// <returns><see langword="true"/> when the names identify the same system; otherwise <see langword="false"/>.</returns>
    public bool IsForSystem(string? systemName)
        => string.Equals(this.SystemName, systemName, StringComparison.OrdinalIgnoreCase);
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
