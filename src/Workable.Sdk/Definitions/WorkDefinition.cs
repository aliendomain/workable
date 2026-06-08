namespace Workable;

/// <summary>
/// Describes one registered unit of work in a Workable system.
/// </summary>
public sealed record WorkDefinition
{
    private WorkDefinition(
        WorkDefinitionId id,
        string name,
        string category,
        string? description,
        WorkSchema inputSchema,
        WorkSchema outputSchema,
        WorkerOptions defaultOptions,
        WorkConfiguration configuration,
        WorkDefinitionMetadata? metadata = null,
        WorkDefinitionAuthorization? authorization = null,
        long revision = 0)
    {
        this.Id = id;
        this.Name = name;
        this.Category = category;
        this.Description = description;
        this.InputSchema = inputSchema;
        this.OutputSchema = outputSchema;
        this.DefaultOptions = defaultOptions;
        this.Configuration = configuration;
        this.Metadata = metadata;
        this.Authorization = authorization ?? WorkDefinitionAuthorization.None;
        this.Revision = revision;
    }

    /// <summary>
    /// Gets the stable identifier for the definition.
    /// </summary>
    public WorkDefinitionId Id { get; init; }

    /// <summary>
    /// Gets the case-insensitive catalog name used to queue and query the definition.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// Gets the catalog category used to group the definition in discovery and UI surfaces.
    /// </summary>
    public string Category { get; init; }

    /// <summary>
    /// Gets the optional human-readable description of the definition.
    /// </summary>
    public string? Description { get; init; }

    /// <summary>
    /// Gets the declared input schema for the definition.
    /// </summary>
    public WorkSchema InputSchema { get; init; }

    /// <summary>
    /// Gets the declared output schema for the definition.
    /// </summary>
    public WorkSchema OutputSchema { get; init; }

    /// <summary>
    /// Gets the default worker options applied when callers do not provide queue-time overrides.
    /// </summary>
    public WorkerOptions DefaultOptions { get; init; }

    /// <summary>
    /// Gets the runtime behavior configuration for the definition.
    /// </summary>
    public WorkConfiguration Configuration { get; init; }

    /// <summary>
    /// Gets optional descriptive metadata for catalog and tool-oriented experiences.
    /// </summary>
    public WorkDefinitionMetadata? Metadata { get; init; }

    /// <summary>
    /// Gets the work-level authorization metadata for the definition.
    /// </summary>
    public WorkDefinitionAuthorization Authorization { get; init; }

    /// <summary>
    /// Gets the revision number for the definition snapshot.
    /// </summary>
    public long Revision { get; init; }

    /// <summary>
    /// Gets the version composed from the definition id and revision.
    /// </summary>
    public WorkDefinitionVersion Version => new(this.Id, this.Revision);

    /// <summary>
    /// Creates a definition with the supplied metadata, schemas, defaults, and configuration.
    /// </summary>
    /// <param name="name">The case-insensitive catalog name used to queue and query the definition.</param>
    /// <param name="description">An optional human-readable description of the definition.</param>
    /// <param name="category">An optional catalog category. When omitted, Workable uses <see cref="WorkDefinitionMetadataDefaults.Category"/>.</param>
    /// <param name="id">An optional explicit definition id. When omitted, Workable generates one.</param>
    /// <param name="inputSchema">An optional input schema. When omitted, Workable uses <see cref="WorkSchema.None"/>.</param>
    /// <param name="outputSchema">An optional output schema. When omitted, Workable uses <see cref="WorkSchema.None"/>.</param>
    /// <param name="defaultOptions">Optional default worker options for queue-time behavior.</param>
    /// <param name="metadata">Optional descriptive metadata for catalog and tool-oriented experiences.</param>
    /// <param name="authorization">Optional work-level authorization metadata.</param>
    /// <param name="configuration">Optional runtime behavior configuration for the definition.</param>
    /// <returns>A validated work definition instance.</returns>
    /// <exception cref="ArgumentException"><paramref name="name"/> is <see langword="null"/>, empty, or whitespace.</exception>
    public static WorkDefinition Create(
        string name,
        string? description = null,
        string? category = null,
        WorkDefinitionId? id = null,
        WorkSchema? inputSchema = null,
        WorkSchema? outputSchema = null,
        WorkerOptions? defaultOptions = null,
        WorkDefinitionMetadata? metadata = null,
        WorkDefinitionAuthorization? authorization = null,
        WorkConfiguration? configuration = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new(
            id ?? WorkDefinitionId.New(),
            name,
            string.IsNullOrWhiteSpace(category) ? WorkDefinitionMetadataDefaults.Category : category,
            description,
            inputSchema ?? WorkSchema.None,
            outputSchema ?? WorkSchema.None,
            defaultOptions ?? WorkerOptions.Default,
            WorkConfigurationValidator.ThrowIfInvalid(configuration ?? WorkConfiguration.Default),
            metadata,
            authorization);
    }
}
