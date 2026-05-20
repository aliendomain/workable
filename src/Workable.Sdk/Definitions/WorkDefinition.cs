namespace Workable;

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

    public WorkDefinitionId Id { get; init; }

    public string Name { get; init; }

    public string Category { get; init; }

    public string? Description { get; init; }

    public WorkSchema InputSchema { get; init; }

    public WorkSchema OutputSchema { get; init; }

    public WorkerOptions DefaultOptions { get; init; }

    public WorkConfiguration Configuration { get; init; }

    public WorkDefinitionMetadata? Metadata { get; init; }

    public WorkDefinitionAuthorization Authorization { get; init; }

    public long Revision { get; init; }

    public WorkDefinitionVersion Version => new(this.Id, this.Revision);

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
