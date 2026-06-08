using System.Text.Json;

namespace Workable;

/// <summary>
/// Provides MCP-oriented descriptor and invocation helpers for <see cref="IWorkSystemSession"/>.
/// </summary>
public static class WorkableMcpExtensions
{
    private const string JsonSchemaContentTypeSuffix = "+json";

    /// <summary>
    /// Projects MCP-eligible work definitions visible to the current caller into tool descriptors.
    /// </summary>
    /// <param name="session">The authorized work-system session to inspect.</param>
    /// <param name="options">Optional descriptor-projection settings.</param>
    /// <returns>The MCP tool descriptors for work definitions the caller may read and invoke through MCP.</returns>
    public static IReadOnlyList<WorkableMcpToolDescriptor> GetMcpToolDescriptors(
        this IWorkSystemSession session,
        WorkableMcpToolCatalogOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(session);

        options ??= WorkableMcpToolCatalogOptions.Default;
        return [.. session.Catalog.Definitions
            .Where(definition => definition.Configuration.Invocation.Allows(WorkInvocationChannel.Mcp))
            .Select(definition => CreateDescriptor(definition, options))
            .OfType<WorkableMcpToolDescriptor>()
            .OrderBy(descriptor => descriptor.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(descriptor => descriptor.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>
    /// Queues a work definition by Workable work name using MCP-oriented completion semantics.
    /// </summary>
    /// <param name="session">The authorized work-system session that will queue the work.</param>
    /// <param name="name">The original Workable work definition name, not the protocol-safe MCP tool name.</param>
    /// <param name="input">The optional JSON input payload to queue.</param>
    /// <param name="options">Optional MCP invocation behavior and queue-time worker options.</param>
    /// <param name="cancellationToken">A token that cancels queueing or completion waiting.</param>
    /// <returns>The MCP invocation result.</returns>
    public static async Task<WorkableMcpInvocationResult> InvokeMcpTool(
        this IWorkSystemSession session,
        string name,
        JsonElement? input = null,
        WorkableMcpInvocationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        options ??= WorkableMcpInvocationOptions.Default;
        var workInput = input.HasValue
            ? WorkInput.FromJson(input.Value.GetRawText())
            : WorkInput.Empty;
        var handle = await session.Queue.Enqueue(name, workInput, options.WorkerOptions, cancellationToken);
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
            completion.Status switch
            {
                WorkCompletionStatus.Completed => WorkableMcpInvocationStatus.Completed,
                WorkCompletionStatus.Interrupted => WorkableMcpInvocationStatus.Interrupted,
                WorkCompletionStatus.Canceled => WorkableMcpInvocationStatus.Canceled,
                WorkCompletionStatus.Failed => WorkableMcpInvocationStatus.Failed,
                _ => WorkableMcpInvocationStatus.Failed,
            },
            handle.QueueOutcome,
            handle.WorkerId,
            completion,
            completion.Output,
            completion.Messages);
    }

    private static WorkableMcpToolDescriptor? CreateDescriptor(
        WorkDefinition definition,
        WorkableMcpToolCatalogOptions options)
    {
        var inputSchema = HasJsonSchema(definition.InputSchema)
            ? definition.InputSchema.JsonSchema
            : null;
        var usesFallbackInputSchema = inputSchema is null;
        if (usesFallbackInputSchema && !options.IncludeDefinitionsWithoutJsonSchema)
        {
            return null;
        }

        return new WorkableMcpToolDescriptor(
            definition.Name,
            definition.Description,
            definition.Category,
            inputSchema ?? options.FallbackInputSchemaJson,
            "application/schema+json",
            HasJsonSchema(definition.OutputSchema) ? definition.OutputSchema.JsonSchema : null,
            HasJsonSchema(definition.OutputSchema) ? "application/schema+json" : null,
            usesFallbackInputSchema,
            definition.Metadata);
    }

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

}
