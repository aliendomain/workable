using System.Text.Json;

namespace Workable;
/// <summary>
/// Represents serialized worker input plus optional business keys and grouping metadata.
/// </summary>
/// <param name="Json">The serialized input payload, or <see langword="null"/> when the work has no input.</param>
/// <param name="ClrType">The optional CLR type name that produced the payload.</param>
/// <param name="ContentType">The content type describing the payload format.</param>
/// <param name="SubjectId">The optional primary business subject associated with the queued worker.</param>
/// <param name="ConcurrencyKey">The optional concurrency grouping key associated with the queued worker.</param>
/// <param name="Identifiers">Optional additional searchable identifiers associated with the queued worker.</param>
public sealed record WorkInput(
    string? Json,
    string? ClrType = null,
    string ContentType = "application/json",
    WorkSubjectId? SubjectId = null,
    WorkConcurrencyKey? ConcurrencyKey = null,
    IReadOnlySet<WorkIdentifier>? Identifiers = null) : WorkData(Json, ClrType, ContentType)
{
    /// <summary>
    /// Gets a reusable empty input instance for work that does not require a payload.
    /// </summary>
    public static WorkInput Empty { get; } = new((string?)null);

    /// <summary>
    /// Creates input from an existing JSON payload.
    /// </summary>
    /// <param name="json">The serialized input payload.</param>
    /// <param name="clrType">The optional CLR type associated with the payload.</param>
    /// <param name="subjectId">The optional primary business subject associated with the queued worker.</param>
    /// <param name="concurrencyKey">The optional concurrency grouping key associated with the queued worker.</param>
    /// <param name="identifiers">Optional additional searchable identifiers associated with the queued worker.</param>
    /// <returns>A work input instance containing the supplied payload and metadata.</returns>
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

    /// <summary>
    /// Creates input by serializing a typed value.
    /// </summary>
    /// <typeparam name="T">The logical input type to serialize.</typeparam>
    /// <param name="value">The typed input value to serialize.</param>
    /// <param name="options">Optional JSON serializer options. When omitted, Workable uses its default JSON options.</param>
    /// <param name="subjectId">The optional primary business subject associated with the queued worker.</param>
    /// <param name="concurrencyKey">The optional concurrency grouping key associated with the queued worker.</param>
    /// <param name="identifiers">Optional additional searchable identifiers associated with the queued worker.</param>
    /// <returns>A work input instance containing the serialized payload and metadata.</returns>
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

    /// <summary>
    /// Returns a copy of the input with the supplied subject id.
    /// </summary>
    /// <param name="subjectId">The subject id to associate with the queued worker.</param>
    /// <returns>A copy of the input with the supplied subject id.</returns>
    public WorkInput WithSubject(WorkSubjectId subjectId)
        => this with
        {
            SubjectId = subjectId,
        };

    /// <summary>
    /// Returns a copy of the input with the supplied concurrency key.
    /// </summary>
    /// <param name="concurrencyKey">The concurrency key to associate with the queued worker.</param>
    /// <returns>A copy of the input with the supplied concurrency key.</returns>
    public WorkInput WithConcurrencyKey(WorkConcurrencyKey concurrencyKey)
        => this with
        {
            ConcurrencyKey = concurrencyKey,
        };

    /// <summary>
    /// Returns a copy of the input with one additional identifier.
    /// </summary>
    /// <param name="identifier">The identifier to add.</param>
    /// <returns>A copy of the input with the additional identifier included.</returns>
    public WorkInput WithIdentifier(WorkIdentifier identifier)
        => this with
        {
            Identifiers = AddIdentifier(this.Identifiers, identifier),
        };

    /// <summary>
    /// Returns a copy of the input with additional identifiers appended and normalized.
    /// </summary>
    /// <param name="identifiers">The identifiers to add.</param>
    /// <returns>A copy of the input with the merged identifier set.</returns>
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
