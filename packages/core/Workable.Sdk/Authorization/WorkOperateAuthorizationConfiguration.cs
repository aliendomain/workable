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
                []));
        }

        if (authorization.Operate.AllowsKnownAuthenticatedUsers)
        {
            grants.Add(new WorkOperateAuthorizationGrant(
                new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                true,
                []));
        }

        return new WorkOperateAuthorizationConfiguration(grants);
    }

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
                input,
                options,
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
                input,
                null,
                workerId,
                action));

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
            if (!grant.Matches(groups, isKnownAuthenticatedUser))
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
}

internal sealed record WorkOperateAuthorizationGrant(
    IReadOnlySet<string> Groups,
    bool AllowsKnownAuthenticatedUsers,
    IReadOnlyList<WorkOperateRequirementRegistration> Requirements)
{
    public bool HasConstraints => this.Requirements.Count > 0;

    public bool Matches(IReadOnlySet<string> groups, bool isKnownAuthenticatedUser)
        => (this.Groups.Count > 0 && groups.Any(this.Groups.Contains)) ||
            (this.AllowsKnownAuthenticatedUsers && isKnownAuthenticatedUser);

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
    Operating = Queueing | WorkerAction,
}

internal readonly record struct WorkOperateAuthorizationEvaluationContext(
    WorkDefinition Definition,
    WorkRequestContext RequestContext,
    WorkOperateRequirementSurface Surface,
    WorkInput? RawInput,
    WorkerOptions? QueueOptions,
    string? WorkerId,
    WorkOperateAction? Action);

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

        var duplicateGroups = grants
            .SelectMany(grant => grant.Groups)
            .GroupBy(group => group, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(group => group, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (duplicateGroups.Count > 0)
        {
            throw new InvalidOperationException(
                $"{DescribeDefinition(definitionName)} cannot configure multiple work-level operate grants for the same groups. Duplicate groups: {string.Join(", ", duplicateGroups)}.");
        }

        var knownAuthenticatedGrantCount = grants.Count(grant => grant.AllowsKnownAuthenticatedUsers);
        if (knownAuthenticatedGrantCount > 1)
        {
            throw new InvalidOperationException(
                $"{DescribeDefinition(definitionName)} can configure only one known-authenticated operate grant.");
        }
    }

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
            _ => WorkOperateRequirementTargets.WorkerAction,
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
                    context.WorkerId))
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
                    context.WorkerId)))));
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
            var messages = context.Surface == WorkOperateRequirementSurface.Queueing
                ? InvalidQueueMessages(context.Definition, inputType)
                : InvalidWorkerActionMessages(
                    context.WorkerId ?? throw new InvalidOperationException("Worker-action requirements require a worker id."),
                    inputType);
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

    private static string DescribeType(Type type)
        => type.FullName ?? type.Name;
}
