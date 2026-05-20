namespace Workable;

internal sealed class DenyAllWorkAuthorizationScopeProvider : IWorkAuthorizationScopeProvider
{
    public static DenyAllWorkAuthorizationScopeProvider Instance { get; } = new();

    private DenyAllWorkAuthorizationScopeProvider()
    {
    }

    public WorkAuthorizationScope GetScope(WorkActor actor, WorkSystemId systemId, string? systemName)
        => WorkAuthorizationScope.Empty;
}
