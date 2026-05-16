using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Workable;
internal sealed class WorkSystemCatalog : IWorkCatalog
{
    private readonly Lock sync = new();
    private readonly List<RegisteredWork> work = [];
    private Dictionary<WorkDefinitionId, RegisteredWork> workById = [];
    private Dictionary<string, RegisteredWork> workByName = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, IReadOnlyList<WorkDefinition>> definitionsByCategory = new Dictionary<string, IReadOnlyList<WorkDefinition>>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, IReadOnlyList<WorkDefinition>> definitionsByCategoryPath = new Dictionary<string, IReadOnlyList<WorkDefinition>>(StringComparer.OrdinalIgnoreCase);

    public WorkSystemCatalog(IReadOnlyList<RegisteredWork> work)
    {
        this.work.AddRange(work);
        this.RebuildIndexes();
    }

    public bool IsFrozen { get; private set; }

    public IReadOnlyCollection<WorkDefinition> Definitions { get; private set; } = [];

    internal IReadOnlyCollection<RegisteredWork> RegisteredWork => this.work;

    public IReadOnlyList<WorkDefinition> ListByCategory(string category, bool includeSubcategories = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(category);

        var index = includeSubcategories ? this.definitionsByCategoryPath : this.definitionsByCategory;
        return index.TryGetValue(category, out var definitions) ? definitions : [];
    }

    public bool TryGet(WorkDefinitionId id, [NotNullWhen(true)] out WorkDefinition? definition)
    {
        if (this.workById.TryGetValue(id, out var registeredWork))
        {
            definition = registeredWork.Definition;
            return true;
        }

        definition = null;
        return false;
    }

    public bool TryGet(string name, [NotNullWhen(true)] out WorkDefinition? definition)
    {
        if (this.workByName.TryGetValue(name, out var registeredWork))
        {
            definition = registeredWork.Definition;
            return true;
        }

        definition = null;
        return false;
    }

    internal bool TryGetWork(WorkDefinitionId id, [NotNullWhen(true)] out RegisteredWork? registeredWork)
        => this.workById.TryGetValue(id, out registeredWork);

    internal bool TryGetWork(string name, [NotNullWhen(true)] out RegisteredWork? registeredWork)
        => this.workByName.TryGetValue(name, out registeredWork);

    internal void AddWork(RegisteredWork registeredWork)
    {
        lock (this.sync)
        {
            if (this.IsFrozen)
            {
                throw new InvalidOperationException("Work definitions cannot be added after the catalog is frozen.");
            }

            this.work.Add(registeredWork);
            this.RebuildIndexes();
        }
    }

    internal void Freeze() => this.IsFrozen = true;

    public Task<WorkDefinitionReconfigurationOutcome> Reconfigure(
        WorkDefinitionVersion definition,
        WorkDefinitionReconfiguration changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        cancellationToken.ThrowIfCancellationRequested();

        lock (this.sync)
        {
            if (!this.workById.TryGetValue(definition.DefinitionId, out var registeredWork))
            {
                return Task.FromResult(WorkDefinitionReconfigurationOutcome.NotFound(definition.DefinitionId));
            }

            if (registeredWork.Definition.Revision != definition.Revision)
            {
                return Task.FromResult(WorkDefinitionReconfigurationOutcome.Conflict(registeredWork.Definition, definition.Revision));
            }

            var updatedOptions = changes.DefaultOptions ?? registeredWork.Definition.DefaultOptions;
            var updatedConfiguration = changes.Configuration ?? registeredWork.Definition.Configuration;
            var messages = ValidateReconfiguration(updatedOptions, updatedConfiguration);
            if (messages.Count > 0)
            {
                return Task.FromResult(WorkDefinitionReconfigurationOutcome.Invalid(registeredWork.Definition, messages));
            }

            var updatedDefinition = registeredWork.Definition with
            {
                DefaultOptions = updatedOptions,
                Configuration = updatedConfiguration,
                Revision = registeredWork.Definition.Revision + 1,
            };
            var index = this.work.IndexOf(registeredWork);
            if (index < 0)
            {
                return Task.FromResult(WorkDefinitionReconfigurationOutcome.NotFound(definition.DefinitionId));
            }

            this.work[index] = registeredWork.WithDefinition(updatedDefinition);
            this.RebuildIndexes();
            return Task.FromResult(WorkDefinitionReconfigurationOutcome.Accepted(updatedDefinition));
        }
    }

    private static List<WorkMessage> ValidateReconfiguration(
        WorkerOptions options,
        WorkConfiguration configuration)
    {
        var messages = new List<WorkMessage>();
        messages.AddRange(WorkConfigurationValidator.Validate(configuration));
        if (options.Configuration is { } optionConfiguration)
        {
            messages.AddRange(WorkConfigurationValidator.Validate(optionConfiguration));
        }

        return messages;
    }

    private void RebuildIndexes()
    {
        var duplicateIds = this.work
            .GroupBy(registeredWork => registeredWork.Definition.Id)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicateIds.Count > 0)
        {
            throw new InvalidOperationException($"Work definition ids must be unique. Duplicate ids: {string.Join(", ", duplicateIds)}.");
        }

        var workWithNames = this.work
            .Select(registeredWork => (Work: registeredWork, registeredWork.Definition.Name))
            .ToList();

        var duplicateNames = workWithNames
            .GroupBy(registeredWork => registeredWork.Name, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .ToList();

        if (duplicateNames.Count > 0)
        {
            throw new InvalidOperationException($"Work definition names must be unique within a catalog. Duplicate names: {string.Join(", ", duplicateNames)}.");
        }

        this.workById = this.work.ToDictionary(registeredWork => registeredWork.Definition.Id);
        this.workByName = workWithNames
            .ToDictionary(registeredWork => registeredWork.Name, registeredWork => registeredWork.Work, StringComparer.OrdinalIgnoreCase);
        this.Definitions = [.. this.work.Select(registeredWork => registeredWork.Definition)];
        this.definitionsByCategory = BuildCategoryIndex(this.Definitions, includeSubcategories: false);
        this.definitionsByCategoryPath = BuildCategoryIndex(this.Definitions, includeSubcategories: true);
    }

    private static Dictionary<string, IReadOnlyList<WorkDefinition>> BuildCategoryIndex(
        IEnumerable<WorkDefinition> definitions,
        bool includeSubcategories)
    {
        var groups = new Dictionary<string, List<WorkDefinition>>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            var categories = includeSubcategories
                ? GetCategoryPath(definition.Category)
                : [definition.Category];

            foreach (var category in categories)
            {
                if (!groups.TryGetValue(category, out var categoryDefinitions))
                {
                    categoryDefinitions = [];
                    groups[category] = categoryDefinitions;
                }

                categoryDefinitions.Add(definition);
            }
        }

        return groups.ToDictionary(
            group => group.Key,
            group => (IReadOnlyList<WorkDefinition>)[.. group.Value
                .OrderBy(definition => definition.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)],
            StringComparer.OrdinalIgnoreCase);
    }

    private static List<string> GetCategoryPath(string category)
    {
        var path = new List<string>();
        var parts = category
            .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 1; i <= parts.Length; i++)
        {
            path.Add(string.Join(':', parts.Take(i)));
        }

        return path.Count == 0 ? [WorkDefinitionMetadataDefaults.Category] : path;
    }
}
