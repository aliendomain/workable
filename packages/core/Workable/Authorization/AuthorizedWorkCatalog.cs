using System.Diagnostics.CodeAnalysis;

namespace Workable;

internal sealed class AuthorizedWorkCatalog(
    WorkSystemCatalog catalog,
    IWorkCatalog inner,
    WorkAuthorizationEvaluator authorization,
    WorkRequestContext requestContext) : IWorkCatalog
{
    public bool IsFrozen => inner.IsFrozen;

    public IReadOnlyCollection<WorkDefinition> Definitions
        => [.. inner.Definitions.Where(authorization.CanRead)];

    public IReadOnlyList<WorkDefinition> ListByCategory(string category, bool includeSubcategories = true)
        => [.. inner.ListByCategory(category, includeSubcategories)
            .Where(authorization.CanRead)];

    public bool TryGet(string name, [NotNullWhen(true)] out WorkDefinition? definition)
    {
        if (inner.TryGet(name, out var found) && authorization.CanRead(found))
        {
            definition = found;
            return true;
        }

        definition = null;
        return false;
    }

    public Task<WorkDefinitionReconfigurationOutcome> Reconfigure(
        WorkDefinitionVersion definition,
        WorkDefinitionReconfiguration changes,
        CancellationToken cancellationToken = default)
    {
        if (!catalog.TryGetWork(definition.DefinitionId, out var registeredWork))
        {
            return Task.FromResult(WorkDefinitionReconfigurationOutcome.Unauthorized(definition.DefinitionId.ToString()));
        }

        var decision = authorization.AuthorizeDefinitionReconfiguration(
            registeredWork,
            changes,
            requestContext);
        if (decision.IsAllowed)
        {
            return this.FilterReconfigurationOutcome(
                inner.Reconfigure(definition, changes, cancellationToken),
                registeredWork.Definition);
        }

        var outcome = decision.IsInvalid
            ? Task.FromResult(WorkDefinitionReconfigurationOutcome.Invalid(registeredWork.Definition, decision.Messages))
            : Task.FromResult(WorkDefinitionReconfigurationOutcome.Unauthorized(registeredWork.Definition.Name));
        return this.FilterReconfigurationOutcome(outcome, registeredWork.Definition);
    }

    internal Task<WorkDefinitionReconfigurationOutcome> Reconfigure(
        string name,
        long revision,
        WorkDefinitionReconfiguration changes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(changes);

        if (!catalog.TryGetWork(name, out var registeredWork) ||
            !authorization.CanDiscover(registeredWork.Definition))
        {
            return Task.FromResult(WorkDefinitionReconfigurationOutcome.NotFound(name));
        }

        return this.Reconfigure(
            new WorkDefinitionVersion(registeredWork.Definition.Id, revision),
            changes,
            cancellationToken);
    }

    private async Task<WorkDefinitionReconfigurationOutcome> FilterReconfigurationOutcome(
        Task<WorkDefinitionReconfigurationOutcome> outcomeTask,
        WorkDefinition definition)
    {
        var outcome = await outcomeTask;
        return authorization.CanRead(definition) || outcome.Definition is null
            ? outcome
            : outcome with { Definition = null };
    }
}
