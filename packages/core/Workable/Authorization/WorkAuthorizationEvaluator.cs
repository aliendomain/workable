namespace Workable;

internal sealed class WorkAuthorizationEvaluator(
    WorkSystemCatalog catalog,
    IReadOnlySet<string> groups,
    bool isKnownAuthenticatedUser,
    WorkSystemAuthorizationEvaluator? systemAuthorization = null)
{
    public bool CanRead(WorkDefinition definition)
        => systemAuthorization?.HasReadAllWorkAccess() == true
            || definition.Authorization.CanRead(groups, isKnownAuthenticatedUser);

    public bool CanRead(WorkDefinitionId definitionId)
    {
        if (!this.TryGet(definitionId, out var definition))
        {
            return false;
        }

        return this.CanRead(definition);
    }

    public bool CanOperate(WorkDefinition definition)
        => systemAuthorization?.HasOperateAllWorkAccess() == true
            || definition.Authorization.CanOperate(groups, isKnownAuthenticatedUser);

    public bool CanOperate(WorkDefinitionId definitionId)
    {
        if (!this.TryGet(definitionId, out var definition))
        {
            return false;
        }

        return this.CanOperate(definition);
    }

    public bool HasReadAllWorkAccess()
        => systemAuthorization?.HasReadAllWorkAccess() == true ||
            this.ReadableDefinitionIds().Count == catalog.Definitions.Count;

    public bool HasOperateAllWorkAccess()
        => systemAuthorization?.HasOperateAllWorkAccess() == true ||
            this.OperableDefinitionIds().Count == catalog.Definitions.Count;

    public bool HasSystemOperateAllWorkAccess()
        => systemAuthorization?.HasOperateAllWorkAccess() == true;

    public IReadOnlySet<WorkDefinitionId> ReadableDefinitionIds()
        => this.ReadableDefinitions()
            .Select(definition => definition.Id)
            .ToHashSet();

    public IReadOnlySet<WorkDefinitionId> OperableDefinitionIds()
        => this.OperableDefinitions()
            .Select(definition => definition.Id)
            .ToHashSet();

    public IReadOnlyList<WorkDefinition> ReadableDefinitions()
        => [.. catalog.Definitions.Where(this.CanRead)];

    public IReadOnlyList<WorkDefinition> OperableDefinitions()
        => [.. catalog.Definitions.Where(this.CanOperate)];

    public IReadOnlySet<string> OperableDefinitionNamesFor(WorkAction action)
        => systemAuthorization?.HasOperateAllWorkAccess() == true
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : catalog.RegisteredWork
                .Where(registeredWork => this.CanAttempt(registeredWork, ToPermission(action)))
                .Select(registeredWork => registeredWork.Definition.Name)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public WorkOperateAuthorizationDecision AuthorizeQueue(
        RegisteredWork registeredWork,
        WorkInput? input,
        WorkerOptions? options,
        WorkRequestContext requestContext)
    {
        ArgumentNullException.ThrowIfNull(registeredWork);
        ArgumentNullException.ThrowIfNull(requestContext);

        if (systemAuthorization?.HasOperateAllWorkAccess() == true)
        {
            return WorkOperateAuthorizationDecision.Allow();
        }

        return registeredWork.Definition.Authorization.CanOperate(groups, isKnownAuthenticatedUser)
            ? registeredWork.OperateAuthorization.EvaluateQueue(
                groups,
                isKnownAuthenticatedUser,
                registeredWork.Definition,
                input,
                options,
                requestContext)
            : WorkOperateAuthorizationDecision.Deny();
    }

    public WorkOperateAuthorizationDecision AuthorizeWorkerAction(
        RegisteredWork registeredWork,
        WorkerSnapshot worker,
        WorkAction action,
        WorkRequestContext requestContext)
    {
        ArgumentNullException.ThrowIfNull(registeredWork);
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(requestContext);

        if (systemAuthorization?.HasOperateAllWorkAccess() == true)
        {
            return WorkOperateAuthorizationDecision.Allow();
        }

        return registeredWork.Definition.Authorization.CanOperate(groups, isKnownAuthenticatedUser)
            ? registeredWork.OperateAuthorization.EvaluateWorkerAction(
                groups,
                isKnownAuthenticatedUser,
                registeredWork.Definition,
                worker.Id.ToString(),
                worker.Input,
                ToOperateAction(action),
                requestContext)
            : WorkOperateAuthorizationDecision.Deny();
    }

    public WorkOperateAuthorizationDecision AuthorizeWorkerReconfiguration(
        RegisteredWork registeredWork,
        WorkerSnapshot worker,
        WorkerReconfiguration changes,
        WorkRequestContext requestContext)
    {
        ArgumentNullException.ThrowIfNull(registeredWork);
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(requestContext);

        if (systemAuthorization?.HasOperateAllWorkAccess() == true)
        {
            return WorkOperateAuthorizationDecision.Allow();
        }

        return registeredWork.Definition.Authorization.CanOperate(groups, isKnownAuthenticatedUser)
            ? registeredWork.OperateAuthorization.EvaluateWorkerReconfiguration(
                groups,
                isKnownAuthenticatedUser,
                registeredWork.Definition,
                worker.Id.ToString(),
                worker.Input,
                ToWorkerChanges(changes),
                requestContext)
            : WorkOperateAuthorizationDecision.Deny();
    }

    public WorkOperateAuthorizationDecision AuthorizeDefinitionReconfiguration(
        RegisteredWork registeredWork,
        WorkDefinitionReconfiguration changes,
        WorkRequestContext requestContext)
    {
        ArgumentNullException.ThrowIfNull(registeredWork);
        ArgumentNullException.ThrowIfNull(changes);
        ArgumentNullException.ThrowIfNull(requestContext);

        if (systemAuthorization?.HasOperateAllWorkAccess() == true)
        {
            return WorkOperateAuthorizationDecision.Allow();
        }

        return registeredWork.Definition.Authorization.CanOperate(groups, isKnownAuthenticatedUser)
            ? registeredWork.OperateAuthorization.EvaluateDefinitionReconfiguration(
                groups,
                isKnownAuthenticatedUser,
                registeredWork.Definition,
                ToDefinitionChanges(changes),
                requestContext)
            : WorkOperateAuthorizationDecision.Deny();
    }

    private static WorkOperateAction ToOperateAction(WorkAction action)
        => action switch
        {
            WorkAction.Start => WorkOperateAction.Start,
            WorkAction.Pause => WorkOperateAction.Pause,
            WorkAction.Cancel => WorkOperateAction.Cancel,
            WorkAction.Push => WorkOperateAction.Push,
            WorkAction.Purge => WorkOperateAction.Purge,
            _ => throw new InvalidOperationException($"Unsupported worker action '{action}'."),
        };

    private static WorkOperationPermissions ToPermission(WorkAction action)
        => action switch
        {
            WorkAction.Start => WorkOperationPermissions.Start,
            WorkAction.Pause => WorkOperationPermissions.Pause,
            WorkAction.Cancel => WorkOperationPermissions.Cancel,
            WorkAction.Push => WorkOperationPermissions.Push,
            WorkAction.Purge => WorkOperationPermissions.Purge,
            _ => throw new InvalidOperationException($"Unsupported worker action '{action}'."),
        };

    private static WorkWorkerReconfigurationChanges ToWorkerChanges(WorkerReconfiguration changes)
        => new(
            changes.ProfilingEnabled,
            changes.Start,
            changes.Coordination,
            changes.Recurrence,
            changes.TransientRetry,
            changes.FailedWorker,
            changes.Logging,
            changes.Retention);

    private static WorkDefinitionReconfigurationChanges ToDefinitionChanges(WorkDefinitionReconfiguration changes)
        => new(
            changes.DefaultOptions,
            changes.Configuration);

    private bool CanAttempt(RegisteredWork registeredWork, WorkOperationPermissions permission)
        => registeredWork.Definition.Authorization.CanOperate(groups, isKnownAuthenticatedUser) &&
            registeredWork.OperateAuthorization.CanAttempt(groups, isKnownAuthenticatedUser, permission);

    private bool TryGet(WorkDefinitionId definitionId, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out WorkDefinition? definition)
    {
        definition = catalog.Definitions.SingleOrDefault(candidate => candidate.Id == definitionId);
        return definition is not null;
    }
}
