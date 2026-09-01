using System.Runtime.ExceptionServices;

namespace Workable.SqlServer;

internal sealed class WorkableSqlServerSchemaInitializer
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly bool autoDeploySchema;
    private readonly Func<CancellationToken, Task> deploy;
    private readonly Func<WorkableSqlServerSchemaComponent, CancellationToken, Task> validate;
    private readonly HashSet<WorkableSqlServerSchemaComponent> validatedComponents = [];
    private readonly Dictionary<WorkableSqlServerSchemaComponent, ExceptionDispatchInfo> validationFailures = [];
    private ExceptionDispatchInfo? deploymentFailure;
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

    public Task InitializeQueue(CancellationToken cancellationToken)
        => this.Initialize(WorkableSqlServerSchemaComponent.QueueDurability, cancellationToken);

    public Task InitializeWorkflows(CancellationToken cancellationToken)
        => this.Initialize(WorkableSqlServerSchemaComponent.WorkflowPersistence, cancellationToken);

    public Task InitializeExecutionDiagnostics(CancellationToken cancellationToken)
        => this.Initialize(WorkableSqlServerSchemaComponent.ExecutionDiagnostics, cancellationToken);

    private async Task Initialize(
        WorkableSqlServerSchemaComponent component,
        CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken);
        try
        {
            if (this.autoDeploySchema)
            {
                this.deploymentFailure?.Throw();
                if (!this.deploymentCompleted)
                {
                    try
                    {
                        await this.deploy(cancellationToken);
                        this.deploymentCompleted = true;
                    }
                    catch (Exception exception) when (ShouldCache(exception))
                    {
                        this.deploymentFailure = ExceptionDispatchInfo.Capture(exception);
                        throw;
                    }
                }
            }

            if (this.validatedComponents.Contains(component))
            {
                return;
            }

            if (this.validationFailures.TryGetValue(component, out var validationFailure))
            {
                validationFailure.Throw();
            }

            try
            {
                await this.validate(component, cancellationToken);
                this.validatedComponents.Add(component);
            }
            catch (Exception exception) when (ShouldCache(exception))
            {
                this.validationFailures[component] = ExceptionDispatchInfo.Capture(exception);
                throw;
            }
        }
        finally
        {
            this.gate.Release();
        }
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
}

internal enum WorkableSqlServerSchemaComponent
{
    QueueDurability,
    WorkflowPersistence,
    ExecutionDiagnostics,
}
