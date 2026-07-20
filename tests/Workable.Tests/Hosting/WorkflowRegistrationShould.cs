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
    public void RegisterParallelBranchesWithSequentialWorkflowStructure()
    {
        var services = new ServiceCollection();
        var collectDefinition = WorkDefinition.Create("sample.collect");
        var normalizeDefinition = WorkDefinition.Create("sample.normalize");
        var renderDefinition = WorkDefinition.Create("sample.render");
        var publishDefinition = WorkDefinition.Create("sample.publish");

        services.AddWorkableSystem(builder => builder.AddWorkflow(
            WorkflowDefinition.Create("workflow.demo.branch"),
            workflow => workflow.RunParallel("fan-out", parallel => parallel
                .Branch("documents", branch => branch
                    .DispatchWork("collect", collectDefinition)
                    .DispatchWork("normalize", normalizeDefinition))
                .Branch("publishing", branch => branch
                    .RunParallel("replicate", replicate => replicate
                        .DispatchWork("render", renderDefinition)
                        .DispatchWork("publish", publishDefinition))))));

        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<WorkSystemRegistration>();
        var workflow = Assert.Single(registration.Workflows);
        var fanOut = Assert.IsType<ParallelWorkflowStepDefinition>(Assert.Single(workflow.Steps));

        Assert.Collection(
            fanOut.Steps,
            documents =>
            {
                var branch = Assert.IsType<BranchWorkflowStepDefinition>(documents);
                Assert.Equal("documents", branch.Name);
                Assert.Collection(
                    branch.Steps,
                    collect =>
                    {
                        var dispatch = Assert.IsType<DispatchWorkflowStepDefinition>(collect);
                        Assert.Equal("collect", dispatch.Name);
                        Assert.Equal("sample.collect", dispatch.WorkDefinition.Name);
                    },
                    normalize =>
                    {
                        var dispatch = Assert.IsType<DispatchWorkflowStepDefinition>(normalize);
                        Assert.Equal("normalize", dispatch.Name);
                        Assert.Equal("sample.normalize", dispatch.WorkDefinition.Name);
                    });
            },
            publishing =>
            {
                var branch = Assert.IsType<BranchWorkflowStepDefinition>(publishing);
                Assert.Equal("publishing", branch.Name);
                var replicate = Assert.IsType<ParallelWorkflowStepDefinition>(Assert.Single(branch.Steps));
                Assert.Equal("replicate", replicate.Name);
                Assert.Equal(["render", "publish"], replicate.Steps.Select(step => step.Name).ToArray());
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
                Assert.Equal(WorkflowCanceledChildBehavior.Continue, dispatchEach.CanceledChildBehavior);
            });
    }

    [Fact]
    public void RegisterDispatchEachCanceledChildBehavior()
    {
        var services = new ServiceCollection();
        var loadDefinition = WorkDefinition.Create("sample.load.canceled-policy");
        var processDefinition = WorkDefinition.Create("sample.process.canceled-policy");

        services.AddWorkableSystem(builder => builder.AddWorkflow(
            WorkflowDefinition.Create("workflow.demo.dispatch-each.canceled-policy"),
            workflow =>
            {
                var load = workflow.DispatchWork<DispatchEachSourceOutput>("load", loadDefinition);
                workflow.DispatchEach(
                    "fan-out",
                    load,
                    processDefinition,
                    output => output.Items,
                    WorkflowCanceledChildBehavior.CancelWorkflow);
            }));

        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<WorkSystemRegistration>();
        var workflow = Assert.Single(registration.Workflows);
        var fanOut = Assert.IsType<DispatchEachWorkflowStepDefinition>(workflow.Steps[1]);

        Assert.Equal(WorkflowCanceledChildBehavior.CancelWorkflow, fanOut.CanceledChildBehavior);
    }

    [Fact]
    public void RejectInvalidDispatchEachCanceledChildBehavior()
    {
        var services = new ServiceCollection();
        var loadDefinition = WorkDefinition.Create("sample.load.invalid-canceled-policy");
        var processDefinition = WorkDefinition.Create("sample.process.invalid-canceled-policy");

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            services.AddWorkableSystem(builder => builder.AddWorkflow(
                WorkflowDefinition.Create("workflow.demo.dispatch-each.invalid-canceled-policy"),
                workflow =>
                {
                    var load = workflow.DispatchWork<DispatchEachSourceOutput>("load", loadDefinition);
                    workflow.DispatchEach(
                        "fan-out",
                        load,
                        processDefinition,
                        output => output.Items,
                        (WorkflowCanceledChildBehavior)999);
                })));

        Assert.Equal("canceledChildBehavior", exception.ParamName);
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
    public void ReturnTypedReferenceToDispatchEachChildOutputs()
    {
        var services = new ServiceCollection();
        var loadDefinition = WorkDefinition.Create("sample.load.chained");
        var processDefinition = WorkDefinition.Create("sample.process.chained");
        var gatherDefinition = WorkDefinition.Create("sample.gather.chained");

        services.AddWorkableSystem(builder => builder.AddWorkflow(
            WorkflowDefinition.Create("workflow.demo.dispatch-each.chained"),
            workflow =>
            {
                var load = workflow.DispatchWork<DispatchEachSourceOutput>("load", loadDefinition);
                var processed = workflow
                    .DispatchEach("process", load, processDefinition, output => output.Items)
                    .Outputs<DispatchEachSourceOutput>();

                workflow.DispatchEach("gather", processed, gatherDefinition, output => output.Items);
            }));

        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<WorkSystemRegistration>();
        var workflow = Assert.Single(registration.Workflows);

        var gather = Assert.IsType<DispatchEachWorkflowStepDefinition>(workflow.Steps[2]);
        Assert.Equal("process", gather.SourceStep.StepName);
        Assert.Equal("sample.gather.chained", gather.WorkDefinition.Name);
        Assert.Equal("/items", gather.SourceSelector.JsonPointer);
    }

    [Fact]
    public void ContinueBuildingTheSameWorkflowFromDispatchEachResult()
    {
        var services = new ServiceCollection();
        var loadDefinition = WorkDefinition.Create("sample.load.continue");
        var processDefinition = WorkDefinition.Create("sample.process.continue");
        var directDefinition = WorkDefinition.Create("sample.direct.continue");
        var typedDefinition = WorkDefinition.Create("sample.typed.continue");
        var workflowInputDefinition = WorkDefinition.Create("sample.workflow-input.continue");
        var typedWorkflowInputDefinition = WorkDefinition.Create("sample.typed-workflow-input.continue");
        var nestedFanOutDefinition = WorkDefinition.Create("sample.nested-fan-out.continue");
        var parallelDefinition = WorkDefinition.Create("sample.parallel.continue");
        var directInput = WorkInput.FromValue(new DispatchEachSourceItem("direct-input"));
        WorkflowStepReference<DispatchEachSourceOutput>? typedDispatch = null;
        WorkflowStepReference<DispatchEachSourceOutput>? typedWorkflowInputDispatch = null;

        services.AddWorkableSystem(builder => builder.AddWorkflow(
            WorkflowDefinition.Create("workflow.demo.dispatch-each.continue"),
            workflow =>
            {
                var load = workflow.DispatchWork<DispatchEachSourceOutput>("load", loadDefinition);
                var fanOut = workflow.DispatchEach(
                    "fan-out",
                    load,
                    processDefinition,
                    output => output.Items);
                var fanOutOutputs = fanOut.Outputs<DispatchEachSourceOutput>();

                fanOut.DispatchWork("direct", directDefinition, directInput);
                typedDispatch = fanOut.DispatchWork<DispatchEachSourceOutput>("typed", typedDefinition);
                fanOut.DispatchWorkFromWorkflowInput("workflow-input", workflowInputDefinition);
                typedWorkflowInputDispatch = fanOut.DispatchWorkFromWorkflowInput<DispatchEachSourceOutput>(
                    "typed-workflow-input",
                    typedWorkflowInputDefinition);
                fanOut.DispatchEach(
                    "nested-fan-out",
                    fanOutOutputs,
                    nestedFanOutDefinition,
                    output => output.Items,
                    WorkflowCanceledChildBehavior.CancelWorkflow);
                fanOut.RunParallel(
                    "parallel",
                    parallel => parallel.DispatchWork("parallel-child", parallelDefinition));
                fanOut.Join("join");
            }));

        using var provider = services.BuildServiceProvider();
        var registration = provider.GetRequiredService<WorkSystemRegistration>();
        var workflow = Assert.Single(registration.Workflows);

        Assert.Equal(
            [
                "load",
                "fan-out",
                "direct",
                "typed",
                "workflow-input",
                "typed-workflow-input",
                "nested-fan-out",
                "parallel",
                "join",
            ],
            workflow.Steps.Select(step => step.Name).ToArray());
        Assert.Equal("typed", typedDispatch?.StepName);
        Assert.Equal("typed-workflow-input", typedWorkflowInputDispatch?.StepName);

        var direct = Assert.IsType<DispatchWorkflowStepDefinition>(workflow.Steps[2]);
        var typed = Assert.IsType<DispatchWorkflowStepDefinition>(workflow.Steps[3]);
        Assert.Equal("sample.direct.continue", direct.WorkDefinition.Name);
        Assert.Same(directInput, direct.Input);
        Assert.Equal("sample.typed.continue", typed.WorkDefinition.Name);

        var workflowInput = Assert.IsType<DispatchWorkflowStepDefinition>(workflow.Steps[4]);
        var typedWorkflowInput = Assert.IsType<DispatchWorkflowStepDefinition>(workflow.Steps[5]);
        Assert.Equal("sample.workflow-input.continue", workflowInput.WorkDefinition.Name);
        Assert.Equal("sample.typed-workflow-input.continue", typedWorkflowInput.WorkDefinition.Name);
        Assert.Equal(WorkflowDispatchInputSource.WorkflowInput, workflowInput.InputSource);
        Assert.Equal(WorkflowDispatchInputSource.WorkflowInput, typedWorkflowInput.InputSource);

        var nestedFanOut = Assert.IsType<DispatchEachWorkflowStepDefinition>(workflow.Steps[6]);
        Assert.Equal("fan-out", nestedFanOut.SourceStep.StepName);
        Assert.Equal("sample.nested-fan-out.continue", nestedFanOut.WorkDefinition.Name);
        Assert.Equal("/items", nestedFanOut.SourceSelector.JsonPointer);
        Assert.Equal(WorkflowCanceledChildBehavior.CancelWorkflow, nestedFanOut.CanceledChildBehavior);

        var parallel = Assert.IsType<ParallelWorkflowStepDefinition>(workflow.Steps[7]);
        var parallelChild = Assert.IsType<DispatchWorkflowStepDefinition>(Assert.Single(parallel.Steps));
        Assert.Equal("parallel-child", parallelChild.Name);
        Assert.Equal("sample.parallel.continue", parallelChild.WorkDefinition.Name);
        Assert.IsType<JoinWorkflowStepDefinition>(workflow.Steps[8]);
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
