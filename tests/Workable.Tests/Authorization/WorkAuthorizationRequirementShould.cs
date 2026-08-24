using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class WorkAuthorizationRequirementShould
{
    [Fact]
    public void MatchConfiguredGroupsWithoutDependingOnOrderOrCasing()
    {
        var requirement = WorkAuthorizationRequirement.Create(["first", "TARGET"]);

        Assert.True(requirement.IsSatisfiedBy(Groups("unrelated", "target")));
        Assert.False(requirement.IsSatisfiedBy(Groups("unrelated", "missing")));
        Assert.False(requirement.IsSatisfiedBy(Groups()));
    }

    [Fact]
    public void MatchOnlyKnownAuthenticatedUsersWhenConfiguredForThem()
    {
        var requirement = WorkAuthorizationRequirement.Create(
            allowsKnownAuthenticatedUsers: true);

        Assert.True(requirement.IsSatisfiedBy(Groups(), isKnownAuthenticatedUser: true));
        Assert.False(requirement.IsSatisfiedBy(Groups(), isKnownAuthenticatedUser: false));
    }

    private static IReadOnlySet<string> Groups(params string[] groups)
        => new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);
}
