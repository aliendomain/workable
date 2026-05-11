namespace Workable;

public interface IStartupWorkSource
{
    Task<IReadOnlyList<StartupWorkRequest>> CreateStartupWork(CancellationToken cancellationToken = default);
}
