using System.Collections.Frozen;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Workable;

/// <summary>
/// Captures caller authorization state so it can be replayed after the original request is gone.
/// </summary>
/// <param name="Actor">The caller identity represented by the snapshot.</param>
/// <param name="Groups">The normalized caller groups represented by the snapshot.</param>
/// <param name="ReadFingerprint">A stable, system-scoped fingerprint of the caller's projection authorization.</param>
public sealed record WorkAuthorizationSnapshot(
    WorkActor Actor,
    IReadOnlySet<string> Groups,
    string ReadFingerprint)
{
    /// <summary>
    /// Gets whether the represented caller was authenticated when this snapshot was created.
    /// </summary>
    public bool IsAuthenticated { get; init; }

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
        WorkAuthorizationScope? scope,
        IEnumerable<WorkflowDefinitionId>? readableWorkflowDefinitionIds = null,
        bool canViewDiagnostics = false,
        bool isAuthenticated = false)
    {
        ArgumentNullException.ThrowIfNull(actor);

        var normalizedGroups = WorkAuthorizationGroups.Normalize(groups);

        return new WorkAuthorizationSnapshot(
            actor,
            normalizedGroups,
            CreateReadFingerprint(
                scope,
                normalizedGroups,
                readableDefinitionIds,
                readableWorkflowDefinitionIds,
                canViewDiagnostics,
                isAuthenticated))
        {
            Scope = scope,
            IsAuthenticated = isAuthenticated,
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
        => CreateForSystem(
            systemName,
            actor,
            groups,
            readableDefinitionIds,
            readableWorkflowDefinitionIds: null,
            canViewDiagnostics: false,
            isAuthenticated: false);

    /// <summary>
    /// Creates a canonical authorization snapshot scoped to one logical Workable system and its complete projection authorization.
    /// </summary>
    /// <param name="systemName">The logical system name, or <see langword="null"/> for the default unnamed system.</param>
    /// <param name="actor">The caller identity to snapshot.</param>
    /// <param name="groups">The raw caller groups to normalize.</param>
    /// <param name="readableDefinitionIds">The readable work definition ids included in the projection scope.</param>
    /// <param name="readableWorkflowDefinitionIds">The readable workflow definition ids included in the projection scope.</param>
    /// <param name="canViewDiagnostics">Whether the caller may view diagnostics.</param>
    /// <param name="isAuthenticated">Whether the represented caller is authenticated.</param>
    /// <returns>The created system-scoped authorization snapshot.</returns>
    public static WorkAuthorizationSnapshot CreateForSystem(
        string? systemName,
        WorkActor actor,
        IEnumerable<string>? groups,
        IEnumerable<WorkDefinitionId>? readableDefinitionIds,
        IEnumerable<WorkflowDefinitionId>? readableWorkflowDefinitionIds = null,
        bool canViewDiagnostics = false,
        bool isAuthenticated = false)
        => CreateCore(
            actor,
            groups,
            readableDefinitionIds,
            new WorkAuthorizationScope(systemName),
            readableWorkflowDefinitionIds,
            canViewDiagnostics,
            isAuthenticated);

    private static string CreateReadFingerprint(
        WorkAuthorizationScope? scope,
        IReadOnlySet<string> groups,
        IEnumerable<WorkDefinitionId>? readableDefinitionIds,
        IEnumerable<WorkflowDefinitionId>? readableWorkflowDefinitionIds,
        bool canViewDiagnostics,
        bool isAuthenticated)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendString(hash, "workable.authorization.projection.v1");
        AppendInt32(hash, scope is null ? 0 : scope.SystemName is null ? 1 : 2);
        if (scope?.SystemName is { } systemName)
        {
            AppendString(hash, systemName.ToUpperInvariant());
        }

        AppendStrings(hash, NormalizeStrings(groups));
        AppendStrings(hash, NormalizeDefinitionIds(readableDefinitionIds));
        AppendStrings(hash, NormalizeWorkflowDefinitionIds(readableWorkflowDefinitionIds));
        AppendInt32(hash, canViewDiagnostics ? 1 : 0);
        AppendInt32(hash, isAuthenticated ? 1 : 0);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static IReadOnlyList<string> NormalizeStrings(IEnumerable<string>? values)
        => [.. values?
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(static value => value.ToUpperInvariant())
            ?? []];

    private static IReadOnlyList<string> NormalizeDefinitionIds(IEnumerable<WorkDefinitionId>? definitionIds)
        => [.. definitionIds?
            .Distinct()
            .OrderBy(static definitionId => definitionId.Value)
            .Select(static definitionId => definitionId.Value.ToString("N"))
            ?? []];

    private static IReadOnlyList<string> NormalizeWorkflowDefinitionIds(IEnumerable<WorkflowDefinitionId>? definitionIds)
        => [.. definitionIds?
            .Distinct()
            .OrderBy(static definitionId => definitionId.Value)
            .Select(static definitionId => definitionId.Value.ToString("N"))
            ?? []];

    private static void AppendStrings(IncrementalHash hash, IReadOnlyList<string> values)
    {
        AppendInt32(hash, values.Count);
        foreach (var value in values)
        {
            AppendString(hash, value);
        }
    }

    private static void AppendString(IncrementalHash hash, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        AppendInt32(hash, bytes.Length);
        hash.AppendData(bytes);
    }

    private static void AppendInt32(IncrementalHash hash, int value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        hash.AppendData(bytes);
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
