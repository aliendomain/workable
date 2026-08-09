using Microsoft.Extensions.DependencyInjection;
using Workable.SqlServer;

namespace Workable.Tests;

public sealed class WorkableSqlServerServiceCollectionExtensionsShould
{
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
