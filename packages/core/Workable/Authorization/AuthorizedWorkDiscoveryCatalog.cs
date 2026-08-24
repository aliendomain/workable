using System.Diagnostics.CodeAnalysis;

namespace Workable;

internal sealed class AuthorizedWorkDiscoveryCatalog(
    WorkSystemCatalog catalog,
    WorkAuthorizationEvaluator? authorization = null) : IWorkDiscoveryCatalog
{
    public IReadOnlyCollection<WorkDefinitionDescriptor> Definitions
        => [.. this.DiscoverableDefinitions().Select(WorkDefinitionDescriptor.FromSnapshot)];

    public IReadOnlyList<WorkDefinitionDescriptor> ListByCategory(
        string category,
        bool includeSubcategories = true)
        => [.. catalog.ListByCategory(category, includeSubcategories)
            .Where(this.CanDiscover)
            .Select(WorkDefinitionDescriptor.FromSnapshot)];

    public IReadOnlyList<WorkDefinitionDescriptor> ListInvocableBy(WorkInvocationChannel channel)
        => [.. this.DiscoverableDefinitions()
            .Where(definition => definition.Configuration.Invocation.Allows(channel))
            .Select(WorkDefinitionDescriptor.FromSnapshot)];

    public bool TryGet(string name, [NotNullWhen(true)] out WorkDefinitionDescriptor? definition)
    {
        if (catalog.TryGet(name, out var found) && this.CanDiscover(found))
        {
            definition = WorkDefinitionDescriptor.FromSnapshot(found);
            return true;
        }

        definition = null;
        return false;
    }

    private IEnumerable<WorkDefinition> DiscoverableDefinitions()
        => catalog.Definitions.Where(this.CanDiscover);

    private bool CanDiscover(WorkDefinition definition)
        => authorization?.CanDiscover(definition) ?? true;
}
