namespace Workable;

public interface IWorkSnapshotQueryService : IWorkQueryService
{
    IWorkQueryService BeginRead();
}
