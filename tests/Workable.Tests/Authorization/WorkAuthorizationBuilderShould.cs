using Workable;

namespace Workable.Tests;

[Trait("Category", "Authorization")]
public sealed class WorkAuthorizationBuilderShould
{
    [Fact]
    public void BuildFluentAuthorizationRequirements()
    {
        var builder = new WorkAuthorizationBuilder();

        var returned = builder.RequireGroups(
            readGroups: ["read.initial"],
            operateGroups: ["operate.initial"]);
        builder.AllowDiscoverToGroups("discover.final");
        builder.AllowReadToGroups("read.final");
        builder.AllowOperateToGroups("operate.final");

        var authorization = builder.Build();

        Assert.Same(builder, returned);
        Assert.Equal(["discover.final"], authorization.Discover.Groups);
        Assert.Equal(["read.final"], authorization.Read.Groups);
        Assert.Equal(["operate.final"], authorization.Operate.Groups);
        Assert.Equal(WorkAuthorizationRegistrationSource.Fluent, authorization.Read.Source);
        Assert.Equal(WorkAuthorizationRegistrationSource.Fluent, authorization.Operate.Source);
        Assert.Equal(WorkAuthorizationRegistrationSource.Fluent, authorization.Discover.Source);
    }

    [Fact]
    public void PreservePreviouslyConfiguredSideWhenOnlyReadOrOperateIsChanged()
    {
        var builder = new WorkAuthorizationBuilder();

        builder.RequireGroups(
            readGroups: ["read.initial"],
            operateGroups: ["operate.initial"]);
        builder.AllowReadToGroups("read.final");

        var afterReadChange = builder.Build();

        builder.AllowOperateToGroups("operate.final");
        var afterOperateChange = builder.Build();

        Assert.Equal(["read.final"], afterReadChange.Read.Groups);
        Assert.Equal(["operate.initial"], afterReadChange.Operate.Groups);
        Assert.Equal(["read.final"], afterOperateChange.Read.Groups);
        Assert.Equal(["operate.final"], afterOperateChange.Operate.Groups);
    }

    [Fact]
    public void AllowOperateToKnownAuthenticatedUsersMarksOperateAuthorization()
    {
        var builder = new WorkAuthorizationBuilder();

        builder.AllowOperateToKnownAuthenticatedUsers();

        var authorization = builder.Build();

        Assert.Equal(WorkAuthorizationRegistrationSource.None, authorization.Read.Source);
        Assert.False(authorization.Read.AllowsKnownAuthenticatedUsers);
        Assert.Empty(authorization.Read.Groups);
        Assert.Equal(WorkAuthorizationRegistrationSource.Fluent, authorization.Operate.Source);
        Assert.True(authorization.Operate.AllowsKnownAuthenticatedUsers);
        Assert.Empty(authorization.Operate.Groups);
    }

    [Fact]
    public void AllowReadToKnownAuthenticatedUsersMarksReadAuthorization()
    {
        var builder = new WorkAuthorizationBuilder();

        var returned = builder.AllowReadToKnownAuthenticatedUsers();
        builder.AllowReadToGroups("read.final");

        var authorization = builder.Build();

        Assert.Same(builder, returned);
        Assert.Equal(WorkAuthorizationRegistrationSource.Fluent, authorization.Read.Source);
        Assert.True(authorization.Read.AllowsKnownAuthenticatedUsers);
        Assert.Equal(["read.final"], authorization.Read.Groups);
        Assert.Equal(WorkAuthorizationRegistrationSource.None, authorization.Operate.Source);
        Assert.False(authorization.Operate.AllowsKnownAuthenticatedUsers);
        Assert.Empty(authorization.Operate.Groups);
    }

    [Fact]
    public void AllowDiscoverToKnownAuthenticatedUsersCanBeCombinedWithDiscoverGroups()
    {
        var builder = new WorkAuthorizationBuilder();

        var returned = builder.AllowDiscoverToKnownAuthenticatedUsers();
        builder.AllowDiscoverToGroups("discover.final");

        var authorization = builder.Build();

        Assert.Same(builder, returned);
        Assert.Equal(WorkAuthorizationRegistrationSource.Fluent, authorization.Discover.Source);
        Assert.True(authorization.Discover.AllowsKnownAuthenticatedUsers);
        Assert.Equal(["discover.final"], authorization.Discover.Groups);
        Assert.Equal(WorkAuthorizationRegistrationSource.None, authorization.Read.Source);
        Assert.Equal(WorkAuthorizationRegistrationSource.None, authorization.Operate.Source);
    }

    [Fact]
    public void RequireGroupsReplacesKnownAuthenticatedUserGrants()
    {
        var builder = new WorkAuthorizationBuilder();
        builder.AllowDiscoverToKnownAuthenticatedUsers();
        builder.AllowReadToKnownAuthenticatedUsers();
        builder.AllowOperateToKnownAuthenticatedUsers();

        builder.RequireGroups(["read.final"], ["operate.final"]);

        var authorization = builder.Build();

        Assert.False(authorization.Discover.AllowsKnownAuthenticatedUsers);
        Assert.Empty(authorization.Discover.Groups);
        Assert.False(authorization.Read.AllowsKnownAuthenticatedUsers);
        Assert.Equal(["read.final"], authorization.Read.Groups);
        Assert.False(authorization.Operate.AllowsKnownAuthenticatedUsers);
        Assert.Equal(["operate.final"], authorization.Operate.Groups);
    }

    [Fact]
    public void AllowOperateToKnownAuthenticatedUsersCanBeCombinedWithOperateGroups()
    {
        var builder = new WorkAuthorizationBuilder();

        builder.AllowOperateToGroups("operate.final");
        builder.AllowOperateToKnownAuthenticatedUsers();

        var authorization = builder.Build();

        Assert.True(authorization.Operate.AllowsKnownAuthenticatedUsers);
        Assert.Equal(["operate.final"], authorization.Operate.Groups);
    }

    [Fact]
    public void BuildRegistrationAggregatesConstrainedOperateGrants()
    {
        var builder = new WorkAuthorizationBuilder();

        builder.AllowQueueToGroups(
            "operate.first");
        builder.AllowWorkerActionsToGroups(
            ["operate.second"],
            operate => operate.WhenWorkerActionsRequire(context => context.Action == WorkOperateAction.Cancel));
        builder.AllowOperationsToGroups(
            ["operate.third"],
            WorkOperationPermissions.Reconfigure,
            operate => operate.WhenDefinitionReconfiguringRequire(context => context.Changes.Configuration is not null));
        builder.AllowQueueToKnownAuthenticatedUsers();

        var registration = builder.BuildRegistration();

        Assert.Equal(
            ["operate.first", "operate.second", "operate.third"],
            registration.DefinitionAuthorization.Operate.Groups.OrderBy(group => group, StringComparer.OrdinalIgnoreCase));
        Assert.True(registration.DefinitionAuthorization.Operate.AllowsKnownAuthenticatedUsers);
        Assert.Equal(4, registration.OperateAuthorization.Grants.Count);
    }

    [Fact]
    public void AllowMultipleGrantBlocksForTheSameAudience()
    {
        var builder = new WorkAuthorizationBuilder();

        builder.AllowQueueToGroups(
            ["operate.duplicate"],
            operate => operate.WhenQueueingRequire(context => context.RawInput is not null));
        builder.AllowWorkerActionsToGroups(
            ["operate.duplicate"],
            operate => operate.WhenWorkerActionsRequire(context => context.Action == WorkOperateAction.Cancel));
        builder.AllowQueueToKnownAuthenticatedUsers();
        builder.AllowWorkerActionsToKnownAuthenticatedUsers(
            operate => operate.WhenWorkerActionsRequire(context => context.Action == WorkOperateAction.Start));

        var registration = builder.BuildRegistration();

        Assert.Equal(4, registration.OperateAuthorization.Grants.Count);
        Assert.Equal(["operate.duplicate"], registration.DefinitionAuthorization.Operate.Groups.OrderBy(group => group).ToArray());
        Assert.True(registration.DefinitionAuthorization.Operate.AllowsKnownAuthenticatedUsers);
    }

    [Fact]
    public void RejectEmptyCustomOperationMask()
    {
        var builder = new WorkAuthorizationBuilder();

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            builder.AllowOperationsToGroups(["operate.none"], WorkOperationPermissions.None));

        Assert.Contains("At least one work operation permission must be supplied", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void IgnoreEmptyAudiencesAndRejectUnknownOperationFlags()
    {
        var builder = new WorkAuthorizationBuilder();

        Assert.Same(builder, builder.AllowOperationsToGroups([], WorkOperationPermissions.Queue));
        Assert.Same(builder, builder.AllowOperationsToGroups(
            ["  "],
            WorkOperationPermissions.Queue,
            _ => throw new InvalidOperationException("Empty audiences must not invoke requirements.")));
        Assert.Same(builder, builder.RequireGroups(readGroups: null, operateGroups: null));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.AllowOperationsToGroups(
            ["operators"],
            (WorkOperationPermissions)(1 << 20)));

        var registration = builder.BuildRegistration();
        Assert.Empty(registration.OperateAuthorization.Grants);
        Assert.Equal(WorkAuthorizationRegistrationSource.None, registration.DefinitionAuthorization.Operate.Source);
    }

    private sealed record QueueInput(string Value);
}
