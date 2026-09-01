using BenchmarkDotNet.Attributes;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using Workable.SqlServer;

namespace Workable.PerformanceHarness;

/// <summary>
/// Measures SQL schema-validation startup cost as the number of Workable systems in one host grows.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[InvocationCount(1)]
public class BaselineSqlSchemaInitializationBenchmarks
{
    private const int HostsPerInvocation = 4;
    private const string SchemaName = "workable_perf_schema_startup";
    private static readonly WorkRequestContext LifecycleContext =
        WorkRequestContext.Create(WorkInvocationChannel.InProcess);

    private string connectionString = string.Empty;
    private ServiceProvider[] providers = null!;
    private IReadOnlyCollection<IWorkSystem>[] systems = null!;

    [Params(1, 8, 32)]
    public int SystemCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        var sql = BenchmarkSqlServerEnvironment.GetShared().GetAwaiter().GetResult();
        this.connectionString = sql.ConnectionString;
        BenchmarkSqlServerEnvironment.PrepareSchema(
                this.connectionString,
                SchemaName,
                resetStore: false)
            .GetAwaiter()
            .GetResult();
    }

    [IterationSetup]
    public void IterationSetup()
    {
        this.providers = new ServiceProvider[HostsPerInvocation];
        this.systems = new IReadOnlyCollection<IWorkSystem>[HostsPerInvocation];
        for (var host = 0; host < HostsPerInvocation; host++)
        {
            var services = new ServiceCollection();
            services.AddWorkableSqlServerPersistence(
                this.connectionString,
                SchemaName,
                persistenceScope: "schema-startup-benchmark",
                autoDeploySchema: false);
            for (var index = 0; index < this.SystemCount; index++)
            {
                services.AddWorkableSystem(
                    $"schema-startup-{index:D2}",
                    builder => builder.RequireAuthorization(false));
            }

            this.providers[host] = services.BuildServiceProvider();
            this.systems[host] = this.providers[host].GetRequiredService<IWorkSystemRegistry>().Systems;
        }
    }

    [Benchmark(OperationsPerInvoke = HostsPerInvocation)]
    public async Task StartSystemsWithInstalledSchema()
    {
        foreach (var hostSystems in this.systems)
        {
            foreach (var system in hostSystems)
            {
                await system.Start(LifecycleContext);
            }
        }
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        for (var host = 0; host < HostsPerInvocation; host++)
        {
            foreach (var system in this.systems[host])
            {
                system.Stop(LifecycleContext).GetAwaiter().GetResult();
            }

            this.providers[host].DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}

/// <summary>
/// Measures fail-open startup when all systems share one unavailable diagnostics database.
/// </summary>
[MemoryDiagnoser]
[ShortRunJob]
[InvocationCount(1)]
public class BaselineUnavailableDiagnosticsStartupBenchmarks
{
    private const int HostsPerInvocation = 12;
    private static readonly WorkRequestContext LifecycleContext =
        WorkRequestContext.Create(WorkInvocationChannel.InProcess);

    private string connectionString = string.Empty;
    private ServiceProvider[] providers = null!;
    private IReadOnlyCollection<IWorkSystem>[] systems = null!;

    [Params(1, 8, 32)]
    public int SystemCount { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        var sql = BenchmarkSqlServerEnvironment.GetShared().GetAwaiter().GetResult();
        this.connectionString = new SqlConnectionStringBuilder(sql.ConnectionString)
        {
            InitialCatalog = $"workable_perf_missing_{Guid.NewGuid():N}",
            ConnectTimeout = 1,
            Pooling = false,
        }.ConnectionString;
    }

    [IterationSetup]
    public void IterationSetup()
    {
        this.providers = new ServiceProvider[HostsPerInvocation];
        this.systems = new IReadOnlyCollection<IWorkSystem>[HostsPerInvocation];
        for (var host = 0; host < HostsPerInvocation; host++)
        {
            var services = new ServiceCollection();
            services.AddWorkableSqlServerPersistence(this.connectionString);
            for (var index = 0; index < this.SystemCount; index++)
            {
                services.AddWorkableSystem(
                    $"diagnostics-unavailable-{index:D2}",
                    builder => builder.RequireAuthorization(false));
            }

            this.providers[host] = services.BuildServiceProvider();
            this.systems[host] = this.providers[host].GetRequiredService<IWorkSystemRegistry>().Systems;
        }
    }

    [Benchmark(OperationsPerInvoke = HostsPerInvocation)]
    public async Task StartSystemsWithUnavailableDiagnosticsDatabase()
    {
        foreach (var hostSystems in this.systems)
        {
            foreach (var system in hostSystems)
            {
                await system.Start(LifecycleContext);
            }
        }
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        for (var host = 0; host < HostsPerInvocation; host++)
        {
            foreach (var system in this.systems[host])
            {
                system.Stop(LifecycleContext).GetAwaiter().GetResult();
            }

            this.providers[host].DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }
}
