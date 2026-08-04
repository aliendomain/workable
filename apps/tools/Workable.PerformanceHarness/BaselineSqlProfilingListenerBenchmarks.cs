using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.SqlClient.Diagnostics;
using Workable.SqlServer;

namespace Workable.PerformanceHarness;

/// <summary>
/// Benchmarks the process-wide SQL diagnostic-listener tax inside and outside an active Workable profile.
/// </summary>
[MemoryDiagnoser]
[MediumRunJob]
[InvocationCount(1)]
public class BaselineSqlProfilingListenerBenchmarks
{
    private const int OperationsPerInvocation = 50_000_000;

    private readonly WorkSystemId systemId = WorkSystemId.New();
    private readonly IWorkProfilingContextAccessor accessor = new WorkProfilingContextAccessor();
    private WorkableSqlServerCommandProfilingObserver observer = null!;
    private DiagnosticListener sqlListener = null!;
    private DiagnosticListener controlListener = null!;
    private WorkProfile profile = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        this.observer = new WorkableSqlServerCommandProfilingObserver(this.systemId, this.accessor);
        this.sqlListener = new DiagnosticListener("Microsoft.Data.SqlClient.PerformanceHarness");
        this.controlListener = new DiagnosticListener("Workable.Performance.SqlControl");
        this.profile = new WorkProfile("benchmark");
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = OperationsPerInvocation)]
    public int ControlEventsWithoutSqlListener()
        => CountEnabledCommandEvents(this.controlListener);

    [Benchmark(OperationsPerInvoke = OperationsPerInvocation)]
    public int SqlEventsOutsideWorkableContext()
        => CountEnabledCommandEvents(this.sqlListener);

    [Benchmark(OperationsPerInvoke = OperationsPerInvocation)]
    public int SqlEventsInsideWorkableContext()
    {
        using var ambient = WorkProfilerContext.Begin(this.systemId, this.profile);
        return CountEnabledCommandEvents(this.sqlListener);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        this.controlListener.Dispose();
        this.sqlListener.Dispose();
        this.observer.Dispose();
    }

    private static int CountEnabledCommandEvents(DiagnosticListener listener)
    {
        var enabled = 0;
        for (var index = 0; index < OperationsPerInvocation; index++)
        {
            if (listener.IsEnabled(SqlClientCommandBefore.Name))
            {
                enabled++;
            }
        }

        return enabled;
    }
}
