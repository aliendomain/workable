using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class WorkSystemAuthorizationConfigurationTests
{
    [Fact]
    public void WorkSystemRequiresAuthorizationByDefault()
    {
        var provider = new ServiceCollection()
            .AddDefaultWorkableSystemForAuthorizationTests(builder => { })
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        Assert.True(system.RequiresAuthorization);
    }

    [Fact]
    public void WorkSystemCanDisableAuthorization()
    {
        var provider = new ServiceCollection()
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.RequireAuthorization(false))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        Assert.False(system.RequiresAuthorization);
    }

    [Fact]
    public void WorkSystemsConfigureAuthorizationIndependently()
    {
        var provider = new ServiceCollection()
            .AddDefaultWorkableSystemForAuthorizationTests("open", builder => builder.RequireAuthorization(false))
            .AddDefaultWorkableSystemForAuthorizationTests("secure", builder => builder.RequireAuthorization())
            .BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();

        Assert.True(registry.TryGet("open", out var open));
        Assert.True(registry.TryGet("secure", out var secure));
        Assert.False(open.RequiresAuthorization);
        Assert.True(secure.RequiresAuthorization);
    }

    [Fact]
    public void DirectInterfacesThrowWhenAuthorizationIsRequired()
    {
        var provider = new ServiceCollection()
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(WorkDefinition.Create("secure"), SuccessfulWork))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        Assert.Throws<WorkSystemAuthorizationRequiredException>(() => system.Catalog);
        Assert.Throws<WorkSystemAuthorizationRequiredException>(() => system.Queue);
        Assert.Throws<WorkSystemAuthorizationRequiredException>(() => system.Workers);
        Assert.Throws<WorkSystemAuthorizationRequiredException>(() => system.Query);
        Assert.Throws<WorkSystemAuthorizationRequiredException>(() => system.Events);
        Assert.Throws<WorkSystemAuthorizationRequiredException>(() => system.Diagnostics);
    }

    [Fact]
    public void DirectInterfacesAreAvailableWhenAuthorizationIsDisabled()
    {
        var provider = new ServiceCollection()
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .RequireAuthorization(false)
                .AddWork(WorkDefinition.Create("open"), SuccessfulWork))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        Assert.NotNull(system.Catalog);
        Assert.NotNull(system.Queue);
        Assert.NotNull(system.Workers);
        Assert.NotNull(system.Query);
        Assert.NotNull(system.Events);
        Assert.NotNull(system.Diagnostics);
    }

    [Fact]
    public void CreateSessionProvidesInterfacesWhenAuthorizationIsRequired()
    {
        var provider = new ServiceCollection()
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(WorkDefinition.Create("secure"), SuccessfulWork))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        var session = system.CreateSession(CreateRequestContext("test-user"));

        Assert.NotNull(session.Catalog);
        Assert.NotNull(session.Queue);
        Assert.NotNull(session.Workers);
        Assert.NotNull(session.Query);
        Assert.NotNull(session.Events);
    }

    [Fact]
    public async Task AuthorizedSessionFiltersCatalogAndQueriesByReadScope()
    {
        var visible = PausedDefinition("visible.work");
        var hidden = PausedDefinition("hidden.work");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["operator"] = Groups("visible.read", "visible.operate", "hidden.read", "hidden.operate"),
                ["reader"] = Groups("visible.read"),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .AddWork(visible, SuccessfulWork, configure: null, authorize: authorize => authorize.RequireGroups(
                    readGroups: ["visible.read"],
                    operateGroups: ["visible.operate"]))
                .AddWork(hidden, SuccessfulWork, configure: null, authorize: authorize => authorize.RequireGroups(
                    readGroups: ["hidden.read"],
                    operateGroups: ["hidden.operate"])))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        await system.Start();
        var operatorSession = system.CreateSession(CreateRequestContext("operator"));
        await operatorSession.Queue.Enqueue(visible.Name);
        await operatorSession.Queue.Enqueue(hidden.Name);
        await TestEventually.Until(async () =>
            (await operatorSession.Query.Workers(new WorkerCriteria(Take: 10))).TotalCount == 2);

        var readerSession = system.CreateSession(CreateRequestContext("reader"));

        Assert.Equal(visible.Id, Assert.Single(readerSession.Catalog.Definitions).Id);
        Assert.Equal(visible.Id, Assert.Single((await readerSession.Query.WorkDefinitions()).Definitions).Id);
        Assert.Equal(visible.Name, Assert.Single((await readerSession.Query.Workers(new WorkerCriteria(Take: 10))).Workers).DefinitionName);
        Assert.Null(await readerSession.Query.WorkInfo(hidden.Name));
    }

    [Fact]
    public async Task AuthorizedSessionRejectsQueueOutsideOperateScope()
    {
        var visible = PausedDefinition("visible.queue");
        var hidden = PausedDefinition("hidden.queue");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["operator"] = Groups("visible.read", "visible.operate", "hidden.read"),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .AddWork(visible, SuccessfulWork, configure: null, authorize: authorize => authorize.RequireGroups(
                    readGroups: ["visible.read"],
                    operateGroups: ["visible.operate"]))
                .AddWork(hidden, SuccessfulWork, configure: null, authorize: authorize => authorize.RequireGroups(
                    readGroups: ["hidden.read"],
                    operateGroups: ["hidden.operate"])))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        await system.Start();
        var session = system.CreateSession(CreateRequestContext("operator"));

        var accepted = await session.Queue.Enqueue(visible.Name);
        var rejected = await session.Queue.Enqueue(hidden.Name);

        Assert.True(accepted.QueueOutcome.IsAccepted);
        Assert.Equal(WorkQueueStatus.Unauthorized, rejected.QueueOutcome.Status);
    }

    [Fact]
    public async Task AuthorizedSessionRejectsWorkerOperationsOutsideOperateScope()
    {
        var definition = PausedDefinition("hidden.operate");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["operator"] = Groups("operate.read", "operate.write"),
                ["reader"] = Groups("operate.read"),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(
                definition,
                SuccessfulWork,
                configure: null,
                authorize: authorize => authorize.RequireGroups(
                    readGroups: ["operate.read"],
                    operateGroups: ["operate.write"])))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        await system.Start();
        var queued = await system.CreateSession(CreateRequestContext("operator")).Queue.Enqueue(definition.Name);
        var worker = await system.CreateSession(CreateRequestContext("operator")).Query.Worker(
            queued.WorkerId ?? throw new InvalidOperationException("Expected queued worker."));

        var outcome = await system.CreateSession(CreateRequestContext("reader")).Workers.Execute(
            worker?.Version ?? throw new InvalidOperationException("Expected worker."),
            WorkAction.Cancel);

        Assert.Equal(WorkActionStatus.Unauthorized, outcome.Status);
    }

    [Fact]
    public async Task AuthorizedSessionReturnsNotFoundForMissingWorkerOperationsWithinOperateScope()
    {
        var definition = PausedDefinition("missing.operate");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["operator"] = Groups("missing.read", "missing.write"),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(
                definition,
                SuccessfulWork,
                configure: null,
                authorize: authorize => authorize.RequireGroups(
                    readGroups: ["missing.read"],
                    operateGroups: ["missing.write"])))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        await system.Start();

        var outcome = await system.CreateSession(CreateRequestContext("operator")).Workers.Execute(
            new WorkerVersion(WorkerId.New(), Revision: 1),
            WorkAction.Start);

        Assert.Equal(WorkActionStatus.NotFound, outcome.Status);
    }

    [Fact]
    public void UnsecuredSessionDoesNotResolveAuthorizationScope()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider, ThrowingGroupProvider>()
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .RequireAuthorization(false)
                .AddWork(WorkDefinition.Create("open"), SuccessfulWork))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        var session = system.CreateSession(CreateRequestContext("anyone"));

        Assert.NotNull(session.Catalog);
    }

    [Fact]
    public async Task WorkWithoutAuthorizationIsClosedByDefaultWhenAuthorizationIsEnabled()
    {
        var definition = PausedDefinition("closed.by.default");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["operator"] = Groups("anything"),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(definition, SuccessfulWork))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        await system.Start();

        var session = system.CreateSession(CreateRequestContext("operator"));
        var queued = await session.Queue.Enqueue(definition.Name);

        Assert.Empty(session.Catalog.Definitions);
        Assert.Equal(WorkQueueStatus.Unauthorized, queued.QueueOutcome.Status);
    }

    [Fact]
    public async Task WorkWithoutAuthorizationIsAccessibleWhenAuthorizationIsDisabled()
    {
        var definition = PausedDefinition("open.when.disabled");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider, ThrowingGroupProvider>()
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .RequireAuthorization(false)
                .AddWork(definition, SuccessfulWork))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        await system.Start();

        var session = system.CreateSession(CreateRequestContext("operator"));
        var queued = await session.Queue.Enqueue(definition.Name);

        Assert.Single(session.Catalog.Definitions);
        Assert.True(queued.QueueOutcome.IsAccepted);
    }

    [Fact]
    public async Task ExplicitDefinitionAuthorizationIsPreservedWhenRegistered()
    {
        var definition = PausedDefinition("explicit.definition.authorization") with
        {
            Authorization = WorkDefinitionAuthorization.Create(
                readGroups: ["explicit.read"],
                operateGroups: ["explicit.operate"],
                source: WorkAuthorizationRegistrationSource.Fluent),
        };
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["operator"] = Groups("explicit.read", "explicit.operate"),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(definition, SuccessfulWork))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        await system.Start();

        var session = system.CreateSession(CreateRequestContext("operator"));
        var registeredDefinition = Assert.Single(session.Catalog.Definitions);
        var queued = await session.Queue.Enqueue(definition.Name);

        Assert.Equal(WorkAuthorizationRegistrationSource.Fluent, registeredDefinition.Authorization.Read.Source);
        Assert.Equal(["explicit.read"], registeredDefinition.Authorization.Read.Groups.OrderBy(group => group).ToArray());
        Assert.True(queued.QueueOutcome.IsAccepted);
    }

    [Fact]
    public void AttributeAuthorizationAppearsOnCatalogDefinition()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["reader"] = Groups("attr.read"),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork<AttributedAuthorizationWork>())
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        var definition = Assert.Single(system.CreateSession(CreateRequestContext("reader")).Catalog.Definitions);

        Assert.Equal(WorkAuthorizationRegistrationSource.Attribute, definition.Authorization.Read.Source);
        Assert.Equal(WorkAuthorizationRegistrationSource.Attribute, definition.Authorization.Operate.Source);
        Assert.Equal(["attr.read"], definition.Authorization.Read.Groups.OrderBy(group => group).ToArray());
        Assert.Equal(["attr.operate"], definition.Authorization.Operate.Groups.OrderBy(group => group).ToArray());
    }

    [Fact]
    public void FluentAuthorizationOverridesAttributeAuthorization()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["reader"] = Groups("fluent.read"),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork<AttributedAuthorizationWork>(
                configure: null,
                authorize: authorize => authorize.RequireGroups(
                    readGroups: ["fluent.read"],
                    operateGroups: ["fluent.operate"])))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        var definition = Assert.Single(system.CreateSession(CreateRequestContext("reader")).Catalog.Definitions);

        Assert.Equal(WorkAuthorizationRegistrationSource.Fluent, definition.Authorization.Read.Source);
        Assert.Equal(WorkAuthorizationRegistrationSource.Fluent, definition.Authorization.Operate.Source);
        Assert.Equal(["fluent.read"], definition.Authorization.Read.Groups.OrderBy(group => group).ToArray());
        Assert.Equal(["fluent.operate"], definition.Authorization.Operate.Groups.OrderBy(group => group).ToArray());
    }

    [Fact]
    public void FluentAllowReadToGroupsSetsReadAuthorization()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["reader"] = Groups("allow.read"),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(
                PausedDefinition("allow.read.definition"),
                SuccessfulWork,
                configure: null,
                authorize: authorize => authorize.AllowReadToGroups("allow.read")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        var definition = Assert.Single(system.CreateSession(CreateRequestContext("reader")).Catalog.Definitions);

        Assert.Equal(WorkAuthorizationRegistrationSource.Fluent, definition.Authorization.Read.Source);
        Assert.Equal(["allow.read"], definition.Authorization.Read.Groups.OrderBy(group => group).ToArray());
        Assert.Equal(WorkAuthorizationRegistrationSource.None, definition.Authorization.Operate.Source);
        Assert.Empty(definition.Authorization.Operate.Groups);
    }

    [Fact]
    public void FluentAllowOperateToGroupsSetsOperateAuthorization()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider, ThrowingGroupProvider>()
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .RequireAuthorization(false)
                .AddWork(
                    PausedDefinition("allow.operate.definition"),
                    SuccessfulWork,
                    configure: null,
                    authorize: authorize => authorize.AllowOperateToGroups("allow.operate")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        var definition = Assert.Single(system.Catalog.Definitions);

        Assert.Equal(WorkAuthorizationRegistrationSource.None, definition.Authorization.Read.Source);
        Assert.Empty(definition.Authorization.Read.Groups);
        Assert.Equal(WorkAuthorizationRegistrationSource.Fluent, definition.Authorization.Operate.Source);
        Assert.Equal(["allow.operate"], definition.Authorization.Operate.Groups.OrderBy(group => group).ToArray());
    }

    [Fact]
    public void FluentAllowOperateToKnownAuthenticatedUsersSetsOperateAuthorization()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider, ThrowingGroupProvider>()
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .RequireAuthorization(false)
                .AddWork(
                    PausedDefinition("allow.known.authenticated.definition"),
                    SuccessfulWork,
                    configure: null,
                    authorize: authorize => authorize.AllowOperateToKnownAuthenticatedUsers()))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        var definition = Assert.Single(system.Catalog.Definitions);

        Assert.Equal(WorkAuthorizationRegistrationSource.None, definition.Authorization.Read.Source);
        Assert.Empty(definition.Authorization.Read.Groups);
        Assert.Equal(WorkAuthorizationRegistrationSource.Fluent, definition.Authorization.Operate.Source);
        Assert.True(definition.Authorization.Operate.AllowsKnownAuthenticatedUsers);
        Assert.Empty(definition.Authorization.Operate.Groups);
    }

    [Fact]
    public void FluentAllowReadAndOperateToGroupsCanBeChained()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["operator"] = Groups("allow.read", "allow.operate"),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(
                PausedDefinition("allow.operate.definition"),
                SuccessfulWork,
                configure: null,
                authorize: authorize => authorize
                    .AllowReadToGroups("allow.read")
                    .AllowOperateToGroups("allow.operate")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        var definition = Assert.Single(system.CreateSession(CreateRequestContext("operator")).Catalog.Definitions);

        Assert.Equal(WorkAuthorizationRegistrationSource.Fluent, definition.Authorization.Operate.Source);
        Assert.Equal(["allow.read"], definition.Authorization.Read.Groups.OrderBy(group => group).ToArray());
        Assert.Equal(["allow.operate"], definition.Authorization.Operate.Groups.OrderBy(group => group).ToArray());
    }

    [Fact]
    public async Task WorkAdministratorCanReadAndOperateAllWorkWithoutPerDefinitionAuthorization()
    {
        var definition = PausedDefinition("work.admin.definition");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["work-admin"] = Groups("work.admin"),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .ConfigureAuthorization(authorization => authorization.WorkAdministrators("work.admin"))
                .AddWork(definition, SuccessfulWork))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        await system.Start();

        var session = system.CreateSession(CreateRequestContext("work-admin"));
        var queued = await session.Queue.Enqueue(definition.Name);

        Assert.Single(session.Catalog.Definitions);
        Assert.True(queued.QueueOutcome.IsAccepted);
    }

    [Fact]
    public async Task SystemAdministratorCanReadAllWorkWithoutOperateAllWork()
    {
        var definition = PausedDefinition("system.admin.definition");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["system-admin"] = Groups("system.admin"),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .ConfigureAuthorization(authorization => authorization.SystemAdministrators("system.admin"))
                .AddWork(definition, SuccessfulWork))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        await system.Start();

        var session = system.CreateSession(CreateRequestContext("system-admin"));
        var queued = await session.Queue.Enqueue(definition.Name);

        Assert.Single(session.Catalog.Definitions);
        Assert.Equal(WorkQueueStatus.Unauthorized, queued.QueueOutcome.Status);
    }

    [Fact]
    public void AccessSummaryHasAnyAccessUsesActualSystemOrWorkRights()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["reader"] = Groups("work.read"),
                ["diagnostics"] = Groups("system.diagnostics"),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .ConfigureAuthorization(authorization => authorization.AllowDiagnosticsToGroups("system.diagnostics"))
                .AddWork(
                    WorkDefinition.Create("connect.permission"),
                    SuccessfulWork,
                    configure: null,
                    authorize: authorize => authorize.AllowReadToGroups("work.read")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        Assert.True(system.DescribeAccess(CreateRequestContext("reader")).HasAnyAccess());
        Assert.True(system.DescribeAccess(CreateRequestContext("diagnostics")).HasAnyAccess());
        Assert.False(system.DescribeAccess(CreateRequestContext("unknown")).HasAnyAccess());
    }

    [Fact]
    public void DiagnosticsRequireConfiguredPermission()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["diagnostics"] = Groups("system.diagnostics"),
                ["reader"] = Groups("work.read"),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .ConfigureAuthorization(authorization => authorization.AllowDiagnosticsToGroups("system.diagnostics"))
                .AddWork(WorkDefinition.Create("diagnostics.permission"), SuccessfulWork))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        var diagnosticsSession = system.CreateSession(CreateRequestContext("diagnostics"));
        var deniedSession = system.CreateSession(CreateRequestContext("reader"));

        Assert.True(diagnosticsSession.Diagnostics.Queue.RejectedWorkCount >= 0);
        var exception = Assert.Throws<WorkSystemAccessDeniedException>(() => deniedSession.Diagnostics.Queue);
        Assert.Equal(WorkSystemPermission.ViewDiagnostics, exception.Permission);
    }

    [Fact]
    public async Task PreResolvedAuthorizationSnapshotGroupsAreUsedWhenPresent()
    {
        var definition = PausedDefinition("snapshot.authorization");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider, ThrowingGroupProvider>()
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(
                definition,
                SuccessfulWork,
                configure: null,
                authorize: authorize => authorize.RequireGroups(
                    readGroups: ["snapshot.read"],
                    operateGroups: ["snapshot.operate"])))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        await system.Start();
        var actor = new WorkActor(Id: "snapshot-user");
        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            actor,
            "Authorize with a pre-resolved snapshot.") with
        {
            Authorization = WorkAuthorizationSnapshot.Create(
                actor,
                ["snapshot.read", "snapshot.operate"],
                readableDefinitionIds: null),
        };

        var session = system.CreateSession(requestContext);
        var queued = await session.Queue.Enqueue(definition.Name);

        Assert.Single(session.Catalog.Definitions);
        Assert.True(queued.QueueOutcome.IsAccepted);
    }

    [Fact]
    public async Task KnownAuthenticatedUsersCanOperateWithoutGroupsWhenExplicitlyAllowed()
    {
        var definition = PausedDefinition("known.authenticated.operate");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>()))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(
                definition,
                SuccessfulWork,
                configure: null,
                authorize: authorize => authorize.AllowOperateToKnownAuthenticatedUsers()))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        await system.Start();
        var session = system.CreateSession(CreateKnownAuthenticatedRequestContext("known-user"));

        var queued = await session.Queue.Enqueue(definition.Name);

        Assert.True(queued.QueueOutcome.IsAccepted);
    }

    [Fact]
    public async Task UnknownActorsDoNotQualifyForKnownAuthenticatedUserOperateAccess()
    {
        var definition = PausedDefinition("known.authenticated.denied");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>()))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(
                definition,
                SuccessfulWork,
                configure: null,
                authorize: authorize => authorize.AllowOperateToKnownAuthenticatedUsers()))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        await system.Start();
        var session = system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            actor: WorkActor.Unknown,
            description: "Authenticated but unknown actor.",
            isAuthenticated: true));

        var queued = await session.Queue.Enqueue(definition.Name);

        Assert.Equal(WorkQueueStatus.Unauthorized, queued.QueueOutcome.Status);
    }

    [Fact]
    public void AuthorizationSnapshotFingerprintDependsOnReadableDefinitionsNotGroups()
    {
        var actor = new WorkActor(Id: "snapshot-user");
        var first = WorkAuthorizationSnapshot.Create(
            actor,
            ["billing.read"],
            [new WorkDefinitionId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))]);
        var second = WorkAuthorizationSnapshot.Create(
            actor,
            ["billing.read", "extra.group"],
            [new WorkDefinitionId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))]);
        var third = WorkAuthorizationSnapshot.Create(
            actor,
            ["billing.read"],
            [new WorkDefinitionId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))]);

        Assert.Equal(first.ReadFingerprint, second.ReadFingerprint);
        Assert.NotEqual(first.ReadFingerprint, third.ReadFingerprint);
    }

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private static WorkDefinition PausedDefinition(string name)
        => WorkDefinition.Create(
            name,
            configuration: WorkConfiguration.Default with { Start = WorkStartConfiguration.DoNotStart });

    private static IReadOnlySet<string> Groups(params string[] groups)
        => new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);

    private static WorkRequestContext CreateRequestContext(string actorId)
        => WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor(Id: actorId),
            $"Authorize actor '{actorId}' in tests.");

    private static WorkRequestContext CreateKnownAuthenticatedRequestContext(string actorId)
        => WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor(Id: actorId),
            $"Authorize known authenticated actor '{actorId}' in tests.",
            isAuthenticated: true);

    private sealed class TestGroupProvider(IReadOnlyDictionary<string, IReadOnlySet<string>> groupsByActor) : IWorkAuthorizationGroupProvider
    {
        public IReadOnlySet<string> GetGroups(WorkActor actor, string? systemName)
            => actor.Id is not null && groupsByActor.TryGetValue(actor.Id, out var groups)
                ? groups
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class ThrowingGroupProvider : IWorkAuthorizationGroupProvider
    {
        public IReadOnlySet<string> GetGroups(WorkActor actor, string? systemName)
            => throw new InvalidOperationException("Authorization groups should not be resolved.");
    }

    [WorkMetadata("attributed.authorization", "Authorization")]
    [WorkAuthorization(ReadGroups = new[] { "attr.read" }, OperateGroups = new[] { "attr.operate" })]
    private sealed class AttributedAuthorizationWork : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}

internal static class WorkSystemAuthorizationConfigurationTestExtensions
{
    public static IServiceCollection AddDefaultWorkableSystemForAuthorizationTests(
        this IServiceCollection services,
        Action<IWorkSystemBuilder> configure)
        => global::Workable.WorkableServiceCollectionExtensions.AddWorkableSystem(services, configure);

    public static IServiceCollection AddDefaultWorkableSystemForAuthorizationTests(
        this IServiceCollection services,
        string? name,
        Action<IWorkSystemBuilder> configure)
        => global::Workable.WorkableServiceCollectionExtensions.AddWorkableSystem(services, name, configure);
}

