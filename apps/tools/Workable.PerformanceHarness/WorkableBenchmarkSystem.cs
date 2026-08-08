using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using Workable;

namespace Workable.PerformanceHarness;

internal sealed class WorkableBenchmarkSystem : IAsyncDisposable
{
    internal const string OperatorGroup = "workable.performance.operator";
    internal const string BenchmarkActorId = "workable.performance.benchmark";
    internal static readonly WorkIdentifier HotIdentifier = new("tenant", "tenant-000");

    private readonly ServiceProvider provider;
    private readonly WorkRequestContext requestContext;

    private WorkableBenchmarkSystem(
        ServiceProvider provider,
        IWorkSystem system,
        IWorkSystemSession session,
        WorkRequestContext requestContext,
        IReadOnlyList<WorkDefinition> definitions)
    {
        this.provider = provider;
        this.System = system;
        this.Session = session;
        this.requestContext = requestContext;
        this.Definitions = definitions;
    }

    public IWorkSystem System { get; }

    public IWorkSystemSession Session { get; }

    public WorkRequestContext RequestContext => this.requestContext;

    public IReadOnlyList<WorkDefinition> Definitions { get; }

    public static async Task<WorkableBenchmarkSystem> CreateQueued(
        int workerCount,
        bool requiresAuthorization = false,
        int definitionCount = 4,
        bool includeUnauthorizedDefinition = false,
        int actorCount = 1,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(actorCount, 1);

        var definitions = Enumerable.Range(0, definitionCount)
            .Select(index => WorkDefinition.Create(
                $"perf.benchmark.{index:D2}",
                category: index % 2 == 0 ? "Perf:Even" : "Perf:Odd"))
            .ToArray();
        var services = new ServiceCollection();
        services.AddWorkableSystem(builder =>
        {
            builder.RequireAuthorization(requiresAuthorization);
            if (requiresAuthorization)
            {
                builder.ConfigureAuthorization(authorization => authorization
                    .AllowControlSystemToGroups(OperatorGroup)
                    .AllowDiagnosticsToGroups(OperatorGroup));
            }

            foreach (var definition in definitions)
            {
                Action<IWorkAuthorizationBuilder>? authorize = requiresAuthorization
                    ? authorization => authorization.RequireGroups([OperatorGroup], [OperatorGroup])
                    : null;
                builder.AddWork(
                    definition,
                    SuccessfulWork,
                    configuration => configuration.DoNotStart(),
                    authorize);
            }

            if (requiresAuthorization && includeUnauthorizedDefinition)
            {
                builder.AddWork(
                    WorkDefinition.Create("perf.benchmark.hidden", category: "Perf:Hidden"),
                    SuccessfulWork,
                    configuration => configuration.DoNotStart(),
                    authorization => authorization.RequireGroups(
                        ["workable.performance.hidden"],
                        ["workable.performance.hidden"]));
            }
        });

        var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var requestContext = CreateRequestContext(actorIndex: 0);
        await system.Start(requestContext, cancellationToken);
        var session = await system.CreateSession(requestContext, cancellationToken);
        var actorSessions = new IWorkSystemSession[actorCount];
        for (var index = 0; index < actorSessions.Length; index++)
        {
            actorSessions[index] = await system.CreateSession(
                CreateRequestContext(index),
                cancellationToken);
        }

        for (var index = 0; index < workerCount; index++)
        {
            var definition = definitions[index % definitions.Length];
            var handle = await actorSessions[index % actorSessions.Length].Queue.Enqueue(
                definition.Name,
                CreateInput(index),
                cancellationToken: cancellationToken);
            if (!handle.QueueOutcome.IsAccepted)
            {
                throw new InvalidOperationException(
                    $"Benchmark worker {index} was not accepted: {string.Join("; ", handle.QueueOutcome.Messages.Select(message => message.Text))}");
            }
        }

        // Keep benchmark methods focused on the target operation, not initial projection catch-up.
        await session.Query.Workers(new WorkerCriteria(Take: 1), cancellationToken);

        return new WorkableBenchmarkSystem(provider, system, session, requestContext, definitions);
    }

    internal static string ActorId(int index)
        => index == 0 ? BenchmarkActorId : $"{BenchmarkActorId}.{index:D3}";

    public static WorkInput CreateInput(int index)
    {
        var tenant = index % 16 == 0
            ? HotIdentifier.Value
            : $"tenant-{index % 1_024:D3}";
        return WorkInput.Empty
            .WithSubject(new WorkSubjectId("benchmark-worker", index.ToString(CultureInfo.InvariantCulture)))
            .WithIdentifier(new WorkIdentifier(HotIdentifier.Type, tenant))
            .WithIdentifier(new WorkIdentifier("segment", $"segment-{index % 32:D2}"));
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

    private static WorkRequestContext CreateRequestContext(int actorIndex)
    {
        var actor = new WorkActor(
            Id: ActorId(actorIndex),
            Name: $"Workable Performance Benchmark Actor {actorIndex:D3}");
        var origin = WorkOrigin.Create(WorkInvocationChannel.InProcess, actor);
        return new WorkRequestContext(
            Origin: origin,
            Authorization: WorkAuthorizationSnapshot.Create(actor, [OperatorGroup], readableDefinitionIds: null));
    }

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());
}
