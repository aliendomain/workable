namespace Workable;

public interface IWorkDefinitionSource
{
    Task DefineWork(IWorkDefinitionBuilder builder, CancellationToken cancellationToken = default);
}
