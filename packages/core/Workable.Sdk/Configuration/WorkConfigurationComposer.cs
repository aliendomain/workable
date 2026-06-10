using System.Reflection;
namespace Workable;
internal static class WorkConfigurationComposer
{
    public static WorkDefinition CreateDefinitionFromAttributes(Type executorType)
    {
        ArgumentNullException.ThrowIfNull(executorType);

        var metadataAttribute = GetRequiredMetadataAttribute(executorType);
        return WorkDefinition.Create(
            metadataAttribute.Name,
            metadataAttribute.Description,
            metadataAttribute.Category);
    }

    public static WorkDefinition Apply(
        WorkDefinition definition,
        Type? executorType,
        Action<IWorkConfigurationBuilder>? configure)
        => ApplyRegistration(definition, executorType, configure, authorize: null).Definition;

    public static WorkRegistrationConfiguration ApplyRegistration(
        WorkDefinition definition,
        Type? executorType,
        Action<IWorkConfigurationBuilder>? configure)
        => ApplyRegistration(definition, executorType, configure, authorize: null);

    public static WorkRegistrationConfiguration ApplyRegistration(
        WorkDefinition definition,
        Type? executorType,
        Action<IWorkConfigurationBuilder>? configure,
        Action<IWorkAuthorizationBuilder>? authorize)
    {
        definition = ApplyMetadata(definition, executorType);
        definition = ApplyAuthorization(definition, executorType, authorize, out var operateAuthorization);
        definition = WorkExecutorAdapterFactory.ApplyTypedSchemas(definition, executorType);
        var configuration = definition.Configuration;
        IReadOnlyList<WorkExceptionClassifier> exceptionClassifiers = [];
        IReadOnlyList<WorkAutomaticStartRegistration> automaticStarts = [];
        IReadOnlyList<WorkInitializationRegistration> initializers = [];
        var startAttribute = executorType?.GetCustomAttribute<WorkStartAttribute>(inherit: true);
        if (startAttribute is not null)
        {
            configuration = configuration with
            {
                Start = startAttribute.Configuration,
            };
        }

        var idempotencyAttribute = executorType?.GetCustomAttribute<WorkIdempotencyAttribute>(inherit: true);
        if (idempotencyAttribute is not null)
        {
            configuration = configuration with
            {
                Coordination = configuration.Coordination with
                {
                    IsEnabled = idempotencyAttribute.Configuration.IsEnabled || configuration.Coordination.IsEnabled,
                    Idempotency = idempotencyAttribute.Configuration,
                },
            };
        }

        var recurrenceAttribute = executorType?.GetCustomAttribute<WorkRecurrenceAttribute>(inherit: true);
        if (recurrenceAttribute is not null)
        {
            configuration = configuration with
            {
                Recurrence = recurrenceAttribute.Configuration,
            };
        }

        var transientRetryAttribute = executorType?.GetCustomAttribute<WorkTransientRetryAttribute>(inherit: true);
        if (transientRetryAttribute is not null)
        {
            configuration = configuration with
            {
                TransientRetry = transientRetryAttribute.Configuration,
            };
        }

        var failedWorkerAttribute = executorType?.GetCustomAttribute<WorkFailedWorkerAttribute>(inherit: true);
        if (failedWorkerAttribute is not null)
        {
            configuration = configuration with
            {
                FailedWorker = failedWorkerAttribute.Configuration,
            };
        }

        var loggingAttribute = executorType?.GetCustomAttribute<WorkLoggingAttribute>(inherit: true);
        if (loggingAttribute is not null)
        {
            configuration = configuration with
            {
                Logging = loggingAttribute.Configuration,
            };
        }

        var retentionAttribute = executorType?.GetCustomAttribute<WorkRetentionAttribute>(inherit: true);
        if (retentionAttribute is not null)
        {
            configuration = configuration with
            {
                Retention = retentionAttribute.Configuration,
            };
        }

        var concurrencyAttribute = executorType?.GetCustomAttribute<WorkConcurrencyAttribute>(inherit: true);
        if (concurrencyAttribute is not null)
        {
            configuration = configuration with
            {
                Coordination = configuration.Coordination with
                {
                    IsEnabled = concurrencyAttribute.Configuration.IsEnabled || configuration.Coordination.IsEnabled,
                    Concurrency = concurrencyAttribute.Configuration,
                },
            };
        }

        var durabilityAttribute = executorType?.GetCustomAttribute<WorkQueueDurabilityAttribute>(inherit: true);
        if (durabilityAttribute is not null)
        {
            configuration = configuration with
            {
                Coordination = configuration.Coordination with
                {
                    IsEnabled = durabilityAttribute.Configuration.IsEnabled || durabilityAttribute.Configuration.CompleteDurably || configuration.Coordination.IsEnabled,
                    Storage = WorkCoordinationStorage.Persistent,
                    Durability = durabilityAttribute.Configuration,
                },
            };
        }

        var invocationAttribute = executorType?.GetCustomAttribute<WorkInvocationAttribute>(inherit: true);
        if (invocationAttribute is not null)
        {
            configuration = configuration with
            {
                Invocation = configuration.Invocation.AllowAdditional(invocationAttribute.AllowedChannels.ToArray()),
            };
        }

        if (configure is not null)
        {
            var builder = new WorkConfigurationBuilder(configuration);
            configure(builder);
            configuration = builder.Build();
            exceptionClassifiers = builder.BuildExceptionClassifiers();
            automaticStarts = builder.BuildAutomaticStarts();
            initializers = builder.BuildInitializers();
        }

        return new WorkRegistrationConfiguration(
            definition with
            {
                Configuration = WorkConfigurationValidator.ThrowIfInvalid(configuration),
            },
            operateAuthorization,
            exceptionClassifiers,
            automaticStarts,
            initializers);
    }

    private static WorkDefinition ApplyMetadata(WorkDefinition definition, Type? executorType)
    {
        var metadataAttribute = executorType?.GetCustomAttribute<WorkMetadataAttribute>(inherit: true);
        if (metadataAttribute is null)
        {
            return definition;
        }

        return definition with
        {
            Name = metadataAttribute.Name,
            Description = metadataAttribute.Description,
            Category = metadataAttribute.Category,
        };
    }

    private static WorkDefinition ApplyAuthorization(
        WorkDefinition definition,
        Type? executorType,
        Action<IWorkAuthorizationBuilder>? authorize,
        out WorkOperateAuthorizationConfiguration operateAuthorization)
    {
        var attribute = executorType?.GetCustomAttribute<WorkAuthorizationAttribute>(inherit: true);
        var authorization = definition.Authorization;
        operateAuthorization = WorkOperateAuthorizationConfiguration.FromDefinition(authorization);
        if (attribute is not null)
        {
            authorization = WorkDefinitionAuthorization.Create(
                attribute.ReadGroups,
                attribute.OperateGroups,
                WorkAuthorizationRegistrationSource.Attribute);
            operateAuthorization = WorkOperateAuthorizationConfiguration.FromDefinition(authorization);
        }

        if (authorize is not null)
        {
            var builder = new WorkAuthorizationBuilder();
            authorize(builder);
            var registration = builder.BuildRegistration();
            authorization = registration.DefinitionAuthorization;
            operateAuthorization = registration.OperateAuthorization;
        }

        return definition with
        {
            Authorization = authorization,
        };
    }

    private static WorkMetadataAttribute GetRequiredMetadataAttribute(Type executorType)
        => executorType.GetCustomAttribute<WorkMetadataAttribute>(inherit: true)
            ?? throw new InvalidOperationException(
                $"Executor type '{executorType.FullName}' must declare {nameof(WorkMetadataAttribute)} when registering work without an explicit {nameof(WorkDefinition)}.");
}
