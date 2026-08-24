namespace Workable;

/// <summary>
/// Describes the discoverable authoring metadata for one work definition without exposing runtime or retained-work state.
/// </summary>
/// <param name="Name">The case-insensitive catalog name used to invoke the definition.</param>
/// <param name="Description">The optional human-readable description.</param>
/// <param name="Category">The catalog category.</param>
/// <param name="InputSchema">The declared input schema.</param>
/// <param name="OutputSchema">The declared output schema.</param>
/// <param name="Metadata">The optional tool-oriented definition metadata.</param>
public sealed record WorkDefinitionDescriptor(
    string Name,
    string? Description,
    string Category,
    WorkSchema InputSchema,
    WorkSchema OutputSchema,
    WorkDefinitionMetadata? Metadata)
{
    internal static WorkDefinitionDescriptor FromDefinition(WorkDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return FromSnapshot(definition.SnapshotMetadata());
    }

    internal static WorkDefinitionDescriptor FromSnapshot(WorkDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);

        return new(
            definition.Name,
            definition.Description,
            definition.Category,
            definition.InputSchema,
            definition.OutputSchema,
            definition.Metadata);
    }
}
