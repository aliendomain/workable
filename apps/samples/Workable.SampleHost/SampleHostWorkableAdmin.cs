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

    public static ValueTask<IWorkSystemSession> CreateSession(
        this IWorkSystem system,
        string description)
    {
        ArgumentNullException.ThrowIfNull(system);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        return system.CreateSession(CreateRequestContext(system.Name, description));
    }

    public static WorkRequestContext CreateRequestContext(string? systemName, string description)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        var origin = WorkOrigin.Create(
            WorkInvocationChannel.InProcess,
            Actor);
        var authorization = WorkAuthorizationSnapshot.CreateForSystem(
            systemName,
            Actor,
            Groups,
            readableDefinitionIds: null);
        return new WorkRequestContext(
            origin,
            description,
            Authorization: authorization,
            IsAuthenticated: true);
    }

    public static Task<WorkDispatchResult<object?>> QueueWork(
        this IWorkCommandDispatcher commands,
        string workName,
        WorkInput input,
        string description,
        WorkerOptions? workerOptions = null,
        CancellationToken cancellationToken = default)
        => commands.QueueWork(
            systemName: null,
            workName,
            input,
            description,
            workerOptions,
            cancellationToken);

    public static Task<WorkDispatchResult<object?>> QueueWork(
        this IWorkCommandDispatcher commands,
        string? systemName,
        string workName,
        WorkInput input,
        string description,
        WorkerOptions? workerOptions = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentException.ThrowIfNullOrWhiteSpace(workName);
        ArgumentNullException.ThrowIfNull(input);

        return commands.Dispatch<WorkInput, object?>(
            systemName,
            workName,
            input,
            CreateRequestContext(systemName, description),
            new WorkDispatchOptions(WorkDispatchCompletion.ReturnAfterAccepted, workerOptions),
            cancellationToken);
    }
}
