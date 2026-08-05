using Workable.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Diagnostics;
using System.Data;
using System.Diagnostics;
using System.Text.Json;

namespace Workable.Tests;

[Trait("Category", "Profiling")]
public sealed class WorkableSqlServerProfilingPerformanceShould
{
    [Fact]
    public void ShareOneDiagnosticObserverAcrossSystemsAndReferenceCountLeases()
    {
        var firstSystem = WorkSystemId.New();
        var secondSystem = WorkSystemId.New();
        var accessor = new EmptyProfilingContextAccessor();
        using var factory = new WorkableSqlServerProfilingInstrumentationFactory();
        var first = factory.Create(firstSystem, accessor);
        var observer = factory.Observer;
        var duplicate = factory.Create(firstSystem, accessor);
        var second = factory.Create(secondSystem, accessor);

        Assert.NotNull(observer);
        Assert.Same(observer, factory.Observer);
        first.Dispose();
        Assert.Same(observer, factory.Observer);
        duplicate.Dispose();
        Assert.Same(observer, factory.Observer);
        second.Dispose();
        Assert.Null(factory.Observer);
    }

    [Fact]
    public void EnableSqlEventsOnlyWhileARegisteredWorkableProfileCouldConsumeThem()
    {
        var systemId = WorkSystemId.New();
        var accessor = new MutableProfilingContextAccessor();
        using var listener = new DiagnosticListener("Microsoft.Data.SqlClient.ProfilingPerformanceTests");
        using var observer = new WorkableSqlServerCommandProfilingObserver(systemId, accessor);
        observer.OnNext(listener);

        Assert.False(listener.IsEnabled(SqlClientCommandBefore.Name));
        Assert.False(listener.IsEnabled(SqlClientCommandAfter.Name));
        Assert.False(listener.IsEnabled(SqlClientCommandError.Name));

        accessor.Current = new WorkProfilingContext(systemId, null!);

        Assert.True(listener.IsEnabled(SqlClientCommandBefore.Name));
        Assert.True(listener.IsEnabled(SqlClientCommandAfter.Name));
        Assert.True(listener.IsEnabled(SqlClientCommandError.Name));

        accessor.Current = null;

        Assert.False(listener.IsEnabled(SqlClientCommandBefore.Name));
        Assert.False(listener.IsEnabled(SqlClientCommandAfter.Name));
        Assert.False(listener.IsEnabled(SqlClientCommandError.Name));
    }

    [Fact]
    public void CaptureUnsupportedParameterWithoutCallingApplicationToString()
    {
        using var command = new SqlCommand { CommandText = new string(' ', 100_000) };
        command.Parameters.Add(new SqlParameter("@Custom", SqlDbType.Variant)
        {
            Value = new ThrowingStringValue(),
        });

        var json = JsonSerializer.Serialize(
            WorkableSqlServerCommandProfilingObserver.CaptureCommandForBenchmark(command));

        Assert.Contains("\"Statement\":\"\\u003Cempty\\u003E\"", json, StringComparison.Ordinal);
        Assert.Contains("\"StatementKind\":\"UNKNOWN\"", json, StringComparison.Ordinal);
        Assert.Contains("\\u003CThrowingStringValue\\u003E", json, StringComparison.Ordinal);
    }

    [Fact]
    public void MarkByteArrayValuesAsOmittedWhenTheirDeclaredSqlTypeIsVariant()
    {
        using var command = new SqlCommand { CommandText = "SELECT @Payload;" };
        command.Parameters.Add(new SqlParameter("@Payload", SqlDbType.Variant)
        {
            Value = new byte[] { 0x01, 0x02, 0x03 },
        });

        var json = JsonSerializer.Serialize(
            WorkableSqlServerCommandProfilingObserver.CaptureCommandForBenchmark(command));

        Assert.Contains("\"Type\":\"Variant\"", json, StringComparison.Ordinal);
        Assert.Contains("\"Value\":\"\\u003Cbinary omitted\\u003E\"", json, StringComparison.Ordinal);
        Assert.Contains("\"IsBinaryOmitted\":true", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AddFailureDetailsToTheTimingContextWithoutRecapturingTheCommand()
    {
        using var command = new SqlCommand { CommandText = "SELECT @Value;" };
        command.Parameters.AddWithValue("@Value", 42);
        var exception = new InvalidOperationException(new string('e', 8_000));

        var json = JsonSerializer.Serialize(
            WorkableSqlServerCommandProfilingObserver.CaptureFailedCommandForBenchmark(command, exception));

        Assert.Contains("\"Outcome\":\"Faulted\"", json, StringComparison.Ordinal);
        Assert.Contains("System.InvalidOperationException", json, StringComparison.Ordinal);
        Assert.Contains("\"MessageTruncated\":true", json, StringComparison.Ordinal);
        Assert.Equal(1, CountOccurrences(json, "\"Statement\""));
        Assert.Equal(1, CountOccurrences(json, "\"Parameters\""));
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var start = 0;
        while ((start = value.IndexOf(search, start, StringComparison.Ordinal)) >= 0)
        {
            count++;
            start += search.Length;
        }

        return count;
    }

    private sealed class ThrowingStringValue
    {
        public override string ToString() => throw new InvalidOperationException("ToString must not be called.");
    }

    private sealed class EmptyProfilingContextAccessor : IWorkProfilingContextAccessor
    {
        public bool TryGetCurrent(out WorkProfilingContext context)
        {
            context = default;
            return false;
        }
    }

    private sealed class MutableProfilingContextAccessor : IWorkProfilingContextAccessor
    {
        public WorkProfilingContext? Current { get; set; }

        public bool TryGetCurrent(out WorkProfilingContext context)
        {
            if (this.Current is { } current)
            {
                context = current;
                return true;
            }

            context = default;
            return false;
        }
    }
}
