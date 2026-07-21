using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Workable;

internal static class WorkflowDefinitionFingerprint
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public static string Create(RegisteredWorkflow workflow)
    {
        ArgumentNullException.ThrowIfNull(workflow);

        var shape = new WorkflowShape(
            workflow.Definition.Name,
            workflow.Definition.Coordination.IsDurable,
            workflow.Steps.Select(CreateStep).ToArray());
        var json = JsonSerializer.Serialize(shape, JsonOptions);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static WorkflowStepShape CreateStep(WorkflowStepDefinition step)
        => step switch
        {
            DispatchWorkflowStepDefinition dispatch => new WorkflowStepShape(
                dispatch.Name,
                dispatch.Kind,
                dispatch.WorkDefinition.Name,
                null,
                dispatch.InputSource,
                CreateInput(dispatch.Input),
                []),
            DispatchEachWorkflowStepDefinition dispatchEach => new WorkflowStepShape(
                dispatchEach.Name,
                dispatchEach.Kind,
                dispatchEach.WorkDefinition.Name,
                new WorkflowDispatchSourceShape(
                    dispatchEach.SourceStep.StepName,
                    dispatchEach.SourceSelector.JsonPointer,
                    dispatchEach.CanceledChildBehavior),
                null,
                null,
                []),
            ParallelWorkflowStepDefinition parallel => new WorkflowStepShape(
                parallel.Name,
                parallel.Kind,
                null,
                null,
                null,
                null,
                parallel.Steps.Select(CreateStep).ToArray()),
            BranchWorkflowStepDefinition branch => new WorkflowStepShape(
                branch.Name,
                branch.Kind,
                null,
                null,
                null,
                null,
                branch.Steps.Select(CreateStep).ToArray()),
            JoinWorkflowStepDefinition join => new WorkflowStepShape(
                join.Name,
                join.Kind,
                null,
                null,
                null,
                null,
                []),
            _ => new WorkflowStepShape(
                step.Name,
                step.Kind,
                step.GetType().FullName,
                null,
                null,
                null,
                []),
        };

    private static WorkflowInputShape? CreateInput(WorkInput? input)
    {
        if (input is null)
        {
            return null;
        }

        return new WorkflowInputShape(
            input.Json,
            input.ClrType,
            input.ContentType,
            input.SubjectId is { } subjectId
                ? new WorkflowKeyShape(subjectId.Type, subjectId.Value)
                : null,
            input.ConcurrencyKey is { } concurrencyKey
                ? new WorkflowKeyShape(concurrencyKey.Type, concurrencyKey.Value)
                : null,
            input.Identifiers?
                .OrderBy(static identifier => identifier.Type, StringComparer.Ordinal)
                .ThenBy(static identifier => identifier.Value, StringComparer.Ordinal)
                .Select(static identifier => new WorkflowKeyShape(identifier.Type, identifier.Value))
                .ToArray()
            ?? []);
    }

    private sealed record WorkflowShape(
        string DefinitionName,
        bool IsDurable,
        IReadOnlyList<WorkflowStepShape> Steps);

    private sealed record WorkflowStepShape(
        string Name,
        WorkflowStepKind Kind,
        string? WorkDefinitionName,
        WorkflowDispatchSourceShape? Source,
        WorkflowDispatchInputSource? InputSource,
        WorkflowInputShape? Input,
        IReadOnlyList<WorkflowStepShape> Steps);

    private sealed record WorkflowDispatchSourceShape(
        string StepName,
        string? JsonPointer,
        WorkflowCanceledChildBehavior CanceledChildBehavior);

    private sealed record WorkflowInputShape(
        string? Json,
        string? ClrType,
        string ContentType,
        WorkflowKeyShape? SubjectId,
        WorkflowKeyShape? ConcurrencyKey,
        IReadOnlyList<WorkflowKeyShape> Identifiers);

    private sealed record WorkflowKeyShape(
        string Type,
        string Value);
}
