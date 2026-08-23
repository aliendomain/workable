using System.Diagnostics.CodeAnalysis;

namespace Workable;

internal sealed class ReadableWorkDiscoveryCatalog(IWorkCatalog catalog) : IWorkDiscoveryCatalog
{
    public IReadOnlyCollection<WorkDefinitionDescriptor> Definitions
        => [.. catalog.Definitions.Select(Project)];

    public IReadOnlyList<WorkDefinitionDescriptor> ListByCategory(
        string category,
        bool includeSubcategories = true)
        => [.. catalog.ListByCategory(category, includeSubcategories).Select(Project)];

    public IReadOnlyList<WorkDefinitionDescriptor> ListInvocableBy(WorkInvocationChannel channel)
        => [.. catalog.Definitions
            .Where(definition => definition.Configuration.Invocation.Allows(channel))
            .Select(Project)];

    public bool TryGet(string name, [NotNullWhen(true)] out WorkDefinitionDescriptor? definition)
    {
        if (catalog.TryGet(name, out var found))
        {
            definition = Project(found);
            return true;
        }

        definition = null;
        return false;
    }

    private static WorkDefinitionDescriptor Project(WorkDefinition definition)
        => WorkDefinitionDescriptor.FromDefinition(definition);
}
