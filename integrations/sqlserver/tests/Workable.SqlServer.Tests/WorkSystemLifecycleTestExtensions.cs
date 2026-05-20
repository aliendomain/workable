using Workable;

namespace Workable.Tests;

internal static class WorkSystemLifecycleTestExtensions
{
    private static readonly WorkActor TestActor = new(
        Id: "workable.sqlserver.tests",
        Name: "Workable SqlServer Tests");

    public static Task Start(
        this IWorkSystem system,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Start(
            CreateRequestContext("Start Workable system in SQL Server tests."),
            cancellationToken);
    }

    public static Task<WorkSystemStopResult> Stop(
        this IWorkSystem system,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Stop(
            CreateRequestContext("Stop Workable system in SQL Server tests."),
            cancellationToken);
    }

    private static WorkRequestContext CreateRequestContext(string description)
        => WorkRequestContext.Create(
            WorkInvocationChannel.DotNet,
            TestActor,
            description);
}
