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
        var handle = GetStartMethod(runtime)
            .Invoke(runtime, [workflowName, requestContext, cancellationToken])
            ?? throw new InvalidOperationException("Expected workflow runtime start handle.");
        EnsureAcceptedStart(workflowName, handle);
        var runId = RequireProperty(handle, "RunId");
        var value = RequireProperty(runId, "Value");
        return (Guid)value;
    }

    public static WorkflowRunStatus? GetStatus(IWorkSystem system, Guid runId)
    {
        var snapshot = GetSnapshot(system, runId);
        return snapshot is null
            ? null
            : (WorkflowRunStatus)RequireProperty(snapshot, "Status");
    }

    public static string DescribeRuns(IWorkSystem system, IEnumerable<Guid> runIds)
    {
        var descriptions = runIds
            .Take(5)
            .Select(runId =>
            {
                var snapshot = GetSnapshot(system, runId);
                if (snapshot is null)
                {
                    return $"{runId:D}: missing";
                }

                var status = RequireProperty(snapshot, "Status");
                var messages = FormatMessages(snapshot);
                return string.IsNullOrWhiteSpace(messages)
                    ? $"{runId:D}: {status}"
                    : $"{runId:D}: {status} ({messages})";
            });
        return $"Workflow status sample: {string.Join("; ", descriptions)}.";
    }

    private static object StartHandle(
        IWorkSystem system,
        string workflowName,
        WorkRequestContext requestContext,
        CancellationToken cancellationToken)
    {
        var runtime = GetWorkflowRuntime(system);
        var handle = GetStartMethod(runtime)
            .Invoke(runtime, [workflowName, requestContext, cancellationToken])
            ?? throw new InvalidOperationException("Expected workflow runtime start handle.");
        EnsureAcceptedStart(workflowName, handle);
        return handle;
    }

    private static MethodInfo GetStartMethod(object runtime)
        => runtime.GetType()
            .GetMethod(
                "Start",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: [typeof(string), typeof(WorkRequestContext), typeof(CancellationToken)],
                modifiers: null)
            ?? throw new InvalidOperationException("Expected workflow runtime start method.");

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

    private static object? GetSnapshot(IWorkSystem system, Guid runId)
    {
        var runtime = GetWorkflowRuntime(system);
        return runtime.GetType()
            .GetMethod("Get", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.Invoke(runtime, [new WorkflowRunId(runId)]);
    }

    private static void EnsureAcceptedStart(string workflowName, object handle)
    {
        var startOutcome = RequireProperty(handle, "StartOutcome");
        if ((bool)RequireProperty(startOutcome, "IsAccepted"))
        {
            return;
        }

        var status = RequireProperty(startOutcome, "Status");
        var messages = RequireProperty(startOutcome, "Messages");
        throw new InvalidOperationException(
            $"Workflow '{workflowName}' start was rejected with status '{status}'. {messages}");
    }

    private static string FormatMessages(object snapshot)
    {
        var messages = EnumerateMessages(RequireProperty(snapshot, "Messages")).ToList();
        var steps = RequireProperty(snapshot, "Steps");
        if (steps is System.Collections.IEnumerable stepItems)
        {
            foreach (var step in stepItems.Cast<object>())
            {
                messages.AddRange(EnumerateMessages(RequireProperty(step, "Messages")));
            }
        }

        return string.Join("; ", messages.Take(5));
    }

    private static IEnumerable<string> EnumerateMessages(object messages)
    {
        if (messages is not System.Collections.IEnumerable items)
        {
            yield break;
        }

        foreach (var item in items.Cast<object>())
        {
            var code = RequireProperty(item, "Code");
            var text = RequireProperty(item, "Text");
            yield return $"{code}: {text}";
        }
    }

    private static object RequireProperty(object instance, string propertyName)
        => instance.GetType()
            .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(instance)
            ?? throw new InvalidOperationException($"Expected property '{propertyName}' on '{instance.GetType().FullName}'.");
}
