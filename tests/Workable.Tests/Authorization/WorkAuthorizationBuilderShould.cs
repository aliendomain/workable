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
        builder.AllowReadToGroups("read.final");
        builder.AllowOperateToGroups("operate.final");

        var authorization = builder.Build();

        Assert.Same(builder, returned);
        Assert.Equal(["read.final"], authorization.Read.Groups);
        Assert.Equal(["operate.final"], authorization.Operate.Groups);
        Assert.Equal(WorkAuthorizationRegistrationSource.Fluent, authorization.Read.Source);
        Assert.Equal(WorkAuthorizationRegistrationSource.Fluent, authorization.Operate.Source);
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

    private sealed record QueueInput(string Value);
}
