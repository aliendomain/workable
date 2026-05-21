namespace Workable.SampleHost;

internal static class SampleHostWorkableAdmin
{
    private static readonly WorkActor Actor = new(
        "sample-host-admin",
        "Sample Host Admin",
        "sample.host.admin@workable.local");

    private static readonly IReadOnlyList<string> Groups =
    [
        SampleFakeAuth.SystemAdministratorGroup,
        SampleFakeAuth.WorkAdministratorGroup,
    ];

    public static IWorkSystemSession CreateSession(
        this IWorkSystem system,
        string description)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        return system.CreateSession(CreateRequestContext(description));
    }

    public static WorkRequestContext CreateRequestContext(string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var origin = WorkOrigin.Create(
            WorkInvocationChannel.DotNet,
            Actor,
            description);
        var authorization = WorkAuthorizationSnapshot.Create(
            Actor,
            Groups,
            readableDefinitionIds: null);
        return new WorkRequestContext(Actor, origin, authorization);
    }
}
