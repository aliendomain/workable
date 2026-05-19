using System.Text.Json;

namespace Workable;

public static class WorkableMcpExtensions
{
    private const string JsonSchemaContentTypeSuffix = "+json";

    public static IReadOnlyList<WorkableMcpToolDescriptor> GetMcpToolDescriptors(
        this IWorkSystem system,
        WorkableMcpToolCatalogOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(system);

        options ??= WorkableMcpToolCatalogOptions.Default;
        return [.. system.Catalog.Definitions
            .Where(definition => definition.Configuration.Invocation.Allows(WorkInvocationChannel.Mcp))
            .Select(definition => CreateDescriptor(definition, options))
            .OfType<WorkableMcpToolDescriptor>()
            .OrderBy(descriptor => descriptor.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase)];
    }

    public static async Task<WorkableMcpInvocationResult> InvokeMcpTool(
        this IWorkSystem system,
        string name,
        JsonElement? input = null,
        WorkableMcpInvocationOptions? options = null,
        CancellationToken cancellationToken = default)
        => await InvokeMcpToolCore(system, name, input, options, origin: null, cancellationToken);

    internal static async Task<WorkableMcpInvocationResult> InvokeMcpTool(
        this IWorkSystem system,
        string name,
        JsonElement? input,
        WorkableMcpInvocationOptions? options,
        WorkOrigin origin,
        CancellationToken cancellationToken = default)
        => await InvokeMcpToolCore(system, name, input, options, origin, cancellationToken);

    private static async Task<WorkableMcpInvocationResult> InvokeMcpToolCore(
        IWorkSystem system,
        string name,
        JsonElement? input,
        WorkableMcpInvocationOptions? options,
        WorkOrigin? origin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        options ??= WorkableMcpInvocationOptions.Default;
        if (!system.Catalog.TryGet(name, out var definition))
        {
            var outcome = WorkQueueOutcome.NotFound(name);
            return new WorkableMcpInvocationResult(
                WorkableMcpInvocationStatus.Rejected,
                outcome,
                WorkerId: null,
                Completion: null,
                Output: null,
                outcome.Messages);
        }

        if (!definition.Configuration.Invocation.Allows(WorkInvocationChannel.Mcp))
        {
            var outcome = WorkQueueOutcome.Invalid(
                definition.Id,
                [WorkMessage.Error("workable.invocation.channel_not_allowed", $"Work '{name}' cannot be invoked through MCP.", "invocation.channel")]);
            return new WorkableMcpInvocationResult(
                WorkableMcpInvocationStatus.Rejected,
                outcome,
                WorkerId: null,
                Completion: null,
                Output: null,
                outcome.Messages);
        }

        var workInput = input.HasValue
            ? WorkInput.FromJson(input.Value.GetRawText())
            : WorkInput.Empty;
        var handle = origin is null
            ? await system.Queue.Enqueue(name, workInput, options.WorkerOptions, cancellationToken)
            : await RequiredOriginAwareSystem(system).Enqueue(name, workInput, options.WorkerOptions, origin, cancellationToken);
        if (!handle.QueueOutcome.IsAccepted)
        {
            return new WorkableMcpInvocationResult(
                WorkableMcpInvocationStatus.Rejected,
                handle.QueueOutcome,
                handle.WorkerId,
                Completion: null,
                Output: null,
                handle.QueueOutcome.Messages);
        }

        if (options.Completion == WorkableMcpInvocationCompletion.ReturnAfterAccepted)
        {
            return new WorkableMcpInvocationResult(
                WorkableMcpInvocationStatus.Accepted,
                handle.QueueOutcome,
                handle.WorkerId,
                Completion: null,
                Output: null,
                handle.QueueOutcome.Messages);
        }

        using var timeout = CreateTimeout(options.CompletionTimeout, cancellationToken);
        var completion = await handle.WaitForCompletion(timeout?.Token ?? cancellationToken);
        return new WorkableMcpInvocationResult(
            ToInvocationStatus(completion.Status),
            handle.QueueOutcome,
            handle.WorkerId,
            completion,
            completion.Output,
            completion.Messages);
    }

    private static IOriginAwareWorkSystem RequiredOriginAwareSystem(IWorkSystem system)
        => system as IOriginAwareWorkSystem
            ?? throw new InvalidOperationException("The configured Workable system does not support trusted origin-aware operations.");

    private static WorkableMcpToolDescriptor? CreateDescriptor(
        WorkDefinition definition,
        WorkableMcpToolCatalogOptions options)
    {
        var inputSchema = GetJsonSchema(definition.InputSchema);
        var usesFallbackInputSchema = inputSchema is null;
        if (usesFallbackInputSchema && !options.IncludeDefinitionsWithoutJsonSchema)
        {
            return null;
        }

        return new WorkableMcpToolDescriptor(
            definition.Name,
            definition.Description,
            definition.Id,
            definition.Category,
            inputSchema ?? options.FallbackInputSchemaJson,
            "application/schema+json",
            GetJsonSchema(definition.OutputSchema),
            HasJsonSchema(definition.OutputSchema) ? "application/schema+json" : null,
            usesFallbackInputSchema,
            definition.Metadata);
    }

    private static string? GetJsonSchema(WorkSchema schema)
        => HasJsonSchema(schema) ? schema.JsonSchema : null;

    private static bool HasJsonSchema(WorkSchema schema)
        => !string.IsNullOrWhiteSpace(schema.JsonSchema) &&
            (schema.ContentType.EndsWith(JsonSchemaContentTypeSuffix, StringComparison.OrdinalIgnoreCase) ||
             schema.ContentType.Equals("application/json", StringComparison.OrdinalIgnoreCase) ||
             schema.ContentType.Equals("application/schema+json", StringComparison.OrdinalIgnoreCase));

    private static CancellationTokenSource? CreateTimeout(TimeSpan? timeout, CancellationToken cancellationToken)
    {
        if (timeout is null)
        {
            return null;
        }

        var source = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        source.CancelAfter(timeout.Value);
        return source;
    }

    private static WorkableMcpInvocationStatus ToInvocationStatus(WorkCompletionStatus status)
        => status switch
        {
            WorkCompletionStatus.Completed => WorkableMcpInvocationStatus.Completed,
            WorkCompletionStatus.Interrupted => WorkableMcpInvocationStatus.Interrupted,
            WorkCompletionStatus.Canceled => WorkableMcpInvocationStatus.Canceled,
            WorkCompletionStatus.Failed => WorkableMcpInvocationStatus.Failed,
            _ => WorkableMcpInvocationStatus.Failed,
        };
}
