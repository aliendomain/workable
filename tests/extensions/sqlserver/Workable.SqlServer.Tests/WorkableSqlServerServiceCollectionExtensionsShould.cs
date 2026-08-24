using Microsoft.Extensions.DependencyInjection;
using Workable.SqlServer;

namespace Workable.Tests;

public sealed class WorkableSqlServerServiceCollectionExtensionsShould
{
    [Fact]
    public void RegisterSqlProfilingOnlyOnce()
    {
        var services = new ServiceCollection();

        Assert.Same(services, services.AddWorkableSqlServerProfiling());
        Assert.Same(services, services.AddWorkableSqlServerProfiling());

        Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(WorkableSqlServerProfilingRegistrationMarker));
        Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(IWorkSystemCapabilityContributor));
        Assert.Single(services, descriptor =>
            descriptor.ServiceType == typeof(IWorkProfilingInstrumentationFactory));
    }

    [Theory]
    [InlineData("enqueue batch size")]
    [InlineData("enqueue batch window")]
    [InlineData("claim batch size")]
    [InlineData("claim sample capacity")]
    public void RejectInvalidDurableQueueLimits(string setting)
    {
        var options = new WorkableSqlServerQueueDurabilityOptions
        {
            ConnectionString = "Server=queue;Database=Workable",
            EnqueueBatchSize = setting == "enqueue batch size" ? 0 : 1,
            EnqueueBatchWindow = setting == "enqueue batch window" ? TimeSpan.FromTicks(-1) : TimeSpan.Zero,
            ClaimBatchSize = setting == "claim batch size" ? 0 : 1,
            RecentClaimSampleCapacity = setting == "claim sample capacity" ? -1 : 0,
        };

        var exception = Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ServiceCollection().AddWorkableSqlServerDurableQueue(options));

        Assert.Contains(setting, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RejectInvalidExecutionDiagnosticReadLimitsBeforeOpeningSqlServer()
    {
        var repository = new WorkableSqlServerExecutionDiagnosticsRepository(
            new WorkableSqlServerPersistenceOptions
            {
                ConnectionString = "Server=unused;Database=Workable",
            });
        var systemId = WorkSystemId.New();
        var workerId = WorkerId.New();

        await repository.AppendLogs([]);

        foreach (var take in new[] { 0, WorkExecutionDiagnosticCriteria.MaximumTake + 1 })
        {
            var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repository.Query(new WorkExecutionDiagnosticCriteria(systemId, Take: take)));
            Assert.Contains("between 1", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        foreach (var maximumLogCount in new[] { 0, 10_001 })
        {
            var exception = await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                repository.Get(new WorkExecutionDiagnosticGetRequest(
                    systemId,
                    workerId,
                    IterationSequence: 1,
                    MaximumLogCount: maximumLogCount)));
            Assert.Contains("between 1", exception.Message, StringComparison.OrdinalIgnoreCase);
        }

        var notInitialized = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            repository.Query(new WorkExecutionDiagnosticCriteria(systemId)));
        Assert.Contains("was not initialized", notInitialized.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PreserveExplicitPersistenceOptionsRegardlessOfRegistrationOrder(bool durableQueueFirst)
    {
        var services = new ServiceCollection();
        if (durableQueueFirst)
        {
            services.AddWorkableSqlServerDurableQueue("Server=shared;Database=Workable", "telemetry");
        }

        services.AddWorkableSqlServerPersistence(new WorkableSqlServerPersistenceOptions
        {
            ConnectionString = "Server=shared;Database=Workable",
            SchemaName = "telemetry",
            PersistenceScope = "application-a",
            AutoDeploySchema = false,
        });

        if (!durableQueueFirst)
        {
            services.AddWorkableSqlServerDurableQueue("Server=shared;Database=Workable", "telemetry");
        }

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<WorkableSqlServerPersistenceOptions>();

        Assert.Equal("application-a", options.PersistenceScope);
        Assert.False(options.AutoDeploySchema);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RejectConflictingDurableQueueAndDiagnosticsStores(bool durableQueueFirst)
    {
        var services = new ServiceCollection();

        void RegisterDurableQueue() =>
            services.AddWorkableSqlServerDurableQueue("Server=queue;Database=Workable", "workable");
        void RegisterDiagnostics() =>
            services.AddWorkableSqlServerPersistence("Server=diagnostics;Database=Workable", "workable");

        if (durableQueueFirst)
        {
            RegisterDurableQueue();
            Assert.Throws<InvalidOperationException>(RegisterDiagnostics);
        }
        else
        {
            RegisterDiagnostics();
            Assert.Throws<InvalidOperationException>(RegisterDurableQueue);
        }
    }

    [Fact]
    public void RejectMultipleExplicitPersistenceScopes()
    {
        var services = new ServiceCollection()
            .AddWorkableSqlServerPersistence("Server=shared;Database=Workable", persistenceScope: "scope-a");

        Assert.Throws<InvalidOperationException>(() => services.AddWorkableSqlServerPersistence(
            "Server=shared;Database=Workable",
            persistenceScope: "scope-b"));
    }
}
