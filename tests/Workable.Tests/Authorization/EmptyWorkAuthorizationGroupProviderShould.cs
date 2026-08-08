using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class EmptyWorkAuthorizationGroupProviderShould
{
    [Fact]
    public async Task ReturnNoGroupsForAnyActorOrSystem()
    {
        var provider = EmptyWorkAuthorizationGroupProvider.Instance;

        var defaultSystemGroups = await provider.GetGroups(new WorkActor("user-1"), systemName: null);
        var namedSystemGroups = await provider.GetGroups(new WorkActor("user-2"), "background");

        Assert.Empty(defaultSystemGroups);
        Assert.Empty(namedSystemGroups);
    }

    [Fact]
    public async Task ReturnImmutableSharedEmptyGroupSet()
    {
        var provider = EmptyWorkAuthorizationGroupProvider.Instance;
        var groups = await provider.GetGroups(new WorkActor("user"), systemName: null);

        Assert.Throws<NotSupportedException>(() => ((ISet<string>)groups).Add("mutated"));
        Assert.Same(groups, await provider.GetGroups(new WorkActor("user"), systemName: null));
        Assert.Empty(await provider.GetGroups(new WorkActor("user"), systemName: null));
    }
}
