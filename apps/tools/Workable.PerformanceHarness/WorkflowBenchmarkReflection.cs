using System.Reflection;
using Workable;

namespace Workable.PerformanceHarness;

internal static class WorkflowBenchmarkReflection
{
    public static async Task<WorkflowRunStatus> StartAndWaitForCompletion(
        IWorkSystem system,
        string workflowName,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        var handle = StartHandle(system, workflowName, requestContext, cancellationToken);
        var completion = await WaitForCompletion(handle, cancellationToken);
        return (WorkflowRunStatus)RequireProperty(completion, "Status");
    }

    public static Guid Start(
        IWorkSystem system,
        string workflowName,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken = default)
    {
        var runtime = GetWorkflowRuntime(system);
        var handle = runtime.GetType()
            .GetMethod("Start", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.Invoke(runtime, [workflowName, requestContext, cancellationToken])
            ?? throw new InvalidOperationException("Expected workflow runtime start handle.");
        var runId = RequireProperty(handle, "RunId");
        var value = RequireProperty(runId, "Value");
        return (Guid)value;
    }

    public static WorkflowRunStatus? GetStatus(IWorkSystem system, Guid runId)
    {
        var runtime = GetWorkflowRuntime(system);
        var snapshot = runtime.GetType()
            .GetMethod("Get", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.Invoke(runtime, [new WorkflowRunId(runId)]);
        return snapshot is null
            ? null
            : (WorkflowRunStatus)RequireProperty(snapshot, "Status");
    }

    private static object StartHandle(
        IWorkSystem system,
        string workflowName,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        var runtime = GetWorkflowRuntime(system);
        return runtime.GetType()
            .GetMethod("Start", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.Invoke(runtime, [workflowName, requestContext, cancellationToken])
            ?? throw new InvalidOperationException("Expected workflow runtime start handle.");
    }

    private static async Task<object> WaitForCompletion(object handle, CancellationToken cancellationToken)
    {
        var waitMethod = handle.GetType().GetMethod("WaitForCompletion", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Expected workflow handle completion method.");
        var waitTask = (Task)waitMethod.Invoke(handle, [cancellationToken])!;
        await waitTask.WaitAsync(cancellationToken);
        return RequireProperty(waitTask, "Result");
    }

    private static object GetWorkflowRuntime(IWorkSystem system)
        => system.GetType()
            .GetProperty("WorkflowRuntime", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(system)
            ?? throw new InvalidOperationException("Expected internal workflow runtime.");

    private static object RequireProperty(object instance, string propertyName)
        => instance.GetType()
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(instance)
            ?? throw new InvalidOperationException($"Expected property '{propertyName}' on '{instance.GetType().FullName}'.");
}
