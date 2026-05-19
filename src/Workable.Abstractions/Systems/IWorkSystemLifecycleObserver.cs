namespace Workable;

public interface IWorkSystemLifecycleObserver
{
    Task SystemStopping(
        IWorkSystem system,
        WorkOrigin origin,
        CancellationToken cancellationToken = default);
}
