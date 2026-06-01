using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class EmptyWorkAuthorizationGroupProviderShould
{
    [Fact]
    public void ReturnNoGroupsForAnyActorOrSystem()
    {
        var provider = EmptyWorkAuthorizationGroupProvider.Instance;

        var defaultSystemGroups = provider.GetGroups(new WorkActor("user-1"), systemName: null);
        var namedSystemGroups = provider.GetGroups(new WorkActor("user-2"), "background");

        Assert.Empty(defaultSystemGroups);
        Assert.Empty(namedSystemGroups);
    }

    [Fact]
    public void ReturnIndependentEmptyGroupSets()
    {
        var provider = EmptyWorkAuthorizationGroupProvider.Instance;
        var groups = provider.GetGroups(new WorkActor("user"), systemName: null);

        Assert.True(groups is ISet<string>);
        ((ISet<string>)groups).Add("mutated");

        Assert.Empty(provider.GetGroups(new WorkActor("user"), systemName: null));
    }
}
