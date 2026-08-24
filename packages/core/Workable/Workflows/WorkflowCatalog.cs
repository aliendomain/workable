namespace Workable;

internal sealed class WorkflowCatalog
{
    private readonly Dictionary<string, RegisteredWorkflow> byName;

    public WorkflowCatalog(IReadOnlyList<RegisteredWorkflow> workflows)
    {
        ArgumentNullException.ThrowIfNull(workflows);

        this.RegisteredWorkflows = workflows.ToArray();
        this.Definitions = this.RegisteredWorkflows.Select(workflow => workflow.Definition).ToArray();
        var duplicates = this.Definitions
            .GroupBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToArray();
        if (duplicates.Length > 0)
        {
            throw new InvalidOperationException($"Workflow definition names must be unique. Duplicate names: {string.Join(", ", duplicates)}.");
        }

        this.byName = this.RegisteredWorkflows.ToDictionary(
            workflow => workflow.Definition.Name,
            workflow => workflow,
            StringComparer.OrdinalIgnoreCase);
    }

    internal IReadOnlyList<RegisteredWorkflow> RegisteredWorkflows { get; }

    public IReadOnlyList<WorkflowDefinition> Definitions { get; }

    public bool TryGet(string name, out RegisteredWorkflow workflow)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return this.byName.TryGetValue(name, out workflow!);
    }

    public IReadOnlyList<WorkflowDefinition> ListByCategory(string category, bool includeSubcategories = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        return [.. this.Definitions.Where(definition =>
            includeSubcategories
                ? definition.Category.StartsWith(category, StringComparison.OrdinalIgnoreCase)
                : string.Equals(definition.Category, category, StringComparison.OrdinalIgnoreCase))];
    }
}
