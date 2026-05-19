using System.Text.Json;

namespace Workable;
public sealed record WorkInput(
    string? Json,
    string? ClrType = null,
    string ContentType = "application/json",
    WorkSubjectId? SubjectId = null,
    WorkConcurrencyKey? ConcurrencyKey = null,
    IReadOnlySet<WorkIdentifier>? Identifiers = null) : WorkData(Json, ClrType, ContentType)
{
    public static WorkInput Empty { get; } = new((string?)null);

    public static WorkInput FromJson(
        string json,
        Type? clrType = null,
        WorkSubjectId? subjectId = null,
        WorkConcurrencyKey? concurrencyKey = null,
        IEnumerable<WorkIdentifier>? identifiers = null)
        => new(
            json,
            clrType?.AssemblyQualifiedName,
            SubjectId: subjectId,
            ConcurrencyKey: concurrencyKey,
            Identifiers: NormalizeIdentifiers(identifiers));

    public static WorkInput FromValue<T>(
        T value,
        JsonSerializerOptions? options = null,
        WorkSubjectId? subjectId = null,
        WorkConcurrencyKey? concurrencyKey = null,
        IEnumerable<WorkIdentifier>? identifiers = null)
        => new(
            JsonSerializer.Serialize(value, options ?? JsonOptions),
            typeof(T).AssemblyQualifiedName,
            SubjectId: subjectId,
            ConcurrencyKey: concurrencyKey,
            Identifiers: NormalizeIdentifiers(identifiers));

    public WorkInput WithSubject(WorkSubjectId subjectId)
        => this with
        {
            SubjectId = subjectId,
        };

    public WorkInput WithConcurrencyKey(WorkConcurrencyKey concurrencyKey)
        => this with
        {
            ConcurrencyKey = concurrencyKey,
        };

    public WorkInput WithIdentifier(WorkIdentifier identifier)
        => this with
        {
            Identifiers = AddIdentifier(this.Identifiers, identifier),
        };

    public WorkInput WithIdentifiers(IEnumerable<WorkIdentifier> identifiers)
    {
        ArgumentNullException.ThrowIfNull(identifiers);

        return this with
        {
            Identifiers = NormalizeIdentifiers((this.Identifiers ?? Enumerable.Empty<WorkIdentifier>()).Concat(identifiers)),
        };
    }

    private static HashSet<WorkIdentifier> AddIdentifier(
        IReadOnlySet<WorkIdentifier>? identifiers,
        WorkIdentifier identifier)
    {
        var normalized = identifiers?.ToHashSet() ?? [];
        normalized.Add(identifier);
        return normalized;
    }

    private static HashSet<WorkIdentifier>? NormalizeIdentifiers(IEnumerable<WorkIdentifier>? identifiers)
    {
        if (identifiers is null)
        {
            return null;
        }

        var normalized = identifiers.ToHashSet();
        return normalized.Count == 0 ? null : normalized;
    }
}
