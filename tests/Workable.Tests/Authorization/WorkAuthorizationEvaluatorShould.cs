using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class WorkAuthorizationEvaluatorShould
{
    [Fact]
    public void ResolveKnownDefinitionIdsAndFailClosedForUnknownIds()
    {
        var visible = CreateDefinition("visible.work", "visible.read", "visible.operate");
        var hidden = CreateDefinition("hidden.work", "hidden.read", "hidden.operate");
        var catalog = CreateCatalog(CreateRegisteredWork(visible), CreateRegisteredWork(hidden));
        var evaluator = new WorkAuthorizationEvaluator(
            catalog,
            Groups("visible.read", "visible.operate"),
            isKnownAuthenticatedUser: true);

        Assert.True(evaluator.CanRead(visible.Id));
        Assert.True(evaluator.CanOperate(visible.Id));
        Assert.False(evaluator.CanRead(hidden.Id));
        Assert.False(evaluator.CanOperate(hidden.Id));
        Assert.False(evaluator.CanRead(WorkDefinitionId.New()));
        Assert.False(evaluator.CanOperate(WorkDefinitionId.New()));
        Assert.Equal([visible.Id], evaluator.ReadableDefinitionIds());
        Assert.Equal([visible.Id], evaluator.OperableDefinitionIds());
    }

    [Fact]
    public void ReportAllAccessOnlyWhenEveryRegisteredDefinitionIsAuthorized()
    {
        var first = CreateDefinition("first.work", "all.read", "all.operate");
        var second = CreateDefinition("second.work", "other.read", "other.operate");
        var catalog = CreateCatalog(CreateRegisteredWork(first), CreateRegisteredWork(second));
        var partial = new WorkAuthorizationEvaluator(
            catalog,
            Groups("all.read", "all.operate"),
            isKnownAuthenticatedUser: true);
        var complete = new WorkAuthorizationEvaluator(
            catalog,
            Groups("all.read", "all.operate", "other.read", "other.operate"),
            isKnownAuthenticatedUser: true);

        Assert.False(partial.HasReadAllWorkAccess());
        Assert.False(partial.HasOperateAllWorkAccess());
        Assert.True(complete.HasReadAllWorkAccess());
        Assert.True(complete.HasOperateAllWorkAccess());
        Assert.Equal(2, complete.ReadableDefinitions().Count);
        Assert.Equal(2, complete.OperableDefinitions().Count);
    }

    [Fact]
    public void LetWorkAdministratorsBypassDefinitionAndOperationScopes()
    {
        var restricted = CreatePermissionedWork(
            "restricted.work",
            "restricted.operators",
            WorkOperationPermissions.Start);
        var catalog = CreateCatalog(restricted);
        var groups = Groups("work.admins");
        var systemAuthorization = new WorkSystemAuthorizationEvaluator(
            WorkSystemAuthorizationConfiguration.Default with
            {
                WorkAdministratorGroups = Groups("work.admins"),
            },
            groups);
        var evaluator = new WorkAuthorizationEvaluator(
            catalog,
            groups,
            isKnownAuthenticatedUser: true,
            systemAuthorization);

        Assert.True(evaluator.CanRead(restricted.Definition));
        Assert.True(evaluator.CanOperate(restricted.Definition));
        Assert.True(evaluator.HasReadAllWorkAccess());
        Assert.True(evaluator.HasOperateAllWorkAccess());
        Assert.True(evaluator.HasSystemOperateAllWorkAccess());
        Assert.Empty(evaluator.OperableDefinitionNamesFor(WorkAction.Purge));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequireDiagnosticsWhenAuthorizedOperationsExplicitlySelectFullProfileCapture(
        bool canViewDiagnostics)
    {
        var work = CreatePermissionedWork(
            "profiled.work",
            "operators",
            WorkOperationPermissions.Queue | WorkOperationPermissions.ReconfigureDefinition);
        var catalog = CreateCatalog(work);
        var groups = canViewDiagnostics
            ? Groups("operators", "diagnostics")
            : Groups("operators");
        var systemAuthorization = new WorkSystemAuthorizationEvaluator(
            WorkSystemAuthorizationConfiguration.Default with
            {
                DiagnosticsGroups = Groups("diagnostics"),
            },
            groups);
        var evaluator = new WorkAuthorizationEvaluator(
            catalog,
            groups,
            isKnownAuthenticatedUser: true,
            systemAuthorization);
        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("profile-operator"));
        var fullCapture = WorkerOptions.Default with
        {
            ProfilingEnabled = true,
            ProfilingCaptureMode = WorkProfileCaptureMode.Full,
        };

        var queue = evaluator.AuthorizeQueue(
            work,
            input: null,
            fullCapture,
            requestContext);
        var reconfigure = evaluator.AuthorizeDefinitionReconfiguration(
            work,
            new WorkDefinitionReconfiguration(DefaultOptions: fullCapture),
            requestContext);
        var boundedQueue = evaluator.AuthorizeQueue(
            work,
            input: null,
            WorkerOptions.Default with { ProfilingCaptureMode = WorkProfileCaptureMode.Bounded },
            requestContext);

        Assert.Equal(canViewDiagnostics, queue.IsAllowed);
        Assert.Equal(canViewDiagnostics, reconfigure.IsAllowed);
        Assert.True(boundedQueue.IsAllowed);
    }

    [Theory]
    [InlineData(WorkAction.Start, WorkOperationPermissions.Start)]
    [InlineData(WorkAction.Pause, WorkOperationPermissions.Pause)]
    [InlineData(WorkAction.Cancel, WorkOperationPermissions.Cancel)]
    [InlineData(WorkAction.Push, WorkOperationPermissions.Push)]
    [InlineData(WorkAction.Purge, WorkOperationPermissions.Purge)]
    public void MapEveryWorkerActionToItsExactOperationPermission(
        WorkAction action,
        WorkOperationPermissions permission)
    {
        var allowed = CreatePermissionedWork("allowed.work", "operators", permission);
        var differentPermission = permission == WorkOperationPermissions.Start
            ? WorkOperationPermissions.Cancel
            : WorkOperationPermissions.Start;
        var denied = CreatePermissionedWork("denied.work", "operators", differentPermission);
        var catalog = CreateCatalog(allowed, denied);
        var evaluator = new WorkAuthorizationEvaluator(
            catalog,
            Groups("operators"),
            isKnownAuthenticatedUser: true);

        Assert.Equal([allowed.Definition.Name], evaluator.OperableDefinitionNamesFor(action));
    }

    [Fact]
    public void RejectUnsupportedWorkerActionsWhenBuildingAnOperationScope()
    {
        var work = CreatePermissionedWork(
            "allowed.work",
            "operators",
            WorkOperationPermissions.WorkerActions);
        var evaluator = new WorkAuthorizationEvaluator(
            CreateCatalog(work),
            Groups("operators"),
            isKnownAuthenticatedUser: true);

        var exception = Assert.Throws<InvalidOperationException>(() =>
            evaluator.OperableDefinitionNamesFor((WorkAction)int.MaxValue));

        Assert.Contains(int.MaxValue.ToString(), exception.Message, StringComparison.Ordinal);
    }

    private static WorkSystemCatalog CreateCatalog(params RegisteredWork[] registrations)
        => new(registrations, persistenceStoreAvailable: false);

    private static WorkDefinition CreateDefinition(
        string name,
        string readGroup,
        string operateGroup)
        => WorkDefinition.Create(
            name,
            authorization: WorkDefinitionAuthorization.Create(
                readGroups: [readGroup],
                operateGroups: [operateGroup]));

    private static RegisteredWork CreatePermissionedWork(
        string name,
        string group,
        WorkOperationPermissions permissions)
    {
        var authorization = new WorkAuthorizationBuilder();
        authorization.AllowOperationsToGroups([group], permissions);
        var registration = authorization.BuildRegistration();
        return new RegisteredWork(
            WorkDefinition.Create(name, authorization: registration.DefinitionAuthorization),
            _ => new NoopExecutor(),
            [],
            [],
            [],
            registration.OperateAuthorization);
    }

    private static RegisteredWork CreateRegisteredWork(WorkDefinition definition)
        => new(definition, _ => new NoopExecutor(), []);

    private static IReadOnlySet<string> Groups(params string[] groups)
        => groups.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private sealed class NoopExecutor : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}
