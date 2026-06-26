using Workable;

namespace Workable.PerformanceHarness;

internal static class BenchmarkRequestContexts
{
    public static WorkRequestContext CreateOperator(string description = "Run performance benchmark.")
    {
        var actor = new WorkActor(
            Id: "workable.performance.benchmark",
            Name: "Workable Performance Benchmark");
        var origin = WorkOrigin.Create(WorkInvocationChannel.InProcess, actor);
        return new WorkRequestContext(
            origin,
            Description: description,
            Authorization: WorkAuthorizationSnapshot.Create(
                actor,
                [WorkableBenchmarkSystem.OperatorGroup],
                readableDefinitionIds: null),
            IsAuthenticated: true);
    }

    public static WorkRequestContext CreateAnonymous(string description = "Run performance benchmark.")
    {
        var actor = new WorkActor(
            Id: "workable.performance.benchmark",
            Name: "Workable Performance Benchmark");
        return WorkRequestContext.Create(
            WorkInvocationChannel.InProcess,
            actor,
            description);
    }
}
