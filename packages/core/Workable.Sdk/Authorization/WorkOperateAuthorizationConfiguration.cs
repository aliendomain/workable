using System.Text.Json;

namespace Workable;

internal sealed record WorkOperateAuthorizationConfiguration(
    IReadOnlyList<WorkOperateAuthorizationGrant> Grants)
{
    public static WorkOperateAuthorizationConfiguration None { get; } = new([]);

    public IReadOnlySet<string> Groups { get; } =
        Grants.SelectMany(grant => grant.Groups).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public bool AllowsKnownAuthenticatedUsers { get; } =
        Grants.Any(grant => grant.AllowsKnownAuthenticatedUsers);

    public static WorkOperateAuthorizationConfiguration FromDefinition(WorkDefinitionAuthorization authorization)
    {
        ArgumentNullException.ThrowIfNull(authorization);

        var grants = new List<WorkOperateAuthorizationGrant>();
        if (authorization.Operate.Groups.Count > 0)
        {
            grants.Add(new WorkOperateAuthorizationGrant(
                authorization.Operate.Groups,
                false,
                WorkOperationPermissions.Operate,
                []));
        }

        if (authorization.Operate.AllowsKnownAuthenticatedUsers)
        {
            grants.Add(new WorkOperateAuthorizationGrant(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                true,
                WorkOperationPermissions.Operate,
                []));
        }

        return new WorkOperateAuthorizationConfiguration(grants);
    }

    public bool CanAttempt(
        IReadOnlySet<string> groups,
        bool isKnownAuthenticatedUser,
        WorkOperationPermissions permission)
        => permission != WorkOperationPermissions.None &&
            this.Grants.Any(grant =>
                grant.Matches(groups, isKnownAuthenticatedUser) &&
                grant.Allows(permission));

    public WorkOperateAuthorizationDecision EvaluateQueue(
        IReadOnlySet<string> groups,
        bool isKnownAuthenticatedUser,
        WorkDefinition definition,
        WorkInput? input,
        WorkerOptions? options,
        WorkRequestContext requestContext)
        => this.Evaluate(
            groups,
            isKnownAuthenticatedUser,
            new WorkOperateAuthorizationEvaluationContext(
                definition,
                requestContext,
                WorkOperateRequirementSurface.Queueing,
                WorkOperationPermissions.Queue,
                input,
                options,
                null,
                null,
                null,
                null));

    public WorkOperateAuthorizationDecision EvaluateWorkerAction(
        IReadOnlySet<string> groups,
        bool isKnownAuthenticatedUser,
        WorkDefinition definition,
        string workerId,
        WorkInput? input,
        WorkOperateAction action,
        WorkRequestContext requestContext)
        => this.Evaluate(
            groups,
            isKnownAuthenticatedUser,
            new WorkOperateAuthorizationEvaluationContext(
                definition,
                requestContext,
                WorkOperateRequirementSurface.WorkerAction,
                ToPermission(action),
                input,
                null,
                workerId,
                action,
                null,
                null));

    public WorkOperateAuthorizationDecision EvaluateWorkerReconfiguration(
        IReadOnlySet<string> groups,
        bool isKnownAuthenticatedUser,
        WorkDefinition definition,
        string workerId,
        WorkInput? input,
        WorkWorkerReconfigurationChanges changes,
        WorkRequestContext requestContext)
    {
        ArgumentNullException.ThrowIfNull(changes);

        return this.Evaluate(
            groups,
            isKnownAuthenticatedUser,
            new WorkOperateAuthorizationEvaluationContext(
                definition,
                requestContext,
                WorkOperateRequirementSurface.WorkerReconfiguration,
                WorkOperationPermissions.ReconfigureWorker,
                input,
                null,
                workerId,
                null,
                changes,
                null));
    }

    public WorkOperateAuthorizationDecision EvaluateDefinitionReconfiguration(
        IReadOnlySet<string> groups,
        bool isKnownAuthenticatedUser,
        WorkDefinition definition,
        WorkDefinitionReconfigurationChanges changes,
        WorkRequestContext requestContext)
    {
        ArgumentNullException.ThrowIfNull(changes);

        return this.Evaluate(
            groups,
            isKnownAuthenticatedUser,
            new WorkOperateAuthorizationEvaluationContext(
                definition,
                requestContext,
                WorkOperateRequirementSurface.DefinitionReconfiguration,
                WorkOperationPermissions.ReconfigureDefinition,
                null,
                null,
                null,
                null,
                null,
                changes));
    }

    private WorkOperateAuthorizationDecision Evaluate(
        IReadOnlySet<string> groups,
        bool isKnownAuthenticatedUser,
        WorkOperateAuthorizationEvaluationContext context)
    {
        if (this.Grants.Count == 0)
        {
            return WorkOperateAuthorizationDecision.Allow();
        }

        IReadOnlyList<WorkMessage>? invalidMessages = null;
        foreach (var grant in this.Grants)
        {
            if (!grant.Matches(groups, isKnownAuthenticatedUser) || !grant.Allows(context.Permission))
            {
                continue;
            }

            var decision = grant.Evaluate(context);
            if (decision.IsAllowed)
            {
                return decision;
            }

            if (decision.IsInvalid && invalidMessages is null)
            {
                invalidMessages = decision.Messages;
            }
        }

        return invalidMessages is null
            ? WorkOperateAuthorizationDecision.Deny()
            : WorkOperateAuthorizationDecision.Invalid(invalidMessages);
    }

    private static WorkOperationPermissions ToPermission(WorkOperateAction action)
        => action switch
        {
            WorkOperateAction.Start => WorkOperationPermissions.Start,
            WorkOperateAction.Pause => WorkOperationPermissions.Pause,
            WorkOperateAction.Cancel => WorkOperationPermissions.Cancel,
            WorkOperateAction.Push => WorkOperationPermissions.Push,
            WorkOperateAction.Purge => WorkOperationPermissions.Purge,
            _ => throw new InvalidOperationException($"Unsupported worker action '{action}'."),
        };
}

internal sealed record WorkOperateAuthorizationGrant(
    IReadOnlySet<string> Groups,
    bool AllowsKnownAuthenticatedUsers,
    WorkOperationPermissions Permissions,
    IReadOnlyList<WorkOperateRequirementRegistration> Requirements)
{
    public bool HasConstraints => this.Requirements.Count > 0;

    public bool Matches(IReadOnlySet<string> groups, bool isKnownAuthenticatedUser)
        => (this.Groups.Count > 0 && groups.Any(this.Groups.Contains)) ||
            (this.AllowsKnownAuthenticatedUsers && isKnownAuthenticatedUser);

    public bool Allows(WorkOperationPermissions permission)
        => permission != WorkOperationPermissions.None &&
            (this.Permissions & permission) == permission;

    public WorkOperateAuthorizationDecision Evaluate(WorkOperateAuthorizationEvaluationContext context)
    {
        if (this.Requirements.Count == 0)
        {
            return WorkOperateAuthorizationDecision.Allow();
        }

        var applicableRequirementCount = 0;
        IReadOnlyList<WorkMessage>? invalidMessages = null;
        foreach (var requirement in this.Requirements)
        {
            if (!requirement.AppliesTo(context.Surface))
            {
                continue;
            }

            applicableRequirementCount++;
            var decision = requirement.Evaluate(context);
            if (decision.IsAllowed)
            {
                return decision;
            }

            if (decision.IsInvalid && invalidMessages is null)
            {
                invalidMessages = decision.Messages;
            }
        }

        if (applicableRequirementCount == 0)
        {
            return WorkOperateAuthorizationDecision.Allow();
        }

        return invalidMessages is null
            ? WorkOperateAuthorizationDecision.Deny()
            : WorkOperateAuthorizationDecision.Invalid(invalidMessages);
    }
}

internal sealed record WorkOperateRequirementRegistration(
    WorkOperateRequirementTargets Targets,
    Func<WorkOperateAuthorizationEvaluationContext, WorkOperateAuthorizationDecision> Evaluate)
{
    public bool AppliesTo(WorkOperateRequirementSurface surface)
        => (this.Targets & surface.ToTargets()) != 0;
}

[Flags]
internal enum WorkOperateRequirementTargets
{
    None = 0,
    Queueing = 1,
    WorkerAction = 2,
    WorkerReconfiguration = 4,
    DefinitionReconfiguration = 8,
    Reconfiguring = WorkerReconfiguration | DefinitionReconfiguration,
    Operating = Queueing | WorkerAction | Reconfiguring,
}

internal readonly record struct WorkOperateAuthorizationEvaluationContext(
    WorkDefinition Definition,
    WorkRequestContext RequestContext,
    WorkOperateRequirementSurface Surface,
    WorkOperationPermissions Permission,
    WorkInput? RawInput,
    WorkerOptions? QueueOptions,
    string? WorkerId,
    WorkOperateAction? Action,
    WorkWorkerReconfigurationChanges? WorkerChanges,
    WorkDefinitionReconfigurationChanges? DefinitionChanges);

internal readonly record struct WorkOperateAuthorizationDecision(
    bool IsAllowed,
    bool IsInvalid,
    IReadOnlyList<WorkMessage> Messages)
{
    public static WorkOperateAuthorizationDecision Allow()
        => new(true, false, []);

    public static WorkOperateAuthorizationDecision Deny()
        => new(false, false, []);

    public static WorkOperateAuthorizationDecision Invalid(IReadOnlyList<WorkMessage> messages)
        => new(false, true, messages);
}

internal static class WorkOperateAuthorizationConfigurationValidator
{
    public static void ValidateOrThrow(
        IReadOnlyList<WorkOperateAuthorizationGrant> grants,
        string? definitionName = null)
    {
        ArgumentNullException.ThrowIfNull(grants);

        var invalidGrants = grants
            .Where(grant => !IsSupported(grant.Permissions))
            .Select(grant => grant.Permissions.ToString())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (invalidGrants.Count > 0)
        {
            throw new InvalidOperationException(
                $"{DescribeDefinition(definitionName)} configured unsupported work-operation permissions: {string.Join(", ", invalidGrants)}.");
        }
    }

    private static bool IsSupported(WorkOperationPermissions permissions)
        => permissions != WorkOperationPermissions.None &&
            (permissions & ~WorkOperationPermissions.Operate) == 0;

    private static string DescribeDefinition(string? definitionName)
        => string.IsNullOrWhiteSpace(definitionName)
            ? "Work authorization"
            : $"Work '{definitionName}' authorization";
}

internal static class WorkOperateRequirementTargetsExtensions
{
    public static WorkOperateRequirementTargets ToTargets(this WorkOperateRequirementSurface surface)
        => surface switch
        {
            WorkOperateRequirementSurface.Queueing => WorkOperateRequirementTargets.Queueing,
            WorkOperateRequirementSurface.WorkerAction => WorkOperateRequirementTargets.WorkerAction,
            WorkOperateRequirementSurface.WorkerReconfiguration => WorkOperateRequirementTargets.WorkerReconfiguration,
            WorkOperateRequirementSurface.DefinitionReconfiguration => WorkOperateRequirementTargets.DefinitionReconfiguration,
            _ => throw new InvalidOperationException($"Unsupported operate requirement surface '{surface}'."),
        };
}

internal sealed class WorkOperateRequirementBuilder : IWorkOperateRequirementBuilder
{
    private readonly List<WorkOperateRequirementRegistration> requirements = [];

    public IWorkOperateRequirementBuilder WhenOperatingRequire(
        Func<WorkOperateRequirementContext, bool> requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        this.requirements.Add(new WorkOperateRequirementRegistration(
            WorkOperateRequirementTargets.Operating,
            context => requirement(new WorkOperateRequirementContext(
                context.Definition,
                context.RequestContext,
                context.Surface,
                context.RawInput,
                context.Action,
                context.WorkerId,
                context.WorkerChanges,
                context.DefinitionChanges))
                ? WorkOperateAuthorizationDecision.Allow()
                : WorkOperateAuthorizationDecision.Deny()));
        return this;
    }

    public IWorkOperateRequirementBuilder WhenOperatingRequire<TInput>(
        Func<WorkOperateRequirementContext<TInput>, bool> requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        this.requirements.Add(new WorkOperateRequirementRegistration(
            WorkOperateRequirementTargets.Operating,
            context => EvaluateTyped<TInput>(
                context,
                typeof(TInput),
                typedInput => requirement(new WorkOperateRequirementContext<TInput>(
                    context.Definition,
                    context.RequestContext,
                    context.Surface,
                    context.RawInput,
                    typedInput,
                    context.Action,
                    context.WorkerId,
                    context.WorkerChanges,
                    context.DefinitionChanges)))));
        return this;
    }

    public IWorkOperateRequirementBuilder WhenQueueingRequire(
        Func<WorkQueueRequirementContext, bool> requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        this.requirements.Add(new WorkOperateRequirementRegistration(
            WorkOperateRequirementTargets.Queueing,
            context => requirement(new WorkQueueRequirementContext(
                context.Definition,
                context.RequestContext,
                context.RawInput,
                context.QueueOptions))
                ? WorkOperateAuthorizationDecision.Allow()
                : WorkOperateAuthorizationDecision.Deny()));
        return this;
    }

    public IWorkOperateRequirementBuilder WhenQueueingRequire<TInput>(
        Func<WorkQueueRequirementContext<TInput>, bool> requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        this.requirements.Add(new WorkOperateRequirementRegistration(
            WorkOperateRequirementTargets.Queueing,
            context => EvaluateTyped<TInput>(
                context,
                typeof(TInput),
                typedInput => requirement(new WorkQueueRequirementContext<TInput>(
                    context.Definition,
                    context.RequestContext,
                    context.RawInput,
                    context.QueueOptions,
                    typedInput)))));
        return this;
    }

    public IWorkOperateRequirementBuilder WhenWorkerActionsRequire(
        Func<WorkWorkerActionRequirementContext, bool> requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        this.requirements.Add(new WorkOperateRequirementRegistration(
            WorkOperateRequirementTargets.WorkerAction,
            context =>
            {
                var action = context.Action
                    ?? throw new InvalidOperationException("Worker-action requirements require an action.");
                var workerId = context.WorkerId
                    ?? throw new InvalidOperationException("Worker-action requirements require a worker id.");
                return requirement(new WorkWorkerActionRequirementContext(
                    context.Definition,
                    context.RequestContext,
                    workerId,
                    context.RawInput,
                    action))
                    ? WorkOperateAuthorizationDecision.Allow()
                    : WorkOperateAuthorizationDecision.Deny();
            }));
        return this;
    }

    public IWorkOperateRequirementBuilder WhenWorkerActionsRequire<TInput>(
        Func<WorkWorkerActionRequirementContext<TInput>, bool> requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        this.requirements.Add(new WorkOperateRequirementRegistration(
            WorkOperateRequirementTargets.WorkerAction,
            context => EvaluateTyped<TInput>(
                context,
                typeof(TInput),
                typedInput =>
                {
                    var action = context.Action
                        ?? throw new InvalidOperationException("Worker-action requirements require an action.");
                    var workerId = context.WorkerId
                        ?? throw new InvalidOperationException("Worker-action requirements require a worker id.");
                    return requirement(new WorkWorkerActionRequirementContext<TInput>(
                        context.Definition,
                        context.RequestContext,
                        workerId,
                        context.RawInput,
                        action,
                        typedInput));
                })));
        return this;
    }

    public IWorkOperateRequirementBuilder WhenReconfiguringRequire(
        Func<WorkReconfigurationRequirementContext, bool> requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        this.requirements.Add(new WorkOperateRequirementRegistration(
            WorkOperateRequirementTargets.Reconfiguring,
            context => requirement(new WorkReconfigurationRequirementContext(
                context.Definition,
                context.RequestContext,
                ToReconfigurationSurface(context.Surface),
                context.RawInput,
                context.WorkerId,
                context.WorkerChanges,
                context.DefinitionChanges))
                ? WorkOperateAuthorizationDecision.Allow()
                : WorkOperateAuthorizationDecision.Deny()));
        return this;
    }

    public IWorkOperateRequirementBuilder WhenReconfiguringRequire<TInput>(
        Func<WorkReconfigurationRequirementContext<TInput>, bool> requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        this.requirements.Add(new WorkOperateRequirementRegistration(
            WorkOperateRequirementTargets.Reconfiguring,
            context => EvaluateTyped<TInput>(
                context,
                typeof(TInput),
                typedInput => requirement(new WorkReconfigurationRequirementContext<TInput>(
                    context.Definition,
                    context.RequestContext,
                    ToReconfigurationSurface(context.Surface),
                    context.RawInput,
                    typedInput,
                    context.WorkerId,
                    context.WorkerChanges,
                    context.DefinitionChanges)))));
        return this;
    }

    public IWorkOperateRequirementBuilder WhenWorkerReconfiguringRequire(
        Func<WorkWorkerReconfigurationRequirementContext, bool> requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        this.requirements.Add(new WorkOperateRequirementRegistration(
            WorkOperateRequirementTargets.WorkerReconfiguration,
            context =>
            {
                var workerId = context.WorkerId
                    ?? throw new InvalidOperationException("Worker-reconfiguration requirements require a worker id.");
                var changes = context.WorkerChanges
                    ?? throw new InvalidOperationException("Worker-reconfiguration requirements require reconfiguration changes.");
                return requirement(new WorkWorkerReconfigurationRequirementContext(
                    context.Definition,
                    context.RequestContext,
                    workerId,
                    context.RawInput,
                    changes))
                    ? WorkOperateAuthorizationDecision.Allow()
                    : WorkOperateAuthorizationDecision.Deny();
            }));
        return this;
    }

    public IWorkOperateRequirementBuilder WhenWorkerReconfiguringRequire<TInput>(
        Func<WorkWorkerReconfigurationRequirementContext<TInput>, bool> requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        this.requirements.Add(new WorkOperateRequirementRegistration(
            WorkOperateRequirementTargets.WorkerReconfiguration,
            context => EvaluateTyped<TInput>(
                context,
                typeof(TInput),
                typedInput =>
                {
                    var workerId = context.WorkerId
                        ?? throw new InvalidOperationException("Worker-reconfiguration requirements require a worker id.");
                    var changes = context.WorkerChanges
                        ?? throw new InvalidOperationException("Worker-reconfiguration requirements require reconfiguration changes.");
                    return requirement(new WorkWorkerReconfigurationRequirementContext<TInput>(
                        context.Definition,
                        context.RequestContext,
                        workerId,
                        context.RawInput,
                        changes,
                        typedInput));
                })));
        return this;
    }

    public IWorkOperateRequirementBuilder WhenDefinitionReconfiguringRequire(
        Func<WorkDefinitionReconfigurationRequirementContext, bool> requirement)
    {
        ArgumentNullException.ThrowIfNull(requirement);

        this.requirements.Add(new WorkOperateRequirementRegistration(
            WorkOperateRequirementTargets.DefinitionReconfiguration,
            context =>
            {
                var changes = context.DefinitionChanges
                    ?? throw new InvalidOperationException("Definition-reconfiguration requirements require reconfiguration changes.");
                return requirement(new WorkDefinitionReconfigurationRequirementContext(
                    context.Definition,
                    context.RequestContext,
                    changes))
                    ? WorkOperateAuthorizationDecision.Allow()
                    : WorkOperateAuthorizationDecision.Deny();
            }));
        return this;
    }

    internal IReadOnlyList<WorkOperateRequirementRegistration> Build()
        => [.. this.requirements];

    private static WorkOperateAuthorizationDecision EvaluateTyped<TInput>(
        WorkOperateAuthorizationEvaluationContext context,
        Type inputType,
        Func<TInput?, bool> requirement)
    {
        try
        {
            var typedInput = context.RawInput is null
                ? default
                : context.RawInput.ToValue<TInput>(WorkData.DefaultJsonOptions);
            return requirement(typedInput)
                ? WorkOperateAuthorizationDecision.Allow()
                : WorkOperateAuthorizationDecision.Deny();
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException)
        {
            var messages = context.Surface switch
            {
                WorkOperateRequirementSurface.Queueing => InvalidQueueMessages(context.Definition, inputType),
                WorkOperateRequirementSurface.WorkerAction => InvalidWorkerActionMessages(
                    context.WorkerId ?? throw new InvalidOperationException("Worker-action requirements require a worker id."),
                    inputType),
                WorkOperateRequirementSurface.WorkerReconfiguration => InvalidWorkerReconfigurationMessages(
                    context.WorkerId ?? throw new InvalidOperationException("Worker-reconfiguration requirements require a worker id."),
                    inputType),
                _ => throw new InvalidOperationException($"Operate requirement surface '{context.Surface}' does not support typed input deserialization failures."),
            };
            return WorkOperateAuthorizationDecision.Invalid(messages);
        }
    }

    private static IReadOnlyList<WorkMessage> InvalidQueueMessages(
        WorkDefinition definition,
        Type inputType)
        =>
        [
            WorkMessage.Error(
                "workable.authorization.operate_requirement_input_invalid",
                $"Work '{definition.Name}' could not evaluate its operate requirement because the queued input could not be deserialized as '{DescribeType(inputType)}'.",
                "input"),
        ];

    private static IReadOnlyList<WorkMessage> InvalidWorkerActionMessages(
        string workerId,
        Type inputType)
        =>
        [
            WorkMessage.Error(
                "workable.authorization.operate_requirement_input_invalid",
                $"Worker '{workerId}' could not evaluate its operate requirement because the retained input could not be deserialized as '{DescribeType(inputType)}'.",
                "worker.input"),
        ];

    private static IReadOnlyList<WorkMessage> InvalidWorkerReconfigurationMessages(
        string workerId,
        Type inputType)
        =>
        [
            WorkMessage.Error(
                "workable.authorization.operate_requirement_input_invalid",
                $"Worker '{workerId}' could not evaluate its reconfiguration requirement because the retained input could not be deserialized as '{DescribeType(inputType)}'.",
                "worker.input"),
        ];

    private static WorkReconfigurationRequirementSurface ToReconfigurationSurface(WorkOperateRequirementSurface surface)
        => surface switch
        {
            WorkOperateRequirementSurface.WorkerReconfiguration => WorkReconfigurationRequirementSurface.Worker,
            WorkOperateRequirementSurface.DefinitionReconfiguration => WorkReconfigurationRequirementSurface.Definition,
            _ => throw new InvalidOperationException($"Operate requirement surface '{surface}' is not a reconfiguration surface."),
        };

    private static string DescribeType(Type type)
        => type.FullName ?? type.Name;
}
