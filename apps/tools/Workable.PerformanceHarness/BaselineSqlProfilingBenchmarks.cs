using System.Data;
using BenchmarkDotNet.Attributes;
using Microsoft.Data.SqlClient;
using Workable.SqlServer;

namespace Workable.PerformanceHarness;

[MemoryDiagnoser]
[MediumRunJob]
[InvocationCount(1)]
/// <summary>
/// Benchmarks SQL profiling context capture for successful, failed, and unsupported parameter values.
/// </summary>
public class BaselineSqlProfilingBenchmarks
{
    private SqlCommand representativeCommand = null!;
    private SqlCommand unsupportedValueCommand = null!;
    private Exception failure = null!;

    [GlobalSetup]
    public void GlobalSetup()
    {
        this.representativeCommand = new SqlCommand
        {
            CommandText = "SELECT @Value; --" + new string('s', 40_000),
        };
        for (var index = 0; index < 8; index++)
        {
            this.representativeCommand.Parameters.Add(
                new SqlParameter($"@Value{index}", SqlDbType.NVarChar, 4_096)
                {
                    Value = new string((char)('a' + index), 4_096),
                });
        }

        this.unsupportedValueCommand = new SqlCommand
        {
            CommandText = new string(' ', 100_000),
        };
        this.unsupportedValueCommand.Parameters.Add(
            new SqlParameter("@Custom", SqlDbType.Variant)
            {
                Value = new ExpensiveStringValue(),
            });
        this.failure = new InvalidOperationException(new string('e', 8_000));
    }

    [Benchmark(Baseline = true)]
    public object CaptureSuccessfulCommand()
        => WorkableSqlServerCommandProfilingObserver.CaptureCommandForBenchmark(this.representativeCommand);

    [Benchmark]
    public object CaptureFailedCommand()
        => WorkableSqlServerCommandProfilingObserver.CaptureFailedCommandForBenchmark(
            this.representativeCommand,
            this.failure);

    [Benchmark]
    public object CaptureUnsupportedParameterAndWhitespaceStatement()
        => WorkableSqlServerCommandProfilingObserver.CaptureCommandForBenchmark(this.unsupportedValueCommand);

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        this.representativeCommand.Dispose();
        this.unsupportedValueCommand.Dispose();
    }

    private sealed class ExpensiveStringValue
    {
        public override string ToString() => new('x', 1_000_000);
    }
}
