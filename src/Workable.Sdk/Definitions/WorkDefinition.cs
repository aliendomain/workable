namespace Workable;
public sealed record WorkDefinition(
    WorkDefinitionId Id,
    string Name,
    string Category,
    string? Description,
    WorkSchema InputSchema,
    WorkSchema OutputSchema,
    WorkerOptions DefaultOptions,
    WorkConfiguration Configuration,
    WorkDefinitionMetadata? Metadata = null)
{
    public static WorkDefinition Create(
        string name,
        string? description = null,
        string? category = null,
        WorkDefinitionId? id = null,
        WorkSchema? inputSchema = null,
        WorkSchema? outputSchema = null,
        WorkerOptions? defaultOptions = null,
        WorkDefinitionMetadata? metadata = null,
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
            metadata);
    }
}
