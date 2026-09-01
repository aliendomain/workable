using Workable.SqlServer;

namespace Workable.SqlServer.Tests;

public sealed class WorkableSqlServerSchemaInitializerShould
{
    [Fact]
    public async Task DeployOnceAndValidateEachComponentOnceAcrossConcurrentSystems()
    {
        var deployments = 0;
        var validations = new Dictionary<WorkableSqlServerSchemaComponent, int>();
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
            initializer.InitializeExecutionDiagnostics(CancellationToken.None),
            initializer.InitializeExecutionDiagnostics(CancellationToken.None),
            initializer.InitializeQueue(CancellationToken.None),
            initializer.InitializeQueue(CancellationToken.None),
            initializer.InitializeWorkflows(CancellationToken.None));

        Assert.True(initializer.AutoDeploySchema);
        Assert.Equal(1, deployments);
        Assert.Equal(1, validations[WorkableSqlServerSchemaComponent.ExecutionDiagnostics]);
        Assert.Equal(1, validations[WorkableSqlServerSchemaComponent.QueueDurability]);
        Assert.Equal(1, validations[WorkableSqlServerSchemaComponent.WorkflowPersistence]);
    }

    [Fact]
    public async Task CacheDeploymentFailureAcrossComponents()
    {
        var deployments = 0;
        var validations = 0;
        var failure = new InvalidOperationException("Database unavailable.");
        var initializer = new WorkableSqlServerSchemaInitializer(
            autoDeploySchema: true,
            _ =>
            {
                Interlocked.Increment(ref deployments);
                return Task.FromException(failure);
            },
            (_, _) =>
            {
                Interlocked.Increment(ref validations);
                return Task.CompletedTask;
            });

        var first = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            initializer.InitializeExecutionDiagnostics(CancellationToken.None));
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            initializer.InitializeQueue(CancellationToken.None));

        Assert.Same(failure, first);
        Assert.Same(failure, second);
        Assert.Equal(1, deployments);
        Assert.Equal(0, validations);
    }

    [Fact]
    public async Task RetryDeploymentAfterCancellation()
    {
        var deployments = 0;
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
            initializer.InitializeQueue(CancellationToken.None));
        await initializer.InitializeQueue(CancellationToken.None);

        Assert.Equal(2, deployments);
    }

    [Fact]
    public async Task SkipDeploymentAndValidateEachComponentOnceWhenAutoDeployIsDisabled()
    {
        var deployments = 0;
        var validations = new Dictionary<WorkableSqlServerSchemaComponent, int>();
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

        await initializer.InitializeExecutionDiagnostics(CancellationToken.None);
        await initializer.InitializeQueue(CancellationToken.None);
        await initializer.InitializeExecutionDiagnostics(CancellationToken.None);

        Assert.False(initializer.AutoDeploySchema);
        Assert.Equal(0, deployments);
        Assert.Equal(1, validations[WorkableSqlServerSchemaComponent.ExecutionDiagnostics]);
        Assert.Equal(1, validations[WorkableSqlServerSchemaComponent.QueueDurability]);
    }

    [Fact]
    public async Task CacheComponentValidationFailureWithoutBlockingOtherComponents()
    {
        var diagnosticsValidations = 0;
        var queueValidations = 0;
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
            initializer.InitializeExecutionDiagnostics(CancellationToken.None));
        var second = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            initializer.InitializeExecutionDiagnostics(CancellationToken.None));
        await initializer.InitializeQueue(CancellationToken.None);

        Assert.Same(failure, first);
        Assert.Same(failure, second);
        Assert.Equal(1, diagnosticsValidations);
        Assert.Equal(1, queueValidations);

        Task CountQueueValidation()
        {
            Interlocked.Increment(ref queueValidations);
            return Task.CompletedTask;
        }
    }
}
