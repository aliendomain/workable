namespace Workable;

internal sealed class EmptyWorkAuthorizationGroupProvider : IWorkAuthorizationGroupProvider
{
    private static readonly IReadOnlySet<string> EmptyGroups = WorkAuthorizationGroups.Normalize(groups: null);

    public static EmptyWorkAuthorizationGroupProvider Instance { get; } = new();

    private EmptyWorkAuthorizationGroupProvider()
    {
    }

    public ValueTask<IReadOnlySet<string>> GetGroups(
        WorkActor actor,
        string? systemName,
        CancellationToken cancellationToken = default)
        => ValueTask.FromResult(EmptyGroups);
}
