using Workable.SqlServer;

namespace Workable.SqlServer.Tests;

public sealed class WorkableSqlServerSchemaInitializerShould
{
    [Fact]
    public async Task DeployOnceAndValidateEachComponentOnceAcrossConcurrentSystems()
    {
        var deployments = 0;
        var validations = new Dictionary<WorkableSqlServerSchemaComponent, int>();
        var firstSystemId = WorkSystemId.New();
        var secondSystemId = WorkSystemId.New();
        var initializer = new WorkableSqlServerSchemaInitializer(
            autoDeploySchema: true,
            _ =>
            {
                Interlocked.Increment(ref deployments);
                return Task.CompletedTask;
            },
            (component, _) =>
            {
                validations[component] = validations.GetValueOrDefault(component) + 1;
                return Task.CompletedTask;
            });

        await Task.WhenAll(
            initializer.InitializeExecutionDiagnostics(firstSystemId, CancellationToken.None),
            initializer.InitializeExecutionDiagnostics(secondSystemId, CancellationToken.None),
            initializer.InitializeQueue(firstSystemId, CancellationToken.None),
            initializer.InitializeQueue(secondSystemId, CancellationToken.None),
            initializer.InitializeWorkflows("first", CancellationToken.None));

        Assert.True(initializer.AutoDeploySchema);
        Assert.Equal(1, deployments);
        Assert.Equal(1, validations[WorkableSqlServerSchemaComponent.ExecutionDiagnostics]);
        Assert.Equal(1, validations[WorkableSqlServerSchemaComponent.QueueDurability]);
        Assert.Equal(1, validations[WorkableSqlServerSchemaComponent.WorkflowPersistence]);
    }

    [Fact]
    public async Task ShareDeploymentFailureAcrossSystemsThenRetryForRepeatedSystemInitialization()
    {
        var deployments = 0;
        var validations = 0;
        var firstSystemId = WorkSystemId.New();
        var secondSystemId = WorkSystemId.New();
        var failure = new InvalidOperationException("Database unavailable.");
        var initializer = new WorkableSqlServerSchemaInitializer(
            autoDeploySchema: true,
            _ =>
            {
                return Interlocked.Increment(ref deployments) == 1
                    ? Task.FromException(failure)
                    : Task.CompletedTask;
            },
            (_, _) =>
            {
                Interlocked.Increment(ref validations);
                return Task.CompletedTask;
            });

        var first = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            initializer.InitializeExecutionDiagnostics(firstSystemId, CancellationToken.None));
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            initializer.InitializeExecutionDiagnostics(secondSystemId, CancellationToken.None));
        await initializer.InitializeExecutionDiagnostics(firstSystemId, CancellationToken.None);

        Assert.Same(failure, first);
        Assert.Same(failure, second);
        Assert.Equal(2, deployments);
        Assert.Equal(1, validations);
    }

    [Fact]
    public async Task RetryDeploymentForDurabilityAfterDiagnosticsDeploymentFails()
    {
        var deployments = 0;
        var validations = new Dictionary<WorkableSqlServerSchemaComponent, int>();
        var systemId = WorkSystemId.New();
        var failure = new InvalidOperationException("Database unavailable for diagnostics initialization.");
        var initializer = new WorkableSqlServerSchemaInitializer(
            autoDeploySchema: true,
            _ => Interlocked.Increment(ref deployments) == 1
                ? Task.FromException(failure)
                : Task.CompletedTask,
            (component, _) =>
            {
                validations[component] = validations.GetValueOrDefault(component) + 1;
                return Task.CompletedTask;
            });

        var diagnosticsFailure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            initializer.InitializeExecutionDiagnostics(systemId, CancellationToken.None));
        await initializer.InitializeQueue(systemId, CancellationToken.None);

        Assert.Same(failure, diagnosticsFailure);
        Assert.Equal(2, deployments);
        Assert.Equal(0, validations.GetValueOrDefault(WorkableSqlServerSchemaComponent.ExecutionDiagnostics));
        Assert.Equal(1, validations[WorkableSqlServerSchemaComponent.QueueDurability]);
    }

    [Fact]
    public async Task RetryDeploymentAfterCancellation()
    {
        var deployments = 0;
        var systemId = WorkSystemId.New();
        using var canceled = new CancellationTokenSource();
        var initializer = new WorkableSqlServerSchemaInitializer(
            autoDeploySchema: true,
            _ =>
            {
                if (Interlocked.Increment(ref deployments) != 1)
                {
                    return Task.CompletedTask;
                }

                canceled.Cancel();
                return Task.FromCanceled(canceled.Token);
            },
            (_, _) => Task.CompletedTask);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            initializer.InitializeQueue(systemId, CancellationToken.None));
        await initializer.InitializeQueue(systemId, CancellationToken.None);

        Assert.Equal(2, deployments);
    }

    [Fact]
    public async Task SkipDeploymentAndValidateEachComponentOnceWhenAutoDeployIsDisabled()
    {
        var deployments = 0;
        var validations = new Dictionary<WorkableSqlServerSchemaComponent, int>();
        var firstSystemId = WorkSystemId.New();
        var secondSystemId = WorkSystemId.New();
        var initializer = new WorkableSqlServerSchemaInitializer(
            autoDeploySchema: false,
            _ =>
            {
                deployments++;
                return Task.CompletedTask;
            },
            (component, _) =>
            {
                validations[component] = validations.GetValueOrDefault(component) + 1;
                return Task.CompletedTask;
            });

        await initializer.InitializeExecutionDiagnostics(firstSystemId, CancellationToken.None);
        await initializer.InitializeQueue(firstSystemId, CancellationToken.None);
        await initializer.InitializeExecutionDiagnostics(secondSystemId, CancellationToken.None);

        Assert.False(initializer.AutoDeploySchema);
        Assert.Equal(0, deployments);
        Assert.Equal(1, validations[WorkableSqlServerSchemaComponent.ExecutionDiagnostics]);
        Assert.Equal(1, validations[WorkableSqlServerSchemaComponent.QueueDurability]);
    }

    [Fact]
    public async Task ShareComponentValidationFailureAcrossSystemsThenRetryRepeatedSystemInitialization()
    {
        var diagnosticsValidations = 0;
        var queueValidations = 0;
        var firstSystemId = WorkSystemId.New();
        var secondSystemId = WorkSystemId.New();
        var failure = new InvalidOperationException("Diagnostics schema is incomplete.");
        var initializer = new WorkableSqlServerSchemaInitializer(
            autoDeploySchema: false,
            _ => Task.CompletedTask,
            (component, _) => component switch
            {
                WorkableSqlServerSchemaComponent.ExecutionDiagnostics =>
                    Interlocked.Increment(ref diagnosticsValidations) == 1
                        ? Task.FromException(failure)
                        : Task.CompletedTask,
                WorkableSqlServerSchemaComponent.QueueDurability =>
                    CountQueueValidation(),
                _ => Task.CompletedTask,
            });

        var first = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            initializer.InitializeExecutionDiagnostics(firstSystemId, CancellationToken.None));
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            initializer.InitializeExecutionDiagnostics(secondSystemId, CancellationToken.None));
        await initializer.InitializeQueue(firstSystemId, CancellationToken.None);
        await initializer.InitializeExecutionDiagnostics(firstSystemId, CancellationToken.None);

        Assert.Same(failure, first);
        Assert.Same(failure, second);
        Assert.Equal(2, diagnosticsValidations);
        Assert.Equal(1, queueValidations);

        Task CountQueueValidation()
        {
            Interlocked.Increment(ref queueValidations);
            return Task.CompletedTask;
        }
    }
}
