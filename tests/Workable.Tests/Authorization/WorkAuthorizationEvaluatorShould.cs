using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class WorkAuthorizationEvaluatorShould
{
    [Fact]
    public void DiscoverExplicitReadAndOperateAudiencesWithoutExpandingReadOrOperate()
    {
        var explicitDiscovery = WorkDefinition.Create(
            "discover.explicit",
            authorization: WorkDefinitionAuthorization.Create(discoverGroups: ["discoverers"]));
        var readable = CreateDefinition("discover.read", "readers", "other.operators");
        var operable = CreateDefinition("discover.operate", "other.readers", "operators");
        var hidden = CreateDefinition("discover.hidden", "hidden.readers", "hidden.operators");
        var catalog = CreateCatalog(
            CreateRegisteredWork(explicitDiscovery),
            CreateRegisteredWork(readable),
            CreateRegisteredWork(operable),
            CreateRegisteredWork(hidden));
        var evaluator = new WorkAuthorizationEvaluator(
            catalog,
            Groups("discoverers", "readers", "operators"),
            isKnownAuthenticatedUser: false);

        Assert.True(evaluator.CanDiscover(explicitDiscovery));
        Assert.True(evaluator.CanDiscover(readable));
        Assert.True(evaluator.CanDiscover(operable));
        Assert.False(evaluator.CanDiscover(hidden));
        Assert.False(evaluator.CanRead(explicitDiscovery));
        Assert.False(evaluator.CanOperate(explicitDiscovery));
        Assert.Equal(
            [explicitDiscovery.Id, readable.Id, operable.Id],
            evaluator.DiscoverableDefinitionIds());
        Assert.False(evaluator.HasDiscoverAllWorkAccess());
    }

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

        Assert.True(evaluator.CanDiscover(visible.Id));
        Assert.True(evaluator.CanRead(visible.Id));
        Assert.True(evaluator.CanOperate(visible.Id));
        Assert.False(evaluator.CanDiscover(hidden.Id));
        Assert.False(evaluator.CanRead(hidden.Id));
        Assert.False(evaluator.CanOperate(hidden.Id));
        Assert.False(evaluator.CanDiscover(WorkDefinitionId.New()));
        Assert.False(evaluator.CanRead(WorkDefinitionId.New()));
        Assert.False(evaluator.CanOperate(WorkDefinitionId.New()));
        Assert.Equal([visible.Id], evaluator.DiscoverableDefinitionIds());
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

        Assert.False(partial.HasDiscoverAllWorkAccess());
        Assert.False(partial.HasReadAllWorkAccess());
        Assert.False(partial.HasOperateAllWorkAccess());
        Assert.True(complete.HasDiscoverAllWorkAccess());
        Assert.True(complete.HasReadAllWorkAccess());
        Assert.True(complete.HasOperateAllWorkAccess());
        Assert.Equal(2, complete.DiscoverableDefinitions().Count);
        Assert.Equal(2, complete.ReadableDefinitions().Count);
        Assert.Equal(2, complete.OperableDefinitions().Count);
    }

    [Fact]
    public void EmptyCatalogDoesNotInferDiscoverAllAccess()
    {
        var evaluator = new WorkAuthorizationEvaluator(
            CreateCatalog(),
            Groups(),
            isKnownAuthenticatedUser: false);

        Assert.False(evaluator.HasDiscoverAllWorkAccess());
        Assert.Empty(evaluator.DiscoverableDefinitions());
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

        Assert.True(evaluator.CanDiscover(restricted.Definition));
        Assert.True(evaluator.CanRead(restricted.Definition));
        Assert.True(evaluator.CanOperate(restricted.Definition));
        Assert.True(evaluator.HasDiscoverAllWorkAccess());
        Assert.True(evaluator.HasReadAllWorkAccess());
        Assert.True(evaluator.HasOperateAllWorkAccess());
        Assert.True(evaluator.HasSystemOperateAllWorkAccess());
        Assert.Empty(evaluator.OperableDefinitionIdsFor(WorkAction.Purge));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequireDiagnosticsWhenAuthorizedOperationsEnableEffectiveFullProfileCapture(
        bool canViewDiagnostics)
    {
        var work = CreatePermissionedWork(
            "profiled.work",
            "operators",
                WorkOperationPermissions.Queue |
                WorkOperationPermissions.ReconfigureDefinition |
                WorkOperationPermissions.ReconfigureWorker);
        var inheritedFull = CreatePermissionedWork(
            "inherited-full.work",
            "operators",
            WorkOperationPermissions.Queue,
            WorkerOptions.Default with
            {
                ProfilingEnabled = false,
                ProfilingCaptureMode = WorkProfileCaptureMode.Full,
            });
        var activeFull = CreatePermissionedWork(
            "active-full.work",
            "operators",
            WorkOperationPermissions.Queue,
            WorkerOptions.Default with
            {
                ProfilingEnabled = true,
                ProfilingCaptureMode = WorkProfileCaptureMode.Full,
            });
        var catalog = CreateCatalog(work, inheritedFull, activeFull);
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
        var workerReconfigure = evaluator.AuthorizeWorkerReconfiguration(
            work,
            CreateWorkerSnapshot(work.Definition, WorkerOptions.Default),
            new WorkerReconfiguration(ProfilingEnabled: true)
            {
                ProfilingCaptureMode = WorkProfileCaptureMode.Full,
            },
            requestContext);
        var enableExistingFullCapture = evaluator.AuthorizeWorkerReconfiguration(
            work,
            CreateWorkerSnapshot(
                work.Definition,
                WorkerOptions.Default with
                {
                    ProfilingEnabled = false,
                    ProfilingCaptureMode = WorkProfileCaptureMode.Full,
                }),
            new WorkerReconfiguration(ProfilingEnabled: true),
            requestContext);
        var retainExistingFullCapture = evaluator.AuthorizeWorkerReconfiguration(
            work,
            CreateWorkerSnapshot(work.Definition, fullCapture),
            new WorkerReconfiguration(ProfilingEnabled: true),
            requestContext);
        var boundedQueue = evaluator.AuthorizeQueue(
            work,
            input: null,
            WorkerOptions.Default with { ProfilingCaptureMode = WorkProfileCaptureMode.Bounded },
            requestContext);
        var inheritedFullQueue = evaluator.AuthorizeQueue(
            inheritedFull,
            input: null,
            WorkerOptions.Default with { ProfilingEnabled = true },
            requestContext);
        var activeFullQueue = evaluator.AuthorizeQueue(
            activeFull,
            input: null,
            options: null,
            requestContext);
        var selectDisabledFullCapture = evaluator.AuthorizeWorkerReconfiguration(
            work,
            CreateWorkerSnapshot(work.Definition, WorkerOptions.Default),
            new WorkerReconfiguration { ProfilingCaptureMode = WorkProfileCaptureMode.Full },
            requestContext);
        var configureDisabledFullCapture = evaluator.AuthorizeDefinitionReconfiguration(
            work,
            new WorkDefinitionReconfiguration(DefaultOptions: WorkerOptions.Default with
            {
                ProfilingEnabled = false,
                ProfilingCaptureMode = WorkProfileCaptureMode.Full,
            }),
            requestContext);

        Assert.Equal(canViewDiagnostics, queue.IsAllowed);
        Assert.Equal(canViewDiagnostics, reconfigure.IsAllowed);
        Assert.Equal(canViewDiagnostics, workerReconfigure.IsAllowed);
        Assert.Equal(canViewDiagnostics, enableExistingFullCapture.IsAllowed);
        Assert.Equal(canViewDiagnostics, inheritedFullQueue.IsAllowed);
        Assert.Equal(canViewDiagnostics, activeFullQueue.IsAllowed);
        Assert.True(retainExistingFullCapture.IsAllowed);
        Assert.True(boundedQueue.IsAllowed);
        Assert.True(selectDisabledFullCapture.IsAllowed);
        Assert.True(configureDisabledFullCapture.IsAllowed);
    }

    [Fact]
    public void FailClosedForFullProfileCaptureWhenNoSystemAuthorizationEvaluatorIsAvailable()
    {
        var work = CreatePermissionedWork(
            "profiled.without-system-authorization",
            "operators",
            WorkOperationPermissions.Queue |
            WorkOperationPermissions.ReconfigureDefinition |
            WorkOperationPermissions.ReconfigureWorker);
        var evaluator = new WorkAuthorizationEvaluator(
            CreateCatalog(work),
            Groups("operators"),
            isKnownAuthenticatedUser: true);
        var context = WorkRequestContext.Create(WorkInvocationChannel.InProcess, new WorkActor("operator"));
        var full = WorkerOptions.Default with
        {
            ProfilingEnabled = true,
            ProfilingCaptureMode = WorkProfileCaptureMode.Full,
        };

        Assert.False(evaluator.AuthorizeQueue(work, null, full, context).IsAllowed);
        Assert.False(evaluator.AuthorizeWorkerReconfiguration(
            work,
            CreateWorkerSnapshot(work.Definition, WorkerOptions.Default),
            new WorkerReconfiguration(ProfilingEnabled: true)
            {
                ProfilingCaptureMode = WorkProfileCaptureMode.Full,
            },
            context).IsAllowed);
        Assert.False(evaluator.AuthorizeDefinitionReconfiguration(
            work,
            new WorkDefinitionReconfiguration(DefaultOptions: full),
            context).IsAllowed);
        Assert.False(evaluator.AuthorizeDefinitionReconfiguration(
            work,
            new WorkDefinitionReconfiguration(Configuration: WorkConfiguration.Default with
            {
                ExecutionDiagnostics = new WorkExecutionDiagnosticsPersistenceConfiguration
                {
                    IsEnabled = true,
                    Retention = TimeSpan.FromHours(1),
                },
            }),
            context).IsAllowed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RequireControlSystemWhenDefinitionReconfigurationChangesPersistentDiagnostics(
        bool canControlSystem)
    {
        var work = CreatePermissionedWork(
            "persistent-diagnostics.work",
            "operators",
            WorkOperationPermissions.ReconfigureDefinition);
        var catalog = CreateCatalog(work);
        var groups = canControlSystem
            ? Groups("operators", "system-control")
            : Groups("operators");
        var systemAuthorization = new WorkSystemAuthorizationEvaluator(
            WorkSystemAuthorizationConfiguration.Default with
            {
                ControlSystemGroups = Groups("system-control"),
                WorkAdministratorGroups = Groups("operators"),
            },
            groups);
        var evaluator = new WorkAuthorizationEvaluator(
            catalog,
            groups,
            isKnownAuthenticatedUser: true,
            systemAuthorization);
        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor("persistence-operator"));
        var changedConfiguration = WorkConfiguration.Default with
        {
            ExecutionDiagnostics = new WorkExecutionDiagnosticsPersistenceConfiguration
            {
                IsEnabled = true,
                Retention = TimeSpan.FromHours(1),
            },
        };

        var changed = evaluator.AuthorizeDefinitionReconfiguration(
            work,
            new WorkDefinitionReconfiguration(Configuration: changedConfiguration),
            requestContext);
        var unchanged = evaluator.AuthorizeDefinitionReconfiguration(
            work,
            new WorkDefinitionReconfiguration(Configuration: WorkConfiguration.Default),
            requestContext);

        Assert.Equal(canControlSystem, changed.IsAllowed);
        Assert.True(unchanged.IsAllowed);
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

        Assert.Equal([allowed.Definition.Id], evaluator.OperableDefinitionIdsFor(action));
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
            evaluator.OperableDefinitionIdsFor((WorkAction)int.MaxValue));

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
        WorkOperationPermissions permissions,
        WorkerOptions? defaultOptions = null)
    {
        var authorization = new WorkAuthorizationBuilder();
        authorization.AllowOperationsToGroups([group], permissions);
        var registration = authorization.BuildRegistration();
        return new RegisteredWork(
            WorkDefinition.Create(
                name,
                defaultOptions: defaultOptions,
                authorization: registration.DefinitionAuthorization),
            _ => new NoopExecutor(),
            [],
            [],
            [],
            registration.OperateAuthorization);
    }

    private static RegisteredWork CreateRegisteredWork(WorkDefinition definition)
        => new(definition, _ => new NoopExecutor(), []);

    private static WorkerSnapshot CreateWorkerSnapshot(WorkDefinition definition, WorkerOptions options)
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkerSnapshot(
            WorkerId.New(),
            1,
            1,
            definition.Name,
            definition.Category,
            null,
            null,
            new HashSet<WorkIdentifier>(),
            WorkRequestContext.Create(WorkInvocationChannel.InProcess),
            WorkerState.Queued,
            null,
            null,
            options,
            definition.Configuration,
            [],
            null,
            now,
            now,
            now);
    }

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
