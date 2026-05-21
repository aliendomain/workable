using System.Security.Cryptography;
using System.Text;

namespace Workable;

public sealed record WorkAuthorizationSnapshot(
    WorkActor Actor,
    IReadOnlySet<string> Groups,
    string ReadFingerprint)
{
    public static WorkAuthorizationSnapshot Create(
        WorkActor actor,
        IEnumerable<string>? groups,
        IEnumerable<WorkDefinitionId>? readableDefinitionIds)
    {
        ArgumentNullException.ThrowIfNull(actor);

        return new(
            actor,
            groups?
                .Where(static group => !string.IsNullOrWhiteSpace(group))
                .Select(static group => group.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
                ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase),
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
