using System.Text.Json.Serialization;
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
        var prepareDefinition = WorkDefinition.Create("sample.prepare");

        services.AddWorkableSystem(builder => builder.AddWorkflow(
            WorkflowDefinition.Create(
                "workflow.demo.dispatch",
                category: "Workflow:Demo",
                coordination: WorkflowCoordinationConfiguration.Durable),
            workflow => workflow.DispatchWork("prepare", prepareDefinition)));

        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<WorkSystemRegistration>();
        var workflow = Assert.Single(registration.Workflows);

        Assert.Equal("workflow.demo.dispatch", workflow.Definition.Name);
        Assert.Equal("Workflow:Demo", workflow.Definition.Category);
        Assert.True(workflow.Definition.Coordination.IsDurable);
        var dispatch = Assert.Single(workflow.Steps);
        var step = Assert.IsType<DispatchWorkflowStepDefinition>(dispatch);
        Assert.Equal("prepare", step.Name);
        Assert.Equal("sample.prepare", step.WorkDefinition.Name);
    }

    [Fact]
    public void RegisterWorkflowInputDispatchSteps()
    {
        var services = new ServiceCollection();
        var prepareDefinition = WorkDefinition.Create("sample.prepare.input");
        var archiveDefinition = WorkDefinition.Create("sample.archive.input");

        services.AddWorkableSystem(builder => builder.AddWorkflow(
            WorkflowDefinition.Create("workflow.demo.workflow-input"),
            workflow => workflow
                .DispatchWorkFromWorkflowInput("prepare", prepareDefinition)
                .RunParallel("fan-out", parallel => parallel
                    .DispatchWorkFromWorkflowInput("archive", archiveDefinition))));

        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<WorkSystemRegistration>();
        var workflow = Assert.Single(registration.Workflows);

        var prepare = Assert.IsType<DispatchWorkflowStepDefinition>(workflow.Steps[0]);
        Assert.Equal(WorkflowDispatchInputSource.WorkflowInput, prepare.InputSource);
        Assert.Null(prepare.Input);

        var parallel = Assert.IsType<ParallelWorkflowStepDefinition>(workflow.Steps[1]);
        var archive = Assert.IsType<DispatchWorkflowStepDefinition>(Assert.Single(parallel.Steps));
        Assert.Equal(WorkflowDispatchInputSource.WorkflowInput, archive.InputSource);
        Assert.Null(archive.Input);
    }

    [Fact]
    public void RegisterParallelStepWithChildDispatches()
    {
        var services = new ServiceCollection();
        var emailDefinition = WorkDefinition.Create("sample.email");
        var reportDefinition = WorkDefinition.Create("sample.report");

        services.AddWorkableSystem(builder => builder.AddWorkflow(
            WorkflowDefinition.Create("workflow.demo.parallel"),
            workflow => workflow.RunParallel("dispatch", parallel => parallel
                .DispatchWork("send-email", emailDefinition)
                .DispatchWork("generate-report", reportDefinition))));

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
                Assert.Equal("sample.email", dispatch.WorkDefinition.Name);
            },
            child =>
            {
                var dispatch = Assert.IsType<DispatchWorkflowStepDefinition>(child);
                Assert.Equal("generate-report", dispatch.Name);
                Assert.Equal("sample.report", dispatch.WorkDefinition.Name);
            });
    }

    [Fact]
    public void RegisterDispatchEachStep()
    {
        var services = new ServiceCollection();
        var loadDefinition = WorkDefinition.Create("sample.load");
        var processDefinition = WorkDefinition.Create("sample.process");

        services.AddWorkableSystem(builder => builder.AddWorkflow(
            WorkflowDefinition.Create("workflow.demo.dispatch-each"),
            workflow =>
            {
                var load = workflow.DispatchWork<DispatchEachSourceOutput>("load", loadDefinition);
                workflow.DispatchEach("fan-out", load, processDefinition, output => output.Items);
            }));

        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<WorkSystemRegistration>();
        var workflow = Assert.Single(registration.Workflows);

        Assert.Collection(
            workflow.Steps,
            load =>
            {
                var dispatch = Assert.IsType<DispatchWorkflowStepDefinition>(load);
                Assert.Equal("load", dispatch.Name);
                Assert.Equal("sample.load", dispatch.WorkDefinition.Name);
            },
            fanOut =>
            {
                var dispatchEach = Assert.IsType<DispatchEachWorkflowStepDefinition>(fanOut);
                Assert.Equal("fan-out", dispatchEach.Name);
                Assert.Equal("load", dispatchEach.SourceStep.StepName);
                Assert.Equal("sample.process", dispatchEach.WorkDefinition.Name);
                Assert.Equal("/items", dispatchEach.SourceSelector.JsonPointer);
            });
    }

    [Fact]
    public void RegisterTypedDispatchEachStep()
    {
        var services = new ServiceCollection();
        var loadDefinition = WorkDefinition.Create("sample.load");
        var processDefinition = WorkDefinition.Create("sample.process");

        services.AddWorkableSystem(builder => builder.AddWorkflow(
            WorkflowDefinition.Create("workflow.demo.dispatch-each.typed"),
            workflow =>
            {
                var load = workflow.DispatchWork<DispatchEachSourceOutput>("load", loadDefinition);
                workflow.DispatchEach("fan-out", load, processDefinition, output => output.Items);
            }));

        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<WorkSystemRegistration>();
        var workflow = Assert.Single(registration.Workflows);

        Assert.Collection(
            workflow.Steps,
            load =>
            {
                var dispatch = Assert.IsType<DispatchWorkflowStepDefinition>(load);
                Assert.Equal("load", dispatch.Name);
                Assert.Equal("sample.load", dispatch.WorkDefinition.Name);
            },
            fanOut =>
            {
                var dispatchEach = Assert.IsType<DispatchEachWorkflowStepDefinition>(fanOut);
                Assert.Equal("fan-out", dispatchEach.Name);
                Assert.Equal("load", dispatchEach.SourceStep.StepName);
                Assert.Equal("sample.process", dispatchEach.WorkDefinition.Name);
                Assert.Equal("/items", dispatchEach.SourceSelector.JsonPointer);
            });
    }

    [Fact]
    public void RegisterDispatchEachStepForRootArraySource()
    {
        var services = new ServiceCollection();
        var loadDefinition = WorkDefinition.Create("sample.load.root-array");
        var processDefinition = WorkDefinition.Create("sample.process.root-array");

        services.AddWorkableSystem(builder => builder.AddWorkflow(
            WorkflowDefinition.Create("workflow.demo.dispatch-each.root-array"),
            workflow =>
            {
                var load = workflow.DispatchWork<IReadOnlyList<DispatchEachSourceItem>>("load", loadDefinition);
                workflow.DispatchEach("fan-out", load, processDefinition, output => output);
            }));

        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<WorkSystemRegistration>();
        var workflow = Assert.Single(registration.Workflows);

        var fanOut = Assert.IsType<DispatchEachWorkflowStepDefinition>(workflow.Steps[1]);
        Assert.Null(fanOut.SourceSelector.JsonPointer);
    }

    [Fact]
    public void RegisterDispatchEachStepUsingJsonPropertyNameSelector()
    {
        var services = new ServiceCollection();
        var loadDefinition = WorkDefinition.Create("sample.load.json-name");
        var processDefinition = WorkDefinition.Create("sample.process.json-name");

        services.AddWorkableSystem(builder => builder.AddWorkflow(
            WorkflowDefinition.Create("workflow.demo.dispatch-each.json-name"),
            workflow =>
            {
                var load = workflow.DispatchWork<DispatchEachJsonNamedOutput>("load", loadDefinition);
                workflow.DispatchEach("fan-out", load, processDefinition, output => output.Items);
            }));

        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<WorkSystemRegistration>();
        var workflow = Assert.Single(registration.Workflows);

        var fanOut = Assert.IsType<DispatchEachWorkflowStepDefinition>(workflow.Steps[1]);
        Assert.Equal("/items_list", fanOut.SourceSelector.JsonPointer);
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
        var childDefinition = WorkDefinition.Create("sample.child");

        services.AddWorkableSystem(builder => builder.AddWorkflow(
            WorkflowDefinition.Create(
                "workflow.demo.authorized",
                authorization: WorkDefinitionAuthorization.Create(
                    readGroups: ["base.read"],
                    operateGroups: ["base.ops"])),
            workflow => workflow.DispatchWork("dispatch", childDefinition),
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
        var firstDefinition = WorkDefinition.Create("sample.first");
        var secondDefinition = WorkDefinition.Create("sample.second");

        services.AddWorkableSystem(builder =>
        {
            builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.duplicate"),
                workflow => workflow.DispatchWork("first", firstDefinition));
            builder.AddWorkflow(
                WorkflowDefinition.Create("WORKFLOW.DUPLICATE"),
                workflow => workflow.DispatchWork("second", secondDefinition));
        });

        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IWorkSystemRegistry>());

        Assert.Contains("Workflow definition names must be unique", exception.Message, StringComparison.Ordinal);
    }

    private static IReadOnlySet<string> Groups(params string[] groups)
        => new HashSet<string>(groups, StringComparer.OrdinalIgnoreCase);

    private sealed record DispatchEachSourceOutput(IReadOnlyList<DispatchEachSourceItem> Items);
    private sealed record DispatchEachSourceItem(string Id);
    private sealed record DispatchEachJsonNamedOutput([property: JsonPropertyName("items_list")] IReadOnlyList<DispatchEachSourceItem> Items);
}
