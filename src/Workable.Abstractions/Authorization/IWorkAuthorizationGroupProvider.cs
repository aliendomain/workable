namespace Workable;

public interface IWorkAuthorizationGroupProvider
{
    IReadOnlySet<string> GetGroups(WorkActor actor, string? systemName);
}
