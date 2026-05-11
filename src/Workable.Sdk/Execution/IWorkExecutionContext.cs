namespace Workable;
public interface IWorkExecutionContext
{
    WorkSystemId WorkSystemId { get; }

    string? WorkSystemName { get; }

    WorkerId WorkerId { get; }

    WorkDefinition Definition { get; }

    WorkOrigin Origin { get; }

    WorkerOptions Options { get; }

    WorkConfiguration Configuration { get; }

    IWorkProfiler Profile { get; }

    IServiceProvider Services { get; }

    bool AddIdentifier(WorkIdentifier identifier);
}
