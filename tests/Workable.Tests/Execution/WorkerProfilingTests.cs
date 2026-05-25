using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.Tests;

[Trait("Category", "Profiling")]
public sealed class WorkerProfilingTests
{
    [Fact]
    public async Task DisabledProfilingUsesNoOpProfilerAndDoesNotAttachProfileSnapshot()
    {
        var definition = WorkDefinition.Create("profile-off", "Uses profile API while profiling is disabled.");
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, (context, input, cancellationToken) =>
            {
                context.Profile.AddInfo("hidden");
                using var scope = context.Profile.CreateScope("hidden scope");
                return Task.FromResult(WorkExecutionResult.Success());
            }))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();

        var completion = await (await system.Queue.Enqueue("profile-off")).WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        Assert.Null(RequiredWorker(completion.Worker).Profile);
    }

    [Fact]
    public async Task EnabledProfilingCapturesContextAndInjectedServiceInTree()
    {
        var definition = WorkDefinition.Create("profile-on", "Captures profile activity.",
            defaultOptions: new WorkerOptions(ProfilingEnabled: true));
        var services = new ServiceCollection();
        services.AddScoped<ProfiledDependency>();
        services.AddWorkableSystem(builder => builder.AddWork<ProfiledExecutor>(definition));
        var system = services.BuildServiceProvider().GetRequiredService<IWorkSystemRegistry>().Default;
        await system.Start();

        var completion = await (await system.Queue.Enqueue("profile-on")).WaitForCompletion();

        Assert.True(completion.IsCompletedSuccessfully);
        var worker = RequiredWorker(completion.Worker);
        var profile = worker.Profile ?? throw new InvalidOperationException("Expected a profile snapshot.");
        var labels = Flatten(profile.Root).Select(node => node.Label).ToList();
        var ascii = profile.ToAsciiTree();

        Assert.Equal($"Worker {worker.Id.Value} profile-on", profile.Root.Label);
        Assert.Contains(labels, label => label.Contains("ProfiledExecutor", StringComparison.Ordinal));
        Assert.Contains(labels, label => label == "context info");
        Assert.Contains(labels, label => label == "context scope");
        Assert.Contains(labels, label => label == "dependency info");
        Assert.Contains(labels, label => label == "dependency timing");
        Assert.Contains("context info", ascii, StringComparison.Ordinal);
        Assert.Contains("dependency info", ascii, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecurringProfilingCapturesProfilePerIteration()
    {
        var attempts = 0;
        var definition = WorkDefinition.Create("recurring-profile", "Captures each recurring iteration separately.",
            defaultOptions: new WorkerOptions(ProfilingEnabled: true),
            configuration: WorkConfiguration.Default with
            {
                Recurrence = WorkRecurrenceConfiguration.Every(TimeSpan.FromMilliseconds(1)) with
                {
                    ContinueAfterFailure = false,
                    RetainedIterations = 5,
                },
            });
        var system = new ServiceCollection()
            .AddWorkableSystem(builder => builder.AddWork(definition, (context, input, cancellationToken) =>
            {
                attempts++;
                context.Profile.AddInfo($"iteration {attempts}");
                return Task.FromResult(attempts < 2
                    ? WorkExecutionResult.Success()
                    : WorkExecutionResult.Failure([WorkMessage.Error("stop", "Stop recurrence.")]));
            }))
            .BuildServiceProvider()
            .GetRequiredService<IWorkSystemRegistry>()
            .Default;
        await system.Start();

        var completion = await (await system.Queue.Enqueue("recurring-profile")).WaitForCompletion();

        Assert.Equal(WorkCompletionStatus.Failed, completion.Status);
        var iterations = RequiredWorker(completion.Worker).Iterations;
        Assert.Equal(2, iterations.Count);
        Assert.All(iterations, iteration => Assert.NotNull(iteration.Profile));
        Assert.Contains(Flatten(iterations[0].Profile!.Root), node => node.Label == "iteration 1");
        Assert.Contains(Flatten(iterations[1].Profile!.Root), node => node.Label == "iteration 2");
    }

    private static IEnumerable<WorkProfileSnapshotNode> Flatten(WorkProfileSnapshotNode node)
    {
        yield return node;
        foreach (var child in node.Children)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    private static WorkerSnapshot RequiredWorker(WorkerSnapshot? worker)
        => worker ?? throw new InvalidOperationException("Expected worker.");

    private sealed class ProfiledDependency(IWorkProfiler profiler)
    {
        public void Execute()
        {
            profiler.AddInfo("dependency info");
            using var timing = profiler.StartTiming("dependency timing");
        }
    }

    private sealed class ProfiledExecutor(ProfiledDependency dependency) : IWorkExecutor
    {
        public Task<WorkExecutionResult> Execute(IWorkExecutionContext context, WorkInput? input, CancellationToken cancellationToken)
        {
            context.Profile.AddInfo("context info");
            using (context.Profile.CreateScope("context scope"))
            {
                dependency.Execute();
            }

            return Task.FromResult(WorkExecutionResult.Success());
        }
    }
}
