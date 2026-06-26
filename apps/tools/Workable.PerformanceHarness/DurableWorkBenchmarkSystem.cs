using Microsoft.Extensions.DependencyInjection;
using System.Diagnostics;
using Workable.SqlServer;

namespace Workable.PerformanceHarness;

internal sealed class DurableWorkBenchmarkSystem : IAsyncDisposable
{
    private const string SchemaName = "workable_perf";
    private readonly ServiceProvider provider;
    private readonly WorkRequestContext requestContext;
    private readonly string schemaName;

    private DurableWorkBenchmarkSystem(
        ServiceProvider provider,
        IWorkSystem system,
        IWorkSystemSession session,
        WorkRequestContext requestContext,
        string connectionString,
        string schemaName)
    {
        this.provider = provider;
        this.System = system;
        this.Session = session;
        this.requestContext = requestContext;
        this.ConnectionString = connectionString;
        this.schemaName = schemaName;
    }

    public IWorkSystem System { get; }

    public IWorkSystemSession Session { get; }

    public string ConnectionString { get; }

    public string DurabilitySchemaName => this.schemaName;

    public string DurableQueuedWorkName => "perf.durable.queued";

    public string DurableFastWorkName => "perf.durable.fast";

    public string PersistentIdempotentWorkName => "perf.idempotent.persistent";

    public string DurableIdempotentWorkName => "perf.idempotent.durable";

    public static async Task<DurableWorkBenchmarkSystem> Create(
        string? durabilityConnectionString = null,
        string schemaName = SchemaName,
        bool resetStore = true,
        CancellationToken cancellationToken = default)
    {
        var connectionString = durabilityConnectionString;
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            var sql = await BenchmarkSqlServerEnvironment.GetShared();
            connectionString = sql.ConnectionString;
        }

        await BenchmarkSqlServerEnvironment.PrepareSchema(
            connectionString,
            schemaName,
            resetStore,
            cancellationToken);

        var services = new ServiceCollection();
        services.AddWorkableSqlServerDurableQueue(connectionString, schemaName);
        services.AddWorkableSystem("durable-benchmarks", builder =>
        {
            builder.RequireAuthorization(false);
            builder.AddWork(
                WorkDefinition.Create("perf.durable.queued", category: "Perf:Durable"),
                SuccessfulWork,
                configuration => configuration.QueueDurably().DoNotStart());
            builder.AddWork(
                WorkDefinition.Create("perf.durable.fast", category: "Perf:Durable"),
                SuccessfulWork,
                configuration => configuration.QueueDurably());
            builder.AddWork(
                WorkDefinition.Create("perf.idempotent.persistent", category: "Perf:Durable"),
                SuccessfulWork,
                configuration => configuration.CoordinatePersistently().RejectDuplicateSubjects().DoNotStart());
            builder.AddWork(
                WorkDefinition.Create("perf.idempotent.durable", category: "Perf:Durable"),
                SuccessfulWork,
                configuration => configuration.QueueDurably().RejectDuplicateSubjects().DoNotStart());
        });

        var provider = services.BuildServiceProvider();
        var system = provider.GetRequiredService<IWorkSystemRegistry>().Default;
        var requestContext = BenchmarkRequestContexts.CreateAnonymous("Run durable performance benchmark.");
        await system.Start(requestContext, cancellationToken);
        var session = system.CreateSession(requestContext);
        return new DurableWorkBenchmarkSystem(
            provider,
            system,
            session,
            requestContext,
            connectionString,
            schemaName);
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

    public async Task<WorkerSnapshot> WaitForWorker(
        WorkerId workerId,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = Stopwatch.GetTimestamp();
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        while (Stopwatch.GetElapsedTime(startedAt) < limit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var worker = await this.Session.Query.Worker(workerId, cancellationToken);
            if (worker is not null)
            {
                return worker;
            }

            await Task.Delay(10, cancellationToken);
        }

        throw new TimeoutException($"Timed out waiting for durable worker '{workerId.Value:D}' to become queryable.");
    }

    private static Task<WorkExecutionResult> SuccessfulWork(
        IWorkExecutionContext context,
        WorkInput? input,
        CancellationToken cancellationToken)
        => Task.FromResult(WorkExecutionResult.Success());
}
