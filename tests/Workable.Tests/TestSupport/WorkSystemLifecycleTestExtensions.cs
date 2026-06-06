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

        return system.Start(CreateSystemAdministratorRequestContext(), cancellationToken);
    }

    public static Task<WorkSystemStopResult> Stop(
        this IWorkSystem system,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(system);

        return system.Stop(CreateSystemAdministratorRequestContext(), cancellationToken);
    }

    private static WorkRequestContext CreateSystemAdministratorRequestContext()
        => new(
            WorkOrigin.Create(
                WorkInvocationChannel.DotNet,
                TestActor),
            Authorization: WorkAuthorizationSnapshot.Create(
                TestActor,
                [InternalWorkAuthorizationGroups.SystemAdministrator],
                readableDefinitionIds: null));
}
