using Workable;
using System.Reflection;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class WorkOperateAuthorizationConfigurationShould
{
    private static readonly WorkDefinition Definition = WorkDefinition.Create("authorization.coverage");
    private static readonly WorkRequestContext RequestContext = WorkRequestContext.Create(
        WorkInvocationChannel.InProcess,
        new WorkActor("authorization-tester"));

    [Fact]
    public void PreserveGroupAndKnownUserGrantsAndMapEveryWorkerAction()
    {
        var configuration = WorkOperateAuthorizationConfiguration.FromDefinition(
            WorkDefinitionAuthorization.Create(
                operateGroups: ["operators"],
                operateKnownAuthenticatedUsers: true));

        Assert.Equal(Groups("operators"), configuration.Groups);
        Assert.True(configuration.AllowsKnownAuthenticatedUsers);
        Assert.False(configuration.CanAttempt(Groups("operators"), false, WorkOperationPermissions.None));
        Assert.True(configuration.CanAttempt(Groups("operators"), false, WorkOperationPermissions.Queue));
        Assert.True(configuration.CanAttempt(Groups(), true, WorkOperationPermissions.Queue));
        Assert.False(configuration.CanAttempt(Groups(), false, WorkOperationPermissions.Queue));

        foreach (var action in Enum.GetValues<WorkOperateAction>())
        {
            Assert.True(configuration.EvaluateWorkerAction(
                Groups("operators"),
                false,
                Definition,
                "worker-1",
                input: null,
                action,
                RequestContext).IsAllowed);
        }

        var unsupported = Assert.Throws<InvalidOperationException>(() => configuration.EvaluateWorkerAction(
            Groups("operators"),
            false,
            Definition,
            "worker-1",
            input: null,
            (WorkOperateAction)int.MaxValue,
            RequestContext));
        Assert.Contains(int.MaxValue.ToString(), unsupported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CombineGrantDecisionsWithoutBroadeningDeniedOrInvalidOperations()
    {
        var messages = new[] { WorkMessage.Error("authorization.invalid", "Invalid request.") };
        var deny = new WorkOperateRequirementRegistration(
            WorkOperateRequirementTargets.Queueing,
            _ => WorkOperateAuthorizationDecision.Deny());
        var invalid = new WorkOperateRequirementRegistration(
            WorkOperateRequirementTargets.Queueing,
            _ => WorkOperateAuthorizationDecision.Invalid(messages));
        var allow = new WorkOperateRequirementRegistration(
            WorkOperateRequirementTargets.Queueing,
            _ => WorkOperateAuthorizationDecision.Allow());
        var nonApplicable = new WorkOperateRequirementRegistration(
            WorkOperateRequirementTargets.WorkerAction,
            _ => WorkOperateAuthorizationDecision.Deny());
        var context = QueueContext();

        Assert.True(new WorkOperateAuthorizationGrant(Groups("ops"), false, WorkOperationPermissions.Queue, []).Evaluate(context).IsAllowed);
        Assert.True(new WorkOperateAuthorizationGrant(Groups("ops"), false, WorkOperationPermissions.Queue, [nonApplicable]).Evaluate(context).IsAllowed);
        Assert.False(new WorkOperateAuthorizationGrant(Groups("ops"), false, WorkOperationPermissions.Queue, [deny]).Evaluate(context).IsAllowed);
        Assert.True(new WorkOperateAuthorizationGrant(Groups("ops"), false, WorkOperationPermissions.Queue, [invalid]).Evaluate(context).IsInvalid);
        Assert.True(new WorkOperateAuthorizationGrant(Groups("ops"), false, WorkOperationPermissions.Queue, [invalid, allow]).Evaluate(context).IsAllowed);

        var configuration = new WorkOperateAuthorizationConfiguration([
            new WorkOperateAuthorizationGrant(Groups("ops"), false, WorkOperationPermissions.Queue, [invalid]),
            new WorkOperateAuthorizationGrant(Groups("ops"), false, WorkOperationPermissions.Queue, [deny]),
        ]);
        var invalidDecision = configuration.EvaluateQueue(Groups("ops"), false, Definition, null, null, RequestContext);
        var deniedDecision = configuration.EvaluateQueue(Groups("other"), false, Definition, null, null, RequestContext);

        Assert.True(invalidDecision.IsInvalid);
        Assert.Same(messages, invalidDecision.Messages);
        Assert.False(deniedDecision.IsAllowed);
        Assert.False(deniedDecision.IsInvalid);
        Assert.True(WorkOperateAuthorizationConfiguration.None.EvaluateQueue(
            Groups(), false, Definition, null, null, RequestContext).IsAllowed);
    }

    [Fact]
    public void ValidatePermissionsAndMapRequirementSurfacesFailClosed()
    {
        var valid = new WorkOperateAuthorizationGrant(Groups("ops"), false, WorkOperationPermissions.Operate, []);
        WorkOperateAuthorizationConfigurationValidator.ValidateOrThrow([valid]);

        var noPermission = new WorkOperateAuthorizationGrant(Groups("ops"), false, WorkOperationPermissions.None, []);
        var unnamed = Assert.Throws<InvalidOperationException>(() =>
            WorkOperateAuthorizationConfigurationValidator.ValidateOrThrow([noPermission], "   "));
        var unsupported = new WorkOperateAuthorizationGrant(
            Groups("ops"),
            false,
            WorkOperationPermissions.Operate | (WorkOperationPermissions)(1 << 20),
            []);
        var named = Assert.Throws<InvalidOperationException>(() =>
            WorkOperateAuthorizationConfigurationValidator.ValidateOrThrow([unsupported], Definition.Name));

        Assert.StartsWith("Work authorization", unnamed.Message, StringComparison.Ordinal);
        Assert.StartsWith($"Work '{Definition.Name}' authorization", named.Message, StringComparison.Ordinal);
        Assert.Equal(WorkOperateRequirementTargets.Queueing, WorkOperateRequirementSurface.Queueing.ToTargets());
        Assert.Equal(WorkOperateRequirementTargets.WorkerAction, WorkOperateRequirementSurface.WorkerAction.ToTargets());
        Assert.Equal(WorkOperateRequirementTargets.WorkerReconfiguration, WorkOperateRequirementSurface.WorkerReconfiguration.ToTargets());
        Assert.Equal(WorkOperateRequirementTargets.DefinitionReconfiguration, WorkOperateRequirementSurface.DefinitionReconfiguration.ToTargets());
        Assert.Throws<InvalidOperationException>(() => ((WorkOperateRequirementSurface)int.MaxValue).ToTargets());
    }

    [Fact]
    public void EvaluateEveryUntypedRequirementSurfaceForAllowedAndDeniedRequests()
    {
        var allow = false;

        AssertBoth(() => Build(builder => builder.WhenOperatingRequire(_ => allow)), QueueContext(), ref allow);
        AssertBoth(() => Build(builder => builder.WhenQueueingRequire(_ => allow)), QueueContext(), ref allow);
        AssertBoth(() => Build(builder => builder.WhenWorkerActionsRequire(_ => allow)), WorkerActionContext(), ref allow);
        AssertBoth(() => Build(builder => builder.WhenReconfiguringRequire(_ => allow)), WorkerReconfigurationContext(), ref allow);
        AssertBoth(() => Build(builder => builder.WhenReconfiguringRequire(_ => allow)), DefinitionReconfigurationContext(), ref allow);
        AssertBoth(() => Build(builder => builder.WhenWorkerReconfiguringRequire(_ => allow)), WorkerReconfigurationContext(), ref allow);
        AssertBoth(() => Build(builder => builder.WhenDefinitionReconfiguringRequire(_ => allow)), DefinitionReconfigurationContext(), ref allow);
    }

    [Fact]
    public void RejectIncompleteOrMismatchedRequirementContexts()
    {
        var basicGrant = new WorkOperateAuthorizationGrant(Groups("ops"), false, WorkOperationPermissions.Queue, []);
        Assert.False(basicGrant.Allows(WorkOperationPermissions.None));

        var workerAction = Build(builder => builder.WhenWorkerActionsRequire(_ => true));
        Assert.Throws<InvalidOperationException>(() => workerAction.Evaluate(WorkerActionContext() with { Action = null }));
        Assert.Throws<InvalidOperationException>(() => workerAction.Evaluate(WorkerActionContext() with { WorkerId = null }));

        var typedWorkerAction = Build(builder => builder.WhenWorkerActionsRequire<object>(_ => true));
        Assert.Throws<InvalidOperationException>(() => typedWorkerAction.Evaluate(WorkerActionContext() with { Action = null }));
        Assert.Throws<InvalidOperationException>(() => typedWorkerAction.Evaluate(WorkerActionContext() with { WorkerId = null }));

        var workerReconfiguration = Build(builder => builder.WhenWorkerReconfiguringRequire(_ => true));
        Assert.Throws<InvalidOperationException>(() => workerReconfiguration.Evaluate(
            WorkerReconfigurationContext() with { WorkerId = null }));
        Assert.Throws<InvalidOperationException>(() => workerReconfiguration.Evaluate(
            WorkerReconfigurationContext() with { WorkerChanges = null }));

        var typedWorkerReconfiguration = Build(builder => builder.WhenWorkerReconfiguringRequire<object>(_ => true));
        Assert.Throws<InvalidOperationException>(() => typedWorkerReconfiguration.Evaluate(
            WorkerReconfigurationContext() with { WorkerId = null }));
        Assert.Throws<InvalidOperationException>(() => typedWorkerReconfiguration.Evaluate(
            WorkerReconfigurationContext() with { WorkerChanges = null }));

        var definitionReconfiguration = Build(builder => builder.WhenDefinitionReconfiguringRequire(_ => true));
        Assert.Throws<InvalidOperationException>(() => definitionReconfiguration.Evaluate(
            DefinitionReconfigurationContext() with { DefinitionChanges = null }));

        var typedQueue = Build(builder => builder.WhenQueueingRequire<object>(context => context.Input is null));
        Assert.True(typedQueue.Evaluate(QueueContext()).IsAllowed);

        var reconfigurationBuilder = new WorkOperateRequirementBuilder();
        reconfigurationBuilder.WhenReconfiguringRequire(_ => true);
        var mismatchedReconfiguration = new WorkOperateAuthorizationGrant(
            Groups("ops"),
            false,
            WorkOperationPermissions.Operate,
            [reconfigurationBuilder.Build().Single() with { Targets = WorkOperateRequirementTargets.Queueing }]);
        Assert.Throws<InvalidOperationException>(() => mismatchedReconfiguration.Evaluate(QueueContext()));

        var genericParameter = typeof(GenericType<>).GetGenericArguments()[0];
        var described = typeof(WorkOperateRequirementBuilder)
            .GetMethod("DescribeType", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [genericParameter]);
        Assert.Equal(genericParameter.Name, described);
    }

    private static void AssertBoth(
        Func<WorkOperateAuthorizationGrant> createGrant,
        WorkOperateAuthorizationEvaluationContext context,
        ref bool allow)
    {
        allow = false;
        Assert.False(createGrant().Evaluate(context).IsAllowed);
        allow = true;
        Assert.True(createGrant().Evaluate(context).IsAllowed);
    }

    private static WorkOperateAuthorizationGrant Build(Action<WorkOperateRequirementBuilder> configure)
    {
        var builder = new WorkOperateRequirementBuilder();
        configure(builder);
        return new WorkOperateAuthorizationGrant(Groups("ops"), false, WorkOperationPermissions.Operate, builder.Build());
    }

    private static WorkOperateAuthorizationEvaluationContext QueueContext()
        => new(
            Definition,
            RequestContext,
            WorkOperateRequirementSurface.Queueing,
            WorkOperationPermissions.Queue,
            null,
            WorkerOptions.Default,
            null,
            null,
            null,
            null);

    private static WorkOperateAuthorizationEvaluationContext WorkerActionContext()
        => new(
            Definition,
            RequestContext,
            WorkOperateRequirementSurface.WorkerAction,
            WorkOperationPermissions.Start,
            null,
            null,
            "worker-1",
            WorkOperateAction.Start,
            null,
            null);

    private static WorkOperateAuthorizationEvaluationContext WorkerReconfigurationContext()
        => new(
            Definition,
            RequestContext,
            WorkOperateRequirementSurface.WorkerReconfiguration,
            WorkOperationPermissions.ReconfigureWorker,
            null,
            null,
            "worker-1",
            null,
            new WorkWorkerReconfigurationChanges(),
            null);

    private static WorkOperateAuthorizationEvaluationContext DefinitionReconfigurationContext()
        => new(
            Definition,
            RequestContext,
            WorkOperateRequirementSurface.DefinitionReconfiguration,
            WorkOperationPermissions.ReconfigureDefinition,
            null,
            null,
            null,
            null,
            null,
            new WorkDefinitionReconfigurationChanges());

    private static IReadOnlySet<string> Groups(params string[] groups)
        => groups.ToHashSet(StringComparer.OrdinalIgnoreCase);

    private sealed class GenericType<T>;
}
