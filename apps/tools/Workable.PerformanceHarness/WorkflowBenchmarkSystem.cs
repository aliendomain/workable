using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.PerformanceHarness;

internal sealed class WorkflowBenchmarkSystem : IAsyncDisposable
{
    private readonly ServiceProvider provider;
    private readonly WorkRequestContext requestContext;

    private WorkflowBenchmarkSystem(
        ServiceProvider provider,
        IWorkSystem system,
        WorkRequestContext requestContext)
    {
        this.provider = provider;
        this.System = system;
        this.requestContext = requestContext;
    }

    public IWorkSystem System { get; }

    public WorkRequestContext RequestContext => this.requestContext;

    public static async Task<WorkflowBenchmarkSystem> Create(
        int branchCount,
        bool requiresAuthorization = false,
        CancellationToken cancellationToken = default)
    {
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(requiresAuthorization);
            if (requiresAuthorization)
            {
                builder.ConfigureAuthorization(authorization => authorization
                    .AllowControlSystemToGroups(WorkableBenchmarkSystem.OperatorGroup)
                    .AllowDiagnosticsToGroups(WorkableBenchmarkSystem.OperatorGroup));
            }

            builder.AddWork(
                WorkDefinition.Create("perf.workflow.dispatch.child", category: "Perf:Workflow"),
                SuccessfulWork,
                configure: null,
                authorize: requiresAuthorization ? AllowOperatorGroups : null);

            for (var index = 0; index < Math.Max(1, branchCount); index++)
            {
                builder.AddWork(
                    WorkDefinition.Create($"perf.workflow.parallel.child.{index:D2}", category: "Perf:Workflow"),
                    SuccessfulWork,
                    configure: null,
                    authorize: requiresAuthorization ? AllowOperatorGroups : null);
            }

            builder.AddWorkflow(
                WorkflowDefinition.Create("perf.workflow.dispatch"),
                workflow => workflow.DispatchWork("dispatch", "perf.workflow.dispatch.child"),
                authorize: requiresAuthorization ? AllowWorkflowOperatorGroups : null);

            builder.AddWorkflow(
                WorkflowDefinition.Create("perf.workflow.parallel"),
                workflow =>
                {
                    workflow.RunParallel("parallel", parallel =>
                    {
                        for (var index = 0; index < Math.Max(1, branchCount); index++)
                        {
                            parallel.DispatchWork(
                                $"branch-{index:D2}",
                                $"perf.workflow.parallel.child.{index:D2}");
                        }
                    });
                    workflow.Join("join");
                },
                authorize: requiresAuthorization ? AllowWorkflowOperatorGroups : null);
        });

        var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var requestContext = requiresAuthorization
            ? BenchmarkRequestContexts.CreateOperator("Run workflow performance benchmark.")
            : BenchmarkRequestContexts.CreateAnonymous("Run workflow performance benchmark.");
        await system.Start(requestContext, cancellationToken);
        return new WorkflowBenchmarkSystem(provider, system, requestContext);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await this.System.Stop(this.requestContext);
        }
        finally
        {
            await this.provider.DisposeAsync();
        }
    }

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());

    private static void AllowOperatorGroups(IWorkAuthorizationBuilder authorization)
        => authorization.RequireGroups(
            [WorkableBenchmarkSystem.OperatorGroup],
            [WorkableBenchmarkSystem.OperatorGroup]);

    private static void AllowWorkflowOperatorGroups(IWorkAuthorizationBuilder authorization)
        => authorization.AllowOperateToGroups(WorkableBenchmarkSystem.OperatorGroup);
}
