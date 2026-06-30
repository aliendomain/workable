namespace Workable;

/// <summary>
/// Describes one registered workflow definition in a Workable system.
/// </summary>
public sealed record WorkflowDefinition
{
    private WorkflowDefinition(
        WorkflowDefinitionId id,
        string name,
        string category,
        string? description,
        WorkSchema inputSchema,
        WorkSchema outputSchema,
        WorkflowCoordinationConfiguration coordination,
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
        this.Coordination = coordination;
        this.Metadata = metadata;
        this.Authorization = authorization ?? WorkDefinitionAuthorization.None;
        this.Revision = revision;
    }

    /// <summary>
    /// Gets the stable identifier for the definition.
    /// </summary>
    public WorkflowDefinitionId Id { get; init; }

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
    /// Gets optional descriptive metadata for catalog and tool-oriented experiences.
    /// </summary>
    public WorkDefinitionMetadata? Metadata { get; init; }

    /// <summary>
    /// Gets the workflow coordination settings for the definition.
    /// </summary>
    public WorkflowCoordinationConfiguration Coordination { get; init; } = WorkflowCoordinationConfiguration.Default;

    /// <summary>
    /// Gets the workflow-level authorization metadata for the definition.
    /// </summary>
    public WorkDefinitionAuthorization Authorization { get; init; }

    /// <summary>
    /// Gets the revision number for the definition snapshot.
    /// </summary>
    public long Revision { get; init; }

    /// <summary>
    /// Gets the version composed from the definition id and revision.
    /// </summary>
    public WorkflowDefinitionVersion Version => new(this.Id, this.Revision);

    /// <summary>
    /// Creates a definition with the supplied metadata and authorization.
    /// </summary>
    /// <param name="name">The case-insensitive catalog name used to start and query the definition.</param>
    /// <param name="description">An optional human-readable description of the definition.</param>
    /// <param name="category">An optional catalog category. When omitted, Workable uses <see cref="WorkDefinitionMetadataDefaults.Category"/>.</param>
    /// <param name="id">An optional explicit definition id. When omitted, Workable generates one.</param>
    /// <param name="inputSchema">An optional input schema. When omitted, Workable uses <see cref="WorkSchema.None"/>.</param>
    /// <param name="outputSchema">An optional output schema. When omitted, Workable uses <see cref="WorkSchema.None"/>.</param>
    /// <param name="coordination">Optional workflow coordination metadata.</param>
    /// <param name="metadata">Optional descriptive metadata for catalog and tool-oriented experiences.</param>
    /// <param name="authorization">Optional workflow-level authorization metadata.</param>
    /// <returns>A validated workflow definition instance.</returns>
    public static WorkflowDefinition Create(
        string name,
        string? description = null,
        string? category = null,
        WorkflowDefinitionId? id = null,
        WorkSchema? inputSchema = null,
        WorkSchema? outputSchema = null,
        WorkflowCoordinationConfiguration? coordination = null,
        WorkDefinitionMetadata? metadata = null,
        WorkDefinitionAuthorization? authorization = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        return new(
            id ?? WorkflowDefinitionId.New(),
            name,
            string.IsNullOrWhiteSpace(category) ? WorkDefinitionMetadataDefaults.Category : category,
            description,
            inputSchema ?? WorkSchema.None,
            outputSchema ?? WorkSchema.None,
            coordination ?? WorkflowCoordinationConfiguration.Default,
            metadata,
            authorization);
    }
}
