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
}
