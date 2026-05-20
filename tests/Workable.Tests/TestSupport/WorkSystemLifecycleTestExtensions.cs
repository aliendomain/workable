using Workable;

namespace Workable.Tests;

internal static class WorkSystemLifecycleTestExtensions
{
    private static readonly WorkActor TestActor = new(
        Id: "workable.tests",
        Name: "Workable Tests");

    public static Task Start(
        this IWorkSystem system,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Start(
            CreateSystemAdministratorRequestContext("Start Workable system in tests."),
            cancellationToken);
    }

    public static Task<WorkSystemStopResult> Stop(
        this IWorkSystem system,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Stop(
            CreateSystemAdministratorRequestContext("Stop Workable system in tests."),
            cancellationToken);
    }

    private static WorkRequestContext CreateSystemAdministratorRequestContext(string description)
        => new(
            TestActor,
            WorkOrigin.Create(
                WorkInvocationChannel.DotNet,
                TestActor,
                description),
            WorkAuthorizationSnapshot.Create(
                TestActor,
                [InternalWorkAuthorizationGroups.SystemAdministrator],
                readableDefinitionIds: null));
}
