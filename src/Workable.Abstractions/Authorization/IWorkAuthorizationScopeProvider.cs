namespace Workable;

public interface IWorkAuthorizationScopeProvider
{
    WorkAuthorizationScope GetScope(WorkActor actor, WorkSystemId systemId, string? systemName);
}
