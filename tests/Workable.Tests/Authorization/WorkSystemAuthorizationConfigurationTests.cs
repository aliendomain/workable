using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Reflection;
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
        Assert.Throws<WorkSystemAuthorizationRequiredException>(() => system.Changes);
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
        Assert.NotNull(system.Changes);
        Assert.NotNull(system.Diagnostics);
    }

    [Fact]
    public async Task CreateSessionProvidesInterfacesWhenAuthorizationIsRequired()
    {
        var provider = new ServiceCollection()
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(WorkDefinition.Create("secure"), SuccessfulWork))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        var session = await system.CreateSession(CreateRequestContext("test-user"));

        Assert.NotNull(session.Catalog);
        Assert.NotNull(session.Queue);
        Assert.NotNull(session.Workers);
        Assert.NotNull(session.Query);
        Assert.NotNull(session.Events);
        Assert.NotNull(session.Changes);
    }

    [Fact]
    public async Task AuthorizedSessionFiltersChangeStreamByReadScope()
    {
        var visible = PausedDefinition("visible.change");
        var hidden = PausedDefinition("hidden.change");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
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
        var inMemory = Assert.IsType<InMemoryWorkSystem>(system);
        var readerSession = await system.CreateSession(CreateRequestContext("reader"));
        await using var subscription = readerSession.Changes.Subscribe();
        await using var reader = subscription.Read().GetAsyncEnumerator();

        inMemory.ChangeStream.Publish(
            WorkChangeKey.Worker(WorkerId.New()).ScopeToDefinition(hidden.Name));
        inMemory.ChangeStream.Publish(
            WorkChangeKey.Subject(new WorkSubjectId("tenant", "visible"))
                .ScopeToDefinition(visible.Name));
        inMemory.ChangeStream.Publish(WorkChangeKey.Definition(hidden.Name));
        inMemory.ChangeStream.Publish(new WorkChangeKey((WorkChangeKind)int.MaxValue, "unknown", "unknown"));
        inMemory.ChangeStream.Publish(WorkChangeKey.Definition(visible.Name));

        var scopedChange = await ReadNextChange(reader);
        var definitionChange = await ReadNextChange(reader);
        Assert.Equal(WorkChangeKind.Subject, scopedChange.Key.Kind);
        Assert.Equal(visible.Name, scopedChange.Key.DefinitionName);
        Assert.Equal(WorkChangeKey.Definition(visible.Name), definitionChange.Key);
        var diagnostics = Assert.IsAssignableFrom<IWorkChangeSubscriptionDiagnostics>(subscription)
            .GetDiagnosticsSnapshot();
        Assert.Equal(0, diagnostics.AcceptedChangeCount);
    }

    [Fact]
    public async Task DiagnosticsOnlySessionObservesDiagnosticsChangeKeys()
    {
        var definition = PausedDefinition("diagnostics.change");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["diagnostics"] = Groups("system.diagnostics"),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .ConfigureAuthorization(authorization => authorization.AllowDiagnosticsToGroups("system.diagnostics"))
                .AddWork(definition, SuccessfulWork, configure: null, authorize: authorize =>
                    authorize.AllowReadToGroups("private.read")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        var inMemory = Assert.IsType<InMemoryWorkSystem>(system);
        var session = await system.CreateSession(CreateRequestContext("diagnostics"));
        await using var subscription = session.Changes.Subscribe();
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var expected = WorkChangeKey.Diagnostics("queue");

        inMemory.ChangeStream.Publish(WorkChangeKey.Worker(WorkerId.New()));
        inMemory.ChangeStream.Publish(expected);

        Assert.Equal(expected, (await ReadNextChange(reader)).Key);
    }

    [Fact]
    public async Task ReadAllSessionCanObserveActorChangeKeys()
    {
        var definition = PausedDefinition("actor.change");
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
        var inMemory = Assert.IsType<InMemoryWorkSystem>(system);
        var session = await system.CreateSession(CreateRequestContext("work-admin"));
        await using var subscription = session.Changes.Subscribe();
        await using var reader = subscription.Read().GetAsyncEnumerator();
        var expected = WorkChangeKey.Actor("visible-actor");

        inMemory.ChangeStream.Publish(expected);

        Assert.Equal(expected, (await ReadNextChange(reader)).Key);
    }

    [Fact]
    public async Task AuthorizedSessionReturnsEmptyChangeStreamWithoutWorkOrDiagnosticsAccess()
    {
        var definition = PausedDefinition("private.change");
        var provider = new ServiceCollection()
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .AddWork(definition, SuccessfulWork, configure: null, authorize: authorize => authorize.RequireGroups(
                    readGroups: ["private.read"],
                    operateGroups: ["private.operate"])))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        var session = await system.CreateSession(CreateRequestContext("unknown"));
        await using var subscription = session.Changes.Subscribe();
        await using var reader = subscription.Read().GetAsyncEnumerator();

        Assert.False(await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1)));
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
        var operatorSession = await system.CreateSession(CreateRequestContext("operator"));
        await operatorSession.Queue.Enqueue(visible.Name);
        await operatorSession.Queue.Enqueue(hidden.Name);
        await TestEventually.Until(async () =>
            (await operatorSession.Query.Workers(new WorkerCriteria(Take: 10))).TotalCount == 2);

        var readerSession = await system.CreateSession(CreateRequestContext("reader"));

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
        var session = await system.CreateSession(CreateRequestContext("operator"));

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
        var queued = await (await system.CreateSession(CreateRequestContext("operator"))).Queue.Enqueue(definition.Name);
        var worker = await (await system.CreateSession(CreateRequestContext("operator"))).Query.Worker(
            queued.WorkerId ?? throw new InvalidOperationException("Expected queued worker."));

        var outcome = await (await system.CreateSession(CreateRequestContext("reader"))).Workers.Execute(
            worker?.Version ?? throw new InvalidOperationException("Expected worker."),
            WorkAction.Cancel);

        Assert.Equal(WorkActionStatus.Unauthorized, outcome.Status);
    }

    [Fact]
    public async Task AuthorizedBulkActionsUseAuthoritativeWorkersAndPreserveScopeFilters()
    {
        var allowedBilling = PausedDefinition("bulk.allowed.billing") with { Category = "Billing:Invoices" };
        var allowedEmail = PausedDefinition("bulk.allowed.email") with { Category = "Email" };
        var hiddenBilling = PausedDefinition("bulk.hidden.billing") with { Category = "Billing:Credit" };
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["seeder"] = Groups("work.admin"),
                ["operator"] = Groups("allowed.operate"),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .ConfigureAuthorization(authorization => authorization.WorkAdministrators("work.admin"))
                .AddWork(
                    allowedBilling,
                    SuccessfulWork,
                    configure: null,
                    authorize: authorize => authorize.AllowOperateToGroups("allowed.operate"))
                .AddWork(
                    allowedEmail,
                    SuccessfulWork,
                    configure: null,
                    authorize: authorize => authorize.AllowOperateToGroups("allowed.operate"))
                .AddWork(
                    hiddenBilling,
                    SuccessfulWork,
                    configure: null,
                    authorize: authorize => authorize.AllowOperateToGroups("hidden.operate")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        await system.Start();
        var seeder = await system.CreateSession(CreateRequestContext("seeder"));
        var allowedBillingWorker = await seeder.Queue.Enqueue(allowedBilling.Name);
        var allowedEmailWorker = await seeder.Queue.Enqueue(allowedEmail.Name);
        var hiddenBillingWorker = await seeder.Queue.Enqueue(hiddenBilling.Name);
        var session = await system.CreateSession(CreateRequestContext("operator"));

        var outcome = await session.Workers.ExecuteAll(
            WorkAction.Cancel,
            new WorkerBulkActionFilter("Billing"));

        Assert.Equal(1, outcome.MatchedWorkerCount);
        Assert.Equal(1, outcome.AcceptedCount);
        Assert.Equal(
            WorkerState.Canceled,
            (await seeder.Query.Worker(allowedBillingWorker.WorkerId!.Value))?.State);
        Assert.Equal(
            WorkerState.Queued,
            (await seeder.Query.Worker(allowedEmailWorker.WorkerId!.Value))?.State);
        Assert.Equal(
            WorkerState.Queued,
            (await seeder.Query.Worker(hiddenBillingWorker.WorkerId!.Value))?.State);
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

        var outcome = await (await system.CreateSession(CreateRequestContext("operator"))).Workers.Execute(
            new WorkerVersion(WorkerId.New(), Revision: 1),
            WorkAction.Start);

        Assert.Equal(WorkActionStatus.NotFound, outcome.Status);
    }

    [Fact]
    public async Task UnsecuredSessionDoesNotResolveAuthorizationScope()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider, ThrowingGroupProvider>()
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .RequireAuthorization(false)
                .AddWork(WorkDefinition.Create("open"), SuccessfulWork))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        var session = await system.CreateSession(CreateRequestContext("anyone"));

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

        var session = await system.CreateSession(CreateRequestContext("operator"));
        var queued = await session.Queue.Enqueue(definition.Name);

        Assert.Empty(session.Catalog.Definitions);
        Assert.Equal(WorkQueueStatus.NotFound, queued.QueueOutcome.Status);
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

        var requestContext = CreateRequestContext("operator");
        var access = await system.DescribeAccess(requestContext);
        var session = await system.CreateSession(requestContext);
        var queued = await session.Queue.Enqueue(definition.Name);

        Assert.True(access.CanDiscoverAllWork);
        Assert.Equal(1, access.DiscoverableDefinitionCount);
        Assert.Single(session.Discovery.Definitions);
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

        var session = await system.CreateSession(CreateRequestContext("operator"));
        var registeredDefinition = Assert.Single(session.Catalog.Definitions);
        var queued = await session.Queue.Enqueue(definition.Name);

        Assert.Equal(WorkAuthorizationRegistrationSource.Fluent, registeredDefinition.Authorization.Read.Source);
        Assert.Equal(["explicit.read"], registeredDefinition.Authorization.Read.Groups.OrderBy(group => group).ToArray());
        Assert.True(queued.QueueOutcome.IsAccepted);
    }

    [Fact]
    public async Task AttributeAuthorizationAppearsOnCatalogDefinition()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["reader"] = Groups("attr.read"),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork<AttributedAuthorizationWork>())
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        var definition = Assert.Single((await system.CreateSession(CreateRequestContext("reader"))).Catalog.Definitions);

        Assert.Equal(WorkAuthorizationRegistrationSource.Attribute, definition.Authorization.Discover.Source);
        Assert.Equal(WorkAuthorizationRegistrationSource.Attribute, definition.Authorization.Read.Source);
        Assert.Equal(WorkAuthorizationRegistrationSource.Attribute, definition.Authorization.Operate.Source);
        Assert.Equal(["attr.discover"], definition.Authorization.Discover.Groups.OrderBy(group => group).ToArray());
        Assert.Equal(["attr.read"], definition.Authorization.Read.Groups.OrderBy(group => group).ToArray());
        Assert.Equal(["attr.operate"], definition.Authorization.Operate.Groups.OrderBy(group => group).ToArray());
    }

    [Fact]
    public async Task FluentAuthorizationOverridesAttributeAuthorization()
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

        var definition = Assert.Single((await system.CreateSession(CreateRequestContext("reader"))).Catalog.Definitions);

        Assert.Equal(WorkAuthorizationRegistrationSource.Fluent, definition.Authorization.Read.Source);
        Assert.Equal(WorkAuthorizationRegistrationSource.Fluent, definition.Authorization.Operate.Source);
        Assert.Equal(WorkAuthorizationRegistrationSource.None, definition.Authorization.Discover.Source);
        Assert.Equal(["fluent.read"], definition.Authorization.Read.Groups.OrderBy(group => group).ToArray());
        Assert.Equal(["fluent.operate"], definition.Authorization.Operate.Groups.OrderBy(group => group).ToArray());
    }

    [Fact]
    public async Task FluentAllowReadToGroupsSetsReadAuthorization()
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

        var definition = Assert.Single((await system.CreateSession(CreateRequestContext("reader"))).Catalog.Definitions);

        Assert.Equal(WorkAuthorizationRegistrationSource.Fluent, definition.Authorization.Read.Source);
        Assert.Equal(["allow.read"], definition.Authorization.Read.Groups.OrderBy(group => group).ToArray());
        Assert.Equal(WorkAuthorizationRegistrationSource.None, definition.Authorization.Operate.Source);
        Assert.Empty(definition.Authorization.Operate.Groups);
    }

    [Fact]
    public async Task FluentAllowReadToKnownAuthenticatedUsersExposesSchemaWithoutOperateAccess()
    {
        var inputSchema = WorkSchema.FromType<string>();
        var registeredDefinition = WorkDefinition.Create(
            "allow.read.known.authenticated.definition",
            inputSchema: inputSchema,
            configuration: WorkConfiguration.Default with { Start = WorkStartConfiguration.DoNotStart });
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(
                new Dictionary<string, IReadOnlySet<string>>()))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(
                registeredDefinition,
                SuccessfulWork,
                configure: null,
                authorize: authorize => authorize.AllowReadToKnownAuthenticatedUsers()))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        await system.Start();
        var session = await system.CreateSession(CreateKnownAuthenticatedRequestContext("known-reader"));

        var visibleDefinition = Assert.Single(session.Catalog.Definitions);
        var queued = await session.Queue.Enqueue(registeredDefinition.Name);

        Assert.Equal(inputSchema, visibleDefinition.InputSchema);
        Assert.Equal(WorkAuthorizationRegistrationSource.Fluent, visibleDefinition.Authorization.Read.Source);
        Assert.True(visibleDefinition.Authorization.Read.AllowsKnownAuthenticatedUsers);
        Assert.Equal(WorkAuthorizationRegistrationSource.None, visibleDefinition.Authorization.Operate.Source);
        Assert.Equal(WorkQueueStatus.Unauthorized, queued.QueueOutcome.Status);
    }

    [Fact]
    public async Task DiscoveryOnlyAndOperateAudiencesSeeRedactedDescriptorsWithoutReceivingReadAccess()
    {
        var metadata = new WorkDefinitionMetadata(
            Purpose: "Describe discovery authorization in tests.",
            Capabilities: ["discovery-test"]);
        var inputSchema = WorkSchema.FromType<string>();
        var discoverableDefinition = WorkDefinition.Create(
            "allow.discover.definition",
            description: "Discoverable without retained work access.",
            category: "Discovery",
            inputSchema: inputSchema,
            metadata: metadata,
            configuration: WorkConfiguration.Default with
            {
                Start = WorkStartConfiguration.DoNotStart,
                Invocation = WorkInvocationConfiguration.Allow(WorkInvocationChannel.Mcp),
            });
        var operableDefinition = PausedDefinition("operate.implies.discovery");
        var hiddenDefinition = PausedDefinition("hidden.from.discovery");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(
                new Dictionary<string, IReadOnlySet<string>>
                {
                    ["caller"] = Groups("work.discover", "work.operate"),
                }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .AddWork(
                    discoverableDefinition,
                    SuccessfulWork,
                    configure: null,
                    authorize: authorize => authorize.AllowDiscoverToGroups("work.discover"))
                .AddWork(
                    operableDefinition,
                    SuccessfulWork,
                    configure: null,
                    authorize: authorize => authorize.AllowOperateToGroups("work.operate"))
                .AddWork(
                    hiddenDefinition,
                    SuccessfulWork,
                    configure: null,
                    authorize: authorize => authorize.AllowDiscoverToGroups("other.discover")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        await system.Start();
        var session = await system.CreateSession(CreateRequestContext("caller"));

        var descriptors = session.Discovery.Definitions.OrderBy(definition => definition.Name).ToArray();
        var discoverable = Assert.Single(
            descriptors,
            descriptor => descriptor.Name == discoverableDefinition.Name);
        var discoverOnlyQueue = await session.Queue.Enqueue(discoverableDefinition.Name);
        var operateQueue = await session.Queue.Enqueue(operableDefinition.Name);

        Assert.Equal(2, descriptors.Length);
        Assert.Equal(discoverableDefinition.Description, discoverable.Description);
        Assert.Equal(discoverableDefinition.Category, discoverable.Category);
        Assert.Equal(inputSchema, discoverable.InputSchema);
        Assert.NotSame(metadata, discoverable.Metadata);
        Assert.Equal(metadata.Purpose, discoverable.Metadata?.Purpose);
        Assert.Equal(metadata.Capabilities, discoverable.Metadata?.Capabilities);
        Assert.Empty(session.Catalog.Definitions);
        Assert.Equal(
            [discoverableDefinition.Name],
            session.Discovery.ListInvocableBy(WorkInvocationChannel.Mcp).Select(definition => definition.Name));
        Assert.Empty(session.Discovery.ListInvocableBy(WorkInvocationChannel.SignalR));
        Assert.Equal(WorkQueueStatus.Unauthorized, discoverOnlyQueue.QueueOutcome.Status);
        Assert.True(operateQueue.QueueOutcome.IsAccepted);
    }

    [Fact]
    public async Task KnownAuthenticatedDiscoveryQualifiesForNamedSystemAccess()
    {
        var definition = PausedDefinition("known.authenticated.discovery");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(
                new Dictionary<string, IReadOnlySet<string>>()))
            .AddDefaultWorkableSystemForAuthorizationTests("remote", builder => builder.AddWork(
                definition,
                SuccessfulWork,
                configure: null,
                authorize: authorize => authorize.AllowDiscoverToKnownAuthenticatedUsers()))
            .BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(registry.TryGet("remote", out var namedSystem));
        var system = Assert.IsAssignableFrom<IWorkSystem>(namedSystem);
        var knownContext = CreateKnownAuthenticatedRequestContext("known-discoverer");

        var access = await system.DescribeAccess(knownContext);
        var knownSession = await system.CreateSession(knownContext);
        var unknownSession = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            WorkActor.Unknown,
            isAuthenticated: true));
        var unauthenticatedSession = await system.CreateSession(CreateRequestContext("known-discoverer"));

        Assert.True(access.HasAnyAccess());
        Assert.True(access.CanDiscoverAllWork);
        Assert.Equal(1, access.DiscoverableDefinitionCount);
        Assert.Single(knownSession.Discovery.Definitions);
        Assert.Empty(knownSession.Catalog.Definitions);
        Assert.Empty(unknownSession.Discovery.Definitions);
        Assert.Empty(unauthenticatedSession.Discovery.Definitions);
    }

    [Fact]
    public async Task UnknownOrUnauthenticatedActorsDoNotQualifyForKnownAuthenticatedUserReadAccess()
    {
        var registeredDefinition = PausedDefinition("allow.read.known.authenticated.denied");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(
                new Dictionary<string, IReadOnlySet<string>>()))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(
                registeredDefinition,
                SuccessfulWork,
                configure: null,
                authorize: authorize => authorize.AllowReadToKnownAuthenticatedUsers()))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        var unknownActorSession = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            actor: WorkActor.Unknown,
            description: "Authenticated but unknown actor.",
            isAuthenticated: true));
        var unauthenticatedActorSession = await system.CreateSession(CreateRequestContext("known-reader"));

        Assert.Empty(unknownActorSession.Catalog.Definitions);
        Assert.Empty(unauthenticatedActorSession.Catalog.Definitions);
    }

    [Fact]
    public async Task FluentAllowOperateToGroupsSetsOperateAuthorization()
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
    public async Task FluentAllowReadAndOperateToGroupsCanBeChained()
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

        var definition = Assert.Single((await system.CreateSession(CreateRequestContext("operator"))).Catalog.Definitions);

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

        var session = await system.CreateSession(CreateRequestContext("work-admin"));
        var queued = await session.Queue.Enqueue(definition.Name);

        Assert.Single(session.Catalog.Definitions);
        Assert.True(queued.QueueOutcome.IsAccepted);
    }

    [Fact]
    public void WarnWhenConstrainedOperateGroupsAreShadowedBySystemWideOperateAccess()
    {
        var loggerProvider = new CapturingLoggerProvider();
        var provider = new ServiceCollection()
            .AddLogging(logging => logging.AddProvider(loggerProvider))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .ConfigureAuthorization(authorization => authorization
                    .AllowOperateAllWorkToGroups("operate.all")
                    .WorkAdministrators("work.admin"))
                .AddWork(
                    PausedDefinition("shadowed.operate.definition"),
                    SuccessfulWork,
                    configure: null,
                    authorize: authorize => authorize.AllowOperateToGroups(
                        ["operate.all", "work.admin"],
                        operate => operate.WhenOperatingRequire(_ => false))))
            .BuildServiceProvider();

        _ = provider.GetRequiredService<IWorkSystem>();

        var warning = Assert.Single(loggerProvider.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("shadowed.operate.definition", warning.Message, StringComparison.Ordinal);
        Assert.Contains("AllowOperateAllWorkToGroups(...)", warning.Message, StringComparison.Ordinal);
        Assert.Contains("WorkAdministrators(...)", warning.Message, StringComparison.Ordinal);
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

        var session = await system.CreateSession(CreateRequestContext("system-admin"));
        var queued = await session.Queue.Enqueue(definition.Name);

        Assert.Single(session.Catalog.Definitions);
        Assert.Equal(WorkQueueStatus.Unauthorized, queued.QueueOutcome.Status);
    }

    [Fact]
    public async Task AccessSummaryHasAnyAccessUsesActualSystemOrWorkRights()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["reader"] = Groups("work.read"),
                ["diagnostics"] = Groups("system.diagnostics"),
                ["workflow-reader"] = Groups("workflow.read"),
                ["workflow-operator"] = Groups("workflow.operate"),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .ConfigureAuthorization(authorization => authorization.AllowDiagnosticsToGroups("system.diagnostics"))
                .AddWork(
                    WorkDefinition.Create("connect.permission"),
                    SuccessfulWork,
                    configure: null,
                    authorize: authorize => authorize.AllowReadToGroups("work.read"))
                .AddWorkflow(
                    WorkflowDefinition.Create("connect.workflow"),
                    workflow => workflow.DispatchWork("dispatch", WorkDefinition.Create("connect.permission")),
                    authorize: authorize => authorize
                        .AllowReadToGroups("workflow.read")
                        .AllowOperateToGroups("workflow.operate")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        var workflowReader = await system.DescribeAccess(CreateRequestContext("workflow-reader"));
        var workflowOperator = await system.DescribeAccess(CreateRequestContext("workflow-operator"));

        Assert.True((await system.DescribeAccess(CreateRequestContext("reader"))).HasAnyAccess());
        Assert.True((await system.DescribeAccess(CreateRequestContext("diagnostics"))).HasAnyAccess());
        Assert.True(workflowReader.HasAnyAccess());
        Assert.Equal(1, workflowReader.ReadableWorkflowDefinitionCount);
        Assert.True(workflowOperator.HasAnyAccess());
        Assert.Equal(1, workflowOperator.OperableWorkflowDefinitionCount);
        Assert.False((await system.DescribeAccess(CreateRequestContext("unknown"))).HasAnyAccess());

        var emptyProvider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(
                new Dictionary<string, IReadOnlySet<string>>()))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => { })
            .BuildServiceProvider();
        var emptySystem = emptyProvider.GetRequiredService<IWorkSystem>();
        var emptyAccess = await emptySystem.DescribeAccess(CreateRequestContext("unknown"));

        Assert.False(emptyAccess.CanDiscoverAllWork);
        Assert.False(emptyAccess.HasAnyAccess());
    }

    [Fact]
    public async Task AccessSummaryCountsDiscoverReadOperateAndWorkflowRightsIndependently()
    {
        var workflowWork = WorkDefinition.Create("access.summary.workflow.work");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["mixed-access"] = Groups(
                    "work.discover",
                    "work.read",
                    "work.operate",
                    "workflow.read",
                    "workflow.operate"),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .AddWork(
                    WorkDefinition.Create("access.summary.discover"),
                    SuccessfulWork,
                    configure: null,
                    authorize: authorization => authorization.AllowDiscoverToGroups("work.discover"))
                .AddWork(
                    WorkDefinition.Create("access.summary.read"),
                    SuccessfulWork,
                    configure: null,
                    authorize: authorization => authorization.AllowReadToGroups("work.read"))
                .AddWork(
                    WorkDefinition.Create("access.summary.operate"),
                    SuccessfulWork,
                    configure: null,
                    authorize: authorization => authorization.AllowOperateToGroups("work.operate"))
                .AddWork(WorkDefinition.Create("access.summary.hidden"), SuccessfulWork)
                .AddWork(workflowWork, SuccessfulWork)
                .AddWorkflow(
                    WorkflowDefinition.Create("access.summary.workflow.read"),
                    workflow => workflow.DispatchWork("dispatch", workflowWork),
                    authorize: authorization => authorization.AllowReadToGroups("workflow.read"))
                .AddWorkflow(
                    WorkflowDefinition.Create("access.summary.workflow.operate"),
                    workflow => workflow.DispatchWork("dispatch", workflowWork),
                    authorize: authorization => authorization.AllowOperateToGroups("workflow.operate")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();

        var access = await system.DescribeAccess(CreateRequestContext("mixed-access"));

        Assert.Equal(5, access.TotalDefinitionCount);
        Assert.Equal(3, access.DiscoverableDefinitionCount);
        Assert.Equal(1, access.ReadableDefinitionCount);
        Assert.Equal(1, access.OperableDefinitionCount);
        Assert.Equal(1, access.ReadableWorkflowDefinitionCount);
        Assert.Equal(1, access.OperableWorkflowDefinitionCount);
        Assert.False(access.CanDiscoverAllWork);
        Assert.False(access.CanReadAllWork);
        Assert.False(access.CanOperateAllWork);
    }

    [Fact]
    public async Task DiagnosticsRequireConfiguredPermission()
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

        var diagnosticsSession = await system.CreateSession(CreateRequestContext("diagnostics"));
        var deniedSession = await system.CreateSession(CreateRequestContext("reader"));

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
            Authorization = WorkAuthorizationSnapshot.CreateForSystem(
                systemName: null,
                actor,
                ["snapshot.read", "snapshot.operate"],
                readableDefinitionIds: null),
        };

        var session = await system.CreateSession(requestContext);
        var queued = await session.Queue.Enqueue(definition.Name);

        Assert.Single(session.Catalog.Definitions);
        Assert.True(queued.QueueOutcome.IsAccepted);
    }

    [Fact]
    public async Task FactoryIssuedAuthorizationSnapshotIsReusedByTheSameSystem()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(
                new Dictionary<string, IReadOnlySet<string>>
                {
                    ["reader"] = Groups("snapshot.read"),
                }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(
                WorkDefinition.Create("snapshot.reuse"),
                SuccessfulWork,
                configure: null,
                authorize: authorize => authorize.AllowReadToGroups("snapshot.read")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        var firstSession = Assert.IsType<WorkSystemSession>(
            await system.CreateSession(CreateRequestContext("reader")));
        var issuedSnapshot = Assert.IsType<WorkAuthorizationSnapshot>(
            firstSession.RequestContext.Authorization);

        var secondSession = Assert.IsType<WorkSystemSession>(
            await system.CreateSession(firstSession.RequestContext));

        Assert.Same(issuedSnapshot, secondSession.RequestContext.Authorization);
        Assert.Single(secondSession.Catalog.Definitions);
    }

    [Fact]
    public async Task ClonedAuthorizationSnapshotIsRecomputedBeforeReuse()
    {
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(
                new Dictionary<string, IReadOnlySet<string>>
                {
                    ["reader"] = Groups("snapshot.read"),
                }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(
                WorkDefinition.Create("snapshot.clone"),
                SuccessfulWork,
                configure: null,
                authorize: authorize => authorize.AllowReadToGroups("snapshot.read")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        var firstSession = Assert.IsType<WorkSystemSession>(
            await system.CreateSession(CreateRequestContext("reader")));
        var issuedSnapshot = Assert.IsType<WorkAuthorizationSnapshot>(
            firstSession.RequestContext.Authorization);
        var forgedClone = issuedSnapshot with { ReadFingerprint = "forged" };
        var clonedContext = firstSession.RequestContext with { Authorization = forgedClone };

        var secondSession = Assert.IsType<WorkSystemSession>(
            await system.CreateSession(clonedContext));
        var replacement = Assert.IsType<WorkAuthorizationSnapshot>(
            secondSession.RequestContext.Authorization);

        Assert.NotSame(forgedClone, replacement);
        Assert.Equal(issuedSnapshot.ReadFingerprint, replacement.ReadFingerprint);
        Assert.Single(secondSession.Catalog.Definitions);
    }

    [Fact]
    public async Task CachedAuthorizationProjectionDoesNotSurviveGroupRevocation()
    {
        var groupsByActor = new Dictionary<string, IReadOnlySet<string>>
        {
            ["reader"] = Groups("snapshot.read"),
        };
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(groupsByActor))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(
                WorkDefinition.Create("snapshot.revocation"),
                SuccessfulWork,
                configure: null,
                authorize: authorize => authorize.AllowReadToGroups("snapshot.read")))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        var authorized = await system.CreateSession(CreateRequestContext("reader"));
        Assert.Single(authorized.Catalog.Definitions);

        groupsByActor["reader"] = Groups();
        var revoked = await system.CreateSession(CreateRequestContext("reader"));

        Assert.Empty(revoked.Catalog.Definitions);
        var authorizedSnapshot = Assert.IsType<WorkAuthorizationSnapshot>(
            Assert.IsType<WorkSystemSession>(authorized).RequestContext.Authorization);
        var revokedSnapshot = Assert.IsType<WorkAuthorizationSnapshot>(
            Assert.IsType<WorkSystemSession>(revoked).RequestContext.Authorization);
        Assert.NotEqual(authorizedSnapshot.ReadFingerprint, revokedSnapshot.ReadFingerprint);
    }

    [Fact]
    public async Task AuthorizationSnapshotForDifferentActorIsIgnored()
    {
        var definition = PausedDefinition("snapshot.actor-mismatch");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(
                new Dictionary<string, IReadOnlySet<string>>()))
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
        var requestActor = new WorkActor("request-user");
        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            requestActor) with
        {
            Authorization = WorkAuthorizationSnapshot.CreateForSystem(
                systemName: null,
                new WorkActor("different-user"),
                ["snapshot.read", "snapshot.operate"],
                readableDefinitionIds: null),
        };

        var session = await system.CreateSession(requestContext);
        var queued = await session.Queue.Enqueue(definition.Name);

        Assert.Empty(session.Catalog.Definitions);
        Assert.Equal(WorkQueueStatus.NotFound, queued.QueueOutcome.Status);
    }

    [Fact]
    public async Task AuthorizationSnapshotForDifferentSystemIsReplacedBeforeCreatingSession()
    {
        var definition = PausedDefinition("snapshot.system-mismatch");
        var groupProvider = new SystemAwareTestGroupProvider(new Dictionary<string, IReadOnlySet<string>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["target"] = Groups("target.read", "target.operate"),
        });
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(groupProvider)
            .AddDefaultWorkableSystemForAuthorizationTests("source", _ => { })
            .AddDefaultWorkableSystemForAuthorizationTests("target", builder => builder.AddWork(
                definition,
                SuccessfulWork,
                configure: null,
                authorize: authorize => authorize.RequireGroups(
                    readGroups: ["target.read"],
                    operateGroups: ["target.operate"])))
            .BuildServiceProvider();
        var registry = provider.GetRequiredService<IWorkSystemRegistry>();
        Assert.True(registry.TryGet("target", out var target));
        await target.Start();
        var actor = new WorkActor("shared-context-user");
        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            actor) with
        {
            Authorization = WorkAuthorizationSnapshot.CreateForSystem(
                "source",
                actor,
                ["source.read", "source.operate"],
                readableDefinitionIds: null),
        };

        var session = await target.CreateSession(requestContext);
        var queued = await session.Queue.Enqueue(definition.Name);
        var worker = await session.Query.Worker(
            queued.WorkerId ?? throw new InvalidOperationException("Expected the target system to accept the worker."));

        Assert.True(queued.QueueOutcome.IsAccepted);
        Assert.NotNull(worker);
        Assert.Null(worker.RequestContext.Authorization);
        var effectiveContext = Assert.IsType<WorkSystemSession>(session).RequestContext;
        var replacement = Assert.IsType<WorkAuthorizationSnapshot>(effectiveContext.Authorization);
        Assert.Equal(actor, replacement.Actor);
        Assert.Equal("target", replacement.Scope?.SystemName);
        Assert.Contains("target.read", replacement.Groups);
        Assert.Contains("target.operate", replacement.Groups);
        Assert.DoesNotContain("source.read", replacement.Groups);
        Assert.Contains("target", groupProvider.RequestedSystemNames);
    }

    [Fact]
    public async Task UnscopedAuthorizationSnapshotUsesNormalGroupResolution()
    {
        var definition = PausedDefinition("snapshot.unscoped");
        var groupProvider = new SystemAwareTestGroupProvider(new Dictionary<string, IReadOnlySet<string>>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["target"] = Groups("target.read", "target.operate"),
        });
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(groupProvider)
            .AddDefaultWorkableSystemForAuthorizationTests("target", builder => builder.AddWork(
                definition,
                SuccessfulWork,
                configure: null,
                authorize: authorize => authorize.RequireGroups(
                    readGroups: ["target.read"],
                    operateGroups: ["target.operate"])))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        await system.Start();
        var actor = new WorkActor("legacy-context-user");
        var requestContext = WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            actor) with
        {
            Authorization = WorkAuthorizationSnapshot.CreateForSystem(
                systemName: null,
                actor,
                ["untrusted.snapshot.group"],
                readableDefinitionIds: null) with { Scope = null },
        };

        var session = await system.CreateSession(requestContext);
        var queued = await session.Queue.Enqueue(definition.Name);
        var worker = await session.Query.Worker(
            queued.WorkerId ?? throw new InvalidOperationException("Expected the target system to accept the worker."));

        Assert.True(queued.QueueOutcome.IsAccepted);
        Assert.NotNull(worker);
        Assert.Null(worker.RequestContext.Authorization);
        var effectiveContext = Assert.IsType<WorkSystemSession>(session).RequestContext;
        var replacement = Assert.IsType<WorkAuthorizationSnapshot>(effectiveContext.Authorization);
        Assert.Equal("target", replacement.Scope?.SystemName);
        Assert.Contains("target.read", replacement.Groups);
        Assert.DoesNotContain("untrusted.snapshot.group", replacement.Groups);
        Assert.Contains("target", groupProvider.RequestedSystemNames);
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
        var session = await system.CreateSession(CreateKnownAuthenticatedRequestContext("known-user"));

        var queued = await session.Queue.Enqueue(definition.Name);
        var effectiveContext = Assert.IsType<WorkSystemSession>(session).RequestContext;
        var snapshot = Assert.IsType<WorkAuthorizationSnapshot>(effectiveContext.Authorization);

        Assert.True(queued.QueueOutcome.IsAccepted);
        Assert.True(snapshot.IsAuthenticated);
    }

    [Fact]
    public async Task ControlOnlyStopDoesNotReturnRetainedWorkerDataOutsideReadScope()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(new Dictionary<string, IReadOnlySet<string>>
            {
                ["operator"] = Groups("system.control", "work.read", "work.operate"),
                ["controller"] = Groups("system.control"),
            }))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder
                .RequireAuthorization()
                .UseShutdownGracePeriod(TimeSpan.FromMilliseconds(20))
                .ConfigureAuthorization(authorization => authorization
                    .AllowControlSystemToGroups("system.control"))
                .AddWork(
                    WorkDefinition.Create("secure.shutdown"),
                    async (context, input, cancellationToken) =>
                    {
                        started.TrySetResult();
                        await Task.Delay(Timeout.InfiniteTimeSpan, CancellationToken.None);
                        return WorkExecutionResult.Success();
                    },
                    configure: null,
                    authorize: authorization => authorization.RequireGroups(
                        readGroups: ["work.read"],
                        operateGroups: ["work.operate"])))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        var operatorContext = CreateRequestContext("operator");

        await system.Start(operatorContext);
        var session = await system.CreateSession(operatorContext);
        var handle = await session.Queue.Enqueue("secure.shutdown");
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var stop = await system.Stop(CreateRequestContext("controller"));

        Assert.Empty(stop.CancellationRequestedWorkers);
        Assert.Empty(stop.ForceInterruptedWorkers);
        Assert.Empty(stop.CancellationRequestedWorkerSummaries);
        Assert.Empty(stop.ForceInterruptedWorkerSummaries);
        Assert.Empty(stop.ForceInterruptedWorkerNames);
        Assert.Equal(WorkCompletionStatus.Interrupted, (await handle.WaitForCompletion()).Status);
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
        var session = await system.CreateSession(WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            actor: WorkActor.Unknown,
            description: "Authenticated but unknown actor.",
            isAuthenticated: true));

        var queued = await session.Queue.Enqueue(definition.Name);

        Assert.Equal(WorkQueueStatus.NotFound, queued.QueueOutcome.Status);
    }

    [Fact]
    public async Task CachedAuthorizationProjectionSeparatesAuthenticationState()
    {
        var definition = PausedDefinition("known.authenticated.cache");
        var provider = new ServiceCollection()
            .AddSingleton<IWorkAuthorizationGroupProvider>(new TestGroupProvider(
                new Dictionary<string, IReadOnlySet<string>>()))
            .AddDefaultWorkableSystemForAuthorizationTests(builder => builder.AddWork(
                definition,
                SuccessfulWork,
                configure: null,
                authorize: authorize => authorize.AllowQueueToKnownAuthenticatedUsers()))
            .BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystem>();
        await system.Start();

        var authenticated = await system.CreateSession(CreateKnownAuthenticatedRequestContext("same-user"));
        var authenticatedQueue = await authenticated.Queue.Enqueue(definition.Name);
        var unauthenticated = await system.CreateSession(CreateRequestContext("same-user"));
        var unauthenticatedQueue = await unauthenticated.Queue.Enqueue(definition.Name);

        Assert.True(authenticatedQueue.QueueOutcome.IsAccepted);
        Assert.Equal(WorkQueueStatus.NotFound, unauthenticatedQueue.QueueOutcome.Status);
    }

    [Fact]
    public void AuthorizationSnapshotFingerprintIncludesCompleteProjectionScope()
    {
        var actor = new WorkActor(Id: "snapshot-user");
        var first = WorkAuthorizationSnapshot.CreateForSystem(
            systemName: null,
            actor,
            ["billing.read"],
            [new WorkDefinitionId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))]);
        var second = WorkAuthorizationSnapshot.CreateForSystem(
            systemName: null,
            actor,
            ["billing.read", "extra.group"],
            [new WorkDefinitionId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))]);
        var third = WorkAuthorizationSnapshot.CreateForSystem(
            systemName: null,
            actor,
            ["billing.read"],
            [new WorkDefinitionId(Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"))]);

        Assert.NotEqual(first.ReadFingerprint, second.ReadFingerprint);
        Assert.NotEqual(first.ReadFingerprint, third.ReadFingerprint);

        var otherSystem = WorkAuthorizationSnapshot.CreateForSystem(
            systemName: "another-system",
            actor,
            ["billing.read"],
            [new WorkDefinitionId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))]);
        var workflowReadable = WorkAuthorizationSnapshot.CreateForSystem(
            systemName: null,
            actor,
            ["billing.read"],
            [new WorkDefinitionId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))],
            [new WorkflowDefinitionId(Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc"))]);
        var diagnosticsReadable = WorkAuthorizationSnapshot.CreateForSystem(
            systemName: null,
            actor,
            ["billing.read"],
            [new WorkDefinitionId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))],
            canViewDiagnostics: true);
        var authenticated = WorkAuthorizationSnapshot.CreateForSystem(
            systemName: null,
            actor,
            ["billing.read"],
            [new WorkDefinitionId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))],
            isAuthenticated: true);
        var delimiterInOneGroup = WorkAuthorizationSnapshot.CreateForSystem(
            systemName: null,
            actor,
            ["billing|read"],
            [new WorkDefinitionId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))]);
        var delimiterBetweenGroups = WorkAuthorizationSnapshot.CreateForSystem(
            systemName: null,
            actor,
            ["billing", "read"],
            [new WorkDefinitionId(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"))]);

        Assert.NotEqual(first.ReadFingerprint, otherSystem.ReadFingerprint);
        Assert.NotEqual(first.ReadFingerprint, workflowReadable.ReadFingerprint);
        Assert.NotEqual(first.ReadFingerprint, diagnosticsReadable.ReadFingerprint);
        Assert.NotEqual(first.ReadFingerprint, authenticated.ReadFingerprint);
        Assert.NotEqual(delimiterInOneGroup.ReadFingerprint, delimiterBetweenGroups.ReadFingerprint);
    }

    [Fact]
    public void AuthorizationProjectionCacheKeyComparesEveryIdentityDimension()
    {
        var keyType = typeof(WorkSystemSessionFactory).GetNestedType(
            "AuthorizationProjectionKey",
            BindingFlags.NonPublic)!;
        var comparerType = typeof(WorkSystemSessionFactory).GetNestedType(
            "AuthorizationProjectionKeyComparer",
            BindingFlags.NonPublic)!;
        object Key(IReadOnlySet<string> groups, bool known, bool authenticated)
            => Activator.CreateInstance(
                keyType,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                args: [groups, known, authenticated],
                culture: null)!;
        var comparer = comparerType.GetProperty(
            "Instance",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
        var equals = comparerType.GetMethods()
            .Single(method => method.Name == "Equals" &&
                method.GetParameters() is [{ ParameterType: var first }, { ParameterType: var second }] &&
                first == keyType && second == keyType);
        var baseline = Key(new HashSet<string>(["reader"], StringComparer.OrdinalIgnoreCase), true, true);

        Assert.True(Assert.IsType<bool>(equals.Invoke(comparer, [
            baseline,
            Key(new HashSet<string>(["READER"], StringComparer.OrdinalIgnoreCase), true, true),
        ])));
        Assert.False(Assert.IsType<bool>(equals.Invoke(comparer, [baseline, Key(new HashSet<string>(), false, true)])));
        Assert.False(Assert.IsType<bool>(equals.Invoke(comparer, [baseline, Key(new HashSet<string>(), true, false)])));
        Assert.False(Assert.IsType<bool>(equals.Invoke(comparer, [baseline, Key(new HashSet<string>(), true, true)])));
    }

    [Fact]
    public void AuthorizationSnapshotFingerprintCoversLegacyAndNullProjectionInputs()
    {
        var actor = new WorkActor("legacy-user");
#pragma warning disable CS0618
        var legacy = WorkAuthorizationSnapshot.Create(actor, groups: null, readableDefinitionIds: null);
#pragma warning restore CS0618
        var scoped = WorkAuthorizationSnapshot.CreateForSystem(
            systemName: null,
            actor,
            groups: null,
            readableDefinitionIds: null,
            readableWorkflowDefinitionIds: null);

        Assert.Null(legacy.Scope);
        Assert.NotNull(scoped.Scope);
        Assert.NotEqual(legacy.ReadFingerprint, scoped.ReadFingerprint);

        var normalizeStrings = typeof(WorkAuthorizationSnapshot).GetMethod(
            "NormalizeStrings",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(normalizeStrings);
        Assert.Empty(Assert.IsAssignableFrom<IReadOnlyList<string>>(
            normalizeStrings.Invoke(null, [null])));
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

    private static async Task<WorkChange> ReadNextChange(IAsyncEnumerator<WorkChange> reader)
        => await reader.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5))
            ? reader.Current
            : throw new InvalidOperationException("Expected a change.");

    private static WorkRequestContext CreateKnownAuthenticatedRequestContext(string actorId)
        => WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            new WorkActor(Id: actorId),
            $"Authorize known authenticated actor '{actorId}' in tests.",
            isAuthenticated: true);

    private sealed class TestGroupProvider(IReadOnlyDictionary<string, IReadOnlySet<string>> groupsByActor) : IWorkAuthorizationGroupProvider
    {
        public ValueTask<IReadOnlySet<string>> GetGroups(
            WorkActor actor,
            string? systemName,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlySet<string>>(actor.Id is not null && groupsByActor.TryGetValue(actor.Id, out var groups)
                ? groups
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase));
    }

    private sealed class SystemAwareTestGroupProvider(
        IReadOnlyDictionary<string, IReadOnlySet<string>> groupsBySystem) : IWorkAuthorizationGroupProvider
    {
        public List<string?> RequestedSystemNames { get; } = [];

        public ValueTask<IReadOnlySet<string>> GetGroups(
            WorkActor actor,
            string? systemName,
            CancellationToken cancellationToken = default)
        {
            this.RequestedSystemNames.Add(systemName);
            return ValueTask.FromResult(
                systemName is not null && groupsBySystem.TryGetValue(systemName, out var groups)
                    ? groups
                    : Groups());
        }
    }

    private sealed class ThrowingGroupProvider : IWorkAuthorizationGroupProvider
    {
        public ValueTask<IReadOnlySet<string>> GetGroups(
            WorkActor actor,
            string? systemName,
            CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Authorization groups should not be resolved.");
    }

    [WorkMetadata("attributed.authorization", "Authorization")]
    [WorkAuthorization(
        DiscoverGroups = new[] { "attr.discover" },
        ReadGroups = new[] { "attr.read" },
        OperateGroups = new[] { "attr.operate" })]
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

internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<LogEntry> entries = [];

    public IReadOnlyList<LogEntry> Entries => this.entries;

    public ILogger CreateLogger(string categoryName)
        => new CapturingLogger(categoryName, this.entries);

    public void Dispose()
    {
    }

    internal sealed record LogEntry(string Category, LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger(string category, List<LogEntry> entries) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
            => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            entries.Add(new LogEntry(category, logLevel, formatter(state, exception), exception));
        }
    }

    private sealed class NullScope : IDisposable
    {
        public static NullScope Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
