namespace Workable;
internal sealed class WorkExecutionContext(
    WorkSystemId WorkSystemId,
    string? WorkSystemName,
    WorkerId WorkerId,
    WorkDefinition Definition,
    WorkOrigin Origin,
    WorkerOptions Options,
    WorkConfiguration Configuration,
    IWorkProfiler Profile,
    IServiceProvider Services,
    Func<WorkIdentifier, bool> AddIdentifierCallback) : IWorkExecutionContext
{
    public WorkSystemId WorkSystemId { get; } = WorkSystemId;

    public string? WorkSystemName { get; } = WorkSystemName;

    public WorkerId WorkerId { get; } = WorkerId;

    public WorkDefinition Definition { get; } = Definition;

    public WorkOrigin Origin { get; } = Origin;

    public WorkerOptions Options { get; } = Options;

    public WorkConfiguration Configuration { get; } = Configuration;

    public IWorkProfiler Profile { get; } = Profile;

    public IServiceProvider Services { get; } = Services;

    public bool AddIdentifier(WorkIdentifier identifier)
        => AddIdentifierCallback(identifier);
}
