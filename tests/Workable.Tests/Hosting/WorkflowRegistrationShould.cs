using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Hosting")]
public sealed class WorkflowRegistrationShould
{
    [Fact]
    public void RegisterDispatchWorkStep()
    {
        var services = new ServiceCollection();

        services.AddWorkableSystem(builder => builder.AddWorkflow(
            WorkflowDefinition.Create("workflow.demo.dispatch", category: "Workflow:Demo"),
            workflow => workflow.DispatchWork("prepare", "sample.prepare")));

        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<WorkSystemRegistration>();
        var workflow = Assert.Single(registration.Workflows);

        Assert.Equal("workflow.demo.dispatch", workflow.Definition.Name);
        Assert.Equal("Workflow:Demo", workflow.Definition.Category);
        var dispatch = Assert.Single(workflow.Steps);
        var step = Assert.IsType<DispatchWorkflowStepDefinition>(dispatch);
        Assert.Equal("prepare", step.Name);
        Assert.Equal("sample.prepare", step.WorkDefinitionName);
    }

    [Fact]
    public void RegisterParallelStepWithChildDispatches()
    {
        var services = new ServiceCollection();

        services.AddWorkableSystem(builder => builder.AddWorkflow(
            WorkflowDefinition.Create("workflow.demo.parallel"),
            workflow => workflow.RunParallel("dispatch", parallel => parallel
                .DispatchWork("send-email", "sample.email")
                .DispatchWork("generate-report", "sample.report"))));

        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<WorkSystemRegistration>();
        var workflow = Assert.Single(registration.Workflows);
        var parallel = Assert.Single(workflow.Steps);
        var step = Assert.IsType<ParallelWorkflowStepDefinition>(parallel);

        Assert.Equal("dispatch", step.Name);
        Assert.Collection(
            step.Steps,
            child =>
            {
                var dispatch = Assert.IsType<DispatchWorkflowStepDefinition>(child);
                Assert.Equal("send-email", dispatch.Name);
                Assert.Equal("sample.email", dispatch.WorkDefinitionName);
            },
            child =>
            {
                var dispatch = Assert.IsType<DispatchWorkflowStepDefinition>(child);
                Assert.Equal("generate-report", dispatch.Name);
                Assert.Equal("sample.report", dispatch.WorkDefinitionName);
            });
    }

    [Fact]
    public void RegisterJoinStep()
    {
        var services = new ServiceCollection();

        services.AddWorkableSystem(builder => builder.AddWorkflow(
            WorkflowDefinition.Create("workflow.demo.join"),
            workflow => workflow.Join("settle")));

        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<WorkSystemRegistration>();
        var workflow = Assert.Single(registration.Workflows);
        var join = Assert.Single(workflow.Steps);
        var step = Assert.IsType<JoinWorkflowStepDefinition>(join);

        Assert.Equal("settle", step.Name);
    }

    [Fact]
    public void ApplyWorkflowAuthorizationUsingTheSameBuilderModelAsWork()
    {
        var services = new ServiceCollection();

        services.AddWorkableSystem(builder => builder.AddWorkflow(
            WorkflowDefinition.Create(
                "workflow.demo.authorized",
                authorization: WorkDefinitionAuthorization.Create(
                    readGroups: ["base.read"],
                    operateGroups: ["base.ops"])),
            workflow => workflow.DispatchWork("dispatch", "sample.child"),
            authorize: auth => auth
                .AllowReadToGroups("workflow.read")
                .AllowOperateToGroups("workflow.ops")));

        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<WorkSystemRegistration>();
        var workflow = Assert.Single(registration.Workflows);

        Assert.Equal(["workflow.read"], workflow.Definition.Authorization.Read.Groups);
        Assert.Equal(["workflow.ops"], workflow.Definition.Authorization.Operate.Groups);
        Assert.True(workflow.OperateAuthorization.CanAttempt(Groups("workflow.ops"), false, WorkOperationPermissions.Queue));
        Assert.False(workflow.OperateAuthorization.CanAttempt(Groups("base.ops"), false, WorkOperationPermissions.Queue));
    }

    [Fact]
    public void RejectDuplicateWorkflowDefinitionNamesWhenCreatingTheInMemoryCatalog()
    {
        var services = new ServiceCollection();

        services.AddWorkableSystem(builder =>
        {
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.duplicate"),
                workflow => workflow.DispatchWork("first", "sample.first"));
            builder.AddWorkflow(
                WorkflowDefinition.Create("WORKFLOW.DUPLICATE"),
                workflow => workflow.DispatchWork("second", "sample.second"));
        });

        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IWorkSystemRegistry>());

        Assert.Contains("Workflow definition names must be unique", exception.Message, StringComparison.Ordinal);
    }

    private static IReadOnlySet<string> Groups(params string[] groups)
        => new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);
}
