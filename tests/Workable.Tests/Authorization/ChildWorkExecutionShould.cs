using System.Collections;
using Microsoft.Extensions.DependencyInjection;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class ChildWorkExecutionShould
{
    [Fact]
    public async Task RejectMalformedIdentifiersWithoutThrowing()
    {
        var definition = WorkDefinition.Create("delegation.identifier.validation");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(definition, (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
        });

        using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var nullType = await system.Queue.Enqueue(
            definition.Name,
            new WorkInput(null, Identifiers: new HashSet<WorkIdentifier>
            {
                new(null!, "value"),
            }));
        var nullValue = await system.Queue.Enqueue(
            definition.Name,
            new WorkInput(null, Identifiers: new HashSet<WorkIdentifier>
            {
                new("type", null!),
            }));

        Assert.Equal(WorkQueueStatus.Invalid, nullType.QueueOutcome.Status);
        Assert.Equal(WorkQueueStatus.Invalid, nullValue.QueueOutcome.Status);
        Assert.All(
            new[] { nullType, nullValue },
            handle => Assert.Contains(
                handle.QueueOutcome.Messages,
                message => message.Code == "workable.identifier.invalid"));
    }

    [Fact]
    public async Task SnapshotCallerOwnedIdentifiersBeforeCheckingReservedNamespaces()
    {
        var definition = WorkDefinition.Create("delegation.identifier.snapshot");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(definition, (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
        });

        using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var forged = new WorkIdentifier("workflow-run", WorkflowRunId.New().ToString());
        var identifiers = new StatefulIdentifierSet(forged);

        var handle = await system.Queue.Enqueue(
            definition.Name,
            new WorkInput(null, Identifiers: identifiers));
        var worker = await system.Query.Worker(handle.WorkerId!.Value);

        Assert.True(handle.QueueOutcome.IsAccepted);
        Assert.Equal(1, identifiers.EnumerationCount);
        Assert.NotNull(worker);
        Assert.DoesNotContain(forged, worker!.Identifiers);
        Assert.DoesNotContain(forged, worker.Input?.Identifiers ?? new HashSet<WorkIdentifier>());
    }

    [Fact]
    public async Task RejectCallerSuppliedWorkflowRunIdentifiersOnOrdinaryAndDelegatedQueues()
    {
        WorkQueueOutcome? delegatedOutcome = null;
        var child = WorkDefinition.Create("delegation.child.reserved-workflow-run");
        var parent = WorkDefinition.Create("delegation.parent.reserved-workflow-run");
        var services = new ServiceCollection();

        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(child, (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
            builder.AddWork(
                parent,
                async (context, _, cancellationToken) =>
                {
                    var children = context.Services.GetRequiredService<IChildWorkQueueService>();
                    delegatedOutcome = (await children.Enqueue(
                        child.Name,
                        ReservedWorkflowRunInput(),
                        cancellationToken: cancellationToken)).QueueOutcome;
                    return WorkExecutionResult.Success();
                },
                configure: configuration => configuration.AllowChildExecution(child));
        });

        using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var direct = await system.Queue.Enqueue(child.Name, ReservedWorkflowRunInput());
        var parentHandle = await system.Queue.Enqueue(parent.Name);
        await parentHandle.WaitForCompletion();

        Assert.Equal(WorkQueueStatus.Invalid, direct.QueueOutcome.Status);
        Assert.Contains(
            direct.QueueOutcome.Messages,
            message => message.Code == "workable.workflow.identifier.reserved");
        Assert.NotNull(delegatedOutcome);
        Assert.Equal(WorkQueueStatus.Invalid, delegatedOutcome!.Status);
        Assert.Contains(
            delegatedOutcome.Messages,
            message => message.Code == "workable.workflow.identifier.reserved");
    }

    [Fact]
    public async Task PreventExecutorsFromAddingTheReservedWorkflowRunIdentifier()
    {
        bool? identifierAdded = null;
        var definition = WorkDefinition.Create("delegation.reserved-workflow-run.runtime");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                definition,
                (context, _, _) =>
                {
                    identifierAdded = context.AddIdentifier(
                        new WorkIdentifier("workflow-run", WorkflowRunId.New().ToString()));
                    return Task.FromResult(WorkExecutionResult.Success());
                });
        });

        using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var handle = await system.Queue.Enqueue(definition.Name);
        await handle.WaitForCompletion();
        var worker = await system.Query.Worker(handle.WorkerId!.Value);

        Assert.False(identifierAdded);
        Assert.NotNull(worker);
        Assert.DoesNotContain(
            worker!.Identifiers,
            identifier => identifier.Type.Equals("workflow-run", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task PreventExecutorsFromAddingMalformedIdentifiers()
    {
        var results = new List<bool>();
        var definition = WorkDefinition.Create("delegation.malformed-runtime-identifiers");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                definition,
                (context, _, _) =>
                {
                    results.Add(context.AddIdentifier(new WorkIdentifier(null!, "value")));
                    results.Add(context.AddIdentifier(new WorkIdentifier("type", null!)));
                    results.Add(context.AddIdentifier(new WorkIdentifier(" ", "value")));
                    results.Add(context.AddIdentifier(new WorkIdentifier("type", " ")));
                    return Task.FromResult(WorkExecutionResult.Success());
                });
        });

        using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var handle = await system.Queue.Enqueue(definition.Name);
        await handle.WaitForCompletion();
        var worker = await system.Query.Worker(handle.WorkerId!.Value);

        Assert.Equal([false, false, false, false], results);
        Assert.NotNull(worker);
        Assert.Empty(worker!.Identifiers);
    }

    [Fact]
    public async Task ExecuteDeclaredChildWithoutGrantingDirectQueuePermission()
    {
        var childRan = new TaskCompletionSource<WorkRequestContext>(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkQueueOutcome? delegatedOutcome = null;
        var child = WorkDefinition.Create("delegation.child.restricted");
        var parent = WorkDefinition.Create("delegation.parent.allowed");
        var services = new ServiceCollection();

        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(true);
            builder.AddWork(
                child,
                (context, _, _) =>
                {
                    childRan.TrySetResult(context.RequestContext);
                    return Task.FromResult(WorkExecutionResult.Success());
                },
                configure: null,
                authorize: authorization => authorization.AllowQueueToGroups("child.queue"));
            builder.AddWork(
                parent,
                async (context, _, cancellationToken) =>
                {
                    var children = context.Services.GetRequiredService<IChildWorkQueueService>();
                    var handle = await children.Enqueue(child.Name, cancellationToken: cancellationToken);
                    delegatedOutcome = handle.QueueOutcome;
                    if (handle.QueueOutcome.IsAccepted)
                    {
                        await handle.WaitForCompletion(cancellationToken);
                    }

                    return WorkExecutionResult.Success();
                },
                configure: configuration => configuration.AllowChildExecution(child),
                authorize: authorization => authorization.AllowQueueToGroups("parent.queue"));
        });

        using var provider = services.BuildServiceProvider();
        var system = (InMemoryWorkSystem)provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var actor = new WorkActor("parent-operator", "Parent Operator");
        var requestContext = AuthorizedContext(system.Name, actor, "parent.queue");
        var session = await system.CreateSession(requestContext);

        var directChild = await session.Queue.Enqueue(child.Name);
        var parentHandle = await session.Queue.Enqueue(parent.Name);
        var parentCompletion = await parentHandle.WaitForCompletion();
        var childRequestContext = await childRan.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(WorkQueueStatus.Unauthorized, directChild.QueueOutcome.Status);
        Assert.True(parentHandle.QueueOutcome.IsAccepted);
        Assert.True(parentCompletion.IsCompletedSuccessfully);
        Assert.NotNull(delegatedOutcome);
        Assert.True(delegatedOutcome!.IsAccepted);
        Assert.Equal(actor, childRequestContext.Actor);
        Assert.Equal(requestContext.Channel, childRequestContext.Channel);

        var childSnapshot = await system.WorkerOperations.Get(delegatedOutcome.WorkerId!.Value);
        Assert.NotNull(childSnapshot);
        Assert.DoesNotContain(
            childSnapshot!.Identifiers,
            identifier => identifier.Type.Equals("parent-worker", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            childSnapshot.Identifiers,
            identifier => identifier.Type.Equals("parent-work-definition", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RejectUndeclaredAndExpiredChildExecutionScopes()
    {
        IChildWorkQueueService? capturedQueue = null;
        WorkQueueOutcome? undeclaredOutcome = null;
        var declaredChild = WorkDefinition.Create("delegation.child.declared");
        var undeclaredChild = WorkDefinition.Create("delegation.child.undeclared");
        var parent = WorkDefinition.Create("delegation.parent.scoped");
        var services = new ServiceCollection();

        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(declaredChild, (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
            builder.AddWork(undeclaredChild, (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
            builder.AddWork(
                parent,
                async (context, _, cancellationToken) =>
                {
                    capturedQueue = context.Services.GetRequiredService<IChildWorkQueueService>();
                    undeclaredOutcome = (await capturedQueue.Enqueue(
                        undeclaredChild.Name,
                        cancellationToken: cancellationToken)).QueueOutcome;
                    return WorkExecutionResult.Success();
                },
                configure: configuration => configuration.AllowChildExecution(declaredChild));
        });

        using var provider = services.BuildServiceProvider();
        var system = (InMemoryWorkSystem)provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var parentHandle = await system.Queue.Enqueue(parent.Name);
        await parentHandle.WaitForCompletion();

        Assert.NotNull(undeclaredOutcome);
        Assert.Equal(WorkQueueStatus.Invalid, undeclaredOutcome!.Status);
        Assert.Contains(
            undeclaredOutcome.Messages,
            message => message.Code == "workable.child_execution.not_declared");

        Assert.NotNull(capturedQueue);
        var expired = await capturedQueue!.Enqueue(declaredChild.Name);
        Assert.Equal(WorkQueueStatus.Invalid, expired.QueueOutcome.Status);
        Assert.Contains(
            expired.QueueOutcome.Messages,
            message => message.Code == "workable.child_execution.scope_expired");
    }

    [Fact]
    public async Task RequireDeclaredChildrenToExistAndKeepRelationshipsImmutableAtRuntime()
    {
        var missingServices = new ServiceCollection();
        missingServices.AddWorkableSystem(builder => builder.AddWork(
            WorkDefinition.Create("delegation.parent.missing-child"),
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configure: configuration => configuration.AllowChildExecution("delegation.child.missing")));
        using var missingProvider = missingServices.BuildServiceProvider();
        var missingSystem = missingProvider.GetRequiredService<IWorkSystemRegistry>().Default;

        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => missingSystem.Start());
        Assert.Contains("delegation.parent.missing-child -> delegation.child.missing", missing.Message);

        var child = WorkDefinition.Create("delegation.child.immutable");
        var parent = WorkDefinition.Create("delegation.parent.immutable");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(child, (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
            builder.AddWork(
                parent,
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                configure: configuration => configuration.AllowChildExecution(child));
        });
        using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();
        var registeredParent = system.Catalog.TryGet(parent.Name, out var definition)
            ? definition
            : throw new InvalidOperationException("Expected the parent definition.");
        var changedConfiguration = registeredParent.Configuration with
        {
            ChildExecution = registeredParent.Configuration.ChildExecution.AllowAdditional("another.child"),
        };

        var changed = await system.Catalog.Reconfigure(
            registeredParent.Version,
            new WorkDefinitionReconfiguration(Configuration: changedConfiguration));

        Assert.Equal(WorkDefinitionReconfigurationStatus.Invalid, changed.Status);
        Assert.Contains(
            changed.Messages,
            message => message.Code == "workable.configuration.child_execution.definition_scoped");
    }

    [Fact]
    public async Task RejectSelfReferentialChildExecutionRelationships()
    {
        var definition = WorkDefinition.Create("delegation.self-referential");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder => builder.AddWork(
            definition,
            (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
            configure: configuration => configuration.AllowChildExecution(definition)));
        using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => system.Start());

        Assert.Equal(
            "Declared child execution relationships cannot be self-referential. Invalid relationships: " +
            "delegation.self-referential -> delegation.self-referential.",
            exception.Message);
    }

    [Fact]
    public async Task RejectCyclicalChildExecutionRelationships()
    {
        var first = WorkDefinition.Create("delegation.cycle.a");
        var second = WorkDefinition.Create("delegation.cycle.b");
        var third = WorkDefinition.Create("delegation.cycle.c");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.AddWork(
                first,
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                configure: configuration => configuration.AllowChildExecution(second));
            builder.AddWork(
                second,
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                configure: configuration => configuration.AllowChildExecution(third));
            builder.AddWork(
                third,
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                configure: configuration => configuration.AllowChildExecution(first));
        });
        using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => system.Start());

        Assert.Equal(
            "Declared child execution relationships must be acyclic. Cycle detected: " +
            "delegation.cycle.a -> delegation.cycle.b -> delegation.cycle.c -> delegation.cycle.a.",
            exception.Message);
    }

    [Fact]
    public async Task PermitAcyclicChildExecutionGraphsWithSharedDescendants()
    {
        var first = WorkDefinition.Create("delegation.acyclic.a");
        var second = WorkDefinition.Create("delegation.acyclic.b");
        var shared = WorkDefinition.Create("delegation.acyclic.shared");
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                first,
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                configure: configuration => configuration.AllowChildExecution(second, shared));
            builder.AddWork(
                second,
                (_, _, _) => Task.FromResult(WorkExecutionResult.Success()),
                configure: configuration => configuration.AllowChildExecution(shared));
            builder.AddWork(shared, (_, _, _) => Task.FromResult(WorkExecutionResult.Success()));
        });
        using var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;

        await system.Start();

        Assert.True(system.Catalog.IsFrozen);
    }

    [Fact]
    public void FreezeVeryDeepAcyclicChildExecutionGraphsWithoutUsingTheCallStack()
    {
        const int definitionCount = 12_000;
        var names = Enumerable.Range(0, definitionCount)
            .Select(index => $"delegation.deep.{index:D5}")
            .ToArray();
        var registeredWork = new RegisteredWork[definitionCount];
        for (var index = 0; index < registeredWork.Length; index++)
        {
            var childExecution = index + 1 < names.Length
                ? WorkChildExecutionConfiguration.Default.AllowAdditional(names[index + 1])
                : WorkChildExecutionConfiguration.Default;
            var definition = WorkDefinition.Create(
                names[index],
                configuration: WorkConfiguration.Default with { ChildExecution = childExecution });
            registeredWork[index] = new RegisteredWork(definition, _ => new NoopExecutor(), []);
        }

        var catalog = new WorkSystemCatalog(registeredWork, persistenceStoreAvailable: false);

        catalog.Freeze();

        Assert.True(catalog.IsFrozen);
    }

    private static WorkRequestContext AuthorizedContext(
        string? systemName,
        WorkActor actor,
        params string[] groups)
        => WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            actor,
            isAuthenticated: true) with
        {
            Authorization = WorkAuthorizationSnapshot.CreateForSystem(
                systemName,
                actor,
                groups.ToHashSet(StringComparer.OrdinalIgnoreCase),
            readableDefinitionIds: null),
        };

    private sealed class StatefulIdentifierSet(WorkIdentifier forged) : IReadOnlySet<WorkIdentifier>
    {
        private int enumerationCount;

        public int Count => 1;

        public int EnumerationCount => Volatile.Read(ref this.enumerationCount);

        public bool Contains(WorkIdentifier item) => item == forged;

        public IEnumerator<WorkIdentifier> GetEnumerator()
        {
            var enumeration = Interlocked.Increment(ref this.enumerationCount) == 1
                ? Array.Empty<WorkIdentifier>()
                : [forged];
            return ((IEnumerable<WorkIdentifier>)enumeration).GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator() => this.GetEnumerator();

        public bool IsProperSubsetOf(IEnumerable<WorkIdentifier> other) => false;

        public bool IsProperSupersetOf(IEnumerable<WorkIdentifier> other) => false;

        public bool IsSubsetOf(IEnumerable<WorkIdentifier> other) => false;

        public bool IsSupersetOf(IEnumerable<WorkIdentifier> other) => false;

        public bool Overlaps(IEnumerable<WorkIdentifier> other) => other.Contains(forged);

        public bool SetEquals(IEnumerable<WorkIdentifier> other) => other.SequenceEqual([forged]);
    }

    private static WorkInput ReservedWorkflowRunInput()
        => WorkInput.Empty.WithIdentifier(new WorkIdentifier("workflow-run", WorkflowRunId.New().ToString()));

    private sealed class NoopExecutor : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(
            IWorkExecutionContext context,
            WorkInput? input,
            CancellationToken cancellationToken)
            => Task.FromResult(WorkExecutionResult.Success());
    }
}
