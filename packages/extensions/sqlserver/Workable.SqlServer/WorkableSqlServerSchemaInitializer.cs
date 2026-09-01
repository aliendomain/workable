using System.Runtime.ExceptionServices;

namespace Workable.SqlServer;

internal sealed class WorkableSqlServerSchemaInitializer
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly bool autoDeploySchema;
    private readonly Func<CancellationToken, Task> deploy;
    private readonly Func<WorkableSqlServerSchemaComponent, CancellationToken, Task> validate;
    private readonly HashSet<WorkableSqlServerSchemaComponent> validatedComponents = [];
    private readonly Dictionary<WorkableSqlServerSchemaComponent, InitializationFailure> deploymentFailures = [];
    private readonly Dictionary<WorkableSqlServerSchemaComponent, InitializationFailure> validationFailures = [];
    private bool deploymentCompleted;

    public WorkableSqlServerSchemaInitializer(
        string connectionString,
        string schemaName,
        bool autoDeploySchema)
        : this(
            autoDeploySchema,
            cancellationToken => WorkableSqlServerSchema.Apply(connectionString, schemaName, cancellationToken),
            (component, cancellationToken) => ValidateComponent(
                component,
                connectionString,
                schemaName,
                cancellationToken))
    {
    }

    internal WorkableSqlServerSchemaInitializer(
        bool autoDeploySchema,
        Func<CancellationToken, Task> deploy,
        Func<WorkableSqlServerSchemaComponent, CancellationToken, Task> validate)
    {
        ArgumentNullException.ThrowIfNull(deploy);
        ArgumentNullException.ThrowIfNull(validate);
        this.autoDeploySchema = autoDeploySchema;
        this.deploy = deploy;
        this.validate = validate;
    }

    public bool AutoDeploySchema => this.autoDeploySchema;

    public Task InitializeQueue(WorkSystemId workSystemId, CancellationToken cancellationToken)
        => this.Initialize(
            WorkableSqlServerSchemaComponent.QueueDurability,
            workSystemId.ToString(),
            cancellationToken);

    public Task InitializeWorkflows(string persistenceScope, CancellationToken cancellationToken)
        => this.Initialize(
            WorkableSqlServerSchemaComponent.WorkflowPersistence,
            persistenceScope,
            cancellationToken);

    public Task InitializeExecutionDiagnostics(WorkSystemId workSystemId, CancellationToken cancellationToken)
        => this.Initialize(
            WorkableSqlServerSchemaComponent.ExecutionDiagnostics,
            workSystemId.ToString(),
            cancellationToken);

    private async Task Initialize(
        WorkableSqlServerSchemaComponent component,
        string initializationScope,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(initializationScope);
        await this.gate.WaitAsync(cancellationToken);
        try
        {
            if (this.autoDeploySchema)
            {
                if (!this.deploymentCompleted)
                {
                    this.ThrowOrAllowRetry(this.deploymentFailures, component, initializationScope);
                    try
                    {
                        await this.deploy(cancellationToken);
                        this.deploymentCompleted = true;
                        this.deploymentFailures.Clear();
                    }
                    catch (Exception exception) when (ShouldCache(exception))
                    {
                        this.deploymentFailures[component] = new InitializationFailure(exception, initializationScope);
                        throw;
                    }
                }
            }

            if (this.validatedComponents.Contains(component))
            {
                return;
            }

            this.ThrowOrAllowRetry(this.validationFailures, component, initializationScope);

            try
            {
                await this.validate(component, cancellationToken);
                this.validatedComponents.Add(component);
                this.validationFailures.Remove(component);
            }
            catch (Exception exception) when (ShouldCache(exception))
            {
                this.validationFailures[component] = new InitializationFailure(exception, initializationScope);
                throw;
            }
        }
        finally
        {
            this.gate.Release();
        }
    }

    private void ThrowOrAllowRetry(
        Dictionary<WorkableSqlServerSchemaComponent, InitializationFailure> failures,
        WorkableSqlServerSchemaComponent component,
        string initializationScope)
    {
        if (!failures.TryGetValue(component, out var failure))
        {
            return;
        }

        if (failure.TryObserve(initializationScope))
        {
            failure.Exception.Throw();
        }

        failures.Remove(component);
    }

    private static bool ShouldCache(Exception exception)
        => exception is not (OperationCanceledException or OutOfMemoryException or StackOverflowException);

    private static Task ValidateComponent(
        WorkableSqlServerSchemaComponent component,
        string connectionString,
        string schemaName,
        CancellationToken cancellationToken)
        => component switch
        {
            WorkableSqlServerSchemaComponent.QueueDurability =>
                WorkableSqlServerSchema.ValidateInstalled(connectionString, schemaName, cancellationToken),
            WorkableSqlServerSchemaComponent.WorkflowPersistence =>
                WorkableSqlServerSchema.ValidateWorkflowPersistenceInstalled(connectionString, schemaName, cancellationToken),
            WorkableSqlServerSchemaComponent.ExecutionDiagnostics =>
                WorkableSqlServerSchema.ValidateExecutionDiagnosticsInstalled(connectionString, schemaName, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(component), component, "Unknown Workable SQL Server schema component."),
        };

    private sealed class InitializationFailure(Exception exception, string initializationScope)
    {
        private readonly HashSet<string> observedScopes = new(StringComparer.Ordinal)
        {
            initializationScope,
        };

        public ExceptionDispatchInfo Exception { get; } = ExceptionDispatchInfo.Capture(exception);

        public bool TryObserve(string initializationScope)
            => this.observedScopes.Add(initializationScope);
    }
}

internal enum WorkableSqlServerSchemaComponent
{
    QueueDurability,
    WorkflowPersistence,
    ExecutionDiagnostics,
}
